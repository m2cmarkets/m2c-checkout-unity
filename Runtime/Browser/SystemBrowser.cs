using System.Threading.Tasks;
using M2C.Checkout.Internal;
using UnityEngine;

namespace M2C.Checkout
{
    /// <summary>
    /// Launches the checkout in the system browser via <c>Application.OpenURL</c> and
    /// resolves when the vendor redirects back to a registered deep link, surfaced by
    /// <c>Application.deepLinkActivated</c>. Used for Android and the iOS external-
    /// browser fallback.
    ///
    /// Note: a customer who kills the external browser without completing produces no
    /// redirect, so a true "dismissed" is not detectable here. Per the foundations
    /// guidance we do NOT guess cancel from app-lifecycle events; the cancel leg
    /// arrives as the vendor's redirect to the cancel URL or through status
    /// reconciliation.
    /// </summary>
    public sealed class SystemBrowser : ICheckoutBrowser
    {
        private readonly bool _waitForDeepLink;

        public SystemBrowser(bool waitForDeepLink)
        {
            _waitForDeepLink = waitForDeepLink;
        }

        public bool RequiresReturnUrl => _waitForDeepLink;

        public Task<BrowserOutcome> LaunchAsync(string checkoutUrl, string returnUrl, string cancelUrl)
        {
            if (!_waitForDeepLink)
            {
                Application.OpenURL(checkoutUrl);
                return Task.FromResult(BrowserOutcome.Launched);
            }

            Task<BrowserOutcome> outcome = ReturnMonitor.Start(returnUrl, cancelUrl);
            Application.OpenURL(checkoutUrl);
            return outcome;
        }
    }
}
