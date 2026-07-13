# Configuration reference

pinqops is intentionally almost configuration-free. There is no server-side
config file and no long-lived secret to manage. This page lists the few knobs
that exist.

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
    image: ghcr.io/<owner>/<repo>:latest
```

A moving `:latest` tag is used by design; `pull` fetches the new digest and
`up -d` recreates the container.

## GHCR package visibility

The image is private. No token is stored on the server — the `deploy` job
authenticates with the per-job `GITHUB_TOKEN` (granted `packages: read`). This
works as long as the GHCR package is linked to the repository, which happens
automatically after the first successful `build` job.

## `pinqops deploy` options

```
pinqops deploy [--compose-file <path>] [--no-prune] [--timeout-seconds <n>]
```

| Option | Default | Purpose |
|---|---|---|
| `--compose-file` | `$APP_COMPOSE_PATH` or `/opt/pinqops/docker-compose.yml` | The fixed compose project to deploy |
| `--no-prune` | prune enabled | Skip `docker image prune -f` after a successful update |
| `--timeout-seconds` | `300` | Maximum time for the whole deploy |

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

## Workflow permissions

Set per job in the deploy workflow template
[`../examples/workflows/deploy.yml`](../examples/workflows/deploy.yml):

| Job | `contents` | `packages` |
|---|---|---|
| `build` (cloud) | read | write |
| `deploy` (runner) | read | read |
