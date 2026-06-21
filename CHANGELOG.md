# Changelog

All notable changes to `com.m2c.checkout` are documented here.

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
  (JNI, no native file), WebGL popup + `postMessage` shim.
- Project settings asset and build post-processors for iOS framework /
  return registration and Android return intent-filter registration.
- Runtime project settings loading via `M2CConfig.FromProjectSettings()` /
  `M2CCheckoutClient.FromProjectSettings()` from an `Assets/Resources` settings asset,
  including optional backend status URL defaults.
- Advanced project settings for browser mode and status-poll timeout.
- EditMode tests for the pure core.

Native launch/return paths and the build post-processor are implemented to spec
but pending on-device / in-browser validation. The AndroidX Browser dependency (for
Custom Tabs) is declared in an EDM4U `Dependencies.xml` and also appended by the
Android build post-processor when it is not already present.
