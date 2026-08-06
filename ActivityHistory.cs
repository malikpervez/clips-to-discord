using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal enum ClipActivityState
{
    Discovered,
    Waiting,
    Hashing,
    Queued,
    Uploading,
    Compressing,
    Retrying,
    Completed,
    Failed,
    Archived
}

internal enum ClipActivityRoute
{
    Uploaded,
    LocalOnly,
    Duplicate,
    Baseline
}

internal sealed record ClipActivityEntry
{
    public Guid Id { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string GameName { get; init; } = "Uncategorized";
    public string SourcePath { get; init; } = string.Empty;
    public string? CurrentPath { get; init; }
    public ClipActivityState State { get; init; }
    public ClipActivityRoute? Route { get; init; }
    public int AttemptCount { get; init; }
    public string? Detail { get; init; }
    public string? Error { get; init; }
    public long OriginalBytes { get; init; }
    public long? CompressedBytes { get; init; }
    public int? CompressionTargetMb { get; init; }
    public int? VideoKbps { get; init; }
    public int? AudioKbps { get; init; }
}

internal sealed record ClipActivityUpdate(
    string SourcePath,
    ClipActivityState State,
    long? OriginalBytes = null,
    string? CurrentPath = null,
    ClipActivityRoute? Route = null,
    bool IncrementAttempt = false,
    string? Detail = null,
    string? Error = null,
    long? CompressedBytes = null,
    int? CompressionTargetMb = null,
    int? VideoKbps = null,
    int? AudioKbps = null);

internal sealed record ClipActivitySnapshot(IReadOnlyList<ClipActivityEntry> Entries);

internal sealed class ActivityHistoryStore : IDisposable
{
    internal const int MaximumEntries = 100;
    private const int MaximumDetailLength = 180;
    private const int MaximumErrorLength = 240;
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<ClipActivityEntry> _entries;
    private readonly Dictionary<int, ActivitySubscription> _subscriptions = [];
    private int _nextSubscriptionId;
    private bool _disposed;

    internal ActivityHistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(SettingsStore.DataDirectory, "activity.json");
        _entries = Load(_path);
    }

