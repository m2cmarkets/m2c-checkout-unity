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
        /// <summary>The customer dismissed the in-app browser without completing (treated as cancel).</summary>
        Dismissed,
        /// <summary>
        /// The app returned to the foreground from a callback-less surface (Android
        /// Custom Tab) with no deep-link outcome. The core polls a short window for a
        /// completion that didn't redirect, then resolves pending-timeout. It is never a
        /// cancel: authenticated-payment returns (3DS/OTP) bring the app back the same way.
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
}
