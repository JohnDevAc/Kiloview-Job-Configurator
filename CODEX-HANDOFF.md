# Codex Project Handoff

Read this file before changing or publishing Kiloview Job Configurator.

Last updated: 30 July 2026

## Current baseline

- Repository: `JohnDevAc/Kiloview-Job-Configurator`
- Active development branch: `development`
- Latest commit: `32a62dd` — `Onboard the preferred local NDI interface`
- Current version: `0.8.0-dev.27`
- Release channel: `Development`
- Published release: <https://github.com/JohnDevAc/Kiloview-Job-Configurator/releases/tag/v0.8.0-dev.26>
- Current active work: Windows PC Onboarding Utility compatibility release.
- The companion source and packages were moved to the sibling
  `Kiloview PC Onboarding` project. Do not copy them back into this repository.
- Companion repository: <https://github.com/JohnDevAc/Kiloview-PC-Onboarding>
  (private, default branch `main`, initial commit `f8f56c3`).

All requested application work through `v0.8.0-dev.26` has been committed, pushed, packaged, and published.

## Uncommitted companion compatibility work

- Main application additions retained in this repository:
  - `GET /api/pc-onboarding/profile`;
  - `POST /api/pc-onboarding/register`;
  - persisted `RemoteWindowsPcs` records;
  - remote Windows NDI endpoint cards in the monitor.
- Registration rejects a reported address that does not equal the connecting
  IPv4 client and rejects an old/missing EULA acceptance.
- Validation completed:
  - main and companion Debug builds: zero warnings/errors;
  - both projects pass `dotnet format --verify-no-changes`;
  - LAN-path profile and successful registration integration test passed with
    isolated state on TCP 18091;
  - spoofed-address and old-EULA registrations did not enter state;
  - self-contained x64 publish and ZIP content inspection passed.
- Still required before release:
  - decide the next development version;
  - commit/push/release only when John explicitly asks.

## Most recent implementation

### Unified network selection

- Network-adapter selection is the first control in device onboarding.
- The old manual **Networks to scan** field was removed.
- The duplicate adapter selector in multicast setup was removed.
- Adapter ID and IPv4 address are persisted in `AppState`.
- Device discovery, KiloLink discovery, NDI Discovery Server discovery, local NDI identity sources, and multicast planning use the stored adapter.
- If the adapter disappears, the application requires explicit reselection instead of silently switching networks.

### NDI Access Manager and the local PC

- Selecting an onboarding adapter writes its IPv4 address as the sole entry in:

  ```json
  {
    "ndi": {
      "adapters": {
        "allowed": ["SELECTED_IPV4_ADDRESS"]
      }
    }
  }
  ```

- The official NDI configuration field is `ndi.adapters.allowed`.
- Existing NDI configuration is preserved and backed up before replacement.
- The Windows PC is immediately stored as a persistent `local-pc` endpoint.
- Its monitor card initially shows:
  - preferred NDI interface;
  - adapter name and address;
  - health;
  - `UNICAST READY`.
- Multicast setup updates this same card with multicast allocation, TTL, and live state.
- Reverting multicast removes multicast state but retains the local endpoint and preferred interface.
- The 15-second monitor pass detects Access Manager preferred-interface drift.
- A **Reapply preferred interface** action appears on the local card when required.
- Onboarding cannot continue when the preferred interface could not be applied.
- If NDI Access Manager is open and would overwrite a changed configuration, the UI asks the user to close it.

## Important files

- Shared release metadata: `Directory.Build.props`
- API and application startup: `Program.cs`
- Persistent models: `Core/Models.cs`
- State storage and corruption recovery: `Core/AppStateStore.cs`
- Network adapter resolution: `Core/NetworkAddressing.cs`
- Device discovery: `Core/NetworkDiscovery.cs`
- KiloLink discovery: `Core/KiloLinkServerClient.cs`
- NDI Discovery Server search: `Core/NdiDiscoveryServerClient.cs`
- NDI Access Manager configuration: `Core/NdiAccessManagerService.cs`
- Local/device status monitoring: `Core/DeviceMonitor.cs`
- Multicast planning/apply/revert: `Core/MulticastService.cs`
- Frontend behavior and rendering: `wwwroot/app.js`
- Main UI structure: `wwwroot/index.html`
- Styling: `wwwroot/styles.css`
- Installer/package script: `scripts/Publish.ps1`
- Release metadata validation: `scripts/Test-ReleaseMetadata.ps1`

