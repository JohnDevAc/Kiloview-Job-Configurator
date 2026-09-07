# Server and PC Agent ownership

This ownership contract covers two independently installed products within the five-repository suite:

| Product | Source | Development branch | Installed location |
| --- | --- | --- | --- |
| NDI Job Configurator | `JohnDevAc/Kiloview-Job-Configurator` | `development` | `%LocalAppData%\Programs\NDI Job Configurator` |
| NDI Configurator PC Agent | `JohnDevAc/Kiloview-PC-Onboarding` | `dev` | `%ProgramFiles%\NDI Configurator\PC Agent` |

`suite.json` resolves the companion checkout, minimum version, and protocol.
`NDI-Configurator.code-workspace` opens all five integration roots. The server explicitly
includes only its own C# and web content in its project; companion source stays
under its own Git and build scripts. Minimum companion version: `0.7.0`.

Remote onboarding additionally requires `onboarding-attempt-v1` and `onboarding-outcome-v1`. Registration is provisional until the durable final outcome is confirmed. See [INTEROPERABILITY.md](INTEROPERABILITY.md) for attempt/job identity, reconciliation, topology, readiness and ports. The local process schema remains 1.

## Installation and independent updates

`scripts/Publish.ps1 -SetupExe` builds both products through their respective
publish scripts. `-CompanionRoot` overrides the configured checkout path. The
complete companion ZIP is expanded into `pc-onboarding` in the server package.
`pc-onboarding-manifest.json` records repository, version, source commit, dirty
state, local command schema, and SHA-256 for every component file. Tagged builds
require a clean companion checkout. Commit both repositories before producing
release assets so the embedded revisions describe the released source.

Both installer entry points offer **Install PC Agent**, checked by default. The
selected component's license is included in the initial agreement. Deselecting
it installs the server alone and retains any previously installed companion.
The component is verified before execution. An independently installed newer
Setup and Agent pair is retained. Component updates, versions, uninstall, and
GitHub releases remain independent of the server. Server uninstall does not
remove the PC Agent. Source builds can run without the companion; local PC
onboarding then displays an installation-required message.

## Local process protocol, schema 1

The elevated server launches the fixed installed
`NDI Configurator PC Agent Setup.exe --server-command` with redirected standard
input/output and no shell. It sends one JSON document, closes stdin, reads one
JSON response, and checks the exit code. Requests are limited to 16 KiB; the
server bounds response output and imposes a three-minute timeout. No executable
path, script, or network-readdress operation is accepted in the request.

Installer request (only after the initial license agreement):

```json
{"schemaVersion":1,"operation":"install","acceptLicense":true}
```

Onboarding request:

```json
{"schemaVersion":1,"operation":"onboard","adapterId":"<active-adapter-id>","address":"192.0.2.15","jobName":"Studio1","ndiDiscoveryServerIp":"192.0.2.10"}
```

Onboarding requires administrator rights, the exact installed utility, an
installed agent, existing license acceptance, and a matching active adapter/IP.
It retains Windows IP settings, applies and verifies NDI preferred interface,
job send/receive groups and discovery server, refreshes agent configuration and
firewall scope, records job membership, and returns the normal registration:

```json
{"schemaVersion":1,"success":true,"version":"0.7.0-dev.1","endpoint":{"endpointId":"<stable-guid>","hostname":"SERVER","address":"192.0.2.15","adapterName":"Ethernet","prefixLength":24,"preferredInterfaceConfigured":true,"ndiToolsVersion":"not installed","utilityVersion":"0.7.0-dev.1","eulaVersion":"1.0","operatingSystemVersion":"Microsoft Windows ..."}}
```

Failures return `success:false`, an `error`, and exit code 1. Successful calls
exit 0. There is no second license, Yes/No, result dialog, or UAC prompt; the
server already runs elevated. Open NDI configuration clients produce an
actionable failure. The background Discovery Server remains running.

## Server integration

Selecting an adapter only persists the selected network. It does not apply NDI
settings or add an endpoint. The onboarding plan can include this server PC,
alone or alongside devices. Its companion call completes before clean onboarding
deletes prior inventory. Local reapply and onboarding startup are serialized.

`GET /api/pc-onboarding/local` reports installation compatibility. `POST` uses the
current job and selected adapter, calls the local utility, validates its result,
and stores a `WindowsPcs` entry with `isServerPc:true`. This is part of the trusted
LAN management UI. The companion's network API gains no no-consent operation.
Remote registration cannot replace the server's local identity.

Internal failure diagnostics are documented in [ONBOARDING-DIAGNOSTICS.md](ONBOARDING-DIAGNOSTICS.md).
The schema-1 local response can optionally carry `failureReport`; older responses remain valid.
Remote failure reports use a separate bounded endpoint and persistent attempt scope, never configuration permission.
The server retains reports for seven days, and the Agent retries a bounded durable queue. No diagnostic log UI is added.

All Windows PCs use their real agent GUID, discovery on UDP 8093, monitoring on
TCP 8094, and membership-authorized multicast requests. Agent status includes
`ndiConfiguration` with preferred-interface state, send/receive groups and
discovery server, so the server reports drift without reading local NDI files.
The server reloads its own preview runtime after local onboarding or multicast.

Legacy `localPc`, `remoteWindowsPcs`, and `local-pc` state/contracts are not
migrated. Re-onboard Windows PCs after this upgrade. Device inventory and the
rest of application state keep their existing formats.

Remote Windows PCs continue to require a visible local Yes/No response and UAC,
using `remote-onboarding-v2` and `network-config-v1`. See the companion's
`SERVER-REMOTE-ONBOARDING-HANDOVER.md` for that independent protocol.

## Validation before deployment

Run server regression, frontend and installer tests, both companion validation
projects, then build and validate package hashes and embedded payloads. Use
isolated paths and ephemeral HTTP ports. Live installation acceptance should
cover selected/deselected component installs, retaining a newer agent, server-only
onboarding without another prompt, remote approval, and multicast readback.
