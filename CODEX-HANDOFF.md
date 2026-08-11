# Codex Project Handoff

Read this file before changing or publishing Kiloview Job Configurator.

Last updated: 11 August 2026

## Current baseline

- Repository: `JohnDevAc/Kiloview-Job-Configurator`
- Active stable branch: `main`
- Latest implementation: reliable parallel Kiloview N6/N60 onboarding and identity cards
- Current version: `0.8.1`
- Release channel: `Main`
- Latest published release: <https://github.com/JohnDevAc/Kiloview-Job-Configurator/releases/tag/v0.8.1>
- Next target release: to be assigned after `v0.8.1`
- Current active work: none.
- The companion source and packages were moved to the sibling
  `Kiloview PC Onboarding` project. Do not copy them back into this repository.
- Companion repository: <https://github.com/JohnDevAc/Kiloview-PC-Onboarding>
  (private, default branch `main`, initial commit `f8f56c3`).

All requested application work through `v0.8.0-dev.53` has been promoted to stable `v0.8.1`.

## Stable 0.8.1 promotion

- `main` includes the fully validated `v0.8.0-dev.53` implementation.
- Shared release metadata uses stable version `0.8.1` and the `Main` update channel.
- The Development prerelease remains available as `v0.8.0-dev.53`.

## Dev.34 uniform Windows endpoint cards

- Local and remote Windows endpoint cards now share one pill renderer and the
  same order: availability, preferred NDI interface, adapter, NDI version, job
  group, multicast allocation, and TTL.
- `LocalPcEndpoint.NdiToolsVersion` is populated from the installed NDI runtime
  so the local card can show the same NDI version pill as remote endpoints.
- Remote connectivity and local readiness retain their distinct live status
  logic, while their visual vocabulary is now consistent.

## Dev.33 multicast preview firewall and Windows card labels

- Both adopted TeleTool previews failed together after multicast was enabled while
  Studio Monitor continued receiving the feeds. The embedded NDI finder saw both
  sources, but its receiver obtained no video frames.
- Root cause: the installed executable had no inbound UDP allow rule for the
  active Private profile. The existing installer rule covered only TCP `8091`;
  an unrelated Windows-generated UDP rule covered Public only.
- Adding `Kiloview Job Configurator NDI` for the installed executable, inbound
  UDP, Domain/Private profiles, and `LocalSubnet` immediately restored both
  previews with zero capture failures.
- The installer now provisions that restricted rule and the uninstaller removes
  it alongside the LAN web rule.
- Local and remote Windows card headers now use `Windows · <OS version>`.
  `POST /api/pc-onboarding/register` accepts the optional JSON field
  `operatingSystemVersion`; the companion project should send
  `RuntimeInformation.OSDescription` (or an equivalent friendly Windows version)
  on its next update. Older registrations remain valid and display
  `VERSION UNKNOWN` until they register again.

## Dev.32 TeleTool temperature integration

- The existing TeleTool `/api/status?lite=1&stats=1&logs=0&rf=1` response now
  supplies `system_temperature_c`; no extra request or polling loop is needed.
- `ManagedDevice.SystemTemperatureC` stores the validated value, rounded to one
  decimal place and limited to a plausible `-40°C` to `150°C` range.
- Every TeleTool monitor card shows a compact temperature pill beside Dante and
  RF: green below `70°C`, amber from `70°C`, red from `80°C`, and neutral when
  unavailable. Text and tooltips accompany the colours.
- Temperature-only changes are excluded from the card render signature and the
  pill is updated in place, preventing unnecessary preview/card reconstruction.

## Dev.31 local-PC card relocation

- The local Windows PC card no longer appears in the first setup form.
- It now appears on the second onboarding/device-selection screen under
  **Windows NDI Endpoints**.
- The endpoint uses the same `device-grid` and standard `device-card` structure
  as discovered Kiloview and TeleTool units, so its width and responsive
  breakpoints match the other cards.
- The card retains endpoint identity, address, adapter, preferred-interface
  state, actionable readiness errors, and the readiness recheck action.

## Dev.31 remote Windows endpoint monitoring

- The existing 15-second device monitor now pings registered remote Windows NDI
  endpoints concurrently, without requiring the companion utility to remain
  running as a service.
- Remote endpoint cards show **Online**, amber **Checking**, or **Offline**.
  Three consecutive failed checks are required before an endpoint is declared
  offline; any successful reply resets the failure count and refreshes
  `LastSeenUtc`.
- Timestamp-only health updates are excluded from the monitor card signature so
  successful checks do not rebuild every card and interrupt live previews.
- The Windows Onboarding App task was asked to create an idempotent inbound
  ICMPv4 Echo firewall rule on the Private profile, restricted to the Job
  Configurator IP or selected subnet, during successful onboarding. It must not
  add a background service.

## Dev.30 local-PC onboarding preflight work

- The first onboarding section now includes a live card for the local Windows
  NDI endpoint beneath the primary adapter selector.
