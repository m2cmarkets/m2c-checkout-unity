#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading.Tasks;
using M2C.Checkout.Internal;
using UnityEngine;

namespace M2C.Checkout
{
    /// <summary>
    /// Android in-app browser via Chrome Custom Tabs, constructed entirely from C#
    /// through JNI (no Java/Kotlin file). The return arrives as a deep link on
    /// <c>Application.deepLinkActivated</c> - Custom Tabs have no completion callback,
    /// so the vendor's redirect to the registered scheme is what brings the app back.
    ///
    /// Requires the AndroidX Browser library on the classpath. The package's build
    /// post-processor (M2CBuildPostProcessor) adds
    /// 'androidx.browser:browser:1.9.0' to the generated Gradle project
    /// automatically, so no EDM4U install or manual gradle edit is needed. If the
    /// library is ever missing, this degrades gracefully to the system browser.
    ///
    /// VERIFY ON DEVICE: the JNI path and Custom Tabs presentation can't run in the Editor.
    /// </summary>
    public sealed class AndroidCustomTabsBrowser : ICheckoutBrowser
    {
        public bool RequiresReturnUrl => true;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            Task<BrowserOutcome> outcome = ReturnMonitor.Start(returnUrl, cancelUrl);

            if (!TryLaunchCustomTab(checkoutUrl))
            {
                // AndroidX Browser not present (or launch failed): fall back to the
                // external system browser. The deep-link / foreground return path is identical.
                Application.OpenURL(checkoutUrl);
            }

            return outcome;
        }

        private static bool TryLaunchCustomTab(string url)
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var builder = new AndroidJavaObject("androidx.browser.customtabs.CustomTabsIntent$Builder"))
                using (var customTabsIntent = builder.Call<AndroidJavaObject>("build"))
                using (var uriClass = new AndroidJavaClass("android.net.Uri"))
                using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", url))
                {
                    customTabsIntent.Call("launchUrl", activity, uri);
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[M2C] Chrome Custom Tabs unavailable (" + e.Message +
                                 "); falling back to the system browser. The Android build post-processor " +
                                 "adds 'androidx.browser:browser:1.9.0' automatically; check the generated Gradle project " +
                                 "or your dependency resolver if in-app tabs should be available.");
                return false;
            }
        }
    }
}
#endif
