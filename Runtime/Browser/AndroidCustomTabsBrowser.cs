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

        // Brief head start after the app returns to the foreground to let an in-flight
        // deep-link redirect resolve precisely before we fall back to a status read.
        // Short, because the resume fallback itself polls only briefly.
        private const double ReturnGraceSeconds = 0.5;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            var tcs = new TaskCompletionSource<BrowserOutcome>();
            Action<string> deepLinkHandler = null;
            Action<bool> focusHandler = null;
            bool backgrounded = false;

            Action cleanup = () =>
            {
                Application.deepLinkActivated -= deepLinkHandler;
                M2CScheduler.Instance.AppFocusChanged -= focusHandler;
            };

            // Happy path: the vendor's redirect to the registered return scheme fires
            // deepLinkActivated and brings the app back with the outcome URL.
            deepLinkHandler = url =>
            {
                if (!ReturnClassifier.IsConfiguredReturn(url, returnUrl, cancelUrl))
                    return;
                cleanup();
                tcs.TrySetResult(BrowserOutcome.Returned(url));
            };

            // Safety net for when no deep link arrives (scheme unregistered, redirect
            // failed, or the user swiped the tab away): when the app returns to the
            // foreground without one, resolve as Resumed so the client does a
            // short status reconciliation and then resolves quickly - it never treats
            // a bare app-resume as a cancel. That distinction matters because real
            // authenticated payments (3-D Secure / OTP, "approve in your bank app")
            // legitimately bounce the user out of the tab and back; canceling on resume
            // would kill live payments. A completed purchase resolves via the status poll
            // (or the deep link, if it fires); a genuine back-out resolves pending-timeout
            // quickly. The deep link and the merchant webhook remain the sources of truth.
            focusHandler = foreground =>
            {
                if (!foreground) { backgrounded = true; return; }
                if (!backgrounded) return; // ignore focus churn before the tab opened
                M2CScheduler.Instance.DelayThen(ReturnGraceSeconds, () =>
                {
                    if (tcs.Task.IsCompleted) return; // a deep link won the race
                    cleanup();
                    tcs.TrySetResult(BrowserOutcome.Resumed);
                });
            };

            Application.deepLinkActivated += deepLinkHandler;
            M2CScheduler.Instance.AppFocusChanged += focusHandler;

            if (!TryLaunchCustomTab(checkoutUrl))
            {
                // AndroidX Browser not present (or launch failed): fall back to the
                // external system browser. The deep-link / foreground return path is identical.
                Application.OpenURL(checkoutUrl);
            }

            return tcs.Task;
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
