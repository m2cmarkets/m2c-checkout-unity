using System;
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

        // Short head start for a deep-link callback that may arrive just after the
        // app returns to the foreground. If it does not arrive, the client performs
        // a short status reconciliation and resolves as completed or pending-timeout.
        private const double ReturnGraceSeconds = 0.5;

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

            var tcs = new TaskCompletionSource<BrowserOutcome>();
            Action<string> deepLinkHandler = null;
            Action<bool> focusHandler = null;
            bool backgrounded = false;

            Action cleanup = () =>
            {
                Application.deepLinkActivated -= deepLinkHandler;
                M2CScheduler.Instance.AppFocusChanged -= focusHandler;
            };

            deepLinkHandler = url =>
            {
                if (!ReturnClassifier.IsConfiguredReturn(url, returnUrl, cancelUrl))
                    return;
                cleanup();
                tcs.TrySetResult(BrowserOutcome.Returned(url));
            };

            focusHandler = foreground =>
            {
                if (!foreground) { backgrounded = true; return; }
                if (!backgrounded) return;
                M2CScheduler.Instance.DelayThen(ReturnGraceSeconds, () =>
                {
                    if (tcs.Task.IsCompleted) return;
                    cleanup();
                    tcs.TrySetResult(BrowserOutcome.Resumed);
                });
            };

            Application.deepLinkActivated += deepLinkHandler;
            M2CScheduler.Instance.AppFocusChanged += focusHandler;
            Application.OpenURL(checkoutUrl);
            return tcs.Task;
        }
    }
}
