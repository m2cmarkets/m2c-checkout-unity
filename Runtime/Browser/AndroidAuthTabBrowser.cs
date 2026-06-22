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
    /// ActivityResult callback (bridged back as <see cref="M2CScheduler.AuthTabResult"/>),
    /// so the return is never inferred from app focus. That removes the whole class of
    /// false returns - minimize, OTP / 3-D Secure bounces, a backgrounded tab - because
    /// we simply wait for the callback instead of guessing from a foreground event.
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
        public bool RequiresReturnUrl => true;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            string scheme = SchemeOf(returnUrl);
            // Auth Tab here drives the custom-scheme return (the common game case). For
            // http(s) Universal/App Link returns, use the proven Custom Tabs + deep-link
            // path rather than Auth Tab's https host/path overload.
            if (string.IsNullOrEmpty(scheme) || scheme == "http" || scheme == "https")
                return new AndroidCustomTabsBrowser().LaunchAsync(checkoutUrl, returnUrl, cancelUrl);

            var tcs = new TaskCompletionSource<BrowserOutcome>();
            Action<string> resultHandler = null;

            resultHandler = payload =>
            {
                M2CScheduler.Instance.AuthTabResult -= resultHandler;

                // payload: "RETURNED|<url>" | "CANCELED|" | "RESUMED|" | "ERROR|<message>"
                int sep = payload.IndexOf('|');
                string kind = sep >= 0 ? payload.Substring(0, sep) : payload;
                string url = sep >= 0 ? payload.Substring(sep + 1) : string.Empty;

                if (kind == "RETURNED" && !string.IsNullOrEmpty(url))
                {
                    tcs.TrySetResult(BrowserOutcome.Returned(url));
                    return;
                }

                if (kind == "ERROR")
                {
                    LaunchCustomTabsFallback(tcs, checkoutUrl, returnUrl, cancelUrl);
                    return;
                }

                if (kind == "CANCELED")
                {
                    // RESULT_CANCELED: the user closed the Auth Tab without completing - a
                    // reliable browser-cancel signal. The core lets already-visible
                    // backend terminal status win before otherwise resolving cancel.
                    tcs.TrySetResult(BrowserOutcome.Canceled);
                    return;
                }

                // RESUMED (verification failed/timed out, or unknown): ambiguous, never a
                // hard cancel. A short status window catches a completion that didn't
                // redirect; otherwise it resolves pending-timeout.
                tcs.TrySetResult(BrowserOutcome.Resumed);
            };

            M2CScheduler.Instance.AuthTabResult += resultHandler;

            if (!TryLaunchAuthTab(checkoutUrl, scheme))
            {
                M2CScheduler.Instance.AuthTabResult -= resultHandler;
                // Helper activity unavailable: hand off to standard Custom Tabs (deep-link
                // + focus return), which itself degrades to the system browser.
                return new AndroidCustomTabsBrowser().LaunchAsync(checkoutUrl, returnUrl, cancelUrl);
            }

            return tcs.Task;
        }

        private static void LaunchCustomTabsFallback(TaskCompletionSource<BrowserOutcome> tcs, string checkoutUrl, string returnUrl, string cancelUrl)
        {
            Task<BrowserOutcome> fallbackTask;
            try
            {
                fallbackTask = new AndroidCustomTabsBrowser().LaunchAsync(checkoutUrl, returnUrl, cancelUrl);
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
                return;
            }

            fallbackTask.ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    tcs.TrySetCanceled();
                    return;
                }
                if (task.IsFaulted)
                {
                    tcs.TrySetException(task.Exception.InnerException ?? task.Exception);
                    return;
                }
                tcs.TrySetResult(task.Result);
            });
        }

        private static bool TryLaunchAuthTab(string checkoutUrl, string scheme)
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
