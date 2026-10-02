using System;
using System.Threading.Tasks;

namespace M2C.Checkout.Internal
{
    /// <summary>
    /// Settles an Android Auth Tab checkout on whichever return signal arrives
    /// first: the M2CAuthTabActivity result bridge, or the deep-link and foreground
    /// signals <see cref="ReturnMonitor"/> watches. The helper alone cannot be
    /// trusted to report. On a browser without Auth Tab support, androidx falls
    /// back to a Custom Tab that reports only RESULT_CANCELED while the real
    /// redirect arrives as a deep link on the Unity activity, and a return through
    /// the launcher icon clears the helper before it reports anything. Waiting on
    /// the helper alone would leave the checkout pending forever and block every
    /// later start until the process restarts.
    /// </summary>
    internal sealed class AuthTabReturn
    {
        // A helper CANCELED can precede the deep link carrying the real redirect,
        // so it yields briefly before settling, matching ReturnMonitor's resume grace.
        internal const double CancelGraceSeconds = 0.5;

        private readonly TaskCompletionSource<BrowserOutcome> _completion =
            new TaskCompletionSource<BrowserOutcome>();
        private readonly IReturnMonitorRuntime _runtime;
        private readonly Func<Task<BrowserOutcome>> _fallback;
        private readonly ReturnMonitorSession _monitor;
        private bool _helperCanceled;

        internal AuthTabReturn(
            string returnUrl,
            string cancelUrl,
            IReturnMonitorRuntime runtime,
            Func<Task<BrowserOutcome>> fallback)
        {
            _runtime = runtime;
            _fallback = fallback;
            _monitor = ReturnMonitor.StartAlongsideCallback(returnUrl, cancelUrl, runtime);
            _monitor.Task.ContinueWith(OnMonitorSettled, TaskContinuationOptions.ExecuteSynchronously);
        }

        internal Task<BrowserOutcome> Task => _completion.Task;

        /// <summary>
        /// Handles one helper payload: "RETURNED|&lt;url&gt;" | "CANCELED|" |
        /// "RESUMED|" | "ERROR|&lt;message&gt;".
        /// </summary>
        internal void OnHelperResult(string payload)
        {
            if (_completion.Task.IsCompleted) return;

            payload = payload ?? string.Empty;
            int sep = payload.IndexOf('|');
            string kind = sep >= 0 ? payload.Substring(0, sep) : payload;
            string url = sep >= 0 ? payload.Substring(sep + 1) : string.Empty;

            if (kind == "RETURNED" && !string.IsNullOrEmpty(url))
            {
                _monitor.Stop();
                _completion.TrySetResult(BrowserOutcome.Returned(url));
                return;
            }

            if (kind == "ERROR")
            {
                // The Auth Tab never showed; the Custom Tabs fallback runs its own
                // monitor, so this one must stop first.
                _monitor.Stop();
                StartFallback();
                return;
            }

            if (kind == "CANCELED")
            {
                // The user closed the Auth Tab, or the Custom Tab fallback closed on
                // a redirect the deep link is about to deliver. The core lets
                // already-visible backend terminal status win before resolving cancel.
                _helperCanceled = true;
                _runtime.DelayThen(CancelGraceSeconds, () =>
                {
                    _monitor.Stop();
                    _completion.TrySetResult(BrowserOutcome.Canceled);
                });
                return;
            }

            // RESUMED (verification failed/timed out, or unknown): ambiguous, never a
            // hard cancel. The core reconciles status.
            _monitor.Stop();
            _completion.TrySetResult(BrowserOutcome.Resumed);
        }

        /// <summary>The helper never launched; release the monitor's subscriptions.</summary>
        internal void Abandon()
        {
            _monitor.Stop();
        }

        private void OnMonitorSettled(Task<BrowserOutcome> monitorTask)
        {
            if (monitorTask.IsFaulted)
            {
                _completion.TrySetException(monitorTask.Exception.InnerException ?? monitorTask.Exception);
                return;
            }
            if (monitorTask.IsCanceled) return;

            BrowserOutcome outcome = monitorTask.Result;
            // A bare foreground after the helper said CANCELED is still a cancel.
            if (outcome.Result == BrowserResult.Resumed && _helperCanceled)
                outcome = BrowserOutcome.Canceled;
            _completion.TrySetResult(outcome);
        }

        private void StartFallback()
        {
            Task<BrowserOutcome> fallbackTask;
            try
            {
                fallbackTask = _fallback();
            }
            catch (Exception e)
            {
                _completion.TrySetException(e);
                return;
            }

            fallbackTask.ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    _completion.TrySetCanceled();
                    return;
                }
                if (task.IsFaulted)
                {
                    _completion.TrySetException(task.Exception.InnerException ?? task.Exception);
                    return;
                }
                _completion.TrySetResult(task.Result);
            });
        }
    }
}
