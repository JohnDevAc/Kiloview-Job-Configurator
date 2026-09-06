# QA corrections and implementation evidence — 6 September 2026

All seven findings in [the latest QA report](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/SUITE-QA-REVIEW-2026-09-06.md>) and its four additional recommendations were implemented across the five independent repositories. The earlier implementation was committed before these corrections began. These corrections have now also been committed in each repository; [the follow-up report](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/QA-FOLLOWUP-2026-09-06.md>) records those checkpoints and additional fixes. Nothing was pushed, published or installed.

## Baseline checkpoints

| Application | Branch | Commit of the preceding implementation |
| --- | --- | --- |
| Job Configurator | development | `3dd2ccb61eb1cac21aced7554e5548a31da99b95` |
| PC Agent and Setup | dev | `19a70fc5a10696d68140b12538181ed200c6632d` |
| Environment Setup | main | `cc44b42e6974697baa248f1e4264a3ce28a81ef6` |
| Production Toolkit | main | `580efc4d5091c20aef3045a76234a58251ee1d62` |
| Resolume Configurator | main | `e62c2dbda5b6e68cefb89995c022281a752fc715` |

Unrelated multicast reports, scratch directories, remote attachments and the Environment scratch handoff were preserved outside these commits. Repository roots, branches, Git histories, version ownership and separate deployments remain intact.

## Finding coverage

| Finding | Implemented behavior | Evidence |
| --- | --- | --- |
| QA01 — firewall verification | Writer and verifier now consume one policy: installed executable, selected interface, all profiles, `*` local binding, `LocalSubnet` peers and the exact protocol/port. | Real policy writer-to-verifier test accepts normal COM representation and rejects wrong application, scope, interface, profile, direction, action, port and protocol. |
| QA02 — locked Setup and recovery | Backups and staged payloads are flushed before atomically publishing the hashed journal. Recovery skips unchanged locked files. A changed running Setup defers to a protected external helper that waits for the original process. A process-lifetime Setup lease covers asynchronous onboarding and updater contention. Failed recovery cannot restart a mixed pair. | A real locked file forces replacement failure and restores the prior pair. Tests cover interrupted journal publication, deferred running-Setup recovery, an external recovery pass, and competing lease acquisition. |
| QA03 — infrastructure collision | Static PC targets exclude every known local server interface, current/requested gateways, remote Discovery and KiloLink, inventory, agents and unresolved attempts. Unknown outcomes retain their address reservation after timeout until reconciled or superseded by fresh onboarding of that PC. | Cross-repository tests reject known infrastructure without probes, preserve expired unresolved reservations, and accept a safe target. |
| QA04 — acknowledgement ambiguity | Registration persists a provisional candidate. Only a durable final completion adds the PC to the job. Lost registration responses cause rollback plus durable abort; lost final confirmation preserves applied settings and retries. Both sides retain receipts across restarts. Interrupted local work reports repair rather than inferred success. | Real client retry/transaction/outcome code exercises the real server service and isolated persisted state through an intercepted HTTP transport: all registration responses lost, one response lost, final response lost, restart on both sides, stale jobs, expiry, unfetched authorization, repair and wrong-source rejection. |
| QA05 — Discovery after removal | Ownership snapshots restore prior service startup, delayed Automatic flag, running state, task XML/state and exact configuration. Newly managed Discovery is disabled. Shared NDI Tools/Agent remain installed. Client-only removal performs no server mutation. Restoration progress makes partial-uninstall retries idempotent. | Isolated removal tests cover new service disablement, prior Manual and delayed Automatic restoration, byte-exact configuration, corrupt snapshots, client-only removal and repeated restoration. |
| QA06 — wrong Windows owner | Owner SID is recorded before server mutations and retained until uninstall completes. Server maintenance/resume/removal require that owner's signed-in administrator desktop. Legacy task ownership can migrate; ambiguous ownership fails closed. | Wrong-owner uninstall/resume fail before mutation; same owner and legacy owner pass. Client setup retains server ownership, and interrupted removal retains evidence for retry. |
| QA07 — stale post-restart writes | Current job and local Agent/NDI readiness are checked after restart waits and immediately before Arena mutations, including internal clip waits. N6/N60 preset changes and activation revalidate after source discovery. | Delayed Arena/clip fixtures reject changed job, changed Discovery or unavailable Agent with zero configuration writes; a mid-restoration change stops subsequent writes. Both decoder families reject stale source lookup results before preset changes. |

