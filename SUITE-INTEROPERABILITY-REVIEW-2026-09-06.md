# Five-application interoperability review

Implementation follow-up: see [implemented changes, validation and deployment limits](INTEROP-IMPLEMENTATION.md). The review below records the original findings before implementation.

Date: 6 September 2026. Scope: the available local code, tests, installers, release configuration, READMEs and handoff information for all five repositories, including the newly added Resolume Configurator.

## Executive assessment

The separate-repository architecture is appropriate and should remain intact. The principal problems are assumptions at application boundaries: which components must exist locally, which Windows account owns configuration, which job an operation belongs to, and which application controls shared firewall/runtime resources.

This review identifies **13 actionable findings: four P1 and nine P2**. P1 means fix before relying on the affected deployment path; P2 means a reproducible or clearly traced functional defect under the stated conditions. Nine findings have isolated executable demonstrations of their central failure or policy decision; the remaining findings are supported by code traces. Deployment consequences are identified separately from what was actually executed.

The highest priorities are the Environment installer's permissive firewall rules on a shared server, recoverability of remote network changes, installation under a different administrator account, and protection against downgrading a partially newer PC Agent installation. Resolume adds two significant integration concerns: stale jobs are detected after Arena has already been modified, and remote job credentials can be taken from unrelated local state.

**No application source, Git history, installation, live NDI settings, firewall rules or device configuration was changed for this review.** The report and isolated reproduction files are the only review additions. Existing user changes were preserved.

## Repositories and reviewed state

| Application | Branch / HEAD | Version in checkout | Working-tree qualification |
| --- | --- | --- | --- |
| NDI Job Configurator | development / cdd6f712d079 | 0.8.8-dev.1 | Existing untracked reports and tmp directory preserved |
| NDI Configurator PC Agent | dev / c651f1ef8bc7 | 0.7.0-dev.2 | Clean |
| Kiloview Environment Setup | main / 2b73181436d4 | 2.1.1 | Existing untracked handoff and attachments preserved |
| Production Toolkit | main / ec9a257ec541 | 1.3.1 | Clean |
| Resolume Configurator | main / e18bb7ae7227 | 0.3.5 | Existing uncommitted startup/discovery changes included in the review |

These are local snapshots, not a claim that every checkout matches its latest published release. In particular, the reviewed Resolume remote-selection behavior includes uncommitted work.

## Deployment and ownership model to preserve

| Deployment | Components that can be local | Components that can be remote or absent |
| --- | --- | --- |
| Client PC | PC Agent, NDI Tools; optionally Resolume Configurator and Arena; optionally Toolkit | Job Configurator, KiloLink and NDI Discovery need not run locally |
| Management host | Job Configurator; optionally Toolkit | Local PC Agent is optional unless this host participates as an NDI endpoint; KiloLink and Discovery can be remote |
| Combined host | Job Configurator, PC Agent, Environment server components; optionally Toolkit, Resolume and Arena | Remote PCs still require their own local approval and elevation |
| Unattended combined host | Server processes expected to survive reboot without user logon | This requires an explicit deployment design; current Job Configurator and Agent startup depend on a user session |

Environment Setup owns provisioning of its selected Client or Server components. Job Configurator owns job planning, device inventory and orchestration. PC Agent owns Windows NDI configuration and its endpoint identity. Resolume Configurator owns Arena configuration and its decoder-output workflow. Toolkit owns application selection, installation launch and status presentation.

The existing versioned local process contract, separate companion package and manifest/hash checks are useful boundaries. Keep the optional PC Agent selected by default, retain newer independently installed versions, use the same agent GUID for local and remote monitoring, and preserve remote Yes/No plus UAC. A server-local process invocation must continue to use its existing elevation and accepted installation license without a second confirmation.

## Findings

### F01 — P1: Environment firewall rules broaden access to coinstalled management applications

**Affected:** combined hosts running Environment Server components and Job Configurator.

[Environment firewall rules](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1742>) allow TCP 5960–10000 without a program, remote-address or profile restriction. This range includes Job Configurator's 8091, PC Agent's 8094 and Arena's usual 8080. Job Configurator's own [installer rules](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/installer/Install-NDIJobConfigurator.ps1:184>) specify Domain/Private and LocalSubnet, but an independent broad allow rule does not preserve those restrictions.

Job Configurator [listens on all IPv4 interfaces in LAN mode](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Program.cs:35>) and does not put an equivalent general authorization/subnet gate in front of its management API. Installing Environment Server on the same host therefore broadens the effective reach of that API, subject to other Windows/network policy. The PC Agent has its own request checks; inclusion of 8094 does not by itself defeat those checks.

**Evidence:** the real firewall function was extracted and run with local mock firewall commands. Its TCP rule included both 8091 and 8094 with no scope constraints. No live rule was created. Microsoft documents the default profile scope as Any: [New-NetFirewallRule reference](https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule?view=windowsserver2025-ps).

