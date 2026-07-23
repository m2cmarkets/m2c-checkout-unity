# Changelog

All notable changes to `com.m2c.checkout` are documented here.

## [0.5.2] - 2026-07-22

- Aligned the Unity, browser, and receiver checkout SDK releases at version 0.5.2.
- Documented the shared HTTPS and loopback-HTTP validation contract.

## [0.5.1] - 2026-07-21

- Aligned the Unity, browser, and receiver checkout SDK releases at version 0.5.1.
- Reject secret API keys during client construction so they cannot be
  accidentally embedded in a shipped Unity application.
- Require HTTPS checkout URLs except for plaintext HTTP on loopback hosts.
- Prune expired or malformed WebGL checkout records and their matching return
  records from local storage.

## [0.5.0] - 2026-07-21

- Added an opt-in, merchant-owned native billing fallback for definitely-not-launched
  Unity checkouts, with typed reasons and per-call disable support.
- Added a bounded, cancellable client-auction deadline (8-30s; 10s recommended)
  that is active only when fallback is enabled.
- Added the terminal `FallbackStarted` result. It means the merchant IAP flow
  accepted responsibility, never payment or entitlement success.
- Preserved the original checkout exception when fallback declines or its handler
  fails; ambiguous handler failures are marked `HandlerOutcomeUnknown` and must
  not be retried automatically.
- Fenced launch with an invocation-local monotonic latch and a distinct WebGL
  prepared-window failure so fallback cannot run after possible vendor exposure.

## [0.4.0] - 2026-07-20

- Version aligned with the unified 0.4.0 release across the M2C SDK family
  (npm `@m2c/*` packages and the OpenAPI spec now share one version). No
  behavior changes in this package.
- Status coercion is now conformance-checked against the shared cross-SDK
  vector table (`sdk/kat`), so the Unity, JS, and server status projections
  cannot drift apart silently.
- Documented the M2C status fallback (`UseM2CStatusFallback` /
  `M2CFallbackAfterSeconds`) in the README, including the recommended 5-15s
  threshold and why the settings asset clamps it.

## [0.3.1] - 2026-07-13

- **Breaking for WebGL return pages:** the old `window.opener.postMessage`
  helper is no longer accepted. Vendors must append the auction `request_id` to
  same-origin success/cancel redirects, and return pages must use the
  request-scoped nonce bridge shown in the README. Existing cross-origin return
  pages continue through authoritative status reconciliation.
- WebGL checkout windows now sever `window.opener` before vendor navigation.
  Browsers with a read-only opener use a secure `noopener`, status-only fallback
  rather than failing checkout. Missing secure randomness, partitioned iframe
  storage, cross-origin returns, and ambiguous closes also reconcile over the
  configured status window.
- Request-scoped storage prevents concurrent same-origin game tabs from
  overwriting each other's return nonce.

## [0.2.0] - 2026-06-22

- Device type and checkout platform are now auto-detected and sent automatically.
  The manual `AuctionRequest.DeviceType` field has been removed (breaking): device
  type (`mobile` / `desktop`) is derived from `SystemInfo.deviceType` at runtime, and
  checkout platform (`webgl` / `ios` / `android`) from the build target. Both are
  metadata only, with no caller override.
- WebGL `Popup` launch mode now pre-opens a blank popup before async auction
  creation and reuses it for the hosted checkout URL, reducing popup blocker
  failures while leaving default tab-style launch free to keep the WebGL tab
  active during auction creation.
- Added a WebGL launch mode hint (`Auto`, `NewTab`, `Popup`) in project settings
  and `M2CConfig`. Browsers still decide the final tab/window presentation.
- WebGL return handling now accepts postMessage, BroadcastChannel, and storage
  notifications so a return page that closes quickly is less likely to race the
  popup-close detector.

## [0.1.2] - 2026-06-21

- Android in-app checkout now uses a Chrome Auth Tab: no minimize button, and the
  return arrives through a real ActivityResult callback instead of being inferred
  from app focus, so minimize, OTP / 3-D Secure bounces, and a backgrounded tab no
  longer trigger a false return. Falls back to Chrome Custom Tabs, then the system
  browser, on browsers without Auth Tab support, and for https Universal/App Link
  returns.
- Bumped the Android dependency to `androidx.browser:browser:1.9.0` and added
  `androidx.activity`; the build post-processor registers a translucent helper
  activity that hosts the Auth Tab result launcher.
- Aligned Android Kotlin stdlib artifacts at build time so Unity or third-party
  SDKs with older `kotlin-stdlib-jdk7/jdk8` dependencies do not trigger duplicate
  Kotlin classes.

## [0.1.1] - 2026-06-21

- Added platform-aware project settings: mobile, WebGL, and optional iOS /
  Android publishable keys, plus dedicated WebGL success/cancel URLs.
- WebGL settings now use a dedicated WebGL publishable key field for
  client-initiated checkout and M2C status polling, avoid sending mobile
  custom-scheme return URLs from browser builds, and document exact-origin
  requirements for web publishable keys.
- Cleaned up the project settings inspector so WebGL, mobile key overrides,
  custom mobile return URLs, and custom status URLs are hidden until needed.
- Standalone desktop player builds now fail as unsupported instead of launching
  checkout through a system-browser fallback.
- WebGL popup closes without a return message now reconcile through brief
  status polling instead of immediately reporting canceled.

## [0.1.0] - 2026-06-21

Initial beta release.

- Platform-agnostic C# core: `M2CCheckoutClient`, the canonical checkout state
  machine, `UnityWebRequest` transport, the bounded exponential-backoff poll
  contract, pluggable status sources (`M2C` / `Url` / `Callback`), typed error
  taxonomy (`M2CCheckoutException`), return classification, and cold-start resume
  with persisted status-source metadata.
- Backend-initiated (`StartFromSessionAsync`) and client-initiated (`StartAsync`)
  flows, plus coroutine overloads.
- Per-target browser strategies: Editor mock, system browser + deep-link return,
  iOS in-app `ASWebAuthenticationSession` shim, Android in-app Chrome Custom Tabs
  (JNI, no Java/Kotlin file), WebGL popup + `postMessage` shim.
- Project settings asset and build post-processors for iOS framework /
  return registration and Android return intent-filter registration.
- Runtime project settings loading via `M2CConfig.FromProjectSettings()` /
  `M2CCheckoutClient.FromProjectSettings()` from an `Assets/Resources` settings asset,
  including optional backend status URL defaults.
- Advanced project settings for browser mode and status-poll timeout.
- EditMode tests for the pure core.

Mobile launch/return paths and the build post-processor are implemented to spec
but pending on-device / in-browser validation. The AndroidX Browser dependency (for
Custom Tabs) is declared in an EDM4U `Dependencies.xml` and also appended by the
Android build post-processor when it is not already present.
