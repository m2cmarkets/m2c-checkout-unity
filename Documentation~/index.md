# M2C Checkout for Unity

M2C Checkout is a headless Unity SDK for launching hosted checkout, handling
returns, and reflecting conversion status in your game UI.

Install from GitHub with Unity Package Manager:

```text
https://github.com/m2cmarkets/m2c-checkout-unity.git#0.8.1
```

Or add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.m2c.checkout": "https://github.com/m2cmarkets/m2c-checkout-unity.git#0.8.1"
  }
}
```

Pin a released tag for reproducible installs. Use an untagged default-branch
URL only when intentionally testing unreleased development code.

After installing, open **Assets > M2C > Find or Create Checkout Settings** and
configure your mobile publishable key, WebGL publishable key when needed, mobile
and WebGL return URLs, optional backend status URL, mobile return setup, and the
mobile browser mode.

`InAppPreferred` is the privacy-preferred default. `InAppPersistent` allows the
system in-app browser to reuse browser-managed vendor state when the OS permits,
and `ExternalBrowser` opens the default browser. Persistence does not guarantee
cookies, remembered identity, AutoFill, or wallet support. The older
`UseExternalBrowser = true` configuration remains a force-external override.
WebGL presentation is controlled separately by `WebGLLaunchMode`.

Backend `CheckoutSession.Ttl` is nullable. Map backend JSON explicitly:
omitted means unknown, positive values are accepted, and non-positive values are
expired. `StatusSource.Url` requires absolute HTTPS plus `{request_id}`; HTTP is
accepted only for exact loopback hosts.

For WebGL client-initiated checkout or M2C status polling, set WebGL Publishable
Key to a web/browser publishable key and add the exact page origin serving the
game to that key. Backend-initiated WebGL with a custom status URL can leave the
WebGL key blank. WebGL success and cancel URLs must be `http://` or `https://`
pages. The SDK severs `window.opener` before navigating to checkout. A
same-origin return page can wake the game through the request-scoped, nonce-bound
`BroadcastChannel('m2c_checkout')` / localStorage bridge documented in the full
README. The vendor must include the auction `request_id` in its final redirect.
Cross-origin and iframe-embedded flows immediately use status reconciliation.

If the checkout surface closes before a valid return signal is received, the
SDK uses the full configured status-poll window and may return `PendingTimeout`;
the webhook-fed backend remains the authority.

For client-initiated WebGL, call checkout directly from the click/tap handler.
`Auto` and `NewTab` launch after the auction URL is ready so the WebGL tab keeps
running. `Popup` mode pre-opens a blank popup before async auction creation, then
navigates it to the hosted checkout URL. `WebGL Launch Mode` is a browser hint;
the browser may still choose a tab, popup window, or mobile tab sheet.

Client-initiated integrations may opt into the merchant-owned native billing
fallback documented in the full README. It runs only before vendor checkout
exposure and never treats fallback acceptance as payment success.

Standalone desktop player builds are disabled for now; use the Unity Editor,
iOS, Android, or WebGL.

For full setup notes, see the package `README.md`.
