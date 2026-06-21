using System;
using System.Collections;
using System.Threading.Tasks;
using M2C.Checkout.Internal;
using UnityEngine;

namespace M2C.Checkout
{
    /// <summary>
    /// Headless checkout client. Launches the winning vendor's hosted checkout,
    /// handles the platform return, and polls conversion status, surfacing the
    /// state machine via <see cref="OnStateChanged"/> and a single terminal
    /// <see cref="CheckoutResult"/>. Holds no secrets beyond an optional publishable
    /// key; the merchant webhook remains the source of truth. Drive a UI off the
    /// state, but grant goods server-side off the webhook - the status read is
    /// advisory UX.
    /// </summary>
    public sealed class M2CCheckoutClient
    {
        private readonly M2CConfig _config;
        private readonly M2CApi _api;
        private static bool _inFlight;

        /// <summary>Fired on the Unity main thread for every state transition.</summary>
        public event Action<CheckoutState> OnStateChanged;

        /// <summary>The current state.</summary>
        public CheckoutState State { get; private set; } = CheckoutState.Idle;

        /// <summary>Create a client from the project settings asset, or default config when the asset is absent.</summary>
        public M2CCheckoutClient() : this(M2CConfig.FromProjectSettings())
        {
        }

        public M2CCheckoutClient(M2CConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _api = new M2CApi(_config.PublishableKey);
        }

        /// <summary>Create a client from the project settings asset, or default config when the asset is absent.</summary>
        public static M2CCheckoutClient FromProjectSettings()
        {
            return new M2CCheckoutClient(M2CConfig.FromProjectSettings());
        }

        // --- Async surface (primary) ---

        /// <summary>Backend-initiated: your server ran the auction and handed you a session.</summary>
        public async Task<CheckoutResult> StartFromSessionAsync(CheckoutSession session)
        {
            BeginFlow();
            try
            {
                ValidateSession(session);
                ValidateStatusSource();
                return await RunAsync(session.CheckoutUrl, session.RequestId, "session", _config.ReturnUrl, _config.CancelUrl);
            }
            catch
            {
                if (State != CheckoutState.Error) SetState(CheckoutState.Error);
                throw;
            }
            finally
            {
                _inFlight = false;
            }
        }

        /// <summary>Client-initiated (publishable key): the SDK runs the auction itself.</summary>
        public async Task<CheckoutResult> StartAsync(AuctionRequest request)
        {
            BeginFlow();
            try
            {
                if (string.IsNullOrEmpty(_config.PublishableKey))
                    throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, MissingPublishableKeyMessage());
                ValidateStatusSource();

                ApplyClientInitiatedDefaults(ref request);
                CreateBrowserForReturnUrl(request.SuccessUrl);

                SetState(CheckoutState.Creating);
                AuctionResult auction = await _api.CreateAuctionAsync(request);
                return await RunAsync(auction.CheckoutUrl, auction.RequestId, "client", request.SuccessUrl, request.CancelUrl);
            }
            catch
            {
                if (State != CheckoutState.Error) SetState(CheckoutState.Error);
                throw;
            }
            finally
            {
                _inFlight = false;
            }
        }

        /// <summary>
        /// Resume a checkout whose process was killed mid-flight (cold start). Call
        /// once on startup; returns null if nothing was pending, otherwise resumes the
        /// status poll for the persisted request id.
        /// </summary>
        public async Task<CheckoutResult> TryResumeAsync()
        {
            BeginFlow();
            ResumeRecord pending = ResumeStore.PendingRecord();
            if (pending == null)
            {
                _inFlight = false;
                return null;
            }

            try
            {
                StatusSource resumeSource = ResolveResumeStatusSource(pending);
                ValidateStatusSource(resumeSource);
                return await PollAsync(pending.RequestId, resumeSource);
            }
            catch
            {
                if (State != CheckoutState.Error) SetState(CheckoutState.Error);
                throw;
            }
            finally
            {
                _inFlight = false;
            }
        }

