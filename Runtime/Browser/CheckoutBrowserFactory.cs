using System;

namespace M2C.Checkout.Internal
{
    /// <summary>
    /// Selects the per-target browser strategy at compile time. The core never sees
    /// the platform; it just gets an <see cref="ICheckoutBrowser"/>.
    /// </summary>
    internal static class CheckoutBrowserFactory
    {
        public static ICheckoutBrowser Create(M2CConfig config, string returnUrl = null)
        {
#if UNITY_EDITOR
            return new EditorCheckoutBrowser();
#elif UNITY_WEBGL
            return new WebGLCheckoutBrowser();
#elif UNITY_IOS
            // ASWebAuthenticationSession captures custom-scheme callbacks. Universal
            // Links return through the system browser / app-link handoff instead.
            if (!config.UseExternalBrowser && !IsWebUrl(returnUrl ?? config.ReturnUrl)) return new IosInAppBrowser();
            return new SystemBrowser(waitForDeepLink: true);
#elif UNITY_ANDROID
            // In-app Chrome Custom Tabs (JNI); it degrades to the system browser if
            // the AndroidX Browser lib is absent. UseExternalBrowser forces the
            // external browser outright.
            if (!config.UseExternalBrowser) return new AndroidCustomTabsBrowser();
            return new SystemBrowser(waitForDeepLink: true);
#elif UNITY_STANDALONE
            throw new M2CCheckoutException(
                M2CErrorCode.InvalidRequest,
                "M2C Checkout does not support standalone desktop player builds yet. Build for iOS, Android, or WebGL, or test in the Unity Editor.");
#else
            throw new M2CCheckoutException(
                M2CErrorCode.InvalidRequest,
                "M2C Checkout does not support this Unity platform yet. Build for iOS, Android, or WebGL, or test in the Unity Editor.");
#endif
        }

        private static bool IsWebUrl(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed)
                   && (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }
    }
}
