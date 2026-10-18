# Desktop Widgets

[Русский](README.ru.md) · [Project page](https://nikolaykazakovvs-ux.github.io/desktop-widgets/) · [Download v0.1.0](https://github.com/nikolaykazakovvs-ux/desktop-widgets/releases/tag/v0.1.0)

Two independent Windows widgets. **System Resources** monitors your computer and keeps a detailed network journal. **AI Limits** shows subscription usage windows and reset times. Use either on its own. Native C# and Windows Presentation Foundation (WPF); the application interface is currently Russian.

## Download and run

First public alpha collection: **v0.1.0**. Requirements: **Windows 10/11 x64 and .NET Framework 4.8**. macOS and Linux are planned separately; neither has an implementation or download yet.

| Application | Download | Start |
| --- | --- | --- |
| System Resources 1.1.0 | [Windows installer](https://github.com/nikolaykazakovvs-ux/desktop-widgets/releases/download/v0.1.0/ResourcesWidgetSetup-1.1.0-x64.exe) | Open the installer; it shows the card after installation |
| System Resources 1.1.0 | [Portable ZIP](https://github.com/nikolaykazakovvs-ux/desktop-widgets/releases/download/v0.1.0/ResourcesWidget-1.1.0-win-x64.zip) | Extract to a writable folder and open ResourcesWidget.exe |
| AI Limits 1.0.0 | [ZIP](https://github.com/nikolaykazakovvs-ux/desktop-widgets/releases/download/v0.1.0/LimitsWidget-1.0.0-win.zip) | Extract, open LimitsWidget.exe, then enable the sources you need |

[Release notes, assets and SHA-256 checksums](https://github.com/nikolaykazakovvs-ux/desktop-widgets/releases/tag/v0.1.0). Applications and installer are unsigned; Windows may warn about an unknown publisher. Follow your organisation's policies on managed computers.

The resources installer installs for the current user into a new/empty dedicated folder or a verified existing installation, adds shortcuts and an uninstall entry, and preserves configuration and window state on upgrade. **Startup is opt-in.** Uninstall removes known application binaries while preserving settings, network history and unrelated files. This first public preview does not bundle, install, start or configure sensor applications or drivers; use independently configured external sensors.

Click the tray icon to show or hide a card. Default hotkeys: **Ctrl+Alt+R** for resources, **Ctrl+Alt+L** for limits. Tray menus offer themes, scale, opacity, pinning, startup and exit. First launch does not automatically enable Windows startup or change tray icon visibility. Data is stored beside the application, so ZIP distributions need a writable folder. Another copy may show an already-running instance of the same widget.

## System Resources

For work and home computers, including investigating bottlenecks on older PCs:

- **CPU:** overall and per-logical-processor activity, history, frequency, temperature when a sensor is available, process/thread counts.
- **Memory:** used, available, committed and cached.
- **GPU:** load, video memory and available temperature, power, clock and fan readings.
- **Disks:** used/free capacity, reads/writes per drive and combined totals.
- **Processes:** leading CPU or memory users, grouped by executable name.
- **Network:** current speed, peaks, ping, link speed, daily traffic and connection events.

[![System Resources: actual application card](docs/images/resources-card.png)](docs/images/resources-card.png)

Actual application interface and system readings; additional sensors depend on hardware and configuration. Click for full size.

### Network journal

Click the peaks in the card to open daily history. Each day shows a short-interval peak, the highest one-minute average, timestamps, percentage of configured capacity, traffic and average latency. Open a day for minute-by-minute speed and router/external-host ping graphs, losses, observed time and events:

- adapter link-speed changes, such as 1 Gbps to 100 Mbps;
- adapter disconnects;
- repeated external-host nonresponses, compared with router replies;
- sustained high latency.

| Daily journal | Day details |
| --- | --- |
| [![Network journal](docs/images/network-journal.png)](docs/images/network-journal.png) | [![Day details](docs/images/network-day.png)](docs/images/network-day.png) |

Actual application interface; controlled, anonymised demo data. Local network-log files contain daily summaries, minute CSV files and events, retained for up to 400 days.

**Measurement boundaries.** The journal observes this computer while the widget runs, including local network traffic; it does not meter every device at home. The short peak depends on the sampling interval (about one second by default); the minute peak uses a rolling average of approximately 60 seconds. Capacity percentage uses the configured plan speed or adapter link speed, not a test of available Internet bandwidth. It helps describe observed load but cannot alone determine the right Internet plan.

External ping defaults to **1.1.1.1**, the router is discovered automatically, and the interval is two seconds. Empty pingHost disables probes. Internet Control Message Protocol (ICMP) replies can be filtered or rate-limited. Router/external comparisons help investigation but do not conclusively identify the provider, router or cable as the cause.

GPU readings use NVIDIA's driver interface and Windows Display Driver Model (WDDM) statistics. CPU temperature can come from compatible HWiNFO, AIDA64, Core Temp or LibreHardwareMonitor. The optional client reads an already configured local LibreHardwareMonitor at 127.0.0.1:8085; it never starts or configures a server. Without an external source, temperature remains unavailable. Empty lhmUrl disables that polling. Sensor availability depends on hardware, driver and helper permissions.

The WDDM path has been checked on an NVIDIA RTX 3070; AMD and Intel have not been separately tested on real hardware. There is no verified older-PC compatibility or overhead matrix. Assess the widget and optional helper on the intended computer. [Comparison with other tools](docs/comparison.md).

## AI Limits

Claude Code, Kimi Code, Antigravity and GPT / Codex in one card: available usage windows, reset times, credits and extra usage when a source returns them. A failed refresh can retain a previous snapshot marked stale. Missing data is not interpreted as zero usage.

[![AI Limits: actual application with test data](docs/images/limits-card.png)](docs/images/limits-card.png)

Actual application interface; demo data. No personal account snapshot is shown.

**All providers are disabled by default**, including when configuration is absent. Enabled providers refresh automatically (default 300 seconds, minimum 60). Sign in through the installed provider application or command-line interface (CLI), then enable only the required services.<provider>.enabled field in local config.json. An optional command field selects the installed CLI. Do not put credentials in configuration.

| Provider | Implemented source and access |
| --- | --- |
| Claude Code | Installed claude CLI: streaming get_usage request with /usage fallback. If CLI authentication is missing, may read the Claude desktop application's local usage cache. A user-triggered login action launches CLI authentication. |
| GPT / Codex | codex app-server and account/rateLimits/read; authentication belongs to the CLI. |
| Antigravity | agy /usage and /credits; needs a compatible installed CLI and its authentication. |
| Kimi Code | Running local application server and its server.token for authenticated loopback requests. This server token is read even when allowLocalTokens is false. Enabling allowLocalTokens permits an OAuth access-token fallback using local Kimi credential files and the configured endpoint. The widget does not refresh that token itself. |

allowLocalTokens controls only Kimi's direct fallback, not all authentication access. It is disabled in the distribution. Do not use an untrusted baseUrl: the direct fallback sends its bearer token there. Provider application compatibility can change; this is an independent project.

## Data and privacy

Settings, snapshots and journals stay beside the applications. Resources probes its configured ping host and may poll a local sensor helper; limits queries explicitly enabled sources. Exports can include device/process/adapter names, paths and activity history. Inspect and redact before sharing. Personal configuration, account data and logs are excluded from the repository and public archives.

The project page is static HTML, without analytics, account queries or external fonts. It uses actual WPF exports: real system readings for resources and controlled demo data for network history and limits.

## Developers

Sources: windows/resources-widget and windows/limits-widget. docs is a standalone GitHub Pages site, without a build step. Builds require Windows PowerShell and the .NET Framework 4.8 compiler; no separate .NET SDK is required.

Run in Windows PowerShell from the repository root; intended for developers or the Codex agent:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\resources-widget\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\limits-widget\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\resources-widget\installer\build-setup.ps1
```

Application build scripts do not stop a running widget or overwrite existing config.json. The widget-only installer builds from these sources without downloads or third-party binary payloads. Do not query personal accounts during ordinary automated checks. See [contribution guidance](CONTRIBUTING.md).

## Licenses

Project code: [MIT](LICENSE), Copyright (c) 2026 Svetomir. External sensor integrations are described in [THIRD-PARTY.txt](windows/resources-widget/installer/payload/THIRD-PARTY.txt). External applications retain their own licenses and are not bundled with this release. No provider affiliation or endorsement is implied.
