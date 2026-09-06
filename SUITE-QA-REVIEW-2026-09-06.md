# Suite QA review — 6 September 2026

**Recommendation: do not release the current validation builds yet.** This pass found **seven actionable issues: two P1 and five P2**. Six were reproduced with isolated fixtures; the Resolume finding is established by tracing the mutation sequence. The existing automated suites pass despite these gaps.

This is a report before further implementation. No application code, existing tests, configuration, version metadata or tracked package was edited during this pass. All five Git repositories and deployments remain separate. Earlier uncommitted work, including the pre-existing Resolume changes, was preserved.

## Scope and baseline

Reviewed the working-tree implementation, relevant callers, contracts, handoffs, prior review and validation evidence across all five repositories. Focus: client-only PCs using remote services, a management-only Job Configurator, combined hosts, account ownership, package-source gates, interrupted installation and state agreement between applications.

| Application | Branch | HEAD reviewed |
| --- | --- | --- |
| NDI Job Configurator | development | cdd6f712d079 |
| NDI Configurator PC Agent | dev | c651f1ef8bc7 |
| Kiloview Environment Setup | main | 2b73181436d4 |
| Production Toolkit | main | ec9a257ec541 |
| Resolume Configurator | main | e18bb7ae7227 |

These are dirty working-tree snapshots, not newly released versions. The earlier implementation report's “addressed” matrix should not be treated as release acceptance: several fixes need the follow-up below.

P1 means fix before distributing these builds. P2 means a concrete failure in a supported lifecycle or deployment case that should be fixed in the same interoperability work.

## Findings

### QA01 — P1: PC Agent rejects the firewall rules it has just created

**Affected:** Client setup, remote PC onboarding and server-local PC onboarding.

[The writer](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/AgentInstallationService.cs:327>) now sets LocalAddresses to `*`, RemoteAddresses to `LocalSubnet`, and adds interface/application scope. [Verification](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/AgentInstallationService.cs:355>) still receives the individual address and numeric CIDR from ConfigureLanRules. [The verifier](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/AgentInstallationService.cs:522>) demands equality with those old scopes; its parser cannot treat either new symbolic value as the old address/CIDR.

**Evidence:** the actual compiled private verifier accepted the previous literal scopes and rejected a fake rule containing precisely the new scopes. No Windows Firewall call was needed. Consequently, a normal successful rule creation reaches the “Windows did not retain…” exception. The first rule can be left installed while the second is never created, and InstallOrUpdate returns failure. Remote onboarding then enters recovery.

**Fix:** make rule creation and verification use the same explicit expected policy, including executable path, interface, direction, profiles, ports, edge traversal and both address scopes. Verify the symbolic scopes as symbolic scopes. Add a regression exercising the writer's resulting values through the real verifier, including legitimate Windows normalization and intentionally wrong interfaces/programs.

This is the principal release blocker: download gating can succeed while the subsequent Client installation still fails.

### QA06 — P1: Server uninstall bypasses the Windows-account ownership gate

**Affected:** combined or server hosts where a standard user supplies a different administrator's UAC credentials.

[The ownership gate](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1788>) is used by server download/preparation paths. [Uninstall-Suite](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:2383>) does not invoke it or verify the recorded server owner. It reads the elevated account's WSL inventory, then removes machine-wide startup tasks, configuration, maintenance entry and role receipt.

**Evidence:** invoking the real uninstall function with record-only mocks, an alternate-account/empty WSL inventory and an owner gate that would reject the operation still completed. It removed the global task/configuration records without ever calling the gate. The original owner's WSL instance and container are outside that account's inventory, so removal can report success while leaving that instance behind and deleting its management metadata.

**Fix:** validate the installation owner before any destructive server maintenance operation. Persist and check the server installation SID, rather than relying only on the current desktop SID. For unsupported cross-account removal, stop before changing tasks/services/configuration and explain which account must perform removal. Keep Client setup's separate desktop-user handling.

