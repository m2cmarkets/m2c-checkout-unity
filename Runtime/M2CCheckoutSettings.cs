using System;
using UnityEngine;

namespace M2C.Checkout
{
    /// <summary>Checkout target used when resolving project settings defaults.</summary>
    public enum M2CCheckoutPlatform
    {
        /// <summary>Editor/default behavior. Uses the mobile publishable key and mobile return URLs.</summary>
        Default,
        /// <summary>iOS player. Uses the iOS key override when set, then the mobile key.</summary>
        Ios,
        /// <summary>Android player. Uses the Android key override when set, then the mobile key.</summary>
        Android,
        /// <summary>WebGL player. Uses http(s) return URLs and the WebGL key when configured.</summary>
        WebGL
    }

    /// <summary>
    /// Project-level checkout settings. Keep this asset in the game's Assets folder
    /// (the menu creates it under Assets/Resources) so values survive package updates.
    /// The same asset drives mobile return registration at build time and can provide
    /// runtime defaults for <see cref="M2CConfig"/>.
    /// </summary>
    public sealed class M2CCheckoutSettings : ScriptableObject
    {
        public const string ResourceName = "M2CCheckoutSettings";
        public const string DefaultAssetPath = "Assets/Resources/M2CCheckoutSettings.asset";

        [Tooltip("Mobile publishable key only (pub_... or pub_test_...). This value ships in the client; never put a secret key here.")]
        public string PublishableKey = "";

        [Tooltip("WebGL publishable key for client-initiated checkout and M2C status polling. Use a web/browser key with the WebGL game's exact allowed origin.")]
        public string WebGLPublishableKey = "";

        [Tooltip("Optional iOS publishable key override. Leave blank to use the mobile publishable key.")]
        public string IosPublishableKey = "";

        [Tooltip("Optional Android publishable key override. Leave blank to use the mobile publishable key.")]
        public string AndroidPublishableKey = "";

        [Tooltip("Default mobile success return URL used when M2CConfig.ReturnUrl or AuctionRequest.SuccessUrl is not set.")]
        public string ReturnUrl = "";

        [Tooltip("Default mobile cancel return URL used when M2CConfig.CancelUrl or AuctionRequest.CancelUrl is not set.")]
        public string CancelUrl = "";

        [Tooltip("Default WebGL success return URL. Must be http:// or https:// and hosted by the WebGL page or another origin allowed on the web key.")]
        public string WebGLReturnUrl = "";

        [Tooltip("Default WebGL cancel return URL. Must be http:// or https:// and hosted by the WebGL page or another origin allowed on the web key.")]
        public string WebGLCancelUrl = "";

        [Tooltip("Optional backend status endpoint template containing {request_id}. Leave blank to poll M2C with the publishable key.")]
        public string StatusUrlTemplate = "";

        [Tooltip("Preferred browser surface. In-app uses platform in-app browser support when available; external always opens the system browser.")]
        public M2CBrowserMode BrowserMode = M2CBrowserMode.InAppPreferred;

        [Tooltip("Total seconds to poll status before resolving PendingTimeout.")]
        public float StatusPollTimeoutSeconds = 90f;

        [Tooltip("Custom URL scheme for the return deep link, WITHOUT the ://. e.g. \"mygame\" for mygame://checkout/return. Recommended for games - no domain verification needed.")]
        public string DeepLinkScheme = "mygame";

        [Tooltip("Also configure https Universal Links (iOS) / App Links (Android). Requires hosting AASA / assetlinks.json on the domain below and verifying it.")]
        public bool UseAssociatedDomains = false;

        [Tooltip("Associated domain host for Universal/App Links, e.g. links.mygame.com")]
        public string AssociatedDomain = "";

        /// <summary>Load the project settings asset from Resources, or null when it has not been created.</summary>
        public static M2CCheckoutSettings Load()
        {
            return Resources.Load<M2CCheckoutSettings>(ResourceName);
        }

        /// <summary>Create an <see cref="M2CConfig"/> from this settings asset.</summary>
        public M2CConfig ToConfig()
        {
            return ToConfig(CurrentPlatform);
        }

        /// <summary>Create an <see cref="M2CConfig"/> for a specific target platform.</summary>
        public M2CConfig ToConfig(M2CCheckoutPlatform platform)
        {
            var config = new M2CConfig();
            ApplyTo(config, platform);
            return config;
        }

        /// <summary>Apply non-empty settings fields to an existing config, preserving code-supplied status source when the asset has no status URL.</summary>
        public void ApplyTo(M2CConfig config)
        {
            ApplyTo(config, CurrentPlatform);
        }