    internal ClipActivitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshotLocked();
        }
    }

    internal ClipActivityEntry Transition(ClipActivityUpdate update)
    {
        try
        {
            return TransitionCore(update);
        }
        catch (Exception exception)
        {
            Log.Error("Could not update recent clip activity; clip processing will continue.", exception);
            var sourcePath = SensitiveDataRedactor.Redact(update.SourcePath ?? string.Empty);
            var fileName = Path.GetFileName(sourcePath);
            return new ClipActivityEntry
            {
                Id = Guid.NewGuid(),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                FileName = string.IsNullOrWhiteSpace(fileName) ? "Unknown clip.mp4" : fileName,
                SourcePath = sourcePath,
                CurrentPath = sourcePath,
                State = update.State,
                OriginalBytes = Math.Max(0, update.OriginalBytes ?? 0)
            };
        }
    }

    private ClipActivityEntry TransitionCore(ClipActivityUpdate update)
    {
        ClipActivitySnapshot snapshot;
        ActivitySubscription[] subscriptions;
        ClipActivityEntry result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sourcePath = SanitizePath(update.SourcePath);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("An activity entry requires a source path.", nameof(update));
            }

            var current = FindCurrentEntryLocked(sourcePath, update.State);
            var now = DateTime.UtcNow;
            var fileName = SanitizeText(Path.GetFileName(sourcePath), 260) ?? "Unknown clip.mp4";
            var next = (current ?? new ClipActivityEntry
            {
                Id = Guid.NewGuid(),
                CreatedUtc = now,
                FileName = fileName,
                GameName = UploadedFolder.GetGameFolderName(fileName),
                SourcePath = sourcePath,
                OriginalBytes = Math.Max(0, update.OriginalBytes ?? 0)
            }) with
            {
                UpdatedUtc = now,
                State = update.State,
                Route = update.Route ?? current?.Route,
                AttemptCount = Math.Max(0, (current?.AttemptCount ?? 0) + (update.IncrementAttempt ? 1 : 0)),
                Detail = update.Detail is null ? current?.Detail : SanitizeText(update.Detail, MaximumDetailLength),
                Error = update.Error is null ? null : SanitizeError(update.Error, sourcePath),
                OriginalBytes = Math.Max(0, update.OriginalBytes ?? current?.OriginalBytes ?? 0),
                CurrentPath = update.CurrentPath is null
                    ? current?.CurrentPath ?? sourcePath
                    : SanitizePath(update.CurrentPath),
                CompressedBytes = update.CompressedBytes ?? current?.CompressedBytes,
                CompressionTargetMb = update.CompressionTargetMb ?? current?.CompressionTargetMb,
                VideoKbps = update.VideoKbps ?? current?.VideoKbps,
                AudioKbps = update.AudioKbps ?? current?.AudioKbps
            };

            var changed = current is null || !EquivalentExceptTimestamp(current, next);
            if (!changed) return current!;

            if (current is not null) _entries.Remove(current);
            _entries.Insert(0, next);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            }

            SaveLocked();
            snapshot = CreateSnapshotLocked();
            subscriptions = _subscriptions.Values.ToArray();
            result = next;
        }

        foreach (var subscription in subscriptions) subscription.Post(snapshot);
        return result;
    }

    internal IDisposable Subscribe(SynchronizationContext context, Action<ClipActivitySnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observer);
        ActivitySubscription subscription;
        ClipActivitySnapshot snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_nextSubscriptionId;
            subscription = new ActivitySubscription(this, id, context, observer);
            _subscriptions.Add(id, subscription);
            snapshot = CreateSnapshotLocked();
        }
        subscription.Post(snapshot);
        return subscription;
    }

    internal int SubscriptionCount
    {
        get
        {
            lock (_gate) return _subscriptions.Count;
        }
    }

    private ClipActivityEntry? FindCurrentEntryLocked(string sourcePath, ClipActivityState state)
    {
        var active = _entries.FirstOrDefault(entry =>
            entry.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) && !IsTerminal(entry.State));
        if (active is not null) return active;
        if (state == ClipActivityState.Discovered) return null;
        return _entries.FirstOrDefault(entry =>
            entry.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTerminal(ClipActivityState state) =>
        state is ClipActivityState.Failed or ClipActivityState.Archived;

    private static bool EquivalentExceptTimestamp(ClipActivityEntry left, ClipActivityEntry right) =>
        left with { UpdatedUtc = default } == right with { UpdatedUtc = default };

    private ClipActivitySnapshot CreateSnapshotLocked() => new(_entries.ToArray());

    private void SaveLocked()
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The activity-history directory could not be determined.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            var document = new ActivityHistoryDocument(CurrentVersion, _entries);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            Log.Error("Could not persist recent clip activity; in-memory activity will continue.", exception);
        }
    }

    private static List<ClipActivityEntry> Load(string path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<ActivityHistoryDocument>(stream, JsonOptions);
            if (document is null || document.Version != CurrentVersion || document.Entries is null) return [];
            return document.Entries
                .Where(entry => entry.Id != Guid.Empty && !string.IsNullOrWhiteSpace(entry.SourcePath))
                .OrderByDescending(entry => entry.UpdatedUtc)
                .Take(MaximumEntries)
                .Select(NormalizeLoadedEntry)
                .ToList();
        }
        catch (Exception exception)
        {
            Log.Error("Could not read recent clip activity; starting with an empty activity history.", exception);
            return [];
        }
    }

    private static ClipActivityEntry NormalizeLoadedEntry(ClipActivityEntry entry)
    {
        var sourcePath = SanitizePath(entry.SourcePath);
        var fileName = SanitizeText(entry.FileName, 260) ?? Path.GetFileName(sourcePath);
        return entry with
        {
            FileName = fileName,
            GameName = SanitizeText(entry.GameName, 80) ?? UploadedFolder.GetGameFolderName(fileName),
            SourcePath = sourcePath,
            CurrentPath = entry.CurrentPath is null ? null : SanitizePath(entry.CurrentPath),
            Detail = SanitizeText(entry.Detail, MaximumDetailLength),
            Error = entry.Error is null ? null : SanitizeError(entry.Error, sourcePath),
            AttemptCount = Math.Max(0, entry.AttemptCount),
            OriginalBytes = Math.Max(0, entry.OriginalBytes),
            CompressedBytes = entry.CompressedBytes is null ? null : Math.Max(0, entry.CompressedBytes.Value)
        };
    }

    private static string SanitizePath(string value) =>
        SensitiveDataRedactor.Redact(value).Trim();

    private static string? SanitizeText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = SensitiveDataRedactor.Redact(value)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (sanitized.Length <= maximumLength) return sanitized;
        var safeLength = maximumLength;
        if (safeLength > 0 && char.IsHighSurrogate(sanitized[safeLength - 1])) safeLength--;
        return sanitized[..safeLength];
    }

    private static string? SanitizeError(string value, string sourcePath)
    {
        var concise = value;
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            concise = concise.Replace(
                sourceDirectory,
                "[clips folder]",
                StringComparison.OrdinalIgnoreCase);
        }
        concise = concise.Replace(sourcePath, Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase);
        return SanitizeText(concise, MaximumErrorLength);
    }

    private void Unsubscribe(int id)
    {
        lock (_gate) _subscriptions.Remove(id);
    }

    public void Dispose()
    {
        ActivitySubscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions) subscription.Detach();
    }

    private sealed record ActivityHistoryDocument(int Version, IReadOnlyList<ClipActivityEntry> Entries);

    private sealed class ActivitySubscription(
        ActivityHistoryStore owner,
        int id,
        SynchronizationContext context,
        Action<ClipActivitySnapshot> observer) : IDisposable
    {
        private int _disposed;

        internal void Post(ClipActivitySnapshot snapshot)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                context.Post(_ =>
                {
                    if (Volatile.Read(ref _disposed) == 0) observer(snapshot);
                }, null);
            }
            catch (InvalidAsynchronousStateException)
            {
                Dispose();
            }
        }

        internal void Detach() => Interlocked.Exchange(ref _disposed, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(id);
        }
    }
}
