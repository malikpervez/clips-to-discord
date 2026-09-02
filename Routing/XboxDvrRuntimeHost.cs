using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal sealed record XboxDvrRuntimeHostOptions(
    TimeSpan PollInterval,
    TimeSpan ErrorRetryInterval,
    TimeSpan MaximumErrorRetryInterval,
    int MaximumConsecutiveLoopFailures,
    int MaximumCandidatesPerSource)
{
    internal const int MaximumAllowedCandidatesPerSource = 10_000;

    internal static XboxDvrRuntimeHostOptions Default { get; } = new(
        PollInterval: TimeSpan.FromSeconds(10),
        ErrorRetryInterval: TimeSpan.FromSeconds(2),
        MaximumErrorRetryInterval: TimeSpan.FromMinutes(1),
        MaximumConsecutiveLoopFailures: 10,
        MaximumCandidatesPerSource: MaximumAllowedCandidatesPerSource);

    internal void Validate()
    {
        if (PollInterval < TimeSpan.Zero || PollInterval > TimeSpan.FromMinutes(5) ||
            ErrorRetryInterval < TimeSpan.Zero || ErrorRetryInterval > TimeSpan.FromMinutes(5) ||
            MaximumErrorRetryInterval < ErrorRetryInterval ||
            MaximumErrorRetryInterval > TimeSpan.FromMinutes(15) ||
            MaximumConsecutiveLoopFailures is < 1 or > 100 ||
            MaximumCandidatesPerSource is < 1 or > MaximumAllowedCandidatesPerSource)
        {
            throw new ArgumentOutOfRangeException(
                nameof(XboxDvrRuntimeHostOptions),
                "The Xbox DVR runtime polling bounds are invalid.");
        }
    }
}

internal sealed record XboxDvrRuntimeMetadataSnapshot(
    string RootIdentitySha256,
    IReadOnlyList<XboxDvrCandidateMetadata> Candidates);

