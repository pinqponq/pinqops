namespace PinqOps.Web;

public sealed record PasswordRequest(string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record SettingsRequest(
    string? RepoUrl,
    string? Username,
    string? Pat,
    string? ComposeFile,
    string? RunnerDirectory);

public sealed record ContainerActionRequest(string? Action);

public sealed record RegistryLoginRequest(string? Registry, string? Username, string? Token);
