namespace ClipsToDiscord;

/// <summary>
/// A user-configured source that participates in Routing without changing the single legacy
/// watched-folder source retained by <see cref="AppSettings"/>. Additional kinds can be added
/// here without widening legacy migration authority.
/// </summary>
internal enum RoutingInputSourceKind
{
    // These numeric values are persisted in input-sources.json. Never reorder or renumber them.
    XboxGameDvrOneDrive = 0,
    SteelSeriesGg = 1,
    Nvidia = 2
}

/// <summary>
/// Creates the durable native-root authority used by ordinary watched-folder sources. The hash
/// intentionally shares the exact identity domain used by <see cref="RoutingWatchedSourceAdapter"/>
/// so catalog registration and later file admission cannot disagree about which folder was bound.
/// </summary>
internal static class RoutingWatchedSourceRootIdentity
{
    internal static string Create(
        RoutingInputSourceKind kind,
        string canonicalRoot)
    {
        var captureSource = kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg => ClipCaptureSource.SteelSeriesGg,
            RoutingInputSourceKind.Nvidia => ClipCaptureSource.Nvidia,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Only SteelSeries GG and NVIDIA use watched-folder root identities.")
        };
        var rootPath = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(canonicalRoot);
        return RoutingWatchedSourceAdapters.Get(captureSource)
            .InspectRootIdentity(rootPath);
    }
}

internal enum RoutingInputSourceHealth
{
    Ready,
    NeedsAttention
}

internal enum RoutingInputSourceAttentionReason
{
    None,
    MetadataInvalid,
    MetadataCapacityExceeded,
    RootAuthorityChanged,
    JournalUnavailable,
    SourceRevisionChanged,
    BlockedOccurrence
}

/// <summary>
/// Durable configuration for one named input. CanonicalRoot is private local configuration; its
/// value must never be copied into route snapshots, outbox records, or diagnostics.
/// </summary>
internal sealed record RoutingInputSourceRecord(
    string SourceId,
    long Revision,
    string DisplayName,
    RoutingInputSourceKind Kind,
    string CanonicalRoot,
    bool Enabled,
    bool Retired,
    RoutingInputSourceHealth Health,
    RoutingInputSourceAttentionReason AttentionReason,
    string RootIdentitySha256,
    string WindowsTimeZoneId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public override string ToString() =>
        $"RoutingInputSourceRecord {{ {SourceId}, {DisplayName}, {Kind}, " +
        $"Enabled = {Enabled}, Retired = {Retired}, Health = {Health}, " +
        $"Attention = {AttentionReason}, " +
        "root and authority redacted }";
}

internal sealed record RoutingInputSourceCatalogDocument(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<RoutingInputSourceRecord> Sources,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool Equals(RoutingInputSourceCatalogDocument? other) =>
        ReferenceEquals(this, other) ||
        other is not null && SchemaVersion == other.SchemaVersion &&
        Generation == other.Generation && CreatedUtc == other.CreatedUtc &&
        UpdatedUtc == other.UpdatedUtc &&
        RoutingStructural.SequenceEqual(Sources, other.Sources);

    public override int GetHashCode() => RoutingStructural.Hash(
        SchemaVersion, Generation, Sources, CreatedUtc, UpdatedUtc);
}

internal sealed record RoutingInputSourceCatalogSnapshot(
    RoutingDocumentLoadStatus StoreStatus,
    long Generation,
    IReadOnlyList<RoutingInputSourceRecord> Sources)
{
    internal bool IsUsable => StoreStatus is
        RoutingDocumentLoadStatus.Missing or RoutingDocumentLoadStatus.Loaded;
}

internal static class RoutingInputSourceCatalogModel
{
    internal const int MaximumSources = 32;
    internal const int MaximumDisplayNameLength = 80;
    internal const int MaximumCanonicalRootLength = 1_024;
    internal const int MaximumTimeZoneIdLength = 128;
    internal const string SourceIdPrefix = "source.";

    internal static RoutingInputSourceCatalogDocument CreateEmpty(DateTimeOffset? now = null)
    {
        var timestamp = RoutingValidation.Utc(now ?? DateTimeOffset.UtcNow);
        return new RoutingInputSourceCatalogDocument(
            RoutingInputSourceCatalogStore.CurrentSchemaVersion,
            Generation: 1,
            Sources: [],
            CreatedUtc: timestamp,
            UpdatedUtc: timestamp);
    }

