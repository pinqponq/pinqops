# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to a rolling release model (latest `master` only).

## [Unreleased]

### Changed

- **`PINQOPS_IMAGE` is now rejected by the dashboard `.env` editor**, alongside
  `PINQOPS_TAG`. Every deploy re-pins both, so a hand edit silently disappeared.
- **The dashboard auto-starts a stopped runner instead of asking for a click.**
  The deployment-readiness card now reads the runner's systemd state: an
  installed-but-stopped service is started automatically (idempotent), so the
  only thing a user runs is the GitHub wizard. When the service is running but
  GitHub still shows it offline — where a restart would not help — the row points
  at the runner's logs (usually a network/clock issue) instead of offering a
  "start" button that does nothing.
- **Split the "Storage & Networks" dashboard view into separate "Storage" and
  "Networks" tabs.** Storage keeps volumes and Docker disk usage; Networks holds
  the network list (create/remove/connect) and the visual network map. Each
  loads independently.

### Removed

- **Domains & SSL (Caddy reverse proxy).** The managed `pinqops-caddy` reverse
  proxy, its dashboard view, routes store, Caddyfile generator, and the
  `/api/proxy` endpoints have been removed — the feature was more surface area
  than the tool needs. Publish app ports directly, or run your own proxy. The
  shared `pinqops-apps` network stays for catalog-app connectivity.

### Added

- **The generated compose project now publishes a port, so a deployed app is
  actually reachable.** Previously the template left `ports:` commented out: the
  container came up but `docker ps` showed only `80/tcp` with nothing mapped. The
  wizard now reads the container port from the repository's own Dockerfile
  (`EXPOSE`, falling back to `80`) and writes
  `ports: ["${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT}"]`, seeding both
  values into the project `.env`. Changing the published port is a `.env` edit in
  **Deployments → Environment** plus **Apply** — no YAML editing.
- **Containers are named after the repository.** The compose project name was the
  fixed string `pinqops`, so every deployment's container was `pinqops-app-1`
  regardless of what was deployed — and indistinguishable from the catalog apps.
  It is now the repository name (reduced to compose's grammar the same way
  compose would), so the container reads `<repo>-app-1`, e.g. `peramice-app-1`.
- **The deployed image now follows the repository automatically (no more stale
  compose after a rename).** The generated compose references
  `image: ${PINQOPS_IMAGE:-…}:${PINQOPS_TAG:-latest}`, and `pinqops deploy
  --image ghcr.io/${{ github.repository }}` (passed by the generated workflow)
  pins `PINQOPS_IMAGE` in the project `.env` before pulling — just like the tag.
  Rename the repository and the next deploy pulls the new image with zero manual
  intervention. Before pulling, pinqops verifies the compose resolves to the
  expected image; an image line hand-edited to hardcode a name (the classic cause
  of an opaque `403`/`denied` on pull) fails fast with the exact fix.
- **Dashboard: runner service logs and multi-runner visibility (Runner view).**
  The Runner view now lists every `actions.runner.*` service on the host (a
  server can carry more than one after re-registering to a new repository) with
  its live state, and a **logs** button shows each service's last 100 journal
  lines — enough to diagnose a runner that is registered but not picking up jobs
  without opening an SSH session.

### Fixed

- **Atomic, owner-only writes for secret state.** `ui.json` (GitHub PAT),
  `app-credentials.json` (plaintext app passwords), the compose `.env`, and
  deploy history are now written via a temp-file-plus-rename that
  creates the file `0600` *before* any bytes are written. This closes a
  create-then-`chmod` window where secrets briefly existed at the process umask,
  and prevents a crash mid-write from truncating `ui.json` and silently dropping
  the dashboard back to the unauthenticated setup flow.
- **Docker argument injection hardening.** Container/network names that begin
  with `-` are now rejected, and every dashboard docker
  call passes `--` before the user-supplied positional, so a crafted name can no
  longer be parsed as a docker flag.
- **Image retention no longer trusts `docker images` ordering.** Retention now
  sorts `sha-*` tags by `CreatedAt` before keeping the newest N, so an
  out-of-order-built or re-pulled image can't cause the newest image (the one a
  rollback needs) to be deleted.
