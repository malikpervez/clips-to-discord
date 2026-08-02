using System.Text.Json;

namespace MomentsToDiscord;

internal sealed class WatchStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _statePath = Path.Combine(SettingsStore.DataDirectory, "state.json");

    public WatchState LoadOrInitialize(string clipsFolder)
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var saved = JsonSerializer.Deserialize<WatchState>(File.ReadAllText(_statePath), JsonOptions);
                if (saved is not null &&
                    saved.ClipsFolder.Equals(clipsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return saved;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not read uploader state; creating a safe baseline.", exception);
        }

        var state = new WatchState { ClipsFolder = clipsFolder };
        foreach (var path in EnumerateClips(clipsFolder))
        {
            try { state.KnownSignatures.Add(Signature(new FileInfo(path))); } catch { }
        }
        Save(state);
        Log.Info($"Initialized with {state.KnownSignatures.Count} existing clip(s); they will not be uploaded.");
        return state;
    }

    public void Save(WatchState state)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var temporaryPath = _statePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, _statePath, true);
    }

    public static string Signature(FileInfo file) =>
        $"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    public static IEnumerable<string> EnumerateClips(string clipsFolder) =>
        Directory.EnumerateFiles(clipsFolder, "*.mp4", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetLastWriteTimeUtc);
}

internal sealed class WatchState
{
    public string ClipsFolder { get; set; } = string.Empty;
    public HashSet<string> KnownSignatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PendingMoves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
