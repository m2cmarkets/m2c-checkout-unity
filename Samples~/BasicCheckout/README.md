# Basic Checkout sample

Open **Assets > M2C > Find or Create Checkout Settings**, fill in your publishable key
(for client-initiated mode), return/cancel deep links, and optional backend
status URL, then attach `CheckoutSample` to a GameObject and call:

- `BuyGems()` - client-initiated: the SDK runs the auction with your publishable key.
- `StartFromBackend(checkoutUrl, requestId, ttl)` - backend-initiated (recommended):
  your server ran the auction and forwarded the session.
- `ResumeIfInterrupted()` - call once on startup to resume a checkout that was killed
  mid-flight.

Wire a UI button's `OnClick` to one of these. Watch the Console for the state
stream and the terminal outcome.

The sample reads `M2CConfig.FromProjectSettings()` by default. Disable
`useProjectSettings` on the component if you want to fill config fields directly on
the sample instead. Browser mode and status poll timeout are project defaults;
auction metadata like currency, language, and segments belongs on each request.

In the Editor the mock browser returns a scripted outcome, so the whole flow runs
without a device; set `EditorCheckoutBrowser.NextOutcome` to exercise cancel/dismiss.
