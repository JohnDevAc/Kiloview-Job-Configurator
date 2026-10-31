# Toolbox lifecycle contract, version 1

Protocol baseline established 26 October 2026. This document defines shared
interoperability rules, not the status of a particular build, release or deployment.
The master owns this document. Changes to the wire format must be coordinated
before producers and consumers diverge.

## Ownership and scope

Toolkit coordinates software lifecycle operations. Each independent product owns
its installer, elevation, licence acceptance, settings, uninstall and recovery.
Endpoint onboarding remains a separate PC Agent contract. Role selection must
appear once, in Environment's user journey. Removing an app never implies removal
of shared NDI Tools, Agent, another role, or unrelated WSL distributions.

Product IDs are the existing Toolkit catalog IDs: `environment`, `job`,
`pc-agent`, `resolume`, `udp-to-ndi`, and `testcard`.

Implementing this protocol includes source, documentation, builds, isolated tests
and reviewable packages. Production installations, hardware/network changes and
publication remain separately authorized activities; this baseline does not state
whether a particular product has already been released or installed.

## User interface

Keep the wrapper concise: app name, short current status and relevant controls.
Use direct labels such as Install, Open, Update and Uninstall. Show explanation
only when it changes the user's next decision. Put versions, diagnostics, logs
and infrequent maintenance actions behind Details or a menu. Avoid repeated
guidance, technical protocol language and permanently visible recovery text.
Failures and restart requests need a brief actionable message when relevant.
Required licence information and meaningful confirmations remain available.
Environment presents its Client/Server choice once. Verify the simplified views
at narrow widths and high DPI as well as the normal layout.

## Protocol

Version 1 uses UTF-8 JSON request and atomic durable result files. Each operation
gets a unique GUID `operationId` and its own directory, retained across caller
restart. A producer must echo and validate the request's schema, operation ID,
product ID and action. A stale, mismatched, missing or malformed result is never
success. Paths and ownership must be validated before elevated writes; no shell
evaluation of request fields, unsafe recursive deletion or reparse traversal.
Path guards must distinguish target-redirecting junctions/symlinks from verified
non-name-surrogate OneDrive cloud placeholders. Windows Desktop and package paths
may be stored under OneDrive. Do not reject every legitimate path solely on
the ReparsePoint attribute; validate the actual tag/final path and ownership, fail
closed for unknown tags/redirects, and test both cloud and malicious-link cases.

Native entry point: `--lifecycle-request <absolute request.json>`.
PowerShell entry point: `-LifecycleRequestPath <absolute request.json>`.
An adapter may invoke a product-owned script directly when that is the existing
supported installer/uninstaller architecture. Do not pass new switches blindly
to older or third-party installers. Advertise new support through a versioned
installed/package capability descriptor and retain honest legacy fallback.
Owners report exact executable/script and descriptor locations to Toolkit.

Request required fields:

```json
{
  "schemaVersion": 1,
  "operationId": "be275086-7445-44e7-b741-c61bcbfc8e27",
  "productId": "environment",
  "action": "install",
  "resultPath": "C:\\owned-operation-directory\\result.json"
}
```

Optional fields are `role` (`client` or `server`), `preserveData` (defaults true),
and `requestedVersion`. A role omitted by Toolkit is selected once in Environment.
`requestedVersion`, when present, is the verified expected payload/binary version,
not a GitHub tag (no leading `v`). Omit it when that expectation is not established;
different channel tags can intentionally reference identical package bytes. The
owner still validates staged payload identity and installed postconditions.
Supported actions: `inspect`, `install`, `update`, `repair`, `uninstall`.
Environment may additionally advertise `remove-role`, `remove-app`, `backup` and
`maintain-runtime`; it must distinguish app removal from role/data removal.
For Environment, generic `uninstall` opens its role-removal review (equivalent to
`remove-role`), asking for installed role scope when omitted or ambiguous.
Configuration-app-only removal is the explicit `remove-app` action and clearly
states that server runtime/data remain. Data retention defaults true; a retained
distro or inactive deployment must be disclosed, not labelled fully removed.
No request implicitly accepts licences, approves a destructive purge, alters
network configuration, or authorizes another product's removal.

Result required fields:

```json
{
  "schemaVersion": 1,
  "operationId": "be275086-7445-44e7-b741-c61bcbfc8e27",
  "productId": "environment",
  "action": "install",
  "outcome": "running",
  "updatedUtc": "2026-10-26T00:00:00Z",
  "message": "Checking prerequisites"
}
```

Outcomes: `running`, `succeeded`, `no-change`, `cancelled`, `partial`, `failed`,
`blocked`, `restart-required`. The first is active; restart-required is resumable
and not Ready; all other outcomes finish the current attempt. Continuation keeps
the operation ID and increments optional `attempt`. Write results atomically.
Refresh results at stage boundaries/heartbeats; include optional `stage`,
`errorCode`, `nextAction`, `logPath`, `safeToCancel`, `restartRequired`,
`partialEffects`, `recoveryRequired`, `backupPath` and `components` as applicable.
Component evidence must distinguish installed, configured, running and healthy;
unknown must not be converted to false or success. Version evidence must describe
the component, not merely its setup executable. Optional fields must be ignored
by older readers; unknown schema versions fail closed.

