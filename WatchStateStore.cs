using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal sealed class WatchStateStore
{
    private const int CurrentVersion = 4;
    private const int MinimumCompatibleVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _statePath;
    private readonly string _safeBaselineMarkerPath;

    public WatchStateStore()
        : this(
            Path.Combine(SettingsStore.DataDirectory, "state.json"),
            SettingsStore.SafeBaselineMarkerPath)
    {
    }

    internal WatchStateStore(string statePath, string safeBaselineMarkerPath)
    {
        _statePath = statePath;
        _safeBaselineMarkerPath = safeBaselineMarkerPath;
    }

    public async Task<WatchState> LoadOrInitializeAsync(
        string clipsFolder,
        Action<string> reportStatus,
        CancellationToken cancellationToken)
    {
        var forceSafeBaseline = File.Exists(_safeBaselineMarkerPath);
        WatchState? saved = null;
        try
        {
            if (File.Exists(_statePath))
            {
                saved = JsonSerializer.Deserialize<WatchState>(File.ReadAllText(_statePath), JsonOptions);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not read uploader state; creating a safe baseline.", exception);
        }

        if (!forceSafeBaseline && saved is not null && saved.Version >= MinimumCompatibleVersion)
        {
            var needsLocalOnlyBaseline = saved.Version < 3;
            var needsVersionUpgrade = saved.Version < CurrentVersion;
            Normalize(saved);
            if (saved.ClipsFolder.Equals(clipsFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (needsLocalOnlyBaseline)
                {
                    await AddLocalOnlyBaselineAsync(saved, clipsFolder, cancellationToken);
                }
                if (needsLocalOnlyBaseline || needsVersionUpgrade) Save(saved);
                return saved;
            }

            reportStatus("Clips folder changed — building a safe baseline");
            saved.ClipsFolder = clipsFolder;
            await AddSafeBaselineAsync(saved, clipsFolder, cancellationToken);
            Save(saved);
            return saved;
        }

        reportStatus(forceSafeBaseline
            ? "Recovering migration — building a safe baseline"
            : "Building content-hash baseline");

        var state = new WatchState
        {
            Version = CurrentVersion,
            ClipsFolder = clipsFolder,
            PendingMoves = saved?.PendingMoves ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingLocalOnlyMoves = saved?.PendingLocalOnlyMoves ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingEditedUploads = saved?.PendingEditedUploads ?? []
        };
        Normalize(state);
        await AddSafeBaselineAsync(state, clipsFolder, cancellationToken);
        Save(state);
        TryDeleteSafeBaselineMarker();
        Log.Info($"Initialized content-hash state with {state.KnownContentHashes.Count} existing clip(s); they will not be uploaded.");
        return state;
    }

    public void Save(WatchState state)
    {
        var stateDirectory = Path.GetDirectoryName(_statePath)
            ?? throw new InvalidOperationException("The state directory could not be determined.");
        Directory.CreateDirectory(stateDirectory);
        var temporaryPath = _statePath + ".tmp";
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOptions));
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, _statePath, true);
    }

    public static string FileKey(FileInfo file) =>
        $"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    public static IEnumerable<string> EnumerateClips(string clipsFolder) =>
        Directory.EnumerateFiles(clipsFolder, "*.mp4", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetLastWriteTimeUtc);

    private async Task AddSafeBaselineAsync(
        WatchState state,
        string clipsFolder,
        CancellationToken cancellationToken)
    {
        var topLevelPaths = EnumerateClips(clipsFolder).ToList();
        var uploadedFolder = UploadedFolder.GetOrCreate(clipsFolder);
        var uploadedPaths = UploadedFolder.EnumerateArchivedClips(uploadedFolder).ToList();

        foreach (var path in topLevelPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                state.IgnoredFileKeys.Add(FileKey(file));
                state.KnownContentHashes.Add(
                    await ContentIdentity.ComputeSha256Async(path, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not hash baseline clip {Path.GetFileName(path)}; it remains protected by its file key.", exception);
            }
        }

        foreach (var path in uploadedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contentHash = await ContentIdentity.ComputeSha256Async(path, cancellationToken);
                state.KnownContentHashes.Add(contentHash);
                state.UploadedContentHashes.Add(contentHash);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not hash archived baseline clip {Path.GetFileName(path)}.", exception);
            }
        }

        await AddLocalOnlyBaselineAsync(state, clipsFolder, cancellationToken);
    }

    private static async Task AddLocalOnlyBaselineAsync(
        WatchState state,
        string clipsFolder,
        CancellationToken cancellationToken)
    {
        var localOnlyFolder = UploadedFolder.FindExistingLocalOnly(clipsFolder);
        if (localOnlyFolder is null) return;

        foreach (var path in UploadedFolder.EnumerateArchivedClips(localOnlyFolder)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contentHash = await ContentIdentity.ComputeSha256Async(path, cancellationToken);
                state.KnownContentHashes.Add(contentHash);
                state.LocalOnlyContentHashes.Add(contentHash);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not hash local-only baseline clip {Path.GetFileName(path)}.", exception);
            }
        }
    }

    private static void Normalize(WatchState state)
    {
        state.Version = CurrentVersion;
        state.ClipsFolder ??= string.Empty;
        state.KnownContentHashes = new HashSet<string>(
            state.KnownContentHashes ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.IgnoredFileKeys = new HashSet<string>(
            state.IgnoredFileKeys ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.UploadedContentHashes = new HashSet<string>(
            state.UploadedContentHashes ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.LocalOnlyContentHashes = new HashSet<string>(
            state.LocalOnlyContentHashes ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.PendingMoves = new HashSet<string>(
            state.PendingMoves ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.PendingLocalOnlyMoves = new HashSet<string>(
            state.PendingLocalOnlyMoves ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.PendingEditedUploads = (state.PendingEditedUploads ?? [])
            .Where(pending => pending is not null && pending.Id != Guid.Empty)
            .GroupBy(pending => pending.Id)
            .Select(group => group.First())
            .ToList();
        state.KnownSignatures = null;
    }

    private void TryDeleteSafeBaselineMarker()
    {
        try { File.Delete(_safeBaselineMarkerPath); } catch { }
    }
}

internal sealed class WatchState
{
    public int Version { get; set; }
    public string ClipsFolder { get; set; } = string.Empty;
    public HashSet<string> KnownContentHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UploadedContentHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // This subset records why known content must remain local. KnownContentHashes is the
    // enforcement guard; this separate set preserves the routing history for migration,
    // recovery, diagnostics, and future activity UI without ever implying an upload.
    public HashSet<string> LocalOnlyContentHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> IgnoredFileKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PendingMoves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PendingLocalOnlyMoves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PendingEditedClipDisposition> PendingEditedUploads { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? KnownSignatures { get; set; }
}

internal sealed record PendingEditedClipDisposition
{
    public Guid Id { get; init; }
    public string ClipsFolder { get; init; } = string.Empty;
    public string EditedPath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public string OriginalLocalOnlyPath { get; init; } = string.Empty;
    public string EditedContentHash { get; init; } = string.Empty;
    public string OriginalContentHash { get; init; } = string.Empty;
    public bool KeepOriginal { get; init; }
    public long OutputBytes { get; init; }
}
