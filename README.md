# pinqops

**Auto-deploy Docker apps to a fully closed server. Merge to `master` → GitHub
builds the image → a `pinqops setup` runner on your server pulls it and restarts
one Docker Compose project. No inbound ports — no 443, no SSH, no Docker socket.
The runner only dials out.**

![CI](https://github.com/pinqponq/pinqops/actions/workflows/ci.yml/badge.svg)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)

> Vercel-style deploys, but it's *your* closed box. A small self-hosted runner
> holds an **outbound** link to GitHub, so nothing has to be opened inbound.

## Quick start

### 1. In your app repo (GitHub)

- Add a `Dockerfile` at the repo root (see [`examples/app/Dockerfile.example`](examples/app/Dockerfile.example)).
- Copy [`examples/workflows/deploy.yml`](examples/workflows/deploy.yml) to
  `.github/workflows/deploy.yml`.
- Protect `master`: **Settings → Branches** → require a pull request, block force
  pushes. Now "push to `master`" only happens via a merged PR. No repo secrets
  needed — `GITHUB_TOKEN` is automatic.

### 2. On your server

```bash
# Bare box? Install Docker + base tools first (docs/SETUP.md §3).

# Install the pinqops CLI (self-contained binary; no .NET runtime needed).
sudo curl -fsSL -o /usr/local/bin/pinqops \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops
sudo chmod +x /usr/local/bin/pinqops

# One command: checks prerequisites, gets a runner token, installs the runner.
pinqops setup --repo-url https://github.com/<owner>/<repo>
```

`pinqops setup` obtains the runner registration token for you — via the
authenticated `gh` CLI, a personal access token, or one you paste. You need no
git token on the server for deploys; see [docs/TOKENS.md](docs/TOKENS.md).

### 3. Deploy

Merge a PR into `master`, watch **Actions** (`build` → `deploy`), and your server
is live. The full walkthrough (and a manual path) is in
[docs/SETUP.md](docs/SETUP.md).

## How it works

1. A merged PR lands on `master` and triggers GitHub Actions.
2. The **build** job (GitHub-hosted) builds your image and pushes
   `ghcr.io/<owner>/<repo>:latest` to GHCR. Your server never sees the source.
3. The **deploy** job is handed to your **self-hosted runner** over its existing
   outbound link — no inbound port is used.
4. On the server, `pinqops deploy` runs a fixed
   `docker compose pull && up -d`, pulling the private image with the per-job
   `GITHUB_TOKEN`. Deploy is effectively instant.

Diagram and trust boundaries: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## The `pinqops` CLI

```
pinqops setup   --repo-url <url> [--pat <pat>] [--token <token>] [flags]
    Guided onboarding: prerequisites → runner token → install the runner.

pinqops deploy  [--compose-file <path>] [--no-prune] [--timeout-seconds <n>]
    Pull the new image and restart the fixed compose project.

pinqops install-runner --repo-url <url> --token <token> [flags]
    Install a self-hosted runner (the manual half of `setup`).

pinqops version | help
```

Configuration reference: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## FAQ

**Does the server open any inbound port?** No. The runner only makes outbound
connections to GitHub and GHCR.

**Do I need a git token for a private repo?** No. The server never clones the
repo; the private image is pulled with the per-job `GITHUB_TOKEN`. The only token
`setup` needs is a short-lived runner registration token — see
[docs/TOKENS.md](docs/TOKENS.md).

**Is the source copied to the server?** No — only the pre-built image is pulled.

## Docs

| | |
|---|---|
| [SETUP.md](docs/SETUP.md) | End-to-end setup, from a bare server to first deploy |
| [TOKENS.md](docs/TOKENS.md) | Which token goes where, and how to create one |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | The flow and trust boundaries |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | The few knobs that exist |
| [SECURITY.md](SECURITY.md) | Security model and trade-offs |

**Out of scope** (by design): Kubernetes/Swarm, a web UI, multiple
servers/environments, rollbacks or image history (a moving `:latest` is used),
and managing the app's own ports/TLS.

## Contributing

Contributions welcome — open an issue, or see [CONTRIBUTING.md](CONTRIBUTING.md)
for the dev setup (`.NET 10 SDK`, `git clone --recurse-submodules`, tests, PR
flow). Please follow the [Code of Conduct](CODE_OF_CONDUCT.md) and report
security issues privately per [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © pinqops contributors