    internal static RoutingInputSourceCatalogDocument ReplaceSources(
        RoutingInputSourceCatalogDocument current,
        IReadOnlyList<RoutingInputSourceRecord> sources,
        DateTimeOffset now)
    {
        Validate(current);
        ArgumentNullException.ThrowIfNull(sources);
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Sources = sources.ToArray(),
            UpdatedUtc = RoutingValidation.Utc(now)
        };
        ValidateSuccessor(current, candidate);
        return candidate;
    }

    internal static void Validate(RoutingInputSourceCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RoutingValidation.Require(
            document.SchemaVersion == RoutingInputSourceCatalogStore.CurrentSchemaVersion,
            "The Routing input-source catalog schema is unsupported.");
        RoutingValidation.Require(document.Generation > 0,
            "The Routing input-source catalog generation is invalid.");
        RoutingValidation.RequireUtc(document.CreatedUtc,
            "Routing input-source catalog creation timestamp");
        RoutingValidation.RequireUtc(document.UpdatedUtc,
            "Routing input-source catalog update timestamp");
        RoutingValidation.Require(document.UpdatedUtc >= document.CreatedUtc,
            "The Routing input-source catalog timestamps are inconsistent.");

        var sources = document.Sources ??
            throw new InvalidDataException("The Routing input-source collection is missing.");
        RoutingValidation.Require(sources.Count <= MaximumSources,
            "The Routing input-source catalog contains too many sources.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var activeRoots = new List<string>();
        var rootIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source is null)
                throw new InvalidDataException("A Routing input-source record is missing.");
            ValidateSourceId(source.SourceId);
            RoutingValidation.Require(ids.Add(source.SourceId),
                "Routing input-source ids must be unique.");
            RoutingValidation.Require(source.Revision > 0,
                "A Routing input-source revision is invalid.");
            RoutingValidation.RequireText(
                source.DisplayName, 1, MaximumDisplayNameLength,
                "Routing input-source display name");
            RoutingValidation.Require(
                source.DisplayName == source.DisplayName.Trim() &&
                source.DisplayName.All(character => !char.IsControl(character)),
                "The Routing input-source display name is not canonical.");
            RoutingValidation.Require(Enum.IsDefined(source.Kind) &&
                                      Enum.IsDefined(source.Health) &&
                                      Enum.IsDefined(source.AttentionReason),
                "The Routing input-source kind, health, or attention reason is unsupported.");
            RoutingValidation.RequireSha256(
                source.RootIdentitySha256, "Routing input-source root identity");
            RoutingValidation.Require(
                source.RootIdentitySha256.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                rootIdentities.Add(source.RootIdentitySha256),
                "Routing input-source root identities must be canonical and unique.");
            RoutingValidation.Require(
                source.Health == RoutingInputSourceHealth.Ready
                    ? source.AttentionReason == RoutingInputSourceAttentionReason.None
                    : source.AttentionReason != RoutingInputSourceAttentionReason.None,
                "The Routing input-source health and attention reason are inconsistent.");
            var root = NormalizeCanonicalRoot(source.CanonicalRoot);
            RoutingValidation.Require(
                source.CanonicalRoot.Equals(root, StringComparison.Ordinal),
                "A Routing input-source root is not canonical.");
            if (!source.Retired)
            {
                RoutingValidation.Require(
                    activeRoots.All(existing =>
                        !CapturePathPolicy.PathsOverlap(existing, root)),
                    "Active Routing input-source roots must not overlap.");
                activeRoots.Add(root);
            }
            RoutingValidation.Require(
                !source.Retired ||
                !source.Enabled &&
                source.Health == RoutingInputSourceHealth.NeedsAttention &&
                source.AttentionReason ==
                    RoutingInputSourceAttentionReason.RootAuthorityChanged,
                "A retired Routing input source must remain disabled under its original authority.");
            ValidateWindowsTimeZoneId(source.WindowsTimeZoneId);
            RoutingValidation.RequireUtc(source.CreatedUtc,
                "Routing input-source creation timestamp");
            RoutingValidation.RequireUtc(source.UpdatedUtc,
                "Routing input-source update timestamp");
            RoutingValidation.Require(
                source.CreatedUtc >= document.CreatedUtc &&
                source.UpdatedUtc >= source.CreatedUtc &&
                source.UpdatedUtc <= document.UpdatedUtc,
                "The Routing input-source timestamps are outside the catalog history.");
        }
    }

    internal static void ValidateSuccessor(
        RoutingInputSourceCatalogDocument current,
        RoutingInputSourceCatalogDocument candidate)
    {
        Validate(current);
        Validate(candidate);
        RoutingValidation.Require(
            candidate.Generation == checked(current.Generation + 1) &&
            candidate.CreatedUtc == current.CreatedUtc &&
            candidate.UpdatedUtc >= current.UpdatedUtc,
            "The Routing input-source catalog successor is inconsistent.");

        var previousById = current.Sources.ToDictionary(
            source => source.SourceId, StringComparer.Ordinal);
        foreach (var source in candidate.Sources)
        {
            if (!previousById.TryGetValue(source.SourceId, out var previous))
            {
                RoutingValidation.Require(
                    source.Revision == 1 &&
                    source.CreatedUtc == source.UpdatedUtc &&
                    source.CreatedUtc >= current.UpdatedUtc,
                    "A new Routing input source has invalid history.");
                continue;
            }

            RoutingValidation.Require(
                source.CreatedUtc == previous.CreatedUtc && source.Kind == previous.Kind &&
                source.RootIdentitySha256 == previous.RootIdentitySha256 &&
                source.Revision is var revision &&
                (revision == previous.Revision ||
                 revision == checked(previous.Revision + 1)),
                "A Routing input source changed immutable identity or skipped a revision.");
            if (source.Revision == previous.Revision)
            {
                RoutingValidation.Require(source == previous,
                    "A Routing input source changed without advancing its revision.");
            }
            else
            {
                RoutingValidation.Require(source.UpdatedUtc >= previous.UpdatedUtc,
                    "A revised Routing input source moved its timestamp backwards.");
            }
        }
    }

    internal static void ValidateInitial(RoutingInputSourceCatalogDocument document)
    {
        Validate(document);
        RoutingValidation.Require(
            document.Generation == 1 && document.Sources.Count == 0 &&
            document.CreatedUtc == document.UpdatedUtc,
            "The initial Routing input-source catalog must be empty.");
    }

    internal static string NormalizeCanonicalRoot(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        RoutingValidation.Require(
            candidate.Length is > 0 and <= MaximumCanonicalRootLength &&
            candidate.All(character => character != '\0' && !char.IsControl(character)) &&
            Path.IsPathFullyQualified(candidate),
            "The Routing input-source root must be a canonical absolute path.");
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                "The Routing input-source root must be a canonical absolute path.", exception);
        }
        var volumeRoot = Path.GetPathRoot(full);
        RoutingValidation.Require(
            full.Length <= MaximumCanonicalRootLength &&
            !string.IsNullOrWhiteSpace(volumeRoot) &&
            !full.Equals(
                Path.TrimEndingDirectorySeparator(volumeRoot),
                StringComparison.OrdinalIgnoreCase),
            "A Routing input source cannot watch an entire filesystem volume.");
        return full;
    }

    internal static void ValidateSourceId(string? sourceId)
    {
        RoutingValidation.RequireOpaqueId(sourceId, 128, "Routing input-source id");
        RoutingValidation.Require(
            sourceId!.StartsWith(SourceIdPrefix, StringComparison.Ordinal) &&
            sourceId.Length == SourceIdPrefix.Length + 32 &&
            sourceId[SourceIdPrefix.Length..].All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "A Routing input-source id is not canonical.");
    }

    internal static void ValidateWindowsTimeZoneId(string? value)
    {
        RoutingValidation.RequireText(
            value, 1, MaximumTimeZoneIdLength, "Routing input-source Windows time zone id");
        RoutingValidation.Require(
            value == value!.Trim() && value.All(character => !char.IsControl(character)),
            "The Routing input-source Windows time zone id is not canonical.");
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(value);
            RoutingValidation.Require(
                zone.Id.Equals(value, StringComparison.OrdinalIgnoreCase),
                "The Routing input-source Windows time zone id is unsupported.");
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidDataException(
                "The Routing input-source Windows time zone id is unsupported.", exception);
        }
    }
}

internal sealed class RoutingInputSourceCatalogStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal const string FileName = "input-sources.json";

    private readonly RoutingAtomicJsonStore<RoutingInputSourceCatalogDocument> _store;

    internal RoutingInputSourceCatalogStore()
        : this(System.IO.Path.Combine(SettingsStore.DataDirectory, "routing", FileName))
    {
    }

    internal RoutingInputSourceCatalogStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetFileName(canonical).Equals(FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The Routing input-source catalog must be named {FileName}.", nameof(path));
        }
        _store = new RoutingAtomicJsonStore<RoutingInputSourceCatalogDocument>(
            canonical,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            document => document.SchemaVersion,
            document => document.Generation,
            RoutingInputSourceCatalogModel.Validate,
            RoutingInputSourceCatalogModel.ValidateSuccessor,
            RoutingInputSourceCatalogModel.ValidateInitial);
    }

    internal string Path => _store.Path;

    internal RoutingDocumentLoadResult<RoutingInputSourceCatalogDocument> Load(
        CancellationToken cancellationToken = default) => _store.Load(cancellationToken);

    internal async Task<RoutingInputSourceCatalogDocument> SaveAsync(
        RoutingInputSourceCatalogDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null)
    {
        using var gate = await RoutingInputSourceExecutionGate.EnterAsync(
                Path, cancellationToken)
            .ConfigureAwait(false);
        return await _store.SaveAsync(
                document, expectedGeneration, cancellationToken, beforeCommit)
            .ConfigureAwait(false);
    }

    internal Task<RoutingInputSourceCatalogDocument> SaveWithinExecutionGateAsync(
        RoutingInputSourceCatalogDocument document,
        long expectedGeneration,
        CancellationToken cancellationToken = default,
        Action? beforeCommit = null) =>
        _store.SaveAsync(document, expectedGeneration, cancellationToken, beforeCommit);

    internal Task<RoutingInputSourceCatalogDocument> LoadOrCreateAsync(
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        _store.LoadOrCreateAsync(
            () => RoutingInputSourceCatalogModel.CreateEmpty(now), cancellationToken);
}

