# Security policy

## Reporting a vulnerability

Please report security issues **privately**. Do not open a public issue for a
suspected vulnerability.

- Preferred: use GitHub's **Report a vulnerability** (Security → Advisories) to
  open a private advisory.
- Alternative: email the maintainers (see the repository owner's profile).

Please include a description, reproduction steps, and impact. We aim to
acknowledge reports within a few business days.

## Threat model

pinqops deploys to a server that exposes **no inbound ports**. The server only
makes outbound connections, which removes the entire class of inbound attacks.

### Assets

- The host Docker daemon (reachable by the runner via the `docker` group).
- The GitHub repository and its workflow definition.

Note there is **no long-lived deploy secret on the server**: the runner
registration token is short-lived, and registry auth uses the per-job
`GITHUB_TOKEN`.

### Controls

| Threat | Control |
|---|---|
| Inbound network attack | The server listens on nothing; only outbound connections exist |
| Deploying from an untrusted event | The workflow triggers only on `push: master`; pull requests never run on the self-hosted runner |
| Untrusted code executing on the runner | `pinqops deploy` does not check out or run repository content; it runs only the fixed compose commands |
| Command injection | Command arguments are built as discrete list items (never a shell string); the compose path is fixed server-side |
| Direct push to `master` | Branch protection requires a reviewed pull request |
| Registry credential leakage | No stored registry secret; `GITHUB_TOKEN` is ephemeral and scoped to `packages: read` on deploy |
| Over-privileged build token | `packages: write` only on the cloud `build` job; `deploy` gets `packages: read` |

### The self-hosted runner trade-off

The runner user is in the `docker` group, which is **root-equivalent** on the
host. This is inherent to running Docker deploys. It is bounded by:

- **Only `push: master` triggers the workflow.** Fork/PR code never reaches the
  runner, which is the primary risk with self-hosted runners.
- **No repo checkout on deploy.** `pinqops deploy` runs only the fixed commands.
- **Keep the repository private.** Public repositories increase the risk of
  someone crafting a workflow/event that targets your runner.

If you need stronger isolation, run the runner in an ephemeral/container mode or
restrict the host Docker API with a socket proxy. These are out of scope for the
base project but compatible with it.

## The web UI (`pinqops-ui`)

The optional dashboard is the one component that **does** listen on a port
(default `7467`), which is why it is optional. If you run it, its built-in
controls are:

| Threat | Control |
|---|---|
| First-visitor claims an unconfigured dashboard | Creating the password requires a one-time **setup code** printed only on the server console |
| Password brute force | Per-client lockout (5 failures → 15 min) + per-client request rate limit + slow failure responses |
| Weak password storage | PBKDF2-SHA256, 600k iterations, per-password salt, constant-time compare; legacy hashes upgrade on login |
| Session theft/abuse | 256-bit random bearer tokens, 24h sliding expiry, capped session table; **all sessions revoked on password change** |
| XSS / script injection | Strict CSP — only the page's own inline script (pinned by SHA-256 hash) can execute; all rendered values are HTML-escaped |
| Clickjacking / embedding | `frame-ancestors 'none'` + `X-Frame-Options: DENY` |
| CSRF | Auth is a Bearer header (never a cookie), so cross-site requests carry no credentials |
| Token/PAT leakage | PAT stored in a `0600` config file, sent only in Authorization headers, returned to the UI only masked |
| Command injection | Fixed `docker` argument lists; container ids/actions validated against strict allowlists |
| Oversized/hostile requests | Request bodies capped at 64 KB; process calls time-bounded |
| Plain-HTTP interception | Optional TLS via `--cert <pfx>` (HSTS enabled); or bind `--host 127.0.0.1` and reach it through a tunnel |

The dashboard still opens one inbound port on an otherwise closed server —
firewall it to trusted addresses, keep TLS on if it crosses a network you do
not own, or simply do not run it.

## Hardening checklist

- [ ] Branch protection blocks direct pushes to `master`.
- [ ] The repository is private.
- [ ] The runner runs as a non-root user that is in the `docker` group.
- [ ] The server has **no** inbound ports open (verify with your firewall/host).
- [ ] Outbound access is limited to what's needed (`github.com`, `ghcr.io`).
- [ ] The runner label in the deploy workflow matches the installed runner.
- [ ] If `pinqops-ui` runs: its port is firewalled to trusted addresses, and it
      serves TLS (`--cert`) or binds `127.0.0.1` behind a tunnel.

## Supported versions

This project follows a rolling model — only the latest `master` is supported.
Fixes are released as new commits/tags; there is no long-term maintenance branch.