internal interface IXboxDvrRuntimeMetadataReader
{
    XboxDvrRuntimeMetadataSnapshot Read(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads only native directory metadata. The content-opening API is intentionally absent, so the
/// admission pass cannot hydrate a OneDrive placeholder.
/// </summary>
internal sealed class WindowsXboxDvrRuntimeMetadataReader : IXboxDvrRuntimeMetadataReader
{
    private readonly IXboxDvrWindowsFileOperations _operations;
    private readonly WindowsXboxDvrMetadataFileSystem _metadata;
    private readonly int _maximumCandidates;

    internal WindowsXboxDvrRuntimeMetadataReader(
        IXboxDvrWindowsFileOperations operations,
        int maximumCandidates = XboxDvrRuntimeHostOptions.MaximumAllowedCandidatesPerSource)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _metadata = new WindowsXboxDvrMetadataFileSystem(operations);
        _maximumCandidates = WindowsXboxDvrMetadataFileSystem.RequireMetadataEntryLimit(
            maximumCandidates);
    }

    public XboxDvrRuntimeMetadataSnapshot Read(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        RoutingInputSourceCatalogModel.ValidateSourceId(source.SourceId);
        if (source.Kind != RoutingInputSourceKind.XboxGameDvrOneDrive)
        {
            throw new InvalidDataException("The Xbox runtime received another source kind.");
        }
        return InspectRoot(source.CanonicalRoot, cancellationToken);
    }

    internal XboxDvrRuntimeMetadataSnapshot InspectRoot(
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        var root = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(canonicalRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var before = _operations.InspectRootNoData(root, cancellationToken);
        ValidateRoot(before);
        var candidates = _metadata.EnumerateMetadata(
            root, _maximumCandidates, cancellationToken);
        var after = _operations.InspectRootNoData(root, cancellationToken);
        ValidateRoot(after);
        if (before != after)
        {
            throw new IOException(
                "The Xbox DVR root changed while its metadata snapshot was collected.");
        }
        return new XboxDvrRuntimeMetadataSnapshot(
            CreateRootIdentity(before),
            candidates.ToArray());
    }

    private static void ValidateRoot(XboxDvrWindowsRootSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var isDirectory = (snapshot.FileAttributes &
                           XboxDvrCloudReparsePolicy.FileAttributeDirectory) != 0;
        var isReparse = (snapshot.FileAttributes &
                         XboxDvrCloudReparsePolicy.FileAttributeReparsePoint) != 0;
        var canonicalId = snapshot.FileId128Hex is { Length: 32 } &&
                          snapshot.FileId128Hex.All(character =>
                              character is >= '0' and <= '9' or >= 'a' and <= 'f');
        var safeReparse = !isReparse
            ? snapshot.ReparseTag == 0
            : (snapshot.ReparseTag & XboxDvrCloudReparsePolicy.ReparseTagNameSurrogate) == 0 &&
              (snapshot.ReparseTag & XboxDvrCloudReparsePolicy.CloudTagFamilyMask) ==
              XboxDvrCloudReparsePolicy.CloudTagFamily;
        if (!isDirectory || !canonicalId || !safeReparse)
        {
            throw new InvalidDataException(
                "The Xbox DVR source root is redirected, replaced, or invalid.");
        }
    }

    private static string CreateRootIdentity(XboxDvrWindowsRootSnapshot snapshot)
    {
        var material = string.Join('\n',
        [
            "clipcord-xbox-dvr-root-v1",
            snapshot.VolumeSerialNumber.ToString("x16", CultureInfo.InvariantCulture),
            snapshot.FileId128Hex
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }
}

internal sealed record XboxDvrLibraryPromotionIdentity(
    string SourceId,
    string OccurrenceId,
    string RevisionId,
    string GameName,
    DateTimeOffset CapturedUtc,
    string StagedPath,
    string DestinationPath,
    RoutingLocalOnlyAdmissionSnapshot? LocalOnlyOverride = null);

internal sealed record XboxDvrLibraryPromotionRequest(
    XboxDvrLibraryPromotionIdentity Identity,
    RoutingWatchedFolderMediaInfo Media);

internal interface IXboxDvrCaptureLibraryPromoter
{
    Task<CaptureJournalDocument?> FindCommittedAsync(
        string sourceId,
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken);

    Task<CaptureJournalDocument?> TryResumeAsync(
        XboxDvrLibraryPromotionIdentity identity,
        CancellationToken cancellationToken);

    Task<CaptureJournalDocument> PromoteAsync(
        XboxDvrLibraryPromotionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapts Xbox-owned staging to the Capture Library's existing write-ahead promotion protocol.
/// The promotion intent is always durable before the stage is moved, and provenance recovery is
/// independent of the destination filename so a restart cannot create a second Library clip.
/// </summary>
internal sealed class XboxDvrCaptureLibraryPromoter : IXboxDvrCaptureLibraryPromoter
{
    private const int JournalPageSize = 256;
    private readonly string _libraryRoot;
    private readonly Func<DateTimeOffset> _clock;

    internal XboxDvrCaptureLibraryPromoter(
        string libraryRoot,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<CaptureJournalDocument?> FindCommittedAsync(
        string sourceId,
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken) => Task.Run(
        () => FindCommittedCore(sourceId, occurrenceId, revisionId, cancellationToken),
        cancellationToken);

    public async Task<CaptureJournalDocument?> TryResumeAsync(
        XboxDvrLibraryPromotionIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var clipId = CaptureProjectStore.CreateProjectId(
            _libraryRoot, identity.DestinationPath);
        var inspection = await CaptureJournalPromotionIntentStore.InspectOriginalAsync(
                _libraryRoot, clipId, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.Status == CaptureJournalPromotionStatus.Missing ||
            inspection.Status == CaptureJournalPromotionStatus.SourceMissing)
        {
            return null;
        }
        RequireMatchingIntent(identity, inspection.Intent);
        if (inspection.Status == CaptureJournalPromotionStatus.MoveRequired)
        {
            inspection = await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
                    _libraryRoot, clipId, cancellationToken)
                .ConfigureAwait(false);
        }
        CaptureJournalDocument document;
        if (inspection.Status == CaptureJournalPromotionStatus.AlreadyJournaled &&
            inspection.Document is not null)
        {
            document = inspection.Document;
        }
        else if (inspection.Status == CaptureJournalPromotionStatus.DestinationReady)
        {
            document = await CaptureJournalPromotionIntentStore.CommitOriginalAsync(
                    _libraryRoot, clipId, cancellationToken, RoutingValidation.Utc(_clock()))
                .ConfigureAwait(false);
        }
        else
        {
            throw inspection.Status == CaptureJournalPromotionStatus.Unavailable
                ? new IOException("The Xbox Capture Library promotion is temporarily unavailable.")
                : new InvalidDataException(
                    $"The Xbox Capture Library promotion is {inspection.Status}.");
        }
        RequireMatchingDocument(identity, document);
        try
        {
            await CaptureJournalPromotionIntentStore.CompleteOriginalAsync(
                    _libraryRoot, clipId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The Capture Journal is already durable. Its startup reconciler can remove the
            // completed intent later without risking a duplicate import.
            Log.Error("ClipCord imported an Xbox clip but could not clean its promotion intent.",
                exception);
        }
        return document;
    }

    public async Task<CaptureJournalDocument> PromoteAsync(
        XboxDvrLibraryPromotionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        ArgumentNullException.ThrowIfNull(request.Media);
        if (request.Media.Duration <= TimeSpan.Zero ||
            request.Media.Width <= 0 || request.Media.Height <= 0)
        {
            throw new InvalidDataException("The Xbox media probe is incomplete.");
        }
        // Deliberate second idempotency check: callers normally reconcile an interrupted
        // promotion before entering here, but the promotion boundary must remain safe alone.
        var resumed = await TryResumeAsync(request.Identity, cancellationToken)
            .ConfigureAwait(false);
        if (resumed is not null) return resumed;

        var intent = await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
                _libraryRoot,
                request.Identity.StagedPath,
                request.Identity.DestinationPath,
                CaptureJournalSourceKind.XboxGameDvr,
                request.Identity.GameName,
                request.Identity.CapturedUtc,
                request.Media.Duration,
                request.Media.Width,
                request.Media.Height,
                reactionCameraRequested: false,
                requestedRenditions: [],
                cancellationToken,
                RoutingValidation.Utc(_clock()),
                request.Identity.SourceId,
                request.Identity.OccurrenceId,
                request.Identity.RevisionId,
                RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                    request.Identity.LocalOnlyOverride))
            .ConfigureAwait(false);
        if (!intent.SourceConnectionId!.Equals(
                request.Identity.SourceId, StringComparison.Ordinal) ||
            !intent.SourceOccurrenceId!.Equals(
                request.Identity.OccurrenceId, StringComparison.Ordinal) ||
            !intent.SourceRevisionId!.Equals(
                request.Identity.RevisionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Xbox promotion lost its source provenance.");
        }
        return await TryResumeAsync(request.Identity, cancellationToken).ConfigureAwait(false) ??
               throw new InvalidDataException(
                   "The Xbox Capture Library promotion did not become recoverable.");
    }

    private CaptureJournalDocument? FindCommittedCore(
        string sourceId,
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
        RoutingValidation.RequireSha256(occurrenceId, "Xbox occurrence identity");
        RoutingValidation.RequireSha256(revisionId, "Xbox revision identity");
        CaptureJournalDocument? match = null;
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = CaptureJournalStore.ReadClipIdPage(
                _libraryRoot, JournalPageSize, cursor, cancellationToken);
            foreach (var clipId in page.ClipIds)
            {
                var loaded = CaptureJournalStore.Load(_libraryRoot, clipId, cancellationToken);
                if (loaded.Status != CaptureJournalLoadStatus.Loaded || loaded.Document is null)
                {
                    if (loaded.Status == CaptureJournalLoadStatus.Missing) continue;
                    throw new IOException(
                        "An Xbox provenance lookup could not read a Capture Journal entry.");
                }
                var candidate = loaded.Document;
                if (candidate.Clip.SourceKind != CaptureJournalSourceKind.XboxGameDvr ||
                    !sourceId.Equals(
                        candidate.Clip.SourceConnectionId, StringComparison.Ordinal) ||
                    !occurrenceId.Equals(
                        candidate.Clip.SourceOccurrenceId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!revisionId.Equals(
                        candidate.Clip.SourceRevisionId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The Xbox occurrence is already committed from another source revision.");
                }
                if (match is not null &&
                    !match.Clip.ClipId.Equals(candidate.Clip.ClipId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "One Xbox occurrence is bound to multiple Capture Library clips.");
                }
                match = candidate;
            }
            cursor = page.HasMore ? page.NextCursor : null;
        } while (cursor is not null);
        return match;
    }

    private static void RequireMatchingIntent(
        XboxDvrLibraryPromotionIdentity identity,
        CaptureJournalOriginalPromotionIntent? intent)
    {
        if (intent is null || intent.SourceKind != CaptureJournalSourceKind.XboxGameDvr ||
            !intent.SourceConnectionId!.Equals(identity.SourceId, StringComparison.Ordinal) ||
            !intent.SourceOccurrenceId!.Equals(identity.OccurrenceId, StringComparison.Ordinal) ||
            !intent.SourceRevisionId!.Equals(identity.RevisionId, StringComparison.Ordinal) ||
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                intent.LocalOnlyOverride) !=
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                identity.LocalOnlyOverride) ||
            !intent.GameName.Equals(identity.GameName, StringComparison.Ordinal) ||
            intent.CapturedUtc != identity.CapturedUtc || intent.ReactionCameraRequested ||
            intent.RequestedRenditions.Count != 0)
        {
            throw new InvalidDataException(
                "The durable Xbox promotion belongs to different source evidence.");
        }
    }

    private static void RequireMatchingDocument(
        XboxDvrLibraryPromotionIdentity identity,
        CaptureJournalDocument document)
    {
        if (document.Clip.SourceKind != CaptureJournalSourceKind.XboxGameDvr ||
            !document.Clip.SourceConnectionId!.Equals(identity.SourceId, StringComparison.Ordinal) ||
            !document.Clip.SourceOccurrenceId!.Equals(identity.OccurrenceId, StringComparison.Ordinal) ||
            !document.Clip.SourceRevisionId!.Equals(identity.RevisionId, StringComparison.Ordinal) ||
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                document.Clip.LocalOnlyOverride) !=
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                identity.LocalOnlyOverride) ||
            !document.Clip.GameName.Equals(identity.GameName, StringComparison.Ordinal) ||
            document.Clip.CapturedUtc != identity.CapturedUtc ||
            document.Clip.ReactionCameraRequested ||
            document.Clip.RequestedRenditions.Count != 0)
        {
            throw new InvalidDataException(
                "The committed Xbox clip belongs to different source evidence.");
        }
    }

    private void ValidateIdentity(XboxDvrLibraryPromotionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RoutingInputSourceCatalogModel.ValidateSourceId(identity.SourceId);
        RoutingValidation.RequireSha256(identity.OccurrenceId, "Xbox occurrence identity");
        RoutingValidation.RequireSha256(identity.RevisionId, "Xbox revision identity");
        RoutingValidation.RequireText(identity.GameName, 1, 160, "Xbox game name");
        RoutingValidation.RequireUtc(identity.CapturedUtc, "Xbox captured time");
        _ = RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
            identity.LocalOnlyOverride);
        EnsureInsideLibrary(identity.StagedPath, "Xbox owned stage");
        EnsureInsideLibrary(identity.DestinationPath, "Xbox Library destination");
    }

    private void EnsureInsideLibrary(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CaptureJournalStore.EnsurePathIsInside(
            _libraryRoot, Path.GetFullPath(path), description);
    }
}

internal interface IXboxDvrRuntimeBackend
{
    bool HasCurrentAuthority { get; }

    RoutingLocalOnlyAdmissionSnapshot CaptureLocalOnlyAdmissionSnapshot() =>
        RoutingLocalOnlyAdmissionSnapshot.FailSafeSnapshot;

    RoutingInputSourceCatalogSnapshot InspectSources(CancellationToken cancellationToken);

    RoutingDocumentLoadResult<RoutingSnapshotDocument> LoadRoutes(
        CancellationToken cancellationToken);

    XboxDvrRuntimeMetadataSnapshot ReadMetadata(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken);

    XboxDvrOccurrenceJournal OpenJournal(
        RoutingInputSourceRecord source,
        string rootIdentitySha256);

    Task<XboxDvrContentImportResult> ImportAsync(
        XboxDvrContentImportRequest request,
        CancellationToken cancellationToken);

    Task<XboxDvrContentImportResult> ImportWithinSourceExecutionGateAsync(
        XboxDvrContentImportRequest request,
        CancellationToken cancellationToken) => ImportAsync(request, cancellationToken);

    Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
        string stagedPath,
        CancellationToken cancellationToken);

    Task<CaptureJournalDocument?> FindCommittedAsync(
        string sourceId,
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken);

    Task<CaptureJournalDocument?> TryResumePromotionAsync(
        XboxDvrLibraryPromotionIdentity identity,
        CancellationToken cancellationToken);

    Task<CaptureJournalDocument> PromoteAsync(
        XboxDvrLibraryPromotionRequest request,
        CancellationToken cancellationToken);

    Task MarkSourceNeedsAttentionAsync(
        string sourceId,
        RoutingInputSourceAttentionReason attentionReason,
        CancellationToken cancellationToken);

    ValueTask<IDisposable> EnterSourceExecutionGateAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RoutingInputSourceExecutionGate.NoopLease);
}