**Required regression:** install under account A, invoke removal under administrator B, and prove zero mutations before rejection. Also verify ordinary same-owner removal and removal on a combined host.

### QA04 — P2: Lost registration acknowledgements can split the server and PC state

**Affected:** remote onboarding when registration reaches the server but its responses are lost.

[Registration](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/JobConfiguratorDiscovery.cs:58>) retries the same request three times. The server correctly accepts matching retries. However, if all responses are lost, [the client](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/RemoteOnboardingService.cs:73>) treats the last transport failure as a definite failure and [restores its previous configuration](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/RemoteOnboardingService.cs:89>), although [the server](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Program.cs:320>) has already persisted the new registration.

**Evidence:** a fixture used the real client registration/retry and transaction code, plus the real server validation and isolated state store. The transport committed each accepted registration and then injected a lost response. After three sends, the server retained the new registration while the client file had been restored to the previous job. The client reported successful restoration; the server-side attempt had completed. Later monitoring may expose drift, but it does not reconcile the registration transaction.

**Fix:** distinguish explicit rejection from an unknown commit outcome. Persist the attempt's recovery state on the PC, provide a receipt/status reconciliation path, and reconcile after connectivity returns. If local rollback is necessary, persist and retry an attempt-specific compensation/abort report. Keep the server status provisional or explicitly unresolved until the final state is known.

**Required regression:** lose the first acknowledgement, lose every acknowledgement, restart either side during recovery, and restore connectivity after local rollback. Both sides must converge without accepting an old job or silently describing different outcomes.

### QA02 — P2: Recovery can become stuck on an unchanged, in-use Setup executable

**Affected:** an update/repair while installed Setup is still open, followed by recovery from that installed utility.

[Recover](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/PackageInstallation.cs:133>) overwrites every original named in the journal, including a Setup file that the failed replacement never changed. Windows can reject replacing an in-use executable. [The next installation attempt](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/AgentInstallationService.cs:66>) performs recovery before proceeding, so it encounters the same lock.

**Evidence:** a Windows file-sharing fixture held Setup open without delete sharing, allowed the Agent replacement, and caused the Setup replacement to fail. The original pair was already intact after the rollback attempt, but the journal remained. A second recovery failed again trying to overwrite the unchanged Setup. This models an executable lock; no installed executable was launched or replaced.

The tray also [clears its pending-launch flag](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/Agent/AgentApplicationContext.cs:231>) after starting Setup, rather than when that process exits, so update availability is not held off for the entire remote operation.

**Fix:** serialize updating against the lifetime of active Setup/onboarding processes. Make recovery idempotent by skipping destinations already matching the verified backup, and arrange external recovery when the running executable actually needs replacement. Publish the recovery journal atomically. Keep restart/recovery decisions inside the installation lock; the current outer catch can attempt to start Agent after lock acquisition itself fails.

**Required regression:** real file-sharing locks, unchanged locked destinations, an interrupted mixed pair, concurrent installers, lock timeout and recovery launched from the installed utility.

### QA05 — P2: Removing the Server role leaves Discovery configured to restart

**Affected:** converting a combined host to Client-only or removing local server components while retaining shared NDI Tools.

[Server installation](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1911>) sets the Discovery service to Automatic. [Uninstall](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:2411>) stops it but leaves that startup setting and its Discovery configuration in place.

**Evidence:** the real uninstall function, using a service fixture initially Running/Automatic, finished with the service Stopped/Automatic. It removed the Server role receipt and firewall rules. On a subsequent Windows start, the automatic service remains eligible to run again.

**Fix:** record whether this installation created or took ownership of the Discovery service and its prior settings. On Server-role removal, unregister or disable the owned server service/task as appropriate, or restore pre-existing settings. Preserve NDI Tools and PC Agent files used by clients. Verify Discovery stays stopped after reboot while client applications still work.

This requires service ownership handling, not removing the shared NDI runtime.

### QA03 — P2: A known Discovery Server address is accepted as a PC static target