/// <summary>
/// Process-wide linearization boundary between source-authority mutations and a content import.
/// UI and runtime construct separate catalog objects, so the canonical catalog path—not object
/// identity—is the shared key.
/// </summary>
internal static class RoutingInputSourceExecutionGate
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static IDisposable NoopLease { get; } = new Noop();

    internal static async ValueTask<IDisposable> EnterAsync(
        string catalogPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        var key = System.IO.Path.GetFullPath(catalogPath);
        var gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        internal Lease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private sealed class Noop : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal enum RoutingInputSourceReferenceStatus
{
    NotReferenced,
    Referenced,
    StateUnavailable
}

internal interface IRoutingInputSourceReferenceProbe
{
    RoutingInputSourceReferenceStatus Inspect(
        string sourceId,
        CancellationToken cancellationToken = default);

    RoutingInputSourceReferenceStatus Inspect(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Inspect(source.SourceId, cancellationToken);
    }
}

internal sealed class UnavailableRoutingInputSourceReferenceProbe :
    IRoutingInputSourceReferenceProbe
{
    internal static UnavailableRoutingInputSourceReferenceProbe Instance { get; } = new();

    private UnavailableRoutingInputSourceReferenceProbe()
    {
    }

    public RoutingInputSourceReferenceStatus Inspect(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
        return RoutingInputSourceReferenceStatus.StateUnavailable;
    }
}

/// <summary>
/// Fails every source removal closed while a route still names it. Xbox sources additionally retain
/// their existing occurrence-journal guard until work reaches immutable Capture Library ownership;
/// ordinary watched sources do not acquire Xbox-only state merely by being listed in the catalog.
/// </summary>
internal sealed class RoutingInputSourceReferenceProbe : IRoutingInputSourceReferenceProbe
{
    private readonly RoutingSnapshotStore _routes;
    private readonly string _routingRoot;

    internal RoutingInputSourceReferenceProbe()
        : this(
            new RoutingSnapshotStore(),
            Path.Combine(SettingsStore.DataDirectory, "routing"))
    {
    }

    internal RoutingInputSourceReferenceProbe(
        RoutingSnapshotStore routes,
        string routingRoot)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        ArgumentException.ThrowIfNullOrWhiteSpace(routingRoot);
        _routingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(routingRoot));
    }

    public RoutingInputSourceReferenceStatus Inspect(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);

        var routeReference = InspectRouteReferences(sourceId, cancellationToken);
        if (routeReference != RoutingInputSourceReferenceStatus.NotReferenced)
            return routeReference;
        var watchedReference = InspectNamedWatchedJournalReferences(
            sourceId, cancellationToken);
        return watchedReference == RoutingInputSourceReferenceStatus.NotReferenced
            ? InspectXboxOccurrenceReferences(sourceId, cancellationToken)
            : watchedReference;
    }

    public RoutingInputSourceReferenceStatus Inspect(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        RoutingInputSourceCatalogModel.ValidateSourceId(source.SourceId);
        var routeReference = InspectRouteReferences(source.SourceId, cancellationToken);
        if (routeReference != RoutingInputSourceReferenceStatus.NotReferenced)
            return routeReference;
        if (source.Kind is RoutingInputSourceKind.SteelSeriesGg or
            RoutingInputSourceKind.Nvidia)
        {
            return InspectNamedWatchedJournalReferences(
                source.SourceId, cancellationToken);
        }
        return source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
            ? InspectXboxOccurrenceReferences(source.SourceId, cancellationToken)
            : RoutingInputSourceReferenceStatus.NotReferenced;
    }

    private RoutingInputSourceReferenceStatus InspectRouteReferences(
        string sourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var routes = _routes.Load(cancellationToken);
        if (routes.Status != RoutingDocumentLoadStatus.Missing && !routes.LoadedFromDisk)
            return RoutingInputSourceReferenceStatus.StateUnavailable;
        if (routes.Document?.Routes.Any(route => route.Conditions.Any(condition =>
                condition.Field == RoutingConditionField.SourceConnection &&
                condition.Operator == RoutingConditionOperator.Equals &&
                condition.Value.Equals(sourceId, StringComparison.Ordinal))) == true)
        {
            return RoutingInputSourceReferenceStatus.Referenced;
        }

        return RoutingInputSourceReferenceStatus.NotReferenced;
    }

    private RoutingInputSourceReferenceStatus InspectXboxOccurrenceReferences(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var occurrences = new XboxDvrOccurrenceStore(_routingRoot, sourceId)
            .Load(cancellationToken);
        if (occurrences.Status != RoutingDocumentLoadStatus.Missing &&
            !occurrences.LoadedFromDisk)
        {
            return RoutingInputSourceReferenceStatus.StateUnavailable;
        }
        return occurrences.Document?.Occurrences.Any(occurrence =>
                   occurrence.State is not (XboxDvrOccurrenceState.Imported or
                       XboxDvrOccurrenceState.Skipped or
                       XboxDvrOccurrenceState.RouteRevoked)) == true
            ? RoutingInputSourceReferenceStatus.Referenced
            : RoutingInputSourceReferenceStatus.NotReferenced;
    }

    private RoutingInputSourceReferenceStatus InspectNamedWatchedJournalReferences(
        string sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var journals = new RoutingWatchedSourceJournalStore(Path.Combine(
                _routingRoot,
                "watched-journal",
                "v1"));
            foreach (var clipId in journals.EnumerateSourceClipIds(cancellationToken))
            {
                var loaded = journals.Load(clipId, cancellationToken);
                if (!loaded.LoadedFromDisk || loaded.Document is null)
                    return RoutingInputSourceReferenceStatus.StateUnavailable;
                if (loaded.Document.SourceConnectionId?.Equals(
                        sourceId, StringComparison.Ordinal) == true)
                {
                    // The source root still owns the filed Gallery archive and the private
                    // path needed to project durable Activity history. Keep its authority.
                    return RoutingInputSourceReferenceStatus.Referenced;
                }
            }
            return RoutingInputSourceReferenceStatus.NotReferenced;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return RoutingInputSourceReferenceStatus.StateUnavailable;
        }
    }
}

internal interface IRoutingInputSourceMembership
{
    bool IsReady(
        string sourceId,
        RoutingInputSourceKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether a route may bind to this stable source authority. Registration creates a
    /// Ready-but-disabled source so the route can be persisted before runtime scanning is enabled;
    /// implementations that do not distinguish that phase retain their prior IsReady behavior.
    /// </summary>
    bool IsRouteBindable(
        string sourceId,
        RoutingInputSourceKind kind,
        CancellationToken cancellationToken = default) =>
        IsReady(sourceId, kind, cancellationToken);

    ValueTask<IDisposable> EnterExecutionGateAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RoutingInputSourceExecutionGate.NoopLease);
}

internal enum RoutingInputSourceMutationStatus
{
    Added,
    Updated,
    Disabled,
    Removed,
    AlreadyExists,
    Unchanged,
    NotFound,
    InUse,
    ReplacementRequired,
    InvalidInput,
    StateUnavailable,
    Conflict
}

internal sealed record RoutingInputSourceMutationResult(
    RoutingInputSourceMutationStatus Status,
    string Reason,
    RoutingInputSourceRecord? Source)
{
    internal bool Succeeded => Status is
        RoutingInputSourceMutationStatus.Added or
        RoutingInputSourceMutationStatus.Updated or
        RoutingInputSourceMutationStatus.Disabled or
        RoutingInputSourceMutationStatus.Removed or
        RoutingInputSourceMutationStatus.AlreadyExists or
        RoutingInputSourceMutationStatus.Unchanged;
}

/// <summary>
/// Generation-CAS manager for additive Routing sources. Identity-changing updates and removals
/// fail closed while a reference exists. Display-only, health, and enabled-state changes retain
/// the same stable source id and revision history.
/// </summary>
internal sealed class RoutingInputSourceCatalog : IRoutingInputSourceMembership
{
    private const int MaximumSaveAttempts = 6;
    private readonly RoutingInputSourceCatalogStore _store;
    private readonly IRoutingInputSourceReferenceProbe _references;
    private readonly Func<Guid> _idFactory;
    private readonly Func<DateTimeOffset> _clock;

    internal RoutingInputSourceCatalog()
        : this(
            new RoutingInputSourceCatalogStore(),
            new RoutingInputSourceReferenceProbe(),
            Guid.NewGuid,
            () => DateTimeOffset.UtcNow)
    {
    }

