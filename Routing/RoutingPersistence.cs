using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal sealed class RoutingSnapshotStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 1024 * 1024;
    internal const string FileName = "routes.json";

    private readonly RoutingAtomicJsonStore<RoutingSnapshotDocument> _store;

    internal RoutingSnapshotStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingSnapshotStore(string path)
    {
        _store = new RoutingAtomicJsonStore<RoutingSnapshotDocument>(
            path,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingSnapshotModel.Validate,
            RoutingSnapshotModel.ValidateSuccessor,
            ValidateInitial);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<RoutingSnapshotDocument> Load(
        CancellationToken cancellationToken = default) =>
        _store.Load(cancellationToken);

    internal Task<RoutingSnapshotDocument> SaveAsync(
        RoutingSnapshotDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null) =>
        _store.SaveAsync(document, expectedGeneration, cancellationToken, beforeCommit);

    internal Task<RoutingSnapshotDocument> LoadOrCreateAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        _store.LoadOrCreateAsync(
            () => RoutingSnapshotModel.CreateEmpty(now),
            cancellationToken);

    private static void ValidateInitial(RoutingSnapshotDocument document)
    {
        RoutingValidation.Require(document.Generation == 1 &&
                                  document.CreatedUtc == document.UpdatedUtc &&
                                  document.Routes.All(route => route.Revision == 1),
            "The first routing snapshot is not canonical.");
    }
}

internal sealed class RoutingOutboxStore
{
    // Version 2 adds immutable RoutingPlanDecision headers for every evaluated source.
    internal const int CurrentSchemaVersion = 2;
    internal const int MaximumDocumentBytes = 8 * 1024 * 1024;
    internal const string FileName = "outbox.json";

    private readonly RoutingAtomicJsonStore<RoutingOutboxDocument> _store;

    internal RoutingOutboxStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingOutboxStore(string path)
    {
        _store = new RoutingAtomicJsonStore<RoutingOutboxDocument>(
            path,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingOutboxModel.Validate,
            RoutingOutboxModel.ValidateSuccessor,
            ValidateInitial);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<RoutingOutboxDocument> Load(
        CancellationToken cancellationToken = default) =>
        _store.Load(cancellationToken);

    internal Task<RoutingOutboxDocument> SaveAsync(
        RoutingOutboxDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null) =>
        _store.SaveAsync(document, expectedGeneration, cancellationToken, beforeCommit);

    internal Task<RoutingOutboxDocument> LoadOrCreateAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        _store.LoadOrCreateAsync(
            () => RoutingOutboxModel.CreateEmpty(now),
            cancellationToken);

    /// <summary>
    /// Loads the outbox and persists conservative crash recovery before returning it. A network
    /// send that was in progress is never repeated automatically; a file move is left for a
    /// content-hash reconciler to inspect. CAS retries tolerate another app instance winning.
    /// </summary>
    internal async Task<RoutingOutboxDocument> LoadAndRecoverAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await LoadOrCreateAsync(now, cancellationToken);
            var recovered = RoutingOutboxModel.RecoverInterruptedWork(
                current,
                now ?? DateTimeOffset.UtcNow);
            if (ReferenceEquals(current, recovered)) return current;
            try
            {
                return await SaveAsync(
                    recovered,
                    current.Generation,
                    cancellationToken);
            }
            catch (RoutingConcurrencyException) when (attempt < 3)
            {
                // Reload and apply recovery to the winning generation.
            }
        }
        throw new RoutingConcurrencyException(
            "The routing outbox kept changing while startup recovery was running.");
    }

    private static void ValidateInitial(RoutingOutboxDocument document)
    {
        RoutingValidation.Require(document.Generation == 1 &&
                                  document.CreatedUtc == document.UpdatedUtc &&
                                  document.Plans.Count == 0 &&
                                  document.Deliveries.Count == 0 &&
                                  document.FileDispositions.Count == 0,
            "The first routing outbox must be the canonical empty document.");
    }
}

