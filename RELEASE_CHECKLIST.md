# Unity release checklist

Repository version: 0.8.1

Public repository: `https://github.com/m2cmarkets/m2c-checkout-unity`

Before tagging a release:

- Keep `package.json`, `CHANGELOG.md`, documentation install URLs, and the bare
  semantic-version tag aligned.
- Confirm the package root contains no generated Unity folders, build output,
  credentials, production keys, or merchant-specific endpoints.
- Run the package EditMode tests. Release maintainers must also run the private
  Android, iOS, and WebGL harnesses; these platform checks are manual until the
  public repository has Unity CI.
- Install the package into a clean supported Unity project using the exact tag.
- Exercise custom-scheme return, cancellation, recovery, timeout, and both
  backend and `pub_test_` starts on representative devices.
- Exercise `InAppPreferred` and `InAppPersistent` on representative iOS and
  Android devices. For each mode, cover success, cancel, Done/close, app switches
  for authentication or banking, and a second checkout to the same vendor.
- Record observed vendor sign-in, AutoFill, and wallet behavior separately from
  browser-state persistence. Verify the product copy does not promise any of
  them, and review retained website data in the integration's privacy disclosure.
- Inspect the package contents and verify the sample uses sandbox data only.
- Enable GitHub secret scanning, private vulnerability reporting, branch
  protection, and required CI checks before accepting the release tag.

No release is complete until the signed merchant webhook path has also been
exercised. `FallbackStarted` is not a purchase result.
