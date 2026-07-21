# Configuration reference

pinqops is intentionally almost configuration-free. The little server-side
state that exists lives next to the compose file and is written by pinqops
itself. This page lists the knobs that exist.

## Server-side files and permissions

| Path | Written by | Mode | Contents |
|---|---|---|---|
| `<compose-dir>/.env` | `pinqops deploy`/`rollback`, dashboard env editor | 0600 | `PINQOPS_IMAGE`, `PINQOPS_TAG`, `PINQOPS_HOST_PORT`, `PINQOPS_CONTAINER_PORT` + your app env |
| `<compose-dir>/.pinqops/history.json` | deploy engine | 0600 | Deploy history (newest first, capped at 100) |
| `<compose-dir>/.pinqops/notify.json` | dashboard (read by the CLI) | 0600 | Notification channels + event toggles |
| `~/.config/pinqops/ui.json` | dashboard | 0600 | Dashboard password hash, GitHub connection (PAT) |
| `~/.config/pinqops/app-credentials.json` | dashboard | 0600 | Generated catalog app credentials |

## The compose project pinqops generates

| | |
|---|---|
| Project name | Your repository name, so containers read `<repo>-app-1` |
| Image | `${PINQOPS_IMAGE}:${PINQOPS_TAG}` — both pinned by `pinqops deploy`, so the image follows the repository even after a rename |
| Published port | `${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT}` — the container side is read from your Dockerfile's `EXPOSE` (`80` when there is none), the host side is the first free port from `8080`. The dashboard's publish wizard shows both up front and lets you override them before (or after) going live |

### Changing the port

`PINQOPS_HOST_PORT` (and `PINQOPS_CONTAINER_PORT`) are ordinary `.env` values:
edit them in the dashboard's **Deployments → Environment (.env)** editor and
press **Apply** — no YAML editing, no redeploy. From a shell it is the same file:

```bash
sudo nano /opt/pinqops/.env                                    # PINQOPS_HOST_PORT=81
docker compose -f /opt/pinqops/docker-compose.yml up -d
```

`PINQOPS_IMAGE` and `PINQOPS_TAG` are rejected by the editor — every deploy
re-pins them, so a manual edit would silently disappear.

A host port that is out of range or **already bound on the server** is rejected
too. That matters because `docker compose up -d` removes the old container
before creating the new one: a port clash would fail the deploy *and* leave the
app stopped. pinqops does not probe the port at deploy time — by then the app's
own container holds it, so every redeploy would look like a conflict.

## Runner label

The `deploy` job targets the runner with:

```yaml
runs-on: [self-hosted, pinqops-prod]
```

The label `pinqops-prod` is assigned when you install the runner
(`pinqops install-runner --labels pinqops-prod`, the default). If you change the
label, change it in **both** places.

## Application compose path

The `deploy` job passes this path to `pinqops deploy`:

```yaml
APP_COMPOSE_PATH: ${{ vars.APP_COMPOSE_PATH || '/opt/pinqops/docker-compose.yml' }}
```

- Default: `/opt/pinqops/docker-compose.yml`.
- To override, set a **repository variable** `APP_COMPOSE_PATH`
  (Settings → Secrets and variables → Actions → **Variables**).

`pinqops deploy` also reads `APP_COMPOSE_PATH` from its environment when
`--compose-file` is not given.

## Image reference

The application compose file references the image the pipeline builds:

```yaml
services:
  app:
    image: ghcr.io/<owner>/<repo>:${PINQOPS_TAG:-latest}
```

Every build pushes both `:latest` and an immutable `sha-<commit>` tag. The
deploy job passes `--tag sha-<commit>`, which pins `PINQOPS_TAG` in the compose
directory's `.env` — that is what enables deploy history and
`pinqops rollback`. Without a `.env` (or with a plain `:latest` reference) the
old moving-tag behavior applies unchanged.

**Migrating from ≤0.4:** change the `image:` line to the interpolated form
above; nothing else is required. Until you do, deploys keep working but history
records tag `latest` and rollback is refused with a clear error.

## GHCR package visibility

The image is private. No token is stored on the server — the `deploy` job
authenticates with the per-job `GITHUB_TOKEN` (granted `packages: read`).

A `GITHUB_TOKEN` has no intrinsic access to a package: it can read one only
because the package is **connected to the repository**. The generated workflow
establishes that connection with the `org.opencontainers.image.source` label on
the built image. Do not rely on it happening implicitly — in particular,
**renaming a repository does not rename its packages**. A push under the new name
creates a *new* package whose connection is independent of the old one, which is
the usual way a deploy ends up able to push but not pull.

### `403 Forbidden` pulling your own image

