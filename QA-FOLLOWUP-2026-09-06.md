# QA follow-up — 6 September 2026

The tested QA work was committed in all five repositories before further changes. The subsequent review found and corrected the issues below. The repositories, branches, deployments, independent versions and local schema-1 process contract are preserved.

## Initial QA checkpoints

| Repository | Branch | Checkpoint |
| --- | --- | --- |
| PC Agent | dev | `7a22b74aa507e9fa9a799dd0c55c3f6917684b16` |
| Job Configurator | development | `a68f5e701fd6ed537500eb11d6272b539adfabf1` |
| Environment Setup | main | `f94fddfd148b839152288e9dce348d40f6780489` |
| Production Toolkit | main | `7d97d7ed6febd1b29acb089aba2e43a660be7513` |
| Resolume Configurator | main | `b5a86ca9500f407b8c5c725bb6d4e0c187b7539b` |

## Further corrections

1. **Confirmation queue blockage.** Agent previously retried only the oldest saved outcome. An unreachable old server could indefinitely block confirmation to a later server/job. Retries now rotate across matching attempts, one bounded request per timer tick. Invalid journal files are preserved and reported without blocking valid entries. Notification suppression prevents repeated identical warnings. Malformed final HTTP response JSON also leaves completion queued for retry.

2. **Permission persistence race.** A fetched attempt could become denied, expired or replaced before its receipt was persisted. The server now checks active permission again inside the state update. Regression cases cover both waiting for state access and denial between fetch and persistence, proving no usable receipt survives restart.

3. **Stale Agent identity.** Setup and Agent now validate saved schema, endpoint/adapter GUIDs, usable IPv4/prefix and memberships. Invalid configuration suspends Agent's listener. Monitoring returns a repair error instead of reporting cached valid state while the persisted state is missing or malformed. The GUID and selected adapter still survive legitimate DHCP changes.

4. **Deployment ownership evidence.** Environment now refuses to overwrite unsupported receipts or unknown roles during Client setup. A completed Discovery restoration marker left after successful removal no longer implies an installed server. Client configured-state evidence now rejects incomplete and unusable network identity, consistent with the companion and Toolkit.

5. **Optional metadata isolation.** A malformed optional Agent field, such as a null schema or a nonnumeric prefix, could abort Toolkit's entire Environment evidence read. The optional Agent payload now degrades to invalid configuration while independently valid Server evidence remains available. Invalid component-role receipts are explicitly unknown and cannot be mistaken for legacy Client installations.

6. **Resolume adapter identity.** Readiness previously accepted a saved IP on any local interface. It now requires the selected adapter to be up, its address preferred and prefix matching. The live Agent must also match schema, endpoint, adapter, address and prefix before NDI/job checks permit configuration writes.

## Validation

| Check | Result |
| --- | --- |
| Server regression | 22 passed |
| Server frontend | 8 passed |
| Server installer | Version/retention/manifest/corruption checks passed |
| Companion Setup | 18 PASS groups |
| Companion Agent | 22 PASS groups |
| Cross-repository onboarding | 11 scenarios passed |
| Environment installer | 95 passed; executable rebuilt |
| Toolkit regression | 166 assertions passed |
| Toolkit full build and UI validation | 281 assertions passed; installer built |
| Resolume regression and WPF | 36 passed |
| Real isolated server-to-Resolume HTTP contract | Identity, origin and same-name replacement checks passed |

All new tests use isolated files or intercepted transports. Existing required suites were also run. One new Environment fixture initially used a hashtable while production reads a JSON object; the corrected fixture exercises the production shape and passes. No live software installation, Windows network/firewall change, reboot/logoff, alternate-account UAC or Arena/decoder operation was performed.

All five application packages/installer builds completed. The server bundle's 12 companion manifest files were verified against clean committed Agent source `de352cec106ea64b696ec17d963fc2b4f1da0fda` (`workingTreeChanges: false`). Toolkit and Resolume installers built successfully, and Environment's tracked setup executable embeds the corrected script. These local validation builds retain existing version metadata; no release was published.

The follow-up fixes were committed separately in their owning repositories after validation. Only previously excluded user scratch files remain untracked. Logs, the final commit audit and package checksums are retained under [follow-up artifacts](<C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/artifacts/qa-followup-2026-09-06>). Git whitespace checks passed.

Client-only operation still requires no local server components. Offline LAN operation and verified cached installers remain supported; download gates still check the sources actually needed. Deployment acceptance still requires controlled Windows/UAC/reboot and hardware checks. Independent release publishing remains separate from this local commit/build work.
