# PhoneBox Local Plugins

PhoneBox supports local declarative cards and sandboxed static HTML widgets installed from a `.zip` archive or local folder in **Settings › Plugins**.

## Locations and permissions

- **Plugins directory**: `%LOCALAPPDATA%\PhoneBox\plugins\<plugin-id>\` on Windows, `~/.local/share/PhoneBox/plugins/<plugin-id>/` on Linux, and `~/Library/Application Support/PhoneBox/plugins/<plugin-id>/` on macOS (`0700` directories on Unix).
- **Plugin state & secrets**: `plugins.json` in the agent configuration folder (`0600` owner-only on Unix).
- **Local-only mode**: Stage 1 supports `backend.kind: "none"` declarative cards and local static HTML files only. Manifests requesting `http`, `exec`, or `bridge` permissions or backends are rejected.

## Security model

1. **Disabled on install & update**: Newly installed plugins always start disabled (`enabled: false`). Updating an installed plugin checks its content SHA-256, version, and permissions; any change disables the plugin, flags `needsReapproval`, summarizes the changes, and requires explicit re-enabling in Settings.
2. **Archive safety**: Local `.zip` installs reject path traversal (`..`), symbolic links, reparse points, archives over 128 entries, and extracted totals over 8 MB.
3. **Secrets stay on the PC**: Settings marked `"secret": true` or `"type": "password"` are stored only in `plugins.json`, masked across Settings saves (`__UNCHANGED__`), never interpolated into widget output, and stripped from `/api/plugins` and `/api/config` (including Smart Stack children).
4. **Sandboxed HTML & CSP**: HTML widgets run inside `<iframe sandbox="allow-scripts" referrerpolicy="no-referrer">` with a strict Content Security Policy (`default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; form-action 'none'; frame-ancestors 'none'`). Template tokens (`{options}`, `{options.<key>}`, `{settings}`, `{settings.<key>}`) are serialized with HTML-safe JSON escaping.
5. **Local management**: `/api/plugins/*` routes stay on `127.0.0.1` via `BrowserGuard` and are blocked over LAN/Wi-Fi. The panel reads widget state via `GET /api/plugin/{id}/state`.

## Manifest format (`plugin.json`)

```json
{
  "id": "desk-card",
  "name": "Desk Card",
  "version": "1.0.0",
  "author": "PhoneBox",
  "description": "Show a configurable status card on the panel.",
  "os": ["windows", "linux", "macos"],
  "permissions": [],
  "settings": [
    { "key": "owner", "label": "Owner", "type": "text", "default": "Desk" },
    { "key": "noteSecret", "label": "Local Secret", "type": "password", "secret": true }
  ],
  "backend": { "kind": "none", "runtime": "none" },
  "widgets": [
    {
      "type": "plugin:desk-card.status",
      "category": "Plugins",
      "name": "Desk Status",
      "icon": "🧩",
      "desc": "Displays a declarative status card.",
      "w": 4, "h": 2, "minW": 2, "minH": 2,
      "ui": {
        "kind": "stat",
        "data": {
          "title": "{options.title}",
          "value": "{options.status}",
          "sub": "Owner: {settings.owner}"
        }
      },
      "options": [
        { "key": "title", "label": "Title", "type": "text", "default": "Focus" },
        { "key": "status", "label": "Status", "type": "text", "default": "Ready" },
        { "key": "control", "label": "Allow phone controls", "type": "bool", "default": true }
      ]
    }
  ]
}
```

- **Widget `type` naming**: Must be `plugin:<id>` or `plugin:<id>.<subType>`.
- **UI kinds**: `auto`, `stat`, `gauge`, `list`, `buttons` (rendered natively with DOM `textContent`), or `html` (`ui.file` path inside the plugin folder).
