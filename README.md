# Kiloview Setup

Kiloview Setup is a local Windows web application for discovering, onboarding, identifying, and monitoring factory-reset Kiloview N6 and N60 converters. It listens only on `http://localhost:8091` and installs a desktop shortcut to that address.

## Current workflow

1. Enter the KiloLink server IP/code and server login, static IP pool, Job Name, NDI Discovery Server IP, and scan network. The KiloLink username/password are retained locally for that server IP.
2. Review discovered N6/N60 devices. Units already in the static pool are preserved and excluded by default.
3. Confirm a collision-checked address plan. New addresses start above the highest occupied/onboarded address in the pool.
4. The service readdresses devices sequentially, configures KiloLink, configures the NDI Discovery Server, and applies the Job Name as the NDI group.
5. All units temporarily enter decoder mode. After the negotiation window, a valid HDMI output resolution classifies a unit as a decoder; other units return to encoder mode.
6. After initial onboarding, select the latest N6 and N60 `.bin` firmware packages. The application validates model coverage, stores local copies with SHA-256 fingerprints, and hands the matching packages to the KiloLink fleet-update stage.
7. Rename decoder hostnames and NDI channels, then select **Setup completed** to send the black preset to every decoder.
8. The application becomes a red/green card-based monitor with IP, role, group, resolution, firmware, and direct links to each device UI.

The advanced setup section contains factory credentials and a simulation mode. Simulation mode exercises the full workflow without making network or hardware changes.

## Build and run

Requires the .NET 8 SDK.

```powershell
dotnet build --configuration Release
dotnet run --project .\Kiloview.Setup.csproj
```

Open `http://localhost:8091`. Use **Simulation mode** for the first acceptance run.

## Create the Windows package

Framework-dependent package (requires the .NET 8 ASP.NET Core Runtime on the destination PC):

```powershell
.\scripts\Publish.ps1
```

Self-contained Windows x64 package (larger; restore may need internet access):

```powershell
.\scripts\Publish.ps1 -SelfContained
```

Extract `artifacts\KiloviewSetup-Windows.zip` and run `Install.cmd`. Installation is per-user, needs no administrator rights, starts the service at sign-in, creates Start Menu entries, and places `Kiloview Setup.url` on the desktop.

## Operational safeguards

- The UI is bound to loopback only. Device credentials and onboarding data are not exposed as a LAN web service.
- The KiloLink onboarding code is used by the active run but is not written to `state.json`.
- KiloLink server usernames/passwords are stored locally in Windows Credential Manager under `KiloviewSetup/KiloLink/<server-ip>`. Passwords are not written to `state.json` or returned by the local web API.
- Persistent state is stored in `%LOCALAPPDATA%\Kiloview Setup\state.json`.
- Staged firmware is stored under `%LOCALAPPDATA%\Kiloview Setup\firmware`, separated by device model, and checked with SHA-256 after upload.
- Static address conflicts are checked using known inventory, ICMP, HTTP, and HTTPS before a plan is offered.
- A failed readdress, reconnect, API call, or mode switch is shown per device and does not silently pass.
- N60 mode changes can take about one minute. Keep displays on until HDMI negotiation completes.

## Hardware acceptance required

The software build and complete simulation workflow are verified, but live N6/N60 hardware was not available in this workspace. Before production use, validate one factory-reset unit of each model and firmware version on an isolated VLAN, specifically:

- factory credentials and API authentication;
- N6 firmware exposure of the KiloLink client endpoint (the published N6 API omits it, so the adapter capability-probes both known endpoint casings);
- NDI Discovery Server persistence for both HX and HB streams;
- whether the firmware reports no negotiated HDMI resolution as an empty/`none` value;
- hostname/channel changes while cycling a decoder through encoder mode;
- black preset output on completion.

KiloLink Server Pro officially supports uploading model-specific firmware and batch-upgrading selected devices in Maintenance Mode. Its public manual does not document the HTTP endpoints or payloads used by that web workflow. Version 0.2 therefore fully stages and validates firmware, completes the fleet update in simulation, and gives a guarded KiloLink handoff for real servers rather than sending an unverified firmware command. To automate the final real-server click path, capture the firmware upload/maintenance/upgrade requests from the exact deployed KiloLink Server Pro version (or obtain its private API specification) and implement them at the existing `FirmwareService` integration boundary.

The documented APIs do not expose a supported call for rendering arbitrary IP/group text over the HDMI output. The UI records and shows that identity, and naming is applied to the device, but a true HDMI title card requires either a firmware overlay API from Kiloview or a small NDI title-card sender. That integration should be added only against the actual firmware/API supplied for the deployment.

## Official API references

- [Kiloview N5/N6 API v3.0](https://enstatic.kiloview.com/wp-content/uploads/2025/09/NEW-N5ampN6-APIEN-version-3.0-1.pdf)
- [Kiloview N60 Web API v2.01](https://enstatic.kiloview.com/wp-content/uploads/2025/09/N60-WEB-API-EN-Version2.01.pdf)
- [Kiloview N6/N5 user manual](https://enstatic.kiloview.com/wp-content/uploads/2025/09/N6ampN5-for-NDI%C2%AEUser-ManuelV1.pdf)
