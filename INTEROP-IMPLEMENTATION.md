# Interoperability implementation — 6 September 2026

Implemented the thirteen findings in [the review](SUITE-INTEROPERABILITY-REVIEW-2026-09-06.md) across all five independent repositories. The workspace now describes all five roots. Their Git histories, branches, installers, runtime ownership and release processes remain separate. Existing Resolume work and unrelated reports/scratch files were preserved.

These are uncommitted local changes and validation builds. No application was installed, published or deployed, and no production network, firewall, NDI, Arena or device configuration was changed.

## Findings addressed

| Finding | Implemented behavior | Principal evidence |
| --- | --- | --- |
| F01 Shared-host access | Environment rules restrict profiles/peers and TCP local address; Job Configurator enforces selected interface/subnet and browser-origin access. | Mocked firewall scope; server access tests; isolated HTTP origin rejection. |
| F02 Network conflicts/recovery | Reserve targets against inventory and active attempts. Agent requires preferred address state and restores network/DNS/NDI/Agent state locally after failure, under a shared mutation lock and independent recovery timeout. | Duplicate-target regression; static/DHCP validation; injected failure restores fixture files. |
| F03 Elevation ownership | Companion resolves the original desktop user from OS tokens/profile metadata. Environment Client verifies that user's configuration. Server installers reject alternate-account desktop installs before changes and explain the signed-in administrator requirement. | Account-gate fixture, code review and builds. Actual different-account UAC remains a deployment test. |
| F04 Agent/Setup retention | Protect either newer component; retain matching newer pairs; require repair for mixed newer pairs. Stage replacements and retain a recovery journal for failed/interrupted replacement. Preserve prerelease ordering. | Mixed/missing version cases, injected second-file failure, journal recovery and package/hash checks. |
| F05 Resolume stale jobs | Carry server/job/revision through plans and restart helpers; validate before mutation, around later phases, after restart and before each decoder. | Identity/replacement tests, restart argument round trip, real server-to-Resolume HTTP fixture. |
| F06 Client-only status | Client/Server receipt, role-aware requirements, combined-host Agent row, legacy uncertainty and independent role removal. | Model tests and rendered Client/combined layouts at multiple scales. |
| F07 DHCP changes | Reconcile the selected adapter's actual address while retaining GUID; rebind listeners; report missing/ambiguous bindings and NDI drift. | Changed/missing/ambiguous address fixtures; isolated Agent API validation. |
| F08 Ports | Discovery fixed to TCP 5959; reject KiloLink collisions with 8080/8091/8094; restrict Job port override to loopback tests. | Backend/installer validation and endpoint documentation. |
| F09 Remote-only bootstrap | Create job metadata without local companion/hardware; avoid local endpoint operations; stop polling after immediate completion. | Server execution with a companion that must never be invoked; frontend behavior tests. |
| F10 Attempt correlation | Approval/fetch/registration share an attempt and job revision; reject stale/denied/expired attempts; conditionally update pending state; retry matching acknowledgements, including after server restart. | Attempt/reservation/idempotency regressions; deferred-launch and CLI identity checks. |
| F11 Credential ownership | Supplement credentials only for exact server/job revision/device identity; remove IP-only matching. | Same-name/IP/different-server/device fixtures and decoder preflight. |
| F12 Subnet discovery | Complete /20–/30 coverage, bounded concurrency/request timeouts and a four-minute scan limit; explicit unsupported/incomplete coverage. | Cross-/24 fixtures in server, companion and Resolume; cancellation coverage. |
| F13 Download gates | Probe actual sources, handle HEAD rejection/HTML, stage both Client packages, gate Windows and WSL phases, bound transfers and accept verified offline inputs/cache. | Offline/cache/captive-portal/cancellation tests; source, owner and WSL-context failure gates; no-installation fixtures. |

## Additional changes

Added [deployment and contract documentation](INTEROPERABILITY.md), per-repository notes and current handover links; expanded suite/workspace metadata; added Resolume local Agent/NDI readiness; preserved shared NDI Tools during server removal; corrected the Toolkit Agent catalog asset path and stable-after-prerelease updater behavior. Applicable packages include the new documentation.

## Automated validation

| Validation | Result |
| --- | --- |
| Job Configurator regression | 21/21 checks passed |
| Job Configurator frontend | 7/7 tests passed |
| Job Configurator installer | Version/retention/manifest/corruption suite passed |
| Companion Setup | 14 PASS groups |
| Companion Agent | 21 PASS groups |
| Environment installer | 82/82 regression checks passed |
| Toolkit regression and UI | 256 assertions passed; no WPF binding errors |
| Resolume regression/WPF | 34/34 tests passed |
| Cross-application HTTP | Public identity, origin rejection, Resolume reader and same-name replacement passed |

The HTTP fixture launches a separate loopback Job Configurator on an ephemeral port with synthetic state and an unavailable fixture adapter. Monitoring cannot target production hardware. It invokes the independently built Resolume reader. Agent API tests use isolated files and temporary ports; installer mutations are mocked.

Full logs: [artifacts/interop-validation-2026-09-06](artifacts/interop-validation-2026-09-06/). Toolkit UI artifacts include [Client only](../ChatGPT/Wrapper%20Application/artifacts/ui-review/client-only-100.png) and [combined host](../ChatGPT/Wrapper%20Application/artifacts/ui-review/combined-host-100.png); both were visually inspected.

Packaged builds succeeded for Environment Setup, Toolkit and Resolume, and the self-contained server installer bundling the companion built by its own publish script. The manifest records companion version, commit, dirty working tree and payload hashes. These local artifacts retain existing version metadata and are not release publications.

## Deployment acceptance and explicit limits

Automated checks validate code behavior and packaging. Complete these checks on controlled machines before release:

1. Client-only operation with remote services, including separate internet and production adapters.
2. Management-only and combined hosts, including effective firewall access from a second/Public NIC.
3. Standard-user Client with different administrator credentials; same-user administrator server setup; logoff, reboot and WSL resume.
4. Real DHCP/static changes, duplicate addresses, gateway/DNS/server loss and rollback. Caught failures recover locally; abrupt power loss during network onboarding can still require local repair.
5. Arena/source/decoder operation, job changes during configuration/restart and recovery from saved compositions. Distributed revision checks do not make all Arena/firmware writes atomic.

The explicit Environment offline bundle covers Client packages. Fresh Server setup still needs WSL/Ubuntu/Linux repositories; WSL-context checks necessarily follow prerequisite installation. Successful source checks cannot guarantee later connectivity, and later-stage failure does not roll back every prerequisite.

Resolume's interim credential fix requires a verified exact-identity source for custom remote credentials; normal onboarded devices retain the admin/job-name fallback. No general credential export/control API was introduced.

Unattended Windows service hosting and generalized custom ports remain separate future features, as specified in the original plan. Server/Agent startup still requires the documented user session.

Before release, complete applicable deployment acceptance, review each repository's own diff/version metadata, publish the companion independently first and build the server against its clean verified checkout. Do not distribute same-version dirty validation builds as production updates.