## Validation already completed

For `v0.8.0-dev.26`:

- `dotnet format --verify-no-changes` passed.
- Release build passed with zero warnings and zero errors.
- Frontend JavaScript syntax check passed.
- Isolated application data and NDI configuration paths were used for integration tests.
- Selecting Wi-Fi at `192.168.0.105/24`:
  - wrote `192.168.0.105` as the sole preferred NDI interface;
  - created the persistent local-PC endpoint.
- Simulation onboarding completed.
- Multicast applied to the simulated endpoint and local PC.
- Multicast revert retained the local-PC endpoint and preferred interface.
- Manually changing the preferred interface to a different test address was detected by the monitor.
- Reapply restored the selected interface.
- GitHub release metadata, target branch, prerelease flag, installer size, and SHA-256 digest were verified.
- The installed `dev.25` updater detected `dev.26`.

## Release procedure

Development releases must:

1. Be made from `development`.
2. Increment the version in `Directory.Build.props`.
3. Use a tag such as `v0.8.0-dev.27`.
4. Be published as a GitHub prerelease targeting `development`.
5. Include:
   - `Kiloview-Job-Configurator.exe`
   - `Kiloview-Job-Configurator-Windows.zip`
   - `SHA256SUMS.txt`
6. Be built with:

   ```powershell
   .\scripts\Publish.ps1 -SetupExe
   ```

7. Be checked through the installed updater after publishing.

Do not publish a development tag from `main`, and do not publish a stable tag from `development`.

## Machine-local data that OneDrive does not transfer

The source folder can sync, but these items remain local to each Windows PC:

- Application state: `%LOCALAPPDATA%\Kiloview Setup\state.json`
- State backup and logs: `%LOCALAPPDATA%\Kiloview Setup`
- Firmware staging and downloaded updates
- Windows Credential Manager entries
- Installed application version
- Scheduled task, firewall rule, and tray process
- NDI configuration: `C:\ProgramData\NDI\ndi-config.v1.json`
- NDI Access Manager installation and running state
- Network adapters and their IDs/IP addresses

Do not assume the laptop has the same application state, credentials, adapters, installed version, or NDI configuration as the desktop.

## Two-machine coordination

Using this file as a context handoff will work, but OneDrive is not a safe substitute for Git synchronization.

- Do not let both Codex instances edit or run Git operations in this same OneDrive-synced repository simultaneously.
- OneDrive can sync `.git/index`, lock files, build outputs, or partially written files and cause conflicts or repository corruption.
- Preferred setup: keep a separate local clone on each PC and exchange committed work through GitHub.
- Treat `origin/development` as the authoritative source baseline.
- Before starting work on either PC:

  ```powershell
  git status -sb
  git fetch origin
  git log -1 --oneline
  ```

- Confirm the latest commit and version match this file.
- If one Codex instance is actively changing files, wait for it to commit and push before the other starts.
- After meaningful work, update this handoff with:
  - latest commit;
  - version/release;
  - completed changes;
  - validation performed;
  - unfinished work or known issues.

## Instructions for the next Codex instance

1. Read this entire file.
2. Inspect `git status -sb`, the latest commit, and `Directory.Build.props`.
3. Do not overwrite uncommitted changes from the other machine.
4. Use the current `development` branch unless John explicitly requests otherwise.
5. Preserve the single local-PC endpoint design; multicast must update that card rather than creating another card.
6. Verify changes with an isolated `KILOVIEW_DATA_DIR` and `KILOVIEW_NDI_CONFIG_PATH` before touching live NDI configuration.
7. Do not commit, push, publish, install, or change real devices unless John asks for that action.
