# Main and Development alignment — 6 September 2026

Both products are published on Main and Development, with every channel aligned.
Each repository retains its independent versioning, deployment and updater.

| Product | Main | Development |
| --- | --- | --- |
| Server | [0.8.8](https://github.com/JohnDevAc/Kiloview-Job-Configurator/releases/tag/v0.8.8) | [0.8.8-dev.1](https://github.com/JohnDevAc/Kiloview-Job-Configurator/releases/tag/v0.8.8-dev.1) |
| PC Agent | [0.7.0](https://github.com/JohnDevAc/Kiloview-PC-Onboarding/releases/tag/v0.7.0) | [0.7.0-dev.2](https://github.com/JohnDevAc/Kiloview-PC-Onboarding/releases/tag/v0.7.0-dev.2) |

Main and Development share the same application code. Only
`Directory.Build.props` contains channel-specific version and release metadata.
Main server packages bundle the released Main companion; Development packages
bundle the released dev companion, identified by a clean source/hash manifest.

## Released sources and publication

| Product/channel | Source commit | Published UTC |
| --- | --- | --- |
| Server Main | `4488bb4abf7c177e468edaf5fc46a61021daeb07` | 15:04:42 |
| Server Development | `e7b6e88f9ff37170b996660c05c37136c052f572` | 15:05:46 |
| PC Agent Main | `bca96659cb520238257ae1e970b0f7ab34822b2e` | 14:52:23 |
| PC Agent Development | `c651f1ef8bc773e151c7057f51baaea91919a03f` | 14:53:49 |

Both server releases were staged as drafts, checked against local assets, then
published with the correct branch/prerelease flags. Main is the latest stable
release in both repositories. Tags resolve to the exact source commits above.
Documentation commits after these tags do not change the released application.

## Validation

- Server promotion: 18 backend checks, five frontend checks, Windows PowerShell
  5.1 installer checks, and release metadata validation passed.
- Companion [Main CI](https://github.com/JohnDevAc/Kiloview-PC-Onboarding/actions/runs/34040445007)
  and [Development CI](https://github.com/JohnDevAc/Kiloview-PC-Onboarding/actions/runs/34040510657)
  passed builds, 11 onboarding checks, 20 agent checks, both Windows package
  variants, and release publication.
- Both server packages passed binary version/source checks, clean companion
  manifests, all eleven component file hashes, required contents, embedded ZIP
  and companion license matching, installer alias identity, and updater size
  limits. Each server bundles its corresponding clean companion channel.
- Both server assemblies started with isolated state and served their packaged
  frontend with the expected build identity. The Development check also verified
  saving its update-channel selection. Build channel and saved operator update
  preference are intentionally separate; a fresh state defaults to Main.
- All sixteen uploaded assets across four releases were checked for expected
  sizes and SHA-256 values. Companion checksum manifests match the published ZIP
  digests; server embedded payloads and release checksum manifests match locally.
- Full tree comparisons between each repository's Main and Development branches
  show only `Directory.Build.props`; all code, tests, workflows and documentation
  are identical. Both development branches contain the Main promotion history.
- Fresh unauthenticated GitHub feeds passed seven server update-selection cases:
  upgrades from Main 0.8.7 and dev.100, current-version detection on both channels,
  and switching both directions. The PC Agent updater offers stable 0.7.0 to
  0.6.1, recognizes 0.7.0 as current, and rejects prereleases as Main metadata.

## Primary release artifacts

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Server Main installer | 280540596 | `318dd0309ca772eb07f620f70af449783080488ab5073adac8a33d64fbaa92c7` |
| Server Development installer | 280541120 | `ba120caabf58594fcf90d72b2e3bfeea65d5a7f7f0db650047849fd8235c5f13` |
| PC Agent Main self-contained ZIP | 133089006 | `94f4cb6f5e8f0b24d7463bd3c14db066f9de4ae4d3d837be9250588b9946cb8e` |
| PC Agent Development self-contained ZIP | 133089029 | `bf69cdac645c99356805d1893660f0cd8bed6322b6a61cc6302e5c9928f3d8d5` |

Server ZIP sizes/hashes are 212331131 bytes /
`5d46a69ac04858ceeeca0aa2e6b1bc4f560387854430a3eac45e000e7dcfa642`
for Main, and 212331254 bytes /
`ee49fe1667dbfb748a5a47993e0c9ee2a92105c3e105aa481dd8051a937fa7ed`
for Development. Each legacy installer alias is byte-identical to its primary
installer. Staged release files and verification records are retained under
`artifacts/releases/v0.8.8` and `artifacts/releases/v0.8.8-dev.1`.

The independent companion CI ZIPs and locally rebuilt bundled component have
different build/archive bytes. The server manifest records the exact bundled
files and released source revision, rather than claiming CI archive identity.

## Installed PC

The installed server remains healthy on Main 0.8.7. Its actual update endpoint
offers Main 0.8.8 without a channel switch, with the exact published installer
size and SHA-256 above. The earlier unauthenticated GitHub rate limit reset;
live update checks succeeded during this promotion.

This operation published software without installing updates locally or changing
live device, Windows network, or NDI configuration. Existing Windows endpoint
registrations require re-onboarding after the operator installs this upgrade.