**Affected:** deployments with Discovery on a separate machine not represented in the device/PC inventory.

[Static target checks](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/WindowsPcRemoteOnboardingService.cs:49>) exclude inventoried devices, other PCs, pending reservations and the selected Job Configurator address. They do not exclude the active job's known Discovery Server address.

**Evidence:** the actual staging service accepted a fixture PC whose requested static address exactly matched LastJob.NdiDiscoveryServerIp. No packets were sent and no address was applied.

Preferred-address/duplicate-address detection on the PC is useful mitigation, but does not justify approving a known infrastructure collision. It can still cause an unnecessary network change and rollback; an offline infrastructure host may not answer a conflict probe.

**Fix:** reserve known job infrastructure addresses, including Discovery and applicable KiloLink/server interfaces, before staging. Check configured gateways and other known infrastructure where applicable. Retain endpoint duplicate-address detection and bounded recovery; probes supplement the known-address reservations.

**Required regression:** remote Discovery with no inventory entry, Discovery temporarily offline, combined-host Discovery, and an unrelated safe static target.

### QA07 — P2: Resolume can restore a composition after its last job check has become stale

**Affected:** a job or local NDI configuration change while Arena is restarting.

[The worker validates job identity](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/PostRestartWorker.cs:16>), then [calls restoration](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/PostRestartWorker.cs:39>). Restoration can [wait another 60 seconds](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/PostRestartWorker.cs:51>) for Arena before [changing clip fit, connecting routers and saving](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/PostRestartWorker.cs:72>). The next job check is after restoration. LocalNdiReadinessService is not called by the restart worker.

**Evidence:** code trace. For example, the job check succeeds, Arena is still loading, the operator changes the job, and Arena becomes ready. Restoration writes to Arena before the subsequent guard detects the change. The existing identity tests do not drive this delayed restoration path. No live Arena mutation was attempted in this QA pass.

**Fix:** carry the expected identity/validation callback into restoration, refresh after the readiness wait and before mutating phases, and recheck the local Agent/NDI readiness after restart. Similarly, keep decoder checks close to their writes after potentially lengthy source discovery. This reduces a substantial avoidable window without claiming atomic transactions across independent applications.

**Required regression:** delayed Arena readiness with a changed job, changed local NDI groups/Discovery address, a stopped Agent, and unchanged healthy readiness. The stale cases must issue zero restoration mutations.

## Download gating and topology assessment

The current source-gating approach is substantially improved: actual package sources are checked, HEAD rejection has a fallback, HTML package responses are rejected, downloads remain size/hash/signature checked, and the C# transfer readers have stalled-read deadlines. Environment stages both Client packages before starting either installer. Toolkit can reuse a verified cached package.

No additional reproduced package-source gating defect was found in this pass. That does **not** mean all installations succeed: QA01 prevents the subsequent PC Agent configuration step.

Retain these boundaries in the fixes:

- A Client-only machine must need only its selected client packages and local PC Agent configuration. Job Configurator, Discovery and KiloLink may all be remote.
- A complete local Job Configurator/companion bundle must not gain an unnecessary internet requirement.
- Cached release metadata is not a cached installer. A verified installer is not proof its downstream prerequisites are cached.
- Fresh Environment Server setup still requires its Windows/WSL/Linux sources; the explicit offline bundle currently covers Client packages. WSL-context checks follow prerequisite availability, and a later failure does not roll back every prerequisite.
- Combined-host removal must remove server ownership without removing shared client runtime files or leaving a local server enabled.

Further acceptance should cover separate internet and production NICs, a proxy usable by only one Windows account, a captive portal, source loss after preflight, an incomplete offline bundle and verified offline cache reuse. Keep failures actionable and allow retry without requiring internet for unrelated LAN operations.

## Additional improvements to include in the fix plan

These are smaller hardening items rather than additional release blockers:

