using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Stores the generated credentials of installed catalog apps
/// (<c>~/.config/pinqops/app-credentials.json</c>, 0600). Credentials are kept
/// retrievable — app volumes survive an uninstall, so a reinstall must reuse
/// the same password or the container env and the persisted data would
/// disagree.
/// </summary>
public sealed class AppCredentialStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public AppCredentialStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "pinqops", "app-credentials.json");
    }

    public string Path_ => _path;

    public sealed class AppCredentials
    {
        public Dictionary<string, string> Env { get; set; } = new();
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Stored credential env for an app, or null when none recorded.</summary>
    public IReadOnlyDictionary<string, string>? Get(string appId)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(appId.ToLowerInvariant())?.Env;
        }
    }

    /// <summary>
    /// Returns the app's stored password, generating and persisting one on
    /// first use. This is what makes reinstalls (and cross-app references like
    /// WordPress → MySQL) line up with data in existing volumes.
    /// </summary>
    public string GetOrCreatePassword(string appId)
    {
        lock (_gate)
        {
            var all = Load();
            var key = appId.ToLowerInvariant();
            if (all.TryGetValue(key, out var existing)
                && existing.Env.TryGetValue("password", out var stored)
                && stored.Length > 0)
            {
                return stored;
            }

            var password = PasswordGenerator.Generate();
            var credentials = all.TryGetValue(key, out var current) ? current : new AppCredentials();
            credentials.Env["password"] = password;
            all[key] = credentials;
            Save(all);
            return password;
        }
    }

    /// <summary>Records the resolved credential env values shown to the user.</summary>
    public void SetEnv(string appId, IReadOnlyDictionary<string, string> env)
    {
        lock (_gate)
        {
            var all = Load();
            var key = appId.ToLowerInvariant();
            var credentials = all.TryGetValue(key, out var current) ? current : new AppCredentials();
            foreach (var (name, value) in env)
            {
                credentials.Env[name] = value;
            }

            all[key] = credentials;
            Save(all);
        }
    }

    private Dictionary<string, AppCredentials> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, AppCredentials>>(
                    File.ReadAllText(_path), SerializerOptions) ?? new();
            }
        }
        catch (JsonException)
        {
            // A corrupt file must not block installs; credentials restart empty.
        }

        return new Dictionary<string, AppCredentials>();
    }

    private void Save(Dictionary<string, AppCredentials> all)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(all, SerializerOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