`docker login` printing `Login Succeeded` only proves the token is valid; GHCR
checks package access later. `Login Succeeded` followed by `403` on a manifest
request means **authenticated but not authorized** — the package is not readable
by this repository. Check the connection:

```bash
gh api /user/packages/container/<package> --jq '{visibility, repo: .repository.full_name}'
```

If `repo` is `null` or an old name, open the package → **Package settings** →
**Manage Actions access** → **Add repository** → pick the repository, role
**Write** — then re-run the failed job. The image does not need rebuilding.

## `pinqops deploy` options

```
pinqops deploy [--compose-file <path>] [--tag <image-tag>] [--no-prune]
               [--timeout-seconds <n>] [--health-timeout-seconds <n>] [--keep-images <n>]
```

| Option | Default | Purpose |
|---|---|---|
| `--compose-file` | `$APP_COMPOSE_PATH` or `/opt/pinqops/docker-compose.yml` | The fixed compose project to deploy |
| `--tag` | — | Image tag to pin as `PINQOPS_TAG` in the project's `.env` (CI passes `sha-<commit>`) |
| `--no-prune` | prune enabled | Skip image cleanup after a successful update |
| `--timeout-seconds` | `300` | Maximum time for the whole deploy |
| `--health-timeout-seconds` | `60` | Wait for services to be running/healthy after `up -d`; `0` skips the check |
| `--keep-images` | `5` | How many recent `sha-*` images to keep locally for rollback |

## `pinqops rollback` / `pinqops history`

```
pinqops rollback [--to <tag>] [--compose-file <path>] [--health-timeout-seconds <n>]
pinqops history  [--compose-file <path>] [--json]
```

`rollback` defaults to the last successful tag before the current one (from
deploy history) and skips the registry pull when the image is still local —
which it is, within the retention window. If the image is gone, the pull needs
a `docker login ghcr.io` with a token that has `read:packages`. There is no
automatic rollback: a failed health check marks the deploy failed and notifies,
and the revert is always an explicit operator action.

## Notifications (`.pinqops/notify.json`)

Managed from the dashboard (Settings → Notifications) and read by the CLI on
every deploy/rollback. Channels: generic webhook (full JSON payload), Slack
incoming webhook (also Discord `/slack` and Mattermost), Telegram bot
(token + chat id). Per-event toggles: deploy succeeded / deploy failed /
health check failed / rolled back. Delivery is best-effort with a 5s
per-channel timeout and never affects the deploy result.

## `pinqops install-runner` options

```
pinqops install-runner --repo-url <url> --token <token> [options]
```

| Option | Required | Default | Purpose |
|---|---|---|---|
| `--repo-url` | ✅ | — | `https://github.com/<owner>/<repo>` (or env `REPO_URL`) |
| `--token` | ✅ | — | Short-lived registration token (or env `RUNNER_TOKEN`) |
| `--labels` | — | `pinqops-prod` | Must match `runs-on` in the deploy workflow |
| `--name` | — | `<hostname>-pinqops` | Display name on GitHub |
| `--version` | — | `2.319.1` | Runner release to download |
| `--dir` | — | `/opt/actions-runner` | Install directory |
| `--user` | — | current user | User the systemd service runs as |

## `pinqops setup` options

```
pinqops setup --repo-url <url> [options]
```

| Option | Default | Purpose |
|---|---|---|
| `--repo-url` | — (prompted) | `https://github.com/<owner>/<repo>` (or env `REPO_URL`) |
| `--pat` | — | GitHub PAT to mint a registration token via the API (or env `GITHUB_PAT`) |
| `--token` | — | A registration token you already have (or env `RUNNER_TOKEN`) |
| `--compose-file` | `/opt/pinqops/docker-compose.yml` | App compose path to reference (or env `APP_COMPOSE_PATH`) |
| `--no-gh` | gh enabled | Don't use the `gh` CLI to mint a token |
| `--skip-preflight` | preflight on | Skip the docker/compose/tar/systemd check |
| `--non-interactive` | auto if stdin redirected | Never prompt; fail if an input is missing |
| `--labels` / `--name` / `--version` / `--dir` / `--user` | as `install-runner` | Pass-throughs to the runner install |

The token fallback chain is: `--token` → authenticated `gh` CLI → `--pat` via the
GitHub API → a pasted token. The PAT is used once and never stored. See
[TOKENS.md](TOKENS.md).

## Workflow permissions

Set per job in the deploy workflow template
[`../examples/workflows/deploy.yml`](../examples/workflows/deploy.yml):

| Job | `contents` | `packages` |
|---|---|---|
| `build` (cloud) | read | write |
| `deploy` (runner) | read | read |
