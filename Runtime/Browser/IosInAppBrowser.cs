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
    /// return URL's custom scheme, and reports either the callback URL or an explicit
    /// user-cancel. This is the frictionless mobile path and pairs with a custom-scheme
    /// return (mygame://...). For an https Universal Link return, the browser
    /// factory routes through the external browser because ASWebAuthenticationSession
    /// keys on a custom scheme, not a universal link.
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
            _pending = new TaskCompletionSource<BrowserOutcome>();
            m2c_presentAuthSession(checkoutUrl, SchemeOf(returnUrl), AuthCompleteCallback);
            return _pending.Task;
        }

        [MonoPInvokeCallback(typeof(AuthCallback))]
        private static void OnAuthComplete(int success, string url)
        {
            var tcs = _pending;
            _pending = null;
            if (tcs == null) return;
            if (success != 0 && !string.IsNullOrEmpty(url))
                tcs.TrySetResult(BrowserOutcome.Returned(url));
            else
                tcs.TrySetResult(BrowserOutcome.Dismissed); // user canceled / no callback URL
        }

        private static string SchemeOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            int i = url.IndexOf("://", StringComparison.Ordinal);
            return i > 0 ? url.Substring(0, i) : string.Empty;
        }
    }
}
#endif
