# Project task coordination

Established at the user's request on 7 September 2026.

The master is the Codex task `Coordinate Current Dev tasks` in
`Production Toolbox - All Entities`, task ID
`01a07cfa-c36b-7370-b78d-1b15d9d673f7`, host `local`.
It coordinates cross-project requirements, dependencies, sequencing, integration
validation, and results. Implementation stays in the existing project tasks so
the user can continue individual development there with its accumulated context.

## Routing

Project checkout paths and repository identities are defined in `suite.json`.
The task IDs below were verified against the app's project and working-directory
metadata; every listed project task has the exact title `Current Dev`.

| Product / project | Task ID | Host |
| --- | --- | --- |
| NDI Job Configurator / Job Setup - Kiloview Suite | `01a07a95-4cba-7f02-b84d-e4da8aaa3073` | `local` |
| NDI Configurator PC Agent / Kiloview PC Onboarding | Same Job Configurator task, retaining existing suite ownership | `local` |
| Production Toolkit / Wrapper Application | `01a07ce1-bbc7-71d2-b61e-a84749bc4c1f` | `local` |
| Environment Setup / Kilolink Server Installer + NDI Discovery Server | `01a07ce5-9002-72d2-b680-fa8e0ca584ad` | `local` |
| Resolume Configurator | `01a07cf1-28a1-7b43-a9db-ba66594d63fe` | `local` |

There is no separate PC Agent task titled `Current Dev` in the current app
inventory. Its existing suite task owns work in the companion's separate
checkout; the historical onboarding task is not a second concurrent owner.
If the user assigns a dedicated PC Agent task later, update this routing.
For additional projects, find and verify their existing `Current Dev` task when
they become involved. Do not create replacement tasks as part of this workflow.

## Working agreement

- Before dispatching work, inspect the relevant task's current status and recent
  context. Preserve ongoing project work and give a bounded request containing
  the desired outcome, affected contracts, dependencies, acceptance checks,
  existing authorization, and the master task ID for reporting.
- Use the app's existing-task messaging tools to send requests and reports.
  Project tasks send a concise report to the master when cross-project work
  completes, is blocked, needs another project's change, or changes an agreed
  contract. Independent project work continues in its own task; report meaningful
  integration or release implications without routing every local decision
  through the master.
- Reports identify the project and task, changed files or commits, branch and
  version where relevant, validation evidence, unresolved issues, and required
  follow-up. Distinguish source changes, builds, published releases, and actual
  installations. An acknowledgement alone does not require a reply.
- The master consolidates reports and resolves sequencing. Use bounded task waits
  while coordinating active work, and avoid repeated messages for unchanged
  status or acknowledgement loops.
- Each checkout has one active writer. This master shares the Job Configurator
  directory, so it must not edit application files or perform Git mutations there
  while that project's task owns the work. Shared coordination document edits
  must also have a single owner. Project tasks keep their own handoffs current.
- Preserve separate repositories, histories, branches, versions, packages,
  updater feeds, and deployments. Coordination does not combine projects or
  transfer one project's deployment authorization to another.
- Read the relevant `AGENTS.md`, handoffs, `suite.json`, and integration contracts
  before integration changes. PC NDI writes remain with the companion; server
  local invocation and remote approval flows remain as documented.
- Run the required isolated validation in each affected repository. Server/agent
  integration changes require server regression, frontend, installer, and both
  companion validation projects. When a release is authorized, publish the
  companion separately first and bundle its complete package from a clean
  matching checkout into the server.

This agreement establishes coordination and reporting. It is not a request to
resume completed development, publish a release, or install software.
