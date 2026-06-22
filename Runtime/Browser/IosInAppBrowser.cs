#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;

namespace M2C.Checkout
{
    /// <summary>
    /// iOS in-app browser via <c>ASWebAuthenticationSession</c> (the Plugins/iOS shim).
    /// It presents the checkout in a system-managed in-app browser bound to the
    /// return URL's custom scheme. This is the frictionless mobile path and pairs
    /// with a custom-scheme return (mygame://...). For an https Universal Link
    /// return, the browser factory routes through the external browser because
    /// ASWebAuthenticationSession keys on a custom scheme, not a universal link.
    ///
    /// VERIFY ON DEVICE: the interop boundary and presentation cannot be exercised in
    /// the Editor.
    /// </summary>
    public sealed class IosInAppBrowser : ICheckoutBrowser
    {
        private delegate void AuthCallback(int success, string url);

        [DllImport("__Internal")]
        private static extern void m2c_presentAuthSession(string url, string callbackScheme, AuthCallback callback);

        private static readonly AuthCallback AuthCompleteCallback = OnAuthComplete;

        // Only one session is presented at a time; the static holds the in-flight
        // completion so the [MonoPInvokeCallback] static can resolve it.
        private static TaskCompletionSource<BrowserOutcome> _pending;

        public bool RequiresReturnUrl => true;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            string scheme = SchemeOf(returnUrl);
            if (string.IsNullOrEmpty(scheme))
                throw new M2CCheckoutException(M2CErrorCode.InvalidRequest, "iOS in-app checkout requires a custom-scheme return URL");

            _pending = new TaskCompletionSource<BrowserOutcome>();
            try
            {
                m2c_presentAuthSession(checkoutUrl, scheme, AuthCompleteCallback);
            }
            catch
            {
                _pending = null;
                throw;
            }
            return _pending.Task;
        }

        [MonoPInvokeCallback(typeof(AuthCallback))]
        private static void OnAuthComplete(int success, string url)
        {
            var tcs = _pending;
            _pending = null;
            if (tcs == null) return;
            if (success == 1 && !string.IsNullOrEmpty(url))
            {
                tcs.TrySetResult(BrowserOutcome.Returned(url));
                return;
            }
            if (success < 0)
            {
                tcs.TrySetException(new M2CCheckoutException(
                    M2CErrorCode.Unknown,
                    string.IsNullOrEmpty(url) ? "iOS auth session failed" : url));
                return;
            }
            if (success == 2)
            {
                // The customer explicitly dismissed the session (canceledLogin) - a
                // reliable browser-cancel signal. The core lets already-visible
                // backend terminal status win before otherwise resolving cancel.
                tcs.TrySetResult(BrowserOutcome.Canceled);
                return;
            }

            // success == 0: no callback URL and no explicit cancel - ambiguous (e.g. a
            // session interrupted by an app-to-app bounce that may still complete).
            // Reconcile through a status read instead of hard-canceling locally.
            tcs.TrySetResult(BrowserOutcome.Resumed);
        }

        private static string SchemeOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            int i = url.IndexOf("://", StringComparison.Ordinal);
            return i > 0 ? url.Substring(0, i).ToLowerInvariant() : string.Empty;
        }
    }
}
#endif
