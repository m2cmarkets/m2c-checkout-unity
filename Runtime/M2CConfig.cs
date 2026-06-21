using System;
using System.Threading.Tasks;

namespace M2C.Checkout
{
    /// <summary>Which source the SDK reads conversion status from after the return.</summary>
    public enum StatusSourceKind
    {
        /// <summary>Poll M2C's read endpoint with the publishable key (client-initiated only).</summary>
        M2C,
        /// <summary>Poll a merchant URL template, substituting <c>{request_id}</c>.</summary>
        Url,
        /// <summary>Resolve status via a merchant-supplied callback.</summary>
        Callback,
        /// <summary>Push adapter (SSE/websocket). Reserved; not implemented in v1.</summary>
        Subscribe
    }

    /// <summary>
    /// A pluggable conversion-status source. Backend-initiated integrations should
    /// point at their own backend (the authoritative, webhook-fed source) via
    /// <see cref="Url"/> or <see cref="Callback"/>; <see cref="M2C"/> is the
    /// no-backend shortcut for publishable client-initiated mode.
    /// </summary>
    public sealed class StatusSource
    {
        public StatusSourceKind Kind { get; }

        /// <summary>For <see cref="StatusSourceKind.Url"/>: a template containing <c>{request_id}</c>.</summary>
        public string UrlTemplate { get; }

        /// <summary>For <see cref="StatusSourceKind.Callback"/>: resolves status given a request id.</summary>
        public Func<string, Task<ClientStatus>> CheckStatus { get; }

        private StatusSource(StatusSourceKind kind, string template, Func<string, Task<ClientStatus>> check)
        {
            Kind = kind;
            UrlTemplate = template;
            CheckStatus = check;
        }

        /// <summary>Poll M2C's read endpoint using the publishable key (client-initiated).</summary>
        public static readonly StatusSource M2C = new StatusSource(StatusSourceKind.M2C, null, null);

        /// <summary>Poll a merchant endpoint; <paramref name="template"/> must contain <c>{request_id}</c>.</summary>
        public static StatusSource Url(string template)
        {
            if (string.IsNullOrEmpty(template) || !template.Contains("{request_id}"))
                throw new ArgumentException("status url template must contain {request_id}", nameof(template));
            return new StatusSource(StatusSourceKind.Url, template, null);
        }

        /// <summary>Resolve status however you like (e.g. your own SDK call).</summary>
        public static StatusSource Callback(Func<string, Task<ClientStatus>> check)
        {
            if (check == null) throw new ArgumentNullException(nameof(check));
            return new StatusSource(StatusSourceKind.Callback, null, check);
        }
    }

    /// <summary>
    /// Bounded exponential-backoff poll schedule, shared by every platform so they
    /// can't diverge. Default: immediate, then 1s, 2s, 4s, 8s, capped at ~8s
    /// intervals, over a ~90s window. On timeout the result is
    /// <see cref="CheckoutPendingTimeout"/>, not an error.
    /// </summary>
    public sealed class PollSchedule
    {
        private readonly double[] _rampSeconds;

        /// <summary>Ramp of delays (seconds) before successive polls. The last value repeats until the window closes.</summary>
        public double[] RampSeconds => (double[])_rampSeconds.Clone();

        /// <summary>Total budget (seconds) before resolving pending-timeout.</summary>
        public double TotalWindowSeconds { get; }

        public PollSchedule(double[] rampSeconds, double totalWindowSeconds)
        {
            if (rampSeconds == null || rampSeconds.Length == 0)
                throw new ArgumentException("ramp must be non-empty", nameof(rampSeconds));
            if (!IsFinite(totalWindowSeconds) || totalWindowSeconds <= 0)
                throw new ArgumentException("total window must be finite and greater than 0", nameof(totalWindowSeconds));

            _rampSeconds = new double[rampSeconds.Length];
            for (int i = 0; i < rampSeconds.Length; i++)
            {
                double delay = rampSeconds[i];
                if (!IsFinite(delay) || delay < 0)
                    throw new ArgumentException("ramp delays must be finite and non-negative", nameof(rampSeconds));
                _rampSeconds[i] = delay;
            }
            if (_rampSeconds[_rampSeconds.Length - 1] <= 0)
                throw new ArgumentException("the final ramp delay must be greater than 0", nameof(rampSeconds));

            TotalWindowSeconds = totalWindowSeconds;
        }

        public static PollSchedule Default => new PollSchedule(new double[] { 0, 1, 2, 4, 8 }, 90);

        /// <summary>The delay before the poll attempt at <paramref name="attemptIndex"/> (0-based).</summary>
        public double DelayForAttempt(int attemptIndex)
        {
            if (attemptIndex < 0) attemptIndex = 0;
            if (attemptIndex < _rampSeconds.Length) return _rampSeconds[attemptIndex];
            return _rampSeconds[_rampSeconds.Length - 1];
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>Preferred checkout browser surface for supported platforms.</summary>
    public enum M2CBrowserMode
    {
        /// <summary>Use the in-app browser when available, with system-browser fallback.</summary>
        InAppPreferred,
        /// <summary>Always open checkout in the external system browser.</summary>
        ExternalBrowser
    }

    /// <summary>
    /// Client configuration. <see cref="StatusSource"/> always applies;
    /// <see cref="PublishableKey"/> is only needed for client-initiated mode and
    /// the <see cref="StatusSource.M2C"/> source.
    /// </summary>
    public sealed class M2CConfig
    {
        /// <summary>
        /// Build config from the project settings asset opened or created by
        /// Assets > M2C > Find or Create Checkout Settings. Returns default config when the
        /// asset is absent.
        /// </summary>
        public static M2CConfig FromProjectSettings()
        {
            var settings = M2CCheckoutSettings.Load();
            return settings != null ? settings.ToConfig() : new M2CConfig();
        }

        /// <summary>
        /// Build config from the project settings asset for a specific platform.
        /// Useful for tooling and tests that need to inspect WebGL/mobile defaults
        /// from inside the Editor.
        /// </summary>
        public static M2CConfig FromProjectSettings(M2CCheckoutPlatform platform)
        {
            var settings = M2CCheckoutSettings.Load();
            return settings != null ? settings.ToConfig(platform) : new M2CConfig();
        }

        /// <summary>Publishable key (<c>pub_</c>/<c>pub_test_</c>). Client-initiated mode only.</summary>
        public string PublishableKey;

        /// <summary>Where conversion status is read from. Defaults to <see cref="StatusSource.M2C"/>.</summary>
        public StatusSource StatusSource = StatusSource.M2C;

        /// <summary>The success return URL. Mobile builds use a custom scheme or https universal/app link; WebGL uses an http(s) return page.</summary>
        public string ReturnUrl;

        /// <summary>The cancel return URL. May be a distinct mobile deep link or WebGL return page.</summary>
        public string CancelUrl;

        /// <summary>Poll schedule after the return. Defaults to <see cref="PollSchedule.Default"/>.</summary>
        public PollSchedule Poll = PollSchedule.Default;

        /// <summary>
        /// Force the external system browser (<c>Application.OpenURL</c>) instead of
        /// the in-app browser. The in-app browser is the default UX on mobile.
        /// </summary>
        public bool UseExternalBrowser = false;
    }
}