- The card shows hostname, address, adapter, preferred-interface state, and any
  actionable intervention. Individual Access Manager, NDI client, and
  Discovery Server status badges are intentionally omitted.
- `GET /api/ndi/preflight` reports running configuration-consuming NDI client
  applications.
- Applying the preferred interface, starting onboarding, applying multicast,
  and reverting multicast now stop when Access Manager or a recognised NDI
  client application is open.
- Exact NDI 6 process names are covered, including:
  - `Application.NdiGroupEditor` for Access Manager;
  - `Application.NDI.DiscoveryService.UI` for the separate Discovery client;
  - the current Studio Monitor, Screen Capture, Bridge, Router, Webcam, Test
    Patterns, and Analysis executables.
- `NDI Discovery Service` is intentionally informational rather than blocking:
  the Discovery Server must remain running while devices are onboarded.
- Validation completed:
  - frontend syntax, .NET formatting, and Release build passed;
  - the live preflight identified the local Discovery Server as running while
    returning `ready: true` when no client applications were open;
  - the card rendered without horizontal overflow at `1920×1080` and
    `640×900`.

## Dev.29 local NDI group replacement work

- Starting a confirmed onboarding plan now replaces the previously managed
  local NDI send and receive group with the new Job Name.
- Unrelated Access Manager groups are preserved and duplicates are removed.
- The selected interface and NDI Discovery Server are reapplied and the saved
  configuration is read back before any device is modified.
- The managed group is persisted as `ManagedLocalNdiGroup`; older state falls
  back to the previous `LastJob.JobName` for its first upgraded onboarding.
- Concurrent start requests are serialized so two plans cannot race while
  changing the local NDI configuration.
- NDI Access Manager must be closed. A write or verification failure stops
  onboarding before device configuration begins.
- Two isolated full simulated onboarding runs were completed successfully:
  - `Public,OldManagedJob1,UserCustom` became
    `Public,UserCustom,NewManagedJob1`;
  - the next run became `Public,UserCustom,NextManagedJob2`;
  - both runs completed all 8 steps with zero errors, removed the tracked
    previous group, and retained the unrelated groups.

## Dev.29 installer DPI work

- The EULA window now uses nested table/flow layouts rather than fixed body
  coordinates.
- The installer is resizable and maximizable, with a DPI-scaled minimum size.
- Before it becomes visible, the window is constrained and centred within the
  active monitor's working area.
- The licence text is the only area that contracts; acceptance and action
  controls remain visible.
- Per-Monitor V2 is declared through the supported WinForms project property
  and reinforced at process startup.
- Windows 10/11 compatibility is declared in the UAC manifest.
- Validation completed:
  - bootstrapper Release build: zero warnings/errors;
  - `dotnet format --verify-no-changes` passed;
  - the live EULA window reports per-monitor DPI awareness;
  - normal `836×739` and compact `640×520` renders were inspected with no
    clipping, overlap, or hidden controls.

## Dev.28 Windows endpoint work

- Main application additions retained in this repository:
  - `GET /api/pc-onboarding/profile`;
  - `POST /api/pc-onboarding/register`;
  - persisted `RemoteWindowsPcs` records;
  - remote Windows NDI endpoint cards in the monitor.
- Registration rejects a reported address that does not equal the connecting
  IPv4 client and rejects an old/missing EULA acceptance.
- Remote Windows endpoint cards can be removed from the job. The removal
  deletes only the remote registration record and never appears on the
  protected local-PC card.
- Each remote Windows endpoint receives a unique reserved `/28` sender range in
  multicast plans. Its card displays the manual NDI Access Manager values; the
  configurator does not claim to apply or verify remote settings.
- Validation completed:
  - main Release build: zero warnings/errors;
  - `dotnet format --verify-no-changes` passed;
  - frontend JavaScript syntax and Git whitespace checks passed;
  - an isolated two-PC multicast test produced two distinct `/28` ranges;
  - applying the test plan marked both remote ranges `reserved`, with no false
    remote-application claim;
  - removing a test endpoint also removed only its stored multicast assignment.

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

For `v0.8.0-dev.32`:

- `dotnet format --verify-no-changes` passed.
- Main Release build passed with zero warnings and zero errors.
- Frontend JavaScript syntax, release metadata, and Git whitespace checks
  passed.
- Isolated runtime checks confirmed that a reachable remote Windows endpoint
  stays online, the first two missed replies report an amber checking state,
  and the third consecutive failure reports offline.
- End-to-end live checks through the Fleet Manager snapshot path returned valid
  TeleTool temperatures of `57.3°C` and `58.4°C` while both devices remained
  online.
- The live preflight detected the installed NDI 6 Access Manager, allowed the
  running Discovery Server, and returned ready when no client applications
  were open.
- The local-PC card had no horizontal overflow at `1920×1080` or `640×900`.

## Release procedure

Development releases must:

1. Be made from `development`.
2. Increment the version in `Directory.Build.props`.
3. Use a tag such as `v0.8.0-dev.30`.
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
