using System;
using UnityEngine;

namespace M2C.Checkout
{
    /// <summary>
    /// Project-level checkout settings. Keep this asset in the game's Assets folder
    /// (the menu creates it under Assets/Resources) so values survive package updates.
    /// The same asset drives native return registration at build time and can provide
    /// runtime defaults for <see cref="M2CConfig"/>.
    /// </summary>
    public sealed class M2CCheckoutSettings : ScriptableObject
    {
        public const string ResourceName = "M2CCheckoutSettings";
        public const string DefaultAssetPath = "Assets/Resources/M2CCheckoutSettings.asset";

        [Tooltip("Publishable key only (pub_... or pub_test_...). This value ships in the client; never put a secret key here.")]
        public string PublishableKey = "";

        [Tooltip("Default success return URL used when M2CConfig.ReturnUrl or AuctionRequest.SuccessUrl is not set.")]
        public string ReturnUrl = "";

        [Tooltip("Default cancel return URL used when M2CConfig.CancelUrl or AuctionRequest.CancelUrl is not set.")]
        public string CancelUrl = "";

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
            var config = new M2CConfig();
            ApplyTo(config);
            return config;
        }

        /// <summary>Apply non-empty settings fields to an existing config, preserving code-supplied status source when the asset has no status URL.</summary>
        public void ApplyTo(M2CConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            if (!string.IsNullOrEmpty(PublishableKey)) config.PublishableKey = PublishableKey.Trim();

            string statusUrl = (StatusUrlTemplate ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(statusUrl)) config.StatusSource = StatusSource.Url(statusUrl);

            config.UseExternalBrowser = BrowserMode == M2CBrowserMode.ExternalBrowser;

            if (IsPositiveFinite(StatusPollTimeoutSeconds))
                config.Poll = new PollSchedule(PollSchedule.Default.RampSeconds, StatusPollTimeoutSeconds);

            string returnUrl = EffectiveReturnUrl;
            if (!string.IsNullOrEmpty(returnUrl)) config.ReturnUrl = returnUrl;

            string cancelUrl = EffectiveCancelUrl;
            if (!string.IsNullOrEmpty(cancelUrl)) config.CancelUrl = cancelUrl;
        }

        public string EffectiveReturnUrl
        {
            get
            {
                string value = TrimOrEmpty(ReturnUrl);
                return !string.IsNullOrEmpty(value) ? value : UrlFromScheme("checkout/return");
            }
        }

        public string EffectiveCancelUrl
        {
            get
            {
                string value = TrimOrEmpty(CancelUrl);
                return !string.IsNullOrEmpty(value) ? value : UrlFromScheme("checkout/cancel");
            }
        }

        public string EffectiveDeepLinkScheme => TrimOrEmpty(DeepLinkScheme);

        public string EffectiveAssociatedDomain => TrimOrEmpty(AssociatedDomain);

        private string UrlFromScheme(string path)
        {
            string scheme = EffectiveDeepLinkScheme;
            return string.IsNullOrEmpty(scheme) ? string.Empty : scheme + "://" + path;
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
