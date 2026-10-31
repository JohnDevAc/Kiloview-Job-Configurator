# Job Configurator and PC Agent lifecycle

Development implementation, 26 October 2026. Wire format is the master-owned
[Toolbox lifecycle contract, version 1](TOOLBOX-LIFECYCLE-CONTRACT.md). This is separate from schema-1 local PC
onboarding. No lifecycle request accepts a licence, selects a network, writes NDI
settings or authorizes another product's removal.

## Capability discovery

Use the complete verified `NDI-Job-Configurator-Windows.zip`. Its root
`toolbox-lifecycle.json` advertises product `job`, PowerShell entry point
`Invoke-NDIJobConfiguratorLifecycle.ps1`, and inspect/install/update/repair/uninstall.
Call `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <entrypoint>
-LifecycleRequestPath <absolute request.json>`; pass arguments as arguments, not
evaluated request text. Older standalone EXEs remain legacy installers and receive
no new switches. Source version numbers alone do not advertise this capability.

Installed server descriptor:
`%LOCALAPPDATA%\Programs\NDI Job Configurator\toolbox-lifecycle.json`.
It advertises **inspect and uninstall only**, resolving
`Maintenance/Invoke-NDIJobConfiguratorLifecycle.ps1`. Maintenance scripts survive
app removal. Install/update/repair require the complete verified package so damaged
installed files are never used as a trusted replacement payload.

The independently built Agent ZIP has its own root descriptor, product `pc-agent`,
native `NDI Configurator PC Agent Setup.exe --lifecycle-request <request.json>`.
Its installed descriptor is `%ProgramFiles%\NDI Configurator\PC Agent\toolbox-lifecycle.json`
and resolves retained `Maintenance/NDI Configurator PC Agent Setup.exe`. All five
actions are supported by that complete retained maintenance pair. Agent packaging
and versioning remain independent; the server records every bundled file hash.

## Evidence and recovery

Each request/result lives in an owned GUID operation directory with a protected
DACL: write access only for the original desktop user, SYSTEM and Administrators.
Results are UTF-8, atomic and flushed. Invalid schema/product/operation/action,
escaped result paths, stale mismatched results and redirected filesystem paths
fail closed. Verified non-name-surrogate Windows cloud placeholders are accepted;
junctions, symlinks and unknown reparse tags are rejected.

Inspection is read-only except for its correlated result. Component evidence
separates installed, configured, running and healthy, with null for unknown.
Agent evidence includes matching Setup/Agent versions, strict current-state
precedence over legacy state, memberships, current network availability and
pending recovery. Saved valid configuration can remain present while its NIC is
unavailable. Installation or endpoint registration does not establish health.
Inspect checks lifecycle locks and recovery state before reporting safeToRetry.
An active operation ID can be unknown; it is never fabricated from an old receipt.

The server holds one lifecycle lease through all installation/removal entry points.
It validates a hashed package manifest, copies and verifies staging before stopping
the server, journals the previous directory and owned task/firewall/shortcut/registry
snapshot, then replaces the directory. Success requires the exact version/channel,
process path, process owner and TCP listener matching `/api/health.processId`.
Failure restores the previous server and its owned integration; incomplete recovery
retains the journal and backup. Independently completed Agent work is reported
separately and is never downgraded by server rollback.

Server uninstall checks the original desktop account and owned registrations.
ShouldProcess/WhatIf gates elevation and every mutation. Both products journal
removal before side effects, retain maintenance for retry, verify remaining files
and report partial errors. App removal preserves user data, Agent endpoint identity,
NDI Tools and NDI settings. Server removal leaves the Agent installed. Agent-owned
uninstall removes its startup/firewall/application registration; other products must
not invent their own cleanup. Legacy Agent installations need an ownership-aware
repair before the new uninstaller can safely remove them.

The Agent tray remains running until Setup validates replacement and stops the
exact old process. Initial Setup closure/cancellation/failure no longer reports a
successful installation. Package replacement cannot be cancelled by closing the
initial wizard. The independent tray updater explicitly targets Production,
including when offering a transition from Development.

## Isolated checks and acceptance

Run `tests/job-lifecycle.Tests.ps1` under Windows PowerShell 5.1. It uses temporary
files, fake integration callbacks, a temporary junction and protected operation
directories. Server regression/frontend/installer and both companion validation
projects remain required; `tests/onboarding-interop.Tests.ps1` validates the
unchanged onboarding contract.

Run `tests/job-agent-component.Tests.ps1` under Windows PowerShell 5.1 for the
optional Agent install/retain boundary. It invokes the real helper with reference
outputs and a harmless compiled child that checks schema-1 stdin; all package and
installed paths are temporary. It verifies caller evidence survives child failure
and a newer pair is retained. `tests/installer-dialog.Tests.ps1` checks native
visibility and UTF-8 licence text in hidden/normal launches, then cancels without
accepting consent or starting an installation.

Harmless server producer fixture:
`tests/job-lifecycle-fixture.ps1 -LifecycleRequestPath <request.json>
-FixtureOutcome <succeeded|cancelled|partial|blocked|restart-required|crash>`.
The companion validation executable supports `--lifecycle-fixture <request.json>
<outcome>`. Both use their real file protocol with injected harmless work. They do
not install, configure, elevate or stop application processes.

Actual UAC, alternate-account desktop rejection, task/firewall registration,
interactive cancellation, reboot and locked installed binaries require the
master-coordinated disposable VM. Do not substitute the user's production host.
Candidate package records must include hashes and the dirty source baseline;
development builds at existing version numbers are not published releases.