    internal RoutingInputSourceCatalog(
        RoutingInputSourceCatalogStore store,
        IRoutingInputSourceReferenceProbe references,
        Func<Guid>? idFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _idFactory = idFactory ?? Guid.NewGuid;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal RoutingInputSourceCatalogSnapshot Inspect(
        CancellationToken cancellationToken = default)
    {
        var loaded = _store.Load(cancellationToken);
        return loaded.LoadedFromDisk && loaded.Document is not null
            ? new RoutingInputSourceCatalogSnapshot(
                RoutingDocumentLoadStatus.Loaded,
                loaded.Document.Generation,
                loaded.Document.Sources.ToArray())
            : new RoutingInputSourceCatalogSnapshot(loaded.Status, 0, []);
    }

    internal string StorePath => _store.Path;

    internal ValueTask<IDisposable> EnterExecutionGateAsync(
        CancellationToken cancellationToken = default) =>
        RoutingInputSourceExecutionGate.EnterAsync(StorePath, cancellationToken);

    bool IRoutingInputSourceMembership.IsReady(
        string sourceId,
        RoutingInputSourceKind kind,
        CancellationToken cancellationToken)
    {
        if (!TryValidateSourceId(sourceId) || !Enum.IsDefined(kind)) return false;
        var snapshot = Inspect(cancellationToken);
        return snapshot.IsUsable && snapshot.Sources.Any(source =>
            source.SourceId.Equals(sourceId, StringComparison.Ordinal) &&
            source.Kind == kind && !source.Retired && source.Enabled &&
             source.Health == RoutingInputSourceHealth.Ready);
    }

    bool IRoutingInputSourceMembership.IsRouteBindable(
        string sourceId,
        RoutingInputSourceKind kind,
        CancellationToken cancellationToken)
    {
        if (!TryValidateSourceId(sourceId) || !Enum.IsDefined(kind)) return false;
        var snapshot = Inspect(cancellationToken);
        return snapshot.IsUsable && snapshot.Sources.Any(source =>
            source.SourceId.Equals(sourceId, StringComparison.Ordinal) &&
            source.Kind == kind && !source.Retired &&
            source.Health == RoutingInputSourceHealth.Ready);
    }

    ValueTask<IDisposable> IRoutingInputSourceMembership.EnterExecutionGateAsync(
        CancellationToken cancellationToken) =>
        RoutingInputSourceExecutionGate.EnterAsync(StorePath, cancellationToken);

    /// <summary>
    /// Unverified roots are never admitted as Ready. Callers must use the Xbox registration
    /// service, which obtains a native metadata-only root identity before calling AddVerifiedAsync.
    /// </summary>
    internal Task<RoutingInputSourceMutationResult> AddAsync(
        string displayName,
        string canonicalRoot,
        string windowsTimeZoneId,
        bool enabled = false,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result(
            RoutingInputSourceMutationStatus.InvalidInput,
            "The Xbox folder must be verified without opening clip content before it can be registered.",
            null));
    }

