#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading.Tasks;
using M2C.Checkout.Internal;
using UnityEngine;

namespace M2C.Checkout
{
    /// <summary>
    /// Android in-app browser via a Chrome Auth Tab (androidx.browser AuthTab),
    /// launched through the native M2CAuthTabActivity helper. Unlike a plain Custom
    /// Tab, an Auth Tab has no minimize button and returns its result through a real
    /// ActivityResult callback (bridged back as <see cref="M2CScheduler.AuthTabResult"/>).
    /// The callback is not guaranteed, so <see cref="AuthTabReturn"/> also watches the
    /// deep-link and foreground signals and settles on whichever arrives first. The
    /// Unity activity regains focus only once the Auth Tab and helper are gone, so
    /// minimizing or an OTP / 3-D Secure bounce inside the tab still cannot trip a
    /// false return.
    ///
    /// Graceful degradation, in order: if the return is an http(s) Universal/App Link,
    /// or the helper activity is missing, or the launch throws (older build, missing
    /// dependency), this hands off to <see cref="AndroidCustomTabsBrowser"/>, which in
    /// turn falls back to the system browser. No Auth Tab UI is shown before a handoff,
    /// so a packaging slip degrades cleanly and never breaks checkout.
    ///
    /// VERIFY ON DEVICE: the JNI launch, the helper activity, and the Auth Tab
    /// presentation cannot run in the Editor; some androidx.browser.auth symbols are
    /// pinned by the build (see M2CAuthTabActivity.java), not by this file.
    /// </summary>
    public sealed class AndroidAuthTabBrowser : ICheckoutBrowser
    {
        private readonly bool _persistent;

        public AndroidAuthTabBrowser() : this(false) { }

        internal AndroidAuthTabBrowser(bool persistent)
        {
            _persistent = persistent;
        }

        public bool RequiresReturnUrl => true;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            string scheme = SchemeOf(returnUrl);
            // Auth Tab here drives the custom-scheme return (the common game case). For
            // http(s) Universal/App Link returns, use the proven Custom Tabs + deep-link
            // path rather than Auth Tab's https host/path overload.
            if (string.IsNullOrEmpty(scheme) || scheme == "http" || scheme == "https")
                return new AndroidCustomTabsBrowser().LaunchAsync(checkoutUrl, returnUrl, cancelUrl);

            var authTab = new AuthTabReturn(
                returnUrl,
                cancelUrl,
                UnityReturnMonitorRuntime.Instance,
                () => new AndroidCustomTabsBrowser().LaunchAsync(checkoutUrl, returnUrl, cancelUrl));
            Action<string> resultHandler = null;
            resultHandler = payload =>
            {
                M2CScheduler.Instance.AuthTabResult -= resultHandler;
                authTab.OnHelperResult(payload);
            };
            M2CScheduler.Instance.AuthTabResult += resultHandler;
            // A deep link or foreground return can win before the helper reports.
            authTab.Task.ContinueWith(
                _ => M2CScheduler.Instance.AuthTabResult -= resultHandler,
                TaskContinuationOptions.ExecuteSynchronously);

            if (!TryLaunchAuthTab(checkoutUrl, scheme, _persistent))
            {
                M2CScheduler.Instance.AuthTabResult -= resultHandler;
                authTab.Abandon();
                // Helper activity unavailable: hand off to standard Custom Tabs (deep-link
                // + focus return), which itself degrades to the system browser.
                return new AndroidCustomTabsBrowser().LaunchAsync(checkoutUrl, returnUrl, cancelUrl);
            }

            return authTab.Task;
        }

        private static bool TryLaunchAuthTab(string checkoutUrl, string scheme, bool persistent)
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = new AndroidJavaObject("android.content.Intent"))
                {
                    intent.Call<AndroidJavaObject>("setClassName", activity, "com.m2c.checkout.M2CAuthTabActivity").Dispose();
                    intent.Call<AndroidJavaObject>("putExtra", "m2c_url", checkoutUrl).Dispose();
                    intent.Call<AndroidJavaObject>("putExtra", "m2c_scheme", scheme).Dispose();
                    intent.Call<AndroidJavaObject>("putExtra", "m2c_persistent", persistent).Dispose();
                    activity.Call("startActivity", intent);
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[M2C] Auth Tab helper launch failed (" + e.Message +
                                 "); falling back to Chrome Custom Tabs. Confirm M2CAuthTabActivity is in the generated " +
                                 "AndroidManifest and androidx.browser:browser:1.9.0 + androidx.activity are on the classpath.");
                return false;
            }
        }

        // The return scheme the Auth Tab keys on, parsed from the configured return URL
        // (e.g. "mygame" from "mygame://checkout/return").
        private static string SchemeOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            int i = url.IndexOf("://", StringComparison.Ordinal);
            return i > 0 ? url.Substring(0, i).ToLowerInvariant() : null;
        }
    }
}
#endif