        /// <summary>
        /// Read the current conversion status for a request id, out of band - e.g. to
        /// re-check a checkout that resolved <see cref="CheckoutPendingTimeout"/> but may
        /// have completed since (the merchant webhook is the authority; this is advisory
        /// UX). Mirrors the web SDK's checkStatus(). Does not run a flow or change
        /// <see cref="OnStateChanged"/>; reads through the configured
        /// <see cref="M2CConfig.StatusSource"/> (and publishable key for the M2C source).
        /// </summary>
        public Task<ClientStatus> CheckStatusAsync(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "request id is required");
            ValidateStatusSource();
            return ResolveStatusWithinBudgetAsync(requestId, null, M2CApi.DefaultHttpTimeoutSeconds);
        }

        private async Task<CheckoutResult> RunAsync(string checkoutUrl, string requestId, string mode, string returnUrl, string cancelUrl)
        {
            if (string.IsNullOrEmpty(checkoutUrl) || string.IsNullOrEmpty(requestId))
            {
                SetState(CheckoutState.Error);
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "missing checkout url or request id");
            }

            ICheckoutBrowser browser = CreateBrowserForReturnUrl(returnUrl);

            SetState(CheckoutState.Ready);
            ResumeStore.Save(requestId, mode, _config.StatusSource);
            SetState(CheckoutState.Launching);
            SetState(CheckoutState.AwaitingReturn);

            BrowserOutcome outcome;
            try
            {
                outcome = await browser.LaunchAsync(checkoutUrl, returnUrl, cancelUrl);
            }
            catch (Exception e)
            {
                ResumeStore.Clear();
                SetState(CheckoutState.Error);
                throw e is M2CCheckoutException ? e : new M2CCheckoutException(M2CErrorCode.Unknown, e.Message);
            }

            // Launched: a surface that polls for its outcome over the full window (the
            // Editor real-checkout mock; backend-session flows).
            if (outcome.Result == BrowserResult.Launched)
                return await PollAsync(requestId);

            // Resumed: the app came back from a Custom Tab with no deep-link outcome. One
            // status read catches a completion that didn't redirect; anything else resolves
            // immediately. Fast, and never a cancel - a bare resume can't be told apart from
            // a 3DS/OTP return, so canceling on it would kill live payments.
            if (outcome.Result == BrowserResult.Resumed)
                return await ResolveResumedAsync(requestId);

            SetState(CheckoutState.Returned);

            if (outcome.Result == BrowserResult.Dismissed)
            {
                ResumeStore.Clear();
                return Terminal(new CheckoutCanceled(requestId), CheckoutState.Canceled);
            }

            if (ReturnClassifier.HasMismatchedRequestId(outcome.ReturnUrl, requestId))
            {
                ResumeStore.Clear();
                SetState(CheckoutState.Error);
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "return url request_id did not match the active checkout");
            }

            var verdict = ReturnClassifier.Classify(outcome.ReturnUrl, returnUrl, cancelUrl, requestId, out _);
            if (verdict == ReturnVerdict.Cancel)
            {
                ResumeStore.Clear();
                return Terminal(new CheckoutCanceled(requestId), CheckoutState.Canceled);
            }
            if (verdict == ReturnVerdict.Unknown)
            {
                ResumeStore.Clear();
                SetState(CheckoutState.Error);
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "return url did not match the active return or cancel URL");
            }

            return await PollAsync(requestId);
        }

        private ICheckoutBrowser CreateBrowserForReturnUrl(string returnUrl)
        {
            ICheckoutBrowser browser = CheckoutBrowserFactory.Create(_config, returnUrl);
            if (browser.RequiresReturnUrl && string.IsNullOrEmpty(returnUrl))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, MissingReturnUrlMessage());
            return browser;
        }

        private void ApplyClientInitiatedDefaults(ref AuctionRequest request)
        {
            if (string.IsNullOrEmpty(request.SuccessUrl)) request.SuccessUrl = _config.ReturnUrl;
            if (string.IsNullOrEmpty(request.CancelUrl)) request.CancelUrl = _config.CancelUrl;
        }

        private async Task<CheckoutResult> PollAsync(string requestId, StatusSource statusSource = null)
        {
            SetState(CheckoutState.Polling);
            var sched = _config.Poll ?? PollSchedule.Default;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int attempt = 0;

            while (stopwatch.Elapsed.TotalSeconds < sched.TotalWindowSeconds)
            {
                double delay = sched.DelayForAttempt(attempt++);
                if (delay > 0)
                {
                    double remainingBeforeDelay = sched.TotalWindowSeconds - stopwatch.Elapsed.TotalSeconds;
                    if (remainingBeforeDelay <= 0) break;
                    await M2CScheduler.Instance.Delay(Math.Min(delay, remainingBeforeDelay));
                    if (delay >= remainingBeforeDelay) break;
                }

                double remaining = sched.TotalWindowSeconds - stopwatch.Elapsed.TotalSeconds;
                if (remaining <= 0) break;

                ClientStatus status;
                try
                {
                    status = await ResolveStatusWithinBudgetAsync(requestId, statusSource, remaining);
                }
                catch (M2CCheckoutException e)
                {
                    if (!IsRetryableStatusRead(e))
                    {
                        ResumeStore.Clear();
                        SetState(CheckoutState.Error);
                        throw;
                    }

                    Debug.LogWarning("[M2C] status read failed, will retry: " + e.Message);
                    continue;
                }
                catch (Exception e)
                {
                    // A transient status-read failure must not fail the checkout; keep
                    // polling until the window closes (then pending-timeout).
                    Debug.LogWarning("[M2C] status read failed, will retry: " + e.Message);
                    continue;
                }

                switch (status)
                {
                    case ClientStatus.Completed:
                        ResumeStore.Clear();
                        return Terminal(new CheckoutCompleted(requestId), CheckoutState.Completed);
                    case ClientStatus.Failed:
                        ResumeStore.Clear();
                        return Terminal(new CheckoutFailed(requestId), CheckoutState.Failed);
                    case ClientStatus.Canceled:
                        ResumeStore.Clear();
                        return Terminal(new CheckoutCanceled(requestId), CheckoutState.Canceled);
                    default:
                        break; // processing - keep polling
                }
            }

            // Window elapsed while still processing. The webhook is the authority; the
            // merchant should show "we'll confirm shortly".
            ResumeStore.Clear();
            return Terminal(new CheckoutPendingTimeout(requestId), CheckoutState.PendingTimeout);
        }

        // Resolve a foreground resume that produced no deep-link outcome with a single
        // status read: a completion that didn't redirect resolves Completed; pending or a
        // retryable status-read failure resolves pending-timeout (the webhook is authoritative).
        // Never a cancel - a resume is indistinguishable from a 3DS/OTP return. One
        // round-trip, so a back-out settles in ~a second instead of riding the poll ramp.
        private async Task<CheckoutResult> ResolveResumedAsync(string requestId)
        {
            SetState(CheckoutState.Polling);
            ClientStatus status;
            try
            {
                status = await ResolveStatusWithinBudgetAsync(requestId, null, M2CApi.DefaultHttpTimeoutSeconds);
            }
            catch (M2CCheckoutException e) when (!IsRetryableStatusRead(e))
            {
                ResumeStore.Clear();
                SetState(CheckoutState.Error);
                throw;
            }
            catch (M2CCheckoutException e)
            {
                Debug.LogWarning("[M2C] resume status read failed, treating as pending: " + e.Message);
                status = ClientStatus.Processing;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[M2C] resume status read failed, treating as pending: " + e.Message);
                status = ClientStatus.Processing;
            }
            ResumeStore.Clear();
            switch (status)
            {
                case ClientStatus.Completed:
                    return Terminal(new CheckoutCompleted(requestId), CheckoutState.Completed);
                case ClientStatus.Failed:
                    return Terminal(new CheckoutFailed(requestId), CheckoutState.Failed);
                case ClientStatus.Canceled:
                    return Terminal(new CheckoutCanceled(requestId), CheckoutState.Canceled);
                default:
                    return Terminal(new CheckoutPendingTimeout(requestId), CheckoutState.PendingTimeout);
            }
        }

        private Task<ClientStatus> ResolveStatusAsync(string requestId, StatusSource statusSource = null, double timeoutBudgetSeconds = 0)
        {
            var src = statusSource ?? _config.StatusSource ?? StatusSource.M2C;
            switch (src.Kind)
            {
                case StatusSourceKind.Url:
                    return M2CApi.ReadStatusUrlAsync(src.UrlTemplate, requestId, timeoutBudgetSeconds);
                case StatusSourceKind.Callback:
                    return src.CheckStatus(requestId);
                case StatusSourceKind.Subscribe:
                    throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "subscribe status source is not implemented in v1");
                default:
                    return _api.ReadStatusM2CAsync(requestId, timeoutBudgetSeconds);
            }
        }

        private async Task<ClientStatus> ResolveStatusWithinBudgetAsync(string requestId, StatusSource statusSource, double timeoutBudgetSeconds)
        {
            if (double.IsNaN(timeoutBudgetSeconds) || double.IsInfinity(timeoutBudgetSeconds) || timeoutBudgetSeconds <= 0)
                timeoutBudgetSeconds = M2CApi.DefaultHttpTimeoutSeconds;

            Task<ClientStatus> statusTask = ResolveStatusAsync(requestId, statusSource, timeoutBudgetSeconds);
            Task timeoutTask = M2CScheduler.Instance.Delay(timeoutBudgetSeconds);
            if (await Task.WhenAny(statusTask, timeoutTask) == statusTask)
                return await statusTask;

            ObserveFault(statusTask);
            throw new M2CCheckoutException(M2CErrorCode.Network, "status read timed out");
        }

        private static void ObserveFault(Task<ClientStatus> task)
        {
            task.ContinueWith(t =>
            {
                t.Exception?.Handle(_ => true);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void BeginFlow()
        {
            if (_inFlight)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "a checkout is already in progress");
            EnsureRuntimePlatformSupported();
            _inFlight = true;
        }

        private static void EnsureRuntimePlatformSupported()
        {
#if UNITY_STANDALONE && !UNITY_EDITOR
            throw new M2CCheckoutException(
                M2CErrorCode.InvalidRequest,
                "M2C Checkout does not support standalone desktop player builds yet. Build for iOS, Android, or WebGL, or test in the Unity Editor.");
#endif
        }

        private void ValidateSession(CheckoutSession session)
        {
            if (string.IsNullOrEmpty(session.CheckoutUrl) || string.IsNullOrEmpty(session.RequestId))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "missing checkout url or request id");
            if (session.Ttl <= 0)
                throw new M2CCheckoutException(M2CErrorCode.CheckoutExpired, "the checkout session has expired; create a new one");
        }

        private void ValidateStatusSource()
        {
            ValidateStatusSource(_config.StatusSource ?? StatusSource.M2C);
        }

        private void ValidateStatusSource(StatusSource src)
        {
            if (src.Kind == StatusSourceKind.Subscribe)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "subscribe status source is not implemented in v1");
            if (src.Kind == StatusSourceKind.M2C && string.IsNullOrEmpty(_config.PublishableKey))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, MissingStatusPublishableKeyMessage());
        }

        private static string MissingPublishableKeyMessage()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "client-initiated WebGL checkout requires a web publishable key; set WebGL Publishable Key in the M2C settings asset or M2CConfig.PublishableKey";