    internal async Task<RoutingInputSourceMutationResult> AddVerifiedAsync(
        string displayName,
        string canonicalRoot,
        string rootIdentitySha256,
        string windowsTimeZoneId,
        bool enabled = false,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        await AddVerifiedCoreAsync(
                displayName,
                RoutingInputSourceKind.XboxGameDvrOneDrive,
                canonicalRoot,
                rootIdentitySha256,
                windowsTimeZoneId,
                enabled,
                now,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Registers one caller-verified ordinary recorder folder. Verification is deliberately a
    /// separate boundary: the caller must first obtain <paramref name="rootIdentitySha256"/> from
    /// <see cref="RoutingWatchedSourceRootIdentity.Create"/>. New sources are Ready but disabled
    /// by default so a route/baseline transaction can be committed before scanning begins.
    /// </summary>
    internal async Task<RoutingInputSourceMutationResult> AddVerifiedWatchedFolderAsync(
        string displayName,
        RoutingInputSourceKind kind,
        string canonicalRoot,
        string rootIdentitySha256,
        bool enabled = false,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (RoutingInputSourceKind.SteelSeriesGg or
            RoutingInputSourceKind.Nvidia))
        {
            return InvalidWatchedFolderInput();
        }

        return await AddVerifiedCoreAsync(
                displayName,
                kind,
                canonicalRoot,
                rootIdentitySha256,
                TimeZoneInfo.Utc.Id,
                enabled,
                now,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RoutingInputSourceMutationResult> AddVerifiedCoreAsync(
        string displayName,
        RoutingInputSourceKind kind,
        string canonicalRoot,
        string rootIdentitySha256,
        string windowsTimeZoneId,
        bool enabled,
        DateTimeOffset? now,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeInput(
                displayName,
                canonicalRoot,
                windowsTimeZoneId,
                out var name,
                out var root,
                out var timeZoneId) ||
            !TryNormalizeRootIdentity(rootIdentitySha256, out var rootIdentity))
        {
            return InvalidInput();
        }

        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await TryLoadOrCreateAsync(now, cancellationToken)
                .ConfigureAwait(false);
            if (current is null) return StateUnavailable();
            var sameRoot = current.Sources.Where(source =>
                    source.CanonicalRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var existing = sameRoot.SingleOrDefault(source => !source.Retired);
            if (existing is not null)
            {
                if (existing.Kind != kind)
                {
                    return Result(
                        RoutingInputSourceMutationStatus.InvalidInput,
                        $"This folder is already registered as {DescribeKind(existing.Kind)}.",
                        existing);
                }
                if (!existing.RootIdentitySha256.Equals(
                        rootIdentity, StringComparison.Ordinal))
                {
                    return Result(
                        RoutingInputSourceMutationStatus.ReplacementRequired,
                        kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                            ? "The configured Xbox folder now has a different filesystem identity. Register it as a new source."
                            : $"The configured {DescribeKind(kind)} folder now has a different filesystem identity. Reconnect it as a replacement source.",
                        existing);
                }
                return Result(
                    RoutingInputSourceMutationStatus.AlreadyExists,
                    kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                        ? "This Xbox Game DVR source is already available to Routes."
                        : $"This {DescribeKind(kind)} watched-folder source is already available to Routes.",
                    existing);
            }
            var overlappingRoot = FindActiveRootOverlap(current, root);
            if (overlappingRoot is not null)
            {
                return Result(
                    RoutingInputSourceMutationStatus.InvalidInput,
                    "This folder overlaps another active clip source. Choose a separate folder so one clip cannot enter two sources.",
                    overlappingRoot);
            }
            var retiredAuthority = sameRoot.SingleOrDefault(source =>
                source.Kind == kind &&
                source.RootIdentitySha256.Equals(rootIdentity, StringComparison.Ordinal));
            if (retiredAuthority is not null)
            {
                return Result(
                    RoutingInputSourceMutationStatus.AlreadyExists,
                    kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                        ? "This native Xbox source is retained as a replaced source and cannot be registered again."
                        : $"This native {DescribeKind(kind)} source is retained as a replaced source and cannot be registered again.",
                    retiredAuthority);
            }
            var existingAuthority = current.Sources.SingleOrDefault(source =>
                source.RootIdentitySha256.Equals(rootIdentity, StringComparison.Ordinal));
            if (existingAuthority is not null)
            {
                if (kind != RoutingInputSourceKind.XboxGameDvrOneDrive ||
                    existingAuthority.Kind != RoutingInputSourceKind.XboxGameDvrOneDrive)
                {
                    return Result(
                        RoutingInputSourceMutationStatus.InvalidInput,
                        "This native folder authority is already registered under another source or path.",
                        existingAuthority);
                }
                return Result(
                    RoutingInputSourceMutationStatus.AlreadyExists,
                    "This Xbox Game DVR folder is already registered under its native identity.",
                    existingAuthority);
            }
            if (current.Sources.Count >= RoutingInputSourceCatalogModel.MaximumSources)
            {
                return Result(
                    RoutingInputSourceMutationStatus.InvalidInput,
                    "The maximum number of Routing input sources has been reached.",
                    null);
            }

            var sourceId = CreateUniqueId(current);
            var timestamp = MutationTimestamp(current, now);
            var added = new RoutingInputSourceRecord(
                sourceId,
                Revision: 1,
                name,
                kind,
                root,
                enabled,
                Retired: false,
                RoutingInputSourceHealth.Ready,
                RoutingInputSourceAttentionReason.None,
                rootIdentity,
                timeZoneId,
                timestamp,
                timestamp);
            var next = RoutingInputSourceCatalogModel.ReplaceSources(
                current,
                current.Sources.Concat([added]).ToArray(),
                timestamp);
            try
            {
                var saved = await _store.SaveAsync(
                        next, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return Result(
                    RoutingInputSourceMutationStatus.Added,
                    kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                        ? "The Xbox Game DVR source was added."
                        : $"The {DescribeKind(kind)} watched-folder source was added.",
                    saved.Sources.Single(source =>
                        source.SourceId.Equals(sourceId, StringComparison.Ordinal)));
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reload so a competing add of this root converges on one stable source.
            }
            catch (RoutingConcurrencyException)
            {
                return Conflict();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
        }
        return Conflict();
    }

    /// <summary>
    /// Atomically retires a replaced source authority and registers the current folder authority
    /// under a new opaque id. The old record and journal remain addressable by existing routes;
    /// the new id cannot inherit routes or historical occurrence approval.
    /// </summary>
    internal async Task<RoutingInputSourceMutationResult> RegisterReplacementAsync(
        string sourceId,
        long expectedRevision,
        string replacementCanonicalRoot,
        string newRootIdentitySha256,
        bool enabled = false,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        string replacementRoot;
        try
        {
            replacementRoot = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(
                replacementCanonicalRoot);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return InvalidInput();
        }
        if (!TryValidateSourceId(sourceId) || expectedRevision <= 0 ||
            !TryNormalizeRootIdentity(newRootIdentitySha256, out var newRootIdentity))
        {
            return InvalidInput();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var current = await TryLoadOrCreateAsync(now, cancellationToken)
            .ConfigureAwait(false);
        if (current is null) return StateUnavailable();
        var existing = current.Sources.SingleOrDefault(source =>
            source.SourceId.Equals(sourceId, StringComparison.Ordinal));
        if (existing is null) return NotFound();
        if (existing.Revision != expectedRevision) return Conflict();
        if (existing.Retired)
        {
            return Result(
                RoutingInputSourceMutationStatus.Unchanged,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "The replaced Xbox source is already retired."
                    : $"The replaced {DescribeKind(existing.Kind)} watched-folder source is already retired.",
                existing);
        }
        if (existing.Health != RoutingInputSourceHealth.NeedsAttention ||
            existing.AttentionReason !=
                RoutingInputSourceAttentionReason.RootAuthorityChanged ||
            existing.RootIdentitySha256.Equals(newRootIdentity, StringComparison.Ordinal))
        {
            return Result(
                RoutingInputSourceMutationStatus.ReplacementRequired,
                "The source must first be verified as a different native folder authority.",
                existing);
        }
        var importing = InspectImportingWork(existing);
        if (importing == RoutingInputSourceReferenceStatus.Referenced)
        {
            return Result(
                RoutingInputSourceMutationStatus.InUse,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "Finish the in-progress Xbox Library recovery before replacing this source."
                    : "Finish the in-progress source recovery before replacing this watched folder.",
                existing);
        }
        if (importing == RoutingInputSourceReferenceStatus.StateUnavailable)
            return StateUnavailable();
        var byAuthority = current.Sources.SingleOrDefault(source =>
            source.RootIdentitySha256.Equals(newRootIdentity, StringComparison.Ordinal));
        if (byAuthority is not null)
        {
            return Result(
                RoutingInputSourceMutationStatus.AlreadyExists,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "The replacement Xbox folder authority is already registered."
                    : $"The replacement {DescribeKind(existing.Kind)} folder authority is already registered.",
                byAuthority);
        }
        if (current.Sources.Count >= RoutingInputSourceCatalogModel.MaximumSources)
        {
            return Result(
                RoutingInputSourceMutationStatus.InvalidInput,
                "The maximum number of Routing input sources has been reached.",
                null);
        }
        var pathOwner = current.Sources.SingleOrDefault(source =>
            !source.SourceId.Equals(sourceId, StringComparison.Ordinal) &&
            !source.Retired &&
            source.CanonicalRoot.Equals(
                replacementRoot, StringComparison.OrdinalIgnoreCase));
        if (pathOwner is not null)
        {
            return Result(
                RoutingInputSourceMutationStatus.AlreadyExists,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "Another active Xbox source already uses the replacement folder."
                    : "Another active clip source already uses the replacement folder.",
                pathOwner);
        }
        var overlappingOwner = FindActiveRootOverlap(
            current, replacementRoot, sourceId);
        if (overlappingOwner is not null)
        {
            return Result(
                RoutingInputSourceMutationStatus.InvalidInput,
                "The replacement folder overlaps another active clip source. Choose a separate folder.",
                overlappingOwner);
        }

        var timestamp = MutationTimestamp(current, now);
        var retired = existing with
        {
            Revision = RoutingValidation.NextGeneration(existing.Revision),
            Enabled = false,
            Retired = true,
            Health = RoutingInputSourceHealth.NeedsAttention,
            AttentionReason = RoutingInputSourceAttentionReason.RootAuthorityChanged,
            UpdatedUtc = timestamp
        };
        var replacementId = CreateUniqueId(current);
        var replacement = new RoutingInputSourceRecord(
            replacementId,
            Revision: 1,
            existing.DisplayName,
            existing.Kind,
            replacementRoot,
            enabled,
            Retired: false,
            RoutingInputSourceHealth.Ready,
            RoutingInputSourceAttentionReason.None,
            newRootIdentity,
            existing.WindowsTimeZoneId,
            timestamp,
            timestamp);
        var next = RoutingInputSourceCatalogModel.ReplaceSources(
            current,
            current.Sources.Select(source =>
                    source.SourceId.Equals(sourceId, StringComparison.Ordinal)
                        ? retired
                        : source)
                .Append(replacement)
                .ToArray(),
            timestamp);
        try
        {
            var saved = await _store.SaveAsync(
                    next, current.Generation, cancellationToken)
                .ConfigureAwait(false);
            return Result(
                RoutingInputSourceMutationStatus.Added,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "The replacement Xbox source was registered under a new identity. Existing routes still reference the retired source."
                    : $"The replacement {DescribeKind(existing.Kind)} watched-folder source was registered under a new identity. Existing routes still reference the retired source.",
                saved.Sources.Single(source =>
                    source.SourceId.Equals(replacementId, StringComparison.Ordinal)));
        }
        catch (RoutingConcurrencyException)
        {
            return Conflict();
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return StateUnavailable();
        }
    }

    internal async Task<RoutingInputSourceMutationResult> RelocateVerifiedAsync(
        string sourceId,
        long expectedRevision,
        string newCanonicalRoot,
        string verifiedRootIdentitySha256,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        string root;
        try
        {
            root = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(newCanonicalRoot);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return InvalidInput();
        }
        if (!TryValidateSourceId(sourceId) || expectedRevision <= 0 ||
            !TryNormalizeRootIdentity(
                verifiedRootIdentitySha256, out var verifiedRootIdentity))
        {
            return InvalidInput();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var current = await TryLoadOrCreateAsync(now, cancellationToken)
            .ConfigureAwait(false);
        if (current is null) return StateUnavailable();
        var existing = current.Sources.SingleOrDefault(source =>
            source.SourceId.Equals(sourceId, StringComparison.Ordinal));
        if (existing is null) return NotFound();
        if (existing.Revision != expectedRevision) return Conflict();
        if (existing.Retired)
        {
            return Result(
                RoutingInputSourceMutationStatus.ReplacementRequired,
                "A retired source authority cannot be relocated.",
                existing);
        }
        if (!existing.RootIdentitySha256.Equals(
                verifiedRootIdentity, StringComparison.Ordinal))
        {
            return Result(
                RoutingInputSourceMutationStatus.ReplacementRequired,
                "The selected folder is a different native source and cannot inherit this SourceId.",
                existing);
        }
        var pathOwner = current.Sources.SingleOrDefault(source =>
            !source.SourceId.Equals(sourceId, StringComparison.Ordinal) &&
            !source.Retired &&
            source.CanonicalRoot.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (pathOwner is not null)
        {
            return Result(
                RoutingInputSourceMutationStatus.AlreadyExists,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "Another active Xbox source already uses the selected folder."
                    : "Another active clip source already uses the selected folder.",
                pathOwner);
        }
        var overlappingOwner = FindActiveRootOverlap(current, root, sourceId);
        if (overlappingOwner is not null)
        {
            return Result(
                RoutingInputSourceMutationStatus.InvalidInput,
                "The selected folder overlaps another active clip source. Choose a separate folder.",
                overlappingOwner);
        }
        if (existing.CanonicalRoot.Equals(root, StringComparison.Ordinal))
        {
            return Result(
                RoutingInputSourceMutationStatus.Unchanged,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "The Xbox source already uses this folder."
                    : $"The {DescribeKind(existing.Kind)} watched-folder source already uses this folder.",
                existing);
        }

        var timestamp = MutationTimestamp(current, now);
        var relocated = existing with
        {
            Revision = RoutingValidation.NextGeneration(existing.Revision),
            CanonicalRoot = root,
            Health = existing.AttentionReason ==
                     RoutingInputSourceAttentionReason.RootAuthorityChanged
                ? RoutingInputSourceHealth.Ready
                : existing.Health,
            AttentionReason = existing.AttentionReason ==
                              RoutingInputSourceAttentionReason.RootAuthorityChanged
                ? RoutingInputSourceAttentionReason.None
                : existing.AttentionReason,
            UpdatedUtc = timestamp
        };
        var next = RoutingInputSourceCatalogModel.ReplaceSources(
            current, Replace(current.Sources, relocated), timestamp);
        try
        {
            var saved = await _store.SaveAsync(
                    next, current.Generation, cancellationToken)
                .ConfigureAwait(false);
            return Result(
                RoutingInputSourceMutationStatus.Updated,
                existing.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "The Xbox source folder moved while preserving its native identity and existing routes."
                    : $"The {DescribeKind(existing.Kind)} watched-folder source moved while preserving its native identity and existing routes.",
                saved.Sources.Single(source =>
                    source.SourceId.Equals(sourceId, StringComparison.Ordinal)));
        }
        catch (RoutingConcurrencyException)
        {
            return Conflict();
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return StateUnavailable();
        }
    }

    internal async Task<RoutingInputSourceMutationResult> UpdateAsync(
        string sourceId,
        string displayName,
        string canonicalRoot,
        string windowsTimeZoneId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSourceId(sourceId) ||
            !TryNormalizeInput(
                displayName,
                canonicalRoot,
                windowsTimeZoneId,
                out var name,
                out var root,
                out var timeZoneId))
        {
            return InvalidInput();
        }

        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await TryLoadOrCreateAsync(now, cancellationToken)
                .ConfigureAwait(false);
            if (current is null) return StateUnavailable();
            var existing = current.Sources.SingleOrDefault(source =>
                source.SourceId.Equals(sourceId, StringComparison.Ordinal));
            if (existing is null) return NotFound();
            if (current.Sources.Any(source =>
                    !source.SourceId.Equals(sourceId, StringComparison.Ordinal) &&
                    source.Kind == existing.Kind &&
                    source.CanonicalRoot.Equals(root, StringComparison.OrdinalIgnoreCase)))
            {
                return Result(
                    RoutingInputSourceMutationStatus.AlreadyExists,
                    "Another Xbox Game DVR source already uses this folder.",
                    existing);
            }

            if (!existing.CanonicalRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return Result(
                    RoutingInputSourceMutationStatus.ReplacementRequired,
                    "A different Xbox folder must be registered as a new source so existing routes and work keep their original authority.",
                    existing);
            }
            if (existing.DisplayName.Equals(name, StringComparison.Ordinal) &&
                existing.CanonicalRoot.Equals(root, StringComparison.Ordinal) &&
                existing.WindowsTimeZoneId.Equals(timeZoneId, StringComparison.Ordinal))
            {
                return Result(
                    RoutingInputSourceMutationStatus.Unchanged,
                    "The Routing input source already has these settings.",
                    existing);
            }

            var timestamp = MutationTimestamp(current, now);
            var changed = existing with
            {
                Revision = RoutingValidation.NextGeneration(existing.Revision),
                DisplayName = name,
                WindowsTimeZoneId = timeZoneId,
                UpdatedUtc = timestamp
            };
            var next = RoutingInputSourceCatalogModel.ReplaceSources(
                current,
                Replace(current.Sources, changed),
                timestamp);
            try
            {
                var saved = await _store.SaveAsync(
                        next,
                        current.Generation,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result(
                    RoutingInputSourceMutationStatus.Updated,
                    "The Routing input source was updated.",
                    saved.Sources.Single(source =>
                        source.SourceId.Equals(sourceId, StringComparison.Ordinal)));
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reload and re-check references against the winning generation.
            }
            catch (RoutingConcurrencyException)
            {
                return Conflict();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
        }
        return Conflict();
    }

    internal Task<RoutingInputSourceMutationResult> DisableAsync(
        string sourceId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(sourceId, enabled: false, now, cancellationToken);

    internal async Task<RoutingInputSourceMutationResult> SetEnabledAsync(
        string sourceId,
        bool enabled,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        !enabled && InspectImportingWork(sourceId) is { } importing &&
        importing != RoutingInputSourceReferenceStatus.NotReferenced
            ? importing == RoutingInputSourceReferenceStatus.Referenced
                ? Result(
                    RoutingInputSourceMutationStatus.InUse,
                    "Finish the in-progress Xbox Library recovery before disabling this source.",
                    Inspect(cancellationToken).Sources.SingleOrDefault(source =>
                        source.SourceId.Equals(sourceId, StringComparison.Ordinal)))
                : StateUnavailable()
            : await MutateStatusAsync(
                sourceId,
                source => source.Retired && enabled || source.Enabled == enabled
                    ? source
                    : source with { Enabled = enabled },
                enabled
                    ? "The Routing input source was enabled."
                    : "The Routing input source was disabled.",
                enabled
                    ? RoutingInputSourceMutationStatus.Updated
                    : RoutingInputSourceMutationStatus.Disabled,
                now,
                cancellationToken,
                expectedRevision: null)
                .ConfigureAwait(false);

    internal async Task<RoutingInputSourceMutationResult> SetEnabledAsync(
        string sourceId,
        long expectedRevision,
        bool enabled,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        !enabled && InspectImportingWork(sourceId) is { } importing &&
        importing != RoutingInputSourceReferenceStatus.NotReferenced
            ? importing == RoutingInputSourceReferenceStatus.Referenced
                ? Result(
                    RoutingInputSourceMutationStatus.InUse,
                    "Finish the in-progress Xbox Library recovery before disabling this source.",
                    Inspect(cancellationToken).Sources.SingleOrDefault(source =>
                        source.SourceId.Equals(sourceId, StringComparison.Ordinal)))
                : StateUnavailable()
            : await MutateStatusAsync(
                sourceId,
                source => source.Retired && enabled || source.Enabled == enabled
                    ? source
                    : source with { Enabled = enabled },
                enabled
                    ? "The Routing input source was enabled."
                    : "The Routing input source was disabled.",
                enabled
                    ? RoutingInputSourceMutationStatus.Updated
                    : RoutingInputSourceMutationStatus.Disabled,
                now,
                cancellationToken,
                expectedRevision)
                .ConfigureAwait(false);

    internal async Task<RoutingInputSourceMutationResult> SetNeedsAttentionAsync(
        string sourceId,
        RoutingInputSourceAttentionReason attentionReason,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (attentionReason == RoutingInputSourceAttentionReason.None ||
            !Enum.IsDefined(attentionReason))
        {
            return InvalidInput();
        }
        return await MutateStatusAsync(
                sourceId,
                source => source.Retired ||
                          source.Health == RoutingInputSourceHealth.NeedsAttention &&
                          source.AttentionReason == attentionReason
                    ? source
                    : source with
                    {
                        Health = RoutingInputSourceHealth.NeedsAttention,
                        AttentionReason = attentionReason
                    },
                "The Routing input source needs attention.",
                RoutingInputSourceMutationStatus.Updated,
                now,
                cancellationToken,
                expectedRevision: null)
            .ConfigureAwait(false);
    }

    internal Task<RoutingInputSourceMutationResult>
        SetRootAuthorityChangedWithinExecutionGateAsync(
            string sourceId,
            long expectedRevision,
            DateTimeOffset? now = null,
            CancellationToken cancellationToken = default) =>
        MutateStatusAsync(
            sourceId,
            source => source.Retired
                ? source
                : source with
                {
                    Health = RoutingInputSourceHealth.NeedsAttention,
                    AttentionReason = RoutingInputSourceAttentionReason.RootAuthorityChanged
                },
            "The Routing input source needs attention.",
            RoutingInputSourceMutationStatus.Updated,
            now,
            cancellationToken,
            expectedRevision,
            executionGateHeld: true);

    internal async Task<RoutingInputSourceMutationResult> SetNeedsAttentionAsync(
        string sourceId,
        long expectedRevision,
        RoutingInputSourceAttentionReason attentionReason,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (attentionReason == RoutingInputSourceAttentionReason.None ||
            !Enum.IsDefined(attentionReason))
        {
            return InvalidInput();
        }
        return await MutateStatusAsync(
                sourceId,
                source => source.Retired ||
                          source.Health == RoutingInputSourceHealth.NeedsAttention &&
                          source.AttentionReason == attentionReason
                    ? source
                    : source with
                    {
                        Health = RoutingInputSourceHealth.NeedsAttention,
                        AttentionReason = attentionReason
                    },
                "The Routing input source needs attention.",
                RoutingInputSourceMutationStatus.Updated,
                now,
                cancellationToken,
                expectedRevision)
            .ConfigureAwait(false);
    }

    internal async Task<RoutingInputSourceMutationResult> TryRestoreReadyAsync(
        string sourceId,
        long expectedRevision,
        string expectedRootIdentitySha256,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeRootIdentity(
                expectedRootIdentitySha256, out var expectedRootIdentity))
        {
            return InvalidInput();
        }
        return await MutateStatusAsync(
                sourceId,
                source => source.Retired || !source.RootIdentitySha256.Equals(
                        expectedRootIdentity, StringComparison.Ordinal)
                    ? source
                    : source.Health == RoutingInputSourceHealth.Ready
                        ? source
                        : source with
                        {
                            Health = RoutingInputSourceHealth.Ready,
                            AttentionReason = RoutingInputSourceAttentionReason.None
                        },
                "The Routing input source is ready.",
                RoutingInputSourceMutationStatus.Updated,
                now,
                cancellationToken,
                expectedRevision,
                requireRootIdentity: expectedRootIdentity)
            .ConfigureAwait(false);
    }

    internal async Task<RoutingInputSourceMutationResult> RemoveAsync(
        string sourceId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        await RemoveCoreAsync(
                sourceId, expectedRevision: null, now, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<RoutingInputSourceMutationResult> RemoveAsync(
        string sourceId,
        long expectedRevision,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        await RemoveCoreAsync(sourceId, expectedRevision, now, cancellationToken)
            .ConfigureAwait(false);

    private async Task<RoutingInputSourceMutationResult> RemoveCoreAsync(
        string sourceId,
        long? expectedRevision,
        DateTimeOffset? now,
        CancellationToken cancellationToken)
    {
        if (!TryValidateSourceId(sourceId) || expectedRevision is <= 0) return InvalidInput();
        using var sourceGate = await RoutingInputSourceExecutionGate.EnterAsync(
                StorePath, cancellationToken)
            .ConfigureAwait(false);
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await TryLoadOrCreateAsync(now, cancellationToken)
                .ConfigureAwait(false);
            if (current is null) return StateUnavailable();
            var existing = current.Sources.SingleOrDefault(source =>
                source.SourceId.Equals(sourceId, StringComparison.Ordinal));
            if (existing is null) return NotFound();
            if (expectedRevision is { } expected && existing.Revision != expected)
                return Conflict();
            if (!await ReconcileRouteRevokedSelectionsAsync(
                    existing, cancellationToken).ConfigureAwait(false))
            {
                return StateUnavailable();
            }
            var reference = InspectReferences(existing, cancellationToken);
            if (reference is { } blocked) return blocked;
            var timestamp = MutationTimestamp(current, now);
            var next = RoutingInputSourceCatalogModel.ReplaceSources(
                current,
                current.Sources.Where(source =>
                        !source.SourceId.Equals(sourceId, StringComparison.Ordinal))
                    .ToArray(),
                timestamp);
            try
            {
                await _store.SaveWithinExecutionGateAsync(
                        next,
                        current.Generation,
                        cancellationToken,
                        () => RequireUnreferenced(existing, cancellationToken))
                    .ConfigureAwait(false);
                return Result(
                    RoutingInputSourceMutationStatus.Removed,
                    "The Routing input source was removed.",
                    existing);
            }
            catch (RoutingInputSourceInUseException)
            {
                return InUse();
            }
            catch (RoutingInputSourceReferenceUnavailableException)
            {
                return StateUnavailable();
            }
            catch (RoutingConcurrencyException) when (
                expectedRevision is null && attempt < MaximumSaveAttempts - 1)
            {
                // Re-check references after every competing catalog mutation.
            }
            catch (RoutingConcurrencyException)
            {
                return Conflict();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
        }
        return Conflict();
    }

    private async Task<bool> ReconcileRouteRevokedSelectionsAsync(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken)
    {
        if (source.Kind != RoutingInputSourceKind.XboxGameDvrOneDrive) return true;
        var routingRoot = System.IO.Path.GetDirectoryName(StorePath);
        if (string.IsNullOrWhiteSpace(routingRoot)) return false;
        var loadedRoutes = new RoutingSnapshotStore(System.IO.Path.Combine(
                routingRoot, RoutingSnapshotStore.FileName))
            .Load(cancellationToken);
        if (loadedRoutes.Status == RoutingDocumentLoadStatus.Missing) return true;
        if (!loadedRoutes.LoadedFromDisk || loadedRoutes.Document is null) return false;
        if (loadedRoutes.Document.Routes.Any(route => route.Conditions.Any(condition =>
                condition.Field == RoutingConditionField.SourceConnection &&
                condition.Operator == RoutingConditionOperator.Equals &&
                condition.Value.Equals(source.SourceId, StringComparison.Ordinal))))
        {
            return true;
        }

        var store = new XboxDvrOccurrenceStore(routingRoot, source.SourceId);
        var loaded = store.Load(cancellationToken);
        if (loaded.Status == RoutingDocumentLoadStatus.Missing) return true;
        if (!loaded.LoadedFromDisk || loaded.Document is null ||
            !loaded.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
            !loaded.Document.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return false;
        }
        var journal = new XboxDvrOccurrenceJournal(
            store, source.SourceId, source.RootIdentitySha256, _clock);
        foreach (var selected in loaded.Document.Occurrences.Where(occurrence =>
                     occurrence.State == XboxDvrOccurrenceState.Selected))
        {
            _ = await journal.DismissRouteRevokedAsync(
                    selected.OccurrenceId, selected.RevisionId, cancellationToken)
                .ConfigureAwait(false);
        }
        return true;
    }

    private RoutingInputSourceReferenceStatus InspectImportingWork(string sourceId)
    {
        if (!TryValidateSourceId(sourceId))
            return RoutingInputSourceReferenceStatus.StateUnavailable;
        var snapshot = Inspect();
        if (!snapshot.IsUsable)
            return RoutingInputSourceReferenceStatus.StateUnavailable;
        var source = snapshot.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(sourceId, StringComparison.Ordinal));
        return source is null
            ? RoutingInputSourceReferenceStatus.NotReferenced
            : InspectImportingWork(source);
    }

    private RoutingInputSourceReferenceStatus InspectImportingWork(
        RoutingInputSourceRecord source)
    {
        if (source.Kind != RoutingInputSourceKind.XboxGameDvrOneDrive)
            return RoutingInputSourceReferenceStatus.NotReferenced;
        var routingRoot = System.IO.Path.GetDirectoryName(StorePath);
        if (string.IsNullOrWhiteSpace(routingRoot))
            return RoutingInputSourceReferenceStatus.StateUnavailable;
        var loaded = new XboxDvrOccurrenceStore(routingRoot, source.SourceId).Load();
        if (loaded.Status == RoutingDocumentLoadStatus.Missing)
            return RoutingInputSourceReferenceStatus.NotReferenced;
        if (!loaded.LoadedFromDisk || loaded.Document is null ||
            !loaded.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
            !loaded.Document.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return RoutingInputSourceReferenceStatus.StateUnavailable;
        }
        return loaded.Document.Occurrences.Any(occurrence =>
                occurrence.State == XboxDvrOccurrenceState.Importing)
            ? RoutingInputSourceReferenceStatus.Referenced
            : RoutingInputSourceReferenceStatus.NotReferenced;
    }

    private async Task<RoutingInputSourceMutationResult> MutateStatusAsync(
        string sourceId,
        Func<RoutingInputSourceRecord, RoutingInputSourceRecord> mutate,
        string reason,
        RoutingInputSourceMutationStatus changedStatus,
        DateTimeOffset? now,
        CancellationToken cancellationToken,
        long? expectedRevision,
        string? requireRootIdentity = null,
        bool executionGateHeld = false)
    {
        if (!TryValidateSourceId(sourceId) || expectedRevision is <= 0) return InvalidInput();
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await TryLoadOrCreateAsync(now, cancellationToken)
                .ConfigureAwait(false);
            if (current is null) return StateUnavailable();
            var existing = current.Sources.SingleOrDefault(source =>
                source.SourceId.Equals(sourceId, StringComparison.Ordinal));
            if (existing is null) return NotFound();
            if (expectedRevision is { } expected && existing.Revision != expected ||
                requireRootIdentity is not null && !existing.RootIdentitySha256.Equals(
                    requireRootIdentity, StringComparison.Ordinal))
            {
                return Conflict();
            }
            var mutated = mutate(existing);
            if (mutated == existing)
            {
                return Result(
                    RoutingInputSourceMutationStatus.Unchanged,
                    "The Routing input source already has this state.",
                    existing);
            }

            var timestamp = MutationTimestamp(current, now);
            var changed = mutated with
            {
                Revision = RoutingValidation.NextGeneration(existing.Revision),
                UpdatedUtc = timestamp
            };
            var next = RoutingInputSourceCatalogModel.ReplaceSources(
                current,
                Replace(current.Sources, changed),
                timestamp);
            try
            {
                var saved = await (executionGateHeld
                        ? _store.SaveWithinExecutionGateAsync(
                            next, current.Generation, cancellationToken)
                        : _store.SaveAsync(
                            next, current.Generation, cancellationToken))
                    .ConfigureAwait(false);
                return Result(
                    changedStatus,
                    reason,
                    saved.Sources.Single(source =>
                        source.SourceId.Equals(sourceId, StringComparison.Ordinal)));
            }
            catch (RoutingConcurrencyException) when (
                expectedRevision is null && attempt < MaximumSaveAttempts - 1)
            {
                // Reload and apply the requested state to the winning revision.
            }
            catch (RoutingConcurrencyException)
            {
                return Conflict();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                return StateUnavailable();
            }
        }
        return Conflict();
    }

    private async Task<RoutingInputSourceCatalogDocument?> TryLoadOrCreateAsync(
        DateTimeOffset? now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.LoadOrCreateAsync(
                    now ?? _clock(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return null;
        }
    }

    private RoutingInputSourceMutationResult? InspectReferences(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken) => _references.Inspect(
            source, cancellationToken) switch
        {
            RoutingInputSourceReferenceStatus.NotReferenced => null,
            RoutingInputSourceReferenceStatus.Referenced => InUse(),
            _ => StateUnavailable()
        };

    private void RequireUnreferenced(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken)
    {
        switch (_references.Inspect(source, cancellationToken))
        {
            case RoutingInputSourceReferenceStatus.NotReferenced:
                return;
            case RoutingInputSourceReferenceStatus.Referenced:
                throw new RoutingInputSourceInUseException();
            default:
                throw new RoutingInputSourceReferenceUnavailableException();
        }
    }

    private string CreateUniqueId(RoutingInputSourceCatalogDocument document)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var value = _idFactory();
            if (value == Guid.Empty) continue;
            var candidate = RoutingInputSourceCatalogModel.SourceIdPrefix + value.ToString("N");
            if (document.Sources.All(source =>
                    !source.SourceId.Equals(candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException(
            "A unique Routing input-source id could not be created.");
    }

    private static RoutingInputSourceRecord? FindActiveRootOverlap(
        RoutingInputSourceCatalogDocument document,
        string canonicalRoot,
        string? excludedSourceId = null) => document.Sources.FirstOrDefault(source =>
        !source.Retired &&
        (excludedSourceId is null || !source.SourceId.Equals(
            excludedSourceId, StringComparison.Ordinal)) &&
        CapturePathPolicy.PathsOverlap(source.CanonicalRoot, canonicalRoot));

    private static IReadOnlyList<RoutingInputSourceRecord> Replace(
        IReadOnlyList<RoutingInputSourceRecord> sources,
        RoutingInputSourceRecord replacement) => sources.Select(source =>
            source.SourceId.Equals(replacement.SourceId, StringComparison.Ordinal)
                ? replacement
                : source).ToArray();

    private static bool TryNormalizeInput(
        string? displayName,
        string? canonicalRoot,
        string? windowsTimeZoneId,
        out string normalizedName,
        out string normalizedRoot,
        out string normalizedTimeZoneId)
    {
        normalizedName = (displayName ?? string.Empty).Trim();
        normalizedRoot = string.Empty;
        normalizedTimeZoneId = (windowsTimeZoneId ?? string.Empty).Trim();
        try
        {
            RoutingValidation.Require(
                normalizedName.Length is > 0 and <=
                    RoutingInputSourceCatalogModel.MaximumDisplayNameLength &&
                normalizedName.All(character => !char.IsControl(character)),
                "The Routing input-source display name is invalid.");
            normalizedRoot = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(canonicalRoot);
            RoutingInputSourceCatalogModel.ValidateWindowsTimeZoneId(normalizedTimeZoneId);
            SensitiveDataRedactor.RegisterSecret(normalizedRoot);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryValidateSourceId(string? sourceId)
    {
        try
        {
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryNormalizeRootIdentity(
        string? value,
        out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        try
        {
            RoutingValidation.RequireSha256(
                normalized, "Routing input-source root identity");
            RoutingValidation.Require(
                normalized.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "The Routing input-source root identity must use canonical lowercase SHA-256 text.");
            return true;
        }
        catch (InvalidDataException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static DateTimeOffset MutationTimestamp(
        RoutingInputSourceCatalogDocument current,
        DateTimeOffset? requested)
    {
        var timestamp = RoutingValidation.Utc(requested ?? DateTimeOffset.UtcNow);
        return timestamp < current.UpdatedUtc ? current.UpdatedUtc : timestamp;
    }

    private static bool IsPersistenceFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException;

    private static RoutingInputSourceMutationResult InvalidInput() => Result(
        RoutingInputSourceMutationStatus.InvalidInput,
        "Enter a name, an absolute Xbox Game DVR folder, and a valid Windows time zone.",
        null);

    private static RoutingInputSourceMutationResult InvalidWatchedFolderInput() => Result(
        RoutingInputSourceMutationStatus.InvalidInput,
        "Choose SteelSeries GG or NVIDIA, enter a name, and select a verified absolute clips folder.",
        null);

    private static string DescribeKind(RoutingInputSourceKind kind) => kind switch
    {
        RoutingInputSourceKind.XboxGameDvrOneDrive => "Xbox Game DVR",
        RoutingInputSourceKind.SteelSeriesGg => "SteelSeries GG",
        RoutingInputSourceKind.Nvidia => "NVIDIA",
        _ => "another clip source"
    };

    private static RoutingInputSourceMutationResult NotFound() => Result(
        RoutingInputSourceMutationStatus.NotFound,
        "The Routing input source no longer exists.",
        null);

    private static RoutingInputSourceMutationResult InUse() => Result(
        RoutingInputSourceMutationStatus.InUse,
        "Disable or replace every route and resolve durable source work before changing this source identity.",
        null);

    private static RoutingInputSourceMutationResult StateUnavailable() => Result(
        RoutingInputSourceMutationStatus.StateUnavailable,
        "The Routing input-source state or its references are unavailable. No change was made.",
        null);

    private static RoutingInputSourceMutationResult Conflict() => Result(
        RoutingInputSourceMutationStatus.Conflict,
        "The Routing input-source catalog changed. No source was overwritten.",
        null);

    private static RoutingInputSourceMutationResult Result(
        RoutingInputSourceMutationStatus status,
        string reason,
        RoutingInputSourceRecord? source) => new(status, reason, source);

    private sealed class RoutingInputSourceInUseException : InvalidOperationException;
    private sealed class RoutingInputSourceReferenceUnavailableException :
        InvalidOperationException;
}
