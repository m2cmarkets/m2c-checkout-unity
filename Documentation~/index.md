# M2C Checkout for Unity

M2C Checkout is a headless Unity SDK for launching hosted checkout, handling
returns, and reflecting conversion status in your game UI.

Install from GitHub with Unity Package Manager:

```text
https://github.com/m2cmarkets/m2c-checkout-unity.git
```

Or add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.m2c.checkout": "https://github.com/m2cmarkets/m2c-checkout-unity.git"
  }
}
```

The untagged Git URL tracks the latest package on the repository's default
branch. Pin a GitHub release tag only when you need reproducible installs.

After installing, open **Assets > M2C > Find or Create Checkout Settings** and
configure your mobile publishable key, WebGL publishable key when needed, mobile
and WebGL return URLs, optional backend status URL, and mobile return setup.

For WebGL client-initiated checkout or M2C status polling, set WebGL Publishable
Key to a web/browser publishable key and add the exact page origin serving the
game to that key. Backend-initiated WebGL with a custom status URL can leave the
WebGL key blank. WebGL success and cancel URLs must be `http://` or `https://`
pages that post a return message back to the opener. Same-origin return pages
should also notify `BroadcastChannel('m2c_checkout')` or write
`localStorage.m2c_checkout_return` with the same message for extra browser
resilience.

If the checkout surface closes before that return message is received, the SDK
polls status briefly and may return `PendingTimeout`; the webhook-fed backend
remains the authority.

For client-initiated WebGL, call checkout directly from the click/tap handler.
`Auto` and `NewTab` launch after the auction URL is ready so the WebGL tab keeps
running. `Popup` mode pre-opens a blank popup before async auction creation, then
navigates it to the hosted checkout URL. `WebGL Launch Mode` is a browser hint;
the browser may still choose a tab, popup window, or mobile tab sheet.

Standalone desktop player builds are disabled for now; use the Unity Editor,
iOS, Android, or WebGL.

For full setup notes, see the package `README.md`.
