<p align="center"><img src="wwwroot/KiloviewSetup.png" width="140" alt="Kiloview Job Configurator icon"></p>

# Kiloview Job Configurator

> **Proprietary source-available software — not open source.** Free for non-commercial use in unmodified form only. Modification, derivative works, redistribution, and commercial use are prohibited. See [LICENSE.md](LICENSE.md).

Kiloview Job Configurator is the Windows web application for discovering, onboarding, identifying, and monitoring Kiloview N6/N60 converters and TeleTool encoders. A development run listens on `http://localhost:8091`; the Windows installer enables private-LAN access on TCP `8091`, permits local-subnet UDP reception for embedded NDI multicast previews, and installs a desktop shortcut to the local address.

This repository contains the **job configurator only**. The separate [Kiloview Environment Setup](https://github.com/JohnDevAc/Kiloview-Environment-Setup) repository installs and maintains KiloLink Server Pro, NDI® Tools, and NDI Discovery Server prerequisites.

## Current workflow

1. Select the active IPv4 adapter connected to the device network. That single stored choice controls device discovery, KiloLink and NDI Discovery Server searches, local NDI identity sources, and multicast setup; the application no longer accepts a separate list of scan networks. The selected IPv4 address is written to NDI Access Manager as the only preferred interface, and the Windows PC is immediately added as an onboarded local NDI endpoint. On the second onboarding screen, a standard device-sized card appears under **Windows NDI Endpoints** and confirms the endpoint, adapter, address, preferred-interface state, and any actionable readiness error without displaying individual NDI application statuses. Access Manager and configuration-consuming NDI client tools—including the separate NDI Discovery client—must be closed before local settings are applied; the background NDI Discovery Server remains running because onboarding depends on it. The Service connections section then starts minimized with a live status spinner while the application scans only the selected network for KiloLink Server Pro and an NDI Discovery Server listening on TCP `5959`, then verifies KiloLink authentication. A green tick keeps the section minimized when everything is ready; any discovery or login failure expands it for intervention, and a manual Show details/Minimize control remains available. Existing KiloLink credentials are reused from Windows Credential Manager. When no stored credential works, the application checks the official KiloLink Server Pro factory login `admin/Kiloview001`; a factory server is given the exact Job Name as its new administrator password, verified, and stored securely before onboarding continues. KiloLink requires that Job Name to be 6–32 characters with an uppercase letter, lowercase letter, and number.
2. Review discovered N6/N60 devices and TeleTool Dev encoders. Every eligible card can be included in onboarding or explicitly left standalone; standalone units are omitted from the final plan and receive no configuration changes. Units already in the static pool are preserved and excluded by default. TeleTools already adopted by another Fleet Manager or managing their own fleet are shown but cannot be selected.
3. Confirm a collision-checked address plan and authorize the application to accept the Kiloview EULA on the selected devices. A clean-onboarding scan automatically retries credentials retained for previously managed Kiloviews, so units whose factory password was replaced by an earlier job remain discoverable without displaying the saved password. New addresses start above the highest occupied/onboarded address in the pool. When the plan starts, the configurator atomically replaces its previously managed local NDI send/receive group with the new Job Name, while preserving unrelated Access Manager groups. It also reapplies the selected interface and Discovery Server and verifies the saved configuration before any device is changed. If NDI Access Manager is open or the configuration cannot be verified, onboarding stops for user intervention.
4. Before the run starts, select the latest N6 and N60 `.bin` firmware packages required by the plan. The application stores local copies with SHA-256 fingerprints. Each selected Kiloview is logged into at its discovered address, its license is accepted, and its login is changed to `admin/<Job Name>`. Devices already on the staged version are skipped; outdated units are updated directly through their model-specific device API and must return reporting the staged version before any network or KiloLink change is allowed. The new local device credentials are stored with the device record for monitoring and future configuration.

Remote Windows NDI endpoints do not require a resident companion service. The Job Configurator sends a lightweight ICMP availability check during its existing 15-second monitor pass. A card remains green while replies are received, turns amber after one or two missed checks, and turns red only after three consecutive failures to avoid flicker during brief network interruptions. Successful checks refresh the endpoint's last-seen time. The separate PC Onboarding utility installs the narrowly scoped inbound ICMP rule needed for this check when the PC joins a job.

Windows endpoint card headers use `Windows · <OS version>`. The local version is read directly from Windows; the companion registration API accepts an optional `operatingSystemVersion` value for remote PCs. Existing remote registrations remain compatible and show `VERSION UNKNOWN` until a companion version that supplies the field registers them again.

Local and remote Windows endpoint cards use the same pill sequence where the data applies: availability, preferred-interface state, adapter, NDI runtime version, job group, multicast allocation, and TTL. The local NDI runtime version is read from the installed NDI Tools runtime; remote values continue to come from companion registration.
5. For each serial number, the service creates or reuses a KiloLink device record, generates any required authorization code on KiloLink Server, and keeps the KiloLink Alias equal to the assigned hostname.
6. The service readdresses devices sequentially, applies the generated KiloLink code, configures the NDI Discovery Server, and applies the Job Name as the NDI group.
7. All units temporarily enter decoder mode. After the negotiation window, a valid HDMI output resolution classifies a unit as a decoder; other units return to encoder mode.
8. On the **Name the displays** page, the application confirms that the Job Name has been applied as the NDI group, publishes one temporary NDI identity card per decoder, and selects it on that unit. Every connected HDMI display shows its hostname, IP address, `JOB NAME / NDI GROUP`, and NDI channel. Decoder and encoder cards both allow the hostname and NDI channel name to be changed; encoder previews make the associated HDMI input easy to identify. Renaming refreshes the displayed decoder card and synchronizes the KiloLink Alias.
9. Select **Setup completed** to stop the temporary NDI identity sources and send the black preset to every decoder.
10. The application becomes a red/green card-based monitor, with Kiloview decoders, Kiloview encoders, and TeleTool encoders in separate groups. Kiloview and TeleTool encoder cards include a 320x240 capture of their current NDI output. When a TeleTool reports that its NDI stream is active but two consecutive frame captures fail, its preview changes to a warning image and the card highlights a likely NDI output, group, or Discovery Server error; the warning clears when a frame is received or the stream stops. TeleTool cards use the TeleTool Fleet Manager snapshot and adoption APIs to show reachability, stream state, TV channel, NDI name/group, RF signal, pipeline health, and Dev version. They also query the TeleTool audio status/device APIs and display a compact green or red Dante audio badge; its text and tooltip identify active, stopped, unavailable, or faulted output without relying on colour alone. Controls include Start NDI, Stop NDI, direct device-UI, and Remove from job. Removing a TeleTool releases this configurator's Fleet Manager adoption and updates the fleet totals immediately; it does not reset the unit's network/NDI configuration or stop an active stream.
11. Open **Multicast setup** from the monitor to generate a deterministic organization-local address pool. It reuses the adapter selected during onboarding rather than asking for another local interface. Every NDI sender receives a unique `/28` allocation, every decoder is registered with the complete sender list, and N60 decoders are explicitly switched to multicast receive mode. The existing local-PC card is updated with its multicast allocation and live state rather than creating another endpoint. Reverting to unicast removes those multicast details but retains the onboarded PC and preferred NDI interface. Device cards show a green multicast icon while the transport is in use, amber when configured but inactive, and grey when unconfigured.

Enable **Clean onboarding** only when intentionally rebuilding a network. At the start of the confirmed run it permanently deletes all existing KiloLink device records and real device groups, clears prior configurator job devices and multicast/remote-endpoint state, preserves the selected devices and local PC, and then creates the new job from an empty inventory. The option is off by default.

TeleTool-only onboarding runs skip the Kiloview firmware and decoder-identification stages, even when an older Kiloview fleet remains in local state. Starting a real network scan with Simulation mode disabled removes all simulated devices and clears any simulated job/firmware state before discovery results are stored.

## TeleTool Dev integration

TeleTool discovery probes port `8000` and validates the `/api/manager/discovery` identity before a host is accepted. Onboarding requires TeleTool `1.8.5+dev.54` or later from the TeleTool `dev` branch because that release exposes the `ndi_groups` and `ndi_discovery_server` configuration fields. Older or Main builds remain visible in discovery with an update-required message and are never partially configured.

Each selected TeleTool remains an encoder and receives a `JOB-TT-###` hostname, a job-derived NDI channel name, the selected static IPv4 address on `eth0`, the job's NDI Discovery Server, and the exact Job Name as its NDI send group. If its NDI stream is already running, onboarding restarts that stream with the new identity/group so the change takes effect immediately. The configurator then maintains the TeleTool adoption heartbeat while the unit remains in the onboarded fleet.

The existing 15-second TeleTool `/api/status` poll also reads `system_temperature_c`. Each TeleTool monitor card shows a compact temperature pill beside Dante and RF: green below `70°C`, amber from `70°C`, and red from `80°C`. The numerical value and accessible tooltip remain present in every state; older TeleTool builds that do not report the field show `TEMP N/A`. Temperature-only updates modify the pill in place so live preview cards are not rebuilt.

The advanced setup section contains factory credentials and a simulation mode. Simulation mode exercises the full workflow without changing Kiloview or KiloLink hardware. Each simulation scan starts a fresh synthetic fleet so identities from an earlier run cannot leak into the next job. On the **Name the displays** page it publishes real test-card NDI sources in both `public` and the simulated Job Name group so they are immediately visible in NDI Studio Monitor; every source name includes the same hostname and static IP shown on its decoder card.

## Multicast setup

Multicast is deliberately separate from onboarding. The generated pool stays within the organization-local `239.192.0.0/14` scope and grows with the fleet while preserving a non-overlapping `/28` block per encoder and for the optional local PC. Regenerating selects another aligned pool; applying validates every range again before making changes.

N6/N60 encoders are configured and read back through their documented multicast sender APIs. N60 decoders use their explicit multicast receive endpoint. The N6 API does not expose a separate receiver-transport switch, so N6 decoders receive the sender-advertised transport and are populated with the job group and every onboarded sender address. TeleTool senders require a Dev build that exposes the multicast prefix, mask, and TTL fields; an active TeleTool stream is restarted so its new transport settings take effect.

On Windows, the configurator preserves unrelated settings in `%ProgramData%\NDI\ndi-config.v1.json`, adds the Job Name to the send/receive groups, enables multicast send/receive, and keeps a `.kiloview-backup` copy before replacement. NDI Access Manager must be closed while applying because an open instance retains an in-memory copy and can overwrite external changes when it exits. The configurator checks the live multicast range, TTL, send/receive groups, receive mode, and Discovery Server every 15 seconds. If any setting changes, the local-PC card is marked as needing attention until multicast setup is reapplied. The embedded card-preview receiver is recreated automatically after this change, so encoder previews can follow multicast sources without restarting the configurator. Other NDI applications maintain their own runtimes and must be restarted after Access Manager changes.

**Revert all to unicast** safely disables multicast across the onboarded fleet and the optional local PC while preserving device names, NDI groups, Discovery Server settings, and saved address details. Active TeleTool streams restart with unicast transport and are verified through live status readback. Successful endpoints are cleared immediately; unreachable endpoints remain visible with an error so the operation can be retried without losing the multicast plan.

Do not enable multicast on an unmanaged or unprepared production LAN. An active IGMP querier and IGMP snooping are required, and routed deployments must be designed with the network operator before increasing TTL above `1`.

## Build and run

Requires the .NET 8 SDK.

```powershell
dotnet build --configuration Release
dotnet run --project .\Kiloview.Setup.csproj
```

Open `http://localhost:8091`. Use **Simulation mode** for the first acceptance run.

## Windows PC onboarding companion

The Windows PC Onboarding Utility is maintained as a separate project. This
repository retains only the compatible registration API, remote Windows-PC
state, and device-monitor cards required by that companion.

After it finds an active Job Configurator on TCP `8091`, the utility backs up
the local NDI configuration, applies the selected preferred interface, Job Name
send/receive group, and NDI Discovery Server, verifies the result, and
registers the PC in the main device monitor. Remote Windows endpoint cards can
be removed from the job without changing that PC's NDI configuration. The Job
Configurator's own local endpoint remains protected and has no removal action.
Multicast planning reserves a unique `/28` sender range for every onboarded
remote Windows endpoint. Because the Job Configurator cannot change NDI Access
Manager on another PC, the endpoint card shows the prefix, netmask, and TTL for
the user to apply manually; the reservation is not reported as remotely applied
or verified.

## Create the Windows package

Recommended single-file installer (self-contained, no separate .NET installation required):

```powershell
.\scripts\Publish.ps1 -SetupExe
```

Distribute `artifacts\Kiloview-Job-Configurator.exe`. The installer carries the Kiloview Job Configurator application icon, uses the same icon and identity on the Windows taskbar, and presents a branded logo/title header above the EULA. Its Per-Monitor V2 EULA window uses a responsive, resizable layout, constrains itself to the active monitor's working area, and keeps the acceptance and action controls visible while the licence text scrolls. Double-clicking it requests Windows administrator approval, installs for the current user, registers the elevated service to start automatically at sign-in with LAN access enabled, adds a Windows Firewall rule for TCP `8091` plus a separate inbound UDP rule required by embedded NDI multicast previews, limits both to `LocalSubnet` on Domain/Private profiles, starts it immediately, opens `http://localhost:8091`, and creates branded Desktop and Start Menu shortcuts. During an upgrade it stops only the executable from this product's installation directory and removes the previous application payload before copying the replacement, while preserving `%LOCALAPPDATA%\Kiloview Setup` state, logs, credentials, and firmware. Updates launched from the web UI explicitly hand foreground activation to the elevated installer, which also brings its EULA window forward when shown. Other trusted LAN devices can open `http://<setup-pc-ip>:8091`. Public network profiles remain blocked, and uninstalling removes both firewall rules. The service runs as a notification-area application without a console window or taskbar button. Double-click its tray icon to open the web UI, or right-click it for **Open Web UI**, **Restart**, and **Exit**. The shortcuts restart the elevated service when necessary before opening the UI.

Framework-dependent package (requires the .NET 8 ASP.NET Core Runtime on the destination PC):

```powershell
.\scripts\Publish.ps1
```

Self-contained Windows x64 package (larger; restore may need internet access):

```powershell
.\scripts\Publish.ps1 -SelfContained
```

Extract `artifacts\Kiloview-Job-Configurator-Windows.zip` and run `Install.cmd`. Installation is per-user, registers an elevated scheduled task so the service starts with administrator rights at sign-in, and creates branded Desktop and Start Menu launch shortcuts. The installer and application request elevation through Windows UAC.

## Software updates

Open **System settings** from the application header to view the installed version, confirm administrator status, download a local diagnostics package, and select an update channel:

- **Main** receives stable GitHub releases targeted to the `main` branch.
- **Development** receives GitHub prereleases targeted to the `development` branch. Development builds display a persistent warning banner on every UI page.

The channel selection is stored in the application's shared local state, so it applies to every browser that opens the service. Switching channels is supported in either direction, including returning from a newer Development build to the latest stable Main release. When an update or channel switch is available, the application downloads only the named Windows installer from `JohnDevAc/Kiloview-Job-Configurator`, verifies its size and GitHub-published SHA-256 digest, and opens it with administrator rights. The update proceeds only after the user accepts the installer EULA.

## Branch and release policy

The repository has two long-lived branches. Production changes are released from `main`; active work and prereleases are released from `development`. Stable release tags use `vMAJOR.MINOR.PATCH`. Development tags use `vMAJOR.MINOR.PATCH-dev.NUMBER`, are marked as GitHub prereleases, and must target the `development` branch. Do not publish a prerelease from `main` or a stable release from `development`, because the in-application updater deliberately rejects a mismatched feed. Version, channel, company, product, and copyright metadata are defined once in `Directory.Build.props`; every package build validates this metadata and, in GitHub tag builds, requires the tag to match the shared version.

## License and third-party notices

Copyright © 2026 John Lightfoot. All rights reserved.

The source is publicly visible but remains proprietary. The [End User License Agreement (EULA)](LICENSE.md) permits viewing the source, compiling it without modification, and installing or running unmodified copies solely for non-commercial purposes. It does not grant permission to modify the project, create derivative works, redistribute it, or use it commercially. The complete EULA controls if this summary differs from it.

The self-contained Windows installer includes Microsoft .NET and ASP.NET Core runtime components under their own terms. Their license and attribution files are retained in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES/README.md).

