using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
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
        private readonly Func<AuctionRequest, int, CancellationToken, Task<AuctionResult>> _createAuction;
        private readonly Func<double, CancellationToken, Task> _delay;
        private readonly Func<string, ICheckoutBrowser> _createBrowser;
        private readonly Func<ShopSessionRequest, Task<ShopSessionResult>> _createShopSession;
        private readonly Func<string, Task<ShopSessionStatus>> _readShopSessionStatus;
        private readonly Func<IShopSessionBrowser> _createShopSessionBrowser;
        private readonly Func<double> _monotonicSeconds;
        private readonly Func<string, StatusSource, double, Task<ClientStatus>> _readStatus;
        private static bool _inFlight;
        private static readonly FallbackAttempt DisabledFallbackAttempt =
            new FallbackAttempt(null, null, default(AuctionRequest), null);
        private bool _loggedFallbackFailure;
        private const double ResumedStatusWindowSeconds = 3.0;
        internal const int MinFallbackAuctionTimeoutMs = 8000;
        internal const int MaxFallbackAuctionTimeoutMs = 30000;

        private sealed class AuctionDeadlineReachedException : Exception
        {
        }

        internal sealed class FallbackAttempt
        {
            private readonly System.Diagnostics.Stopwatch _stopwatch;

            public readonly CheckoutFallbackHandler Handler;
            public readonly string AttemptId;
            public readonly string FallbackProductId;
            public readonly AuctionRequest Request;
            public string RequestId;
            public bool LaunchedOrUnknown { get; private set; }
            public bool Enabled => Handler != null;
            public bool CanFallback => Enabled && !LaunchedOrUnknown;
            public long ElapsedMilliseconds => _stopwatch != null ? _stopwatch.ElapsedMilliseconds : 0;

            public FallbackAttempt(
                CheckoutFallbackHandler handler,
                CheckoutStartOptions options,
                AuctionRequest request,
                string requestId)
            {
                Handler = handler;
                if (handler != null)
                {
                    AttemptId = Guid.NewGuid().ToString("N");
                    _stopwatch = System.Diagnostics.Stopwatch.StartNew();
                }
                FallbackProductId = options != null ? options.FallbackProductId : null;
                Request = request;
                RequestId = requestId;
            }

            public void MarkLaunchedOrUnknown()
            {
                if (Enabled) LaunchedOrUnknown = true;
            }
        }

        /// <summary>Fired on the Unity main thread for every state transition.</summary>
        public event Action<CheckoutState> OnStateChanged;

        /// <summary>The current state.</summary>
        public CheckoutState State { get; private set; } = CheckoutState.Idle;

        /// <summary>Create a client from the project settings asset, or default config when the asset is absent.</summary>
        public M2CCheckoutClient() : this(M2CConfig.FromProjectSettings())
        {
        }

        public M2CCheckoutClient(M2CConfig config)
            : this(config, null)
        {
        }

        internal M2CCheckoutClient(
            M2CConfig config,
            Func<AuctionRequest, int, CancellationToken, Task<AuctionResult>> createAuction,
            Func<double, CancellationToken, Task> delay = null,
            Func<string, ICheckoutBrowser> createBrowser = null,
            Func<ShopSessionRequest, Task<ShopSessionResult>> createShopSession = null,
            Func<string, Task<ShopSessionStatus>> readShopSessionStatus = null,
            Func<IShopSessionBrowser> createShopSessionBrowser = null,
            Func<double> monotonicSeconds = null,
            Func<string, StatusSource, double, Task<ClientStatus>> readStatus = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (!string.IsNullOrEmpty(_config.PublishableKey) &&
                !_config.PublishableKey.StartsWith("pub_", StringComparison.Ordinal))
            {
                throw new M2CCheckoutException(
                    M2CErrorCode.InvalidRequest,
                    "PublishableKey must start with pub_ or pub_test_; never embed a secret key in a client application");
            }
            _api = new M2CApi(_config.PublishableKey);
            _createAuction = createAuction ?? _api.CreateAuctionAsync;
            _delay = delay ?? ((seconds, token) => M2CScheduler.Instance.Delay(seconds, token));
            _createBrowser = createBrowser ?? (returnUrl => CheckoutBrowserFactory.Create(_config, returnUrl));
            _createShopSession = createShopSession ?? _api.CreateShopSessionAsync;
            _readShopSessionStatus = readShopSessionStatus ?? _api.ReadShopSessionStatusAsync;
            _createShopSessionBrowser = createShopSessionBrowser ?? (() => ShopSessionBrowserFactory.Create(_config));
            _monotonicSeconds = monotonicSeconds ?? (() =>
                (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
            _readStatus = readStatus;
        }

        /// <summary>Create a client from the project settings asset, or default config when the asset is absent.</summary>
        public static M2CCheckoutClient FromProjectSettings()
        {
            return new M2CCheckoutClient(M2CConfig.FromProjectSettings());
        }

        // --- Async surface (primary) ---

        /// <summary>Backend-initiated: your server ran the auction and handed you a session.</summary>
        public Task<CheckoutResult> StartFromSessionAsync(CheckoutSession session)
        {
            return StartFromSessionAsync(session, null);
        }

        /// <summary>Backend-initiated checkout with per-call fallback policy and product context.</summary>
        public async Task<CheckoutResult> StartFromSessionAsync(CheckoutSession session, CheckoutStartOptions options)
        {
            BeginFlow();
            ICheckoutBrowser browser = null;
            try
            {
                ValidateFallbackOptions(options, false);
                ValidateSession(session);
                ValidateStatusSource();

                var fallback = CreateFallbackAttempt(options, default(AuctionRequest), session.RequestId);
                browser = CreateBrowserForReturnUrl(_config.ReturnUrl);
                if (fallback.Enabled)
                {
                    try
                    {
                        PrepareLaunchIfSupported(browser);
                    }
                    catch (CheckoutPreparationException e)
                    {
                        return await CompleteFallbackAsync(
                            fallback,
                            FallbackReason.LaunchFailed,
                            e.CheckoutError,
                            browser);
                    }
                }
                return await RunAsync(
                    session.CheckoutUrl,
                    session.RequestId,
                    "session",
                    _config.ReturnUrl,
                    _config.CancelUrl,
                    browser,
                    fallback);
            }
            catch
            {
                CancelPreparedLaunch(browser);
                if (State != CheckoutState.Error) SetState(CheckoutState.Error);
                throw;
            }
            finally
            {
                _inFlight = false;
            }
        }

        /// <summary>Client-initiated (publishable key): the SDK runs the auction itself.</summary>
        public Task<CheckoutResult> StartAsync(AuctionRequest request)
        {
            return StartAsync(request, null);
        }

        /// <summary>Client-initiated checkout with per-call fallback policy and product context.</summary>
        public async Task<CheckoutResult> StartAsync(AuctionRequest request, CheckoutStartOptions options)
        {
            BeginFlow();
            ICheckoutBrowser browser = null;
            IDisposable runtimeScope = null;
            try
            {
                ValidateFallbackOptions(options, true);
                if (string.IsNullOrEmpty(_config.PublishableKey))
                    throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, MissingPublishableKeyMessage());
                ValidateStatusSource();

                ApplyClientInitiatedDefaults(ref request);
                var fallback = CreateFallbackAttempt(options, request, null);
                browser = CreateBrowserForReturnUrl(request.SuccessUrl);
                runtimeScope = EnterRuntimeScope(browser);
                try
                {
                    PrepareLaunchIfSupported(browser);
                }
                catch (CheckoutPreparationException e)
                {
                    if (!fallback.Enabled) throw e.CheckoutError;
                    return await CompleteFallbackAsync(
                        fallback,
                        FallbackReason.LaunchFailed,
                        e.CheckoutError,
                        browser);
                }

                SetState(CheckoutState.Creating);
                AuctionResult auction;
                try
                {
                    auction = fallback.Enabled
                        ? await CreateAuctionWithinFallbackDeadlineAsync(request)
                        : await _createAuction(request, M2CApi.DefaultHttpTimeoutSeconds, CancellationToken.None);
                }
                catch (AuctionDeadlineReachedException)
                {
                    return await CompleteFallbackAsync(
                        fallback,
                        FallbackReason.Timeout,
                        new M2CCheckoutException(M2CErrorCode.Network, "auction request timed out"),
                        browser);
                }
                catch (Exception e)
                {
                    var original = AsCheckoutException(e);
                    FallbackReason reason;
                    if (!fallback.Enabled || !TryClassifyAuctionFailure(original, out reason)) throw;
                    return await CompleteFallbackAsync(fallback, reason, original, browser);
                }

                if (fallback.Enabled) fallback.RequestId = auction.RequestId;
                if (auction.Ttl <= 0)
                {
                    var invalidTtl = new M2CCheckoutException(
                        M2CErrorCode.Unknown,
                        "auction response contained an invalid checkout TTL");
                    if (!fallback.Enabled) throw invalidTtl;
                    return await CompleteFallbackAsync(
                        fallback,
                        FallbackReason.ApiError,
                        invalidTtl,
                        browser);
                }
                if (!IsValidCheckoutUrl(auction.CheckoutUrl))
                {
                    var invalidUrl = new M2CCheckoutException(
                        M2CErrorCode.Unknown,
                        "auction response contained an invalid checkout URL");
                    if (!fallback.Enabled) throw invalidUrl;
                    return await CompleteFallbackAsync(
                        fallback,
                        FallbackReason.ApiError,
                        invalidUrl,
                        browser);
                }

                return await RunAsync(
                    auction.CheckoutUrl,
                    auction.RequestId,
                    "client",
                    request.SuccessUrl,
                    request.CancelUrl,
                    browser,
                    fallback);
            }
            catch
            {
                CancelPreparedLaunch(browser);
                if (State != CheckoutState.Error) SetState(CheckoutState.Error);
                throw;
            }
            finally
            {
                runtimeScope?.Dispose();
                _inFlight = false;
            }
        }

        /// <summary>Create a shop session, launch the winning storefront, and return its immutable handle.</summary>
        public async Task<ShopSessionHandle> StartShopSessionAsync(ShopSessionRequest request)
        {
            ValidateShopSessionConfig();
            ValidateShopSessionRequest(request);
            IShopSessionBrowser browser = _createShopSessionBrowser();
            if (_inFlight)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "a checkout is already in progress");
            _inFlight = true;
            try
            {
                browser.PrepareLaunch();
                double started = _monotonicSeconds();
                ShopSessionResult created = await _createShopSession(request);
                string sessionId = M2CApi.CanonicalSessionId(created.SessionId);
                if (created.Ttl < 60 || created.Ttl > 3600
                    || !IsValidCheckoutUrl(created.ShopUrl))
                {
                    throw new M2CCheckoutException(
                        M2CErrorCode.Unknown,
                        "shop session response had an unexpected shape");
                }
                if (_monotonicSeconds() - started >= created.Ttl)
                    throw new M2CCheckoutException(M2CErrorCode.CheckoutExpired, "shop URL expired before launch");
                await browser.LaunchAsync(created.ShopUrl, request.ReturnUrl);
                return new ShopSessionHandle(
                    sessionId,
                    created.SessionExpiresAt,
                    created.ShopUrlExpiresAt,
                    created.ShopUrl);
            }
            finally
            {
                try
                {
                    browser.CancelPreparedLaunch();
                }
                finally
                {
                    _inFlight = false;
                }
            }
        }

        /// <summary>Read one advisory shop-session status snapshot without polling or retrying.</summary>
        public Task<ShopSessionStatus> ReadShopSessionStatusAsync(string sessionId)
        {
            ValidateShopSessionConfig();
            string canonical = M2CApi.CanonicalSessionId(sessionId);
            return _readShopSessionStatus(canonical);
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
            double started = _monotonicSeconds();
            return ResolveStatusWithBackstopAsync(
                requestId, null, started + M2CApi.DefaultHttpTimeoutSeconds,
                started + _config.M2CFallbackAfterSeconds,
                budget => ResolveStatusWithinBudgetAsync(requestId, null, budget));
        }

        private async Task<CheckoutResult> RunAsync(
            string checkoutUrl,
            string requestId,
            string mode,
            string returnUrl,
            string cancelUrl,
            ICheckoutBrowser preparedBrowser,
            FallbackAttempt fallback)
        {
            if (string.IsNullOrEmpty(checkoutUrl) || string.IsNullOrEmpty(requestId))
            {
                SetState(CheckoutState.Error);
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "missing checkout url or request id");
            }

            CheckoutBrowserFactory.ValidateReturnUrls(_config, returnUrl, cancelUrl);
            ICheckoutBrowser browser = preparedBrowser ?? CreateBrowserForReturnUrl(returnUrl);
            IDisposable runtimeScope = EnterRuntimeScope(browser);
            if (fallback.Enabled) fallback.RequestId = requestId;

            try
            {
                SetState(CheckoutState.Ready);
                SetState(CheckoutState.Launching);
                // Persist immediately before the exposure boundary, regardless of
                // fallback policy, so process death cannot strand a launched checkout.
                ResumeStore.Save(requestId, mode, _config.StatusSource);
                if (!fallback.Enabled) SetState(CheckoutState.AwaitingReturn);

                Task<BrowserOutcome> launchTask;
                try
                {
                    var requestContext = browser as ICheckoutBrowserRequestContext;
                    if (requestContext != null) requestContext.SetRequestId(requestId);
                    launchTask = browser.LaunchAsync(checkoutUrl, returnUrl, cancelUrl);
                }
                catch (Exception e)
                {
                    fallback.MarkLaunchedOrUnknown();
                    if (!fallback.Enabled) ResumeStore.Clear();
                    SetState(CheckoutState.Error);
                    throw AsCheckoutException(e);
                }

                BrowserOutcome outcome = default(BrowserOutcome);
                bool outcomeReady = launchTask.IsCompleted;
                if (outcomeReady)
                {
                    try
                    {
                        outcome = await launchTask;
                    }
                    catch (Exception e)
                    {
                        fallback.MarkLaunchedOrUnknown();
                        if (!fallback.Enabled) ResumeStore.Clear();
                        SetState(CheckoutState.Error);
                        throw AsCheckoutException(e);
                    }

                    if (outcome.Result == BrowserResult.PreparedLaunchFailed && fallback.Enabled)
                    {
                        ResumeStore.Clear();
                        return await CompleteFallbackAsync(
                            fallback,
                            FallbackReason.LaunchFailed,
                            new M2CCheckoutException(
                                M2CErrorCode.Unknown,
                                "the prepared checkout window closed before vendor navigation"),
                            browser);
                    }
                }

                // LaunchAsync performs the platform launch synchronously before it
                // returns its outcome task. Once that call returns without the
                // explicit prepared-window failure above, exposure is possible and
                // the per-invocation latch can never move backward.
                fallback.MarkLaunchedOrUnknown();
                if (fallback.Enabled)
                {
                    SetState(CheckoutState.AwaitingReturn);
                }

                if (!outcomeReady)
                {
                    try
                    {
                        outcome = await launchTask;
                    }
                    catch (Exception e)
                    {
                        ResumeStore.Clear();
                        SetState(CheckoutState.Error);
                        throw AsCheckoutException(e);
                    }
                }

                if (outcome.Result == BrowserResult.PreparedLaunchFailed)
                {
                    if (!fallback.Enabled)
                    {
                        // With the fallback off, this internal WebGL signal
                        // surfaces as a browser cancellation.
                        outcome = BrowserOutcome.Canceled;
                    }
                    else
                    {
                        ResumeStore.Clear();
                        SetState(CheckoutState.Error);
                        throw new M2CCheckoutException(
                            M2CErrorCode.Unknown,
                            "checkout launch failed after the exposure boundary");
                    }
                }

                // Launched: a surface that polls for its outcome over the full window (the
                // Editor real-checkout mock; backend-session flows).
                if (outcome.Result == BrowserResult.Launched)
                    return await PollAsync(requestId);

                // Closed: WebGL close is ambiguous and may follow a completed payment
                // whose webhook-fed status has not reached the merchant yet.
                if (outcome.Result == BrowserResult.Closed)
                    return await PollAsync(requestId);

                // Resumed: a return-capable surface ended with no return URL. A short
                // status window catches terminal state that did not redirect. It is
                // never a cancel, because a bare resume can also be 3DS/OTP bounce-back.
                if (outcome.Result == BrowserResult.Resumed)
                    return await ResolveViaShortStatusPollAsync(requestId, ResumedStatusWindowSeconds);

                // Canceled: the customer explicitly closed a return-capable surface (iOS
                // canceledLogin / Auth Tab RESULT_CANCELED). Backend terminal status still
                // wins if it is already visible; otherwise this is a browser cancel.
                if (outcome.Result == BrowserResult.Canceled)
                    return await ResolveBrowserCancelAsync(requestId);

                SetState(CheckoutState.Returned);

                if (outcome.Result == BrowserResult.Dismissed)
                {
                    ResumeStore.Clear();
                    return Terminal(new CheckoutCanceled(requestId), CheckoutState.Canceled);
                }

                ReturnClassification classification = ReturnClassifier.Classify(
                    outcome.ReturnUrl,
                    returnUrl,
                    cancelUrl,
                    requestId);
                if (classification.Error == ReturnClassifier.RequestIdMismatch)
                {
                    // Never let a link-supplied id steer status reads. Reconcile the
                    // persisted active checkout over the bounded ambiguity window.
                    return await ResolveViaShortStatusPollAsync(requestId, ResumedStatusWindowSeconds);
                }

                if (classification.Verdict == ReturnVerdict.Cancel)
                {
                    ResumeStore.Clear();
                    return Terminal(new CheckoutCanceled(requestId), CheckoutState.Canceled);
                }
                if (classification.Verdict == ReturnVerdict.Unknown)
                {
                    ResumeStore.Clear();
                    SetState(CheckoutState.Error);
                    throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "return url did not match the active return or cancel URL");
                }

                return await PollAsync(requestId);
            }
            finally
            {
                runtimeScope?.Dispose();
            }
        }
        private ICheckoutBrowser CreateBrowserForReturnUrl(string returnUrl)
        {
            ICheckoutBrowser browser = _createBrowser(returnUrl);
            if (browser.RequiresReturnUrl && string.IsNullOrEmpty(returnUrl))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, MissingReturnUrlMessage());
            return browser;
        }

        private static void PrepareLaunchIfSupported(ICheckoutBrowser browser)
        {
            var prelauncher = browser as ICheckoutBrowserPrelauncher;
            if (prelauncher != null) prelauncher.PrepareLaunch();
        }

        private static void CancelPreparedLaunch(ICheckoutBrowser browser)
        {
            var prelauncher = browser as ICheckoutBrowserPrelauncher;
            if (prelauncher != null) prelauncher.CancelPreparedLaunch();
        }

        private static IDisposable EnterRuntimeScope(ICheckoutBrowser browser)
        {
            var scoped = browser as ICheckoutBrowserRuntimeScope;
            return scoped != null ? scoped.EnterRuntimeScope() : null;
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
            double started = _monotonicSeconds();
            double deadline = started + sched.TotalWindowSeconds;
            int attempt = 0;

            while (_monotonicSeconds() < deadline)
            {
                double delay = sched.DelayForAttempt(attempt++);
                if (delay > 0)
                {
                    double remainingBeforeDelay = deadline - _monotonicSeconds();
                    if (remainingBeforeDelay <= 0) break;
                    await _delay(Math.Min(delay, remainingBeforeDelay), CancellationToken.None);
                    if (delay >= remainingBeforeDelay) break;
                }

                double remaining = deadline - _monotonicSeconds();
                if (remaining <= 0) break;

                ClientStatus status = ClientStatus.Processing;
                try
                {
                    status = await ResolveStatusWithBackstopAsync(
                        requestId, statusSource, deadline, started + _config.M2CFallbackAfterSeconds,
                        budget => ResolveStatusWithinBudgetAsync(requestId, statusSource, budget));
                }
                catch (OperationCanceledException)
                {
                    throw;
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
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[M2C] status read failed, will retry: " + e.Message);
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

            // Browser tabs can suspend the Unity loop while checkout is open. Take a
            // final authoritative read after the wall-clock window so a completion
            // that landed during suspension is not returned as pending without ever
            // being observed.
            return await ResolveStatusReadWithFallbackAsync(requestId, false, statusSource);
        }

        // Resolve a return-less mobile resume with a bounded, short poll. WebGL
        // closes use PollAsync because their completion signal is ambiguous.
        private async Task<CheckoutResult> ResolveViaShortStatusPollAsync(string requestId, double maxWindowSeconds)
        {
            SetState(CheckoutState.Polling);
            var sched = _config.Poll ?? PollSchedule.Default;
            double windowSeconds = Math.Min(maxWindowSeconds, sched.TotalWindowSeconds);
            double started = _monotonicSeconds();
            double deadline = started + windowSeconds;
            bool useFallback = ShouldUseM2CFallback(null);
            // A stalled merchant read must leave time for one final backstop probe.
            double primaryDeadline = useFallback ? deadline - windowSeconds / 2 : deadline;
            int attempt = 0;

            while (_monotonicSeconds() < primaryDeadline)
            {
                double delay = ReturnlessStatusDelayForAttempt(attempt++);
                if (delay > 0)
                {
                    double remainingBeforeDelay = primaryDeadline - _monotonicSeconds();
                    if (remainingBeforeDelay <= 0) break;
                    await _delay(Math.Min(delay, remainingBeforeDelay), CancellationToken.None);
                    if (delay >= remainingBeforeDelay) break;
                }

                double remaining = primaryDeadline - _monotonicSeconds();
                if (remaining <= 0) break;

                ClientStatus status;
                try
                {
                    status = await ResolveStatusWithBackstopAsync(
                        requestId, null, primaryDeadline, double.PositiveInfinity,
                        budget => ResolveStatusWithinBudgetAsync(requestId, null, budget));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (M2CCheckoutException e)
                {
                    if (!IsRetryableStatusRead(e))
                    {
                        ResumeStore.Clear();
                        SetState(CheckoutState.Error);
                        throw;
                    }

                    Debug.LogWarning("[M2C] status read failed, will retry briefly: " + e.Message);
                    continue;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[M2C] status read failed, will retry briefly: " + e.Message);
                    continue;
                }

                CheckoutResult result = ResultFromStatusRead(requestId, status);
                if (result.Outcome != CheckoutOutcome.PendingTimeout)
                {
                    ResumeStore.Clear();
                    return Terminal(result, StateForResult(result));
                }
            }

            if (useFallback)
            {
                ClientStatus fallback = await ReadM2CFallbackAsync(requestId, deadline - _monotonicSeconds());
                CheckoutResult result = ResultFromStatusRead(requestId, fallback);
                if (result.Outcome != CheckoutOutcome.PendingTimeout)
                {
                    ResumeStore.Clear();
                    return Terminal(result, StateForResult(result));
                }
            }
            ResumeStore.Clear();
            return Terminal(new CheckoutPendingTimeout(requestId), CheckoutState.PendingTimeout);
        }

        private static double ReturnlessStatusDelayForAttempt(int attemptIndex)
        {
            if (attemptIndex <= 0) return 0.0;
            if (attemptIndex == 1) return 0.25;
            return 0.5;
        }

        private async Task<CheckoutResult> ResolveBrowserCancelAsync(string requestId)
        {
            return await ResolveStatusReadWithFallbackAsync(requestId, true);
        }

        private async Task<CheckoutResult> ResolveStatusReadWithFallbackAsync(string requestId, bool cancelWhenProcessing, StatusSource statusSource = null)
        {
            if (State != CheckoutState.Polling) SetState(CheckoutState.Polling);
            ClientStatus status;
            try
            {
                double started = _monotonicSeconds();
                status = await ResolveStatusWithBackstopAsync(
                    requestId, statusSource, started + M2CApi.DefaultHttpTimeoutSeconds, started,
                    budget => ResolveStatusWithinBudgetAsync(requestId, statusSource, budget));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (M2CCheckoutException e) when (!IsRetryableStatusRead(e))
            {
                ResumeStore.Clear();
                SetState(CheckoutState.Error);
                throw;
            }
            catch (M2CCheckoutException e)
            {
                Debug.LogWarning("[M2C] status read failed, treating as " + (cancelWhenProcessing ? "canceled" : "pending") + ": " + e.Message);
                status = ClientStatus.Processing;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[M2C] status read failed, treating as " + (cancelWhenProcessing ? "canceled" : "pending") + ": " + e.Message);
                status = ClientStatus.Processing;
            }
            ResumeStore.Clear();
            CheckoutResult result = cancelWhenProcessing
                ? ResultFromBrowserCancelStatusRead(requestId, status)
                : ResultFromStatusRead(requestId, status);
            return Terminal(result, StateForResult(result));
        }

        internal static CheckoutResult ResultFromStatusRead(string requestId, ClientStatus status)
        {
            switch (status)
            {
                case ClientStatus.Completed:
                    return new CheckoutCompleted(requestId);
                case ClientStatus.Failed:
                    return new CheckoutFailed(requestId);
                case ClientStatus.Canceled:
                    return new CheckoutCanceled(requestId);
                default:
                    return new CheckoutPendingTimeout(requestId);
            }
        }

        internal static CheckoutResult ResultFromBrowserCancelStatusRead(string requestId, ClientStatus status)
        {
            CheckoutResult result = ResultFromStatusRead(requestId, status);
            return result.Outcome == CheckoutOutcome.PendingTimeout
                ? new CheckoutCanceled(requestId)
                : result;
        }

        private static CheckoutState StateForResult(CheckoutResult result)
        {
            switch (result.Outcome)
            {
                case CheckoutOutcome.Completed:
                    return CheckoutState.Completed;
                case CheckoutOutcome.Failed:
                    return CheckoutState.Failed;
                case CheckoutOutcome.Canceled:
                    return CheckoutState.Canceled;
                case CheckoutOutcome.FallbackStarted:
                    return CheckoutState.FallbackStarted;
                default:
                    return CheckoutState.PendingTimeout;
            }
        }

        private Task<ClientStatus> ResolveStatusAsync(string requestId, StatusSource statusSource = null, double timeoutBudgetSeconds = 0)
        {
            var src = statusSource ?? _config.StatusSource ?? StatusSource.M2C;
            if (_readStatus != null) return _readStatus(requestId, src, timeoutBudgetSeconds);
            switch (src.Kind)
            {
                case StatusSourceKind.Url:
                    return M2CApi.ReadStatusUrlAsync(src.UrlTemplate, requestId, timeoutBudgetSeconds);
                case StatusSourceKind.Callback:
                    return ReadStatusCallbackAsync(requestId, src, timeoutBudgetSeconds);
                default:
                    return _api.ReadStatusM2CAsync(requestId, timeoutBudgetSeconds);
            }
        }

        private async Task<ClientStatus> ReadStatusCallbackAsync(string requestId, StatusSource source, double budget)
        {
            try
            {
                return await CallbackLimiter.InvokeAsync(() => source.CheckStatus(requestId), budget);
            }
            catch (OperationCanceledException) { throw; }
            catch (M2CCheckoutException) { throw; }
            catch (Exception)
            {
                throw new M2CCheckoutException(M2CErrorCode.ServiceUnavailable, "status callback failed");
            }
        }

        private async Task<ClientStatus> ResolveStatusWithBackstopAsync(
            string requestId, StatusSource source, double deadline, double backstopEligibleAt,
            Func<double, Task<ClientStatus>> primaryRead)
        {
            double remaining = deadline - _monotonicSeconds();
            if (remaining <= 0) return ClientStatus.Processing;
            bool useFallback = ShouldUseM2CFallback(source);
            double untilThreshold = backstopEligibleAt - _monotonicSeconds();
            double primaryBudget = useFallback
                ? (untilThreshold <= 0 ? remaining / 2 : Math.Min(remaining, untilThreshold))
                : remaining;
            M2CCheckoutException primaryError = null;
            try
            {
                ClientStatus primary = await primaryRead(primaryBudget);
                if (primary != ClientStatus.Processing) return primary;
            }
            catch (M2CCheckoutException error) when (IsRetryableStatusRead(error))
            {
                primaryError = error;
            }
            if (useFallback && _monotonicSeconds() >= backstopEligibleAt)
            {
                ClientStatus fallback = await ReadM2CFallbackAsync(requestId, deadline - _monotonicSeconds());
                if (fallback != ClientStatus.Processing) return fallback;
            }
            if (primaryError != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryError).Throw();
            return ClientStatus.Processing;
        }

        private async Task<ClientStatus> ResolveStatusWithinBudgetAsync(string requestId, StatusSource statusSource, double timeoutBudgetSeconds)
        {
            if (double.IsNaN(timeoutBudgetSeconds) || double.IsInfinity(timeoutBudgetSeconds) || timeoutBudgetSeconds <= 0)
                timeoutBudgetSeconds = M2CApi.DefaultHttpTimeoutSeconds;

            Task<ClientStatus> statusTask = ResolveStatusAsync(requestId, statusSource, timeoutBudgetSeconds);
            if (statusTask.IsCompleted) return await statusTask;
            using (var timeoutCancellation = new CancellationTokenSource())
            {
                Task timeoutTask = _delay(timeoutBudgetSeconds, timeoutCancellation.Token);
                if (await Task.WhenAny(statusTask, timeoutTask) == statusTask)
                {
                    timeoutCancellation.Cancel();
                    return await statusTask;
                }

                ObserveFault(statusTask);
                await timeoutTask;
                throw new M2CCheckoutException(M2CErrorCode.Network, "status read timed out");
            }
        }

        // The M2C-status fallback backs a merchant's own (Url/Callback) source: it is
        // redundant when the primary already reads M2C, needs the publishable key the
        // M2C read is authenticated by, and never applies to the reserved subscribe
        // source. ValidateStatusSource fails loud when it is on without a key; this
        // degrades to no fallback if a key is somehow absent at poll time.
        private bool ShouldUseM2CFallback(StatusSource statusSource)
        {
            if (!_config.UseM2CStatusFallback) return false;
            StatusSource src = statusSource ?? _config.StatusSource ?? StatusSource.M2C;
            if (src.Kind != StatusSourceKind.Url && src.Kind != StatusSourceKind.Callback) return false;
            return !string.IsNullOrEmpty(_config.PublishableKey);
        }

        private async Task<ClientStatus> ReadM2CFallbackAsync(string requestId, double timeoutBudgetSeconds)
        {
            if (timeoutBudgetSeconds <= 0) return ClientStatus.Processing;
            try
            {
                return await ResolveStatusWithinBudgetAsync(requestId, StatusSource.M2C, timeoutBudgetSeconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                // A fallback read must never fail a checkout the primary could still
                // complete on its own; swallow everything and let the primary drive.
                // Warn once, not every cycle, so a persistently failing fallback (a
                // misconfigured key/origin, or an M2C blip) does not spam the log.
                if (!_loggedFallbackFailure)
                {
                    _loggedFallbackFailure = true;
                    Debug.LogWarning("[M2C] M2C status fallback read failed and is being ignored (the primary source still drives): " + e.Message);
                }
                return ClientStatus.Processing;
            }
        }

        private static void ObserveFault(Task<ClientStatus> task)
        {
            task.ContinueWith(t =>
            {
                t.Exception?.Handle(_ => true);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void ValidateFallbackOptions(CheckoutStartOptions options, bool clientInitiated)
        {
            var mode = options != null ? options.FallbackMode : FallbackMode.Inherit;
            if (mode != FallbackMode.Inherit && mode != FallbackMode.Disabled)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "invalid fallback mode");
            if (!clientInitiated || mode == FallbackMode.Disabled || _config.FallbackHandler == null)
                return;
            if (_config.FallbackAuctionTimeoutMs < MinFallbackAuctionTimeoutMs ||
                _config.FallbackAuctionTimeoutMs > MaxFallbackAuctionTimeoutMs)
            {
                throw new M2CCheckoutException(
                    M2CErrorCode.InvalidRequest,
                    "fallback auction timeout must be between 8000 and 30000 milliseconds");
            }
        }

        private FallbackAttempt CreateFallbackAttempt(
            CheckoutStartOptions options,
            AuctionRequest request,
            string requestId)
        {
            var mode = options != null ? options.FallbackMode : FallbackMode.Inherit;
            var handler = mode == FallbackMode.Disabled ? null : _config.FallbackHandler;
            return handler == null
                ? DisabledFallbackAttempt
                : new FallbackAttempt(handler, options, request, requestId);
        }

        private async Task<AuctionResult> CreateAuctionWithinFallbackDeadlineAsync(AuctionRequest request)
        {
            using (var cancellation = new CancellationTokenSource())
            using (var deadlineCancellation = new CancellationTokenSource())
            {
                Task<AuctionResult> auctionTask = _createAuction(request, 0, cancellation.Token);
                if (auctionTask.IsCompleted)
                    return await auctionTask;

                Task deadlineTask = _delay(
                    _config.FallbackAuctionTimeoutMs / 1000.0,
                    deadlineCancellation.Token);
                Task winner = await Task.WhenAny(auctionTask, deadlineTask);
                if (winner == auctionTask || auctionTask.IsCompleted)
                {
                    deadlineCancellation.Cancel();
                    return await auctionTask;
                }

                cancellation.Cancel();
                try { await auctionTask; }
                catch { /* Observe the aborted request; the deadline remains authoritative. */ }
                throw new AuctionDeadlineReachedException();
            }
        }

        private async Task<CheckoutResult> CompleteFallbackAsync(
            FallbackAttempt attempt,
            FallbackReason reason,
            M2CCheckoutException original,
            ICheckoutBrowser preparedBrowser)
        {
            if (attempt == null || !attempt.CanFallback)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
                return null;
            }

            // Close the blank checkout surface before merchant code can present IAP.
            CancelPreparedLaunch(preparedBrowser);
            // Billing fallback is only legal before vendor exposure. Flush any
            // just-written recovery record before merchant billing takes over.
            ResumeStore.Clear();
            var context = new FallbackContext
            {
                AttemptId = attempt.AttemptId,
                Reason = reason,
                OriginalError = original,
                RequestId = attempt.RequestId,
                FallbackProductId = attempt.FallbackProductId,
                LatencyMs = attempt.ElapsedMilliseconds,
                ProductId = attempt.Request.ProductId,
                Quantity = attempt.Request.Quantity ?? 1,
                Reference = attempt.Request.Reference
            };

            FallbackDecision decision;
            try
            {
                decision = await attempt.Handler(reason, context);
            }
            catch (Exception handlerError)
            {
                original.AttachFallback(FallbackStatus.HandlerOutcomeUnknown, handlerError);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
                return null;
            }

            if (decision == FallbackDecision.Accepted)
            {
                return Terminal(
                    new CheckoutFallbackStarted(attempt.AttemptId, attempt.RequestId, reason),
                    CheckoutState.FallbackStarted);
            }
            if (decision == FallbackDecision.Unavailable)
            {
                original.AttachFallback(FallbackStatus.Declined);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
                return null;
            }

            original.AttachFallback(
                FallbackStatus.HandlerOutcomeUnknown,
                new InvalidOperationException("fallback handler returned an invalid decision"));
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
            return null;
        }

        internal static bool TryClassifyAuctionFailure(
            M2CCheckoutException error,
            out FallbackReason reason)
        {
            if (error.Code == M2CErrorCode.NoVendorsAvailable)
            {
                reason = FallbackReason.NoBids;
                return true;
            }
            if (error.Code == M2CErrorCode.Network ||
                error.Code == M2CErrorCode.RateLimited ||
                error.Code == M2CErrorCode.ServiceUnavailable ||
                (error.Code == M2CErrorCode.Unknown &&
                 (error.HttpStatus == 0 || error.HttpStatus >= 500)))
            {
                reason = FallbackReason.ApiError;
                return true;
            }
            reason = default(FallbackReason);
            return false;
        }

        // Keep the HTTPS/loopback-HTTP contract aligned with the shared SDK URL vectors.
        internal static bool IsValidCheckoutUrl(string url)
        {
            return UrlValidator.IsValidHttpsOrLoopbackHttp(url);
        }

        private static M2CCheckoutException AsCheckoutException(Exception error)
        {
            var checkoutError = error as M2CCheckoutException;
            return checkoutError ?? new M2CCheckoutException(M2CErrorCode.Unknown, error.Message);
        }
        private void BeginFlow()
        {
            if (_inFlight)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "a checkout is already in progress");
            EnsureRuntimePlatformSupported();
            _inFlight = true;
        }

        private void ValidateShopSessionConfig()
        {
            if (string.IsNullOrEmpty(_config.PublishableKey)
                || !_config.PublishableKey.StartsWith("pub_", StringComparison.Ordinal))
            {
                throw new M2CCheckoutException(
                    M2CErrorCode.InvalidRequest,
                    "shop sessions require a publishable key");
            }
        }

        private static void ValidateShopSessionRequest(ShopSessionRequest request)
        {
            if (string.IsNullOrEmpty(request.Currency))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "currency must be non-empty");
            if (request.Language != null && Encoding.UTF8.GetByteCount(request.Language) > 64)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "language is too long");
            if (request.Segments != null)
            {
                if (request.Segments.Length > 20)
                    throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "segments are invalid");
                for (int i = 0; i < request.Segments.Length; i++)
                {
                    if (request.Segments[i] == null
                        || Encoding.UTF8.GetByteCount(request.Segments[i]) > 128)
                        throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "segments are invalid");
                }
            }
            if (!string.IsNullOrEmpty(request.ReturnUrl) && !IsValidShopReturnUrl(request.ReturnUrl))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "return URL is invalid");
        }

        private static bool IsValidShopReturnUrl(string value)
        {
            if (Encoding.UTF8.GetByteCount(value) > 2048) return false;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Scheme))
                return false;
            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme == "https") return !string.IsNullOrEmpty(uri.Host);
            if (scheme == "http") return UrlValidator.IsValidHttpsOrLoopbackHttp(value);
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            switch (scheme)
            {
                case "file":
                case "data":
                case "javascript":
                case "about":
                case "ftp":
                case "mailto":
                case "tel":
                case "sms":
                case "intent":
                case "market":
                    return false;
                default:
                    return true;
            }
