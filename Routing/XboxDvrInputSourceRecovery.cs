namespace ClipsToDiscord;

internal interface IXboxDvrSourceIdentityInspector
{
    XboxDvrRuntimeMetadataSnapshot Inspect(
        string canonicalRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Establishes a native root identity using metadata-only filesystem operations. This boundary
/// cannot open a clip or request OneDrive hydration.
/// </summary>
internal sealed class WindowsXboxDvrSourceIdentityInspector :
    IXboxDvrSourceIdentityInspector
{
    private readonly WindowsXboxDvrRuntimeMetadataReader _reader;

    internal WindowsXboxDvrSourceIdentityInspector(
        IXboxDvrWindowsFileOperations? operations = null,
        int maximumCandidates =
            XboxDvrRuntimeHostOptions.MaximumAllowedCandidatesPerSource)
    {
        _reader = new WindowsXboxDvrRuntimeMetadataReader(
            operations ?? new XboxDvrWindowsFileOperations(),
            maximumCandidates);
    }

    public XboxDvrRuntimeMetadataSnapshot Inspect(
        string canonicalRoot,
        CancellationToken cancellationToken = default) =>
        _reader.InspectRoot(canonicalRoot, cancellationToken);
}

internal enum XboxDvrSourceRegistrationStatus
{
    Registered,
    AlreadyExists,
    Offline,
    Invalid,
    CapacityExceeded,
    ReplacementRequired,
    StateUnavailable,
    Conflict
}

internal sealed record XboxDvrSourceRegistrationResult(
    XboxDvrSourceRegistrationStatus Status,
    string Reason,
    RoutingInputSourceRecord? Source)
{
    internal bool Succeeded => Status is
        XboxDvrSourceRegistrationStatus.Registered or
        XboxDvrSourceRegistrationStatus.AlreadyExists;
}

internal enum XboxDvrSourceRecoveryStatus
{
    Restored,
    Relocated,
    AlreadyReady,
    Offline,
    StillNeedsAttention,
    ReplacementRequired,
    StateUnavailable,
    Conflict,
    NotFound
}

internal sealed record XboxDvrSourceRecoveryResult(
    XboxDvrSourceRecoveryStatus Status,
    string Reason,
    RoutingInputSourceRecord? Source)
{
    internal bool Succeeded => Status is
        XboxDvrSourceRecoveryStatus.Restored or
        XboxDvrSourceRecoveryStatus.Relocated or
        XboxDvrSourceRecoveryStatus.AlreadyReady;
}

internal enum XboxDvrBlockedOccurrenceResolutionStatus
{
    Resolved,
    NoEligibleBlockedClips,
    UnsupportedBlocker,
    StateUnavailable,
    Conflict,
    NotFound
}

internal sealed record XboxDvrBlockedOccurrenceResolutionResult(
    XboxDvrBlockedOccurrenceResolutionStatus Status,
    string Reason,
    int SkippedCount,
    RoutingInputSourceRecord? Source)
{
    internal bool Succeeded => Status is
        XboxDvrBlockedOccurrenceResolutionStatus.Resolved or
        XboxDvrBlockedOccurrenceResolutionStatus.NoEligibleBlockedClips;
}

internal enum XboxDvrBlockedOccurrenceRetryStatus
{
    Retried,
    RecoveryCompleted,
    UnsupportedBlocker,
    StateUnavailable,
    Conflict,
    NotFound
}

internal sealed record XboxDvrBlockedOccurrenceRetryResult(
    XboxDvrBlockedOccurrenceRetryStatus Status,
    string Reason,
    int RetriedCount,
    RoutingInputSourceRecord? Source)
{
    internal bool Succeeded => Status is
        XboxDvrBlockedOccurrenceRetryStatus.Retried or
        XboxDvrBlockedOccurrenceRetryStatus.RecoveryCompleted;
}

internal enum XboxDvrBlockedOccurrenceInspectionStatus
{
    Available,
    RecoveryPending,
    RetryPending,
    Unsupported,
    StateUnavailable,
    Conflict,
    NotFound
}

internal sealed record XboxDvrBlockedOccurrenceInspectionResult(
    XboxDvrBlockedOccurrenceInspectionStatus Status,
    string Reason,
    int EligibleCount);

/// <summary>
/// Registers and rechecks Xbox roots without reading clip content. A SourceId is bound to one
/// native root identity forever. Replaced folders must receive a new SourceId and explicit route
/// history authorization; recovery can never repoint the old identity.
/// </summary>
internal sealed class XboxDvrInputSourceRecoveryService
{
    private readonly RoutingInputSourceCatalog _catalog;
    private readonly IXboxDvrSourceIdentityInspector _inspector;
    private readonly string _routingRoot;
    private readonly string? _libraryRoot;
    private readonly IXboxDvrCaptureLibraryPromoter? _promoter;
    private readonly Func<DateTimeOffset> _clock;

    internal XboxDvrInputSourceRecoveryService(
        RoutingInputSourceCatalog catalog,
        string routingRoot,
        IXboxDvrSourceIdentityInspector? inspector = null,
        Func<DateTimeOffset>? clock = null,
        string? libraryRoot = null,
        IXboxDvrCaptureLibraryPromoter? promoter = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _inspector = inspector ?? new WindowsXboxDvrSourceIdentityInspector();
        ArgumentException.ThrowIfNullOrWhiteSpace(routingRoot);
        _routingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(routingRoot));
        _libraryRoot = string.IsNullOrWhiteSpace(libraryRoot)
            ? null
            : CaptureJournalStore.NormalizeLibraryRoot(libraryRoot);
        _promoter = promoter ?? (_libraryRoot is null
            ? null
            : new XboxDvrCaptureLibraryPromoter(_libraryRoot));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<XboxDvrSourceRegistrationResult> RegisterAsync(
        string displayName,
        string canonicalRoot,
        string windowsTimeZoneId,
        bool enabled = false,
        CancellationToken cancellationToken = default)
    {
        var name = (displayName ?? string.Empty).Trim();
        string root;
        try
        {
            RoutingValidation.RequireText(
                name, 1,
                RoutingInputSourceCatalogModel.MaximumDisplayNameLength,
                "Routing input-source display name");
            root = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(canonicalRoot);
            RoutingInputSourceCatalogModel.ValidateWindowsTimeZoneId(windowsTimeZoneId);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return Registration(
                XboxDvrSourceRegistrationStatus.Invalid,
                "Enter a valid Xbox Game DVR folder and Windows time zone.");
        }

        XboxDvrRuntimeMetadataSnapshot metadata;
        try
        {
            metadata = _inspector.Inspect(root, cancellationToken);
            RequireRootIdentity(metadata.RootIdentitySha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (XboxDvrMetadataCapacityExceededException)
        {
            return Registration(
                XboxDvrSourceRegistrationStatus.CapacityExceeded,
                "The Xbox folder contains too many direct entries to verify safely.");
        }
        catch (InvalidDataException)
        {
            return Registration(
                XboxDvrSourceRegistrationStatus.Invalid,
                "The Xbox folder is redirected, replaced, or invalid.");
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return Registration(
                XboxDvrSourceRegistrationStatus.Offline,
                "The Xbox folder is unavailable. Confirm OneDrive is running and try again.");
        }

        var mutation = await _catalog.AddVerifiedAsync(
                name,
                root,
                metadata.RootIdentitySha256,
                windowsTimeZoneId,
                enabled,
                RoutingValidation.Utc(_clock()),
                cancellationToken)
            .ConfigureAwait(false);
        return mutation.Status switch
        {
            RoutingInputSourceMutationStatus.Added => Registration(
                XboxDvrSourceRegistrationStatus.Registered,
                "The Xbox Game DVR source was registered.", mutation.Source),
            RoutingInputSourceMutationStatus.AlreadyExists => Registration(
                XboxDvrSourceRegistrationStatus.AlreadyExists,
                "This Xbox Game DVR source is already registered.", mutation.Source),
            RoutingInputSourceMutationStatus.ReplacementRequired => Registration(
                XboxDvrSourceRegistrationStatus.ReplacementRequired,
                "The folder path now identifies a different source. Add it as a replacement instead of reusing the old source.",
                mutation.Source),
            RoutingInputSourceMutationStatus.Conflict => Registration(
                XboxDvrSourceRegistrationStatus.Conflict,
                mutation.Reason, mutation.Source),
            RoutingInputSourceMutationStatus.StateUnavailable => Registration(
                XboxDvrSourceRegistrationStatus.StateUnavailable,
                mutation.Reason, mutation.Source),
            _ => Registration(
                XboxDvrSourceRegistrationStatus.Invalid,
                mutation.Reason, mutation.Source)
        };
    }

    internal async Task<XboxDvrSourceRecoveryResult> RecheckAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision <= 0)
            return Recovery(XboxDvrSourceRecoveryStatus.Conflict,
                "The Xbox source changed. Reload it and try again.");

        RoutingInputSourceCatalogSnapshot catalog;
        try
        {
            catalog = _catalog.Inspect(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return Recovery(XboxDvrSourceRecoveryStatus.StateUnavailable,
                "The Routing input-source catalog is unavailable.");
        }
        if (!catalog.IsUsable)
            return Recovery(XboxDvrSourceRecoveryStatus.StateUnavailable,
                "The Routing input-source catalog is unavailable.");

        RoutingInputSourceRecord? source;
        try
        {
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            source = catalog.Sources.SingleOrDefault(item =>
                item.SourceId.Equals(sourceId, StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return Recovery(XboxDvrSourceRecoveryStatus.NotFound,
                "The Xbox source no longer exists.");
        }
        if (source is null)
            return Recovery(XboxDvrSourceRecoveryStatus.NotFound,
                "The Xbox source no longer exists.");
        if (source.Revision != expectedRevision)
            return Recovery(XboxDvrSourceRecoveryStatus.Conflict,
                "The Xbox source changed. Reload it and try again.", source);
        if (source.Health == RoutingInputSourceHealth.Ready)
            return Recovery(XboxDvrSourceRecoveryStatus.AlreadyReady,
                "The Xbox source is already ready.", source);

        XboxDvrRuntimeMetadataSnapshot metadata;
        try
        {
            metadata = _inspector.Inspect(source.CanonicalRoot, cancellationToken);
            RequireRootIdentity(metadata.RootIdentitySha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (XboxDvrMetadataCapacityExceededException)
        {
            return Recovery(
                XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "The Xbox folder still exceeds its bounded metadata capacity.", source);
        }
        catch (InvalidDataException)
        {
            return Recovery(
                XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "The Xbox folder is redirected or invalid.", source);
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return Recovery(
                XboxDvrSourceRecoveryStatus.Offline,
                "The Xbox folder is unavailable. Confirm OneDrive is running and try again.",
                source);
        }

        if (!metadata.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            using var gate = await _catalog.EnterExecutionGateAsync(cancellationToken)
                .ConfigureAwait(false);
            var currentSnapshot = _catalog.Inspect(cancellationToken);
            var gatedSource = currentSnapshot.IsUsable
                ? currentSnapshot.Sources.SingleOrDefault(item =>
                    item.SourceId.Equals(source.SourceId, StringComparison.Ordinal))
                : null;
            if (gatedSource is null || gatedSource.Revision != expectedRevision ||
                gatedSource.Retired ||
                !gatedSource.RootIdentitySha256.Equals(
                    source.RootIdentitySha256, StringComparison.Ordinal) ||
                !gatedSource.CanonicalRoot.Equals(
                    source.CanonicalRoot, StringComparison.Ordinal))
            {
                return Recovery(
                    XboxDvrSourceRecoveryStatus.Conflict,
                    "The Xbox source changed. Reload it and try again.",
                    gatedSource);
            }
            source = gatedSource;
            if (!await ReconcileImportingWithoutSourceAsync(
                    source, cancellationToken).ConfigureAwait(false))
            {
                return Recovery(
                    XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                    "Finish the durable Capture Library recovery before replacing this Xbox source.",
                    source);
            }
            var marked = await _catalog.SetRootAuthorityChangedWithinExecutionGateAsync(
                    source.SourceId,
                    expectedRevision,
                    RoutingValidation.Utc(_clock()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (marked.Status == RoutingInputSourceMutationStatus.Conflict)
                return Recovery(XboxDvrSourceRecoveryStatus.Conflict,
                    marked.Reason, marked.Source);
            if (!marked.Succeeded)
                return Recovery(XboxDvrSourceRecoveryStatus.StateUnavailable,
                    marked.Reason, marked.Source);
            return Recovery(
                XboxDvrSourceRecoveryStatus.ReplacementRequired,
                "The configured folder has a different filesystem identity. Register it as a new source; the old source was not repointed.",
                marked.Source);
        }

        var journalStore = new XboxDvrOccurrenceStore(_routingRoot, source.SourceId);
        var journal = journalStore.Load(cancellationToken);
        if (journal.LoadedFromDisk && journal.Document is not null)
        {
            if (!journal.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
                !journal.Document.RootIdentitySha256.Equals(
                    source.RootIdentitySha256, StringComparison.Ordinal))
            {
                return Recovery(
                    XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                    "The Xbox source journal belongs to different source authority.", source);
            }
        }
        else if (journal.Status != RoutingDocumentLoadStatus.Missing)
        {
            return Recovery(
                XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "The Xbox source journal cannot be validated safely.", source);
        }

        if (source.AttentionReason == RoutingInputSourceAttentionReason.BlockedOccurrence)
        {
            return Recovery(
                XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "This source has blocked clips that require an explicit skip or repair decision.",
                source);
        }
        if (source.AttentionReason == RoutingInputSourceAttentionReason.MetadataInvalid &&
            journal.Document?.Occurrences.Any(item =>
                item.State == XboxDvrOccurrenceState.NeedsAttention) == true)
        {
            return Recovery(
                XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "The Xbox source journal still contains unresolved evidence that a metadata-only recheck cannot clear.",
                source);
        }
        IReadOnlyList<XboxDvrOccurrenceEntry>? revisionsToRestore = null;
        if (source.AttentionReason == RoutingInputSourceAttentionReason.SourceRevisionChanged)
        {
            revisionsToRestore = journal.LoadedFromDisk && journal.Document is not null
                ? FindRestorableSourceRevisions(
                    source, journal.Document, metadata.Candidates, cancellationToken)
                : null;
            if (revisionsToRestore is null)
            {
                return Recovery(
                    XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                    "A previously selected Xbox clip still has different source revision evidence.",
                    source);
            }
        }

        var restored = await _catalog.TryRestoreReadyAsync(
                source.SourceId,
                expectedRevision,
                source.RootIdentitySha256,
                RoutingValidation.Utc(_clock()),
                cancellationToken)
            .ConfigureAwait(false);
        if ((restored.Status is RoutingInputSourceMutationStatus.Updated or
             RoutingInputSourceMutationStatus.Unchanged) &&
            revisionsToRestore is { Count: > 0 })
        {
            try
            {
                var occurrenceJournal = new XboxDvrOccurrenceJournal(
                    journalStore, source.SourceId, source.RootIdentitySha256, _clock);
                foreach (var entry in revisionsToRestore)
                {
                    _ = await occurrenceJournal.RestoreSourceRevisionAsync(
                            entry.OccurrenceId, entry.RevisionId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or InvalidOperationException)
            {
                if (restored.Source is { } ready)
                {
                    _ = await _catalog.SetNeedsAttentionAsync(
                            ready.SourceId,
                            ready.Revision,
                            RoutingInputSourceAttentionReason.SourceRevisionChanged,
                            RoutingValidation.Utc(_clock()),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                return Recovery(
                    XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                    "The source revision was verified, but its durable journal repair did not complete.",
                    restored.Source);
            }
        }
        return restored.Status switch
        {
            RoutingInputSourceMutationStatus.Updated => Recovery(
                XboxDvrSourceRecoveryStatus.Restored,
                "The Xbox source is ready.", restored.Source),
            RoutingInputSourceMutationStatus.Unchanged => Recovery(
                XboxDvrSourceRecoveryStatus.AlreadyReady,
                "The Xbox source is already ready.", restored.Source),
            RoutingInputSourceMutationStatus.Conflict => Recovery(
                XboxDvrSourceRecoveryStatus.Conflict,
                restored.Reason, restored.Source),
            RoutingInputSourceMutationStatus.NotFound => Recovery(
                XboxDvrSourceRecoveryStatus.NotFound,
                restored.Reason, restored.Source),
            _ => Recovery(
                XboxDvrSourceRecoveryStatus.StateUnavailable,
                restored.Reason, restored.Source)
        };
    }

    /// <summary>
    /// Explicitly dismisses only known per-clip media/library blockers. It never opens, moves,
    /// deletes, or changes the OneDrive source. Skipped entries remain terminal audit evidence;
    /// source readiness is restored only after every durable blocker is resolved.
    /// </summary>
    internal async Task<XboxDvrBlockedOccurrenceResolutionResult>
        SkipBlockedOccurrencesAsync(
            string sourceId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
    {
        if (expectedRevision <= 0)
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.Conflict,
                "The Xbox source changed. Reload it and try again.");

        RoutingInputSourceCatalogSnapshot snapshot;
        try
        {
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            snapshot = _catalog.Inspect(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsOffline(exception) ||
                                           exception is InvalidDataException)
        {
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.StateUnavailable,
                "The Xbox source catalog is unavailable.");
        }
        if (!snapshot.IsUsable)
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.StateUnavailable,
                "The Xbox source catalog is unavailable.");
        var source = snapshot.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(sourceId, StringComparison.Ordinal));
        if (source is null)
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.NotFound,
                "The Xbox source no longer exists.");
        if (source.Revision != expectedRevision)
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.Conflict,
                "The Xbox source changed. Reload it and try again.", source: source);
        if (source.Retired ||
            source.AttentionReason != RoutingInputSourceAttentionReason.BlockedOccurrence)
        {
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.UnsupportedBlocker,
                "This source needs a different repair action; no clips were skipped.",
                source: source);
        }

        var store = new XboxDvrOccurrenceStore(_routingRoot, source.SourceId);
        var loaded = store.Load(cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is null ||
            !loaded.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
            !loaded.Document.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.StateUnavailable,
                "The Xbox blocked-clip journal cannot be verified safely.",
                source: source);
        }
        var blocked = loaded.Document.Occurrences.Where(item =>
                item.State == XboxDvrOccurrenceState.NeedsAttention)
            .ToArray();
        if (blocked.Any(item => item.ErrorCode is not (
                "invalid-media" or "library-conflict")))
        {
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.UnsupportedBlocker,
                "At least one blocked clip requires a different repair action; nothing was skipped.",
                source: source);
        }
        if (blocked.Length == 0 && !loaded.Document.Occurrences.Any(item =>
                item.State == XboxDvrOccurrenceState.Skipped &&
                item.ErrorCode is "skipped-invalid-media" or
                    "skipped-library-conflict"))
        {
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.UnsupportedBlocker,
                "No durably skippable clip is recorded for this source; use the source recheck instead.",
                source: source);
        }

        var skipped = 0;
        try
        {
            var journal = new XboxDvrOccurrenceJournal(
                store, source.SourceId, source.RootIdentitySha256, _clock);
            foreach (var entry in blocked)
            {
                _ = await journal.SkipBlockedAsync(
                        entry.OccurrenceId, entry.RevisionId, cancellationToken)
                    .ConfigureAwait(false);
                skipped++;
            }
            var after = journal.Load(cancellationToken);
            if (!after.LoadedFromDisk || after.Document is null ||
                after.Document.Occurrences.Any(item =>
                    item.State == XboxDvrOccurrenceState.NeedsAttention))
            {
                return BlockedResolution(
                    XboxDvrBlockedOccurrenceResolutionStatus.StateUnavailable,
                    "Blocked-clip resolution did not reach a verified durable state.",
                    skipped, source);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.StateUnavailable,
                "Blocked-clip resolution did not complete; retry is safe.",
                skipped, source);
        }

        var restored = await _catalog.TryRestoreReadyAsync(
                source.SourceId,
                expectedRevision,
                source.RootIdentitySha256,
                RoutingValidation.Utc(_clock()),
                cancellationToken)
            .ConfigureAwait(false);
        return restored.Status switch
        {
            RoutingInputSourceMutationStatus.Updated or
                RoutingInputSourceMutationStatus.Unchanged => BlockedResolution(
                    skipped == 0
                        ? XboxDvrBlockedOccurrenceResolutionStatus.NoEligibleBlockedClips
                        : XboxDvrBlockedOccurrenceResolutionStatus.Resolved,
                    skipped == 0
                        ? "No blocked clips remained; the Xbox source is ready."
                        : "The blocked clips were skipped. Their OneDrive originals were untouched, and future clips can resume.",
                    skipped,
                    restored.Source),
            RoutingInputSourceMutationStatus.Conflict => BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.Conflict,
                "The blocked clips are durably skipped, but the source changed. Reload and recheck it.",
                skipped,
                restored.Source),
            RoutingInputSourceMutationStatus.NotFound => BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.NotFound,
                "The Xbox source no longer exists.", skipped),
            _ => BlockedResolution(
                XboxDvrBlockedOccurrenceResolutionStatus.StateUnavailable,
                "The blocked clips are durably skipped, but source readiness could not be restored.",
                skipped,
                restored.Source)
        };
    }

    /// <summary>
    /// Explicitly retries only known per-clip media/library blockers. Every qualifying occurrence
    /// is returned to Importing before the source becomes Ready, so the runtime must recover a
    /// committed journal or promotion intent before it may discard an uncommitted owned stage and
    /// copy the exact frozen source revision again. This method never opens or changes source
    /// content, and a catalog-write failure leaves retry work durably paused and convergent.
    /// </summary>
    internal async Task<XboxDvrBlockedOccurrenceRetryResult> RetryBlockedOccurrencesAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision <= 0)
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.Conflict,
                "The Xbox source changed. Reload it and try again.");

        RoutingInputSourceCatalogSnapshot snapshot;
        try
        {
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            snapshot = _catalog.Inspect(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsOffline(exception) ||
                                           exception is InvalidDataException)
        {
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.StateUnavailable,
                "The Xbox source catalog is unavailable.");
        }
        if (!snapshot.IsUsable)
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.StateUnavailable,
                "The Xbox source catalog is unavailable.");
        var source = snapshot.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(sourceId, StringComparison.Ordinal));
        if (source is null)
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.NotFound,
                "The Xbox source no longer exists.");
        if (source.Revision != expectedRevision)
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.Conflict,
                "The Xbox source changed. Reload it and try again.", source: source);
        if (source.Retired ||
            source.AttentionReason != RoutingInputSourceAttentionReason.BlockedOccurrence)
        {
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.UnsupportedBlocker,
                "This source needs a different repair action; no clips were retried.",
                source: source);
        }

        var store = new XboxDvrOccurrenceStore(_routingRoot, source.SourceId);
        var loaded = store.Load(cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is null ||
            !loaded.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
            !loaded.Document.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.StateUnavailable,
                "The Xbox blocked-clip journal cannot be verified safely.",
                source: source);
        }
        var blocked = loaded.Document.Occurrences.Where(item =>
                item.State == XboxDvrOccurrenceState.NeedsAttention)
            .ToArray();
        if (blocked.Any(item => item.ErrorCode is not (
                "invalid-media" or "library-conflict")))
        {
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.UnsupportedBlocker,
                "At least one blocked clip requires a different repair action; nothing was retried.",
                source: source);
        }
        var recoveryPending = loaded.Document.Occurrences.Any(item =>
            item.State == XboxDvrOccurrenceState.Importing);
        if (blocked.Length == 0 && !recoveryPending)
        {
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.UnsupportedBlocker,
                "No durably retryable clip is recorded for this source.",
                source: source);
        }

        var retried = 0;
        try
        {
            var journal = new XboxDvrOccurrenceJournal(
                store, source.SourceId, source.RootIdentitySha256, _clock);
            foreach (var entry in blocked)
            {
                _ = await journal.RetryBlockedAsync(
                        entry.OccurrenceId, entry.RevisionId, cancellationToken)
                    .ConfigureAwait(false);
                retried++;
            }
            var after = journal.Load(cancellationToken);
            if (!after.LoadedFromDisk || after.Document is null ||
                !after.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
                !after.Document.RootIdentitySha256.Equals(
                    source.RootIdentitySha256, StringComparison.Ordinal) ||
                after.Document.Occurrences.Any(item =>
                    item.State == XboxDvrOccurrenceState.NeedsAttention) ||
                !after.Document.Occurrences.Any(item =>
                    item.State == XboxDvrOccurrenceState.Importing))
            {
                return BlockedRetry(
                    XboxDvrBlockedOccurrenceRetryStatus.StateUnavailable,
                    "Blocked-clip retry did not reach a verified durable state.",
                    retried,
                    source);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.StateUnavailable,
                "Blocked-clip retry did not complete; retrying this action is safe.",
                retried,
                source);
        }

        var restored = await _catalog.TryRestoreReadyAsync(
                source.SourceId,
                expectedRevision,
                source.RootIdentitySha256,
                RoutingValidation.Utc(_clock()),
                cancellationToken)
            .ConfigureAwait(false);
        return restored.Status switch
        {
            RoutingInputSourceMutationStatus.Updated or
                RoutingInputSourceMutationStatus.Unchanged => BlockedRetry(
                    retried == 0
                        ? XboxDvrBlockedOccurrenceRetryStatus.RecoveryCompleted
                        : XboxDvrBlockedOccurrenceRetryStatus.Retried,
                    retried == 0
                        ? "The blocked-clip retry was already durable. The Xbox source is ready to resume."
                        : "The blocked clips are ready for safe recovery. ClipCord will resume them before later clips.",
                    retried,
                    restored.Source),
            RoutingInputSourceMutationStatus.Conflict => BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.Conflict,
                "The clip retries are durable, but the source changed. Reload it and finish recovery.",
                retried,
                restored.Source),
            RoutingInputSourceMutationStatus.NotFound => BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.NotFound,
                "The Xbox source no longer exists.",
                retried),
            _ => BlockedRetry(
                XboxDvrBlockedOccurrenceRetryStatus.StateUnavailable,
                "The clip retries are durable, but source readiness could not be restored. Retrying this action is safe.",
                retried,
                restored.Source)
        };
    }

    internal XboxDvrBlockedOccurrenceInspectionResult InspectBlockedOccurrences(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision <= 0)
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.Conflict,
                "The Xbox source changed. Reload it and try again.");
        RoutingInputSourceCatalogSnapshot snapshot;
        try
        {
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            snapshot = _catalog.Inspect(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsOffline(exception) ||
                                           exception is InvalidDataException)
        {
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.StateUnavailable,
                "Blocked-clip state is unavailable.");
        }
        if (!snapshot.IsUsable)
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.StateUnavailable,
                "Blocked-clip state is unavailable.");
        var source = snapshot.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(sourceId, StringComparison.Ordinal));
        if (source is null)
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.NotFound,
                "The Xbox source no longer exists.");
        if (source.Revision != expectedRevision)
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.Conflict,
                "The Xbox source changed. Reload it and try again.");
        if (source.Retired ||
            source.AttentionReason != RoutingInputSourceAttentionReason.BlockedOccurrence)
        {
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.Unsupported,
                "This source needs a different repair action.");
        }
        var loaded = new XboxDvrOccurrenceStore(_routingRoot, source.SourceId)
            .Load(cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is null ||
            !loaded.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
            !loaded.Document.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.StateUnavailable,
                "Blocked-clip state cannot be verified safely.");
        }
        var blockers = loaded.Document.Occurrences.Where(item =>
                item.State == XboxDvrOccurrenceState.NeedsAttention)
            .ToArray();
        if (blockers.Any(item => item.ErrorCode is not (
                "invalid-media" or "library-conflict")))
        {
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.Unsupported,
                "At least one clip needs a different repair action.");
        }
        if (blockers.Length > 0)
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.Available,
                "Blocked clips can be retried or skipped without changing their OneDrive originals.",
                blockers.Length);
        if (loaded.Document.Occurrences.Any(item =>
                item.State == XboxDvrOccurrenceState.Importing))
        {
            return BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.RetryPending,
                "The retry is durable; source readiness can be finished safely.");
        }
        return loaded.Document.Occurrences.Any(item =>
                item.State == XboxDvrOccurrenceState.Skipped &&
                item.ErrorCode is "skipped-invalid-media" or
                    "skipped-library-conflict")
            ? BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.RecoveryPending,
                "The skip is durable; source readiness can be finished safely.")
            : BlockedInspection(
                XboxDvrBlockedOccurrenceInspectionStatus.Unsupported,
                "No durably skippable clip is recorded for this source.");
    }

    internal Task<XboxDvrSourceRegistrationResult> RegisterReplacementAsync(
        string sourceId,
        long expectedRevision,
        bool enabled = false,
        CancellationToken cancellationToken = default) =>
        RegisterReplacementCoreAsync(
            sourceId, expectedRevision, candidateCanonicalRoot: null,
            enabled, cancellationToken);

    internal Task<XboxDvrSourceRegistrationResult> RegisterReplacementAsync(
        string sourceId,
        long expectedRevision,
        string candidateCanonicalRoot,
        bool enabled = false,
        CancellationToken cancellationToken = default) =>
        RegisterReplacementCoreAsync(
            sourceId, expectedRevision, candidateCanonicalRoot,
            enabled, cancellationToken);

    private async Task<XboxDvrSourceRegistrationResult> RegisterReplacementCoreAsync(
        string sourceId,
        long expectedRevision,
        string? candidateCanonicalRoot,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var snapshot = _catalog.Inspect(cancellationToken);
        if (!snapshot.IsUsable)
            return Registration(XboxDvrSourceRegistrationStatus.StateUnavailable,
                "The Routing input-source catalog is unavailable.");
        var source = snapshot.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(sourceId, StringComparison.Ordinal));
        if (source is null)
            return Registration(XboxDvrSourceRegistrationStatus.Invalid,
                "The Xbox source no longer exists.");
        if (source.Revision != expectedRevision)
            return Registration(XboxDvrSourceRegistrationStatus.Conflict,
                "The Xbox source changed. Reload it and try again.", source);
        if (source.Retired)
            return Registration(XboxDvrSourceRegistrationStatus.AlreadyExists,
                "The replaced Xbox source is already retired.", source);

        string candidateRoot;
        try
        {
            candidateRoot = candidateCanonicalRoot is null
                ? source.CanonicalRoot
                : RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(
                    candidateCanonicalRoot);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return Registration(XboxDvrSourceRegistrationStatus.Invalid,
                "Choose a valid replacement Xbox Game DVR folder.", source);
        }

        XboxDvrRuntimeMetadataSnapshot metadata;
        try
        {
            metadata = _inspector.Inspect(candidateRoot, cancellationToken);
            RequireRootIdentity(metadata.RootIdentitySha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (XboxDvrMetadataCapacityExceededException)
        {
            return Registration(XboxDvrSourceRegistrationStatus.CapacityExceeded,
                "The replacement folder contains too many direct entries to verify safely.",
                source);
        }
        catch (InvalidDataException)
        {
            return Registration(XboxDvrSourceRegistrationStatus.Invalid,
                "The replacement folder is redirected or invalid.", source);
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return Registration(XboxDvrSourceRegistrationStatus.Offline,
                "The replacement folder is unavailable.", source);
        }

        if (metadata.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return Registration(
                XboxDvrSourceRegistrationStatus.AlreadyExists,
                "The folder still has the original source identity. Recheck it instead of registering a replacement.",
                source);
        }

        if (source.Health != RoutingInputSourceHealth.NeedsAttention ||
            source.AttentionReason != RoutingInputSourceAttentionReason.RootAuthorityChanged)
        {
            var marked = await _catalog.SetNeedsAttentionAsync(
                    source.SourceId,
                    expectedRevision,
                    RoutingInputSourceAttentionReason.RootAuthorityChanged,
                    RoutingValidation.Utc(_clock()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (marked.Status == RoutingInputSourceMutationStatus.Conflict)
                return Registration(XboxDvrSourceRegistrationStatus.Conflict,
                    marked.Reason, marked.Source);
            if (!marked.Succeeded || marked.Source is null)
                return Registration(XboxDvrSourceRegistrationStatus.StateUnavailable,
                    marked.Reason, marked.Source);
            source = marked.Source;
        }

        if (!await ReconcileImportingWithoutSourceAsync(
                source, cancellationToken).ConfigureAwait(false))
        {
            return Registration(
                XboxDvrSourceRegistrationStatus.StateUnavailable,
                "Finish the durable Capture Library recovery before replacing this Xbox source.",
                source);
        }

        var replacement = await _catalog.RegisterReplacementAsync(
                source.SourceId,
                source.Revision,
                candidateRoot,
                metadata.RootIdentitySha256,
                enabled,
                RoutingValidation.Utc(_clock()),
                cancellationToken)
            .ConfigureAwait(false);
        return replacement.Status switch
        {
            RoutingInputSourceMutationStatus.Added => Registration(
                XboxDvrSourceRegistrationStatus.Registered,
                "The replacement Xbox source was registered. Existing routes remain bound to the retired source and must be recreated explicitly.",
                replacement.Source),
            RoutingInputSourceMutationStatus.AlreadyExists => Registration(
                XboxDvrSourceRegistrationStatus.AlreadyExists,
                replacement.Reason, replacement.Source),
            RoutingInputSourceMutationStatus.Conflict => Registration(
                XboxDvrSourceRegistrationStatus.Conflict,
                replacement.Reason, replacement.Source),
            RoutingInputSourceMutationStatus.StateUnavailable => Registration(
                XboxDvrSourceRegistrationStatus.StateUnavailable,
                replacement.Reason, replacement.Source),
            _ => Registration(
                XboxDvrSourceRegistrationStatus.ReplacementRequired,
                replacement.Reason, replacement.Source)
        };
    }

    internal async Task<XboxDvrSourceRecoveryResult> RelocateAsync(
        string sourceId,
        long expectedRevision,
        string newCanonicalRoot,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _catalog.Inspect(cancellationToken);
        if (!snapshot.IsUsable)
            return Recovery(XboxDvrSourceRecoveryStatus.StateUnavailable,
                "The Routing input-source catalog is unavailable.");
        var source = snapshot.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(sourceId, StringComparison.Ordinal));
        if (source is null)
            return Recovery(XboxDvrSourceRecoveryStatus.NotFound,
                "The Xbox source no longer exists.");
        if (source.Revision != expectedRevision)
            return Recovery(XboxDvrSourceRecoveryStatus.Conflict,
                "The Xbox source changed. Reload it and try again.", source);
        if (source.Retired)
            return Recovery(XboxDvrSourceRecoveryStatus.ReplacementRequired,
                "A retired source cannot be relocated.", source);

        string root;
        try
        {
            root = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(newCanonicalRoot);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return Recovery(XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "Choose a valid Xbox Game DVR folder.", source);
        }

        XboxDvrRuntimeMetadataSnapshot metadata;
        try
        {
            metadata = _inspector.Inspect(root, cancellationToken);
            RequireRootIdentity(metadata.RootIdentitySha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (XboxDvrMetadataCapacityExceededException)
        {
            return Recovery(XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "The selected folder exceeds its bounded metadata capacity.", source);
        }
        catch (InvalidDataException)
        {
            return Recovery(XboxDvrSourceRecoveryStatus.StillNeedsAttention,
                "The selected folder is redirected or invalid.", source);
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return Recovery(XboxDvrSourceRecoveryStatus.Offline,
                "The selected folder is unavailable.", source);
        }

        if (!metadata.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return Recovery(
                XboxDvrSourceRecoveryStatus.ReplacementRequired,
                "The selected folder is a different native source. It cannot inherit existing routes or history.",
                source);
        }

        var relocated = await _catalog.RelocateVerifiedAsync(
                source.SourceId,
                expectedRevision,
                root,
                metadata.RootIdentitySha256,
                RoutingValidation.Utc(_clock()),
                cancellationToken)
            .ConfigureAwait(false);
        return relocated.Status switch
        {
            RoutingInputSourceMutationStatus.Updated => Recovery(
                XboxDvrSourceRecoveryStatus.Relocated,
                relocated.Reason, relocated.Source),
            RoutingInputSourceMutationStatus.Unchanged => Recovery(
                XboxDvrSourceRecoveryStatus.AlreadyReady,
                relocated.Reason, relocated.Source),
            RoutingInputSourceMutationStatus.ReplacementRequired => Recovery(
                XboxDvrSourceRecoveryStatus.ReplacementRequired,
                relocated.Reason, relocated.Source),
            RoutingInputSourceMutationStatus.Conflict => Recovery(
                XboxDvrSourceRecoveryStatus.Conflict,
                relocated.Reason, relocated.Source),
            RoutingInputSourceMutationStatus.NotFound => Recovery(
                XboxDvrSourceRecoveryStatus.NotFound,
                relocated.Reason, relocated.Source),
            _ => Recovery(
                XboxDvrSourceRecoveryStatus.StateUnavailable,
                relocated.Reason, relocated.Source)
        };
    }

    private static void RequireRootIdentity(string value)
    {
        RoutingValidation.RequireSha256(value, "Xbox root identity");
        RoutingValidation.Require(
            value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "The Xbox root identity must use canonical lowercase SHA-256 text.");
    }

    private static IReadOnlyList<XboxDvrOccurrenceEntry>? FindRestorableSourceRevisions(
        RoutingInputSourceRecord source,
        XboxDvrOccurrenceDocument document,
        IReadOnlyList<XboxDvrCandidateMetadata> candidates,
        CancellationToken cancellationToken)
    {
        var replacements = document.Occurrences.Where(item =>
                item.State == XboxDvrOccurrenceState.NeedsAttention &&
                item.ErrorCode == "source-replaced")
            .ToArray();
        if (document.Occurrences.Any(item =>
                item.State == XboxDvrOccurrenceState.NeedsAttention &&
                item.ErrorCode != "source-replaced"))
        {
            return null;
        }

        // Recovery deliberately makes the catalog Ready before repairing the occurrence
        // journal. If the final journal write commits but its response is lost, the catalog
        // can be marked SourceRevisionChanged again with no source-replaced entries left.
        // Revalidate every nonterminal source-backed entry so that state converges safely.
        var evidenceToValidate = document.Occurrences.Where(item =>
                item.State is XboxDvrOccurrenceState.Selected or
                    XboxDvrOccurrenceState.Importing ||
                item.State == XboxDvrOccurrenceState.NeedsAttention &&
                item.ErrorCode == "source-replaced")
            .ToArray();

        var current = new Dictionary<string, (
            XboxDvrCandidateMetadata Metadata,
            XboxDvrParsedFileName Parsed,
            string RevisionId)>(StringComparer.Ordinal);
        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = XboxDvrFileNameParser.Parse(candidate.PortableRelativePath);
                if (!parsed.IsParsed || parsed.CapturedUtc is null || parsed.GameName is null)
                    continue;
                var occurrenceId = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
                    source.SourceId, candidate.PortableRelativePath, candidate);
                var revisionId = XboxDvrSourceAdapter.CreateRevisionIdentity(
                    occurrenceId, parsed.CapturedUtc.Value, candidate);
                if (!current.TryAdd(occurrenceId, (candidate, parsed, revisionId)))
                    return null;
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        foreach (var entry in evidenceToValidate)
        {
            if (!current.TryGetValue(entry.OccurrenceId, out var candidate) ||
                !candidate.RevisionId.Equals(entry.RevisionId, StringComparison.Ordinal) ||
                !candidate.Metadata.PortableRelativePath.Equals(
                    entry.PortableRelativePath, StringComparison.Ordinal) ||
                !candidate.Parsed.GameName!.Equals(entry.GameName, StringComparison.Ordinal) ||
                candidate.Parsed.CapturedUtc!.Value != entry.CapturedUtc ||
                candidate.Metadata.LogicalBytes != entry.LogicalBytes ||
                candidate.Metadata.LastWriteUtcTicks != entry.LastWriteUtcTicks)
            {
                return null;
            }
        }
        return replacements;
    }

    private async Task<bool> ReconcileImportingWithoutSourceAsync(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken)
    {
        var store = new XboxDvrOccurrenceStore(_routingRoot, source.SourceId);
        var loaded = store.Load(cancellationToken);
        if (loaded.Status == RoutingDocumentLoadStatus.Missing) return true;
        if (!loaded.LoadedFromDisk || loaded.Document is null ||
            !loaded.Document.SourceId.Equals(source.SourceId, StringComparison.Ordinal) ||
            !loaded.Document.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal))
        {
            return false;
        }
        var importing = loaded.Document.Occurrences.Where(entry =>
                entry.State == XboxDvrOccurrenceState.Importing)
            .OrderBy(entry => entry.CreatedUtc)
            .ThenBy(entry => entry.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        if (importing.Length == 0) return true;
        if (_libraryRoot is null || _promoter is null) return false;

        var journal = new XboxDvrOccurrenceJournal(
            store, source.SourceId, source.RootIdentitySha256, _clock);
        try
        {
            foreach (var entry in importing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = XboxDvrRuntimeLayout.GetStagedPath(
                    _libraryRoot, source.SourceId, entry.OccurrenceId);
                var identity = new XboxDvrLibraryPromotionIdentity(
                    source.SourceId,
                    entry.OccurrenceId,
                    entry.RevisionId,
                    entry.GameName,
                    entry.CapturedUtc,
                    stagedPath,
                    XboxDvrRuntimeLayout.GetDestinationPath(
                        _libraryRoot,
                        entry.GameName,
                        entry.CapturedUtc,
                        entry.OccurrenceId));
                var committed = await _promoter.FindCommittedAsync(
                        source.SourceId,
                        entry.OccurrenceId,
                        entry.RevisionId,
                        cancellationToken)
                    .ConfigureAwait(false) ??
                    await _promoter.TryResumeAsync(identity, cancellationToken)
                        .ConfigureAwait(false);
                if (committed is not null)
                {
                    if (committed.Clip.SourceKind != CaptureJournalSourceKind.XboxGameDvr ||
                        !committed.Clip.SourceConnectionId!.Equals(
                            source.SourceId, StringComparison.Ordinal) ||
                        !committed.Clip.SourceOccurrenceId!.Equals(
                            entry.OccurrenceId, StringComparison.Ordinal) ||
                        !committed.Clip.SourceRevisionId!.Equals(
                            entry.RevisionId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    _ = await journal.CompleteAsync(
                            entry.OccurrenceId,
                            entry.RevisionId,
                            committed.Clip.ClipId,
                            committed.Clip.Original.Fingerprint.Sha256,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (Directory.Exists(stagedPath)) return false;
                if (File.Exists(stagedPath))
                {
                    CaptureJournalStore.EnsureOrdinaryExistingPath(
                        _libraryRoot,
                        stagedPath,
                        requireDirectory: false,
                        "Xbox owned staging file");
                    File.Delete(stagedPath);
                }
                _ = await journal.ResetInterruptedAsync(
                        entry.OccurrenceId, entry.RevisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsOffline(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidOperationException;

    private static XboxDvrSourceRegistrationResult Registration(
        XboxDvrSourceRegistrationStatus status,
        string reason,
        RoutingInputSourceRecord? source = null) => new(status, reason, source);

    private static XboxDvrSourceRecoveryResult Recovery(
        XboxDvrSourceRecoveryStatus status,
        string reason,
        RoutingInputSourceRecord? source = null) => new(status, reason, source);

    private static XboxDvrBlockedOccurrenceResolutionResult BlockedResolution(
        XboxDvrBlockedOccurrenceResolutionStatus status,
        string reason,
        int skippedCount = 0,
        RoutingInputSourceRecord? source = null) =>
        new(status, reason, skippedCount, source);

    private static XboxDvrBlockedOccurrenceRetryResult BlockedRetry(
        XboxDvrBlockedOccurrenceRetryStatus status,
        string reason,
        int retriedCount = 0,
        RoutingInputSourceRecord? source = null) =>
        new(status, reason, retriedCount, source);

    private static XboxDvrBlockedOccurrenceInspectionResult BlockedInspection(
        XboxDvrBlockedOccurrenceInspectionStatus status,
        string reason,
        int eligibleCount = 0) => new(status, reason, eligibleCount);
}