#else
            return "client-initiated checkout requires a publishable key";
#endif
        }

        private static string MissingStatusPublishableKeyMessage()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "the m2c status source requires a web publishable key on WebGL; set WebGL Publishable Key, or use Url or Callback for backend-initiated checkout";
#else
            return "the m2c status source requires a publishable key; use Url or Callback for backend-initiated checkout";
#endif
        }

        private static string MissingReturnUrlMessage()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "return url is required for WebGL; set WebGL Success URL in the M2C settings asset or AuctionRequest.SuccessUrl";
#else
            return "return url is required for this platform; set M2CConfig.ReturnUrl or AuctionRequest.SuccessUrl";
#endif
        }

        private StatusSource ResolveResumeStatusSource(ResumeRecord record)
        {
            switch (record.StatusKind)
            {
                case StatusSourceKind.Url:
                    if (string.IsNullOrEmpty(record.StatusUrlTemplate))
                        throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "saved checkout is missing its status url template");
                    return StatusSource.Url(record.StatusUrlTemplate);
                case StatusSourceKind.Callback:
                    if (_config.StatusSource == null || _config.StatusSource.Kind != StatusSourceKind.Callback)
                        throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "saved checkout used a callback status source; recreate the callback in config before resuming");
                    return _config.StatusSource;
                case StatusSourceKind.Subscribe:
                    throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "subscribe status source is not implemented in v1");
                default:
                    return StatusSource.M2C;
            }
        }

        private static bool IsRetryableStatusRead(M2CCheckoutException e)
        {
            return e.Code == M2CErrorCode.Network
                   || e.Code == M2CErrorCode.RateLimited
                   || e.Code == M2CErrorCode.ServiceUnavailable;
        }

        private CheckoutResult Terminal(CheckoutResult result, CheckoutState state)
        {
            SetState(state);
            return result;
        }

        private void SetState(CheckoutState state)
        {
            State = state;
            var handlers = OnStateChanged;
            if (handlers == null) return;
            foreach (Action<CheckoutState> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(state);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        // --- Coroutine surface (for teams avoiding async, and a familiar Unity idiom) ---

        /// <summary>Coroutine form of <see cref="StartFromSessionAsync"/>.</summary>
        public IEnumerator StartFromSession(CheckoutSession session, Action<CheckoutResult> onResult = null, Action<CheckoutState> onState = null)
        {
            return Await(() => StartFromSessionAsync(session), onResult, onState);
        }

        /// <summary>Coroutine form of <see cref="StartAsync"/>.</summary>
        public IEnumerator Start(AuctionRequest request, Action<CheckoutResult> onResult = null, Action<CheckoutState> onState = null)
        {
            return Await(() => StartAsync(request), onResult, onState);
        }

        private IEnumerator Await(Func<Task<CheckoutResult>> start, Action<CheckoutResult> onResult, Action<CheckoutState> onState)
        {
            Action<CheckoutState> sub = onState;
            if (sub != null) OnStateChanged += sub;
            Task<CheckoutResult> task = null;
            try
            {
                task = start();
                while (!task.IsCompleted) yield return null;
            }
            finally
            {
                if (sub != null) OnStateChanged -= sub;
            }

            if (task.IsFaulted)
            {
                // The coroutine surface can't rethrow a typed exception cleanly; log it
                // and hand back null so the caller can branch on a missing result.
                Debug.LogException(task.Exception);
                onResult?.Invoke(null);
            }
            else
            {
                onResult?.Invoke(task.Result);
            }
        }
    }
}
