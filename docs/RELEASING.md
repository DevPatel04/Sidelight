# Releasing Sidelight

1. Update `agent/Sidelight.Agent/Sidelight.Agent.csproj`, Android `versionName` and its increasing `versionCode`.
2. Add dated release notes to `CHANGELOG.md`; update README and demo catalog counts when widgets/templates change.
3. Run `python tools/check-release.py`, `./gradlew lintDebug assembleDebug` in `android`, the agent tests, `npm test` in `tools/ui-tests`, and
   `pwsh tools/test-install-apk.ps1` on Windows. Build and verify the release-signed APK.
4. Commit the reviewed scope and push:
   - **`main`** — the **release** workflow runs automatically and publishes the stable GitHub release (and Scoop/winget
     follow-up). Use Actions › release › Run workflow with `draft=true` first if you want to review assets.
   - **`test`** — every push publishes a Testing-channel pre-release numbered `<version>.<run>` (for example
     `0.9.22.57`); it does not update Scoop/winget.
5. Or dispatch manually: `.\scripts\release.ps1` (optionally `-Draft`) from a clean `main` that matches `origin/main`.

The release workflow reuses the build checks, then tests and smoke-runs the agent on native Windows x64,
Linux x64/ARM64, and macOS ARM64/Intel runners. It builds the signed APK, signs macOS apphosts ad hoc, creates
provenance attestations and SHA-256 checksums, and publishes the release notes from the changelog.
The reusable artifact contract also checks the real upload/download boundary with transfer fixtures: exact release
filenames and hashes, exclusion of debug intermediates, and the actual demo's Pages tar format. It never publishes
a release or deploys Pages. Changes to Pages deployment still need a successful Pages workflow on `main`.
Published releases update the Scoop bucket and generate winget manifests in a follow-up commit.
Winget manifests are prepared for submission; generation alone does not mean the package is accepted into winget-pkgs.

The agent zips are self-contained single files: each one carries its own .NET runtime, so .NET security fixes
reach users only through a Sidelight release. When .NET ships a fix for Kestrel, TLS or the runtime, publish a patch
release (`setup-dotnet` with `10.0.x` picks up the newest runtime). Single-file bundles are not compressed on
purpose: the zip is barely smaller and a compressed bundle costs about 90 MB more memory in a process that runs all day.

Required GitHub Actions secrets are `SIDELIGHT_KEYSTORE_BASE64`, `SIDELIGHT_STORE_PASSWORD`, `SIDELIGHT_KEY_ALIAS`
and `SIDELIGHT_KEY_PASSWORD`. Keep the original Android signing key: an incompatible APK must fail the update
while preserving the installed app and its data. Never commit the key or its passwords.

`SidelightAgent.exe` in the Windows zip is Authenticode-signed through [SignPath Foundation](https://signpath.org)
while the repository variable `SIGNPATH_SIGNING_POLICY` is `release-signing` (the API token is the
`SIGNPATH_API_TOKEN` secret). The release then waits, up to an hour, for the signing request to be approved in
SignPath; the signature is checked on Windows before the smoke test, and the zip, checksums, attestation and package
manifests all cover the signed file. Without the variable the agent is released unsigned. The **signing test**
workflow runs the same step on its own with the test certificate, without releasing anything.

After publishing, verify the tag points to the intended commit, every asset matches `SHA256SUMS-<version>.txt`,
and `gh attestation verify <asset> --repo DevPatel04/phonebox` succeeds for the APK and agent archives.
Extract downloaded archives and run `python tools/smoke-agent.py <executable> --version <version>` on available
native machines. Separate CI proof, downloaded-binary checks and physical-device tests in the release report.

`scripts/build.ps1` is a local packaging helper. `scripts/release.ps1` dispatches the gated GitHub release workflow.
This release process does not submit packages to external registries automatically.
