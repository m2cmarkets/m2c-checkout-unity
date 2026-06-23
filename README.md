# M2C Checkout for Unity (`com.m2c.checkout`)

Headless checkout SDK for [M2C](https://m2cmarkets.com). Launch the winning
vendor's hosted checkout, handle returns on iOS, Android, and WebGL, and
reflect conversion status to your UI - from a single C# API, no platform SDK
dependencies.

It holds no secrets beyond an optional publishable key, renders no UI (you draw
every pixel), and never grants goods: **the merchant webhook is the source of
truth**. The SDK's status read is advisory UX.

> **Status: beta.** The platform-agnostic core (state machine, HTTP,
> poll/backoff, status sources, error mapping, return classification) is
> implemented and unit-tested in the Editor. The mobile launch/return paths (the
> iOS `ASWebAuthenticationSession` shim, the WebGL popup `.jslib`, and the build
> post-processors) should still be smoke-tested on your target devices before a
> production rollout.

## Requirements

- Unity 2021.3 LTS or newer.
- No manual dependency setup. The only non-C# files are a tiny WebGL `.jslib`, a
  ~60-line iOS Objective-C shim, and a small Android Auth Tab helper activity,
  all shipped as source. Android in-app tabs require AndroidX Browser / Activity
  in the generated Gradle build; the package adds them automatically during
  Android project generation.

## Package layout

This folder is the Unity package root for the `m2c-checkout-unity` repository:

- `package.json` - UPM manifest for `com.m2c.checkout`.
- `Runtime/` - runtime API and platform browser implementations.
- `Editor/` - settings inspector, menu items, build post-processors, and editor-only assets.
- `Plugins/` - platform shims for iOS and WebGL.
- `Samples~/BasicCheckout/` - importable Package Manager sample.
- `Tests/` - EditMode tests for the pure checkout core.
- `Documentation~/` - Package Manager documentation entry point.

## Install

Via UPM (Package Manager > Add package from git URL):

```
https://github.com/m2cmarkets/m2c-checkout-unity.git
```

or add to `Packages/manifest.json`:

```json
"com.m2c.checkout": "https://github.com/m2cmarkets/m2c-checkout-unity.git"
```

This tracks the latest package on the repository's default branch. For a fully
reproducible production build, append a release tag from GitHub Releases.

## Quick start

Open **Assets > M2C > Find or Create Checkout Settings** and fill in:

- `Mobile Publishable Key` - mobile publishable key (`pub_...` / `pub_test_...`), never a secret key.
- `WebGL Publishable Key` - required for WebGL client-initiated checkout and M2C status polling. Backend-initiated flows with a custom status URL can leave it blank. Use a web/browser publishable key whose allowed origins include the exact WebGL page origin.
- `Success URL` / `Cancel URL` - mobile return URLs, e.g. `mygame://checkout/return` and `mygame://checkout/cancel`.
- `WebGL Success URL` / `WebGL Cancel URL` - `http://` or `https://` pages for WebGL returns.
- `StatusUrlTemplate` - optional backend status URL containing `{request_id}`.
- `DeepLinkScheme` - the scheme part only, e.g. `mygame`, for mobile build registration.

The asset is created under `Assets/Resources`, in your project rather than inside
the package, so your settings survive package updates and can be loaded at runtime.
The inspector also has a collapsed **Advanced Settings** section for less common
defaults: browser mode, status-poll timeout, and optional iOS / Android key
overrides when you want separate mobile publishable keys per platform.

```csharp
using M2C.Checkout;

var client = M2CCheckoutClient.FromProjectSettings();
// Equivalent: var client = new M2CCheckoutClient();

client.OnStateChanged += state => Debug.Log(state); // on the main thread
```

Code can still override anything:

```csharp
var config = M2CConfig.FromProjectSettings();
config.StatusSource = StatusSource.Url("https://shop.example/status/{request_id}");
var client = new M2CCheckoutClient(config);
```

### Backend-initiated (recommended)

Your server runs the auction with its **secret** key and forwards the session.
This is the blessed path - and for games selling virtual currency it matters more
than usual: grant entitlements server-side off the webhook, not off the client poll.
If you do not ship a publishable key, set `StatusSource` to your backend via
the settings asset's status URL, `StatusSource.Url(...)`, or
`StatusSource.Callback(...)`.

```csharp
CheckoutResult result = await client.StartFromSessionAsync(new CheckoutSession {
    CheckoutUrl = url, RequestId = requestId, Ttl = 900,
});
```

### Client-initiated (no-backend shortcut)

The SDK runs the auction itself with a publishable key.

```csharp
CheckoutResult result = await client.StartAsync(new AuctionRequest {
    TransactionValue = 4.99, Currency = "USD", Description = "100 Gems",
    // SuccessUrl / CancelUrl default from the project settings asset.
});
```

The SDK automatically attaches checkout-context metadata to the auction:
`platform` (`webgl` / `ios` / `android`, from the build target) and a coarse
`device_type` (`mobile` / `desktop`, from `SystemInfo.deviceType`). Both are metadata
only - never auth or fulfillment - and there is no caller field for either.

### Coroutine form (for teams avoiding async / on WebGL)

```csharp
yield return client.Start(request, onResult: r => { ... }, onState: s => { ... });
```

### Handling the result

```csharp
switch (result.Outcome) {
    case CheckoutOutcome.Completed:      /* show success; goods granted server-side */ break;
    case CheckoutOutcome.Failed:         /* payment failed */ break;
    case CheckoutOutcome.Canceled:       /* customer canceled */ break;
    case CheckoutOutcome.PendingTimeout: /* "we'll confirm shortly" - webhook decides */ break;
}
```

Errors throw `M2CCheckoutException` with a typed `Code` (`InvalidRequest`,
`OriginNotAllowed`, `AccountSuspended`, `NoVendorsAvailable`, `RateLimited` with
`RetryAfter`, `ServiceUnavailable`, ...). `PendingTimeout` is a result, not an error.

## Status sources

- `StatusSource.M2C` - poll M2C's read endpoint with the publishable key (client-initiated).
- `StatusSource.Url("https://shop.example/status/{request_id}")` - poll your backend (the
  authoritative, webhook-fed source). **Recommended for backend-initiated.**
- `StatusSource.Callback(requestId => ...)` - resolve status however you like.

The project settings asset exposes `StatusUrlTemplate` for the common URL case.
Leave it blank to keep the default M2C status source; fill it with a template such
as `https://shop.example/status/{request_id}` to make `M2CConfig.FromProjectSettings()`
poll your backend instead.

`StatusPollTimeoutSeconds` in Advanced Settings controls how long the SDK polls
before resolving `PendingTimeout`. It defaults to 90 seconds.

## Return setup

Register the return so the vendor's redirect reaches the app:

- **Custom scheme (recommended for games):** create the project settings asset
  (Assets > M2C > Find or Create Checkout Settings) and set `DeepLinkScheme`. The same
  project asset can also hold runtime defaults (mobile and WebGL publishable keys,
  return URLs, `StatusUrlTemplate`, browser mode, poll timeout). The
  post-processors register the scheme in iOS `Info.plist` and in the generated
  Android manifest. This requires no web domain and pairs with
  M2C's mobile publishable keys, which accept a
  custom-scheme `success_url` you register on the key.
- **https Universal/App Links:** enable `UseAssociatedDomains` and host `AASA` /
  `assetlinks.json` on the verified domain. The post-processors add the iOS
  Associated Domains entitlement and the Android App Link intent-filter; domain-file
  hosting is still yours.

Android in-app browser: the SDK prefers Android Auth Tab for custom-scheme
returns and falls back to Chrome Custom Tabs / the system browser when needed.
The Android build post-processor appends `androidx.browser:browser:1.9.0`,
`androidx.activity:activity:1.9.3`, and Kotlin stdlib alignment dependencies to
the generated Gradle project when they are not already present. The package also ships
`Editor/M2CCheckoutDependencies.xml`, so projects that use EDM4U can let EDM4U
manage the same dependencies instead.

If the dependency is removed or the generated Gradle file cannot be updated, the
SDK falls back to the external system browser automatically
(`M2CConfig.UseExternalBrowser = true` forces the external browser).

WebGL uses browser security rules and does not reuse the Mobile Publishable Key.
Client-initiated WebGL checkout and M2C status polling require a web/browser
publishable key; backend-initiated WebGL can leave it blank when `StatusSource`
points at your backend. When you use a web publishable key, add the exact WebGL
game page origin to that key and use `http(s)` success/cancel pages whose origins
either match the game page or are also allowed on the key. The merchant
`success_url` / `cancel_url` page must post a message to its opener so the popup
shim can capture the return:

```js
const message = { m2c: 'return', url: location.href };
if (window.opener && !window.opener.closed) {
  window.opener.postMessage(message, 'https://your-app-origin');
}
try {
  const channel = new BroadcastChannel('m2c_checkout');
  channel.postMessage(message);
  channel.close();
} catch {}
try {
  localStorage.setItem('m2c_checkout_return', JSON.stringify(message));
} catch {}
```

If the checkout surface closes before that return message is received, the SDK
polls status briefly and may return `PendingTimeout`; the webhook-fed backend
remains the authority.

For client-initiated WebGL, call `StartAsync` directly from the click/tap handler
that starts checkout. `Auto` and `NewTab` launch checkout after the auction URL is
ready so the WebGL game tab keeps running while the request is created. `Popup`
mode pre-opens a blank popup before the async auction request, then navigates it
to the hosted checkout URL when the auction returns; this can reduce popup
blocker failures on desktop browsers. `WebGL Launch Mode` is only a browser hint
(`Auto`, `NewTab`, or `Popup`); desktop and mobile browsers may still choose their
own tab, popup window, or tab sheet presentation. The return page must preserve
`window.opener`, so do not use `noopener` on this checkout surface.

For client-initiated local WebGL testing, use a test web publishable key with the
exact loopback origin Unity serves, such as `http://localhost:8000`. `localhost`,
`127.0.0.1`, and different ports are distinct origins.

## Cold-start resume

Call `await client.TryResumeAsync()` once on startup; if a checkout's process was
killed mid-flight, it resumes the status poll from the persisted request id and
status-source reference. Callback status sources must be recreated in `M2CConfig`
before resuming because functions cannot be serialized.

## Targets

- **iOS, Android, WebGL** - full support (mobile paths pending device validation).
- **Editor / Play Mode** - a mock browser returns a scripted outcome so the whole
  flow runs without a device (`EditorCheckoutBrowser.NextOutcome`). Essential for iteration.
- **Standalone desktop** - disabled for now. Checkout launch calls throw an unsupported-platform error in Windows, macOS, and Linux player builds.

## Testing

EditMode tests (`Tests/`) cover the pure core - return classification, status mapping,
the poll schedule, request building, and error mapping - and run via the Unity Test
Runner without a device.

## Notes

- AOT / IL2CPP-safe: no reflection, no runtime codegen; JSON requests are hand-built and
  responses parsed with `JsonUtility` against explicit DTOs.
- `.meta` files are checked in for stable package imports; files under `Plugins/iOS`
  and `Plugins/WebGL` are auto-assigned to their platforms by path.
