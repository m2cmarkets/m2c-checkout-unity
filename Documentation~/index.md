# M2C Checkout for Unity

M2C Checkout is a headless Unity SDK for launching hosted checkout, handling
returns, and reflecting conversion status in your game UI.

Install from GitHub with Unity Package Manager:

```text
https://github.com/m2cmarkets/m2c-checkout-unity.git#v0.1.0
```

Or add the package to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.m2c.checkout": "https://github.com/m2cmarkets/m2c-checkout-unity.git#v0.1.0"
  }
}
```

After installing, open **Assets > M2C > Find or Create Checkout Settings** and
configure your publishable key, return URLs, optional backend status URL, and
native return setup.

For full setup notes, see the package `README.md`.