**Suggested fix:** constrain Environment rules to required profiles, peers/interfaces and owning programs/services where possible, while retaining required NDI media traffic. Add management access enforcement in Job Configurator as defense in depth. Test the combined effective policy on a second/Public NIC and from outside the selected subnet; testing each installer's rules separately is insufficient.

### F02 — P1: Remote static-address onboarding permits conflicts and lacks recovery after partial changes

**Affected:** remote Windows PCs in either distributed or combined deployments.

The [server's static-address validation](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/WindowsPcRemoteOnboardingService.cs:278>) checks address shape, mutual subnet membership and the server's own address, but does not reserve target addresses across pending PCs or reject known occupied device addresses. Two different endpoint requests can be staged for the same target IPv4 address.

On the client, [network changes](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/NetworkConfigurationService.cs:120>) occur before later NDI configuration and registration. [address readiness](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/NetworkConfigurationService.cs:151>) checks for an address appearing, without requiring a successful duplicate-address-detection state. The [overall operation](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/RemoteOnboardingService.cs:38>) has no restoration of the previous network/NDI configuration when a later step fails. Subnet arithmetic is also not proof that the server is reachable.

**Evidence:** the real server service accepted two fixture endpoints with the same static target. No traffic or network mutation occurred. Loss of connectivity and lack of rollback were established by code trace, not deliberately triggered on a real PC.

**Suggested fix:** reserve addresses per job/attempt, check inventory and probe conflicts as appropriate, then require valid address state on the endpoint. Snapshot the original network, NDI and agent configuration before changing it. Use a bounded registration/reachability check and local rollback/recovery when it fails. Keep recovery on the PC so it works when the server can no longer reach it. Test duplicate targets, occupied targets, DHCP timeout, DNS failure and server loss between configuration and registration.

### F03 — P1: Elevation with another administrator account changes installation/state ownership

**Affected:** a standard user running Toolkit, PC Agent Setup or Job Configurator Setup and entering another account's administrator credentials.

[PC Agent state paths](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/AgentInstallationService.cs:52>) use the elevated process's LocalApplicationData; [startup registration](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/AgentInstallationService.cs:246>) uses Registry.CurrentUser. The tray agent [reads the running user's profile](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/Agent/AgentStore.cs:12>), while Setup attempts to start it through Explorer. This can leave state/startup registered for the administrator and the interactive user's tray process unable to find its configuration. Remote reconfiguration can likewise look for the selected adapter in the wrong profile.

The [Job Configurator installer](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/installer/Install-NDIJobConfigurator.ps1:25>) derives its per-user paths after elevation, then [resolves the startup SID from that profile](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/installer/Install-NDIJobConfigurator.ps1:167>). Toolkit's user-context detection can consequently miss an installation placed in the administrator's profile.