/// <summary>
/// Bounded, version-gated, generation-CAS JSON persistence shared by routing configuration and
/// outbox state. It refuses to overwrite corrupt/unsupported state and writes a unique sibling
/// temporary file with write-through before one atomic rename.
/// </summary>
internal sealed class RoutingAtomicJsonStore<TDocument>
    where TDocument : class
{
    private const string SaveMutexPrefix = @"Local\ClipCord.RoutingJson.";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private readonly int _maximumBytes;
    private readonly int _schemaVersion;
    private readonly Func<TDocument, int> _getSchemaVersion;
    private readonly Func<TDocument, long> _getGeneration;
    private readonly Action<TDocument> _validate;
    private readonly Action<TDocument, TDocument> _validateSuccessor;
    private readonly Action<TDocument> _validateInitial;

    internal RoutingAtomicJsonStore(
        string path,
        int maximumBytes,
        int schemaVersion,
        Func<TDocument, int> getSchemaVersion,
        Func<TDocument, long> getGeneration,
        Action<TDocument> validate,
        Action<TDocument, TDocument> validateSuccessor,
        Action<TDocument> validateInitial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        Path = System.IO.Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
        _schemaVersion = schemaVersion;
        _getSchemaVersion = getSchemaVersion ?? throw new ArgumentNullException(nameof(getSchemaVersion));
        _getGeneration = getGeneration ?? throw new ArgumentNullException(nameof(getGeneration));
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        _validateSuccessor = validateSuccessor ?? throw new ArgumentNullException(nameof(validateSuccessor));
        _validateInitial = validateInitial ?? throw new ArgumentNullException(nameof(validateInitial));
    }

    internal string Path { get; }

    internal RoutingDocumentLoadResult<TDocument> Load(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LoadCore(cancellationToken);
    }

    internal Task<TDocument> SaveAsync(
        TDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (expectedGeneration < 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveCore(document, expectedGeneration, cancellationToken, beforeCommit),
            cancellationToken);
    }

    internal async Task<TDocument> LoadOrCreateAsync(
        Func<TDocument> createInitial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createInitial);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = Load(cancellationToken);
            if (loaded.LoadedFromDisk) return loaded.Document!;
            if (loaded.Status != RoutingDocumentLoadStatus.Missing)
            {
                throw new InvalidDataException(
                    $"The routing document cannot be loaded safely ({loaded.Status}).");
            }
            try
            {
                return await SaveAsync(
                    createInitial(),
                    expectedGeneration: 0,
                    cancellationToken);
            }
            catch (RoutingConcurrencyException) when (attempt < 3)
            {
                // Another instance initialized it. Reload rather than replacing its document.
            }
        }
        throw new RoutingConcurrencyException(
            "The routing document kept changing while it was initialized.");
    }

    private TDocument SaveCore(
        TDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken,
        Action? beforeCommit)
    {
        _validate(document);
        cancellationToken.ThrowIfCancellationRequested();
        using var saveMutex = new Mutex(initiallyOwned: false, GetSaveMutexName(Path));
        var lockTaken = false;
        try
        {
            while (!lockTaken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lockTaken = saveMutex.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
            }

            var current = LoadCore(cancellationToken);
            var replacingExisting = current.LoadedFromDisk;
            if (!replacingExisting)
            {
                if (current.Status != RoutingDocumentLoadStatus.Missing)
                {
                    throw new InvalidDataException(
                        $"The existing routing document cannot be replaced safely ({current.Status}).");
                }
                if (expectedGeneration != 0 || _getGeneration(document) != 1)
                {
                    throw new RoutingConcurrencyException(
                        "The routing document appeared or disappeared before it could be saved.");
                }
                _validateInitial(document);
            }
            else
            {
                var persisted = current.Document!;
                if (_getGeneration(persisted) != expectedGeneration)
                {
                    throw new RoutingConcurrencyException(
                        "A newer routing document has already been saved.");
                }
                _validateSuccessor(persisted, document);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length <= 0 || bytes.Length > _maximumBytes)
            {
                throw new InvalidDataException("The routing document is too large.");
            }

            var directory = System.IO.Path.GetDirectoryName(Path)
                ?? throw new InvalidOperationException("The routing directory is unavailable.");
            EnsureNoReparsePointsInExistingPath(directory);
            Directory.CreateDirectory(directory);
            EnsureOrdinaryPath(directory, requireFile: false);
            if (replacingExisting) EnsureOrdinaryPath(Path, requireFile: true);
            var temporaryPath = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                beforeCommit?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOrdinaryPath(directory, requireFile: false);
                EnsureOrdinaryPath(temporaryPath, requireFile: true);
                if (replacingExisting) EnsureOrdinaryPath(Path, requireFile: true);
                File.Move(temporaryPath, Path, overwrite: replacingExisting);
                return document;
            }
            finally
            {
                TryDeleteOwnedTemporaryFile(directory, temporaryPath);
            }
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    private RoutingDocumentLoadResult<TDocument> LoadCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EnsureNoReparsePointsInExistingPath(Path);
            if (!File.Exists(Path))
            {
                return new RoutingDocumentLoadResult<TDocument>(
                    null,
                    RoutingDocumentLoadStatus.Missing);
            }
            EnsureOrdinaryPath(Path, requireFile: true);
            var info = new FileInfo(Path);
            if (info.Length <= 0 || info.Length > _maximumBytes)
            {
                return new RoutingDocumentLoadResult<TDocument>(
                    null,
                    RoutingDocumentLoadStatus.Invalid);
            }
            var bytes = File.ReadAllBytes(Path);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length <= 0 || bytes.Length > _maximumBytes)
            {
                return new RoutingDocumentLoadResult<TDocument>(
                    null,
                    RoutingDocumentLoadStatus.Invalid);
            }

            using (var parsed = JsonDocument.Parse(bytes))
            {
                if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                    !schema.TryGetInt32(out var schemaVersion))
                {
                    return new RoutingDocumentLoadResult<TDocument>(
                        null,
                        RoutingDocumentLoadStatus.Invalid);
                }
                if (schemaVersion != _schemaVersion)
                {
                    return new RoutingDocumentLoadResult<TDocument>(
                        null,
                        RoutingDocumentLoadStatus.UnsupportedSchema);
                }
            }

            var document = JsonSerializer.Deserialize<TDocument>(bytes, JsonOptions);
            if (document is null)
            {
                return new RoutingDocumentLoadResult<TDocument>(
                    null,
                    RoutingDocumentLoadStatus.Corrupt);
            }
            if (_getSchemaVersion(document) != _schemaVersion)
            {
                return new RoutingDocumentLoadResult<TDocument>(
                    null,
                    RoutingDocumentLoadStatus.UnsupportedSchema);
            }
            _validate(document);
            return new RoutingDocumentLoadResult<TDocument>(
                document,
                RoutingDocumentLoadStatus.Loaded);
        }
        catch (JsonException)
        {
            return new RoutingDocumentLoadResult<TDocument>(
                null,
                RoutingDocumentLoadStatus.Corrupt);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NullReferenceException or OverflowException)
        {
            return new RoutingDocumentLoadResult<TDocument>(
                null,
                RoutingDocumentLoadStatus.Invalid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new RoutingDocumentLoadResult<TDocument>(
                null,
                RoutingDocumentLoadStatus.Unavailable);
        }
    }

    private static string GetSaveMutexName(string path)
    {
        var normalized = System.IO.Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return SaveMutexPrefix + hash;
    }

    private static void EnsureOrdinaryPath(string path, bool requireFile)
    {
        EnsureNoReparsePointsInExistingPath(path);
        if (requireFile ? !File.Exists(path) : !Directory.Exists(path))
        {
            throw new IOException("The routing state path has an unexpected filesystem type.");
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Routing state cannot use a symbolic link or junction.");
        }
    }

    /// <summary>
    /// Rejects a reparse point anywhere between the volume root and the state path. Checking only
    /// the leaf still permits a normal-looking routes.json to be reached through an ancestor
    /// junction. Missing suffixes are allowed so a new ordinary routing directory can be created.
    /// </summary>
    private static void EnsureNoReparsePointsInExistingPath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(fullPath)
            ?? throw new IOException("The routing state path has no filesystem root.");
        var relative = fullPath[root.Length..];
        var current = root;
        foreach (var component in relative.Split(
                     [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Routing state cannot traverse a symbolic link, mount point, or junction.");
            }
        }
    }

    private static void TryDeleteOwnedTemporaryFile(string directory, string temporaryPath)
    {
        try
        {
            var file = new FileInfo(temporaryPath);
            if (!file.Exists || file.Directory is null ||
                !file.Directory.FullName.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                !file.Name.StartsWith(".", StringComparison.Ordinal) ||
                !file.Name.EndsWith(".tmp", StringComparison.Ordinal) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }
            file.Delete();
        }
        catch
        {
            // Cleanup is restricted to the one exact owned temporary file.
        }
    }
}