#endif
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
            if (!IsValidCheckoutUrl(session.CheckoutUrl))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout session contains an invalid checkout URL");
            if (session.Ttl.HasValue && session.Ttl.Value <= 0)
                throw new M2CCheckoutException(M2CErrorCode.CheckoutExpired, "the checkout session has expired; create a new one");
        }

        private void ValidateStatusSource()
        {
            ValidateStatusSource(_config.StatusSource ?? StatusSource.M2C);
        }

        private void ValidateStatusSource(StatusSource src)
        {
            if (src.Kind == StatusSourceKind.M2C && string.IsNullOrEmpty(_config.PublishableKey))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, MissingStatusPublishableKeyMessage());
            if (src.Kind == StatusSourceKind.Url && !UrlValidator.IsValidStatusTemplate(src.UrlTemplate))
                throw new M2CCheckoutException(
                    M2CErrorCode.InvalidRequest,
                    "status url template must be absolute HTTPS (or loopback HTTP) and contain {request_id}");
            // The opt-in M2C fallback backs a merchant's own source, so it needs the
            // same publishable key the M2C read requires. Fail loud rather than
            // silently never falling back.
            if (_config.UseM2CStatusFallback
                && (src.Kind == StatusSourceKind.Url || src.Kind == StatusSourceKind.Callback)
                && string.IsNullOrEmpty(_config.PublishableKey))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, MissingFallbackPublishableKeyMessage());
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

        private static string MissingFallbackPublishableKeyMessage()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return "M2C status fallback requires a web publishable key on WebGL; set WebGL Publishable Key, or turn off Use M2C Status Fallback";