## Additional recommendations

Toolkit now requires schema 1, valid nonempty endpoint/adapter GUIDs, usable unicast IPv4 and a /1–/30 prefix. Malformed state cannot report a complete Client installation. Live adapter/address availability is displayed separately, so an unplugged adapter does not imply missing software or require local server components.

Approval/UAC has a ten-minute attempt lifetime. The server's five-minute registration window starts only on the first configuration fetch. The companion bounds execution to four minutes after fetch and retains a separate recovery deadline. A four-minute approval delay followed by three-and-a-half minutes of configuration remains valid in the controlled clock test. Retries cannot extend the first-fetch deadline.

Server identity is published atomically under a process lock. Interrupted first creation and malformed identity files recover from persisted identity where available; legacy credential snapshots receive the same identity/revision before the first API read returns. Concurrent initialization is tested.

Tests now have executable seams for firewall policy, Setup lifetime and locked files, final outcome transport, owner-specific removal, and restart/decoder mutation boundaries. Environment fixtures explicitly reject unmocked service, firewall, WSL, process and task mutations.

## Validation results

| Suite | Result |
| --- | --- |
| Job Configurator regression | 22/22 passed |
| Job Configurator frontend | 8/8 passed |
| Job Configurator installer | Version ordering, complete manifest, retention and corruption checks passed |
| PC Agent Setup validation | 17 PASS groups |
| PC Agent runtime validation | 21 PASS groups |
| Cross-repository onboarding outcomes | 8 scenarios passed |
| Environment installer regression | 92/92 passed |
| Toolkit regression | 155 assertions passed |
| Toolkit complete build with UI validation | 270 assertions passed; installer built |
| Resolume regression and WPF | 36/36 passed |
| Server-to-Resolume HTTP contract | Identity, origin boundary and same-name replacement checks passed |

Package validation built the companion through its own publish script, the server self-contained installer with that complete companion package, the Environment executable, Toolkit installer and Resolume installer. The companion bundle records version, baseline commit, `workingTreeChanges: true` and file hashes. These are local validation artifacts using existing version metadata, not new releases.

Logs and package evidence are retained in [QA fix artifacts](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/qa-fixes-2026-09-06>). The executable outcome runner is [onboarding-interop.Tests.ps1](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/tests/onboarding-interop.Tests.ps1>); it resolves the independent companion checkout through `suite.json`. The earlier QA reproductions and report remain historical evidence and intentionally describe the pre-fix behavior.

## Compatibility and deployment acceptance

Remote onboarding requires `onboarding-outcome-v1` together with the existing attempt, remote and network capabilities. Updated Setup rejects a server lacking final confirmation before changing configuration; the server frontend and backend reject an older Agent. Upgrade the complete companion package and compatible server together when enabling this workflow. Local installed-process schema 1, server-local inherited elevation/licence, remote Yes/No and UAC, and independent updaters remain unchanged.

Client-only deployments continue to use remote Job Configurator, KiloLink and Discovery. Combined hosts keep distinct ports. Installed applications, LAN operation, complete local packages and verified cached downloads do not require a general internet connection. Actual package sources remain gated by the downloading application, including Environment's Windows and WSL phases.

No production onboarding, software installation, real network/firewall reconfiguration, reboot/logoff, alternate-account UAC or live Arena/decoder operation was performed. Controlled machines must still verify those paths, including the external helper under actual Program Files/UAC conditions and Discovery state across reboot. Distributed Arena/firmware writes are not one atomic transaction; existing backups remain necessary after partial work. Power loss during network mutation produces repair-required reconciliation rather than an automatic claim that Windows settings were restored. Publish the companion independently first, then build a release from its clean checkout; the validation packages must not be treated as published releases.
