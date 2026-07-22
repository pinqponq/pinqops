using System.Text.Json;

namespace PinqOps.Web;

/// <summary>
/// Loads and saves the <see cref="UiConfig"/> JSON file. The file holds a PAT,
/// so it is created with owner-only permissions.
/// </summary>
public sealed class UiConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private UiConfig _current;

    public UiConfigStore(string? path = null)
    {
        _path = path
            ?? Environment.GetEnvironmentVariable("PINQOPS_UI_CONFIG")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "pinqops", "ui.json");
        _current = Load();
    }

    public string Path_ => _path;

    public UiConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Update(Action<UiConfig> mutate)
    {
        lock (_gate)
        {
            mutate(_current);
            Save(_current);
        }
    }

    private UiConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return Migrate(JsonSerializer.Deserialize<UiConfig>(File.ReadAllText(_path)) ?? new UiConfig());
            }
        }
        catch (JsonException)
        {
            // A corrupt config file should not brick the UI; start fresh.
        }

        return new UiConfig();
    }

    /// <summary>
    /// Wraps a legacy single-app config into <see cref="UiConfig.Apps"/>. The
    /// migrated app keeps its paths; account fields (password hash, PAT) are
    /// untouched. In-memory only — the file is rewritten in the new shape on
    /// the next <see cref="Update"/>. Idempotent.
    /// </summary>
    public static UiConfig Migrate(UiConfig config)
    {
        if (config.Apps.Count == 0 && !string.IsNullOrWhiteSpace(config.RepoUrl))
        {
            string id;
            try
            {
                id = AppConnection.SlugFor(GitHubRepositoryParser.Parse(config.RepoUrl));
            }
            catch (ArgumentException)
            {
                // A hand-edited garbage URL still must not lose the connection.
                id = ComposeProjectName.Fallback;
            }

            config.Apps.Add(new AppConnection
            {
                Id = id,
                RepoUrl = config.RepoUrl,
                ComposeFile = string.IsNullOrWhiteSpace(config.ComposeFile)
                    ? UiConfig.DefaultComposeFile
                    : config.ComposeFile,
                RunnerDirectory = string.IsNullOrWhiteSpace(config.RunnerDirectory)
                    ? UiConfig.DefaultRunnerDirectory
                    : config.RunnerDirectory,
            });
        }

        config.RepoUrl = null;
        config.ComposeFile = null;
        config.RunnerDirectory = null;
        return config;
    }

    private void Save(UiConfig config)
    {
        // Atomic + owner-only: the file holds a broad-scope PAT, and a torn
        // write would wipe the password hash and drop the dashboard back to the
        // unauthenticated setup flow.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