**Evidence:** code trace plus Windows account semantics; no different-account UAC session was executed. This is conditional on alternate credentials, not ordinary same-user administrator consent. [Microsoft's UAC description](https://learn.microsoft.com/en-us/windows-server/security/user-account-control/how-user-account-control-works) explains execution using the administrative user's token.

**Suggested fix:** make the initiating user/session an explicit, validated part of the privileged-operation contract. Perform per-user configuration and startup registration in that user's context, with narrowly scoped elevation for machine operations. Decide explicitly which endpoint state is machine-wide and which is per-user. Do not solve this by accepting arbitrary writable paths from an untrusted client. Add standard-user/different-admin and multiple-user acceptance tests.

### F04 — P1: Server bundling can downgrade one half of a newer PC Agent installation

**Affected:** a host with mixed or partially updated Agent and Setup versions.

The [retain-newer condition](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/installer/Install-NDIJobConfigurator.ps1:65>) protects an installation only when **both** installed binaries are newer than the bundle. For example, Agent 0.8.0 plus Setup 0.7.0 with bundle 0.7.0 fails the guard. The installer invokes the older bundled Setup, whose [copy path](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/AgentInstallationService.cs:78>) replaces differing published files without an independent version floor.

Environment Setup already [rejects an incomplete newer installation](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1624>), so the two installation routes enforce different upgrade policies.

**Evidence:** evaluating the actual server version comparison and guard gives retain=False for Agent 0.8.0 / Setup 0.7.0 / bundle 0.7.0. No installed binaries were overwritten.

**Suggested fix:** never replace either newer binary with an older package. Treat a mixed pair as a repair case requiring a matching or newer complete package. Stage and replace the pair together, with recovery if replacement is interrupted. Add tests for both directions of mixed versions, one missing binary, corrupt payloads and prerelease/stable ordering.

### F05 — P2: Resolume checks the active job after modifying Arena

**Affected:** local or remote Job Configurator changes after Resolume loads its device list.

[BuildPlan](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/MainWindow.xaml.cs:246>) uses cached composition/device rows. Configure does not refresh or compare the selected server's job before execution. The orchestrator performs decoder authentication preflight, then [starts modifying the open composition](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/ArenaConfigurationOrchestrator.cs:34>). The [post-restart worker](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/PostRestartWorker.cs:16>) checks the job name only after Arena has been changed and restarted.

A changed job or unavailable Job Configurator can therefore be reported after substantial changes. Checking only a name also cannot detect replacing devices or recreating a job under the same name.

**Evidence:** code trace; Arena was not mutated for this review.

**Suggested fix:** bind the plan to a stable server ID, job ID and revision, and revalidate immediately before the first mutation. Pass the expected device identities/revision through the restart helper. Reject a changed job before applying decoder operations, preserve recoverable Arena backups, and make interrupted completion visible. Test job changes before Configure, during configuration and during restart, including a same-name replacement.

### F06 — P2: Toolkit classifies a valid client-only installation as incomplete

**Affected:** client PCs intentionally running no local WSL/KiloLink/Discovery server.

Environment's [Client install path](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1664>) supports NDI Tools and PC Agent without server components. Toolkit's [environment completeness calculation](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Wrapper Application/src/ToolkitLauncher.Core/EnvironmentStatus.cs:43>) always requires configuration, distro, container, watchdog, NDI Tools and Discovery. Its component presentation always includes server rows and does not make PC Agent part of the Client completeness contract.

**Evidence:** constructing the real Toolkit status model for an intentional client-only installation yields Partial, with KiloLink and Discovery shown as missing.

**Suggested fix:** write a small role/component installation receipt that represents Client, Server and selected options. Toolkit should compare observed state against that selection, show unselected components as not required, and include the actual client requirements. Keep this receipt separate from server configuration; creating dummy server configuration would mask the issue. Test Client, Server, combined, optional Agent absent and upgrade/uninstall transitions.

### F07 — P2: PC Agent does not reconcile a changed DHCP address

**Affected:** DHCP clients whose selected adapter receives a different address, including after reboot.

[the API listener](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/Agent/AgentNetworkHost.cs:45>) binds to the saved address and discovery replies advertise it. [monitoring](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/Agent/AgentMonitor.cs:40>) reports saved address/prefix values. [configuration refresh](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/Agent/AgentApplicationContext.cs:69>) responds to saved-state updates rather than reconciling changes to the selected adapter. Installation firewall rules also scope access to the configured address.

When DHCP changes the address, monitoring/discovery can retain stale information and the listener can fail to bind after restart. This undermines the stable endpoint GUID recovery path.

**Evidence:** code trace; the live adapter was not changed.

**Suggested fix:** monitor the selected AdapterId and reconcile its actual usable address, retaining the same endpoint GUID. Update/rebind listeners atomically and arrange any privileged firewall/NDI repair through the companion's approved path. Report a stale or unusable binding rather than online status based on saved configuration. Do not silently switch to another adapter. Test DHCP changes while running and after restart, temporary loss of address and multiple IPv4 addresses.

### F08 — P2: Configurable ports are not supported consistently across applications

**Affected:** nondefault discovery ports and collocated services with customized ports.

Environment Setup permits a nondefault NDI Discovery port and [launches Discovery with that setting](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1728>). Job Configurator's [discovery probe](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/wwwroot/app.js:234>) uses 5959, and the job/agent contract carries an IPv4 address rather than an address and port. [Agent validation](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/RemoteOnboardingService.cs:95>) rejects an address containing a port. NDI itself supports a custom Discovery port: [NDI Discovery Server additional information](https://docs.ndi.video/all/using-ndi/ndi-tools/ndi-tools-for-windows/discovery/discovery-server-additional-information).

There is a similar inconsistency between Job Configurator's [service-port override](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Program.cs:10>) and [remote Agent's fixed 8091 requirement](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/RemoteOnboardingService.cs:157>). The override cannot currently represent a fully supported production configuration. Environment's configurable web/discovery ports also lack a suite-wide check against coinstalled management services.

**Evidence:** the real Agent validator rejects a custom Discovery endpoint. Other port incompatibilities were traced through producers and consumers.

**Suggested fix:** document a single port contract and check collisions against selected local components. Either constrain Discovery to 5959 for supported suite deployments, or introduce versioned address/port fields end to end and verify firmware/client support. Make Job Configurator's override explicitly test-only until its consumers support it, or carry its endpoint consistently through discovery and onboarding.

### F09 — P2: A job containing only remote Windows endpoints cannot be bootstrapped

**Affected:** a management host without local PC Agent/NDI participation or selected Kiloview hardware.

[initial job planning](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/OnboardingService.cs:70>) requires at least one selected device or the server PC. Selecting the server PC requires its local companion. Meanwhile [remote PC staging](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/WindowsPcRemoteOnboardingService.cs:34>) requires an already active job. This creates a circular dependency for an otherwise valid Windows-only remote deployment.

**Evidence:** the real planner rejects an initial job with no hardware devices and no local PC using “Select a device or this server PC to onboard.” The remote path's active-job gate was verified separately in code.

