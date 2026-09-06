# Suite deployment and integration contract

## QA follow-up contract — 6 September 2026

This section updates the earlier contract below. Remote onboarding now also requires `onboarding-outcome-v1`; configuration must return `requiresFinalConfirmation: true`. Registration creates a durable candidate receipt and returns `awaiting-confirmation`. Only a matching `completed` final outcome posted to `/api/pc-onboarding/outcome` adds that PC to the active job. Exhausted registration responses cause local rollback and a durable `aborted` outcome, or `recovery-required` if restoration fails. Lost final confirmation never triggers rollback: Agent retries its saved outcome after Setup exits and after restart. Interrupted `applying` journals require local repair. Changed jobs/newer attempts return `superseded`; terminal outcomes are idempotent. Preserve server state/backups and the companion's `onboarding-outcomes` directory beside its configuration for reconciliation.

The attempt allows ten minutes for approval/UAC. The five-minute registration window begins on the first configuration fetch; retries and late HTTP 202 responses cannot reset it. Companion execution is bounded to four minutes after fetch, with recovery separately bounded. Final outcome reconciliation can finish later. Static targets reserve known local server interfaces, gateways, Discovery and KiloLink addresses as well as inventory, agents and other attempts, even when infrastructure is offline.

Server identity publication now uses a flushed temporary file and process lock. A malformed identity is recovered from persisted state where possible. Legacy credential snapshots receive the same identity/revision before the first API read returns.

Environment records the server owner's Windows SID and requires that owner's signed-in administrator desktop for server maintenance, resume and removal. A legacy KiloLink task principal can establish ownership; unverifiable ownership fails before mutation. Recover missing ownership evidence from the original installation's backup; do not adopt another user's WSL deployment. Discovery snapshots preserve the prior service startup mode, delayed Automatic flag, running state, task XML and exact configuration. Removal restores the prior snapshot or disables a newly managed service. Client-only removal preserves server components and shared tools.

Toolkit validates schema, nonempty endpoint/adapter GUIDs, usable IPv4 and prefix before reporting Agent configured. Live adapter/address availability is separate from installation completeness. Resolume validates current job and local NDI readiness immediately before Arena writes and decoder preset/activation writes, including after restart and internal clip/source waits.

The local process contract remains schema 1. Repository, deployment, consent and offline/cached-package rules remain unchanged. See [QA-FIX-IMPLEMENTATION-2026-09-06.md](QA-FIX-IMPLEMENTATION-2026-09-06.md) for validation and deployment acceptance limits.

These five applications have separate repositories, build outputs, installations and release histories. This workspace describes their integration; it does not combine their source. Paths and contract identifiers are in `suite.json`.

## Supported deployments

| Host role | Local requirements | Services that may be remote |
| --- | --- | --- |
| Client / Arena PC | PC Agent configured for its production adapter; NDI Tools when selected by Environment Client setup; Arena and Resolume Configurator when used | Job Configurator, NDI Discovery, KiloLink |
| Management host | Job Configurator and an active production adapter | PC Agent endpoints, NDI Discovery, KiloLink |
| Combined host | Selected server and client components, each independently configured | Additional clients and hardware |

A management host can create a job without selecting hardware or installing a local Agent. Onboarding that host as an NDI endpoint remains optional and uses the installed companion's schema-1 local process contract. The optional bundled Agent stays selected by default. Local server invocation uses existing elevation and accepted component licence; remote requests retain local Yes/No and UAC.

Job Configurator is a per-user elevated application with an interactive-logon startup task. Install it from the signed-in administrator account. Alternate administrator credentials at UAC are rejected before installation, because they cannot safely create this per-user server for the original standard-user desktop. Environment Server similarly requires its intended signed-in administrator/WSL owner. Agent startup is per-user at logon. A combined host therefore needs its operator signed in for Job Configurator/Agent; it is not an unattended Windows service deployment.

## Network contract

| Component | Supported endpoint |
| --- | --- |
| Job Configurator | HTTP TCP 8091 |
| PC Agent | UDP 8093 discovery; HTTP TCP 8094 |
| NDI Discovery | TCP 5959; IPv4 address supplied without a custom port |
| Arena API | HTTP TCP 8080 |
| KiloLink | Configured web port excluding 8080, 8091 and 8094; configured even UDP link pair |

