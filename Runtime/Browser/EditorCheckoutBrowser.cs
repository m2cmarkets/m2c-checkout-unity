using System.Threading.Tasks;
using UnityEngine;

namespace M2C.Checkout
{
    /// <summary>
    /// Editor / Play Mode browser. Two modes:
    ///
    /// - Default (mock): doesn't open anything; returns a scripted
    ///   <see cref="NextOutcome"/> so the state machine and unit tests run without a
    ///   device or a real conversion.
    /// - <see cref="OpenRealCheckout"/> = true: actually opens the checkout URL in the
    ///   system browser and resolves by polling. Use this to click through the real
    ///   M2C sandbox checkout page in the Editor and have the real conversion (complete
    ///   / fail / abandon) drive the result.
    /// </summary>
    public sealed class EditorCheckoutBrowser : ICheckoutBrowser
    {
        public enum MockOutcome { Success, Cancel, Dismiss }

        /// <summary>The scripted outcome the mock returns. Defaults to Success.</summary>
        public static MockOutcome NextOutcome = MockOutcome.Success;

        /// <summary>
        /// When true, open the checkout URL in the system browser and poll for the real
        /// status instead of returning a scripted result. Lets you exercise the actual
        /// sandbox checkout page from the Editor.
        /// </summary>
        public static bool OpenRealCheckout = false;

        // The poll path (real checkout) needs no return URL; the mock path returns one.
        public bool RequiresReturnUrl => !OpenRealCheckout;

        public async Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            if (OpenRealCheckout)
            {
                Debug.Log("[M2C] editor browser: opening real checkout in the system browser - " + checkoutUrl
                          + "\nClick complete / fail / abandon on that page; the SDK polls for the result.");
                Application.OpenURL(checkoutUrl);
                return BrowserOutcome.Launched; // resolve via status polling, not a scripted return
            }

            Debug.Log("[M2C] editor mock browser: launch " + checkoutUrl + " (outcome=" + NextOutcome + ")");
            await Task.Yield(); // simulate a frame on the vendor page
            switch (NextOutcome)
            {
                case MockOutcome.Cancel: return BrowserOutcome.Returned(cancelUrl ?? returnUrl);
                case MockOutcome.Dismiss: return BrowserOutcome.Dismissed;
                default: return BrowserOutcome.Returned(returnUrl);
            }
        }
    }
}
