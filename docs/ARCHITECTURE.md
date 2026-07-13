# Architecture

This document describes the components, the deploy lifecycle, and the trust
boundaries of pinqops.

## Goals and constraints

- The production server exposes **no inbound ports** — no 443, no SSH, no Docker
  daemon over TCP. It only makes **outbound** connections.
- A deployment happens **only** when a commit reaches `master` (via merged pull
  request).
- The deploy is **instant** (no polling): it runs the moment CI finishes.
- No long-lived secrets are stored on the server.
- The deploy engine is a small, testable .NET CLI that can grow into a broader
  DevOps toolkit.

## The core idea

A closed server cannot receive a webhook. Instead, an agent on the server holds
an **outbound** connection open to GitHub, and GitHub pushes work down that
connection. That agent is a **GitHub Actions self-hosted runner**. This is the
same pattern hosted platforms (e.g. Vercel) use: the deploy side is already
connected outbound, so nothing needs to be opened inbound.

## Components

### 1. `build` job — GitHub-hosted runner (cloud)

Runs on `ubuntu-latest`. Builds the application image from the repository's
`Dockerfile` and pushes it to GHCR as `:latest`. The production server is never
involved in the build and never receives the source code.

### 2. Self-hosted runner — production server

The official GitHub Actions runner, installed as a **systemd service** by
`pinqops install-runner`. It:

- dials `github.com` over **outbound** HTTPS and keeps the connection open;
- registers with the label `pinqops-prod`;
- receives and executes the `deploy` job when master's workflow runs;
- runs as a user in the `docker` group so it can talk to the host Docker daemon.

### 3. `pinqops` CLI — the deploy engine

A .NET 10 console app distributed as a self-contained single-file binary. It has
two commands today:

- `pinqops deploy` — runs the fixed `docker compose pull && up -d` against the
  fixed compose file.
- `pinqops install-runner` — downloads, registers, and installs the self-hosted
  runner as a systemd service.

The interesting logic lives in `PinqOps.Core` behind small interfaces
(`IProcessRunner`, `IFileDownloader`) so it is unit-tested without Docker or the
network. The CLI (`PinqOps.Cli`) is a thin argument-parsing layer over it.

### 4. Application compose project — production server

A single, fixed `docker-compose.yml` (e.g. `/opt/pinqops/docker-compose.yml`)
whose service references `ghcr.io/<owner>/<repo>:latest`.

## Deploy lifecycle

```
Pull Request
   │  (review + merge — direct pushes to master are blocked)
   ▼
push: master ──► GitHub Actions
                    │
                    ├─ build job (GitHub-hosted, cloud)
                    │     docker build .
                    │     push ghcr.io/<owner>/<repo>:latest ─────► GHCR (private)
                    │
                    └─ deploy job (assigned to the self-hosted runner
                       over its OUTBOUND link — no inbound port used)
                             │  runs ON the production server:
                             │  docker login ghcr.io (per-job GITHUB_TOKEN)
                             │  pinqops deploy --compose-file <fixed>
                             │    → docker compose -f <fixed> pull  ◄── pulls from GHCR (443 outbound)
                             │    → docker compose -f <fixed> up -d
                             ▼
                       new container running
```

## Trust boundaries

| Boundary | What crosses it | Control |
|---|---|---|
| GitHub → server | The deploy job, over the runner's outbound link | Only `push: master` triggers it; PRs never run on the runner |
| pinqops → Docker daemon | Fixed compose commands | Discrete argument lists; no repo checkout; `docker` group access |
| Server → GHCR | Image pull (443 outbound) | Per-job `GITHUB_TOKEN` with `packages: read` |
| Server → GitHub | Runner control channel (443 outbound) | Runner registration (short-lived token at install) |

### The `docker` group / daemon boundary

The runner user is in the `docker` group, which is **root-equivalent** on the
host — the same practical trust level Watchtower/Portainer require. Mitigations:

- `pinqops deploy` never checks out or executes repository content on the server;
  it runs only the fixed compose commands.
- Only `push: master` can trigger the workflow, so untrusted pull-request code
  never reaches the self-hosted runner.
- Keep the repository private.

See [`../SECURITY.md`](../SECURITY.md) for the full threat model.

## Design decisions

- **Self-hosted runner over webhook/polling** — a webhook needs an inbound port;
  polling adds latency. A runner keeps the server fully closed *and* deploys
  instantly.
- **Build in the cloud, deploy on the runner** — the production server never
  builds and never receives the source; it only pulls a ready image.
- **Per-job `GITHUB_TOKEN` for GHCR** — no long-lived registry secret on the
  server.
- **Moving `:latest` tag** — keeps the deploy command static and matches the
  no-rollback scope.
- **.NET CLI with a testable core** — the team is a .NET shop; the deploy engine
  and installer are one small binary designed to grow into a wider DevOps toolkit.
