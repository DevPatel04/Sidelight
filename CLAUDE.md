# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

PhoneBox turns an old Android phone into a desk panel for a PC. Two halves: a **.NET 10 desktop agent**
(`agent/PhoneBox.Agent`, Windows/Linux/macOS) and an **Android app** (`android/`, Kotlin shell + WebView running a
plain-JS panel in `android/app/src/main/assets/www`). CONTRIBUTING.md and `.coderabbit.yaml` hold the review rules;
read them before changing anything security- or privacy-relevant.

## Commands

Run from the repo root.

```sh
# Agent unit tests (xUnit, against the cross-platform net10.0 build)
dotnet test agent/PhoneBox.Agent.Tests
dotnet test agent/PhoneBox.Agent.Tests --filter "FullyQualifiedName~ConfigTests"          # one class
dotnet test agent/PhoneBox.Agent.Tests --filter "FullyQualifiedName~ConfigTests.SomeTest" # one test

# Run the agent (Linux/macOS: only net10.0 exists; Windows: pick net10.0-windows10.0.19041.0 for tray/WinRT/Core Audio)
dotnet run --project agent/PhoneBox.Agent -f net10.0
# Isolated run beside an installed agent (set deviceSerial and turn off auto-updates in that config)
dotnet run --project agent/PhoneBox.Agent -f net10.0 -- --config /tmp/pb/config.json --port 18765

# Release metadata / catalog consistency (version sync, widget+template counts in README and demo)
python3 tools/check-release.py

# Local demo of panel + settings with simulated data (no agent needed)
python3 tools/demo-server.py   # http://127.0.0.1:8790/panel/ and /settings/ ; size the window 780x360

# Browser UI tests (Playwright; builds the web demo via tools/pages/build.py and serves it on :8781)
cd tools/ui-tests && npm ci && npx playwright install chromium && npx playwright test
npx playwright test controls.spec.js          # one spec file
npx playwright test -g "test name substring"  # one test

# Android (JDK 17, SDK Platform 35; minSdk 24)
cd android && ./gradlew lintDebug assembleDebug
```

CI (`.github/workflows/build.yml`) runs all of the above plus installer regression scripts in `tools/test-*.py|ps1`
and a smoke test of the published single-file agent (`/api/state`, `/api/procs`).

## Architecture

### Data path
- The WebView never talks to the PC directly. The panel fetches `/agent/api/...`; `MainActivity.shouldInterceptRequest`
  catches those and `AgentLink.kt` forwards them over **USB** (`adb reverse` → agent on `127.0.0.1:8765`) or **Wi-Fi /
  Tailscale** (HTTPS on :8766, pinned self-signed cert + paired bearer token). Live events come over one SSE stream
  (`/api/stream`, `AgentStream.kt`), with polling as fallback.
- In a desktop browser (editor live preview at `/panel/`) the agent rewrites `/agent/api/*` → `/api/*` itself, so the
  panel code uses the same paths everywhere.
- The native bridge (`window.Android`) is injected into every frame, so each call carries a key only the panel's own
  frames receive; JS must go through the `bridge` wrapper exported from `main.js`.

### Agent (`agent/PhoneBox.Agent`)
- `Program.cs` is the whole HTTP surface (minimal-API `MapGet/MapPost`) plus the security middleware: Host/Origin/
  `Sec-Fetch-*` checks on the local port (DNS-rebinding/CSRF), and on the LAN port only paths on the allow-list in
  `Lan.AllowedOverLan` with the paired key. **Adding a path to that allow-list is a security decision.**
- Background pollers are singletons registered via `AddMonitor<T>` (singleton + hosted service): `SystemMonitor`,
  `MediaMonitor`, `QuotaMonitor`, `SessionMonitor`, `GitHubMonitor`, `RuleEngine`, etc. `ControlDeck` (split across
  `ControlDeck.*.cs` partials) is the Control Deck: audio, media, radio/HLS, OBS, tasks, MIDI, leases.
- Platform split is by target framework, not `#if` alone: the `-windows` TFM compiles `Tray.cs`, `JobObject.cs`,
  `AudioControl.cs`, `MediaMonitor.cs`; the plain `net10.0` TFM excludes those and compiles `Unix/` instead (same class
  names). Tests reference only the `net10.0` build (`InternalsVisibleTo` is set).
- `Config.cs`: `AgentConfig` + `ConfigStore` (versioned writes via `X-Config-Version`). `ToPublic()` is what the phone
  receives — secrets (tokens, keys, calendar URLs, commands) must stay out of it, including Smart Stack children.
  Features that control the PC are gated by per-widget options checked through `Widgets(enabledPagesOnly: true)`
  (e.g. `AllowKill()`), so removing/hiding the widget disables the capability.
- Embedded resources (served from the binary): `settings.html` (the whole settings page + layout editor),
  `widgets.json` (widget catalogue driving the editor), `templates.json` (page templates on a 12×6 grid),
  `config.default{,.linux,.macos}.json`, and the entire panel `www/` folder (served at `/panel/` for live preview).
- Files holding keys are written owner-only (`AppPaths.WriteAllTextPrivate` / `CreatePrivate`). Processes use
  `ProcessStartInfo.ArgumentList`, never a shell or request text, with timeout + kill on cancel. The Claude OAuth
  token is read-only — never refresh it.

### Panel (`android/app/src/main/assets/www`)
- Plain ES modules, no build step. `main.js` = core (config, pages, navigation, polling, helpers like `h()`, `api()`,
  `t()`); `registry.js` holds the `W` registry; widgets live in `widgets/*.js` as
  `W[type] = { needs: [dataKinds], create(box, ctx) → { update(kind), destroy() } }`. Styles in `style.css`,
  `controls.css`, `appearance.css`. `i18n.js` is a shared string table used by both the panel and `settings.html`
  (11 languages).
- Build DOM with `textContent` / `h()`; no `innerHTML` with agent/network/option data. Widgets must render an empty
  state without the native bridge (web demo and UI tests run without it). Panel requires Chrome/WebView 105+.

### Web demo
`tools/pages/build.py` assembles the GitHub Pages demo from the real panel and settings page, injecting
`tools/pages/demo.js` (a mock agent API with made-up data). The Playwright tests and `tools/demo-server.py` both run
against this, so agent-backed widgets need a mock in `demo.js`, and `demo.js`'s `PAGES` must list every template id.

## Change checklists (from CONTRIBUTING.md)

- **New setting:** `Config.cs` (default + normalization of bad values), the same key in all three
  `config.default*.json`, the settings page; decide whether it belongs in `ToPublic()`.
- **New widget:** entry in `widgets.json`, implementation in `www/widgets/`, an icon in `WIDGET_ICONS` (`main.js`), a mock in
  `tools/pages/demo.js` if it calls the agent, and update the widget count in README (`**N widgets**`) — enforced by
  `check-release.py`. Same for template counts.
- **New outbound connection:** add a row to `PRIVACY.md` (host, what is sent, how to disable). Features that start
  programs, record or send data are off by default.
- Version lives in both `PhoneBox.Agent.csproj` `<Version>` and `android/app/build.gradle.kts` `versionName`, and
  `CHANGELOG.md` needs a dated `## X.Y.Z — YYYY-MM-DD` heading; leave version bumps and the changelog to the maintainer.
- Tests must not depend on the host machine (real ports, clocks, user settings, install dir); use `TestPorts` for
  HTTP fixtures. Code, comments, UI text and commits in English; comments explain why.
