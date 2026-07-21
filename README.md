<p align="center"><img src="wwwroot/KiloviewSetup.png" width="140" alt="Kiloview Job Configurator icon"></p>

# Kiloview Job Configurator

> **Proprietary source-available software — not open source.** Free for non-commercial use in unmodified form only. Modification, derivative works, redistribution, and commercial use are prohibited. See [LICENSE.md](LICENSE.md).

Kiloview Job Configurator is the local Windows web application for discovering, onboarding, identifying, and monitoring factory-reset Kiloview N6 and N60 converters. It listens only on `http://localhost:8091` and installs a desktop shortcut to that address.

This repository contains the **job configurator only**. The separate [Kiloview Environment Setup](https://github.com/JohnDevAc/Kiloview-Environment-Setup) repository installs and maintains KiloLink Server Pro, NDI® Tools, and NDI Discovery Server prerequisites.

## Current workflow

1. The application scans active local networks for KiloLink Server Pro. Confirm the detected server, enter its login, then enter the static IP pool, Job Name, NDI Discovery Server IP, and scan network. The KiloLink username/password are retained locally for that server IP.
2. Review discovered N6/N60 devices. Units already in the static pool are preserved and excluded by default.
3. Confirm a collision-checked address plan and authorize the application to accept the Kiloview EULA on the selected devices. New addresses start above the highest occupied/onboarded address in the pool.
4. Each factory-reset unit is logged into with `admin/admin`, its license is accepted, and its login is changed to `admin/<Job Name>`. The new local device credentials are stored with the device record for monitoring and future configuration.
5. For each serial number, the service creates or reuses a KiloLink device record, generates any required authorization code on KiloLink Server, and keeps the KiloLink Alias equal to the assigned hostname.
6. The service readdresses devices sequentially, applies the generated KiloLink code, configures the NDI Discovery Server, and applies the Job Name as the NDI group.
7. All units temporarily enter decoder mode. After the negotiation window, a valid HDMI output resolution classifies a unit as a decoder; other units return to encoder mode.
8. After initial onboarding, select the latest N6 and N60 `.bin` firmware packages. The application validates model coverage, stores local copies with SHA-256 fingerprints, authenticates to KiloLink Server Pro, uploads each package, matches the onboarded devices, and dispatches model-specific batch upgrades.
9. On the **Name the displays** page, the application confirms that the Job Name has been applied as the NDI group, publishes one temporary NDI identity card per decoder, and selects it on that unit. Every connected HDMI display shows its hostname, IP address, `JOB NAME / NDI GROUP`, and NDI channel. Decoder and encoder cards both allow the hostname and NDI channel name to be changed; encoder previews make the associated HDMI input easy to identify. Renaming refreshes the displayed decoder card and synchronizes the KiloLink Alias.
10. Select **Setup completed** to stop the temporary NDI identity sources and send the black preset to every decoder.
11. The application becomes a red/green card-based monitor, with decoders and encoders in separate compact groups. Encoder cards include a 320x240 capture of their current HDMI input obtained from the encoder's low-bandwidth NDI preview stream and refreshed every five seconds, along with IP, group, firmware, and a direct device-UI link.

The advanced setup section contains factory credentials and a simulation mode. Simulation mode exercises the full workflow without changing Kiloview or KiloLink hardware. Each simulation scan starts a fresh synthetic fleet so identities from an earlier run cannot leak into the next job. On the **Name the displays** page it publishes real test-card NDI sources in both `public` and the simulated Job Name group so they are immediately visible in NDI Studio Monitor; every source name includes the same hostname and static IP shown on its decoder card.

## Build and run

Requires the .NET 8 SDK.

```powershell
dotnet build --configuration Release
dotnet run --project .\Kiloview.Setup.csproj
```

Open `http://localhost:8091`. Use **Simulation mode** for the first acceptance run.

## Create the Windows package

Recommended single-file installer (self-contained, no separate .NET installation required):

```powershell
.\scripts\Publish.ps1 -SetupExe
```

Distribute `artifacts\Kiloview-Job-Configurator.exe`. The installer carries the Kiloview Job Configurator application icon. Double-clicking it requests Windows administrator approval, installs for the current user, registers the elevated service to start automatically at sign-in, starts it immediately, opens `http://localhost:8091`, and creates branded Desktop and Start Menu shortcuts. The service runs as a notification-area application without a console window or taskbar button. Double-click its tray icon to open the web UI, or right-click it for **Open Web UI**, **Restart**, and **Exit**. The shortcuts restart the elevated service when necessary before opening the UI.

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

Open **System settings** from the application header to view the installed version, confirm administrator status, and check the official GitHub Releases feed. When a newer release is available, the application downloads only the named Windows installer from `JohnDevAc/Kiloview-Job-Configurator`, verifies its size and GitHub-published SHA-256 digest, and opens it with administrator rights. The update proceeds only after the user accepts the installer EULA.

## License and third-party notices

Copyright © 2026 John Lightfoot. All rights reserved.

The source is publicly visible but remains proprietary. The [End User License Agreement (EULA)](LICENSE.md) permits viewing the source, compiling it without modification, and installing or running unmodified copies solely for non-commercial purposes. It does not grant permission to modify the project, create derivative works, redistribute it, or use it commercially. The complete EULA controls if this summary differs from it.

The self-contained Windows installer includes Microsoft .NET and ASP.NET Core runtime components under their own terms. Their license and attribution files are retained in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES/README.md).

The application loads the NDI runtime only from a separate installation of [NDI Tools](https://ndi.video/tools/); the NDI runtime is not redistributed by this project. NDI® is a registered trademark of Vizrt NDI AB. Kiloview, KiloLink, and related product names belong to their respective owner. This project is not endorsed by or affiliated with Kiloview or Vizrt NDI AB.

## Operational safeguards

- The UI is bound to loopback only. Device credentials and onboarding data are not exposed as a LAN web service.
- KiloLink authorization codes are generated server-side per serial number, used by the active device configuration call, and are not written to `state.json`.
- KiloLink server usernames/passwords are stored locally in Windows Credential Manager under `KiloviewSetup/KiloLink/<server-ip>`. Passwords are not written to `state.json` or returned by the local web API.
- Device credentials are intentionally stored locally in `state.json`; after first-login provisioning the username is `admin` and the password is the exact Job Name.
- Persistent state is stored in `%LOCALAPPDATA%\Kiloview Setup\state.json`.
- Staged firmware is stored under `%LOCALAPPDATA%\Kiloview Setup\firmware`, separated by device model, and checked with SHA-256 after upload.
- The KiloLink web/API port is configured separately from the device-link UDP port. The defaults are web `80` and device link `50000` (with KiloLink using `50000–50001` UDP).
- Static address conflicts are checked using known inventory, ICMP, HTTP, and HTTPS before a plan is offered.
- A failed readdress, reconnect, API call, or mode switch is shown per device and does not silently pass.
- N60 mode changes can take about one minute. Keep displays on until HDMI negotiation completes.
- Display identity cards use the NDI runtime installed with NDI Tools 6 on the setup PC. The runtime is loaded locally and is not redistributed in the installer.

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
