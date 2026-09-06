# Suite ownership

This workspace is responsible for both NDI Job Configurator and NDI Configurator
PC Agent. Read `CODEX-HANDOFF.md`, `suite.json`, and the companion's handoff before
changing their integration. Open `NDI-Configurator.code-workspace` for both roots.

- Keep the server and companion as separate Git repositories and deployments.
  Resolve the companion checkout through `suite.json`; do not vendor its source,
  move its `.git`, or compile it into the server.
- Server development uses `development`; companion development uses `dev`.
  Each repository has its own `main`, version metadata, tags, assets, and updater.
- PC NDI configuration belongs to the companion. The server invokes the installed
  utility using the local process contract in `PC-ONBOARDING-CONTRACT.md`.
  Do not restore the removed direct local configuration service or `local-pc`
  synthetic endpoint. Windows endpoints share the agent GUID and monitoring path.
- A server-local invocation needs no second confirmation. It uses the server's
  existing elevation and the license accepted during optional component install.
  Remote PCs retain their local Yes/No and UAC flow.
- For integration changes, validate the server regression/frontend/installer
  suites and both companion validation projects. Use isolated state/configuration
  paths; building or testing must not install software or alter live NDI settings.
- Build the companion with its own publish script. The server installer bundles
  that complete package and records its version, commit, and file hashes.
  Keep the optional component selected by default and retain newer installed agents.
- For releases, commit and publish the companion separately first, then build the
  server against that clean checkout. Review both Git diffs and release metadata;
  publishing one repository never implies publishing or installing the other.

See each repository's handoff for current work and validation evidence. Preserve
unrelated user files and scratch reports when staging changes.
