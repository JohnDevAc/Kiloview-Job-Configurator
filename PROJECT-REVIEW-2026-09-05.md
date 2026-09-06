**NDI Job Configurator project review — 5 September 2026**

Reviewed `development` at `eecd131`, version `0.8.0-dev.97`. Six functional issues were reproduced with synthetic data in a temporary harness. The initial review left source unchanged; the subsequently requested fixes are implemented for release `v0.8.0-dev.98`. Existing untracked files were preserved.

**Fix status — 5 September 2026**

All six findings below are fixed. Clean onboarding verifies local NDI readiness
before deleting inventory; monitoring compares Windows capability contents and
rejects stale device snapshots while preserving credentials and license metadata;
incremental plans reserve hostnames; Device UI sessions use unique `.localhost`
origins; and monitoring preserves partial unicast reversions and operations in progress.

The three optimizations are implemented: Windows metrics update existing cards,
state reads reuse immutable snapshots with reload and corruption recovery, and
previews cache source discovery, back off failed captures, and skip offscreen or
hidden refreshes. Native receivers remain short-lived; receiver reuse was not
introduced without a hardware performance comparison.

Repository regression coverage now lives in `tests/Regression/Program.cs`
and `tests/frontend.test.mjs`. The backend
suite contains 13 checks and the frontend suite contains five. A browser also
successfully opened a generated `.localhost` gateway against a local mock device
with the default `127.0.0.1` listener. These checks do not measure native NDI
throughput or replace physical-device acceptance testing.

Validation after the fixes:

- All 13 backend checks and five frontend checks pass, including recovery from
  an interrupted configuration and collisions between sanitized job names.
- Application and installer Release builds pass with zero warnings and errors.
- All five frontend JavaScript modules pass syntax checks.
- Release metadata validation and Git whitespace checks pass.