- **Dashboard robustness.** `GitHubDashboardService` no longer disposes an
  injected `HttpClient`, and its JSON reader tolerates `null` nodes (e.g. a
  workflow run with `actor: null`) instead of throwing and failing the whole
  overview. Malformed `docker --format json` lines are skipped rather than
  discarding every result.
- **Auth & input hardening.** The first-run setup code is widened to 64 bits and
  the setup endpoint is now covered by the brute-force throttle; generated
  passwords use rejection-sampled selection (no modulo bias); the OAuth
  device-flow handle table is swept and capped; `install-service` validates its
  arguments before writing the systemd unit; and repository owner/name parsing
  enforces GitHub's character set.

## [0.5.0] - 2026-07-19

### Added

- **Safe deploys: SHA tags, history, health checks and rollback.** Builds now
  push an immutable `sha-<commit>` tag alongside `:latest`, and
  `pinqops deploy --tag sha-<commit>` pins it in the compose project's `.env`
  (compose file references the image as `:${PINQOPS_TAG:-latest}` — fully
  backward compatible without a `.env`). After `up -d` the services are
  health-checked (`compose ps` until running/healthy, default 60s,
  `--health-timeout-seconds`, 0 skips); every deploy is recorded in
  `.pinqops/history.json` next to the compose file. New commands:
  **`pinqops rollback [--to <tag>]`** (defaults to the last successful tag;
  uses the locally kept image, so no registry login needed) and
  **`pinqops history [--json]`**. Instead of blanket image pruning, the newest
  N `sha-*` images are kept for rollback (`--keep-images`, default 5). There is
  deliberately **no automatic rollback** — a failed deploy shows red in CI and
  rolling back is an explicit operator action. The dashboard's Deployments view
  gains a deploy-history card with the current version and a one-click
  **Roll back** button.
- **Notifications.** Deploy results (success, failure, health-check failure,
  rollback) are sent to a generic **webhook** (full JSON), **Slack**-compatible
  incoming webhooks (also Discord `/slack`, Mattermost) and **Telegram** bots.
  Configured per event and per channel in `.pinqops/notify.json` (0600) next to
  the compose file, so CLI deploys on the runner and dashboard rollbacks both
  notify. Settings UI with per-channel Test buttons. Best-effort by design: a
  notification failure never fails a deploy.
- **Generated catalog passwords + credential storage.** Catalog apps no longer
  ship hardcoded defaults (`postgres/pinqops` etc.) — every credential is
  generated per install (CSPRNG, 20 chars) and stored 0600 in
  `~/.config/pinqops/app-credentials.json`. The dashboard shows them after
  install and behind a key button on installed apps (masked, reveal/copy). A
  reinstall reuses the stored password so data in surviving volumes keeps
  working; WordPress automatically receives the MySQL app's password. A guard
  test keeps hardcoded passwords from coming back.
- **Compose `.env` editor.** The Deployments view manages the compose project's
  `.env` (masked values, `PINQOPS_TAG` shown read-only) with an explicit
  *Apply* that recreates containers via `compose up -d`.
- **Domains & SSL (Caddy reverse proxy).** A new dashboard view installs a
  managed `pinqops-caddy` container publishing 80/443 with automatic Let's
  Encrypt certificates (persisted in named volumes). Routes map a domain to a
  container port over the shared `pinqops-apps` network; the Caddyfile is
  generated from strictly validated fields and hot-reloaded. The generated
  compose template now joins `pinqops-apps` so the deployed app is routable by
  container DNS.
- **First web test project** — `tests/PinqOps.Web.Tests` covers catalog
  password substitution, the credential store, docker run arguments, the
  Caddyfile generator (golden + injection rejection) and the Caddy service
  sequences.

### Changed

- `examples/workflows/deploy.yml`, the dashboard's generated workflow/compose
  templates and `deploy/app.docker-compose.example.yml` moved to the SHA-tag +
  `${PINQOPS_TAG:-latest}` scheme. Existing users: add the interpolation to
  your compose file's `image:` line to enable history/rollback — nothing breaks
  if you don't.
