# Suite integration validation — 6 September 2026

## Published deployment

Both independent Development releases are published and their source commits are
pushed. Production feeds and the installed Main server remain unchanged.

| Product | Released source | Release |
| --- | --- | --- |
| Server | `82a0cf7887b611dbb39e7d78d6c33a08ceb2e2e2` on `development` | [v0.8.0-dev.100](https://github.com/JohnDevAc/Kiloview-Job-Configurator/releases/tag/v0.8.0-dev.100) |
| PC Agent | `7f7a8d9eb3d5ccbb75bf3f6d34fd6934f5dc6ba0` on `dev` | [v0.7.0-dev.1](https://github.com/JohnDevAc/Kiloview-PC-Onboarding/releases/tag/v0.7.0-dev.1) |

The companion's [GitHub workflow](https://github.com/JohnDevAc/Kiloview-PC-Onboarding/actions/runs/34039621067)
passed the build, 11 onboarding checks, 20 agent checks, both package publishes,
and release publication. Its self-contained ZIP is 133089062 bytes and has
SHA-256 `816486DE43D61D56CB9F1FCBB39DFFDF2903CDAFE0210A87A03CF35C5BCC32EB`.
The framework-dependent ZIP is 886384 bytes, SHA-256
`6738F5F5F8477BDDA064562373D3B92C7721F837189238D2BED9330A53BDB0FC`.
Both published checksum manifests match their package and GitHub asset digests.

The server was then rebuilt from its release source and the clean companion
checkout. Its bundled companion is built locally by the companion's own script;
the manifest records the released companion commit, `workingTreeChanges:false`,
and all eleven payload hashes. The independent CI and local package archives
have different build/archive bytes; their source revision and product version
are the same. The server manifest identifies the exact bundled files.

Server publication completed at 14:42:14 UTC. Every uploaded asset's size,
SHA-256, and upload state matched the verified local release package before and
after publication. The installer alias is byte-identical, and the embedded ZIP
and companion license match their package/source files.

| Server asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `NDI-Job-Configurator.exe` | 280541124 | `2F1E1078B0797E63157D544DC0EB705E5FF7131E78AC1B4A7A96BB0BCBF818BF` |
| `NDI-Job-Configurator-Windows.zip` | 212331476 | `4971126224004504BD55D5D74FA0BF1175283A39847C57C4EAB0F3FE201A7312` |
| `Kiloview-Job-Configurator.exe` | 280541124 | Same as the primary installer |

The updater selected dev.100 as a channel switch from the installed Main 0.8.7
identity, with the exact published installer size and hash, using a freshly
retrieved authenticated GitHub release-feed snapshot. A direct unauthenticated
request from this PC returned GitHub's 403 rate limit response (60/60 used), with
reset at 15:04:55 UTC / 16:04:55 BST. No credentials were added to the product.

The installed server health endpoint remained `ok`, version `0.8.7`, selected
channel `Main`. Main remains server v0.8.7 and companion v0.6.1. No answer arrived
to the optional local Development installation question during publication, so
neither product was installed and this PC's channel was not changed.

## Initial implementation validation

Implemented on server `development` (`0.8.0-dev.100`, base `cbbf2ba`) and companion
`dev` (`0.7.0-dev.1`, base `91513fd`). This section records the initial validation
before publication. Its packages used the working trees and marked the companion
as changed; release builds must use committed source and clean provenance.

## Results

| Check | Result |
| --- | --- |
| Server backend regression suite | 18 passed |
| Server frontend suite | 5 passed |
| Companion onboarding validation | 11 passed |
| Companion agent validation | 20 passed |
| Installer version ordering / package completeness / corruption checks | Passed under Windows PowerShell 5.1 |
| Changed installer and publish script syntax | Passed under Windows PowerShell 5.1 and PowerShell 7 |
| Release application and installer publish | Passed; no compiler warnings or errors reported |
| Companion Setup and Agent self-contained publish | Passed; no compiler warnings or errors reported |
| Git whitespace checks | Passed in both repositories |

New regression coverage includes companion failure before clean inventory
deletion, server-only plans, uniform server/remote multicast allocations, NDI
configuration drift, active-adapter matching, installed-utility enforcement,
managed group replacement, and agent NDI status. Existing remote approval,
network configuration, multicast, monitoring, update and recovery checks passed.

The actual server assembly was started on an ephemeral loopback port with its
own state and NDI fixture. API checks confirmed that adapter selection persists
only the network, creates no synthetic endpoint, and leaves NDI configuration
unchanged. Component status reports server identity, the legacy NDI preflight
route returns 404, and local reapply without a job returns 409. Web assets loaded.
The owned test process was stopped afterwards.

A browser smoke test used the real frontend with synthetic API fixtures. It
showed server and remote PCs through the common card rendering, the server badge
and reapply action, direct local reapply without a browser confirmation, and a
default-selected server-PC option. A server-only selection enabled Continue;
deselecting it with no devices selected disabled Continue. The browser tab and
temporary fixture server were closed.

The actual companion command process was launched with redirected pipes and an
invalid operation. It returned schema-1 JSON failure and exit code 1 with no UI
or side effects. Successful privileged installation/onboarding was not executed
on the live PC during these checks.

## Packages

| Artifact | Size in bytes |
| --- | ---: |
| `artifacts/NDI-Job-Configurator.exe` | 280541124 |
| `artifacts/NDI-Job-Configurator-Windows.zip` | 212331469 |
| Companion `artifacts/NDI-Configurator-PC-Agent-win-x64.zip` | 133138968 |

The combined installer remains below the updater's 512 MiB limit. The installer
alias is byte-identical. Server `artifacts/SHA256SUMS.txt` and the companion ZIP's
adjacent `.sha256` file contain final checksums.

The temporary `artifacts/bootstrapper-final` build output also remains: automatic
approval review blocked its cleanup with the reason "blocked by policy". This
does not affect the final installers, ZIPs, or verification results.

Verified all eleven companion payload hashes against the server manifest;
checked both products' binary versions; confirmed the embedded installer ZIP
matches the standalone ZIP and the embedded companion license matches its
source; checked all required scripts, web assets, executables, licenses and
handovers; and confirmed no C# source, tests, scratch directories, or Git files
leaked into the package. The blue PC Agent icon contains seven sizes from 16 to
256 pixels, and both companion executables reference it.

## Initial deployment limits

No commit, push, release publication, live installation, device onboarding, or
real NDI/network change was performed for this implementation. Live acceptance
still covers selected/deselected component installation, retaining a newer
installed companion, successful local onboarding without another prompt, remote
approval, and multicast readback. Legacy Windows-PC state is intentionally not
migrated; Windows endpoints need re-onboarding after this upgrade.

See `PC-ONBOARDING-CONTRACT.md` for the maintained ownership and deployment
contract. Commit and publish each repository separately before building a
release against the companion's clean released revision.