Nondefault Job Configurator ports are restricted to isolated loopback testing. Device firmware/media ports remain component-owned. Environment media rules retain their required ranges while restricting Windows access to Domain/Private profiles and local-subnet peers; TCP is restricted to the selected local address. Job management also enforces loopback or the selected local interface/subnet and rejects foreign browser origins. This is a trusted-production-LAN boundary, not user authentication or encrypted transport.

Job Configurator and Agent scan the complete selected IPv4 subnet for /20 through /30, with bounded concurrency, per-request deadlines and a four-minute scan limit. Unsupported prefixes fail explicitly. Incomplete timed-out scans report their coverage; they do not masquerade as a completed /24 scan. Use a supported production subnet.

## Versioned identity and onboarding

Public `/api/state` adds `integrationSchemaVersion: 1`, a persistent GUID `serverId`, and opaque `jobId` / `jobRevision` strings. They are correlation identifiers, not authentication secrets. Consumers compare the complete tuple. A same-name replacement has a new job ID. Device/job/network configuration changes invalidate its revision; health polls and Windows membership telemetry do not.

The server stores its ID in `server-id.txt` beside its state. Preserve that file when restoring the same server; provision a fresh data directory for a different server. Device credentials remain excluded from the HTTP state response.

Strengthened remote onboarding requires Agent capability `onboarding-attempt-v1`, in addition to existing remote/network capabilities. Schema 1 gains `attemptId`, `jobId` and `jobRevision`; the attempt is passed through approval, the configuration query and registration. Older peers receive an explicit update requirement. Active attempts cannot be silently replaced. Static targets are reserved against inventory, discovered PCs and other active attempts. Only the fetched approved attempt/current job may register; matching acknowledgements can be retried, including after server restart. A fresh attempt supersedes old consent.

PC Agent validates preferred address state, snapshots network/DNS/NDI/Agent configuration and restores locally when configuration or bounded registration fails. Recovery uses its own deadline. Setup and multicast share one configuration lock. Secondary IPv4 addresses are rejected for remote network changes to avoid dropping them. DHCP changes retain the endpoint GUID and adapter ID, rebind monitoring, and report NDI drift requiring companion reapplication; they never silently select a different NIC.

## Installation and compatibility

Download readiness belongs to the component doing the download and runs in that component's account/proxy context. Installed apps, local self-contained installers and LAN operation do not require a generic internet test. Toolkit verifies cached payloads before accepting offline installation. Environment stages both Client packages before starting either installer and gates Windows prerequisites before enabling features; its Linux gate runs inside WSL before dependent changes. Later-phase failures remain retryable and may leave prerequisite installation completed.

Server, Environment and companion installation reject a downgrade of either newer Agent/Setup binary. Mixed or missing newer components require a matching/newer complete package. Companion replacement stages the pair, keeps a local recovery journal and restores a failed replacement; the next Setup repairs an interrupted replacement before continuing.

Environment's schema-1 `installation-components.json` records Client/Server selections separately from server configuration. Toolkit evaluates actual files/state against those roles. Removing server ownership preserves shared NDI Tools and independently installed Agent.

Resolume requires the selected server/job revision and verifies the local Arena PC's Agent endpoint, NDI interface, groups and Discovery setting before mutation. It rechecks around configuration phases, restart and each decoder. Local credentials are enriched only from an exact server/job revision and device ID. Remote jobs with nondefault credentials need a verified exact-identity local credential source; arbitrary remote secret export is not introduced. Failed decoder preflight blocks Arena changes. Existing Arena backups remain the recovery path after partial configuration.

Release each repository independently. Publish the companion first, then build a server release from its clean verified checkout; the bundled manifest records companion version, commit, dirty-state flag and every file hash. Local test builds with dirty working trees are validation artifacts, not releases. An unattended service mode and generalized custom-port support remain separate future features.

See `INTEROP-IMPLEMENTATION.md` for finding coverage, executed tests and deployment acceptance limits.