internal sealed class XboxDvrProductionRuntimeBackend : IXboxDvrRuntimeBackend
{
    private readonly string _routingRoot;
    private readonly RoutingInputSourceCatalog _sources;
    private readonly RoutingSnapshotStore _routes;
    private readonly IXboxDvrRuntimeMetadataReader _metadata;
    private readonly IXboxDvrContentImporter _importer;
    private readonly IRoutingWatchedFolderMediaProbe _probe;
    private readonly IXboxDvrCaptureLibraryPromoter _promoter;
    private readonly Func<bool> _hasCurrentAuthority;
    private readonly Func<RoutingLocalOnlyAdmissionSnapshot> _captureLocalOnlyOverride;

    internal XboxDvrProductionRuntimeBackend(
        string routingRoot,
        string libraryRoot,
        RoutingInputSourceCatalog sources,
        RoutingSnapshotStore routes,
        Func<bool> hasCurrentAuthority,
        IXboxDvrWindowsFileOperations? windowsOperations = null,
        IRoutingWatchedFolderMediaProbe? mediaProbe = null,
        int maximumCandidatesPerSource =
            XboxDvrRuntimeHostOptions.MaximumAllowedCandidatesPerSource,
        IXboxDvrContentImporter? contentImporter = null,
        Func<RoutingLocalOnlyAdmissionSnapshot>? captureLocalOnlyOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _routingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(routingRoot));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _hasCurrentAuthority = hasCurrentAuthority ??
                               throw new ArgumentNullException(nameof(hasCurrentAuthority));
        var operations = windowsOperations ?? new XboxDvrWindowsFileOperations();
        _metadata = new WindowsXboxDvrRuntimeMetadataReader(
            operations, maximumCandidatesPerSource);
        _importer = contentImporter ?? new WindowsXboxDvrContentImporter(operations);
        _probe = mediaProbe ?? new FfmpegRoutingWatchedFolderMediaProbe();
        _promoter = new XboxDvrCaptureLibraryPromoter(libraryRoot);
        _captureLocalOnlyOverride = captureLocalOnlyOverride ?? (() =>
            new RoutingLocalOnlyOverrideState().CaptureAdmissionSnapshot());
    }

    public bool HasCurrentAuthority => _hasCurrentAuthority();

    public RoutingLocalOnlyAdmissionSnapshot CaptureLocalOnlyAdmissionSnapshot()
    {
        var snapshot = _captureLocalOnlyOverride();
        RoutingLocalOnlyAdmissionSnapshot.Validate(snapshot);
        return snapshot;
    }

    public ValueTask<IDisposable> EnterSourceExecutionGateAsync(
        CancellationToken cancellationToken = default) =>
        RoutingInputSourceExecutionGate.EnterAsync(_sources.StorePath, cancellationToken);

    public RoutingInputSourceCatalogSnapshot InspectSources(
        CancellationToken cancellationToken) => _sources.Inspect(cancellationToken);

    public RoutingDocumentLoadResult<RoutingSnapshotDocument> LoadRoutes(
        CancellationToken cancellationToken) => _routes.Load(cancellationToken);

    public XboxDvrRuntimeMetadataSnapshot ReadMetadata(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken) => _metadata.Read(source, cancellationToken);

    public XboxDvrOccurrenceJournal OpenJournal(
        RoutingInputSourceRecord source,
        string rootIdentitySha256) => new(
        new XboxDvrOccurrenceStore(_routingRoot, source.SourceId),
        source.SourceId,
        rootIdentitySha256);

    public async Task<XboxDvrContentImportResult> ImportAsync(
        XboxDvrContentImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var gate = await RoutingInputSourceExecutionGate.EnterAsync(
                _sources.StorePath, cancellationToken)
            .ConfigureAwait(false);
        return await ImportWithinSourceExecutionGateAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<XboxDvrContentImportResult> ImportWithinSourceExecutionGateAsync(
        XboxDvrContentImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = _sources.Inspect(cancellationToken);
        var routes = _routes.Load(cancellationToken);
        var source = snapshot.IsUsable
            ? snapshot.Sources.SingleOrDefault(item =>
                item.SourceId.Equals(request.SourceId, StringComparison.Ordinal))
            : null;
        if (!routes.LoadedFromDisk || routes.Document is null ||
            routes.Document.Generation != request.ExpectedRoutingGeneration ||
            source is null || source.Revision != request.ExpectedSourceRevision ||
            source.Kind != RoutingInputSourceKind.XboxGameDvrOneDrive ||
            !source.Enabled || source.Retired ||
            source.Health != RoutingInputSourceHealth.Ready ||
            source.AttentionReason != RoutingInputSourceAttentionReason.None ||
            !source.RootIdentitySha256.Equals(
                request.ExpectedRootIdentitySha256, StringComparison.Ordinal) ||
            !source.CanonicalRoot.Equals(
                request.CanonicalRoot, StringComparison.Ordinal))
        {
            throw new XboxDvrRuntimeAdmissionChangedException(
                "The Xbox source authority changed before content access.");
        }
        return await _importer.ImportAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
        string stagedPath,
        CancellationToken cancellationToken) => _probe.ProbeAsync(stagedPath, cancellationToken);

    public Task<CaptureJournalDocument?> FindCommittedAsync(
        string sourceId,
        string occurrenceId,
        string revisionId,
        CancellationToken cancellationToken) =>
        _promoter.FindCommittedAsync(sourceId, occurrenceId, revisionId, cancellationToken);

    public Task<CaptureJournalDocument?> TryResumePromotionAsync(
        XboxDvrLibraryPromotionIdentity identity,
        CancellationToken cancellationToken) =>
        _promoter.TryResumeAsync(identity, cancellationToken);

    public Task<CaptureJournalDocument> PromoteAsync(
        XboxDvrLibraryPromotionRequest request,
        CancellationToken cancellationToken) =>
        _promoter.PromoteAsync(request, cancellationToken);

    public async Task MarkSourceNeedsAttentionAsync(
        string sourceId,
        RoutingInputSourceAttentionReason attentionReason,
        CancellationToken cancellationToken)
    {
        var result = await _sources.SetNeedsAttentionAsync(
                sourceId, attentionReason, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new IOException(
                "The Xbox source could not be placed into its durable needs-attention state.");
        }
    }
}

