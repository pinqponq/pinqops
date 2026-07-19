using System.Text;

namespace PinqOps.Web;

/// <summary>
/// Installs pinqops-ui as a systemd service so the dashboard keeps running
/// after the SSH session ends and comes back after a reboot.
/// </summary>
public sealed class ServiceInstaller
{
    private const string ServiceName = "pinqops-ui";
    private const string UnitPath = $"/etc/systemd/system/{ServiceName}.service";

    private readonly IProcessRunner _processRunner;
    private readonly Action<string> _log;

    public ServiceInstaller(IProcessRunner processRunner, Action<string> log)
    {
        _processRunner = processRunner;
        _log = log;
    }

    public async Task<int> InstallAsync(string port, string host, string? certPath, string? certPassword, string user)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)
            || Path.GetFileName(executable) is "dotnet" or "dotnet.exe")
        {
            _log("error: run 'install-service' from the published pinqops-ui binary, not via 'dotnet'.");
            return 1;
        }

        // These values are interpolated into the systemd unit's ExecStart line;
        // reject anything that could inject an extra directive (newline) or break
        // out of a quoted argument (double quote).
        if (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
        {
            _log("error: --port must be an integer between 1 and 65535.");
            return 1;
        }

        foreach (var (option, value) in new[]
                 {
                     ("--host", host),
                     ("--cert", certPath ?? string.Empty),
                     ("--cert-password", certPassword ?? string.Empty),
                     ("--user", user),
                 })
        {
            if (value.AsSpan().IndexOfAny('\r', '\n', '"') >= 0)
            {
                _log($"error: {option} contains an invalid character (newline or double quote).");
                return 1;
            }
        }

        var execStart = new StringBuilder($"\"{executable}\" --port {port} --host {host}");
        if (!string.IsNullOrWhiteSpace(certPath))
        {
            execStart.Append($" --cert \"{certPath}\"");
        }

        if (!string.IsNullOrWhiteSpace(certPassword))
        {
            execStart.Append($" --cert-password \"{certPassword}\"");
        }

        var unit = $"""
            [Unit]
            Description=pinqops web dashboard
            Wants=network-online.target
            After=network-online.target docker.service

            [Service]
            Type=simple
            User={user}
            ExecStart={execStart}
            Restart=on-failure
            RestartSec=3
            NoNewPrivileges=true

            [Install]
            WantedBy=multi-user.target

            """;

        try
        {
            File.WriteAllText(UnitPath, unit);
            // The unit embeds the cert password when one is used — keep it root-only then.
            File.SetUnixFileMode(UnitPath, string.IsNullOrWhiteSpace(certPassword)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (UnauthorizedAccessException)
        {
            _log($"error: cannot write {UnitPath} — run with sudo.");
            return 1;
        }

        if (!await SystemctlAsync("daemon-reload").ConfigureAwait(false)
            || !await SystemctlAsync("enable", "--now", ServiceName).ConfigureAwait(false))
        {
            return 1;
        }

        _log($"{ServiceName} installed as a systemd service (user '{user}') and started on port {port}.");
        _log("it now survives SSH logout and starts again after a reboot.");
        _log($"logs & the first-run setup code:  journalctl -u {ServiceName} -n 20");
        return 0;
    }

    public async Task<int> UninstallAsync()
    {
        // Best effort: the service may already be stopped or half-removed.
        await SystemctlAsync("disable", "--now", ServiceName).ConfigureAwait(false);

        if (File.Exists(UnitPath))
        {
            try
            {
                File.Delete(UnitPath);
            }
            catch (UnauthorizedAccessException)
            {
                _log($"error: cannot delete {UnitPath} — run with sudo.");
                return 1;
            }
        }

        await SystemctlAsync("daemon-reload").ConfigureAwait(false);
        _log($"{ServiceName} service removed.");
        return 0;
    }

    private async Task<bool> SystemctlAsync(params string[] arguments)
    {
        try
        {
            var result = await _processRunner.RunAsync("systemctl", arguments).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _log($"error: 'systemctl {string.Join(' ', arguments)}' failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            _log($"error: could not run systemctl ({exception.Message}). Is this a systemd machine?");
            return false;
        }
    }
}
