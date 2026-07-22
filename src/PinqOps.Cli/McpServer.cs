using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PinqOps;

namespace PinqOps.Cli;

/// <summary>
/// A minimal Model Context Protocol server over stdio, exposing a pinqops
/// dashboard's REST API as agent tools. MCP is an open standard, so this works
/// with any MCP client — Claude Code / Claude Desktop, Cursor, the OpenAI Agents
/// SDK / Codex, and others. It runs on the operator's machine and calls the
/// dashboard over HTTPS with an API token (PINQOPS_URL + PINQOPS_TOKEN), so it
/// never needs an inbound port on the server.
/// </summary>
public static class McpServer
{
    private const string ProtocolVersion = "2024-11-05";

    private sealed record Tool(string Name, string Description, object InputSchema);

    private static readonly Tool[] Tools =
    [
        new("list_apps", "List the app repositories this pinqops server manages.", NoArgs()),
        new("deploy_status", "Current deploy state and live container status of an app.", AppArg()),
        new("deploy_history", "Recent deploy history of an app (tags, results, timestamps).", AppArg()),
        new("trigger_deploy", "Start a build & deploy of an app (workflow_dispatch). Requires a deploy-scope token.", AppArg()),
        new("rollback", "Roll an app back to a previous image tag. Requires a deploy-scope token.", RollbackArgs()),
        new("app_metrics", "Live CPU/memory of running containers (docker stats).", NoArgs()),
        new("container_logs", "Recent logs of a container by id or name.", LogsArgs()),
    ];

    public static async Task<int> RunAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("PINQOPS_URL");
        var token = Environment.GetEnvironmentVariable("PINQOPS_TOKEN");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("pinqops mcp: set PINQOPS_URL and PINQOPS_TOKEN (a 'pot_…' token from Settings → API tokens).");
            return 1;
        }

        var handler = new HttpClientHandler();
        if (Environment.GetEnvironmentVariable("PINQOPS_INSECURE") is "1" or "true")
        {
            // Homelab servers often use a self-signed cert; opt-in only.
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Console.Error.WriteLine($"pinqops mcp: serving {Tools.Length} tools against {baseUrl}");
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument request;
            try
            {
                request = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (request)
            {
                var response = await HandleAsync(request.RootElement, http);
                if (response is not null)
                {
                    Console.WriteLine(response);
                    await Console.Out.FlushAsync();
                }
            }
        }

        return 0;
    }

    private static async Task<string?> HandleAsync(JsonElement message, HttpClient http)
    {
        var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
        var hasId = message.TryGetProperty("id", out var id);
        // Notifications (no id) get no response.
        if (!hasId)
        {
            return null;
        }

        return method switch
        {
            "initialize" => Ok(id, new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { tools = new { } },
                serverInfo = new { name = "pinqops", version = PinqOpsVersion.Current },
            }),
            "ping" => Ok(id, new { }),
            "tools/list" => Ok(id, new
            {
                tools = Tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }),
            }),
            "tools/call" => await CallToolAsync(id, message, http),
            _ => Error(id, -32601, $"Unknown method '{method}'"),
        };
    }

    private static async Task<string> CallToolAsync(JsonElement id, JsonElement message, HttpClient http)
    {
        if (!message.TryGetProperty("params", out var prms) || !prms.TryGetProperty("name", out var nameEl))
        {
            return Error(id, -32602, "Missing tool name");
        }

        var name = nameEl.GetString();
        var args = prms.TryGetProperty("arguments", out var a) ? a : default;
        string? App() => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("app", out var v) ? v.GetString() : null;
        string Query() => App() is { Length: > 0 } app ? $"?appId={Uri.EscapeDataString(app)}" : string.Empty;

        try
        {
            var text = name switch
            {
                "list_apps" => await GetAsync(http, "api/settings"),
                "deploy_status" => await GetAsync(http, $"api/deploy/state{Query()}"),
                "deploy_history" => await GetAsync(http, $"api/deploy/history{Query()}"),
                "app_metrics" => await GetAsync(http, "api/docker/stats"),
                "trigger_deploy" => await PostAsync(http, $"api/setup/trigger-deploy{Query()}", null),
                "rollback" => await PostAsync(http, $"api/deploy/rollback{Query()}",
                    new { tag = args.TryGetProperty("tag", out var t) ? t.GetString() : null }),
                "container_logs" => await GetAsync(http,
                    $"api/docker/containers/{Uri.EscapeDataString(args.GetProperty("container").GetString() ?? "")}/logs"),
                _ => throw new InvalidOperationException($"Unknown tool '{name}'"),
            };
            return ToolResult(id, text, isError: false);
        }
        catch (Exception exception)
        {
            return ToolResult(id, exception.Message, isError: true);
        }
    }

    private static async Task<string> GetAsync(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path);
        return await ReadAsync(response);
    }

    private static async Task<string> PostAsync(HttpClient http, string path, object? body)
    {
        using var content = new StringContent(body is null ? "{}" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content);
        return await ReadAsync(response);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }

    // ---- JSON-RPC framing ---------------------------------------------------------

    private static string Ok(JsonElement id, object result) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), result });

    private static string Error(JsonElement id, int code, string messageText) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), error = new { code, message = messageText } });

    private static string ToolResult(JsonElement id, string text, bool isError) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = IdValue(id),
            result = new { content = new[] { new { type = "text", text } }, isError },
        });

    private static object? IdValue(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.GetInt64(),
        JsonValueKind.String => id.GetString(),
        _ => null,
    };

    // ---- tool input schemas -------------------------------------------------------

    private static object NoArgs() => new { type = "object", properties = new { } };

    private static object AppArg() => new
    {
        type = "object",
        properties = new { app = new { type = "string", description = "App id (optional; defaults to the only/first app)." } },
    };

    private static object RollbackArgs() => new
    {
        type = "object",
        required = new[] { "tag" },
        properties = new
        {
            app = new { type = "string", description = "App id (optional)." },
            tag = new { type = "string", description = "Image tag to roll back to (from deploy_history)." },
        },
    };

    private static object LogsArgs() => new
    {
        type = "object",
        required = new[] { "container" },
        properties = new { container = new { type = "string", description = "Container id or name." } },
    };
}
