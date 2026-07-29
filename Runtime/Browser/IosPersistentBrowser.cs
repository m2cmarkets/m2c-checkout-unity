#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;
using M2C.Checkout.Internal;

namespace M2C.Checkout
{
    /// <summary>
    /// iOS persistence-preferred in-app browser via <c>SFSafariViewController</c>.
    /// Return and cancel deep links are observed by Unity while the browser remains
    /// in process. Browser-managed state is best effort and is not shared with the
    /// standalone Safari app.
    /// </summary>
    public sealed class IosPersistentBrowser : ICheckoutBrowser
    {
        private delegate void SafariCallback(int result, string message);

        private delegate void SafariDismissCallback();

        [DllImport("__Internal")]
        private static extern void m2c_presentSafariViewController(string url, SafariCallback callback);

        [DllImport("__Internal")]
        private static extern void m2c_dismissSafariViewController(SafariDismissCallback callback);

        private static readonly SafariCallback SafariCompleteCallback = OnSafariComplete;
        private static readonly SafariDismissCallback SafariDismissedCallback = OnSafariDismissed;
        private static ReturnMonitorSession _pending;
        private static TaskCompletionSource<bool> _dismissalCompletion;

        public bool RequiresReturnUrl => true;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            if (_pending != null)
                throw new M2CCheckoutException(
                    M2CErrorCode.InvalidRequest,
                    "an iOS persistent checkout browser is already open");

            var dismissalCompletion = new TaskCompletionSource<bool>();
            _dismissalCompletion = dismissalCompletion;
            ReturnMonitorSession session = null;
            session = ReturnMonitor.StartInProcess(returnUrl, cancelUrl, _ =>
            {
                m2c_dismissSafariViewController(SafariDismissedCallback);
            });
            _pending = session;

            try
            {
                m2c_presentSafariViewController(checkoutUrl, SafariCompleteCallback);
            }
            catch
            {
                if (ReferenceEquals(_pending, session)) _pending = null;
                if (ReferenceEquals(_dismissalCompletion, dismissalCompletion))
                    _dismissalCompletion = null;
                session.Stop();
                throw;
            }

            return AwaitDismissalAsync(session, dismissalCompletion);
        }

        private static async Task<BrowserOutcome> AwaitDismissalAsync(
            ReturnMonitorSession session,
            TaskCompletionSource<bool> dismissalCompletion)
        {
            try
            {
                BrowserOutcome outcome = await session.Task;
                await dismissalCompletion.Task;
                return outcome;
            }
            finally
            {
                if (ReferenceEquals(_pending, session)) _pending = null;
                if (ReferenceEquals(_dismissalCompletion, dismissalCompletion))
                    _dismissalCompletion = null;
            }
        }

        [MonoPInvokeCallback(typeof(SafariCallback))]
        private static void OnSafariComplete(int result, string message)
        {
            ReturnMonitorSession session = _pending;
            if (session == null) return;

            if (result < 0)
            {
                session.TrySetException(new M2CCheckoutException(
                    M2CErrorCode.Unknown,
                    string.IsNullOrEmpty(message) ? "iOS persistent browser failed" : message));
                return;
            }

            // The Done button does not prove that checkout was canceled. Reconcile
            // authoritative status through the same short resume path used by other
            // ambiguous browser dismissals.
            session.TrySetResult(BrowserOutcome.Resumed);
        }

        [MonoPInvokeCallback(typeof(SafariDismissCallback))]
        private static void OnSafariDismissed()
        {
            _dismissalCompletion?.TrySetResult(true);
        }
    }
}
#endif
