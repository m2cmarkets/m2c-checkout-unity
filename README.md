# M2C Checkout for Unity (`com.m2c.checkout`)

Headless checkout SDK for [M2C](https://m2cmarkets.com). Launch the winning
vendor's hosted checkout, handle returns on iOS, Android, and WebGL, and
reflect conversion status to your UI - from a single C# API, no native SDK
dependencies.

It holds no secrets beyond an optional publishable key, renders no UI (you draw
every pixel), and never grants goods: **the merchant webhook is the source of
truth**. The SDK's status read is advisory UX.

> **Status: beta (0.1.0).** The platform-agnostic core (state machine, HTTP,
> poll/backoff, status sources, error mapping, return classification) is
> implemented and unit-tested in the Editor. The native launch/return paths (the
> iOS `ASWebAuthenticationSession` shim, the WebGL popup `.jslib`, and the build
> post-processors) should still be smoke-tested on your target devices before a
> production rollout.

## Requirements

- Unity 2021.3 LTS or newer.
- No manual dependency setup. The only non-C# files are a tiny WebGL `.jslib` and a
  ~60-line iOS Objective-C shim, both shipped as source; Android uses no native file.
  Android Custom Tabs require AndroidX Browser in the generated Gradle build; the
  package adds it automatically during Android project generation.

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
https://github.com/m2cmarkets/m2c-checkout-unity.git#v0.1.0
```

or add to `Packages/manifest.json`:

```json
"com.m2c.checkout": "https://github.com/m2cmarkets/m2c-checkout-unity.git#v0.1.0"
```

## Quick start

Open **Assets > M2C > Find or Create Checkout Settings** and fill in:

- `PublishableKey` - publishable only (`pub_...` / `pub_test_...`), never a secret key.
- `ReturnUrl` / `CancelUrl` - e.g. `mygame://checkout/return` and `mygame://checkout/cancel`.
- `StatusUrlTemplate` - optional backend status URL containing `{request_id}`.
- `DeepLinkScheme` - the scheme part only, e.g. `mygame`, for native build registration.

The asset is created under `Assets/Resources`, in your project rather than inside
the package, so your settings survive package updates and can be loaded at runtime.
The inspector also has a collapsed **Advanced Settings** section for less common
defaults: browser mode and status-poll timeout.

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
  project asset can also hold runtime defaults (`PublishableKey`, `ReturnUrl`,
  `CancelUrl`, `StatusUrlTemplate`, browser mode, poll timeout). The
  post-processors register the scheme in iOS `Info.plist` and in the generated
  Android manifest. This requires no web domain and pairs with
  M2C's mobile publishable keys, which accept a
  custom-scheme `success_url` you register on the key.
- **https Universal/App Links:** enable `UseAssociatedDomains` and host `AASA` /
  `assetlinks.json` on the verified domain. The post-processors add the iOS
  Associated Domains entitlement and the Android App Link intent-filter; domain-file
  hosting is still yours.

Android in-app browser (Chrome Custom Tabs): the SDK launches the checkout in an
in-app Custom Tab via JNI, which needs the AndroidX Browser library. The Android
build post-processor appends `androidx.browser:browser:1.8.0` to the generated
`unityLibrary/build.gradle` when it is not already present. The package also ships
`Editor/M2CCheckoutDependencies.xml`, so projects that use EDM4U can let EDM4U
manage the same dependency instead.

If the dependency is removed or the generated Gradle file cannot be updated, the
SDK falls back to the external system browser automatically
(`M2CConfig.UseExternalBrowser = true` forces the external browser).

WebGL: the merchant `success_url` / `cancel_url` page must post a message to its
opener so the popup shim can capture the return:

```js
window.opener && window.opener.postMessage({ m2c: 'return', url: location.href }, 'https://your-app-origin');
```

## Cold-start resume

Call `await client.TryResumeAsync()` once on startup; if a checkout's process was
killed mid-flight, it resumes the status poll from the persisted request id and
status-source reference. Callback status sources must be recreated in `M2CConfig`
before resuming because functions cannot be serialized.

## Targets

- **iOS, Android, WebGL** - full support (native paths pending device validation).
- **Editor / Play Mode** - a mock browser returns a scripted outcome so the whole
  flow runs without a device (`EditorCheckoutBrowser.NextOutcome`). Essential for iteration.
- **Standalone desktop** - works via the system browser + poll; documented as a fallback.

## Testing

EditMode tests (`Tests/`) cover the pure core - return classification, status mapping,
the poll schedule, request building, and error mapping - and run via the Unity Test
Runner without a device.

## Notes

- AOT / IL2CPP-safe: no reflection, no runtime codegen; JSON requests are hand-built and
  responses parsed with `JsonUtility` against explicit DTOs.
- `.meta` files are checked in for stable package imports; files under `Plugins/iOS`
  and `Plugins/WebGL` are auto-assigned to their platforms by path.