Published as [v0.8.0-dev.98](https://github.com/JohnDevAc/Kiloview-Job-Configurator/releases/tag/v0.8.0-dev.98)
from commit `57a1fdb` on `development`. The installer, ZIP, legacy updater alias,
and checksum file all match GitHub's reported sizes and SHA-256 digests. The
isolated application and a harness using the installed `0.8.6` identity both
resolved the new Development release through the production updater code. The
local installed application remains at `0.8.6`; this deployment published the
release for download and update-channel distribution.

The findings and line references below describe the original reviewed revision.

1. **[P1] Run local readiness checks before clean onboarding deletes inventory.**

   [OnboardingService.cs:148](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/OnboardingService.cs:148>) calls `PrepareCleanOnboardingAsync` before `ApplyLocalNdiJobAsync`. Cleanup deletes KiloLink inventory for physical Kiloview plans and removes prior devices, the last job, multicast records, and remote endpoints from local state. Only afterward does the application resolve the selected adapter and check whether NDI configuration can be applied.

   Reproduction: start a valid synthetic clean plan with a missing selected adapter. Start throws the adapter error and progress remains `idle`, but the previous device and job have already been deleted. Move adapter/application/configuration preflight ahead of cleanup, preserve the previous managed group before clearing `LastJob`, and test that preflight failure leaves inventory intact. KiloLink deletion was inspected in source; the reproduction used only synthetic local state.

2. **[P1] Compare Windows endpoint snapshots by value or revision.**

   [DeviceMonitor.cs:256](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/DeviceMonitor.cs:256>) compares whole `RemoteWindowsPcEndpoint` records. Their `AgentCapabilities` property is an `IReadOnlyList<string>`, which participates in record equality by reference. `AppStateStore` deserializes the file independently for the poll snapshot and the later update, so the capability lists are different objects even when no state changed.

   Reproduction: persist an endpoint with capabilities, read state twice, then apply a successful poll. The two unchanged endpoint records compare unequal and the new uptime is discarded. Failed polls are discarded through the same branch, so an endpoint registered as online can remain online after disconnecting; multicast status updates are also skipped. Compare relevant scalar fields plus capability contents, or use a persisted revision to reject stale results.

3. **[P1] Prevent old monitor results from replacing new credentials.**

   [DeviceMonitor.cs:500](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/DeviceMonitor.cs:500>) checks address, role, names, and onboarding flags but omits credentials, license state, and configuration state. If a poll starts before access provisioning and finishes after the new credentials are saved, it passes this check and replaces the entire current device record with its older result.

   Reproduction: apply an old successful poll to a current record containing new credentials, `LicenseAccepted=true`, and `Health=Configuring`. The old credentials replace the new ones, license acceptance becomes false, and health becomes online. A subsequent failure during onboarding could leave the configurator with obsolete credentials for a device whose password was already changed. Merge only observed fields into the current record and reject results from an older configuration revision; serialize monitoring with disruptive changes where necessary.

4. **[P2] Reserve existing hostnames when adding devices.**

   [OnboardingService.cs:70](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/OnboardingService.cs:70>) resets the Kiloview and TeleTool counters to zero for every plan without checking the retained fleet. Adding devices under the same Job Name therefore reuses existing names.

   Reproduction: retain an onboarded `ReviewJob2-KV-001` and build a plan for one additional device. Its planned hostname is also `ReviewJob2-KV-001`. Reserve existing names and allocate an unused suffix for each family. Cover both incremental additions and distinct Job Names that sanitize to the same hostname prefix.

5. **[P2] Make Device UI gateway addresses reachable in localhost mode.**

   [Program.cs:34](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Program.cs:34>) binds the default server only to `127.0.0.1`, while [DeviceUiGatewayService.cs:125](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/DeviceUiGatewayService.cs:125>) allocates random `127.x.y.z` addresses for authenticated UI sessions. Requests to those addresses cannot reach the listener in the documented default development mode.

   Reproduction: start Kestrel with the same localhost binding. The primary address responds, but an alternate loopback address times out. Keep the gateway's browser-origin isolation while providing reachable listeners or loopback-resolving hostnames. Test the default mode as well as the installed LAN mode.

6. **[P2] Preserve successful unicast reversions during monitoring.**

   [DeviceMonitor.cs:205](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/DeviceMonitor.cs:205>) validates every retained TeleTool multicast assignment as though multicast were still desired. A partial revert deliberately retains successful assignments with `Status="unicast"` while another endpoint awaits retry, but this branch ignores that status.

   Reproduction: poll a successfully reverted TeleTool in a `revert-partial` plan. Its assignment changes from `unicast` to `drifted`, gains an error instructing the operator to reapply multicast, and the overall plan changes to `partial`. Check the desired transport and operation state before detecting drift; successful reversions should remain successful while failed endpoints are retried.

**Practical optimizations**

- **Update Windows metrics without rebuilding all cards.** [app.js:406](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/wwwroot/app.js:406>) excludes two timestamps from its render signature, but retains uptime, memory, disk metrics, `agentObservedUtc`, and the complete unregistered-agent snapshots. Those routine changes cause the entire grid to be replaced at line 408, including encoder preview elements. Evaluating the actual signature expression confirmed that both a metric-only change and an unregistered-agent timestamp change trigger a rebuild. Update these fields in place, like TeleTool temperatures, and rebuild only cards whose structure changed.

- **Cache the current state snapshot for frequent reads.** [EncoderThumbnailService.cs:30](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/EncoderThumbnailService.cs:30>) reads and deserializes the full state before checking its image cache. At a five-second preview interval, N visible encoders cause N full state reads per cycle, plus normal monitoring and API reads. Maintain an immutable in-memory snapshot updated under the existing write gate, with an explicit reload policy if external state edits must be supported. Preserve atomic writes and corruption recovery. This is a code-path observation, not a measured throughput improvement.

- **Reduce failed preview work and repeated discovery.** [EncoderThumbnailService.cs:199](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/EncoderThumbnailService.cs:199>) creates a finder per capture; each attempt can spend five seconds discovering and another six seconds waiting for video, with only four captures allowed concurrently. Multiple unavailable sources can delay otherwise healthy previews. Cache source resolution, back off repeated failures, and prioritize visible healthy sources. Benchmark receiver reuse against its additional continuous NDI processing cost before changing that lifecycle.

**Verification and limits**

- Main application and installer Release builds passed with zero warnings and zero errors.
- Both `dotnet format --verify-no-changes --no-restore` checks passed.
- All four frontend JavaScript modules passed syntax checks.
- Installer and release PowerShell scripts parsed successfully.
- Release metadata validation and `git diff --check` passed.
- The isolated harness reproduced all six functional findings. A separate evaluation of the actual frontend signature reproduced the two grid-rebuild triggers.
- A 32 MB multipart upload through a local test host using the firmware staging service and the production request-size metadata returned HTTP 200.

The harness and its synthetic state are outside the repository in [the temporary review directory](<C:/Users/jligh/AppData/Local/Temp/ndi-project-review-840ad1546f964105aa680c12c5276b34>). The harness invokes production classes from the Release assembly, uses reflection for private monitor merge logic, and uses a separate local Kestrel host for HTTP checks. It does not constitute a full browser or hardware acceptance test.

No physical device, installed application, live NDI configuration, firmware, or KiloLink inventory was changed. Earlier repository reports describe N60 multicast traffic outside its assigned range and decoder blanking failures; those historical observations require current hardware verification and are not counted as newly reproduced findings here.
