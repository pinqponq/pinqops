# Security Model

Short version — the full document is
[SECURITY.md](https://github.com/pinqponq/pinqops/blob/master/SECURITY.md).

## The core idea

The server only makes **outbound** connections: the runner long-polls GitHub,
and Docker pulls from GHCR. No inbound ports, no SSH-based deploys, no git
token stored on the server.

```
GitHub (cloud)  ──build+push──▶  ghcr.io
      ▲                            │
      │ long-poll                  ▼ pull (outbound)
   runner ◀────────────────── your server
```

## Trust boundaries

- Deploys can only originate from a `master` push — protect the branch so
  merge is the only way in.
- The runner executes repo workflows: keep the repo private, review PRs.
- The web UI is the one optional component that listens on a port.

## Web UI controls

Setup-code-gated first run · PBKDF2-SHA256 (600k) passwords · 256-bit bearer
sessions revoked on password change · per-client login lockout + rate limit ·
strict CSP with a hash-pinned inline script · `frame-ancestors 'none'` ·
64 KB body cap · fixed `docker` argument lists with allowlisted names · PAT
kept in a `0600` file and only ever sent in Authorization headers.

## Hardening checklist

- [ ] Branch protection blocks direct pushes to `master`
- [ ] The repository is private
- [ ] Runner runs as a non-root user in the `docker` group
- [ ] No inbound ports (verify with your firewall)
- [ ] If `pinqops-ui` runs: firewalled port, TLS (`--cert`) or
      `--host 127.0.0.1` behind a tunnel
