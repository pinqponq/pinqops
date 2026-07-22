# API tokens, MCP, and AI agents

pinqops exposes its whole dashboard as a REST API, and ships an **MCP server** so
AI agents can drive deploys, rollbacks, status, logs, and metrics. Because
[MCP](https://modelcontextprotocol.io) is an open standard and the API is plain
HTTP + bearer tokens, this works with **any** agent — Claude Code / Claude
Desktop, Cursor, the OpenAI Agents SDK / Codex, LangChain, or your own script.

## 1. Create an API token

Dashboard → **Settings → API tokens** → Create. Pick a scope:

| Scope | Can do |
|---|---|
| `read` | All `GET`s: list apps, deploy status/history, logs, metrics. |
| `deploy` | `read` + trigger a deploy, roll back, apply env, install catalog apps, run a backup/restore. |
| `admin` | Everything, including settings, domains, backups config, and token management. |

The token (`pot_…`) is shown **once**. Store it as `PINQOPS_TOKEN`. Every
request sends it as `Authorization: Bearer pot_…`; a token used beyond its scope
gets `403` with a message saying which scope was needed.

## 2. MCP server (`pinqops mcp`)

The MCP server runs on **your** machine (not the server) and calls the dashboard
over HTTPS with your token, so it honors pinqops' "no inbound port" model. It
exposes these tools: `list_apps`, `deploy_status`, `deploy_history`,
`trigger_deploy`, `rollback`, `app_metrics`, `container_logs`.

It needs the `pinqops` binary on your PATH and two env vars:

```
PINQOPS_URL=https://pinqops.example.com   # your dashboard
PINQOPS_TOKEN=pot_…                        # the token from step 1
PINQOPS_INSECURE=1                         # optional: accept a self-signed cert
```

### Claude Code

```bash
claude mcp add pinqops --env PINQOPS_URL=https://pinqops.example.com \
  --env PINQOPS_TOKEN=pot_… -- pinqops mcp
```

### Claude Desktop / Cursor (`mcp.json` / `claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "pinqops": {
      "command": "pinqops",
      "args": ["mcp"],
      "env": { "PINQOPS_URL": "https://pinqops.example.com", "PINQOPS_TOKEN": "pot_…" }
    }
  }
}
```

### OpenAI Agents SDK / Codex

The OpenAI Agents SDK speaks MCP over stdio — point it at the same command:

```python
from agents.mcp import MCPServerStdio

pinqops = MCPServerStdio(params={
    "command": "pinqops",
    "args": ["mcp"],
    "env": {"PINQOPS_URL": "https://pinqops.example.com", "PINQOPS_TOKEN": "pot_…"},
})
# add `pinqops` to your Agent's mcp_servers=[...]
```

Codex and other MCP-aware CLIs use the same `command` / `args` / `env` shape in
their MCP config.

## 3. Plain REST (OpenAI function calling, curl, CI)

No MCP needed — any HTTP client works. The same token, the same scopes:

```bash
curl -H "Authorization: Bearer $PINQOPS_TOKEN" https://pinqops.example.com/api/settings
curl -H "Authorization: Bearer $PINQOPS_TOKEN" -X POST \
  "https://pinqops.example.com/api/setup/trigger-deploy?appId=acme-shop"
```

For an OpenAI function-calling agent, wrap the endpoints you need as tools (the
model calls them, your code makes the HTTP request). Useful ones:

| Purpose | Method + path |
|---|---|
| List apps | `GET /api/settings` → `apps[]` |
| Deploy status | `GET /api/deploy/state?appId=<id>` |
| Deploy history | `GET /api/deploy/history?appId=<id>` |
| Trigger deploy | `POST /api/setup/trigger-deploy?appId=<id>` |
| Roll back | `POST /api/deploy/rollback?appId=<id>` `{ "tag": "sha-…" }` |
| Container metrics | `GET /api/docker/stats` |
| Container logs | `GET /api/docker/containers/<id>/logs` |

`appId` is optional; with one app it defaults to that app.
