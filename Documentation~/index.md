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
pages that post a return message back to the opener.

Standalone desktop player builds are disabled for now; use the Unity Editor,
iOS, Android, or WebGL.

For full setup notes, see the package `README.md`.
