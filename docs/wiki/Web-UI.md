# Web UI

`pinqops-ui` is an optional, minimal dashboard for the server. One binary, one
embedded page, no external assets, English + Turkish.

## Install

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops-ui \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops-ui
sudo chmod +x /usr/local/bin/pinqops-ui

sudo pinqops-ui install-service   # systemd service: runs now, starts on boot
sudo journalctl -u pinqops-ui | grep "setup code"
```

Open `http://<server>:7467`, enter the **setup code**, create the dashboard
password. Options: `--port <n>`, `--host <addr>`, `--cert <pfx>` (HTTPS),
`uninstall-service`.

## Views

| Menu | What you get |
|---|---|
| Overview | Container/runner/disk stat cards, run-duration chart, resource chart, compose project, recent workflow runs |
| Deployments | [Deploy history with rollback](Deploy-History-and-Rollback), the compose `.env` editor, workflow runs and the last runner job |
| Runner | Repository runners on GitHub + the local runner (unit, registration, last job) |
| Containers | List with start/stop/restart/inspect — and an **inline log panel** (tail, follow, download) that opens from a container's `logs` button |
| Apps | The [App Catalog](App-Catalog) as a compact list |
| Images / Storage | Images with prune; volumes, disk usage, networks with a visual map |
| **Domains & SSL** | [Caddy reverse proxy](Domains-and-SSL): routes, automatic Let's Encrypt certificates |
| **GitHub** | Sign-in, repository picker, and the [Repository Wizard](Repository-Wizard). Carries a lock icon until connected |
| System | Memory, disk, load, uptime, host + Docker info |
| Settings | Language, auto-refresh, [notification channels](Notifications), dashboard password |

There is deliberately **no deploy button**: deploys happen by merging to
`master`, which is the whole point of the pipeline.

## Security

Setup-code-gated first run, PBKDF2 passwords, bearer sessions, strict CSP
(only the page's own hash-pinned script executes), rate limiting and login
throttling. The dashboard opens one inbound port on an otherwise closed
server — firewall it, serve TLS, or bind `127.0.0.1` behind a tunnel.
Details: [Security Model](Security-Model).
