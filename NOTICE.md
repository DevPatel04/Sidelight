# Notice

## Origin

This project is a fork of **PhoneBox** by **Ersin KOÇ**:

- Upstream repository: <https://github.com/ersinkoc/phonebox>
- Upstream license: MIT License, Copyright (c) 2026 Ersin KOÇ

The original copyright and permission notice are kept, unchanged, in [LICENSE](LICENSE), as the MIT License requires.
Changes made in this fork are Copyright (c) 2026 DevPatel04 and contributors and are released under the same MIT
License.

This fork is maintained independently by [DevPatel04](https://github.com/DevPatel04). It is not affiliated with,
sponsored by or endorsed by Ersin KOÇ or the upstream project. Report problems with this fork at
<https://github.com/DevPatel04/phonebox/issues>, not upstream.

## What this fork changes

- Releases, installers, the update check, the Scoop bucket, winget manifests and the pairing page point at
  `DevPatel04/phonebox` instead of the upstream repository.
- Update channels: **Stable** (releases from `main`) and **Testing** (pre-releases built from the `test` branch).
- Fork-specific features and fixes; see [CHANGELOG.md](CHANGELOG.md) and the git history for details.
- Release APKs are signed with this fork's own key. Windows agent releases from this fork are not code-signed; the
  SignPath Foundation arrangement belongs to the upstream project.

The full history of upstream changes is kept in this repository's git history and in the upstream repository.
Links in older documents (for example `docs/ROADMAP.md`, `docs/PR-FOLLOWUPS.md` and older CHANGELOG entries) refer to
upstream issues and pull requests and point there on purpose.

## Name and icon

The PhoneBox name and icon come from the upstream project. The Android application ID
`net.oxog.phonebox.deskpanel` is inherited from upstream, so this fork's app and the upstream app cannot be installed
side by side on one phone.

## Third-party software

The agent bundles the .NET runtime, ASP.NET Core and other libraries under their own licenses; see
[agent/PhoneBox.Agent/THIRD-PARTY-NOTICES.txt](agent/PhoneBox.Agent/THIRD-PARTY-NOTICES.txt).