#else
            return "M2C status fallback requires a publishable key; set one for this platform, or turn off Use M2C Status Fallback";
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
        public IEnumerator StartFromSession(CheckoutSession session, Action<CheckoutResult> onResult = null, Action<CheckoutState> onState = null, CheckoutStartOptions options = null)
        {
            return Await(() => StartFromSessionAsync(session, options), onResult, onState);
        }

        /// <summary>Coroutine form of <see cref="StartAsync"/>.</summary>
        public IEnumerator Start(AuctionRequest request, Action<CheckoutResult> onResult = null, Action<CheckoutState> onState = null, CheckoutStartOptions options = null)
        {
            return Await(() => StartAsync(request, options), onResult, onState);
        }

        /// <summary>Coroutine form of <see cref="StartShopSessionAsync"/>.</summary>
        public IEnumerator StartShopSession(
            ShopSessionRequest request,
            Action<ShopSessionHandle> onResult,
            Action<M2CCheckoutException> onError)
        {
            return AwaitSession(() => StartShopSessionAsync(request), onResult, onError);
        }

        /// <summary>Coroutine form of <see cref="ReadShopSessionStatusAsync"/>.</summary>
        public IEnumerator ReadShopSessionStatus(
            string sessionId,
            Action<ShopSessionStatus> onResult,
            Action<M2CCheckoutException> onError)
        {
            return AwaitSession(() => ReadShopSessionStatusAsync(sessionId), onResult, onError);
        }

        private static IEnumerator AwaitSession<T>(
            Func<Task<T>> start,
            Action<T> onResult,
            Action<M2CCheckoutException> onError)
        {
            Task<T> task;
            try
            {
                task = start();
            }
            catch (Exception error)
            {
                onError?.Invoke(AsCheckoutException(error));
                yield break;
            }
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted || task.IsCanceled)
            {
                Exception error = task.Exception;
                if (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                    error = aggregate.InnerExceptions[0];
                onError?.Invoke(AsCheckoutException(
                    error ?? new M2CCheckoutException(M2CErrorCode.Unknown, "shop session operation was canceled")));
                yield break;
            }
            onResult?.Invoke(task.Result);
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