internal static class XboxDvrRuntimeLayout
{
    private const string XboxStagingFolderName = "Xbox";
    private const int MaximumPartialInspectionCount = 256;
    private const int MaximumPartialDeleteCount = 32;
    private static readonly TimeSpan StalePartialAge = TimeSpan.FromHours(24);

    internal static string GetSourceStagingRoot(string libraryRoot, string sourceId)
    {
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
        var sourceKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sourceId)))
            .ToLowerInvariant();
        return Path.Combine(
            CaptureLibraryLayout.GetStagingDirectory(libraryRoot),
            XboxStagingFolderName,
            sourceKey);
    }

    internal static string GetStagedPath(
        string libraryRoot,
        string sourceId,
        string occurrenceId)
    {
        RoutingValidation.RequireSha256(occurrenceId, "Xbox occurrence identity");
        return Path.Combine(GetSourceStagingRoot(libraryRoot, sourceId), occurrenceId + ".mp4");
    }

    internal static string GetDestinationPath(
        string libraryRoot,
        string gameName,
        DateTimeOffset capturedUtc,
        string occurrenceId)
    {
        RoutingValidation.RequireSha256(occurrenceId, "Xbox occurrence identity");
        RoutingValidation.RequireUtc(capturedUtc, "Xbox captured time");
        var directory = CaptureLibraryLayout.GetRecordingDirectory(libraryRoot, gameName);
        var leaf =
            $"Xbox__{capturedUtc:yyyy-MM-dd}__{capturedUtc:HH-mm-ss}Z__{occurrenceId[..16]}.mp4";
        return Path.Combine(directory, leaf);
    }

    internal static void EnsureOwnedDirectories(
        string libraryRoot,
        string sourceId,
        string gameName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        var baseStaging = CaptureLibraryLayout.GetStagingDirectory(root);
        var staging = GetSourceStagingRoot(root, sourceId);
        var destination = CaptureLibraryLayout.GetRecordingDirectory(root, gameName);
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root, root, requireDirectory: true, "ClipCord library root");
        EnsureDirectoryChain(root, baseStaging, "Xbox staging root");
        EnsureDirectoryChain(root, staging, "Xbox source staging root");
        EnsureDirectoryChain(root, destination, "Xbox Library game folder");
    }

    internal static int CleanupStaleOwnedPartials(
        string libraryRoot,
        string sourceId,
        DateTimeOffset observedUtc)
    {
        RoutingValidation.RequireUtc(observedUtc, "Xbox partial-cleanup time");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        var staging = GetSourceStagingRoot(root, sourceId);
        if (!Directory.Exists(staging)) return 0;
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            root, staging, requireDirectory: true, "Xbox source staging root");
        var cutoff = observedUtc.UtcDateTime - StalePartialAge;
        var candidates = Directory.EnumerateFiles(
                staging, "*.partial", SearchOption.TopDirectoryOnly)
            .Take(MaximumPartialInspectionCount)
            .Select(path => new FileInfo(path))
            .Where(file => IsOwnedPartialLeaf(file.Name) &&
                           file.Exists &&
                           !file.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                           file.LastWriteTimeUtc <= cutoff)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .Take(MaximumPartialDeleteCount)
            .ToArray();
        var removed = 0;
        foreach (var candidate in candidates)
        {
            CaptureJournalStore.EnsureOrdinaryExistingPath(
                root, candidate.FullName, requireDirectory: false,
                "Xbox owned staging partial");
            candidate.Delete();
            removed++;
        }
        return removed;
    }

    private static bool IsOwnedPartialLeaf(string leaf)
    {
        var pieces = leaf.Split('.');
        return pieces is [var occurrence, var token, "partial"] &&
               occurrence.Length == 64 &&
               occurrence.All(character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
               token.Length is >= 1 and <= 64 &&
               token.All(char.IsAsciiLetterOrDigit);
    }

    private static void EnsureDirectoryChain(
        string root,
        string destination,
        string description)
    {
        var normalized = Path.GetFullPath(destination);
        CaptureJournalStore.EnsurePathIsInside(root, normalized, description);
        var relative = Path.GetRelativePath(root, normalized);
        var current = root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            CaptureJournalStore.EnsureOrdinaryExistingPath(
                root, current, requireDirectory: true, description);
            var child = Path.Combine(current, component);
            if (Directory.Exists(child) || File.Exists(child))
            {
                CaptureJournalStore.EnsureOrdinaryExistingPath(
                    root, child, requireDirectory: true, description);
            }
            else
            {
                // The parent was checked immediately before this one-component create. Never use
                // recursive CreateDirectory on an unverified tail: a junction in an existing
                // ancestor must not redirect Xbox staging or Library writes outside the root.
                Directory.CreateDirectory(child);
                CaptureJournalStore.EnsureOrdinaryExistingPath(
                    root, child, requireDirectory: true, description);
            }
            current = child;
        }
    }
}

internal sealed record XboxDvrRuntimeCandidate(
    XboxDvrCandidateMetadata Metadata,
    string GameName,
    DateTimeOffset CapturedUtc,
    string OccurrenceId,
    string RevisionId);

internal static class XboxDvrRuntimeAuthorityValidator
{
    internal static void RequireDisjointOwnedAndWatchedRoots(
        string libraryRoot,
        IReadOnlyList<RoutingInputSourceRecord> sources,
        IReadOnlyList<string> legacyWatchedRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(legacyWatchedRoots);
        var library = NormalizeRoot(libraryRoot);
        var legacy = legacyWatchedRoots.Select(NormalizeRoot).ToArray();
        var xbox = sources
            .Where(source => source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive &&
                             source.Enabled)
            .Select(source =>
            {
                RoutingInputSourceCatalogModel.ValidateSourceId(source.SourceId);
                return (source.SourceId, Root: NormalizeRoot(source.CanonicalRoot));
            })
            .ToArray();

        for (var index = 0; index < xbox.Length; index++)
        {
            var source = xbox[index];
            if (Overlaps(source.Root, library) || legacy.Any(root =>
                    Overlaps(source.Root, root)))
            {
                throw new InvalidDataException(
                    $"Xbox source {source.SourceId} overlaps a ClipCord-owned or legacy watched root.");
            }
            for (var other = index + 1; other < xbox.Length; other++)
            {
                if (Overlaps(source.Root, xbox[other].Root))
                {
                    throw new InvalidDataException(
                        "Enabled Xbox sources cannot contain one another.");
                }
            }
        }
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static bool Overlaps(string left, string right) =>
        IsSameOrAncestor(left, right) || IsSameOrAncestor(right, left);

    private static bool IsSameOrAncestor(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative.Equals(".", StringComparison.Ordinal) ||
               !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar,
                   StringComparison.Ordinal);
    }
}

internal sealed class XboxDvrRuntimeAuthorityException(string message) :
    InvalidOperationException(message);

internal sealed class XboxDvrRuntimeAdmissionChangedException(string message) :
    IOException(message);

/// <summary>
/// Polls named Xbox sources. Every candidate is filtered from metadata and selected durably before
/// the first admitted source is opened. Content import is serialized and enters only ClipCord-owned
/// staging; the original Xbox/OneDrive file is never moved, renamed, written, or deleted.
/// </summary>
internal sealed class XboxDvrRuntimeHost : IAsyncDisposable, IDisposable
{
    private readonly string _libraryRoot;
    private readonly IXboxDvrRuntimeBackend _backend;
    private readonly XboxDvrRuntimeHostOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly Dictionary<string, int> _sourceFailures = new(StringComparer.Ordinal);
    private Task? _loop;
    private Exception? _failure;
    private int _disposed;

