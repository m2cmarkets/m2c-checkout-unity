#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;

namespace M2C.Checkout
{
    /// <summary>
    /// WebGL browser: opens the checkout in a popup/new tab and waits for the return
    /// page to publish an origin-scoped, nonce-bound wake signal (the
    /// Plugins/WebGL .jslib shim).
    /// A full-page redirect is never used - it would tear down the running WebGL app.
    /// Popup launch mode pre-opens a blank window before the auction request so
    /// browser popup blockers still see a user-initiated open. Tab-style launch
    /// waits until the checkout URL is ready so the WebGL tab keeps running.
    ///
    /// VERIFY IN A BROWSER: the JS interop and the merchant return page's
    /// origin-scoped channel cannot be exercised in the Editor.
    /// </summary>
    public sealed class WebGLCheckoutBrowser : ICheckoutBrowser, ICheckoutBrowserPrelauncher, ICheckoutBrowserRuntimeScope, ICheckoutBrowserRequestContext
    {
        private const string PopupBlocked = "__M2C_POPUP_BLOCKED__";
        private const string PopupClosed = "__M2C_POPUP_CLOSED__";
        private const string PreparedClosed = "__M2C_PREPARED_CLOSED__";
        private const string StatusOnly = "__M2C_STATUS_ONLY__";

        private readonly M2CWebGLLaunchMode _launchMode;
        private string _requestId;

        private delegate void ReturnCallback(string url);

        [DllImport("__Internal")]
        private static extern int M2CCheckoutPrepare(int launchMode);

        [DllImport("__Internal")]
        private static extern void M2CCheckoutCancelPrepared();

        [DllImport("__Internal")]
        private static extern void M2CCheckoutOpen(string url, string returnUrl, string cancelUrl, string requestId, int launchMode, ReturnCallback onReturn);

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

        void ICheckoutBrowserRequestContext.SetRequestId(string requestId)
        {
            _requestId = requestId;
        }

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            var pending = new TaskCompletionSource<BrowserOutcome>();
            _pending = pending;
            M2CCheckoutOpen(checkoutUrl, returnUrl ?? string.Empty, cancelUrl ?? string.Empty, _requestId ?? string.Empty, LaunchModeCode(_launchMode), ReturnHandler);
            return pending.Task;
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
                // Browser close is ambiguous. The core uses its full status budget so
                // a completed payment cannot be lost behind a delayed status source.
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
            if (url == StatusOnly)
            {
                tcs.TrySetResult(BrowserOutcome.Launched);
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
