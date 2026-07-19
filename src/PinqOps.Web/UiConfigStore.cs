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
                return JsonSerializer.Deserialize<UiConfig>(File.ReadAllText(_path)) ?? new UiConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt config file should not brick the UI; start fresh.
        }

        return new UiConfig();
    }

    private void Save(UiConfig config)
    {
        // Atomic + owner-only: the file holds a broad-scope PAT, and a torn
        // write would wipe the password hash and drop the dashboard back to the
        // unauthenticated setup flow.
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
