# Desktop Widgets

[Русский](README.ru.md) · [Project page](https://nikolaykazakovvs-ux.github.io/desktop-widgets/)

Two compact Windows desktop widgets: **AI Limits** keeps subscription usage windows in view; **System Resources** shows hardware load and network activity. Native C# / Windows Presentation Foundation (WPF), with tray menus, configurable hotkeys, light/dark themes and movable cards. The application interface is currently Russian.

## Platforms

Desktop Widgets is the umbrella project. **This distribution is the Windows section**, under `windows/`.

| Platform | Availability |
| --- | --- |
| Windows | Alpha source package in this repository |
| macOS | Planned separately; no implementation or download available |
| Linux | Planned separately; no implementation or download available |

**Alpha, source distribution.** No signed installer or release download is provided by this repository. Provider integrations depend on upstream applications and may change. A successful local compilation does not establish compatibility across Windows versions or hardware.

## AI Limits

Separate cards for Claude Code, Kimi Code, Antigravity and GPT / Codex. Shows available usage windows, reset times, credits or extra usage when returned by a source. Failed refreshes can retain a previous snapshot marked stale; missing data is not interpreted as zero usage. Card order, scale, opacity, pinning and compact view are configurable.

All providers are **disabled by default**, including when the configuration is absent. Enable only the providers you intend to query. Refreshes are automatic once enabled (default 300 seconds; minimum 60). This is a local application, not an official provider product.

| Provider | Implemented source and access |
| --- | --- |
| Claude Code | Runs the locally installed `claude` CLI: a streaming `get_usage` control request, with `/usage` fallback. If CLI authentication is missing, may read the Claude desktop application's local `plan-usage-history.json` usage cache. A user-triggered login action can launch CLI authentication. |
| GPT / Codex | Runs `codex app-server` and requests `account/rateLimits/read` over standard input/output. Authentication is handled by the installed CLI. |
| Antigravity | Runs `agy -p "/usage" --output-format json` and, for credits, `/credits`. Authentication is handled by the installed CLI. These integrations require a compatible CLI; compatibility is not guaranteed for every version. |
| Kimi Code | Discovers a running local Kimi Code server from its instance metadata and reads its `server.token` to authenticate a loopback usage request. **This local server token is read even when `allowLocalTokens` is false.** With `allowLocalTokens: true`, a fallback reads an OAuth access token from Kimi Code's local credential files and sends it to the configured Kimi usage endpoint. No automatic token refresh is performed by the widget. |

`allowLocalTokens` controls only the **Kimi direct credential fallback**, not all authentication or credential access. Review the provider sources before enabling them. Do not point the Kimi `baseUrl` at an untrusted endpoint: the fallback sends the bearer token there. The supplied configuration disables that fallback.

## System Resources

CPU load, per-thread activity, RAM, GPU, disks, network speeds and leading processes. Includes a local network journal with traffic, peaks and interruptions. GPU readings use NVIDIA's driver interface where available and Windows Display Driver Model (WDDM) statistics otherwise. CPU temperature can be read from compatible, separately installed monitoring software (HWiNFO, AIDA64, Core Temp, LibreHardwareMonitor or OpenHardwareMonitor); availability depends on hardware, permissions and the external application.

No monitoring driver, sensor installer or third-party binary is bundled. The existing installer integration remains in source but no setup program is distributed here. Unavailable sensor values remain unavailable. The public defaults disable external ping probes and the optional LibreHardwareMonitor HTTP endpoint. Enable `pingHost` or `lhmUrl` explicitly if desired. Local hardware sampling and network journaling still run when the widget starts.

## Build and run from source

Requirements: Windows x64, Windows PowerShell and .NET Framework 4.8 with its 64-bit C# compiler and WPF assemblies. The scripts use the Windows Framework compiler; no .NET SDK or package installation is required. Windows 10/11 x64 are intended targets; there is no published compatibility test matrix yet.

Run in **Windows PowerShell**, from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\limits-widget\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\resources-widget\build.ps1
```

Each build copies `config.example.json` to an untracked `config.json` only if it does not exist, then compiles the executable alongside the source. Build scripts never request that a running widget exit. A running output executable must be closed manually before rebuilding it.

Run in **Windows PowerShell**, from the repository root:

```powershell
.\windows\limits-widget\LimitsWidget.exe --show
.\windows\resources-widget\ResourcesWidget.exe --show
```

Before enabling a provider, sign in using its own supported application/CLI workflow. Open `windows/limits-widget/config.json` locally and change only the required `services.<provider>.enabled` field to `true`. Optional `command` fields can point to locally installed CLI executables; no personal paths are supplied. Keep credentials out of configuration and issues.

Tray click toggles the card. Default hotkeys: **Ctrl+Alt+L** for AI Limits and **Ctrl+Alt+R** for System Resources. Tray menus expose theme, pinning, size and startup choices. **Starting either public widget does not automatically enable Windows startup or change notification-area visibility.** Startup is an explicit tray-menu action. Exit via the tray menu.

The application folder must be writable: widgets keep local settings, snapshots and logs beside their executable. Avoid a protected system folder. A named per-user single-instance mechanism means running another copy of the same widget may show the already-running copy.

## Local data and privacy

- AI Limits writes `state.json` (window settings, usage snapshots and credit history) and diagnostic `widget.log` locally. It invokes enabled provider CLIs and makes the Kimi requests described above; those services apply their own network and authentication behavior.
- System Resources writes local state, diagnostic logs and `network-log/` journals. Logs and exports can contain hardware identifiers, process names, adapters, usage information and diagnostic paths. Review and redact before sharing.
- No account files, real usage fixtures, logs, local configuration or compiled binaries are included in this repository. They are ignored by Git. `.gitignore` does not sanitize an export or undo an accidentally committed file.
- The static project page has no application backend, provider calls, analytics or external fonts. Its widget cards contain **simulated data**, not a live account snapshot or a screenshot of a running app.

## Layout

```text
windows/limits-widget/       AI limits application source, icon, build script and example config
windows/resources-widget/    System resources application source, icon, build script and example config
docs/                Standalone GitHub Pages site (no build step)
CONTRIBUTING.md      Contribution and verification guidance
```

Open `docs/index.html` directly to preview the page, or serve `docs/` with any static HTTP server. GitHub Pages can publish the `docs` folder on `main`; all site resources are relative to support `/desktop-widgets/`.

## Verification and current limits

This package was compiled locally with the installed Windows Framework compiler. Configuration JSON and static links were checked. No authenticated provider requests, interactive application launch, hardware matrix, installer, signing or automatic update verification was performed for this distribution. See [contribution guidance](CONTRIBUTING.md) for useful reports.

## License

[MIT License](LICENSE), Copyright (c) 2026 Svetomir. No third-party provider affiliation or endorsement is implied.


