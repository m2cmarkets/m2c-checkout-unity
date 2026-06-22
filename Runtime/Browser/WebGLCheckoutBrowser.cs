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
    /// Popup launch mode pre-opens a blank window before the auction request so
    /// browser popup blockers still see a user-initiated open. Tab-style launch
    /// waits until the checkout URL is ready so the WebGL tab keeps running.
    ///
    /// VERIFY IN A BROWSER: the JS interop and the merchant return page's postMessage
    /// cannot be exercised in the Editor.
    /// </summary>
    public sealed class WebGLCheckoutBrowser : ICheckoutBrowser, ICheckoutBrowserPrelauncher, ICheckoutBrowserRuntimeScope
    {
        private const string PopupBlocked = "__M2C_POPUP_BLOCKED__";
        private const string PopupClosed = "__M2C_POPUP_CLOSED__";
        private const string PreparedClosed = "__M2C_PREPARED_CLOSED__";

        private readonly M2CWebGLLaunchMode _launchMode;

        private delegate void ReturnCallback(string url);

        [DllImport("__Internal")]
        private static extern int M2CCheckoutPrepare(int launchMode);

        [DllImport("__Internal")]
        private static extern void M2CCheckoutCancelPrepared();

        [DllImport("__Internal")]
        private static extern void M2CCheckoutOpen(string url, string returnUrl, string cancelUrl, int launchMode, ReturnCallback onReturn);

        private static readonly ReturnCallback ReturnHandler = OnReturn;

        private static TaskCompletionSource<BrowserOutcome> _pending;

        public WebGLCheckoutBrowser(M2CWebGLLaunchMode launchMode)
        {
            _launchMode = launchMode;
        }

        public bool RequiresReturnUrl => true;

        public IDisposable EnterRuntimeScope()
        {
            if (_launchMode != M2CWebGLLaunchMode.Popup) return null;
            return RunInBackgroundScope.Enter();
        }

        public void PrepareLaunch()
        {
            if (_launchMode != M2CWebGLLaunchMode.Popup) return;
            if (M2CCheckoutPrepare(LaunchModeCode(_launchMode)) == 0)
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout window was blocked; allow popups for this site and try again");
        }

        public void CancelPreparedLaunch()
        {
            if (_launchMode != M2CWebGLLaunchMode.Popup) return;
            M2CCheckoutCancelPrepared();
        }

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            _pending = new TaskCompletionSource<BrowserOutcome>();
            M2CCheckoutOpen(checkoutUrl, returnUrl ?? string.Empty, cancelUrl ?? string.Empty, LaunchModeCode(_launchMode), ReturnHandler);
            return _pending.Task;
        }

        private static int LaunchModeCode(M2CWebGLLaunchMode launchMode)
        {
            return launchMode == M2CWebGLLaunchMode.Popup ? 2 :
                   launchMode == M2CWebGLLaunchMode.NewTab ? 1 : 0;
        }

        [MonoPInvokeCallback(typeof(ReturnCallback))]
        private static void OnReturn(string url)
        {
            var tcs = _pending;
            _pending = null;
            if (tcs == null) return;
            if (url == PopupBlocked)
            {
                tcs.TrySetException(new M2CCheckoutException(M2CErrorCode.InvalidRequest, "checkout window was blocked; allow popups for this site and try again"));
                return;
            }
            if (url == PopupClosed)
            {
                // A return page can close without postMessage reaching the opener
                // when the browser severs opener across origins. Reconcile with
                // a short status window instead of waiting out the full poll window
                // or turning a completed payment into a cancel.
                tcs.TrySetResult(BrowserOutcome.Closed);
                return;
            }
            if (url == PreparedClosed)
            {
                // The customer closed the pre-opened blank surface before checkout
                // could be navigated there. No vendor page ran, so this is a real
                // browser cancel rather than an ambiguous post-checkout close.
                tcs.TrySetResult(BrowserOutcome.Canceled);
                return;
            }
            if (string.IsNullOrEmpty(url))
                tcs.TrySetResult(BrowserOutcome.Launched);
            else
                tcs.TrySetResult(BrowserOutcome.Returned(url));
        }

        private sealed class RunInBackgroundScope : IDisposable
        {
            private readonly bool _previous;
            private bool _disposed;

            private RunInBackgroundScope()
            {
                _previous = UnityEngine.Application.runInBackground;
                UnityEngine.Application.runInBackground = true;
            }

            public static RunInBackgroundScope Enter()
            {
                return new RunInBackgroundScope();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                UnityEngine.Application.runInBackground = _previous;
            }
        }
    }
}
#endif
