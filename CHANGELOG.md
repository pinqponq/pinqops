# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to a rolling release model (latest `master` only).

## [Unreleased]

### Added

- **`pinqops-ui`** (`src/PinqOps.Web`) — an optional, self-contained web
  dashboard for the server (default port `7467`). Password-protected; connect
  it to GitHub with the repo URL plus a PAT (or username + token). Shows
  containers (start/stop/restart/logs/inspect/stats), images, volumes,
  networks, Docker disk usage, the compose project, workflow runs, GitHub
  runner status and the last job the self-hosted runner executed, the local
  runner's systemd service, host health (disk/memory/load/uptime), and a
  one-click deploy. Ships as a single binary attached to releases; the CLI
  works fully without it.

### Changed

- **README** compressed further; a web UI is no longer out of scope.
- **release.yml** now also publishes the `pinqops-ui` binary.

## [0.2.1] - 2026-07-15

### Fixed

- **Runner registration failed with "cannot start process './config.sh'".** The
  installer invoked the runner's `config.sh` by a relative path, but .NET
  resolves a relative executable against the current process's directory, not the
  child working directory — so it was not found unless pinqops ran from
  `/opt/actions-runner`. It is now invoked by its full path. Affected both
  `pinqops setup` and `pinqops install-runner`.
- **Registering as root now works.** The installer sets `RUNNER_ALLOW_RUNASROOT=1`
  (ignored for non-root users), so `config.sh` no longer refuses on a root-only
  server.

## [0.2.0] - 2026-07-15

### Added

- **`pinqops setup`** — a guided, one-command onboarding wizard for a fresh
  server: it checks prerequisites (docker, docker compose, tar, systemd) and
  prints exact install commands when any are missing, obtains a runner
  registration token (authenticated `gh` CLI → a PAT via the GitHub API → a
  pasted token), installs and registers the self-hosted runner, and prints the
  remaining compose steps. Scriptable via flags/env and `--non-interactive`.
- **[docs/TOKENS.md](docs/TOKENS.md)** — centralizes registration-token vs PAT
  guidance and explains why deploys need no git token on the server.

### Changed

- **README** slimmed to a one-screen quickstart centered on `pinqops setup`,
  with deeper material linked under `docs/`.
- **docs/SETUP.md** now features `pinqops setup` as the primary path, keeping the
  manual step-by-step as the equivalent/advanced route.

## [0.1.0] - 2026-07-15

### Added

- Initial release of **pinqops**, a minimal DevOps CLI + pipeline for
  auto-deploying Docker apps to a fully closed server (no inbound ports).
- **`pinqops` .NET 10 CLI** (`src/PinqOps.Cli` + `src/PinqOps.Core`):
  - `pinqops deploy` — runs the fixed `docker compose pull && up -d` against a
    fixed compose project (arguments built as discrete list items; no injection).
  - `pinqops install-runner` — downloads, registers, and installs a GitHub
    Actions self-hosted runner as a systemd service (outbound-only; label
    `pinqops-prod`).
- **xUnit tests** (`tests/PinqOps.Core.Tests`) covering command building,
  option validation, the deploy sequence (via a fake process runner), and the
  runner-install orchestration.
- **Workflows:** `ci.yml` (dotnet build + test on PRs) and `release.yml`
  (tag → publish a self-contained linux-x64 `pinqops` binary). A deploy pipeline
  **template** ships under `examples/workflows/deploy.yml` for consumers to copy
  into their own app repo (push to `master` → cloud build + GHCR push → deploy
  job on the self-hosted runner).
- Example files: the fixed application compose project and an example
  application Dockerfile.
- Documentation: README, ARCHITECTURE, SETUP, CONFIGURATION, SECURITY,
  CONTRIBUTING, CODE_OF_CONDUCT, and issue/PR templates.

### Security

- The production server exposes no inbound ports; the runner only dials GitHub
  and GHCR outbound.
- No long-lived secret is stored on the server: registry auth uses the per-job
  `GITHUB_TOKEN`.
- `pinqops deploy` never checks out or executes repository content on the
  server, and the workflow triggers only on `push: master`.