- **Toolkit configuration evidence:** [the configured predicate](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Wrapper Application/src/ToolkitLauncher/Diagnostics/Read-Environment.ps1:27>) accepts an empty GUID and does not validate schema/address. Its model can consequently report a Client installation as Full for that malformed receipt. Align structural validation with Environment/Agent, and report adapter availability separately from installation completeness.
- **Attempt timing:** separate the local approval/UAC window from the configuration/registration deadline. The fixed registration timer starts on HTTP 202, before the elevated flow completes its preflight, installation-lock wait and network operation. Add a slow-but-valid operation test.
- **Identity persistence:** publish server-id.txt atomically and test interrupted first creation and migration from legacy state. Persist the new identity in the credential-bearing state snapshot during migration so the public API and local reader agree immediately.
- **Test boundaries:** add executable seams around firewall policy verification, process ownership/lifetime, restart readiness and acknowledgement failure. Pure model tests and successful package builds cannot substitute for these paths.

## Proposed implementation order

| Step | Changes | Completion evidence |
| --- | --- | --- |
| 1 | QA01 firewall writer/verifier agreement; QA06 owner check before server removal | Writer-to-verifier regression; wrong-owner uninstall produces zero mutations |
| 2 | QA02 complete-pair recovery and process/lock ownership | Locked-file, active Setup and concurrent-installer cases recover without a stuck journal |
| 3 | QA03 infrastructure reservations; QA04 durable outcome reconciliation and timeout handling | Safe isolated end-to-end attempt tests, including lost acknowledgements and restart |
| 4 | QA05 Discovery service ownership/removal; Toolkit evidence consistency | Server-to-Client conversion and reboot preserve client runtime while keeping Discovery off |
| 5 | QA07 job and local NDI validation at restart/restoration writes | Delayed-readiness tests prove no stale mutations |
| 6 | Identity migration hardening; full validation and controlled deployment acceptance | All automated suites pass plus the relevant real-machine cases below |

Implement within each owning repository. Keep the schema-1 local process contract, remote Yes/No and UAC, independent versions/updaters and separate release ownership. Rebuild packages only after the fixes; release the companion independently before building a server package against its clean checkout.

## Validation performed during this pass

| Check | Result |
| --- | --- |
| Job Configurator regression | 21/21 passed |
| Job Configurator frontend | 7/7 passed |
| Job Configurator installer | Version/retention/manifest/corruption checks passed |
| Companion Setup validation | 14 PASS groups |
| Companion Agent validation | 21 PASS groups |
| Environment installer regression | 82/82 passed |
| Toolkit regression | 141 assertions passed |
| Resolume regression/WPF | 34/34 passed |
| Isolated server-to-Resolume HTTP contract | Passed identity, origin and same-name replacement checks |
| New targeted C# QA fixtures | Reproduced QA01–QA04 |
| New targeted Environment QA fixtures | Reproduced QA05–QA06 |
| Git whitespace checks | Passed in all five repositories |

Logs and reproducible QA harnesses: [QA artifacts](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/qa-review-2026-09-06>). [The repository audit](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/qa-review-2026-09-06/repository-audit.json>) records unchanged branches, HEADs and hashes for the existing dirty/untracked files. The report itself is the only new non-ignored workspace file from this pass.

Toolkit's earlier 256-assertion UI review and the previous package builds remain historical evidence; they were not rerun or repackaged here. This pass reran the 141-assertion Toolkit regression command. Resolume's normal test command includes its WPF regression coverage.

**Fixture execution note:** during development of the uninstall fixture, an unmocked Hyper-V firewall removal cmdlet returned Windows “Access is denied.” That invocation failed. It was replaced with a record-only mock before the successful rerun; no successful live firewall mutation was reported. The finalized fixture mocks every removal command it exercises.

**Remaining acceptance limits:** no application installation, production onboarding, real network reconfiguration, UAC account-switch exercise, logoff/reboot, or live Arena/decoder operation was performed. Use controlled machines for those checks after the fixes. Passing automated tests is necessary, but the reproduced setup failure means the current builds are not ready to distribute.

