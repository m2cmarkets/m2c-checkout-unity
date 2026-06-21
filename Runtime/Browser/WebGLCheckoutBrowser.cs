#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;

namespace M2C.Checkout
{
    /// <summary>
    /// WebGL browser: opens the checkout in a popup/new tab and waits for the return
    /// page to <c>postMessage</c> back to the opener (the Plugins/WebGL .jslib shim).
    /// A full-page redirect is never used - it would tear down the running WebGL app.
    ///
    /// VERIFY IN A BROWSER: the JS interop and the merchant return page's postMessage
    /// cannot be exercised in the Editor.
    /// </summary>
    public sealed class WebGLCheckoutBrowser : ICheckoutBrowser
    {
        private const string PopupBlocked = "__M2C_POPUP_BLOCKED__";
        private const string PopupClosed = "__M2C_POPUP_CLOSED__";

        private delegate void ReturnCallback(string url);

        [DllImport("__Internal")]
        private static extern void M2CCheckoutOpen(string url, ReturnCallback onReturn);

        private static readonly ReturnCallback ReturnHandler = OnReturn;

        private static TaskCompletionSource<BrowserOutcome> _pending;

        public bool RequiresReturnUrl => true;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            _pending = new TaskCompletionSource<BrowserOutcome>();
            M2CCheckoutOpen(checkoutUrl, ReturnHandler);
            return _pending.Task;
        }

        [MonoPInvokeCallback(typeof(ReturnCallback))]
        private static void OnReturn(string url)
        {
            var tcs = _pending;
            _pending = null;
            if (tcs == null) return;
            if (url == PopupBlocked)
            {
                tcs.TrySetException(new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout popup was blocked; allow popups for this site and try again"));
                return;
            }
            if (url == PopupClosed)
            {
                // A return page can close without postMessage reaching the opener
                // when the browser severs opener across origins. Reconcile with
                // status instead of turning a completed payment into a cancel.
                tcs.TrySetResult(BrowserOutcome.Launched);
                return;
            }
            if (string.IsNullOrEmpty(url))
                tcs.TrySetResult(BrowserOutcome.Launched);
            else
                tcs.TrySetResult(BrowserOutcome.Returned(url));
        }
    }
}
#endif
