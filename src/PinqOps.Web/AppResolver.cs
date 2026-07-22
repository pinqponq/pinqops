namespace PinqOps.Web;

/// <summary>
/// Picks the <see cref="AppConnection"/> a request targets. Callers that never
/// learned about multi-app (older frontend tabs, scripts) send no id and get
/// the sole/first app — exactly the pre-upgrade behavior.
/// </summary>
public static class AppResolver
{
    public static AppConnection Resolve(UiConfig config, string? appId)
    {
        var apps = config.Apps;
        if (!string.IsNullOrWhiteSpace(appId))
        {
            return apps.FirstOrDefault(a => string.Equals(a.Id, appId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown app '{appId.Trim()}'.");
        }

        return apps.Count > 0
            ? apps[0]
            : throw new InvalidOperationException("Connect a repository first.");
    }
}
