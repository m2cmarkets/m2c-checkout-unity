using System.Threading.Tasks;

namespace M2C.Checkout
{
    /// <summary>Whether the browser yielded a return URL or the customer dismissed it.</summary>
    public enum BrowserResult
    {
        /// <summary>The customer came back to the app with a return URL (deep link or postMessage).</summary>
        Returned,
        /// <summary>The checkout was launched in a surface that cannot report a return; proceed to status polling.</summary>
        Launched,
        /// <summary>The customer dismissed the in-app browser without completing (treated as cancel, immediately).</summary>
        Dismissed,
        /// <summary>
        /// The customer explicitly closed a return-capable surface (iOS
        /// ASWebAuthenticationSession canceledLogin, Android Auth Tab RESULT_CANCELED) -
        /// a reliable browser-cancel signal, unlike a bare <see cref="Resumed"/>. The
        /// core reconciles visible backend terminal state before otherwise resolving
        /// CheckoutCanceled.
        /// </summary>
        Canceled,
        /// <summary>
        /// A browser surface closed without a return URL. The core polls status very
        /// briefly because browser close can race with WebGL postMessage delivery.
        /// </summary>
        Closed,
        /// <summary>
        /// A return-capable surface ended without a return URL, but without a reliable
        /// cancel result. The core polls status briefly for terminal state that did
        /// not redirect, then resolves pending-timeout. It is never a cancel: mobile
        /// 3DS / OTP bounces can arrive the same way.
        /// </summary>
        Resumed
    }

    /// <summary>The raw outcome of launching the vendor checkout. The core classifies the URL.</summary>
    public struct BrowserOutcome
    {
        public BrowserResult Result;
        /// <summary>The return URL, when <see cref="Result"/> is <see cref="BrowserResult.Returned"/>.</summary>
        public string ReturnUrl;

        public static BrowserOutcome Returned(string url) => new BrowserOutcome { Result = BrowserResult.Returned, ReturnUrl = url };
        public static readonly BrowserOutcome Launched = new BrowserOutcome { Result = BrowserResult.Launched };
        public static readonly BrowserOutcome Dismissed = new BrowserOutcome { Result = BrowserResult.Dismissed };
        public static readonly BrowserOutcome Canceled = new BrowserOutcome { Result = BrowserResult.Canceled };
        public static readonly BrowserOutcome Closed = new BrowserOutcome { Result = BrowserResult.Closed };
        public static readonly BrowserOutcome Resumed = new BrowserOutcome { Result = BrowserResult.Resumed };
    }

    /// <summary>
    /// Per-target strategy that launches the vendor's hosted checkout and resolves
    /// when the customer returns. Implementations: an Editor mock, a deep-link
    /// (system or in-app browser) launcher on mobile, and a popup + postMessage
    /// shim on WebGL. The core, not the strategy, decides success vs cancel.
    /// </summary>
    public interface ICheckoutBrowser
    {
        /// <summary>True when this strategy cannot complete without a configured success return URL.</summary>
        bool RequiresReturnUrl { get; }

        /// <summary>
        /// Launch <paramref name="checkoutUrl"/>. <paramref name="returnUrl"/> /
        /// <paramref name="cancelUrl"/> are provided so strategies that need the
        /// callback scheme (iOS ASWebAuthenticationSession) can register it; deep-link
        /// strategies may ignore them and capture the next return.
        /// </summary>
        Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl);
    }

    internal interface ICheckoutBrowserPrelauncher
    {
        /// <summary>Reserve a browser surface before async work can lose the user's activation.</summary>
        void PrepareLaunch();

        /// <summary>Close any reserved browser surface when checkout cannot proceed.</summary>
        void CancelPreparedLaunch();
    }

    internal interface ICheckoutBrowserRuntimeScope
    {
        /// <summary>Apply temporary runtime settings needed while this browser surface is active.</summary>
        System.IDisposable EnterRuntimeScope();
    }
}