    internal XboxDvrRuntimeHost(
        string libraryRoot,
        IXboxDvrRuntimeBackend backend,
        XboxDvrRuntimeHostOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? XboxDvrRuntimeHostOptions.Default;
        _options.Validate();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    internal Exception? Failure => Volatile.Read(ref _failure);
    internal Task Completion => _completion.Task;

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Task loop;
        lock (_lifecycle)
        {
            _loop ??= RunCoreAsync(_shutdown.Token);
            loop = _loop;
        }
        try
        {
            await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _shutdown.Cancel();
            await loop.ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _shutdown.Cancel();
        Task? loop;
        lock (_lifecycle) loop = _loop;
        if (loop is not null) await loop.ConfigureAwait(false);
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAuthority();
            RoutingInputSourceCatalogSnapshot sources;
            try
            {
                sources = _backend.InspectSources(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsOptionalCatalogFailure(exception))
            {
                Log.Error(
                    "Xbox input sources are temporarily unavailable; other Routing inputs remain active.",
                    exception);
                return;
            }
            if (!sources.IsUsable)
            {
                Log.Error(
                    $"Xbox input sources are unavailable ({sources.StoreStatus}); other Routing inputs remain active.");
                return;
            }
            try
            {
                XboxDvrRuntimeAuthorityValidator.RequireDisjointOwnedAndWatchedRoots(
                    _libraryRoot, sources.Sources, []);
            }
            catch (InvalidDataException exception)
            {
                Log.Error(
                    "Xbox input-source authority is invalid; other Routing inputs remain active.",
                    exception);
                return;
            }
            RoutingDocumentLoadResult<RoutingSnapshotDocument> loadedRoutes;
            try
            {
                loadedRoutes = _backend.LoadRoutes(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                Log.Error(
                    "The shared Routing snapshot is temporarily unavailable; Xbox polling will retry.",
                    exception);
                return;
            }
            if (loadedRoutes.Status == RoutingDocumentLoadStatus.Missing) return;
            if (loadedRoutes.Status == RoutingDocumentLoadStatus.Unavailable)
            {
                Log.Error(
                    "The shared Routing snapshot is temporarily unavailable; Xbox polling will retry.");
                return;
            }
            if (!loadedRoutes.LoadedFromDisk || loadedRoutes.Document is null)
            {
                throw new XboxDvrRuntimeAuthorityException(
                    $"The shared Routing snapshot is {loadedRoutes.Status}; Xbox admission stopped fail-closed.");
            }
            var routes = loadedRoutes.Document;
            try
            {
                RoutingSnapshotModel.Validate(routes);
            }
            catch (InvalidDataException exception)
            {
                throw new XboxDvrRuntimeAuthorityException(
                    $"The shared Routing snapshot is invalid: {exception.Message}");
            }
            foreach (var source in sources.Sources
                         .Where(source => source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive &&
                                          source.Enabled &&
                                          source.Health == RoutingInputSourceHealth.Ready)
                         .OrderBy(source => source.SourceId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireAuthority();
                try
                {
                    await ProcessSourceAsync(source, routes, cancellationToken)
                        .ConfigureAwait(false);
                    _sourceFailures.Remove(source.SourceId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (XboxDvrMetadataCapacityExceededException exception)
                {
                    await TryMarkSourceNeedsAttentionAsync(
                            source.SourceId,
                            RoutingInputSourceAttentionReason.MetadataCapacityExceeded,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _sourceFailures.Remove(source.SourceId);
                    Log.Error(
                        $"Xbox source {source.SourceId} exceeds its bounded metadata capacity; other Routing sources remain active.",
                        exception);
                }
                catch (InvalidDataException exception)
                {
                    await TryMarkSourceNeedsAttentionAsync(
                            source.SourceId,
                            RoutingInputSourceAttentionReason.MetadataInvalid,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _sourceFailures.Remove(source.SourceId);
                    Log.Error(
                        $"Xbox source {source.SourceId} needs attention; other Routing sources remain active.",
                        exception);
                }
                catch (Exception exception) when (IsTransient(exception))
                {
                    var failures = _sourceFailures.TryGetValue(source.SourceId, out var prior)
                        ? checked(prior + 1)
                        : 1;
                    _sourceFailures[source.SourceId] = failures;
                    if (failures >= _options.MaximumConsecutiveLoopFailures)
                    {
                        // A OneDrive outage is not source replacement. Keep the optional source
                        // Ready and retry on later polls so an offline startup cannot permanently
                        // disable Xbox imports or terminate legacy/Capture routing.
                        _sourceFailures.Remove(source.SourceId);
                        Log.Error(
                            $"Xbox source {source.SourceId} remains unavailable after its retry budget; later polls will retry while other Routing sources stay active.",
                            exception);
                    }
                    else
                    {
                        Log.Error(
                            $"Xbox source {source.SourceId} is temporarily unavailable; ClipCord will retry without stopping other Routing sources.",
                            exception);
                    }
                }
            }
            RequireAuthority();
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task TryMarkSourceNeedsAttentionAsync(
        string sourceId,
        RoutingInputSourceAttentionReason attentionReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _backend.MarkSourceNeedsAttentionAsync(
                    sourceId, attentionReason, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsOptionalCatalogFailure(exception))
        {
            Log.Error(
                $"Xbox source {sourceId} needs attention, but its optional catalog is temporarily unavailable; ClipCord will retry without stopping other inputs.",
                exception);
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var consecutiveFailures = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                    consecutiveFailures = 0;
                    _started.TrySetResult();
                    await _delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (XboxDvrRuntimeAuthorityException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= _options.MaximumConsecutiveLoopFailures)
                    {
                        throw new InvalidOperationException(
                            "The Xbox DVR runtime exceeded its bounded retry budget.", exception);
                    }
                    var retry = ErrorRetryDelay(consecutiveFailures);
                    Log.Error(
                        $"Xbox DVR runtime pass failed; retrying in {retry.TotalSeconds:0.#} seconds.",
                        exception);
                    await _delay(retry, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _started.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            _started.TrySetException(exception);
            _completion.TrySetException(exception);
            Log.Error("Xbox DVR runtime stopped fail-closed.", exception);
        }
        finally
        {
            if (!_started.Task.IsCompleted)
            {
                _started.TrySetException(new InvalidOperationException(
                    "The Xbox DVR runtime stopped before startup completed."));
            }
            if (Failure is null) _completion.TrySetResult();
        }
    }

    private async Task ProcessSourceAsync(
        RoutingInputSourceRecord source,
        RoutingSnapshotDocument routes,
        CancellationToken cancellationToken)
    {
        var metadata = _backend.ReadMetadata(source, cancellationToken);
        RoutingValidation.RequireSha256(metadata.RootIdentitySha256, "Xbox root identity");
        if (!metadata.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            await _backend.MarkSourceNeedsAttentionAsync(
                    source.SourceId,
                    RoutingInputSourceAttentionReason.RootAuthorityChanged,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (metadata.Candidates is null ||
            metadata.Candidates.Count > _options.MaximumCandidatesPerSource)
        {
            throw new InvalidDataException("The Xbox source exceeded its bounded scan capacity.");
        }
        var journal = _backend.OpenJournal(source, metadata.RootIdentitySha256);
        var prior = journal.Load(cancellationToken);
        if (prior.LoadedFromDisk && prior.Document is not null &&
            (!prior.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
             !prior.Document.RootIdentitySha256.Equals(
                 metadata.RootIdentitySha256, StringComparison.Ordinal)))
        {
            await _backend.MarkSourceNeedsAttentionAsync(
                    source.SourceId,
                    RoutingInputSourceAttentionReason.RootAuthorityChanged,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (prior.Status is not (RoutingDocumentLoadStatus.Missing or
            RoutingDocumentLoadStatus.Loaded))
        {
            await _backend.MarkSourceNeedsAttentionAsync(
                    source.SourceId,
                    RoutingInputSourceAttentionReason.JournalUnavailable,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        var unresolved = prior.Document?.Occurrences.FirstOrDefault(item =>
            item.State == XboxDvrOccurrenceState.NeedsAttention);
        if (unresolved is not null)
        {
            await _backend.MarkSourceNeedsAttentionAsync(
                    source.SourceId,
                    unresolved.ErrorCode switch
                    {
                        "source-replaced" =>
                            RoutingInputSourceAttentionReason.SourceRevisionChanged,
                        "invalid-media" or "library-conflict" =>
                            RoutingInputSourceAttentionReason.BlockedOccurrence,
                        _ => RoutingInputSourceAttentionReason.MetadataInvalid
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _ = XboxDvrRuntimeLayout.CleanupStaleOwnedPartials(
            _libraryRoot, source.SourceId, RoutingValidation.Utc(_clock()));

        var candidates = BuildCandidates(source, metadata.Candidates, cancellationToken);
        var byOccurrence = candidates.ToDictionary(
            candidate => candidate.OccurrenceId, StringComparer.Ordinal);
        if (prior.Document is not null)
        {
            foreach (var pending in prior.Document.Occurrences.Where(item =>
                         item.State != XboxDvrOccurrenceState.Imported &&
                         item.State != XboxDvrOccurrenceState.Skipped &&
                         item.State != XboxDvrOccurrenceState.RouteRevoked &&
                         item.State != XboxDvrOccurrenceState.NeedsAttention))
            {
                if (byOccurrence.TryGetValue(pending.OccurrenceId, out var current) &&
                    !current.RevisionId.Equals(pending.RevisionId, StringComparison.Ordinal))
                {
                    await journal.MarkNeedsAttentionAsync(
                            pending.OccurrenceId,
                            pending.RevisionId,
                            "source-replaced",
                            cancellationToken)
                        .ConfigureAwait(false);
                    await _backend.MarkSourceNeedsAttentionAsync(
                            source.SourceId,
                            RoutingInputSourceAttentionReason.SourceRevisionChanged,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }

        var admitted = candidates.Where(candidate =>
                candidate.CapturedUtc <= RoutingValidation.Utc(_clock()) &&
                XboxDvrRouteAdmission.FindPotentialRoutes(
                    routes,
                    source.SourceId,
                    candidate.GameName,
                    candidate.CapturedUtc,
                    candidate.OccurrenceId,
                    candidate.RevisionId).Count > 0)
            .OrderBy(candidate => candidate.CapturedUtc)
            .ThenBy(candidate => candidate.Metadata.PortableRelativePath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Freeze the complete metadata-only selection before any ImportAsync call can hydrate a
        // source. A crash during this loop therefore leaves either no content work or a durable,
        // exact occurrence/revision admission.
        var selectionFoundRevisionChange = false;
        if (admitted.Length > 0)
        {
            using var selectionGate = await _backend
                .EnterSourceExecutionGateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!RevalidateAdmission(
                    source, admitted[0], routes.Generation, cancellationToken))
            {
                return;
            }
            foreach (var candidate in admitted)
            {
                var selected = await journal.SelectAsync(
                        candidate.OccurrenceId,
                        candidate.RevisionId,
                        candidate.Metadata.PortableRelativePath,
                        candidate.GameName,
                        candidate.CapturedUtc,
                        candidate.Metadata.LogicalBytes,
                        candidate.Metadata.LastWriteUtcTicks,
                        cancellationToken,
                        localOnlyOverride:
                            _backend.CaptureLocalOnlyAdmissionSnapshot())
                    .ConfigureAwait(false);
                if (selected.State == XboxDvrOccurrenceState.NeedsAttention)
                {
                    selectionFoundRevisionChange = true;
                    break;
                }
            }
        }
        if (selectionFoundRevisionChange)
        {
            await _backend.MarkSourceNeedsAttentionAsync(
                    source.SourceId,
                    RoutingInputSourceAttentionReason.SourceRevisionChanged,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var selectedSnapshot = journal.Load(cancellationToken);
        if (!selectedSnapshot.LoadedFromDisk || selectedSnapshot.Document is null)
        {
            if (admitted.Length == 0 && selectedSnapshot.Status == RoutingDocumentLoadStatus.Missing)
                return;
            throw new IOException("The Xbox occurrence selection could not be reloaded.");
        }
        foreach (var entry in selectedSnapshot.Document.Occurrences
                     .Where(item => item.State is XboxDvrOccurrenceState.Selected or
                         XboxDvrOccurrenceState.Importing)
                     .OrderBy(item => item.CapturedUtc)
                     .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireAuthority();
            byOccurrence.TryGetValue(entry.OccurrenceId, out var current);
            if (current is not null &&
                !current.RevisionId.Equals(entry.RevisionId, StringComparison.Ordinal))
            {
                await journal.MarkNeedsAttentionAsync(
                        entry.OccurrenceId,
                        entry.RevisionId,
                        "source-replaced",
                        cancellationToken)
                    .ConfigureAwait(false);
                await _backend.MarkSourceNeedsAttentionAsync(
                        source.SourceId,
                        RoutingInputSourceAttentionReason.SourceRevisionChanged,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (entry.State == XboxDvrOccurrenceState.Selected &&
                (current is null ||
                 XboxDvrRouteAdmission.FindPotentialRoutes(
                     routes,
                     source.SourceId,
                     current.GameName,
                     current.CapturedUtc,
                     current.OccurrenceId,
                     current.RevisionId).Count == 0))
            {
                if (!HasSourceRoute(routes, source.SourceId))
                {
                    _ = await TryDismissRouteRevokedAsync(
                            source, journal, entry, cancellationToken)
                        .ConfigureAwait(false);
                }
                continue;
            }
            var sourceAttention = await ProcessOccurrenceAsync(
                    source, journal, entry, current, routes.Generation, cancellationToken)
                .ConfigureAwait(false);
            if (sourceAttention is { } attentionReason)
            {
                await _backend.MarkSourceNeedsAttentionAsync(
                        source.SourceId,
                        attentionReason,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task<bool> TryDismissRouteRevokedAsync(
        RoutingInputSourceRecord source,
        XboxDvrOccurrenceJournal journal,
        XboxDvrOccurrenceEntry entry,
        CancellationToken cancellationToken)
    {
        using var gate = await _backend.EnterSourceExecutionGateAsync(cancellationToken)
            .ConfigureAwait(false);
        var loadedRoutes = _backend.LoadRoutes(cancellationToken);
        if (!loadedRoutes.LoadedFromDisk || loadedRoutes.Document is null)
            return false;
        RoutingSnapshotModel.Validate(loadedRoutes.Document);
        if (HasSourceRoute(loadedRoutes.Document, source.SourceId)) return false;

        var sources = _backend.InspectSources(cancellationToken);
        if (!sources.IsUsable) return false;
        var currentSource = sources.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(source.SourceId, StringComparison.Ordinal));
        if (!HasSameSourceAuthority(source, currentSource)) return false;

        _ = await journal.DismissRouteRevokedAsync(
                entry.OccurrenceId, entry.RevisionId, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static bool HasSourceRoute(
        RoutingSnapshotDocument routes,
        string sourceId) => routes.Routes.Any(route =>
        route.Conditions.Any(condition =>
            condition.Field == RoutingConditionField.SourceConnection &&
            condition.Operator == RoutingConditionOperator.Equals &&
            condition.Value.Equals(sourceId, StringComparison.Ordinal)));

    private static bool HasSameSourceAuthority(
        RoutingInputSourceRecord expected,
        RoutingInputSourceRecord? current) => current is not null &&
        current.Revision == expected.Revision &&
        current.Kind == expected.Kind &&
        current.Enabled && !current.Retired &&
        current.Health == RoutingInputSourceHealth.Ready &&
        current.AttentionReason == RoutingInputSourceAttentionReason.None &&
        current.RootIdentitySha256.Equals(
            expected.RootIdentitySha256, StringComparison.Ordinal) &&
        current.CanonicalRoot.Equals(expected.CanonicalRoot, StringComparison.Ordinal);

    private async Task<RoutingInputSourceAttentionReason?> ProcessOccurrenceAsync(
        RoutingInputSourceRecord source,
        XboxDvrOccurrenceJournal journal,
        XboxDvrOccurrenceEntry entry,
        XboxDvrRuntimeCandidate? current,
        long admittedRoutingGeneration,
        CancellationToken cancellationToken)
    {
        // One lease owns the complete durable occurrence lifecycle. In particular, source
        // recovery/replacement must not observe Importing between BeginImport and stage
        // publication, or delete/reset a published stage while it is being probed/promoted.
        using var ownedStageLifecycleGate = await _backend
            .EnterSourceExecutionGateAsync(cancellationToken)
            .ConfigureAwait(false);
        XboxDvrRuntimeLayout.EnsureOwnedDirectories(
            _libraryRoot, source.SourceId, entry.GameName);
        var stagedPath = XboxDvrRuntimeLayout.GetStagedPath(
            _libraryRoot, source.SourceId, entry.OccurrenceId);
        var destinationPath = XboxDvrRuntimeLayout.GetDestinationPath(
            _libraryRoot, entry.GameName, entry.CapturedUtc, entry.OccurrenceId);
        var identity = new XboxDvrLibraryPromotionIdentity(
            source.SourceId,
            entry.OccurrenceId,
            entry.RevisionId,
            entry.GameName,
            entry.CapturedUtc,
            stagedPath,
            destinationPath,
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                entry.LocalOnlyOverride));

        var committed = await _backend.FindCommittedAsync(
                source.SourceId, entry.OccurrenceId, entry.RevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (committed is not null)
        {
            entry = await EnsureImportingAsync(journal, entry, cancellationToken)
                .ConfigureAwait(false);
            await CompleteOccurrenceAsync(
                    journal, entry, source.SourceId, committed, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (entry.State == XboxDvrOccurrenceState.Importing)
        {
            try
            {
                var resumed = await _backend.TryResumePromotionAsync(identity, cancellationToken)
                    .ConfigureAwait(false);
                if (resumed is not null)
                {
                    await CompleteOccurrenceAsync(
                            journal, entry, source.SourceId, resumed, cancellationToken)
                        .ConfigureAwait(false);
                    return null;
                }
            }
            catch (InvalidDataException)
            {
                await journal.MarkNeedsAttentionAsync(
                        entry.OccurrenceId,
                        entry.RevisionId,
                        "library-conflict",
                        cancellationToken)
                    .ConfigureAwait(false);
                return RoutingInputSourceAttentionReason.BlockedOccurrence;
            }

            // Without a durable Capture promotion intent there is no persisted content hash for
            // an abandoned stage. Delete only this exact ClipCord-owned occurrence path and copy
            // the already-admitted source revision again; never adopt an unauthenticated orphan.
            if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
            {
                DeleteUncommittedOwnedStage(stagedPath, source.SourceId, entry.OccurrenceId);
            }
            entry = await journal.ResetInterruptedAsync(
                    entry.OccurrenceId, entry.RevisionId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
        {
            DeleteUncommittedOwnedStage(stagedPath, source.SourceId, entry.OccurrenceId);
        }

        // A successful metadata enumeration that no longer contains the frozen occurrence is not
        // an import attempt. Keep it Selected so a temporarily absent OneDrive item can reappear
        // without monotonically increasing attempts or remaining stuck in Importing.
        if (current is null) return null;

        if (!RevalidateAdmission(
                source, current, admittedRoutingGeneration, cancellationToken))
        {
            return null;
        }

        entry = await journal.BeginImportAsync(
                entry.OccurrenceId, entry.RevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (!RevalidateAdmission(
                source, current, admittedRoutingGeneration, cancellationToken))
        {
            _ = await journal.ResetInterruptedAsync(
                    entry.OccurrenceId, entry.RevisionId, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        CaptureJournalFingerprint importedFingerprint;
        try
        {
            var imported = await _backend.ImportWithinSourceExecutionGateAsync(
                    new XboxDvrContentImportRequest(
                        source.SourceId,
                        source.Revision,
                        admittedRoutingGeneration,
                        source.RootIdentitySha256,
                        source.CanonicalRoot,
                        current.Metadata,
                        entry.OccurrenceId,
                        entry.RevisionId,
                        XboxDvrRuntimeLayout.GetSourceStagingRoot(
                            _libraryRoot, source.SourceId)),
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateImportResult(imported, entry, stagedPath);
            importedFingerprint = await ReadOwnedStageFingerprintAsync(
                    stagedPath, cancellationToken)
                .ConfigureAwait(false);
            if (importedFingerprint.ByteLength != entry.LogicalBytes ||
                !importedFingerprint.Sha256.Equals(
                    imported.ContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The Xbox owned stage does not match the importer's verified content evidence.");
            }
        }
        catch (XboxDvrRuntimeAdmissionChangedException)
        {
            _ = await journal.ResetInterruptedAsync(
                    entry.OccurrenceId, entry.RevisionId, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (InvalidDataException)
        {
            await journal.MarkNeedsAttentionAsync(
                    entry.OccurrenceId,
                    entry.RevisionId,
                    "source-replaced",
                    cancellationToken)
                .ConfigureAwait(false);
            return RoutingInputSourceAttentionReason.SourceRevisionChanged;
        }

        RoutingWatchedFolderMediaInfo media;
        try
        {
            media = await _backend.ProbeAsync(stagedPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or FileNotFoundException)
        {
            Log.Error("ClipCord could not read valid media metadata from an imported Xbox clip.",
                exception);
            await journal.MarkNeedsAttentionAsync(
                    entry.OccurrenceId,
                    entry.RevisionId,
                    "invalid-media",
                    cancellationToken)
                .ConfigureAwait(false);
            return RoutingInputSourceAttentionReason.BlockedOccurrence;
        }

        try
        {
            var afterProbe = await ReadOwnedStageFingerprintAsync(
                    stagedPath, cancellationToken)
                .ConfigureAwait(false);
            if (afterProbe != importedFingerprint)
            {
                throw new InvalidDataException(
                    "The Xbox owned stage changed while its media metadata was inspected.");
            }
            var document = await _backend.PromoteAsync(
                    new XboxDvrLibraryPromotionRequest(identity, media),
                    cancellationToken)
                .ConfigureAwait(false);
            await CompleteOccurrenceAsync(
                    journal, entry, source.SourceId, document, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or FileNotFoundException)
        {
            Log.Error(
                exception is InvalidDataException
                    ? "ClipCord could not safely promote an imported Xbox clip into the Library."
                    : "ClipCord lost access to an imported Xbox clip before Library promotion completed.",
                exception);
            await journal.MarkNeedsAttentionAsync(
                    entry.OccurrenceId,
                    entry.RevisionId,
                    exception is InvalidDataException ? "library-conflict" : "invalid-media",
                    cancellationToken)
                .ConfigureAwait(false);
            return RoutingInputSourceAttentionReason.BlockedOccurrence;
        }
    }

    private bool RevalidateAdmission(
        RoutingInputSourceRecord source,
        XboxDvrRuntimeCandidate candidate,
        long admittedRoutingGeneration,
        CancellationToken cancellationToken)
    {
        RoutingDocumentLoadResult<RoutingSnapshotDocument> loaded;
        try
        {
            loaded = _backend.LoadRoutes(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            Log.Error(
                "Xbox admission could not revalidate the shared Routing snapshot before content access.",
                exception);
            return false;
        }
        if (loaded.Status is RoutingDocumentLoadStatus.Missing or
            RoutingDocumentLoadStatus.Unavailable)
        {
            return false;
        }
        if (!loaded.LoadedFromDisk || loaded.Document is null)
        {
            throw new XboxDvrRuntimeAuthorityException(
                $"The shared Routing snapshot became {loaded.Status} before Xbox content access.");
        }
        RoutingSnapshotModel.Validate(loaded.Document);
        if (loaded.Document.Generation != admittedRoutingGeneration) return false;
        if (XboxDvrRouteAdmission.FindPotentialRoutes(
            loaded.Document,
            source.SourceId,
            candidate.GameName,
            candidate.CapturedUtc,
            candidate.OccurrenceId,
            candidate.RevisionId).Count == 0)
        {
            return false;
        }

        RoutingInputSourceCatalogSnapshot sources;
        try
        {
            sources = _backend.InspectSources(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsOptionalCatalogFailure(exception))
        {
            Log.Error(
                "Xbox source authority could not be revalidated before content access.",
                exception);
            return false;
        }
        if (!sources.IsUsable) return false;
        var currentSource = sources.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(source.SourceId, StringComparison.Ordinal));
        return currentSource is not null &&
               currentSource.Revision == source.Revision &&
               currentSource.Kind == source.Kind &&
               currentSource.Enabled && !currentSource.Retired &&
               currentSource.Health == RoutingInputSourceHealth.Ready &&
               currentSource.AttentionReason == RoutingInputSourceAttentionReason.None &&
               currentSource.RootIdentitySha256.Equals(
                   source.RootIdentitySha256, StringComparison.Ordinal) &&
               currentSource.CanonicalRoot.Equals(
                   source.CanonicalRoot, StringComparison.Ordinal);
    }

    private void DeleteUncommittedOwnedStage(
        string stagedPath,
        string sourceId,
        string occurrenceId)
    {
        var expected = XboxDvrRuntimeLayout.GetStagedPath(
            _libraryRoot, sourceId, occurrenceId);
        if (!Path.GetFullPath(stagedPath).Equals(
                Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "An Xbox orphan stage is outside its deterministic owned path.");
        }
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            _libraryRoot, expected, requireDirectory: false, "Xbox owned stage");
        File.Delete(expected);
    }

    private async Task<CaptureJournalFingerprint> ReadOwnedStageFingerprintAsync(
        string stagedPath,
        CancellationToken cancellationToken)
    {
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            _libraryRoot, stagedPath, requireDirectory: false, "Xbox owned stage");
        await using var stream = new FileStream(
            stagedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length <= 0) throw new InvalidDataException("The Xbox owned stage is empty.");
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new CaptureJournalFingerprint(
            length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static async Task<XboxDvrOccurrenceEntry> EnsureImportingAsync(
        XboxDvrOccurrenceJournal journal,
        XboxDvrOccurrenceEntry entry,
        CancellationToken cancellationToken) => entry.State == XboxDvrOccurrenceState.Importing
        ? entry
        : await journal.BeginImportAsync(
                entry.OccurrenceId, entry.RevisionId, cancellationToken)
            .ConfigureAwait(false);

    private static Task<XboxDvrOccurrenceEntry> CompleteOccurrenceAsync(
        XboxDvrOccurrenceJournal journal,
        XboxDvrOccurrenceEntry entry,
        string expectedSourceId,
        CaptureJournalDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Clip.SourceKind != CaptureJournalSourceKind.XboxGameDvr ||
            !document.Clip.SourceConnectionId!.Equals(
                expectedSourceId, StringComparison.Ordinal) ||
            !document.Clip.SourceOccurrenceId!.Equals(
                entry.OccurrenceId, StringComparison.Ordinal) ||
            !document.Clip.SourceRevisionId!.Equals(
                entry.RevisionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The recovered Capture Journal does not belong to this Xbox occurrence.");
        }
        if (RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                document.Clip.LocalOnlyOverride) !=
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                entry.LocalOnlyOverride))
        {
            throw new InvalidDataException(
                "The recovered Capture Journal changed its Xbox Local-only admission.");
        }
        return journal.CompleteAsync(
            entry.OccurrenceId,
            entry.RevisionId,
            document.Clip.ClipId,
            document.Clip.Original.Fingerprint.Sha256,
            cancellationToken);
    }

    private static void ValidateImportResult(
        XboxDvrContentImportResult result,
        XboxDvrOccurrenceEntry entry,
        string expectedStagedPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Path.GetFullPath(result.StagedPath).Equals(
                Path.GetFullPath(expectedStagedPath), StringComparison.OrdinalIgnoreCase) ||
            result.CopiedBytes != entry.LogicalBytes ||
            !result.OccurrenceIdentitySha256.Equals(
                entry.OccurrenceId, StringComparison.Ordinal) ||
            !result.RevisionIdentitySha256.Equals(
                entry.RevisionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Xbox content importer returned different admission evidence.");
        }
        RoutingValidation.RequireSha256(result.ContentSha256, "Xbox imported content hash");
    }

    private static IReadOnlyList<XboxDvrRuntimeCandidate> BuildCandidates(
        RoutingInputSourceRecord source,
        IReadOnlyList<XboxDvrCandidateMetadata> metadata,
        CancellationToken cancellationToken)
    {
        var candidates = new List<XboxDvrRuntimeCandidate>(metadata.Count);
        var occurrences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var parsed = XboxDvrFileNameParser.Parse(item.PortableRelativePath);
                if (!parsed.IsParsed || parsed.CapturedUtc is null ||
                    string.IsNullOrWhiteSpace(parsed.GameName) ||
                    XboxDvrCloudReparsePolicy.Classify(
                        item.FileAttributes, item.ReparseTag) ==
                    XboxDvrReparseClassification.Rejected)
                {
                    continue;
                }
                var occurrence = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
                    source.SourceId, item.PortableRelativePath, item);
                var revision = XboxDvrSourceAdapter.CreateRevisionIdentity(
                    occurrence, parsed.CapturedUtc, item);
                if (!occurrences.Add(occurrence))
                {
                    throw new InvalidDataException(
                        "The Xbox metadata poll contains a duplicate occurrence identity.");
                }
                candidates.Add(new XboxDvrRuntimeCandidate(
                    item,
                    parsed.GameName.Trim(),
                    parsed.CapturedUtc.Value,
                    occurrence,
                    revision));
            }
            catch (InvalidDataException)
            {
                // Unsupported or unsafe metadata is intentionally ineligible and never reaches
                // the content-opening boundary.
            }
        }
        return candidates;
    }

    private void RequireAuthority()
    {
        bool current;
        try
        {
            current = _backend.HasCurrentAuthority;
        }
        catch (Exception exception)
        {
            throw new XboxDvrRuntimeAuthorityException(
                $"Xbox Routing authority cannot be inspected: {exception.Message}");
        }
        if (!current)
        {
            throw new XboxDvrRuntimeAuthorityException(
                "The Xbox DVR runtime lost its borrowed Routing authority.");
        }
    }

    private TimeSpan ErrorRetryDelay(int consecutiveFailures)
    {
        if (_options.ErrorRetryInterval == TimeSpan.Zero) return TimeSpan.Zero;
        var multiplier = Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        var ticks = Math.Min(
            _options.ErrorRetryInterval.Ticks * multiplier,
            _options.MaximumErrorRetryInterval.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static bool IsTransient(Exception exception) => exception is
        IOException or UnauthorizedAccessException or System.Security.SecurityException or
        NotSupportedException or RoutingConcurrencyException;

    private static bool IsOptionalCatalogFailure(Exception exception) => exception is
        IOException or InvalidDataException or UnauthorizedAccessException or
        System.Security.SecurityException or NotSupportedException or
        RoutingConcurrencyException;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAsync().AsTask().GetAwaiter().GetResult();
        _pollGate.Dispose();
        _shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _pollGate.Dispose();
        _shutdown.Dispose();
    }

}