        /// <summary>Apply non-empty settings fields for a specific target platform.</summary>
        public void ApplyTo(M2CConfig config, M2CCheckoutPlatform platform)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            string publishableKey = EffectivePublishableKeyForPlatform(platform);
            if (!string.IsNullOrEmpty(publishableKey)) config.PublishableKey = publishableKey;

            string statusUrl = (StatusUrlTemplate ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(statusUrl)) config.StatusSource = StatusSource.Url(statusUrl);

            config.UseExternalBrowser = BrowserMode == M2CBrowserMode.ExternalBrowser;

            if (IsPositiveFinite(StatusPollTimeoutSeconds))
                config.Poll = new PollSchedule(PollSchedule.Default.RampSeconds, StatusPollTimeoutSeconds);

            string returnUrl = EffectiveReturnUrlForPlatform(platform);
            if (!string.IsNullOrEmpty(returnUrl)) config.ReturnUrl = returnUrl;

            string cancelUrl = EffectiveCancelUrlForPlatform(platform);
            if (!string.IsNullOrEmpty(cancelUrl)) config.CancelUrl = cancelUrl;
        }

        public string EffectivePublishableKey => EffectivePublishableKeyForPlatform(CurrentPlatform);

        public string EffectiveReturnUrl => EffectiveReturnUrlForPlatform(CurrentPlatform);

        public string EffectiveCancelUrl => EffectiveCancelUrlForPlatform(CurrentPlatform);

        public string EffectiveMobileReturnUrl
        {
            get
            {
                string value = TrimOrEmpty(ReturnUrl);
                return !string.IsNullOrEmpty(value) ? value : UrlFromScheme("checkout/return");
            }
        }

        public string EffectiveMobileCancelUrl
        {
            get
            {
                string value = TrimOrEmpty(CancelUrl);
                return !string.IsNullOrEmpty(value) ? value : UrlFromScheme("checkout/cancel");
            }
        }

        public string EffectiveDeepLinkScheme => TrimOrEmpty(DeepLinkScheme);

        public string EffectiveAssociatedDomain => TrimOrEmpty(AssociatedDomain);

        public string EffectivePublishableKeyForPlatform(M2CCheckoutPlatform platform)
        {
            switch (platform)
            {
                case M2CCheckoutPlatform.WebGL:
                    return TrimOrEmpty(WebGLPublishableKey);
                case M2CCheckoutPlatform.Ios:
                    return FirstNonEmpty(IosPublishableKey, PublishableKey);
                case M2CCheckoutPlatform.Android:
                    return FirstNonEmpty(AndroidPublishableKey, PublishableKey);
                default:
                    return TrimOrEmpty(PublishableKey);
            }
        }

        public string EffectiveReturnUrlForPlatform(M2CCheckoutPlatform platform)
        {
            if (platform == M2CCheckoutPlatform.WebGL)
                return EffectiveWebGLUrl(WebGLReturnUrl, ReturnUrl);
            return EffectiveMobileReturnUrl;
        }

        public string EffectiveCancelUrlForPlatform(M2CCheckoutPlatform platform)
        {
            if (platform == M2CCheckoutPlatform.WebGL)
                return EffectiveWebGLUrl(WebGLCancelUrl, CancelUrl);
            return EffectiveMobileCancelUrl;
        }

        public static M2CCheckoutPlatform CurrentPlatform
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return M2CCheckoutPlatform.WebGL;
#elif UNITY_IOS && !UNITY_EDITOR
                return M2CCheckoutPlatform.Ios;
#elif UNITY_ANDROID && !UNITY_EDITOR
                return M2CCheckoutPlatform.Android;
#else
                return M2CCheckoutPlatform.Default;
#endif
            }
        }

        private string UrlFromScheme(string path)
        {
            string scheme = EffectiveDeepLinkScheme;
            return string.IsNullOrEmpty(scheme) ? string.Empty : scheme + "://" + path;
        }

        private static string EffectiveWebGLUrl(string webGLUrl, string legacyUrl)
        {
            string value = TrimOrEmpty(webGLUrl);
            if (!string.IsNullOrEmpty(value)) return value;

            // Compatibility for projects that used the original ReturnUrl fields
            // for WebGL before dedicated WebGL fields existed. Do not carry mobile
            // app schemes into browser builds.
            value = TrimOrEmpty(legacyUrl);
            return IsHttpUrl(value) ? value : string.Empty;
        }

        private static string FirstNonEmpty(string first, string fallback)
        {
            string value = TrimOrEmpty(first);
            return !string.IsNullOrEmpty(value) ? value : TrimOrEmpty(fallback);
        }

        public static bool IsHttpUrl(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host))
                return false;
            return string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimOrEmpty(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static bool IsPositiveFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }
    }
}
