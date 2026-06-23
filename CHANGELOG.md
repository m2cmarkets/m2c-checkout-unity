# Changelog

All notable changes to `com.m2c.checkout` are documented here.

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
