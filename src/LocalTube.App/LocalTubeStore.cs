using System.Text.Json;

namespace LocalTube;

public sealed class LocalTubeStore
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly string _dataDir;
    private readonly string _settingsPath;
    private readonly string _statePath;

    public string DataDirectory => _dataDir;
    public AppSettings Settings { get; private set; }
    public LibraryState State { get; private set; }

    public LocalTubeStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dataDir = Path.Combine(localAppData, "YouTube Offline");
        Directory.CreateDirectory(_dataDir);
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        _statePath = Path.Combine(_dataDir, "library-state.json");

        // Preserve settings/playlists from the previous LocalTube builds when upgrading.
        var legacyDir = Path.Combine(localAppData, "LocalTube");
        TryMigrate(Path.Combine(legacyDir, "settings.json"), _settingsPath);
        TryMigrate(Path.Combine(legacyDir, "library-state.json"), _statePath);

        Settings = Load<AppSettings>(_settingsPath) ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(Settings.LibraryFolder))
        {
            Settings.LibraryFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "YouTube Offline");
            SaveSettings();
        }

        Directory.CreateDirectory(Settings.LibraryFolder);
        State = Load<LibraryState>(_statePath) ?? new LibraryState();
    }

    private static void TryMigrate(string oldPath, string newPath)
    {
        try
        {
            if (!File.Exists(newPath) && File.Exists(oldPath))
                File.Copy(oldPath, newPath, false);
        }
        catch { }
    }

    private T? Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), _json);
        }
        catch
        {
            return default;
        }
    }

    public void SaveSettings()
    {
        lock (_gate)
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, _json));
    }

    public void SaveState()
    {
        lock (_gate)
            File.WriteAllText(_statePath, JsonSerializer.Serialize(State, _json));
    }

    public void SetLibraryFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Pasta inválida.");
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        Directory.CreateDirectory(path);
        Settings.LibraryFolder = path;
        SaveSettings();
    }

    public UserVideoState GetOrCreateVideoState(string id)
    {
        lock (_gate)
        {
            if (!State.VideoStates.TryGetValue(id, out var state))
            {
                state = new UserVideoState();
                State.VideoStates[id] = state;
            }
            return state;
        }
    }
}
