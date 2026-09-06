# Main and Development alignment — 6 September 2026

The user requested both products on Main and every channel aligned. Each
repository retains its independent versioning, deployment and updater.

| Product | Main | Development |
| --- | --- | --- |
| Server | `0.8.8` | `0.8.8-dev.1` |
| PC Agent | `0.7.0` | `0.7.0-dev.2` |

Main and Development share the same application code. Only
`Directory.Build.props` contains channel-specific version and release metadata.
Main server packages bundle the released Main companion; Development packages
bundle the released dev companion, identified by a clean source/hash manifest.

The server promotion passed 18 backend checks, five frontend checks, Windows
PowerShell 5.1 installer checks, and release metadata validation. Companion
publication runs both validation projects and builds both package variants in
its own GitHub workflows. Final release revisions, hashes, branch parity and
update-feed verification will be recorded after publication.

This operation publishes software. It does not install updates on the local PC
or change live device, Windows network, or NDI configuration.
