# pinqops

**Merge to your default branch → your closed server updates itself.** GitHub builds the
Docker image; a small self-hosted runner on your server pulls it and restarts
one compose project. Outbound-only — no open ports, no SSH, no git token on
the server.

![CI](https://github.com/pinqponq/pinqops/actions/workflows/ci.yml/badge.svg)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)

## Setup

**In your app repo:** add a `Dockerfile`
([example](examples/app/Dockerfile.example)) and copy
[`examples/workflows/deploy.yml`](examples/workflows/deploy.yml) to
`.github/workflows/deploy.yml`.

**On your server:**

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops
sudo chmod +x /usr/local/bin/pinqops

pinqops setup --repo-url https://github.com/<owner>/<repo>
```

**Deploy:** merge a PR into your default branch. That's it. Full walkthrough:
[docs/SETUP.md](docs/SETUP.md).

Every deploy is pinned to the commit's `sha-<...>` image tag, health-checked
after `up -d`, recorded in a deploy history, and reversible with
**`pinqops rollback`** (or one click in the dashboard). Deploy results can be
sent to a webhook, Slack or Telegram.

## Web UI (optional)

A minimal dashboard for the server — containers (with inline logs), images,
volumes, network management with a visual map, workflow runs, runner status
(down to when it last ran a job), and system health. English + Turkish.

The **GitHub** menu is the whole onboarding, as a three-step wizard:
**1 Connect** (OAuth device flow or paste a token) → **2 Choose repository**
(search and pick from the list — no URLs) → **3 Publish**. If the repository
has no Dockerfile, the publish step **detects the stack** (Node, Python, Go,
.NET, Rust, PHP, Ruby, or a static site) and offers a generated, editable
Dockerfile to commit — monorepos included. It then shows the ports up front —
the container side read from your Dockerfile's `EXPOSE` (with a clear warning
and an editable field when there is none), the host side pre-filled with a free
port and validated live as you type — then
commits the deploy workflow, generates the compose file, **registers the
self-hosted runner** (replacing a leftover runner from another repository if
it finds one), streams the install log live, verifies on GitHub that the
runner actually appeared, sets the `APP_COMPOSE_PATH` repository variable, and
kicks off the first build & deploy via `workflow_dispatch`. Once the container
is up, the wizard shows a live "your app is running" card that opens the app.
Subsequent deploys happen the intended way: merge to your default branch.

One server hosts **as many apps as you like**: repeat step 2 for each
repository (the topbar's app switcher jumps between them) — every app gets its
own compose project under `/opt/pinqops/apps/<app>/`, its own deploy history,
`.env`, notifications, and its own runner.

**Real domains with automatic HTTPS** are one click away: the **Domains** page
installs a managed Caddy reverse proxy and points `app.example.com` at your
container with an auto-renewing Let's Encrypt certificate (HTTP/3 included) —
apps stay reachable on their host ports too, and the plain port access keeps
working for anything without a domain.

**Preview environments** give every pull request its own throwaway copy of the
app: open a PR and GitHub builds its image, the runner brings it up as a
separate compose project (`<repo>-pr-<n>`) on a free host port next to
production — reusing production's `.env` minus the pinned image/tag/host-port —
and, when the app has a domain, routes `pr-<n>.<domain>` to it. Closing the PR
tears it down. Only the repository's own PRs are ever built and deployed (a
fork's PR never reaches your server), and a concurrency cap bounds how many run
at once. The Deployments page lists live previews with a manual teardown; repos
on the older workflow get a one-click "update workflow" in the wizard.

**Scheduled backups** cover your data services: the **Backups** page dumps a
database container (PostgreSQL, MySQL, MariaDB, MongoDB, Redis) or any docker
volume on a schedule, with retention, one-click restore, and download — a
background worker runs whatever is due.

**Teams & audit**: invite more users (Settings → Users) with a role —
**viewer** (read-only), **deployer** (deploy & roll back), or **admin**
(everything, including user management) — the same permission levels the API
tokens use. Every change made through the dashboard is written to an
append-only audit log (who, what, result), browsable and filterable on the
**Audit** page. Your existing single password migrates to the first admin
automatically.

**AI agents & the API**: create a scoped API token (Settings → API tokens) and
drive deploys, rollbacks, status, logs, and metrics from any agent. `pinqops
mcp` is a Model Context Protocol server that works with Claude Code/Desktop,
Cursor, and the OpenAI Agents SDK / Codex; the token-authed REST API also works
with plain OpenAI function calling or curl. See
[docs/API-AND-AGENTS.md](docs/API-AND-AGENTS.md).

There is also a curated catalog of ~50 one-click apps (Redis, PostgreSQL,
Grafana, MinIO, …) — installed with generated passwords (retrievable in the
dashboard, reused on reinstall), running in the background with live
pulling → starting progress. The Deployments view shows deploy history with
one-click rollback plus a compose `.env` editor.
You don't have to install the UI; everything also works from the CLI.

```bash
curl -fsSL -o /tmp/pinqops-ui \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops-ui
chmod +x /tmp/pinqops-ui
sudo install /tmp/pinqops-ui /usr/local/bin/pinqops-ui
rm /tmp/pinqops-ui

sudo pinqops-ui install-service   # runs now, survives SSH logout, starts on boot
sudo journalctl -u pinqops-ui | grep "setup code"   # then open http://<server>:7467
```

(`pinqops-ui` with no command runs it in the foreground instead;
`uninstall-service` removes the service.)

On first visit, enter the **setup code** (from the journalctl line above, or
the console when running in the foreground) and create a dashboard password.
Then open the **GitHub** menu (it carries a lock icon until connected), sign
in, pick your repository, and hit Publish. Use `--port <n>` /
`--host <addr>` to change where it listens, and `--cert <pfx>` to serve
HTTPS.

> Heads-up: the UI opens one inbound port on an otherwise closed server.
> Keep it off, firewall it, or bind it to `127.0.0.1` and reach it through a
> tunnel if that matters to you. Details: [SECURITY.md](SECURITY.md).

## Updating

Self-update in place — no curl, no copy-paste:

```bash
sudo pinqops update       # replaces the pinqops binary with the latest release
sudo pinqops-ui update    # replaces pinqops-ui and restarts its service
```

Each downloads the latest release binary, swaps it in atomically, and — for the
dashboard, when it runs as the systemd service — restarts it so the new version
takes over. (Prefer doing it by hand? Re-running the install commands above
still works; the download URLs always point at the latest release.) Check with:

```bash
pinqops version
```

> Releases before v0.3.0 always printed `1.0.0` — a version-stamping bug, not
> a stale binary. From v0.3.0 on, the output matches the release tag.

## CLI

```
pinqops setup            guided onboarding: prerequisites → runner token → runner install
pinqops deploy           pull the new image and restart the compose project
pinqops install-runner   the manual half of setup
pinqops version | help
```

Flags and defaults: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Docs

| | |
|---|---|
| [Wiki](https://github.com/pinqponq/pinqops/wiki) | Guides (sources in [docs/wiki](docs/wiki), incl. a Turkish guide) |
| [SETUP.md](docs/SETUP.md) | Bare server → first deploy |
| [TOKENS.md](docs/TOKENS.md) | Which token goes where |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Flow and trust boundaries |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | The few knobs that exist |
| [SECURITY.md](SECURITY.md) | Security model |

Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) © pinqops contributors
