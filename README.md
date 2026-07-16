# pinqops

**Merge to `master` → your closed server updates itself.** GitHub builds the
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

**Deploy:** merge a PR into `master`. That's it. Full walkthrough:
[docs/SETUP.md](docs/SETUP.md).

## Web UI (optional)

A dashboard for the server — containers, images, volumes, logs, workflow
runs, runner status (down to when it last ran a job), system health, and a
one-click deploy. You don't have to install it; everything also works from
the CLI.

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops-ui \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops-ui
sudo chmod +x /usr/local/bin/pinqops-ui

pinqops-ui        # open http://<server>:7467
```

On first visit, create a dashboard password. Then, in **Settings**, paste
your repository URL and a token — a PAT alone, or a username + token. Use
`--port <n>` / `--host <addr>` to change where it listens.

> Heads-up: the UI opens one inbound port on an otherwise closed server.
> Keep it off, firewall it, or bind it to `127.0.0.1` and reach it through a
> tunnel if that matters to you.

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
| [SETUP.md](docs/SETUP.md) | Bare server → first deploy |
| [TOKENS.md](docs/TOKENS.md) | Which token goes where |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Flow and trust boundaries |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | The few knobs that exist |
| [SECURITY.md](SECURITY.md) | Security model |

Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) © pinqops contributors
