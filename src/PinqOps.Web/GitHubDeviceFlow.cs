using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// "Sign in with GitHub" via the OAuth device flow: the browser shows a short
/// code, the user confirms it on github.com, and polling yields a token — no
/// client secret and no inbound callback needed. Requires an OAuth App client
/// id (Settings or PINQOPS_GITHUB_CLIENT_ID) with device flow enabled. The
/// sensitive device_code never leaves the server; the browser only holds an
/// opaque handle.
/// </summary>
public sealed class GitHubDeviceFlow : IDisposable
{
    private sealed record Pending(string ClientId, string DeviceCode, int IntervalSeconds, DateTimeOffset ExpiresAt);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public GitHubDeviceFlow(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<object> StartAsync(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var payload = await PostAsync(
                "https://github.com/login/device/code",
                new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = "repo" })
            .ConfigureAwait(false);

        if (GetString(payload, "device_code") is not { } deviceCode
            || GetString(payload, "user_code") is not { } userCode)
        {
            throw new InvalidOperationException(
                GetString(payload, "error_description")
                ?? "GitHub did not return a device code — check the OAuth App client id and that device flow is enabled.");
        }

        var interval = payload.TryGetProperty("interval", out var i) && i.TryGetInt32(out var seconds) ? seconds : 5;
        var expiresIn = payload.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var exp) ? exp : 900;

        var handle = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _pending[handle] = new Pending(clientId, deviceCode, interval, DateTimeOffset.UtcNow.AddSeconds(expiresIn));

        return new
        {
            handle,
            userCode,
            verificationUri = GetString(payload, "verification_uri") ?? "https://github.com/login/device",
            intervalSeconds = interval,
            expiresInSeconds = expiresIn,
        };
    }

    /// <summary>One poll step. Status: pending | success | denied | expired.</summary>
    public async Task<(string Status, string? Token)> PollAsync(string handle)
    {
        if (!_pending.TryGetValue(handle, out var pending))
        {
            return ("expired", null);
        }

        if (pending.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _pending.TryRemove(handle, out _);
            return ("expired", null);
        }

        var payload = await PostAsync(
                "https://github.com/login/oauth/access_token",
                new Dictionary<string, string>
                {
                    ["client_id"] = pending.ClientId,
                    ["device_code"] = pending.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                })
            .ConfigureAwait(false);

        if (GetString(payload, "access_token") is { Length: > 0 } token)
        {
            _pending.TryRemove(handle, out _);
            return ("success", token);
        }

        switch (GetString(payload, "error"))
        {
            case "authorization_pending":
            case "slow_down":
                return ("pending", null);
            case "access_denied":
                _pending.TryRemove(handle, out _);
                return ("denied", null);
            default:
                _pending.TryRemove(handle, out _);
                return ("expired", null);
        }
    }

    private async Task<JsonElement> PostAsync(string url, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("pinqops-ui");

        using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub device-flow request failed ({(int)response.StatusCode}).");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose() => _httpClient.Dispose();
}
