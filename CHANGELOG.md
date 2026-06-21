# Changelog

All notable changes to `com.m2c.checkout` are documented here.

## Unreleased

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
- WebGL popup closes without a return message now reconcile through status
  polling instead of immediately reporting canceled.

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