The application loads the NDI runtime only from a separate installation of [NDI Tools](https://ndi.video/tools/); the NDI runtime is not redistributed by this project. NDI® is a registered trademark of Vizrt NDI AB. Kiloview, KiloLink, and related product names belong to their respective owner. This project is not endorsed by or affiliated with Kiloview or Vizrt NDI AB.

## Operational safeguards

- The installed UI listens on TCP `8091` for LAN management. Windows Firewall limits inbound access to `LocalSubnet` on Domain/Private profiles and blocks Public profiles. The UI has no separate application login, so expose it only on a trusted management LAN.
- Stored device credentials remain in local `state.json` for device management but are excluded from every HTTP API response.
- KiloLink authorization codes are generated server-side per serial number, used by the active device configuration call, and are not written to `state.json`.
- KiloLink server usernames/passwords are stored locally in Windows Credential Manager under `KiloviewSetup/KiloLink/<server-ip>`. A newly discovered factory server is authenticated with the official `admin/Kiloview001` login, changed to `admin/<Job Name>`, re-authenticated, and only then stored. When a stored login is available, onboarding displays its username and a masked password indicator and allows the blank password field to reuse it. An explicit View/Hide control can retrieve the password only through a no-cache, loopback-only endpoint opened from `localhost` on the setup PC; LAN clients cannot retrieve it. Passwords are never written to `state.json`.
- Device credentials are intentionally stored locally in `state.json`; after first-login provisioning the username is `admin` and the password is the exact Job Name.
- Persistent state is stored in `%LOCALAPPDATA%\Kiloview Setup\state.json`, with a last-known-good `state.json.bak`. Invalid primary state is timestamped and quarantined before the backup is restored; it is never silently replaced with an empty configuration.
- The selected network-adapter ID and current IPv4 address are stored in the same application state and reused by every network-aware workflow. Its address is also stored as the sole entry in NDI's `ndi.adapters.allowed` configuration. If that adapter disappears or Access Manager is changed, the local endpoint card is marked as needing attention instead of silently switching networks.
- Rotating runtime logs are retained under `%LOCALAPPDATA%\Kiloview Setup\logs`. **System settings → Download diagnostics** packages those logs with runtime details while deliberately excluding device state and credentials; downloads are limited to `localhost`.
- Staged firmware is stored under `%LOCALAPPDATA%\Kiloview Setup\firmware`, separated by device model, and checked with SHA-256 after upload.
- The KiloLink web/API port is configured separately from the device-link UDP port. The defaults are web `80` and device link `50000` (with KiloLink using `50000–50001` UDP).
- Static address conflicts are checked using known inventory, ICMP, HTTP, and HTTPS before a plan is offered.
- A failed readdress, reconnect, API call, or mode switch is shown per device and does not silently pass.
- N60 mode changes can take about one minute. Keep displays on until HDMI negotiation completes.
- Display identity cards use the NDI runtime installed with NDI Tools 6 on the setup PC. The runtime is loaded locally and is not redistributed in the installer.
- Multicast allocations are conflict-checked within the generated organization-local pool. Applying local-PC settings backs up the existing NDI Access Manager JSON and atomically validates its replacement.

## Hardware acceptance required

The software build and complete simulation workflow are verified, but live N6/N60 hardware was not available in this workspace. Before production use, validate one factory-reset unit of each model and firmware version on an isolated VLAN, specifically:

- factory credentials and API authentication;
- first-login EULA acceptance and the forced password-change endpoint on the exact installed firmware (the published APIs document the user change but omit the EULA call, so the adapter capability-probes known routes and the device Web UI bundle);
- N6 firmware exposure of the KiloLink client endpoint (the published N6 API omits it, so the adapter capability-probes both known endpoint casings);
- NDI Discovery Server persistence for both HX and HB streams;
- whether the firmware reports no negotiated HDMI resolution as an empty/`none` value;
- hostname/channel changes while cycling a decoder through encoder mode;
- identity-source discovery and selection on N6 `2.00.0009.0134` and N60 `2.45.0014.0170`;
- black preset output on completion.

The KiloLink firmware API used by this application was recovered from and read-only tested against KiloLink Server Pro `1.08.0034`. Login, version discovery, firmware inventory, device-type inventory, and device-list calls were verified against a live local server. Firmware upload and batch dispatch remain explicitly confirmation-gated and will stop if the server is not on the validated 1.08 API contract or if every onboarded device cannot be matched in KiloLink.

Kiloview's N6 2.00 release notes mention text/image overlay support, but neither the published N6 nor N60 API documents an overlay endpoint. To avoid depending on an undocumented firmware call, the application renders the identity as a temporary local NDI source and selects it with each model's documented decoder API. This provides the same HDMI result on both N6 and N60 units.

## Official API references

- [Kiloview N5/N6 API v3.0](https://enstatic.kiloview.com/wp-content/uploads/2025/09/NEW-N5ampN6-APIEN-version-3.0-1.pdf)
- [Kiloview N60 Web API v2.01](https://enstatic.kiloview.com/wp-content/uploads/2025/09/N60-WEB-API-EN-Version2.01.pdf)
- [Kiloview N6/N5 user manual](https://enstatic.kiloview.com/wp-content/uploads/2025/09/N6ampN5-for-NDI%C2%AEUser-ManuelV1.pdf)
- [NDI sender API](https://docs.ndi.video/all/developing-with-ndi/sdk/ndi-send)
- [NDI configuration files](https://docs.ndi.video/all/developing-with-ndi/sdk/configuration-files)
