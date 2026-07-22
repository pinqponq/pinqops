using System.Text.Json;

namespace PinqOps.Notifications;

/// <summary>
/// Notification settings shared by the CLI (which sends after deploys on the
/// runner) and the dashboard (which edits them). Stored as
/// <c>.pinqops/notify.json</c> next to the compose file, 0600 — it can hold a
/// bot token.
/// </summary>
public sealed class NotificationConfig
{
    public EventToggles Events { get; set; } = new();
    public WebhookChannel Webhook { get; set; } = new();
    public SlackChannel Slack { get; set; } = new();
    public TelegramChannel Telegram { get; set; } = new();

    public sealed class EventToggles
    {
        public bool DeploySucceeded { get; set; } = true;
        public bool DeployFailed { get; set; } = true;
        public bool HealthCheckFailed { get; set; } = true;
        public bool RolledBack { get; set; } = true;
    }

    public sealed class WebhookChannel
    {
        public bool Enabled { get; set; }
        public string Url { get; set; } = string.Empty;
    }

    public sealed class SlackChannel
    {
        public bool Enabled { get; set; }
        public string WebhookUrl { get; set; } = string.Empty;
    }

    public sealed class TelegramChannel
    {
        public bool Enabled { get; set; }
        public string BotToken { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
    }

    public bool IsEventEnabled(string eventName) => eventName switch
    {
        NotificationEvents.DeploySucceeded => Events.DeploySucceeded,
        NotificationEvents.DeployFailed => Events.DeployFailed,
        NotificationEvents.HealthCheckFailed => Events.HealthCheckFailed,
        NotificationEvents.RolledBack => Events.RolledBack,
        _ => false,
    };
}

/// <summary>Loads and saves <see cref="NotificationConfig"/> (camelCase JSON, 0600).</summary>
public sealed class NotificationConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public NotificationConfigStore(string composeFilePath)
    {
        _path = PinqOpsStatePaths.NotifyConfigFile(composeFilePath);
    }

    public string Path_ => _path;

    public NotificationConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<NotificationConfig>(File.ReadAllText(_path), SerializerOptions)
                    ?? new NotificationConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt config means "no notifications", never a failed deploy.
        }

        return new NotificationConfig();
    }

    public void Save(NotificationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Atomic + owner-only (0600 from the first byte): this file can hold a
        // Telegram bot token, and File.WriteAllText would both expose it during
        // the create-then-chmod window and — on any save after the first — leave
        // the mode unfixed, as well as risk a torn write that Load reads as
        // corrupt and silently turns notifications off.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
