namespace PinqOps.Web;

public sealed record PasswordRequest(string? Password, string? Username = null);

public sealed record SetupRequest(string? Password, string? SetupCode);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record UserRequest(string? Username, string? Password, string? Role);

public sealed record UserPasswordRequest(string? Password);

public sealed record SettingsRequest(
    string? RepoUrl,
    string? Username,
    string? Pat,
    string? ComposeFile,
    string? RunnerDirectory,
    string? GithubClientId,
    string? AppId);

public sealed record AppRemoveRequest(string? Id);

public sealed record CreateDockerfileRequest(string? Content, string? Dir);

public sealed record ProxyInstallRequest(string? AcmeEmail, bool? Staging, bool? Force);

public sealed record DomainRequest(string? Domain, string? Target, int? TargetPort);

public sealed record BackupTargetRequest(
    string? Id, string? Kind, string? Name, string? Engine, string? Schedule, int? AtHour, int? RetentionCount, bool? Enabled);

public sealed record BackupRestoreRequest(string? TargetId, string? Snapshot);

public sealed record TokenCreateRequest(string? Name, string? Scope);

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
