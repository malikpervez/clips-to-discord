using System.Text;
using System.Text.Json;

namespace ClipsToDiscord;

internal sealed record FavoriteClipIdentity(
    string Path,
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed record FavoritesState(IReadOnlyList<FavoriteClipIdentity> Entries)
{
    internal static FavoritesState Empty { get; } = new([]);
}

internal interface IFavoritesStore
{
    FavoritesState Load();
    void Save(FavoritesState state);
}

internal interface IFavoritesService
{
    event Action? Changed;

    bool IsFavorite(GalleryClipEntry clip);
    int CountFavorites(IEnumerable<GalleryClipEntry> clips);
    bool SetFavorite(GalleryClipEntry clip, bool favorite);
    bool MigrateFavorite(string originalPath, string archivedPath, bool originalKept);
}

internal sealed class FavoritesStore : IFavoritesStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    internal FavoritesStore(string? path = null)
    {
        _path = path ?? Path.Combine(SettingsStore.DataDirectory, "favorites.json");
    }

    public FavoritesState Load()
    {
        try
        {
            if (!File.Exists(_path)) return FavoritesState.Empty;
            var stored = JsonSerializer.Deserialize<StoredFavorites>(File.ReadAllText(_path), JsonOptions);
            if (stored is null || stored.Version != CurrentVersion) return FavoritesState.Empty;

            var entries = new Dictionary<string, FavoriteClipIdentity>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in stored.Entries ?? [])
            {
                if (!TryNormalize(entry.Path, entry.Length, entry.LastWriteTimeUtc, out var normalized)) continue;
                entries[normalized.Path] = normalized;
            }
            return new FavoritesState(entries.Values.ToArray());
        }
        catch (Exception exception)
        {
            Log.Error("Could not load Gallery favorites.", exception);
            return FavoritesState.Empty;
        }
    }

    public void Save(FavoritesState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var stored = new StoredFavorites
        {
            Version = CurrentVersion,
            Entries = state.Entries
                .Select(entry => TryNormalize(
                    entry.Path,
                    entry.Length,
                    entry.LastWriteTimeUtc,
                    out var normalized)
                        ? normalized
                        : null)
                .Where(entry => entry is not null)
                .Cast<FavoriteClipIdentity>()
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        var serialized = JsonSerializer.Serialize(stored, JsonOptions);

        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Favorites path has no directory.");
        }
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(serialized);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool TryNormalize(
        string? path,
        long length,
        DateTime lastWriteTimeUtc,
        out FavoriteClipIdentity normalized)
    {
        normalized = null!;
        if (string.IsNullOrWhiteSpace(path) || length < 0) return false;
        try
        {
            normalized = new FavoriteClipIdentity(
                Path.GetFullPath(path),
                length,
                lastWriteTimeUtc.Kind == DateTimeKind.Utc
                    ? lastWriteTimeUtc
                    : lastWriteTimeUtc.ToUniversalTime());
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class StoredFavorites
    {
        public int Version { get; set; } = CurrentVersion;
        public FavoriteClipIdentity[]? Entries { get; set; } = [];
    }
}

internal sealed class FavoritesService : IFavoritesService
{
    private readonly IFavoritesStore _store;
    private readonly object _gate = new();
    private Dictionary<string, FavoriteClipIdentity> _entries;

    internal FavoritesService(IFavoritesStore? store = null)
    {
        _store = store ?? new FavoritesStore();
        _entries = _store.Load().Entries
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => NormalizePath(group.Key), group => group.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public event Action? Changed;

    public bool IsFavorite(GalleryClipEntry clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        lock (_gate)
        {
            return _entries.TryGetValue(NormalizePath(clip.Path), out var favorite) &&
                favorite.Length == clip.Length &&
                favorite.LastWriteTimeUtc == clip.LastWriteTimeUtc;
        }
    }

    public int CountFavorites(IEnumerable<GalleryClipEntry> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        lock (_gate)
        {
            return clips.Count(IsFavoriteLocked);
        }
    }

    public bool SetFavorite(GalleryClipEntry clip, bool favorite)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var path = NormalizePath(clip.Path);
        var changed = false;
        lock (_gate)
        {
            var replacement = new Dictionary<string, FavoriteClipIdentity>(
                _entries,
                StringComparer.OrdinalIgnoreCase);
            if (favorite)
            {
                var identity = new FavoriteClipIdentity(path, clip.Length, clip.LastWriteTimeUtc);
                if (replacement.TryGetValue(path, out var existing) && existing == identity) return true;
                replacement[path] = identity;
            }
            else
            {
                if (!replacement.Remove(path)) return true;
            }

            if (!TrySave(replacement)) return false;
            _entries = replacement;
            changed = true;
        }
        if (changed) Changed?.Invoke();
        return true;
    }

    public bool MigrateFavorite(string originalPath, string archivedPath, bool originalKept)
    {
        if (originalKept) return true;
        var original = NormalizePath(originalPath);
        var archived = NormalizePath(archivedPath);
        var changed = false;
        lock (_gate)
        {
            if (!_entries.ContainsKey(original)) return true;
            var destination = new FileInfo(archived);
            destination.Refresh();
            if (!destination.Exists) return false;

            var replacement = new Dictionary<string, FavoriteClipIdentity>(
                _entries,
                StringComparer.OrdinalIgnoreCase);
            replacement.Remove(original);
            replacement[archived] = new FavoriteClipIdentity(
                destination.FullName,
                Math.Max(0, destination.Length),
                destination.LastWriteTimeUtc);
            if (!TrySave(replacement)) return false;
            _entries = replacement;
            changed = true;
        }
        if (changed) Changed?.Invoke();
        return true;
    }

    private bool IsFavoriteLocked(GalleryClipEntry clip) =>
        _entries.TryGetValue(NormalizePath(clip.Path), out var favorite) &&
        favorite.Length == clip.Length &&
        favorite.LastWriteTimeUtc == clip.LastWriteTimeUtc;

    private bool TrySave(Dictionary<string, FavoriteClipIdentity> entries)
    {
        try
        {
            _store.Save(new FavoritesState(entries.Values.ToArray()));
            return true;
        }
        catch (Exception exception)
        {
            Log.Error("Could not save Gallery favorites.", exception);
            return false;
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);
}