**Suggested fix:** support creating job metadata independently of onboarding a local endpoint or physical device. Then allow remote PCs to join that job. Require KiloLink and hardware-specific settings only for the devices that need them. Test job creation and remote onboarding with no local Agent, WSL, KiloLink or Discovery process, using remote infrastructure as configured.

### F10 — P2: Remote registration can complete an operation for the wrong job or attempt

**Affected:** a job changes or onboarding is retried while a remote PC is applying its configuration.

Pending operations are [keyed and replaced by endpoint ID](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/WindowsPcRemoteOnboardingService.cs:60>). Fetching configuration checks the active job, but [RecordRegistration](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/WindowsPcRemoteOnboardingService.cs:212>) removes/completes whichever operation is pending for that endpoint without matching a job or attempt. The [registration route](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Program.cs:287>) only checks that some current job exists before updating inventory.

**Evidence:** in an isolated real-service fixture, a PC fetched Job1 configuration, the stored active job changed to Job2, and registration still completed the pending operation. The HTTP route's current-job inventory behavior was code-traced rather than exercised against a live service.

**Suggested fix:** generate an attempt ID bound to endpoint GUID, stable job ID/revision and the approved network configuration. Carry it through approval, fetch and registration. Reject/expire stale attempts and validate membership transactionally before reporting completion. A retry must not substitute different configuration after local consent. Test delayed registration after job replacement and overlapping retries.

### F11 — P2: Resolume mixes remote jobs with unrelated local credentials

**Affected:** selecting a remote Job Configurator while a local state file exists, especially reused job names and IP addresses.

[credential enrichment](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/NdiJobConfiguratorReader.cs:87>) reads local Job Configurator state even for a selected remote server. [matching](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/NdiJobConfiguratorReader.cs:98>) checks the job name but not server identity; it then [falls back to IP address](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/src/ResolumeConfigurator/Services/NdiJobConfiguratorReader.cs:111>) if the device ID has no matching credentials. A different device on another server can receive credentials belonging to unrelated local state.

When no local credentials exist, it derives admin/job-name credentials for supported onboarded decoders. This supports the normal provisioned case but is not a general solution for remote devices whose valid credentials differ.

**Evidence:** calling the real merge implementation with synthetic local and remote jobs of the same name/IP but different server and device identities selected the unrelated local credential. No actual credentials were read or transmitted in this fixture.

**Suggested fix:** enrich from local state only when the selected server and exact device identity are verified; remove cross-server IP-only matching. Prefer a bounded, authorized decoder-control API on the owning Job Configurator, which already holds credentials, over exporting secrets or depending on a local state file. Bind such commands to the expected job/device revision. Test client-only operation with no local state, stale local jobs, reused names/IPs and nondefault credentials.

### F12 — P2: Discovery silently truncates larger selected subnets to /24

**Affected:** supported local IPv4 networks such as /23 or /22.