`components` is an array of objects keyed by `productId`. A component object uses
`installed`, `configured`, `running`, `healthy` (boolean or null), `version` and
`channel` (string or null). Subcomponents may use stable product IDs such as
`ndi-tools`, `ndi-discovery`, `kilolink`, `wsl` and `docker`. Agent evidence also
uses `setupVersion`, `pairMatches`, `onboarded`, `networkAvailable`,
`recoveryRequired` and `error` where known. These are observations, not instructions
to configure or start software. `partialEffects`, when provided, is an array of
human-readable strings describing completed or retained changes. `nextAction` and
`message` are display text, never shell commands.

Inspect may additionally return `activeOperationId` (GUID or null), `safeToRetry`
(boolean or null), and `recoveryRequired` (boolean). A producer may report
`safeToRetry:true` only after checking its lifecycle lock and durable recovery
state; it means it can safely accept a new supported request. It is not a claim
that an interrupted operation succeeded.

Cancellation is separate from failure and from success. For owned Windows entry
points use exit 0 for succeeded/no-change, 1602 for cancelled, 3010 for restart,
1618 for another active operation, and a nonzero failure for failed/partial or
other blocked outcomes. Toolkit treats the correlated verified result as primary,
checks process failure and re-detects actual state after every outcome. Legacy
installer exit codes alone establish neither requested-version success nor health.
Normal launch is never a read-only status probe.

Capabilities descriptor: `toolbox-lifecycle.json` adjacent to the installed
product's maintenance entry point (or in verified package metadata). Required:
`schemaVersion:1`, `productId`, `supportedActions` array, and `entryPoint` relative
to that owned directory. Optional `entryPointKind` is `native` or `powershell`;
default native. Entry point must resolve inside the validated product directory.
Products with distinct install/uninstall scripts may additionally advertise an
`actionEntryPoints` map from supported action to relative owned path. Each mapped
file's extension determines native versus PowerShell invocation. Metadata does
not grant permission to execute untrusted command lines.

Fresh installation advertises capabilities through a complete verified ZIP, or
a separately digest-verified descriptor asset in the same release as the verified
native installer. Absent metadata uses honest legacy fallback; malformed
advertised metadata blocks the operation. Support must not be inferred from an
unchanged version number. Installed/retained descriptors advertise only actions
their retained files can perform; package-dependent repair/update uses the full
verified package. Bundled independent products retain their own nested metadata;
the consumer selects the requested product's authoritative package descriptor.

## Operation safety and persistence

Each product uses a lifecycle lock shared by install/update/removal, including
standalone and Toolkit entry points. Preserve existing working files until staged
payload validation and replacement readiness. Journal partial effects and rollback
where supported; do not promise a transaction over Windows features, apt or vendor
installers. Preserve a usable maintenance/uninstall path after app-only removal.

Bound unattended native/process/probe stages and detect stalls. Interactive
licence/UAC prompts must be reported as waiting for user input, not blindly killed.
Stopping unsafe data replacement must leave explicit recovery-required status.
Persist reviewed intent through elevation and reboot, re-inspect actual state on
resume, and retain original-account ownership requirements.

Toolkit persists pending operations and reconcilable results independently of UI
preferences. Its own global operation coordination complements product locks. A
running operation does not become successful when a bootstrap parent exits or
the Toolkit window closes. Remote release/icon checks do not disable local Open.

After a missing/stale result or bootstrap exit, Toolkit retains the unconfirmed
operation and permits read-only inspection. An explicit user Recheck may release
the stale Toolkit mutation lock only when product-owned inspection reports no
active operation, `safeToRetry:true` and no unresolved recovery requirement.
Retain the original operation as interrupted/unconfirmed, never succeeded. Process
absence alone is not sufficient. An owner-confirmed terminal failed/partial result
also completes that attempt, with its recovery instructions retained. Never use a
generic force-unlock/kill to bypass an unknown owner operation; owners must provide
a supported recovery path when their journal indicates pending work.

## Required acceptance

- Fresh Client/Server, current no-op, update, repair, removal and retry after partial removal.
- Correlated outcomes after cancellation, worker exit/crash, malformed/stale result,
  elevation handoff, reboot and Toolkit reopen.
- Source/download failure, checksum mismatch, locked file, disk/registration/task
  failure, conflicting lifecycle operations and interrupted replacement.
- Distinct installed/configured/running/healthy states, matching Agent pair and
  consistent Agent readiness in Environment and its own Toolkit card.
- Shared-component and user-data retention; owner/account/reparse path safety.
- Offline local launch; portable dependency damage, receipt rediscovery, repair,
  safe removal and failed activation without losing the previous copy.
- Actual Windows/UAC/WSL/vendor/reboot acceptance is reported separately from
  isolated fixtures. Do not mutate the user's live system to substitute for a
  disposable test environment.

## Routing

Use [Project coordination](PROJECT-COORDINATION.md) for the current master and
product-owner task routing.

Owners retain separate repositories, histories, packages and deployments. The
master owns this contract and implementation coordination records only; owners
write their own source and handoffs. Preserve all unrelated existing changes.
