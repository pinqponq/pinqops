# Setup guide

End-to-end setup, from an empty server to a working `master`-merge deploy — with
**no inbound ports** on the server.

- [1. Branch protection](#1-branch-protection)
- [2. Application Dockerfile](#2-application-dockerfile)
- [3. Server: Docker and a runner user](#3-server-docker-and-a-runner-user)
- [4. Server: install the pinqops CLI](#4-server-install-the-pinqops-cli)
- [5. Server: install the self-hosted runner](#5-server-install-the-self-hosted-runner)
- [6. Server: the application compose project](#6-server-the-application-compose-project)
- [7. Verify end-to-end](#7-verify-end-to-end)
- [Troubleshooting](#troubleshooting)

---

## 1. Branch protection

The deploy workflow triggers on `push` to `master`. Make merge the only way to
reach `master`:

1. GitHub → **Settings → Branches → Add branch ruleset** (or "Add rule").
2. Target branch: `master`.
3. Enable **Require a pull request before merging**.
4. Enable **Block force pushes**.
5. (Recommended) Require at least one approval and require the **CI** check.

With this in place, direct pushes to `master` are rejected, so `on: push`
effectively means "a PR was merged". Pull requests never run on the self-hosted
runner because the workflow triggers only on push to `master`.

## 2. Application Dockerfile

Add a `Dockerfile` at the repository root that builds your application image.
The pipeline is Dockerfile-agnostic — the `build` job just runs `docker build .`.
See [`../examples/app/Dockerfile.example`](../examples/app/Dockerfile.example).

No repository secrets are needed: the `build` job pushes to GHCR with the
automatic `GITHUB_TOKEN`.

## 3. Server: Docker and a runner user

On the production server:

```bash
# Install Docker Engine + the Compose plugin (see docs.docker.com if needed).
docker version
docker compose version

# Use a non-root user for the runner, and add it to the docker group.
sudo usermod -aG docker "$USER"
# Log out/in (or `newgrp docker`) so the group change takes effect.
```

The server needs **outbound** HTTPS (443) to `github.com` and `ghcr.io`. It does
**not** need any inbound port open.

## 4. Server: install the pinqops CLI

`pinqops` ships as a self-contained single-file binary attached to each GitHub
Release — no .NET runtime is required on the server.

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops
sudo chmod +x /usr/local/bin/pinqops
pinqops version
```

(If you prefer to build it yourself, see [CONTRIBUTING.md](../CONTRIBUTING.md).)

## 5. Server: install the self-hosted runner

Get a registration token from **repo → Settings → Actions → Runners → New
self-hosted runner** (the token is short-lived — use it right away), then let
pinqops install and register the runner as a systemd service:

```bash
# Pre-create the install dir owned by your user (config.sh must not run as root).
sudo mkdir -p /opt/actions-runner && sudo chown "$USER" /opt/actions-runner

pinqops install-runner \
  --repo-url https://github.com/<owner>/<repo> \
  --token <registration-token> \
  --user "$USER"
```

`install-runner` downloads the official runner, registers it with the label
`pinqops-prod`, and installs + starts it as a systemd service. Verify:

```bash
sudo /opt/actions-runner/svc.sh status
```

The runner should appear as **Idle** under Settings → Actions → Runners.

> The label `pinqops-prod` must match `runs-on: [self-hosted, pinqops-prod]` in
> [`../.github/workflows/deploy.yml`](../.github/workflows/deploy.yml). Override
> it with `--labels` if you change one — but change both.

## 6. Server: the application compose project

Create the single, fixed application project (default path
`/opt/pinqops/docker-compose.yml`):

```bash
sudo mkdir -p /opt/pinqops
sudo cp deploy/app.docker-compose.example.yml /opt/pinqops/docker-compose.yml
sudo nano /opt/pinqops/docker-compose.yml   # set image: ghcr.io/<owner>/<repo>:latest
```

(If you use a different path, set the repository variable `APP_COMPOSE_PATH`
accordingly — Settings → Secrets and variables → Actions → Variables.)

## 7. Verify end-to-end

1. Open a pull request, get it approved, and **merge into `master`**.
2. Watch **Actions** → the `Build and Deploy` run:
   - `build` (GitHub-hosted) builds and pushes `:latest`;
   - `deploy` (your self-hosted runner) runs `pinqops deploy`.
3. On the server, confirm the new container is running:
   ```bash
   docker compose -f /opt/pinqops/docker-compose.yml ps
   ```

## Troubleshooting

- **`deploy` job stuck on "Waiting for a runner"** — the runner is offline or the
  label doesn't match. Check `sudo /opt/actions-runner/svc.sh status` and that
  the runner's labels include `pinqops-prod`.
- **`pinqops: command not found`** — the binary isn't on PATH for the runner
  service. Install it to `/usr/local/bin/pinqops`.
- **`permission denied while trying to connect to the Docker daemon`** — the
  runner user isn't in the `docker` group. Run `sudo usermod -aG docker <user>`
  and restart the runner service (`sudo /opt/actions-runner/svc.sh stop && start`).
- **Pull fails / old image keeps running** — the package isn't readable by
  `GITHUB_TOKEN`. Ensure the GHCR package is linked to the repository (it is,
  automatically, after the first successful `build`).
- **`install-runner` fails creating `/opt/actions-runner`** — pre-create it and
  `chown` it to your user (see step 5); `config.sh` must not run as root.
- **Need to remove the runner** — `sudo /opt/actions-runner/svc.sh stop`, then
  `sudo /opt/actions-runner/svc.sh uninstall`, then `./config.sh remove --token
  <removal-token>` from `/opt/actions-runner`.