[Job Configurator's scan network](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/NetworkAddressing.cs:33>) clamps the selected prefix to /24–/30. PC Agent's [scan enumeration](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/NetworkService.cs>) makes the same restrictive assumption. An endpoint can be inside the selected subnet and pass the later subnet policy while lying outside the address range actually scanned.

Resolume's newly added discovery considers actual adapter prefixes, so it can find a server on a network where the other applications fail to discover related endpoints.

**Evidence:** the real Job Configurator calculation turns 192.0.2.10/23 into 192.0.2.0/24. The fixture verifies 192.0.3.20 belongs to the selected /23 but is outside the scan range.

**Suggested fix:** scan the selected subnet within explicit host/time budgets, prioritize nearby and previously known addresses, and report incomplete coverage. Offer explicit known-target discovery where consistent with the subnet policy. Avoid replacing the current clamp with an unbounded scan of a very large prefix. Test /23 and /22 hosts across the /24 boundary as well as cancellation and coverage reporting.

### F13 — P2: Download-dependent setup needs built-in connectivity detection and an operation gate

**Added at the user's request:** every setup application that needs internet access to obtain packages must detect whether its required downloads are available and gate the affected installation operation accordingly. This applies equally to standalone setup, Toolkit-launched setup, updates, repair and restart/resume.

**Affected:** primarily Environment Setup's Client and Server paths and PC Agent Setup's NDI Tools download; also package acquisition through Toolkit and the individual updaters. An active LAN adapter is not evidence that internet package sources are accessible.

**Current behavior and recommended ownership:**

| Application / operation | Current evidence | Recommended gate |
| --- | --- | --- |
| Environment Setup — Server install/repair/update | [Installation](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1966>) saves configuration/registers maintenance and enters WSL setup before proving its download dependencies. [WSL preparation](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:998>) can enable Windows features before runtime/update downloads. Later operations require Ubuntu packages, Docker packages/image acquisition and NDI Tools. [Resume readiness](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:2009>) only waits for a usable physical IPv4 adapter. | Check the dependencies needed for the selected operation before configuration/feature/service changes. Check the Windows download path first, then verify the actual WSL package/registry path when WSL is available. Recheck on resume and before each dependent phase. |
| Environment Setup — Client install | [Client sequencing](<C:/Users/jligh/OneDrive/Documents/Kilolink Server Installer + NDI Discovery Server/Install-KiloLinkSuite.ps1:1669>) installs NDI Tools before resolving/downloading PC Agent, so GitHub failure can leave only the first component installed. Internet requirements are described in the wizard, but there is no combined readiness gate. | Resolve, acquire and verify both required packages before launching either installer. Check only Client dependencies: NDI and PC Agent release/package sources. Do not require WSL, Docker or a local server. |
| PC Agent Setup — NDI Tools installation/update | [CheckAsync](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/NdiToolsService.cs:14>) tries the NDI web page and catches connection errors, but its result has no explicit download-readiness state. [InstallNdiAsync](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/MainForm.cs:313>) proceeds to the download when requested. Missing Tools still makes UpdateRequired true even if the online check failed. | Expose package-source readiness separately from installed-version status. Gate the NDI download/install action when the installer cannot be obtained. Preserve Agent installation from its complete local package and LAN operation when the locally installed prerequisites are adequate. |
| Production Toolkit — package acquisition and self-update | [Install availability](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Wrapper Application/src/ToolkitLauncher/AppCard.cs:61>) does not depend on the Offline flag. [Package preparation](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Wrapper Application/src/ToolkitLauncher.Core/PackageService.cs:26>) correctly reuses a verified local package or downloads and verifies one before running setup. Cached release metadata alone does not mean the package is cached. | Distinguish “release information cached,” “complete package verified locally,” and “download required.” Check the package source before acquisition when bytes are missing. Let each launched setup own its downstream prerequisites; a locally cached Environment setup executable does not make its component downloads available offline. |
| Job Configurator installer/updater and PC Agent updater | The Job [installer uses a local companion bundle](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/installer/Install-NDIJobConfigurator.ps1:56>). Its [updater](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/Core/GitHubUpdateService.cs:112>) downloads and verifies before launching setup. The [Agent updater](<C:/Users/jligh/OneDrive/Documents/Kiloview PC Onboarding/Agent/AgentUpdateService.cs:54>) stages and checks its package. | Gate online release/package acquisition and expose clear retryable connection failures. Keep the existing verified-package-before-launch behavior. Do not add a blanket internet requirement to installation from a complete local package. |
| Resolume Configurator | The [release build](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/scripts/Build-Installer.ps1:40>) publishes a self-contained payload; [local setup](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Resolume Configurator/scripts/Install-Local.ps1:43>) copies that payload rather than downloading runtime packages. Runtime communication is with Arena, the selected Job Configurator and devices. | No internet gate is needed for the current complete installer or ordinary LAN configuration. Its acquisition through Toolkit follows Toolkit's download gate. If a future setup adds downloads, declare and check those dependencies within that setup. |

**Evidence:** code inspection of these setup/download paths. This finding does not claim that existing tools lack download error handling or package verification; the missing behavior is a consistent, early, operation-specific readiness gate. No live connectivity disruption or installation was performed for this addendum.

**Recommended behavior:**

1. **Plan dependencies before applying changes.** Compute required components from role, requested action, installed versions and verified local packages. Use a read-only preflight with a short bounded deadline before enabling Install/Update/Repair. A backend check must enforce the same rule so direct executable/script invocation cannot bypass the UI. Checking or downloading into a staging cache is allowed; modifying network settings, enabling features, stopping services or starting vendor installation waits until the applicable prerequisites are ready.

2. **Check real package sources.** Use the same HTTPS/proxy/account context as the eventual downloader to validate release metadata and the selected asset endpoint, including its normal redirects. Distinguish DNS/TLS/proxy failure, service unavailable, access denied/rate limited and missing package. An adapter, ping response, generic website or successful GitHub API response alone is insufficient. A bounded lightweight request is appropriate; handle endpoints that reject HEAD without downloading an entire package as a probe. Preserve certificate, publisher, size and digest validation. Use the available internet route; do not require the selected production/NDI adapter itself to have internet access.

3. **Acquire and verify before installing wherever possible.** For Environment Client, stage NDI Tools and PC Agent together. For Server, acquire Windows payloads first and verify/download Linux dependencies and the container image before replacing the running application where the tooling permits. Some checks require WSL to exist: make these explicit staged prerequisites, retain resumable state and preserve existing service/configuration if that later phase cannot proceed. A successful preflight is not a guarantee against a later connection loss.

4. **Provide clear gating and recovery.** Show states such as Checking downloads, Ready, Ready from local packages, and Required download unavailable. Block only actions that require unavailable packages, name the affected component/source, and offer Retry and Cancel. Refresh after a network change, account/elevation transition, reboot/resume or retry. Use bounded connection and stalled-transfer timeouts with cancellation; do not leave an indefinite spinner or automatically repeat an install/reboot loop. Preserve verified downloads and installation progress where safe; never run a partial or unverified package.

5. **Keep offline operation supported.** Allow a complete, verified local package set and already-satisfied prerequisites to pass without internet access. Keep viewing status, launching installed apps, LAN discovery/configuration and local uninstall available. Represent “latest version could not be checked” separately from “installed component is missing/broken.” If an operation explicitly requires the latest release, explain that it needs an online lookup rather than declaring an offline installation current.

6. **Keep responsibility within each repository.** Define a small versioned prerequisite-status/result contract with component, source, required/download-needed, readiness, reason and retryability. Each setup implements its own authoritative checks; Toolkit may display or invoke them. Do not make a setup depend on a running local Job Configurator, another repository's source tree, or Toolkit itself.

**Acceptance tests to add:** use mocked endpoints and isolated state for offline/DNS failure, captive-portal or wrong-content responses, proxy authentication, TLS failure, GitHub API reachable but release assets blocked, NDI reachable but GitHub unavailable, Windows online but WSL/package registry unavailable, and loss of connection during transfer. Assert that unmet prerequisites prevent installation/configuration mutations, retries recover without duplicate installs, and partial packages never launch. Also test metadata-only cache versus complete verified cache, offline uninstall/launch, Client mode without server dependencies, separate internet and production adapters, and resume after reboot. Full deployment acceptance should cover the actual elevated downloader context.


## Further improvements

1. **Describe all five roots without combining their source or Git history.** [suite.json](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/suite.json>) and [the checked-in workspace](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/NDI-Configurator.code-workspace>) currently model only Job Configurator and PC Agent. Add the other repositories as integration metadata/workspace roots, with roles, supported contracts, versions and optional dependencies. Keep each build, release and updater independent.

2. **Publish small versioned integration DTOs and compatibility fixtures.** Include stable server/job identity, revision and capabilities in public job snapshots. Consumers should not reconstruct ownership from a URL, job name or local internal state file. Use shared test vectors or contract packages with independently pinned versions; do not vendor the companion into the server or merge repositories.

3. **Make unattended operation explicit.** Job Configurator's [startup task](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/installer/Install-NDIJobConfigurator.ps1:176>) requires interactive logon and Agent uses HKCU Run. Environment's server processes have different startup lifetimes. Either document that combined operation requires a signed-in operator, or offer an explicit service-host mode with a separate user consent/status UI. PC configuration must remain companion-owned. Validate reboot and logoff without assuming the word “server” implies Windows Server OS support.

4. **Add Resolume readiness checks before changing Arena.** Verify that the Arena PC is participating in the intended job/NDI discovery/group configuration, required remote services are reachable and decoder/source capabilities are compatible. Explain a missing local Agent as a prerequisite only when this PC actually needs that role. Direct recovery through the companion; do not reintroduce direct PC NDI writes into another app.

5. **Unify installer compatibility rules and shared-runtime ownership.** Environment version normalization currently collapses 0.7.0-dev.2 and 0.7.0 to the same numeric version in the reproduction. Apply consistent prerelease ordering wherever cross-application version decisions are made. Make NDI Tools removal component-aware so uninstalling server components can preserve a runtime still used by clients, Job Configurator or Arena. Keep the already documented uninstall confirmations; this is an ownership improvement, not a claim that removal is currently silent.

6. **Tidy catalog/documentation drift.** Toolkit's [PC Agent catalog icon path](<C:/Users/jligh/OneDrive/Documents/ChatGPT/Wrapper Application/src/ToolkitLauncher.Core/Catalog.cs:15>) still references the older KiloviewSetup asset name. Reconcile current component names, optional-install defaults, port restrictions and topology examples across the five READMEs/handoffs. Distinguish historical notes from the current supported contract.

## Implementation plan for behavior changes

This is a proposed change plan; the review has not applied these fixes. Keep all five Git repositories, their development branches, installers, packages and release ownership separate. Each delivery below should be independently reviewable, with coordinated consumer changes only where its contract changes.

### 1. Add download-readiness gates first — F13

Implement this as separate changes in Environment Setup, PC Agent Setup and Toolkit, with consistent behavior in the Job Configurator and Agent updaters.

| Repository | Planned changes | Completion criterion |
| --- | --- | --- |
| Environment Setup | Add a dependency planner and source-readiness check to Install-KiloLinkSuite.ps1. Invoke it from Client, Server, update/repair and resume entry points. Surface per-component results in SetupWizard.cs / SetupLauncher.cs. Split Client package acquisition from installation so both payloads are verified before either vendor installer launches. Stage Server prerequisites around Windows/WSL readiness and keep existing configuration/services intact when acquisition fails. | Required download failure blocks the affected operation before dependent mutations; Retry recovers; Client never probes Server-only dependencies. |
| PC Agent | Extend NdiToolsStatus and NdiToolsService with separate installed-state, release-check and package-readiness results. Update MainForm to show the missing source and a Retry action, and gate DownloadAndInstallAsync itself as well as the button. Add bounded transfer cancellation and consistent acquisition errors to the Agent updater. | Missing NDI Tools plus an unavailable source yields an actionable blocked download; sufficient local Tools and a complete Agent package still support offline setup/LAN operation. |
| Production Toolkit | Extend AppCard state and PackageService preparation to distinguish cached metadata from a verified complete package. Present readiness before a required transfer; retain package verification and cache reuse. Show downstream setup prerequisite status where its versioned interface is available. Apply the same acquisition behavior to self-update. | Offline users can launch installed apps and use complete verified packages, but cannot start an install that depends on unavailable bytes. Cached Environment setup is never presented as proof that its component packages are available. |
| Job Configurator | Add structured prerequisite/download errors and retryable UI status around GitHubUpdateService. Keep complete-package installation and the bundled companion path offline-capable. | A failed update download leaves the running installation intact and can be retried; normal job management is unaffected. |
| Resolume Configurator | Preserve the current local self-contained installer. Document that its package acquisition may need internet while installation and configured LAN operation do not. Add a readiness contract only if future setup actually downloads dependencies. | A complete local installer works on an isolated production LAN; no unnecessary internet gate is introduced. |

Use explicit states rather than one global Online boolean. Readiness is scoped to the selected action and refreshed on retry/resume; the setup backend remains authoritative even when launched outside Toolkit. Reuse existing signature/digest validation and avoid introducing an alternative unchecked download path.

### 2. Correct installation and shared-host behavior — F01, F03, F04, F06

- **Environment Setup and Job Configurator:** narrow the permissive Environment firewall rules, add Job management access enforcement, and test their effective combined policy. This should precede relying on an all-in-one deployment.
- **PC Agent and Job Configurator installers:** implement validated initiating-user/session ownership for elevated work. Preserve the endpoint GUID, remote consent flow and server-local process contract. Verify the original user's configuration/startup before reporting success.
- **Job Configurator bundle guard and PC Agent installation:** reject older replacement of either newer binary and support recoverable complete-pair installation. Match the Environment installer's retention policy.
- **Environment Setup then Toolkit:** introduce a role/component receipt, followed by Toolkit's role-aware health presentation. For old installations without a receipt, report the role as unverified or infer it only from reliable evidence; do not silently assume Server or create fake server configuration.

Run installer regression coverage and alternate-account/shared-host acceptance before releasing these changes. Preserve complete local installation capability introduced in step 1.

### 3. Make remote onboarding recoverable and tied to a specific job — F02, F07, F09, F10

First define a backward-compatible versioned contract for server/job identity, revision, onboarding attempt ID and the approved network plan. Implement endpoint validation/recovery in PC Agent and matching orchestration in Job Configurator.

The server should support creating job metadata without a local endpoint, reserve target addresses, prevent a retry from replacing an already approved plan, and commit registration only for the matching job/attempt. The companion should validate address readiness, keep a restoration snapshot, roll back failed network changes locally, and reconcile DHCP changes on the selected adapter while retaining its GUID.

Older clients must receive an explicit capability limitation for operations that need the new guarantees; do not silently report the strengthened operation as successful using the old contract. Validate packet loss, delayed registration, job replacement and address changes using isolated fixtures, then controlled deployment testing.

### 4. Remove Resolume's stale-job and local-credential assumptions — F05, F11

Build on the stable server/job/device identities from step 3. Update the Resolume reader, plan, orchestrator and restart worker to revalidate the selected job before mutations and carry the expected revision through completion.

Remove cross-server IP-only credential matching. Introduce a bounded authorized decoder action on the owning Job Configurator, or use a verified exact-identity credential path as an interim fix. Never export the server's general credential store. Preserve Arena backups and present recoverable partial completion when an interruption happens after mutation.

Acceptance must include a client-only Arena PC with no local Job Configurator state, two servers with reused job names/IPs, changed device membership and a job change during restart.

### 5. Align discovery, ports and deployment documentation — F08, F12 and further improvements

Implement bounded discovery over the actual selected subnet in Job Configurator and PC Agent, using compatible coverage semantics in Resolume. Add explicit reporting when a time/host budget leaves discovery incomplete.

For the first compatibility fix, enforce currently supported production defaults and reject conflicting or unsupported port selections before installation. Treat generalized custom-port support as a later versioned contract change, with every consumer and device capability validated before enabling it.

Expand suite/workspace metadata to all five roots and document Client, management-host and combined-host requirements. Correct catalog/version drift. Document current logon-dependent startup truthfully; an optional unattended service mode is a separate deployment feature with its own design and acceptance tests, not an assumed side effect of these fixes.

### Delivery and validation

Use small commits/PRs per repository and behavior change, preserving unrelated working-tree files. Extend the existing regression/validation projects with the failure cases associated with each delivery, and run the affected repository's suites. For server/companion integration changes, run the server regression/frontend/installer suites and both companion validation projects together, using isolated state/configuration paths.

After automated checks, perform the deployment acceptance matrix below on controlled machines: Client only with remote services, management host without a local companion, and all components on one host. Include offline package acquisition, different-account elevation and reboot/resume. Do not use live production NDI/network settings for these tests.

Publish the companion independently first whenever its new contract/package is required, then build the server from that verified clean companion checkout. Release compatible Toolkit/Environment/Resolume changes separately. Record the supported versions and capabilities; publishing or installing one application must never implicitly publish or install another beyond its explicitly selected bundled component.


## Validation performed

All of the following completed successfully against the reviewed local code:

| Repository | Validation | Result |
| --- | --- | --- |
| Job Configurator | tests/Regression/Regression.csproj, Release | 18 checks passed |
| Job Configurator | node --test tests/frontend.test.mjs | 5 tests passed |
| Job Configurator | tests/installer.Tests.ps1 | Package/version/integrity checks passed |
| PC Agent | tests/Kiloview.PcOnboarding.Validation | 11 checks passed |
| PC Agent | tests/Kiloview.PcAgent.Validation | 20 checks passed |
| Environment Setup | tests/Installer.Regression.ps1 | 75/75 passed |
| Production Toolkit | tests/ToolkitLauncher.Tests, Release | 130 assertions passed |
| Resolume Configurator | tests/ResolumeConfigurator.Tests, Release | 33/33 passed |

The existing suites are valuable but do not cover the cross-application scenarios above. Passing them does not establish that the deployment combinations are correct.

The F13 connectivity addendum and implementation plan were added after those validation runs. Their proposed failure-injection and deployment tests are future work; only documentation and source references were checked for these report additions.

Additional isolated evidence:

- [C# reproduction results](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/interop-review-2026-09-06/Reproduction-results.txt>): client-only classification, /23 truncation, Windows-only bootstrap, duplicate static targets, stale-job completion, custom Discovery endpoint rejection and remote/local credential mixing.
- [Installer reproduction results](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/interop-review-2026-09-06/Installer-reproduction-results.txt>): permissive rule parameters, incomplete-newer-package guard and prerelease normalization.
- [C# harness](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/interop-review-2026-09-06/Program.cs>) and [project](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/interop-review-2026-09-06/InteropReview.csproj>): reference locally built application assemblies and use temporary fixture state.
- [PowerShell harness](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/interop-review-2026-09-06/Reproduce-InstallerBoundaries.ps1>): extracts installer functions and mocks firewall commands; it does not execute an installation.

No live installation/uninstallation, publishing, device configuration, NDI setting changes, firewall changes, DHCP changes, alternate-account UAC flow or reboot was performed. Tests used isolated configuration/temp state and local test endpoints. Full installer lifecycle and real hardware/media-flow acceptance remain necessary after fixes.

## Suggested implementation order and acceptance matrix

First address F01–F04 and add the requested download-readiness gates in F13, then address the role/job/identity contracts underlying F05–F11, followed by discovery coverage and documentation. Commit and validate each application independently. Release companion changes first when the server needs a new companion contract/package, then build the server against the verified clean companion checkout.

| Acceptance scenario | Required outcome |
| --- | --- |
| Client only; remote Job Configurator, Discovery and KiloLink | No local server required; correct Toolkit status; Agent joins and Resolume uses selected remote job |
| Management host without local Agent or NDI participation | Can create a Windows-only job and onboard remote PCs |
| All components on one host | Ports do not collide; effective firewall scope remains intended; local PC uses companion process contract |
| Same-user UAC and different-admin UAC | State, startup and endpoint GUID belong to the intended user/machine |
| Mixed installed/bundled versions | Neither newer binary is downgraded; repair is explicit and recoverable |
| DHCP change or failed static transition | Same endpoint is recoverable; no false success or abandoned partial network configuration |
| Job changes while PC onboarding or Arena configuration is pending | Stale operations cannot join or configure the new job; failures precede mutation where possible |
| /23 and /22 selected networks | Discovery reaches both sides of the /24 boundary or accurately reports incomplete coverage |
| Reboot/logoff | Behavior matches the documented interactive or unattended deployment mode |
| Removing Environment server components | Other installed applications retain required shared runtime components when requested |
| Required package source unavailable | Install/update/repair is gated before dependent changes; the missing source is identified and Retry works |
| Metadata cached but package missing; complete verified package available | The first case requires a successful download; the second supports installation offline when all prerequisites are met |
| Connection lost or setup resumes after reboot | Connectivity is rechecked, transfers are bounded/cancellable, and partial packages never launch |
| Isolated production LAN with internet on another adapter | Package checks use the available internet route; local client operations do not require internet or local server components |

These improvements preserve five separate applications and deployments. They strengthen the contracts between them and remove assumptions that all server components are present on every machine.