- `docker image prune -f` after deploys is replaced by tag-aware retention
  (keep `latest` + newest N `sha-*`), then a dangling-layer prune.

## [0.4.0] - 2026-07-18

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
- **`pinqops-ui install-service`** — installs the dashboard as a systemd
  service (enabled + started), so it keeps running after the SSH session ends
  and comes back after a reboot. `uninstall-service` removes it; the first-run
  setup code is in `journalctl -u pinqops-ui`. Also adds `version` / `help`
  subcommands.
- **Sign in with GitHub** — the dashboard can authenticate via the OAuth
  device flow (bring your own OAuth App client id; no secret, no callback
  port), or with a pasted token as before. Either way it now shows who is
  signed in and lets you **pick the repository from the list of repos your
  account is authorized for** instead of typing a URL.
- **Turkish localization** — the entire dashboard is available in Türkçe
  (EN/TR switch in the top bar; auto-detected from the browser, remembered).
- **Docker network management** — create networks (driver + internal flag),
  inspect them (subnet, gateway, attached containers), connect/disconnect
  containers, and remove non-built-in networks, all from the Storage &
  Networks panel.
- **Professional visual refresh** — grouped sidebar with vector icons, a
  refined dark palette, consistent buttons/inputs/chips, focus states, and
  polished tables/cards across every view.
- **Portainer-style onboarding.** No repository URL typing: picking a repo
  from the authorized list connects it immediately, and a new *Deployment
  readiness* panel checks the whole pipeline — Dockerfile present, deploy
  workflow present, runner installed/online, compose project present — and
  fixes what it can: one click commits `.github/workflows/deploy.yml` to the
  repo, generates the server compose file for the repo's GHCR image, or
  **installs and registers the self-hosted runner from the dashboard**
  (registration token via the stored PAT, same code path as
  `pinqops install-runner`). A missing Dockerfile is called out as the only
  thing expected from the repo.
- **App catalog.** ~50 curated one-click installs (Redis, PostgreSQL, MySQL,
  MongoDB, RabbitMQ, Kafka, Elasticsearch, MinIO, Grafana, Prometheus,
  Uptime Kuma, Gitea, Jenkins, Keycloak, Vaultwarden, Nextcloud, n8n, …)
  grouped by category with search, editable host port, open-in-browser links,
  and safe removal (volumes kept). The API only accepts fixed catalog specs —
  it can never be used to run an arbitrary image.
- **Network map.** The Storage & Networks panel renders a live SVG diagram of
  which containers sit on which Docker networks.

### Changed

- **README** compressed further; a web UI is no longer out of scope.
- **release.yml** now also publishes the `pinqops-ui` binary.

### Fixed

- **Setup screen flipped to the login form mid-paste.** The dashboard's
  auto-refresh timer ran before sign-in; its 401 responses switched the
  first-run setup form into the password login form and cleared the inputs
  (typically while pasting the setup code). Refresh now only runs once signed
  in, and a 401 only returns to the login screen when a real session expires.
- **`pinqops version` always reported `1.0.0`.** The release workflow never
  stamped the git tag into the published binaries, so every release carried the
  SDK's default assembly version — updating the binary looked like a no-op even
  though the code changed. `release.yml` now passes `-p:Version=<tag>` to both
  publishes, the CLI prints the stamped (informational) version, and the web UI
  shows it in the sidebar footer and its startup line.

### Security

- **`pinqops-ui` hardening.** First-run password creation requires a one-time
  setup code printed on the server console; login and password change are
  brute-force throttled (per-client lockout) on top of a per-client API rate
  limit; PBKDF2 iterations raised to 600k (legacy hashes upgrade on login);
  all sessions are revoked on password change; a strict Content-Security-Policy
  pins the dashboard's inline script by SHA-256 hash; hardened response headers
  (`X-Frame-Options`, `nosniff`, `Referrer-Policy`, COOP/CORP, HSTS on TLS);
  request bodies capped at 64 KB; optional HTTPS via `--cert <pfx>`; auth
  events are logged; the unauthenticated state endpoint no longer reveals
  whether GitHub is configured. See the new web-UI section in SECURITY.md.

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
