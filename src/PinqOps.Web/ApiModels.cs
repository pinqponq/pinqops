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
