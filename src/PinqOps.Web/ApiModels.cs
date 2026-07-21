namespace PinqOps.Web;

public sealed record PasswordRequest(string? Password);

public sealed record SetupRequest(string? Password, string? SetupCode);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record SettingsRequest(
    string? RepoUrl,
    string? Username,
    string? Pat,
    string? ComposeFile,
    string? RunnerDirectory,
    string? GithubClientId);

public sealed record TokenRequest(string? Pat, string? Username);

public sealed record DeviceStartRequest(string? ClientId);

public sealed record DevicePollRequest(string? Handle);

public sealed record NetworkCreateRequest(string? Name, string? Driver, bool Internal);

public sealed record NetworkContainerRequest(string? Container);

public sealed record AppInstallRequest(string? Id, int? HostPort, int[]? HostPorts);

public sealed record ContainerActionRequest(string? Action);

public sealed record RollbackRequest(string? Tag);

public sealed record NotificationsRequest(
    NotificationEventsRequest? Events,
    NotificationWebhookRequest? Webhook,
    NotificationSlackRequest? Slack,
    NotificationTelegramRequest? Telegram);

public sealed record NotificationEventsRequest(
    bool? DeploySucceeded,
    bool? DeployFailed,
    bool? HealthCheckFailed,
    bool? RolledBack);

public sealed record NotificationWebhookRequest(bool? Enabled, string? Url);

public sealed record NotificationSlackRequest(bool? Enabled, string? WebhookUrl);

public sealed record NotificationTelegramRequest(bool? Enabled, string? BotToken, string? ChatId);

public sealed record NotificationTestRequest(string? Channel);

public sealed record ComposeEnvRequest(Dictionary<string, string>? Set, string[]? Remove);

public sealed record ComposeCreateRequest(int? HostPort, int? ContainerPort);
