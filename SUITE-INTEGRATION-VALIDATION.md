# Suite integration validation — 6 September 2026

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
