# Sources, conflicts and verification

[Documentation home](README.md) · [N6](N6.md) · [N60](N60.md)

Prepared 7 September 2026. Public sources were retrieved online during this documentation task. Device behavior was established from existing code and historical reports; no current hardware session was opened.

## Public primary references

| Reference | Use in this documentation |
| --- | --- |
| [N6 API landing page](https://www.kiloview.com/en/docs/n6/api/) | Confirms the advertised v3.0 document |
| [N60 API landing page](https://www.kiloview.com/en/docs/n60/api/) | Identifies the advertised v2.01 document; available via search indexing, with intermittent direct-fetch timeouts |
| [N5/N6 API v3.0 PDF](https://enstatic.kiloview.com/wp-content/uploads/2025/09/NEW-N5ampN6-APIEN-version-3.0-1.pdf) | Cross-check of legacy API routes; 73 PDF pages |
| [N60 Web API v2.01 PDF](https://enstatic.kiloview.com/wp-content/uploads/2025/09/N60-WEB-API-EN-Version2.01.pdf) | Cross-check of legacy request shapes; 72 PDF pages |
| [Device HTTP API firmware module](https://www.kiloview.com/en/docs/device-http-api/10-firmware-module/) | Corroboration of the shared older firmware-upload fields |

The PDFs are referenced rather than redistributed as part of this documentation. Pages cited with `#page=` count from the PDF's first page; printed page numbers can differ. Technical recipes are independently written from the project implementation. The public manuals provide cross-checks and pointers for features outside this integration's scope.

Public firmware pages were also searched, but exact latest-build verification was not needed to describe the tested integration. No statement that a tested build is currently the newest is made.

## Project provenance

Repository: `JohnDevAc/Kiloview-Job-Configurator`.

Historical source baseline: commit `9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9`, version `0.8.9-dev.1`, then on `development`. Source links below are pinned to that commit. The device adapter files were clean relative to that baseline when inspected, and their hashes were rechecked after drafting. Publication on 31 October 2026 preserves this dated reference; it does not reclassify its examples as the behavior of later releases. The [documentation home](README.md) links to the current adapters separately.

| Source | Relevant evidence |
| --- | --- |
| [N6 adapter](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/Devices/N6DeviceApi.cs) | Exact paths, JSON types, cookies, role safeguards, source layouts and per-source transport |
| [N60 adapter](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/Devices/N60DeviceApi.cs) | Exact paths, raw token header, root responses, HB mode, TTL type and blanking exceptions |
| [HTTP helper](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/Devices/DeviceApi.cs) | Common response parser, cookie handling, multipart layout and first-login probing |
| [Onboarding service](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/Core/OnboardingService.cs) | Operation order, firmware version wait, reconnect handling and high-level completion |
| [Device monitor](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/Core/DeviceMonitor.cs) | Poll cadence |
| [Regression fixtures](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/tests/Regression/Program.cs) | Offline multipart compatibility and duplicate-error/reconnect coverage |
| [Project README](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/README.md) | Workflow context, source links and older caveats; not preferred over newer source/report evidence |
| [Project handoff](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/CODEX-HANDOFF.md) | Historical releases and device test context |

### SHA-256 of key source material

```text
Devices/N6DeviceApi.cs
51f31d1f71cb8ee6c5fdf9cd8a067d19e2877473669c6f9a10dd7e55d8051eac

Devices/N60DeviceApi.cs
37aa6bc19e8c3fbce3eb1e68a6be0d9a3a031762dc10a28546d3f8b9c0052e04

Devices/DeviceApi.cs
7f90c51803a4d9e7a01873c433edf61dac4a2d6b4bb248aa80b61023c0bab49c

N5/N6 v3.0 PDF retrieved from the URL above
7a30f7ebf1771f9510dac73faa2ffe200c4c405841d1211604525ffceb18a005

N60 v2.01 PDF retrieved from the URL above
84dacbd1bb9d6fbe967f75d0f19ad54417726851963520833fe65c39ef8e5b0b
```

## Historical hardware evidence

| Report | What it supports | Limit |
| --- | --- | --- |
| Local report `E2E-MULTICAST-TEST-REPORT-2026-08-13.md` | N60 encoder API allocation disagreed with captured media destinations; decoder blanking errors occurred | Not distributed; encoder firmware not recorded; historical result, not newly reproduced |
| Local report `E2E-N6-ENCODER-MULTICAST-TEST-REPORT-2026-08-14.md` | N6 `2.00.0009.0134` multicast sender, traffic within allocation, N60 `2.45.0014.0170` reception, visible Studio Monitor picture | Not distributed; N60 blank-colour cleanup still failed in that test |
| [5 September onboarding report](https://github.com/JohnDevAc/Kiloview-Job-Configurator/blob/9d431ce25f83e37e0f05f3301ade5b2ac8ac9cd9/E2E-ONBOARDING-TEST-2026-09-05.md) | Successful corrected N6 firmware upload to `2.10.0011.0885`; three decoder onboarding/identity/completion results | Initial upload failed and needed recovery; N60s were not flashed; physical screens not independently observed |

The August multicast documents remain local workspace evidence and are not distributed with this reference. Their summarized observations cannot be independently reproduced from this documentation alone. Subsequent code fixes and September API checks do not prove that the historical N60 encoder multicast-allocation issue is resolved.

## Conflict resolution

| Topic | Basis used |
| --- | --- |
| N6 receiver transport | Current source supports per-source edits; older README wording is incomplete |
| N6 KiloLink/license routes | Candidate paths remain labeled as probes; an overall onboarding pass does not prove every fallback |
| N60 modern request shapes | Current code defines the worked examples; older manual differences are annotated in the N60 reference |
| N60 TTL and blank colour | Serializer-sensitive project behavior takes precedence in worked examples |
| API success versus actual media | Readback, packet capture and visual inspection are kept distinct |

## Documentation validation

Original 7 September validation passed: 35 JSON examples parsed, all 3 PowerShell examples passed the PowerShell parser, dummy cookie construction worked, and all 42 then-local document links resolved. A path inventory found documentation coverage for all 44 distinct request-path literals in each adapter, including clearly labeled compatibility candidates and resolved stream-path templates. This checks coverage, not universal endpoint availability. Whitespace and source-hash checks passed. Selected vendor PDF pages were rendered and visually inspected where table layout or exact field casing mattered. The authentication examples and firmware requests were not executed against devices.

The 31 October publication changes documentation only: source links are pinned, the historical scope is explicit, and unpublished local reports are no longer broken relative links. JSON/PowerShell example syntax and published local-link targets are checked again before commit. No application logic, installer, PC Agent integration or version metadata changes; no build, installation, firmware upload or live NDI operation is needed for this documentation publication.
