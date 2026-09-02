using System.Security.Cryptography;

namespace ClipsToDiscord;

internal sealed record RoutingConnectionDisplay(
    string ConnectionId,
    string Name,
    string Detail,
    bool Available);

internal sealed record RoutingInputSourceDisplay(
    string SourceId,
    string Name,
    string Detail,
    RoutingInputSourceKind Kind,
    string CanonicalRoot,
    bool Enabled,
    bool Available,
    long Revision = 1,
    RoutingInputSourceHealth Health = RoutingInputSourceHealth.Ready,
    bool Retired = false,
    RoutingInputSourceAttentionReason AttentionReason =
        RoutingInputSourceAttentionReason.None,
    int SkippableBlockedClipCount = 0,
    bool BlockedClipRecoveryPending = false,
    bool BlockedClipRetryPending = false)
{
    public override string ToString() =>
        $"RoutingInputSourceDisplay {{ {SourceId}, {Name}, {Kind}, root redacted }}";
}

internal sealed record RoutingInputSourceViewSnapshot(
    bool IsUsable,
    IReadOnlyList<RoutingInputSourceDisplay> Sources,
    string StatusDetail);

internal enum RoutingInputSourceViewActionStatus
{
    Completed,
    NeedsAttention,
    ReplacementRequired,
    Conflict,
    Failed
}

internal sealed record RoutingInputSourceViewActionResult(
    RoutingInputSourceViewActionStatus Status,
    string Reason,
    RoutingInputSourceDisplay? Source = null,
    int AffectedCount = 0)
{
    internal bool Succeeded => Status == RoutingInputSourceViewActionStatus.Completed;
}

internal sealed record RoutingInputSourceRegistrationDraft(
    string DisplayName,
    RoutingInputSourceKind Kind,
    string CanonicalRoot);

internal sealed record RoutingMigratedInputSourceDisplay(
    string Name,
    string Detail,
    ClipCaptureSource Kind);

internal sealed record RoutingLocalOnlyModeViewSnapshot(
    bool IsAvailable,
    bool EffectiveEnabled,
    string HotkeyDisplayText,
    bool FirstRunNoticeDismissed,
    string StatusDetail);

internal sealed record RoutingLocalOnlyModeViewActionResult(
    bool Succeeded,
    RoutingLocalOnlyModeViewSnapshot Snapshot,
    string? Error = null,
    bool HotkeyConflict = false);

internal interface IRoutingLocalOnlyModeViewSource
{
    RoutingLocalOnlyModeViewSnapshot Inspect();

    Task<RoutingLocalOnlyModeViewActionResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<RoutingLocalOnlyModeViewActionResult> SetHotkeyAsync(
        string hotkeyDisplayText,
        CancellationToken cancellationToken = default);

    Task<RoutingLocalOnlyModeViewActionResult> DismissFirstRunNoticeAsync(
        CancellationToken cancellationToken = default);
}

internal interface IRoutingInputSourceViewSource
{
    RoutingInputSourceViewSnapshot InspectInputSources();

    Task<RoutingInputSourceViewActionResult> RegisterAsync(
        string displayName,
        RoutingInputSourceKind kind,
        string canonicalRoot,
        bool enabled = false,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceDisplay?> PrepareDefaultXboxSourceAsync(
        CancellationToken cancellationToken = default);

    Task<bool> EnableAsync(
        string sourceId,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceViewActionResult> RecheckAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceViewActionResult> SetEnabledAsync(
        string sourceId,
        long expectedRevision,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceViewActionResult> RemoveAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceViewActionResult> RelocateAsync(
        string sourceId,
        long expectedRevision,
        string newCanonicalRoot,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceViewActionResult> RegisterReplacementAsync(
        string sourceId,
        long expectedRevision,
        string candidateCanonicalRoot,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceViewActionResult> SkipBlockedOccurrencesAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<RoutingInputSourceViewActionResult> RetryBlockedOccurrencesAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    XboxDvrPreflightPreview Preflight(
        RoutingInputSourceDisplay source,
        XboxDvrHistoryPolicy policy,
        CancellationToken cancellationToken = default);
}

internal sealed class RoutingInputSourceCatalogViewSource : IRoutingInputSourceViewSource
{
    private readonly RoutingInputSourceCatalog _catalog;
    private readonly IXboxDvrMetadataFileSystem _metadata;
    private readonly XboxDvrInputSourceRecoveryService _recovery;
    private readonly Func<string?> _discoverDefaultXboxRoot;
    private readonly string? _libraryRoot;
    private readonly string? _legacyWatchedRoot;
    private readonly RoutingNamedWatchedBaselineStore _namedWatchedBaselines;

    internal RoutingInputSourceCatalogViewSource(
        RoutingInputSourceCatalog catalog,
        IXboxDvrMetadataFileSystem metadata,
        XboxDvrInputSourceRecoveryService? recovery = null,
        Func<string?>? discoverDefaultXboxRoot = null,
        string? libraryRoot = null,
        string? legacyWatchedRoot = null,
        RoutingNamedWatchedBaselineStore? namedWatchedBaselines = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _recovery = recovery ?? new XboxDvrInputSourceRecoveryService(
            _catalog,
            Path.Combine(SettingsStore.DataDirectory, "routing"),
            libraryRoot: libraryRoot);
        _discoverDefaultXboxRoot = discoverDefaultXboxRoot ??
            XboxDvrSourceDiscovery.FindDefaultRoot;
        _libraryRoot = string.IsNullOrWhiteSpace(libraryRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        _legacyWatchedRoot = string.IsNullOrWhiteSpace(legacyWatchedRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyWatchedRoot));
        _namedWatchedBaselines = namedWatchedBaselines ??
            new RoutingNamedWatchedBaselineStore();
    }

    public RoutingInputSourceViewSnapshot InspectInputSources()
    {
        var snapshot = _catalog.Inspect();
        if (!snapshot.IsUsable)
        {
            return new RoutingInputSourceViewSnapshot(
                IsUsable: false,
                Sources: [],
                "Saved clip-source status could not be read. No source was removed or changed.");
        }
        return new RoutingInputSourceViewSnapshot(
            IsUsable: true,
            Sources: snapshot.Sources
            .OrderBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToDisplay)
            .ToArray(),
            StatusDetail: string.Empty);
    }

    public async Task<RoutingInputSourceViewActionResult> RegisterAsync(
        string displayName,
        RoutingInputSourceKind kind,
        string canonicalRoot,
        bool enabled = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(canonicalRoot);
            if (!Directory.Exists(root))
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.Failed,
                    "Choose an existing recorder folder.");
            }
            if (_libraryRoot is not null && CapturePathPolicy.PathsOverlap(root, _libraryRoot))
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.Failed,
                    "Choose a recorder folder outside the ClipCord Capture Library.");
            }
            if (_legacyWatchedRoot is not null &&
                CapturePathPolicy.PathsOverlap(root, _legacyWatchedRoot))
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.Failed,
                    "That folder overlaps the migrated ClipCord 1.x source. Keep the migrated source as-is, or choose a separate recorder folder.");
            }
            var catalogSnapshot = _catalog.Inspect(cancellationToken);
            if (!catalogSnapshot.IsUsable)
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.Failed,
                    "Saved clip-source status could not be read. No source was added.");
            }
            if (catalogSnapshot.Sources.Any(source =>
                    !source.Retired &&
                    CapturePathPolicy.PathsOverlap(root, source.CanonicalRoot)))
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.Failed,
                    "That folder overlaps another connected clip source. Choose a separate recorder folder so one clip cannot enter two routes.");
            }

            if (kind == RoutingInputSourceKind.XboxGameDvrOneDrive)
            {
                var xbox = await _recovery.RegisterAsync(
                        displayName,
                        root,
                        TimeZoneInfo.Utc.Id,
                        enabled: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
                return new RoutingInputSourceViewActionResult(
                    xbox.Succeeded
                        ? RoutingInputSourceViewActionStatus.Completed
                        : xbox.Status == XboxDvrSourceRegistrationStatus.ReplacementRequired
                            ? RoutingInputSourceViewActionStatus.ReplacementRequired
                            : xbox.Status == XboxDvrSourceRegistrationStatus.Conflict
                                ? RoutingInputSourceViewActionStatus.Conflict
                                : xbox.Status == XboxDvrSourceRegistrationStatus.Offline
                                    ? RoutingInputSourceViewActionStatus.NeedsAttention
                                    : RoutingInputSourceViewActionStatus.Failed,
                    xbox.Reason,
                    xbox.Source is null ? null : ToDisplay(xbox.Source));
            }
            if (kind is not (RoutingInputSourceKind.SteelSeriesGg or
                    RoutingInputSourceKind.Nvidia))
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.Failed,
                    "Choose a supported recorder type.");
            }

            var captureSource = kind == RoutingInputSourceKind.SteelSeriesGg
                ? ClipCaptureSource.SteelSeriesGg
                : ClipCaptureSource.Nvidia;
            var adapter = RoutingWatchedSourceAdapters.Get(captureSource);
            var rootIdentity = RoutingWatchedSourceRootIdentity.Create(kind, root);
            var result = await _catalog.AddVerifiedWatchedFolderAsync(
                    displayName,
                    kind,
                    root,
                    rootIdentity,
                    enabled: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            if (result.Succeeded && result.Source is { } source)
            {
                await _namedWatchedBaselines.EnsureAsync(
                        source,
                        adapter,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            return ToViewAction(result);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
                UnauthorizedAccessException or NotSupportedException or
                System.Security.SecurityException)
        {
            return new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.Failed,
                "ClipCord could not verify that recorder folder. Choose an ordinary local folder that is available now.");
        }
    }

    public async Task<RoutingInputSourceDisplay?> PrepareDefaultXboxSourceAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = InspectInputSources();
        if (!snapshot.IsUsable)
            throw new InvalidOperationException(
                "ClipCord could not read the saved Xbox source catalog.");
        var xboxSources = snapshot.Sources.Where(source =>
            source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive).ToArray();
        var existing = xboxSources.FirstOrDefault(source => !source.Retired);
        if (existing is not null) return existing;
        var root = _discoverDefaultXboxRoot();
        if (root is null)
        {
            if (xboxSources.Any(source => source.Retired))
                throw new InvalidOperationException(
                    "Only replaced Xbox sources remain and the current Xbox folder could not be verified.");
            return null;
        }
        var result = await _recovery.RegisterAsync(
                "Xbox captures · OneDrive",
                root,
                TimeZoneInfo.Utc.Id,
                enabled: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        if (result.Succeeded && result.Source is { Retired: false } source)
            return ToDisplay(source);
        throw new InvalidOperationException(
            "ClipCord could not register the currently discovered Xbox source. " +
            "A replaced source identity may still be reserved for existing routes.");
    }

    public async Task<bool> EnableAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _catalog.Inspect(cancellationToken);
        var existing = snapshot.IsUsable
            ? snapshot.Sources.SingleOrDefault(source =>
                source.SourceId.Equals(sourceId, StringComparison.Ordinal))
            : null;
        if (existing is null || existing.Retired ||
            existing.Health != RoutingInputSourceHealth.Ready)
            return false;
        if ((existing.Kind is RoutingInputSourceKind.SteelSeriesGg or
                RoutingInputSourceKind.Nvidia) &&
            !HasMatchingNamedBaseline(existing, cancellationToken))
            return false;
        var result = await _catalog.SetEnabledAsync(
                sourceId,
                existing.Revision,
                enabled: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        return result.Succeeded && result.Source is
            { Enabled: true, Health: RoutingInputSourceHealth.Ready };
    }

    public async Task<RoutingInputSourceViewActionResult> RecheckAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var existing = FindSource(sourceId, expectedRevision, cancellationToken);
        if (existing is { Kind: RoutingInputSourceKind.SteelSeriesGg or
                RoutingInputSourceKind.Nvidia })
        {
            try
            {
                var identity = RoutingWatchedSourceRootIdentity.Create(
                    existing.Kind,
                    existing.CanonicalRoot);
                if (!identity.Equals(existing.RootIdentitySha256, StringComparison.Ordinal))
                {
                    return await MarkRootReplacementRequiredAsync(
                            existing,
                            "That path now identifies a different recorder folder. Register it as a replacement so existing routes stay bound safely.",
                            cancellationToken)
                        .ConfigureAwait(true);
                }
                var restored = await _catalog.TryRestoreReadyAsync(
                        sourceId,
                        expectedRevision,
                        identity,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
                return ToViewAction(restored);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or IOException or
                    UnauthorizedAccessException or NotSupportedException or
                    System.Security.SecurityException)
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.NeedsAttention,
                    "The recorder folder is unavailable. Reconnect the original folder at its saved location, then recheck. To use a different path, choose Replace folder and register a separate source.",
                    ToDisplay(existing));
            }
        }
        var result = await _recovery.RecheckAsync(
                sourceId, expectedRevision, cancellationToken)
            .ConfigureAwait(true);
        return new RoutingInputSourceViewActionResult(
            result.Status switch
            {
                XboxDvrSourceRecoveryStatus.Restored or
                    XboxDvrSourceRecoveryStatus.AlreadyReady or
                    XboxDvrSourceRecoveryStatus.Relocated =>
                    RoutingInputSourceViewActionStatus.Completed,
                XboxDvrSourceRecoveryStatus.ReplacementRequired =>
                    RoutingInputSourceViewActionStatus.ReplacementRequired,
                XboxDvrSourceRecoveryStatus.Conflict =>
                    RoutingInputSourceViewActionStatus.Conflict,
                XboxDvrSourceRecoveryStatus.Offline or
                    XboxDvrSourceRecoveryStatus.StillNeedsAttention =>
                    RoutingInputSourceViewActionStatus.NeedsAttention,
                _ => RoutingInputSourceViewActionStatus.Failed
            },
            result.Reason,
            result.Source is null ? null : ToDisplay(result.Source));
    }

    public async Task<RoutingInputSourceViewActionResult> SetEnabledAsync(
        string sourceId,
        long expectedRevision,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (enabled)
        {
            var existing = FindSource(sourceId, expectedRevision, cancellationToken);
            if (existing is { Kind: RoutingInputSourceKind.SteelSeriesGg or
                    RoutingInputSourceKind.Nvidia } &&
                !HasMatchingNamedBaseline(existing, cancellationToken))
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.NeedsAttention,
                    "The safe new-clips baseline is missing or no longer matches this recorder folder. Remove and add the source again before enabling it.",
                    ToDisplay(existing));
            }
        }
        var result = await _catalog.SetEnabledAsync(
                sourceId,
                expectedRevision,
                enabled,
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        return ToViewAction(result);
    }

    public async Task<RoutingInputSourceViewActionResult> RemoveAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var result = await _catalog.RemoveAsync(
                sourceId,
                expectedRevision,
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        return ToViewAction(result);
    }

    public async Task<RoutingInputSourceViewActionResult> RelocateAsync(
        string sourceId,
        long expectedRevision,
        string newCanonicalRoot,
        CancellationToken cancellationToken = default)
    {
        var existing = FindSource(sourceId, expectedRevision, cancellationToken);
        if (existing is { Kind: RoutingInputSourceKind.SteelSeriesGg or
                RoutingInputSourceKind.Nvidia })
        {
            try
            {
                var isolationFailure = ValidateRootIsolation(
                    newCanonicalRoot,
                    exceptSourceId: sourceId,
                    cancellationToken);
                if (isolationFailure is not null)
                {
                    return new RoutingInputSourceViewActionResult(
                        RoutingInputSourceViewActionStatus.Failed,
                        isolationFailure,
                        ToDisplay(existing));
                }
                var identity = RoutingWatchedSourceRootIdentity.Create(
                    existing.Kind,
                    newCanonicalRoot);
                if (!identity.Equals(existing.RootIdentitySha256, StringComparison.Ordinal))
                {
                    return await MarkRootReplacementRequiredAsync(
                            existing,
                            "The chosen path is a separate recorder source. It cannot inherit the old source's routes, from-now baseline, or pending work. Reconnect the original saved folder to restore the old source, or register this path as a replacement.",
                            cancellationToken)
                        .ConfigureAwait(true);
                }
                var relocated = await _catalog.RelocateVerifiedAsync(
                        sourceId,
                        expectedRevision,
                        newCanonicalRoot,
                        identity,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
                return ToViewAction(relocated);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or IOException or
                    UnauthorizedAccessException or NotSupportedException or
                    System.Security.SecurityException)
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.NeedsAttention,
                    "ClipCord could not verify that recorder folder.",
                    ToDisplay(existing));
            }
        }
        var result = await _recovery.RelocateAsync(
                sourceId,
                expectedRevision,
                newCanonicalRoot,
                cancellationToken)
            .ConfigureAwait(true);
        return ToRecoveryViewAction(result);
    }

    public async Task<RoutingInputSourceViewActionResult> RegisterReplacementAsync(
        string sourceId,
        long expectedRevision,
        string candidateCanonicalRoot,
        CancellationToken cancellationToken = default)
    {
        var existing = FindSource(sourceId, expectedRevision, cancellationToken);
        if (existing is { Kind: RoutingInputSourceKind.SteelSeriesGg or
                RoutingInputSourceKind.Nvidia })
        {
            try
            {
                var adapter = AdapterFor(existing.Kind);
                var isolationFailure = ValidateRootIsolation(
                    candidateCanonicalRoot,
                    exceptSourceId: sourceId,
                    cancellationToken);
                if (isolationFailure is not null)
                {
                    return new RoutingInputSourceViewActionResult(
                        RoutingInputSourceViewActionStatus.Failed,
                        isolationFailure,
                        ToDisplay(existing));
                }
                var identity = RoutingWatchedSourceRootIdentity.Create(
                    existing.Kind,
                    candidateCanonicalRoot);
                var replacement = await _catalog.RegisterReplacementAsync(
                        sourceId,
                        expectedRevision,
                        candidateCanonicalRoot,
                        identity,
                        enabled: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
                if (replacement.Succeeded && replacement.Source is { } replacementSource)
                {
                    await _namedWatchedBaselines.EnsureAsync(
                            replacementSource,
                            adapter,
                            cancellationToken)
                        .ConfigureAwait(true);
                }
                return ToViewAction(replacement);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or IOException or
                    UnauthorizedAccessException or NotSupportedException or
                    System.Security.SecurityException)
            {
                return new RoutingInputSourceViewActionResult(
                    RoutingInputSourceViewActionStatus.Failed,
                    "ClipCord could not register that replacement recorder folder.",
                    ToDisplay(existing));
            }
        }
        var result = await _recovery.RegisterReplacementAsync(
                sourceId,
                expectedRevision,
                candidateCanonicalRoot,
                enabled: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        return new RoutingInputSourceViewActionResult(
            result.Succeeded
                ? RoutingInputSourceViewActionStatus.Completed
                : result.Status == XboxDvrSourceRegistrationStatus.ReplacementRequired
                    ? RoutingInputSourceViewActionStatus.ReplacementRequired
                    : result.Status == XboxDvrSourceRegistrationStatus.Conflict
                        ? RoutingInputSourceViewActionStatus.Conflict
                        : RoutingInputSourceViewActionStatus.Failed,
            result.Reason,
            result.Source is null ? null : ToDisplay(result.Source));
    }

    public async Task<RoutingInputSourceViewActionResult> SkipBlockedOccurrencesAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var result = await _recovery.SkipBlockedOccurrencesAsync(
                sourceId,
                expectedRevision,
                cancellationToken)
            .ConfigureAwait(true);
        return new RoutingInputSourceViewActionResult(
            result.Status switch
            {
                XboxDvrBlockedOccurrenceResolutionStatus.Resolved or
                    XboxDvrBlockedOccurrenceResolutionStatus.NoEligibleBlockedClips =>
                    RoutingInputSourceViewActionStatus.Completed,
                XboxDvrBlockedOccurrenceResolutionStatus.Conflict =>
                    RoutingInputSourceViewActionStatus.Conflict,
                XboxDvrBlockedOccurrenceResolutionStatus.UnsupportedBlocker =>
                    RoutingInputSourceViewActionStatus.NeedsAttention,
                _ => RoutingInputSourceViewActionStatus.Failed
            },
            result.Reason,
            result.Source is null ? null : ToDisplay(result.Source),
            result.SkippedCount);
    }

    public async Task<RoutingInputSourceViewActionResult> RetryBlockedOccurrencesAsync(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var result = await _recovery.RetryBlockedOccurrencesAsync(
                sourceId,
                expectedRevision,
                cancellationToken)
            .ConfigureAwait(true);
        return new RoutingInputSourceViewActionResult(
            result.Status switch
            {
                XboxDvrBlockedOccurrenceRetryStatus.Retried or
                    XboxDvrBlockedOccurrenceRetryStatus.RecoveryCompleted =>
                    RoutingInputSourceViewActionStatus.Completed,
                XboxDvrBlockedOccurrenceRetryStatus.Conflict =>
                    RoutingInputSourceViewActionStatus.Conflict,
                XboxDvrBlockedOccurrenceRetryStatus.UnsupportedBlocker =>
                    RoutingInputSourceViewActionStatus.NeedsAttention,
                _ => RoutingInputSourceViewActionStatus.Failed
            },
            result.Reason,
            result.Source is null ? null : ToDisplay(result.Source),
            result.RetriedCount);
    }

    public XboxDvrPreflightPreview Preflight(
        RoutingInputSourceDisplay source,
        XboxDvrHistoryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new XboxDvrSourceAdapter(_metadata).Preflight(
            source.SourceId,
            source.CanonicalRoot,
            policy,
            cancellationToken);
    }

    private RoutingInputSourceDisplay ToDisplay(RoutingInputSourceRecord source)
    {
        XboxDvrBlockedOccurrenceInspectionResult? blocked = null;
        if (source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive &&
            !source.Retired &&
            source.AttentionReason == RoutingInputSourceAttentionReason.BlockedOccurrence)
        {
            blocked = _recovery.InspectBlockedOccurrences(
                source.SourceId,
                source.Revision);
        }
        var eligibleBlockedCount = blocked is
            { Status: XboxDvrBlockedOccurrenceInspectionStatus.Available, EligibleCount: > 0 }
            ? blocked.EligibleCount
            : 0;
        var recoveryPending = blocked?.Status ==
                              XboxDvrBlockedOccurrenceInspectionStatus.RecoveryPending;
        var retryPending = blocked?.Status ==
                           XboxDvrBlockedOccurrenceInspectionStatus.RetryPending;
        return new RoutingInputSourceDisplay(
            source.SourceId,
            source.DisplayName,
            source.Kind switch
            {
                RoutingInputSourceKind.XboxGameDvrOneDrive =>
                    "Console captures synced through OneDrive",
                RoutingInputSourceKind.SteelSeriesGg =>
                    "SteelSeries GG · MP4 clips directly in the selected folder",
                RoutingInputSourceKind.Nvidia =>
                    "NVIDIA · clips inside one game folder below the selected folder",
                _ => "External clip source"
            },
            source.Kind,
            source.CanonicalRoot,
            source.Enabled,
            !source.Retired && source.Health == RoutingInputSourceHealth.Ready,
            source.Revision,
            source.Health,
            source.Retired,
            source.AttentionReason,
            eligibleBlockedCount,
            recoveryPending,
            retryPending);
    }

    private RoutingInputSourceRecord? FindSource(
        string sourceId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var snapshot = _catalog.Inspect(cancellationToken);
        return snapshot.IsUsable
            ? snapshot.Sources.SingleOrDefault(source =>
                source.SourceId.Equals(sourceId, StringComparison.Ordinal) &&
                source.Revision == expectedRevision)
            : null;
    }

    private static IRoutingWatchedSourceAdapter AdapterFor(RoutingInputSourceKind kind) =>
        RoutingWatchedSourceAdapters.Get(kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg => ClipCaptureSource.SteelSeriesGg,
            RoutingInputSourceKind.Nvidia => ClipCaptureSource.Nvidia,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

    private bool HasMatchingNamedBaseline(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentIdentity = RoutingWatchedSourceRootIdentity.Create(
                source.Kind,
                source.CanonicalRoot);
            if (!currentIdentity.Equals(
                    source.RootIdentitySha256, StringComparison.Ordinal))
                return false;
            var loaded = _namedWatchedBaselines.Load(
                source.SourceId,
                cancellationToken);
            return loaded.LoadedFromDisk && loaded.Document is { } baseline &&
                   baseline.SourceId.Equals(source.SourceId, StringComparison.Ordinal) &&
                   baseline.Kind == source.Kind &&
                   baseline.RootIdentitySha256.Equals(
                       source.RootIdentitySha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
                UnauthorizedAccessException or NotSupportedException or
                System.Security.SecurityException)
        {
            return false;
        }
    }

    private async Task<RoutingInputSourceViewActionResult>
        MarkRootReplacementRequiredAsync(
            RoutingInputSourceRecord source,
            string reason,
            CancellationToken cancellationToken)
    {
        var marked = await _catalog.SetNeedsAttentionAsync(
                source.SourceId,
                source.Revision,
                RoutingInputSourceAttentionReason.RootAuthorityChanged,
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        return marked.Succeeded && marked.Source is { } changed
            ? new RoutingInputSourceViewActionResult(
                RoutingInputSourceViewActionStatus.ReplacementRequired,
                reason,
                ToDisplay(changed))
            : ToViewAction(marked);
    }

    private string? ValidateRootIsolation(
        string canonicalRoot,
        string? exceptSourceId,
        CancellationToken cancellationToken)
    {
        var root = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(canonicalRoot);
        if (_libraryRoot is not null && CapturePathPolicy.PathsOverlap(root, _libraryRoot))
            return "Choose a recorder folder outside the ClipCord Capture Library.";
        if (_legacyWatchedRoot is not null &&
            CapturePathPolicy.PathsOverlap(root, _legacyWatchedRoot))
        {
            return "That folder overlaps the migrated ClipCord 1.x source. Choose a separate recorder folder.";
        }
        var snapshot = _catalog.Inspect(cancellationToken);
        if (!snapshot.IsUsable)
            return "Saved clip-source status could not be read. No source was changed.";
        return snapshot.Sources.Any(source =>
                !source.Retired &&
                !source.SourceId.Equals(exceptSourceId, StringComparison.Ordinal) &&
                CapturePathPolicy.PathsOverlap(root, source.CanonicalRoot))
            ? "That folder overlaps another connected clip source. Choose a separate recorder folder so one clip cannot enter two routes."
            : null;
    }

    private RoutingInputSourceViewActionResult ToViewAction(
        RoutingInputSourceMutationResult result) => new(
        result.Succeeded
            ? RoutingInputSourceViewActionStatus.Completed
            : result.Status == RoutingInputSourceMutationStatus.ReplacementRequired
                ? RoutingInputSourceViewActionStatus.ReplacementRequired
                : result.Status == RoutingInputSourceMutationStatus.Conflict
                    ? RoutingInputSourceViewActionStatus.Conflict
                    : RoutingInputSourceViewActionStatus.Failed,
        result.Reason,
        result.Source is null ? null : ToDisplay(result.Source));

    private RoutingInputSourceViewActionResult ToRecoveryViewAction(
        XboxDvrSourceRecoveryResult result) => new(
        result.Status switch
        {
            XboxDvrSourceRecoveryStatus.Restored or
                XboxDvrSourceRecoveryStatus.AlreadyReady or
                XboxDvrSourceRecoveryStatus.Relocated =>
                RoutingInputSourceViewActionStatus.Completed,
            XboxDvrSourceRecoveryStatus.ReplacementRequired =>
                RoutingInputSourceViewActionStatus.ReplacementRequired,
            XboxDvrSourceRecoveryStatus.Conflict =>
                RoutingInputSourceViewActionStatus.Conflict,
            XboxDvrSourceRecoveryStatus.Offline or
                XboxDvrSourceRecoveryStatus.StillNeedsAttention =>
                RoutingInputSourceViewActionStatus.NeedsAttention,
            _ => RoutingInputSourceViewActionStatus.Failed
        },
        result.Reason,
        result.Source is null ? null : ToDisplay(result.Source));
}

internal static class XboxDvrSourceDiscovery
{
    internal static string? FindDefaultRoot()
    {
        var oneDriveRoots = new[]
        {
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDrive")
        };
        foreach (var root in oneDriveRoots
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(root!, "Videos", "Xbox Game DVR"));
                if (Directory.Exists(candidate))
                    return Path.TrimEndingDirectorySeparator(candidate);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException or
                    NotSupportedException)
            {
                // Continue to the next registered consumer OneDrive root.
            }
        }
        return null;
    }
}

internal enum RoutesRuntimeViewState
{
    LegacySetupNeeded,
    LegacyActive,
    Activating,
    Active,
    RecoveryNeeded,
    Blocked
}

internal interface IRoutingConnectionViewSource
{
    IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections();
}

internal interface IRoutingConnectionManagerViewSource : IRoutingConnectionViewSource
{
    Task<DiscordConnectionMutationResult> AddDiscordAsync(
        string displayName,
        string webhookUrl,
        CancellationToken cancellationToken = default);

    Task<DiscordConnectionMutationResult> RemoveDiscordAsync(
        string connectionId,
        CancellationToken cancellationToken = default);
}

internal sealed class LegacyDiscordConnectionViewSource : IRoutingConnectionViewSource
{
    private readonly Func<AppSettings> _settings;

    internal LegacyDiscordConnectionViewSource(Func<AppSettings> settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections()
    {
        var settings = _settings();
        if (!WebhookValidation.IsDiscordWebhook(settings.WebhookUrl) ||
            !DiscordRoutingConnectionIdentity.TryCreate(settings.WebhookUrl, out var connectionId))
        {
            return [];
        }

        return
        [
            new RoutingConnectionDisplay(
                connectionId,
                "Friends server",
                "Existing encrypted Discord webhook",
                Available: true)
        ];
    }
}

internal sealed class DiscordConnectionCatalogViewSource : IRoutingConnectionManagerViewSource
{
    private readonly DiscordConnectionCatalog _catalog;

    internal DiscordConnectionCatalogViewSource(DiscordConnectionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyList<RoutingConnectionDisplay> LoadDiscordConnections()
    {
        var snapshot = _catalog.Inspect();
        if (!snapshot.IsUsable) return [];
        return snapshot.Connections.Select(connection => new RoutingConnectionDisplay(
                connection.ConnectionId,
                connection.DisplayName,
                connection.ImportedFromLegacySettings
                    ? "Imported securely from your existing Discord destination"
                    : "Encrypted for this Windows account",
                connection.Health == DiscordConnectionHealth.Ready))
            .ToArray();
    }

    public Task<DiscordConnectionMutationResult> AddDiscordAsync(
        string displayName,
        string webhookUrl,
        CancellationToken cancellationToken = default) =>
        _catalog.AddAsync(displayName, webhookUrl, cancellationToken: cancellationToken);

    public Task<DiscordConnectionMutationResult> RemoveDiscordAsync(
        string connectionId,
        CancellationToken cancellationToken = default) =>
        _catalog.RemoveAsync(connectionId, cancellationToken: cancellationToken);
}

internal sealed class RoutesView : UserControl
{
    private readonly RoutingRouteManager _routeManager;
    private readonly IRoutingConnectionViewSource _connections;
    private readonly IRoutingInputSourceViewSource? _inputSources;
    private readonly IRoutingLocalOnlyModeViewSource? _localOnlyMode;
    private readonly BrandedScrollHost _scrollHost;
    private readonly Panel _contentHost;
    private readonly OutlineButton _routesTab;
    private readonly OutlineButton _connectionsTab;
    private readonly GradientButton _newRouteButton;
    private readonly FlowLayoutPanel _headerActions;
    private readonly RoundedPanel _localOnlyStatusPill;
    private readonly Func<bool> _isCutoverCommitted;
    private readonly Func<RoutesRuntimeViewState>? _runtimeStateProvider;
    private readonly Func<Task<bool>>? _retryRuntimeAsync;
    private readonly Func<IReadOnlyList<RoutingConnectionDisplay>, RoutingRouteDraft?> _editRoute;
    private readonly Func<RoutingInputSourceRegistrationDraft?> _createInputSourceDraft;
    private readonly Func<string, string, DialogResult> _confirmInputSourceRemoval;
    private readonly Func<string, string, DialogResult> _confirmInputSourceReplacement;
    private readonly Func<string, string, DialogResult> _confirmInputSourceSkip;
    private readonly Func<string?> _discoverDefaultXboxRoot;
    private readonly Action<string, string> _showInputSourceMessage;
    private readonly RoutingMigratedInputSourceDisplay? _migratedInputSource;
    private readonly int? _layoutDpi;
    private bool _showConnections;
    private bool _busy;
    private bool _localOnlyModeBusy;
    private bool _recordingLocalOnlyHotkey;
    private string? _localOnlyModeTransientError;
    private RoutingLocalOnlyModeViewSnapshot? _localOnlyModeSnapshot;
    private bool _cutoverCommitted;
    private RoutesRuntimeViewState _runtimeState;
    private IReadOnlyList<RoutingInputSourceDisplay> _editorInputSources = [];

    internal event EventHandler? OpenSettingsRequested;
    internal event EventHandler? DeliveryHistoryRequested;
    internal event EventHandler? LocalOnlyModeChanged;

    internal Control HeaderActionButton => _newRouteButton;
    internal Control HeaderActions => _headerActions;

    internal bool TryBeginCreateRoute()
    {
        SelectTab(showConnections: false);
        if (_runtimeState != RoutesRuntimeViewState.Active ||
            !_newRouteButton.Visible || !_newRouteButton.Enabled)
        {
            return false;
        }

        _newRouteButton.PerformClick();
        return true;
    }

    internal RoutesView(
        RoutingRouteManager? routeManager = null,
        IRoutingConnectionViewSource? connections = null,
        Func<bool>? isCutoverCommitted = null,
        Func<IReadOnlyList<RoutingConnectionDisplay>, RoutingRouteDraft?>? editRoute = null,
        int? layoutDpi = null,
        Func<RoutesRuntimeViewState>? runtimeStateProvider = null,
        Func<Task<bool>>? retryRuntimeAsync = null,
        IRoutingInputSourceViewSource? inputSources = null,
        Func<string, string, DialogResult>? confirmInputSourceRemoval = null,
        Func<string, string, DialogResult>? confirmInputSourceReplacement = null,
        Func<string, string, DialogResult>? confirmInputSourceSkip = null,
        Func<string?>? discoverDefaultXboxRoot = null,
        Action<string, string>? showInputSourceMessage = null,
        IRoutingLocalOnlyModeViewSource? localOnlyMode = null,
        Func<RoutingInputSourceRegistrationDraft?>? createInputSourceDraft = null,
        RoutingMigratedInputSourceDisplay? migratedInputSource = null)
    {
        _layoutDpi = layoutDpi is null ? null : Math.Max(96, layoutDpi.Value);
        _routeManager = routeManager ?? new RoutingRouteManager();
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _inputSources = inputSources;
        _localOnlyMode = localOnlyMode;
        _isCutoverCommitted = isCutoverCommitted ?? CreateCutoverStatusSource(_routeManager);
        _runtimeStateProvider = runtimeStateProvider;
        _retryRuntimeAsync = retryRuntimeAsync;
        _editRoute = editRoute ?? ShowRouteEditor;
        _createInputSourceDraft = createInputSourceDraft ?? ShowInputSourceDialog;
        _migratedInputSource = migratedInputSource;
        _confirmInputSourceRemoval = confirmInputSourceRemoval ?? ((message, caption) =>
            MessageBox.Show(
                this,
                message,
                caption,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2));
        _confirmInputSourceReplacement = confirmInputSourceReplacement ??
            ((message, caption) => MessageBox.Show(
                this,
                message,
                caption,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2));
        _confirmInputSourceSkip = confirmInputSourceSkip ??
            ((message, caption) => MessageBox.Show(
                this,
                message,
                caption,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2));
        _discoverDefaultXboxRoot = discoverDefaultXboxRoot ??
            XboxDvrSourceDiscovery.FindDefaultRoot;
        _showInputSourceMessage = showInputSourceMessage ?? ((message, caption) =>
            MessageBox.Show(
                this,
                message,
                caption,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning));
        Name = "RoutesView";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.SurfaceBase;
        DoubleBuffered = true;

        _newRouteButton = new GradientButton
        {
            Name = "NewRouteButton",
            Text = "+  New route",
            AccessibleName = "Create a new routing rule",
            AutoSize = false,
            Size = new Size(ScaleLogical(114), ScaleLogical(33)),
            Margin = Padding.Empty
        };
        _newRouteButton.Click += async (_, _) =>
        {
            if (_runtimeState == RoutesRuntimeViewState.Active)
                await AddRouteAsync();
            else if (_runtimeState == RoutesRuntimeViewState.LegacySetupNeeded)
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
            else
                await RetryRuntimeAsync();
        };

        _localOnlyStatusPill = BuildHeaderLocalOnlyModePill();
        _headerActions = new FlowLayoutPanel
        {
            Name = "RoutesHeaderActions",
            AccessibleName = "Routes status and primary action",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        _localOnlyStatusPill.Margin = new Padding(0, ScaleLogical(4), ScaleLogical(12), ScaleLogical(4));
        _newRouteButton.Margin = Padding.Empty;
        _headerActions.Controls.Add(_localOnlyStatusPill);
        _headerActions.Controls.Add(_newRouteButton);

        _routesTab = CreateTab("Routes", selected: true);
        _connectionsTab = CreateTab("Connections", selected: false);
        _routesTab.Click += (_, _) => SelectTab(showConnections: false);
        _connectionsTab.Click += (_, _) => SelectTab(showConnections: true);

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(45)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildTabs(), 0, 0);

        _contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "RoutesScrollHost",
            AccessibleName = "Routing rules",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        _contentHost.Controls.Add(_scrollHost);
        root.Controls.Add(_contentHost, 0, 1);
        Controls.Add(root);
        Resize += (_, _) => RefreshViewport();
        Reload();
    }

    internal void ActivateView()
    {
        Reload();
        RefreshViewport();
    }

    internal void RefreshViewport()
    {
        _scrollHost.RefreshContentLayout();
        Invalidate(true);
    }

    internal void RefreshRuntimeStatus()
    {
        if (IsDisposed || Disposing) return;
        var cutover = ReadCutoverStatus();
        var runtime = ReadRuntimeState(cutover);
        if (cutover == _cutoverCommitted && runtime == _runtimeState) return;
        Reload();
    }

    private Control BuildTabs()
    {
        var host = new BufferedTableLayoutPanel
        {
            Name = "RoutesTabs",
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(ScaleLogical(28), 0, ScaleLogical(28), 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(92)));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(124)));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        host.Controls.Add(_routesTab, 0, 0);
        host.Controls.Add(_connectionsTab, 1, 0);
        var line = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.BorderDefault
        };
        host.Controls.Add(line, 0, 1);
        host.SetColumnSpan(line, 3);
        return host;
    }

    private RoundedPanel BuildHeaderLocalOnlyModePill()
    {
        var pill = new RoundedPanel
        {
            Name = "HeaderLocalOnlyModePill",
            AccessibleName = "Local-only mode is on",
            AccessibleRole = AccessibleRole.StaticText,
            AutoSize = false,
            Size = new Size(ScaleLogical(161), ScaleLogical(25)),
            BackColor = Color.FromArgb(58, 53, 50),
            BorderColor = Color.FromArgb(224, 151, 54),
            CornerRadius = ScaleLogical(13),
            Padding = new Padding(ScaleLogical(11), 0, ScaleLogical(12), 0),
            TabStop = false,
            Visible = false
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(16)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new RoundedPanel
        {
            Name = "HeaderLocalOnlyModeDot",
            Size = new Size(ScaleLogical(7), ScaleLogical(7)),
            Anchor = AnchorStyles.None,
            BackColor = Color.FromArgb(224, 151, 54),
            BorderColor = Color.Transparent,
            CornerRadius = ScaleLogical(4),
            Margin = Padding.Empty,
            TabStop = false
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Name = "HeaderLocalOnlyModeLabel",
            Text = "LOCAL-ONLY MODE",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(224, 151, 54),
            Font = ClipCordTheme.InterfaceFont(8.25f, FontStyle.Bold),
            Margin = Padding.Empty,
            AutoEllipsis = false,
            UseMnemonic = false,
            TabStop = false
        }, 1, 0);
        pill.Controls.Add(layout);
        return pill;
    }

    private OutlineButton CreateTab(string text, bool selected) => new()
    {
        Name = $"{text}RouteTab",
        Text = text,
        AccessibleRole = AccessibleRole.PageTab,
        AccessibleName = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, ScaleLogical(12), 0),
        SurfaceColor = ClipCordTheme.SurfaceBase,
        HoverColor = ClipCordTheme.SurfaceControl,
        OutlineColor = Color.Transparent,
        ForeColor = selected ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary,
        Font = ClipCordTheme.InterfaceFont(9.5f, selected ? FontStyle.Bold : FontStyle.Regular)
    };

    private void SelectTab(bool showConnections)
    {
        if (_showConnections == showConnections) return;
        _showConnections = showConnections;
        _routesTab.ForeColor = showConnections ? ClipCordTheme.TextSecondary : ClipCordTheme.TextPrimary;
        _routesTab.Font = ClipCordTheme.InterfaceFont(
            9.5f, showConnections ? FontStyle.Regular : FontStyle.Bold);
        _connectionsTab.ForeColor = showConnections ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary;
        _connectionsTab.Font = ClipCordTheme.InterfaceFont(
            9.5f, showConnections ? FontStyle.Bold : FontStyle.Regular);
        Reload();
    }

    private void Reload()
    {
        if (IsDisposed || Disposing) return;
        _cutoverCommitted = ReadCutoverStatus();
        _runtimeState = ReadRuntimeState(_cutoverCommitted);
        _localOnlyModeSnapshot = SafeInspectLocalOnlyMode();
        ConfigureHeaderAction();
        SuspendLayout();
        try
        {
            _scrollHost.Content = _showConnections
                ? BuildConnectionsContent()
                : BuildRoutesContent();
            _scrollHost.RefreshContentLayout(preservePosition: false);
        }
        finally
        {
            ResumeLayout(true);
        }
    }

    private Control BuildRoutesContent()
    {
        var content = CreateContentTable("RoutesContent");
        var row = 0;
        content.Controls.Add(BuildRouteToolbar(), 0, row++);
        if (_localOnlyMode is not null && _cutoverCommitted &&
            _localOnlyModeSnapshot is { } localOnlySnapshot)
        {
            content.Controls.Add(BuildLocalOnlyModeControl(localOnlySnapshot), 0, row++);
            if (localOnlySnapshot.IsAvailable &&
                !localOnlySnapshot.FirstRunNoticeDismissed)
            {
                content.Controls.Add(BuildLocalOnlyMigrationNotice(), 0, row++);
            }
        }
        content.Controls.Add(BuildLibraryNotice(_localOnlyModeSnapshot?.EffectiveEnabled == true), 0, row++);
        content.Controls.Add(BuildCutoverNotice(), 0, row++);

        var loaded = _routeManager.Load();
        if (loaded.Status == RoutingDocumentLoadStatus.Missing)
        {
            content.Controls.Add(BuildEmptyState(), 0, row++);
            return FinishContent(content, row);
        }
        if (!loaded.LoadedFromDisk || loaded.Document is null)
        {
            content.Controls.Add(BuildUnavailableState(loaded.Status), 0, row++);
            return FinishContent(content, row);
        }

        var ordered = loaded.Document.Routes.OrderBy(route => route.Priority).ToArray();
        var specific = ordered.Where(route => route.Kind == RoutingRouteKind.Specific).ToArray();
        var fallback = ordered.Where(route => route.Kind == RoutingRouteKind.Fallback).ToArray();
        content.Controls.Add(BuildSectionHeader(
            "CONFIGURED ROUTES",
            $"{specific.Length} configured"), 0, row++);
        if (specific.Length == 0)
        {
            content.Controls.Add(BuildInlineEmpty(
                "No specific routes yet",
                _runtimeState == RoutesRuntimeViewState.Active && _cutoverCommitted
                    ? "Create a route for a game, capture type, or watched-folder source."
                    : "Specific route editing remains locked until Routes is active."), 0, row++);
        }
        else
        {
            foreach (var route in specific)
                content.Controls.Add(BuildRouteCard(route, ordered), 0, row++);
        }

        content.Controls.Add(BuildSectionHeader(
            "FALLBACK",
            "Runs only when no specific route matches"), 0, row++);
        if (fallback.Length == 0)
        {
            content.Controls.Add(BuildInlineEmpty(
                "No fallback route",
                _runtimeState == RoutesRuntimeViewState.Active && _cutoverCommitted
                    ? "The committed legacy fallback is missing. Routing must remain inactive until it is restored."
                    : "Safe cutover will create the fallback that preserves today’s Discord or Local-only behavior."), 0, row++);
        }
        else
        {
            foreach (var route in fallback)
                content.Controls.Add(BuildRouteCard(route, ordered), 0, row++);
        }
        return FinishContent(content, row);
    }

    private Control BuildConnectionsContent()
    {
        var content = CreateContentTable("ConnectionsContent");
        var row = 0;
        var inputSources = SafeInspectInputSources();
        var configuredSourceCount = inputSources.Sources.Count +
                                    (_migratedInputSource is null ? 0 : 1);
        content.Controls.Add(BuildSectionHeader(
            "CLIP SOURCES",
            inputSources.IsUsable
                ? $"{configuredSourceCount} configured source{(configuredSourceCount == 1 ? string.Empty : "s")}"
                : "Status unavailable"), 0, row++);
        if (!inputSources.IsUsable)
        {
            var unavailable = BuildInlineEmpty(
                "Clip sources unavailable",
                inputSources.StatusDetail);
            unavailable.Name = "InputSourcesUnavailableState";
            content.Controls.Add(unavailable, 0, row++);
        }
        else
        {
            content.Controls.Add(BuildInputSourceToolbar(), 0, row++);
            if (_migratedInputSource is not null)
                content.Controls.Add(BuildMigratedInputSourceCard(_migratedInputSource), 0, row++);
            foreach (var source in inputSources.Sources)
                content.Controls.Add(BuildInputSourceCard(source), 0, row++);
            if (configuredSourceCount == 0)
            {
                var empty = BuildInlineEmpty(
                    "No clip sources connected",
                    "Add SteelSeries GG, NVIDIA, or Xbox Game DVR. You choose the folder, so it can live on any local drive.");
                empty.Name = "InputSourcesEmptyState";
                content.Controls.Add(empty, 0, row++);
            }
        }

        content.Controls.Add(BuildSectionHeader(
            "CONNECTED ACCOUNTS",
            "Destinations routes can safely reference"), 0, row++);
        if (_connections is IRoutingConnectionManagerViewSource)
            content.Controls.Add(BuildConnectionToolbar(), 0, row++);
        var connections = SafeLoadConnections();
        if (connections.Count == 0)
        {
            var empty = BuildInlineEmpty(
                "Discord is not connected",
                _connections is IRoutingConnectionManagerViewSource
                    ? "Use Add Discord above to connect a webhook in Routes before creating a Discord delivery."
                    : "No Discord connection is available. Routes did not assume that a legacy webhook is connected.");
            content.Controls.Add(empty, 0, row++);
        }
        else
        {
            foreach (var connection in connections)
                content.Controls.Add(BuildConnectionCard(connection), 0, row++);
        }

        content.Controls.Add(BuildConnectorRoadmap(), 0, row++);
        return FinishContent(content, row);
    }

    private Control BuildInputSourceToolbar()
    {
        var host = new BufferedTableLayoutPanel
        {
            Name = "InputSourceToolbar",
            Dock = DockStyle.Top,
            Height = ScaleLogical(44),
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(132)));
        host.Controls.Add(new Label
        {
            Name = "InputSourceToolbarDescription",
            Text = "Connect recorder folders without changing where those apps save clips.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 0, 0);
        var add = CreateSmallButton("+  Add source", 124);
        add.Name = "AddInputSourceButton";
        add.AccessibleName = "Add an external clip source";
        add.AccessibleDescription =
            "Connect a SteelSeries GG, NVIDIA, or Xbox Game DVR folder. Existing clips are not delivered by default.";
        add.Anchor = AnchorStyles.Right;
        add.Margin = Padding.Empty;
        add.Enabled = !_busy;
        add.TabStop = add.Enabled;
        add.Click += async (_, _) => await AddInputSourceAsync();
        host.Controls.Add(add, 1, 0);
        return host;
    }

    private Control BuildMigratedInputSourceCard(RoutingMigratedInputSourceDisplay source)
    {
        var card = new RoundedPanel
        {
            Name = "MigratedInputSourceCard",
            AccessibleName = $"{source.Name}, migrated source",
            Dock = DockStyle.Top,
            Height = ScaleLogical(92),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(12), ScaleLogical(16), ScaleLogical(12))
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(42)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(160)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = new FigmaIconControl
        {
            Asset = FigmaIconAsset.Folder,
            IconColor = Color.FromArgb(176, 128, 255),
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 7, 10, 7)
        };
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);
        layout.Controls.Add(new Label
        {
            Name = "MigratedInputSourceName",
            Text = source.Name,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 1, 0);
        layout.Controls.Add(new Label
        {
            Name = "MigratedInputSourceDetail",
            Text = $"{source.Detail} · Preserved from ClipCord 1.x. Add a named source for a different recorder folder.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = false,
            Margin = Padding.Empty
        }, 1, 1);
        layout.Controls.Add(new Label
        {
            Name = "MigratedInputSourceStatus",
            Text = "ACTIVE · MIGRATED",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(76, 210, 145),
            Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty
        }, 2, 0);
        var explanation = new Label
        {
            Name = "MigratedInputSourceLockExplanation",
            Text = "Read-only safety binding",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            TextAlign = ContentAlignment.TopRight,
            Margin = Padding.Empty
        };
        layout.Controls.Add(explanation, 2, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildInputSourceCard(RoutingInputSourceDisplay source)
    {
        var retired = source.Retired;
        var needsAttention = !retired &&
                             (source.Health == RoutingInputSourceHealth.NeedsAttention ||
                              !source.Available);
        var replacementRequired = needsAttention &&
                                  source.AttentionReason ==
                                  RoutingInputSourceAttentionReason.RootAuthorityChanged;
        var hasKnownBlockedClips = needsAttention &&
                                   source.SkippableBlockedClipCount > 0;
        var blockedRecoveryPending = needsAttention &&
                                     source.BlockedClipRecoveryPending;
        var blockedRetryPending = needsAttention &&
                                  source.BlockedClipRetryPending;
        var actionWidth = hasKnownBlockedClips
            ? ScaleLogical(472)
            : blockedRetryPending
                ? ScaleLogical(376)
            : blockedRecoveryPending
                ? ScaleLogical(376)
            : replacementRequired
            ? ScaleLogical(376)
            : retired
                ? ScaleLogical(252)
                : source.Kind is RoutingInputSourceKind.SteelSeriesGg or
                    RoutingInputSourceKind.Nvidia
                    ? ScaleLogical(352)
                    : ScaleLogical(340);
        var card = new RoundedPanel
        {
            Name = $"InputSourceCard_{source.SourceId}",
            AccessibleName = $"{source.Name}, {DescribeInputSourceStatus(source)}",
            Dock = DockStyle.Top,
            Height = ScaleLogical(108),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = needsAttention || retired
                ? Color.FromArgb(137, 94, 36)
                : ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(13), ScaleLogical(16), ScaleLogical(12))
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(42)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(126)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, actionWidth));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(31)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = new FigmaIconControl
        {
            Asset = FigmaIconAsset.Folder,
            IconColor = needsAttention || retired
                ? Color.FromArgb(224, 151, 54)
                : Color.FromArgb(176, 128, 255),
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 7, 10, 7)
        };
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);
        layout.Controls.Add(new Label
        {
            Name = $"InputSourceName_{source.SourceId}",
            Text = source.Name,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 1, 0);
        layout.Controls.Add(new Label
        {
            Name = $"InputSourceDetail_{source.SourceId}",
            Text = retired
                ? $"{source.Detail} · Replaced source retained for existing routes; it cannot receive new clips."
                : replacementRequired
                    ? $"{source.Detail} · A different source was found here. Register it separately; existing routes stay unchanged."
                : hasKnownBlockedClips
                    ? $"{source.Detail} · This Xbox source is paused by one or more blocked clips. Retry them safely, skip them explicitly, or leave this source paused; other clip sources keep running."
                : blockedRetryPending
                    ? $"{source.Detail} · The blocked-clip retry is already durable. Resume this Xbox source to finish verified recovery before later clips continue."
                : blockedRecoveryPending
                    ? $"{source.Detail} · The blocked clips were already skipped. Resume this Xbox source to finish verified recovery; other clip sources keep running."
                : needsAttention
                ? $"{source.Detail} · Recheck before using this source in a new route."
                : source.Enabled
                    ? $"{source.Detail} · Available to routing."
                    : $"{source.Detail} · Kept for existing routes while disabled.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = false,
            Margin = Padding.Empty
        }, 1, 1);
        layout.Controls.Add(new Label
        {
            Name = $"InputSourceStatus_{source.SourceId}",
            Text = DescribeInputSourceStatus(source),
            Dock = DockStyle.Fill,
            ForeColor = needsAttention || retired
                ? Color.FromArgb(224, 151, 54)
                : source.Enabled
                    ? Color.FromArgb(76, 210, 145)
                    : ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 2, 0);

        var actions = new FlowLayoutPanel
        {
            Name = $"InputSourceActions_{source.SourceId}",
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleLogical(18), 0, 0),
            BackColor = Color.Transparent
        };
        var remove = CreateSmallButton("Remove", 72);
        remove.Name = $"RemoveInputSource_{source.SourceId}";
        remove.AccessibleName = $"Remove {source.Name}";
        remove.Click += async (_, _) =>
        {
            if (_confirmInputSourceRemoval(
                    $"Remove ‘{source.Name}’? Existing routes and pending work must stop referencing it first.",
                    "Remove clip source") != DialogResult.OK) return;
            await RunInputSourceCommandAsync(
                () => _inputSources!.RemoveAsync(source.SourceId, source.Revision));
        };
        actions.Controls.Add(remove);

        var toggle = CreateSmallButton(source.Enabled ? "Disable" : "Enable", 72);
        toggle.Name = $"ToggleInputSource_{source.SourceId}";
        toggle.AccessibleName = $"{(source.Enabled ? "Disable" : "Enable")} {source.Name}";
        toggle.Enabled = !retired && (source.Enabled || !needsAttention);
        toggle.TabStop = toggle.Enabled;
        toggle.AccessibleDescription = !toggle.Enabled
            ? retired
                ? "This replaced source is retained for existing references and cannot be enabled."
                : "Recheck this source before enabling it."
            : null;
        toggle.Click += async (_, _) => await RunInputSourceCommandAsync(
            () => _inputSources!.SetEnabledAsync(
                source.SourceId,
                source.Revision,
                enabled: !source.Enabled));
        actions.Controls.Add(toggle);

        if (!retired && !hasKnownBlockedClips && !blockedRetryPending)
        {
            var isXbox = source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive;
            var relocate = CreateSmallButton(
                isXbox ? "Find folder" : "Replace folder",
                isXbox ? 92 : 104);
            relocate.Name = $"RelocateInputSource_{source.SourceId}";
            relocate.AccessibleName = isXbox
                ? $"Find moved folder for {source.Name}"
                : $"Replace folder for {source.Name}";
            relocate.AccessibleDescription = isXbox
                ? "Check the currently discovered Xbox Game DVR folder and preserve this source only when its native identity matches."
                : "Register a different path as a separate source. It does not inherit this source's routes, from-now baseline, or pending work; reconnect the original saved folder to restore this source.";
            relocate.Click += async (_, _) => await RelocateInputSourceAsync(source);
            actions.Controls.Add(relocate);
        }

        if (replacementRequired)
        {
            var replace = CreateSmallButton("Replace source", 112);
            replace.Name = $"ReplaceInputSource_{source.SourceId}";
            replace.AccessibleName = $"Register replacement for {source.Name}";
            replace.AccessibleDescription =
                "Retire this source and register the different source separately. Existing routes and approved history stay on the replaced source.";
            replace.Click += async (_, _) => await RegisterInputSourceReplacementAsync(source);
            actions.Controls.Add(replace);
        }

        if (hasKnownBlockedClips)
        {
            var retryLabel = source.SkippableBlockedClipCount == 1
                ? "Retry blocked clip (1)"
                : $"Retry blocked clips ({source.SkippableBlockedClipCount})";
            var retry = CreateSmallButton(retryLabel, 148);
            retry.Name = $"RetryBlockedInputSource_{source.SourceId}";
            retry.AccessibleName = $"Retry blocked clips for {source.Name}";
            retry.AccessibleDescription =
                "Resume any committed promotion first; otherwise recopy and revalidate the untouched OneDrive originals before later clips continue.";
            retry.SurfaceColor = ClipCordTheme.VioletMuted;
            retry.HoverColor = Color.FromArgb(65, 52, 105);
            retry.OutlineColor = ClipCordTheme.Violet;
            retry.Click += async (_, _) => await RetryBlockedInputSourceClipsAsync(source);
            actions.Controls.Add(retry);

            var skipLabel = source.SkippableBlockedClipCount == 1
                ? "Skip blocked clip (1)"
                : $"Skip blocked clips ({source.SkippableBlockedClipCount})";
            var skip = CreateSmallButton(skipLabel, 148);
            skip.Name = $"SkipBlockedInputSource_{source.SourceId}";
            skip.AccessibleName = $"Skip blocked clips for {source.Name}";
            skip.AccessibleDescription =
                "Keep the OneDrive originals untouched, exclude the failed clips from routing, and let future clips resume after every blocker is resolved.";
            skip.Click += async (_, _) => await SkipBlockedInputSourceClipsAsync(source);
            actions.Controls.Add(skip);
        }
        else if (blockedRetryPending)
        {
            var resumeRetry = CreateSmallButton("Resume retry", 112);
            resumeRetry.Name = $"ResumeRetryInputSource_{source.SourceId}";
            resumeRetry.AccessibleName = $"Resume blocked-clip retry for {source.Name}";
            resumeRetry.AccessibleDescription =
                "Finish restoring this Xbox source after its blocked-clip retry was already saved.";
            resumeRetry.SurfaceColor = ClipCordTheme.VioletMuted;
            resumeRetry.HoverColor = Color.FromArgb(65, 52, 105);
            resumeRetry.OutlineColor = ClipCordTheme.Violet;
            resumeRetry.Click += async (_, _) => await ResumeBlockedInputSourceRetryAsync(source);
            actions.Controls.Add(resumeRetry);
        }
        else if (blockedRecoveryPending)
        {
            var resume = CreateSmallButton("Resume source", 112);
            resume.Name = $"ResumeBlockedInputSource_{source.SourceId}";
            resume.AccessibleName = $"Resume {source.Name} after blocked clips were skipped";
            resume.AccessibleDescription =
                "Finish verified source recovery after the previously confirmed skips; the OneDrive originals remain untouched.";
            resume.Click += async (_, _) => await ResumeBlockedInputSourceAsync(source);
            actions.Controls.Add(resume);
        }
        else if (!retired && !replacementRequired)
        {
            var recheck = CreateSmallButton("Recheck", 76);
            recheck.Name = $"RecheckInputSource_{source.SourceId}";
            recheck.AccessibleName = $"Recheck {source.Name}";
            recheck.Enabled = needsAttention;
            recheck.TabStop = recheck.Enabled;
            recheck.AccessibleDescription = recheck.Enabled
                ? source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "Verify the saved OneDrive source without opening any clip content."
                    : "Verify the saved recorder folder and its native identity without opening clip content."
                : "This source is ready and does not need a recheck.";
            recheck.Click += async (_, _) => await RunInputSourceCommandAsync(
                () => _inputSources!.RecheckAsync(source.SourceId, source.Revision));
            actions.Controls.Add(recheck);
        }
        layout.Controls.Add(actions, 3, 0);
        layout.SetRowSpan(actions, 2);
        card.Controls.Add(layout);
        return card;
    }

    private static string DescribeInputSourceStatus(RoutingInputSourceDisplay source) =>
        source.Retired
            ? "REPLACED"
            : source.AttentionReason == RoutingInputSourceAttentionReason.RootAuthorityChanged
                ? "NEEDS REPLACEMENT"
            : source.Health == RoutingInputSourceHealth.NeedsAttention || !source.Available
            ? "NEEDS ATTENTION"
            : source.Enabled ? "READY" : "OFF";

    private BufferedTableLayoutPanel CreateContentTable(string name)
    {
        var content = new BufferedTableLayoutPanel
        {
            Name = name,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 0,
            Margin = Padding.Empty,
            Padding = new Padding(
                ScaleLogical(28),
                ScaleLogical(16),
                ScaleLogical(28),
                ScaleLogical(28)),
            BackColor = ClipCordTheme.SurfaceBase,
            MinimumSize = new Size(Math.Max(1, Width), 1)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return content;
    }

    private static Control FinishContent(BufferedTableLayoutPanel content, int rows)
    {
        content.RowCount = rows;
        for (var index = 0; index < rows; index++)
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return content;
    }

    private Control BuildRouteToolbar()
    {
        var toolbar = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(38),
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, ScaleLogical(12)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(108)));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(144)));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toolbar.Controls.Add(new Label
        {
            Text = "Matching specific routes run in order. Fallback runs only when none match.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(9f),
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 0);
        var reorder = CreateSmallButton("Reorder", 96);
        reorder.Name = "ReorderRoutesButton";
        reorder.Enabled = false;
        reorder.AccessibleDescription = "Use the up and down buttons on each route.";
        toolbar.Controls.Add(reorder, 1, 0);
        var history = CreateSmallButton("Delivery history", 132);
        history.Name = "DeliveryHistoryButton";
        history.Click += (_, _) => DeliveryHistoryRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(history, 2, 0);
        return toolbar;
    }

    private Control BuildLibraryNotice(bool localOnlyModeEnabled = false)
    {
        var notice = new RoundedPanel
        {
            Name = "RoutingLibraryNotice",
            Dock = DockStyle.Top,
            Height = ScaleLogical(48),
            BackColor = localOnlyModeEnabled
                ? Color.FromArgb(54, 39, 20)
                : Color.FromArgb(19, 49, 47),
            BorderColor = localOnlyModeEnabled
                ? Color.FromArgb(156, 106, 35)
                : Color.FromArgb(42, 120, 93),
            CornerRadius = ScaleLogical(9),
            Margin = new Padding(0, 0, 0, ScaleLogical(18)),
            Padding = new Padding(ScaleLogical(14), 0, ScaleLogical(14), 0)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(29)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new FigmaIconControl
        {
            Asset = localOnlyModeEnabled ? FigmaIconAsset.Shield : FigmaIconAsset.Disk,
            IconColor = localOnlyModeEnabled
                ? Color.FromArgb(235, 172, 67)
                : Color.FromArgb(49, 177, 113),
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 14, 8, 14)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = localOnlyModeEnabled
                ? "Clips are still filed into your Library → Local only. External delivery actions are paused for future clips."
                : "Library is always on · every source clip is filed after its route finishes and stays available in Gallery.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = localOnlyModeEnabled
                ? Color.FromArgb(244, 207, 145)
                : Color.FromArgb(190, 238, 218),
            Font = ClipCordTheme.InterfaceFont(9f),
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 1, 0);
        notice.Controls.Add(layout);
        return notice;
    }

    private Control BuildLocalOnlyModeControl(RoutingLocalOnlyModeViewSnapshot snapshot)
    {
        var enabled = snapshot.EffectiveEnabled;
        var available = snapshot.IsAvailable &&
                        _runtimeState == RoutesRuntimeViewState.Active &&
                        !_localOnlyModeBusy;
        var card = new RoundedPanel
        {
            Name = "RoutingLocalOnlyModeControl",
            AccessibleName = "Local-only mode",
            Dock = DockStyle.Top,
            Height = ScaleLogical(66),
            BackColor = enabled
                ? Color.FromArgb(58, 53, 50)
                : ClipCordTheme.SurfaceRaised,
            BorderColor = enabled
                ? Color.FromArgb(224, 151, 54)
                : ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(12)),
            Padding = new Padding(ScaleLogical(12), ScaleLogical(10), ScaleLogical(12), ScaleLogical(10))
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(40)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(112)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(139)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(42)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(16)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(15)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var shieldTile = BuildLocalOnlyShieldTile(enabled);
        layout.Controls.Add(shieldTile, 0, 0);
        layout.SetRowSpan(shieldTile, 3);
        layout.Controls.Add(new Label
        {
            Name = "RoutingLocalOnlyModeTitleLabel",
            Text = enabled ? "Local-only mode on" : "Local-only mode",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9.5f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 1, 0);
        var detail = _localOnlyModeTransientError ??
            (!snapshot.IsAvailable
                ? snapshot.StatusDetail
                : _recordingLocalOnlyHotkey
                    ? "Press a new shortcut · Backspace turns it off · Esc cancels"
                : enabled
                    ? "Future clips stay on this PC. Existing deliveries are unchanged."
                    : "Off · future clips follow your active Routes");
        layout.Controls.Add(new Label
        {
            Name = "RoutingLocalOnlyModeDetailLabel",
            Text = detail,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = _localOnlyModeTransientError is null
                ? ClipCordTheme.TextSecondary
                : Color.FromArgb(244, 174, 180),
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty
        }, 1, 1);
        layout.Controls.Add(new Label
        {
            Name = "RoutingLocalOnlyModeLibraryLabel",
            Text = enabled
                ? "Clips are still filed into your Library → Local only."
                : "Library stays on for every routed clip.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty
        }, 1, 2);

        var shortcut = BuildLocalOnlyShortcutHost(snapshot);
        layout.Controls.Add(shortcut, 2, 0);
        layout.SetRowSpan(shortcut, 3);

        var changeShortcut = CreateSmallButton("Change shortcut", 127);
        changeShortcut.Name = "ChangeLocalOnlyModeShortcutButton";
        changeShortcut.AccessibleName = "Change Local-only mode shortcut";
        changeShortcut.Anchor = AnchorStyles.None;
        changeShortcut.Enabled = available;
        changeShortcut.Click += (_, _) =>
        {
            _localOnlyModeTransientError = null;
            _recordingLocalOnlyHotkey = true;
            Reload();
            BeginInvoke((Action)(() =>
                Controls.Find("LocalOnlyModeShortcutField", searchAllChildren: true)
                    .FirstOrDefault()?.Focus()));
        };
        changeShortcut.Size = new Size(ScaleLogical(127), ScaleLogical(29));
        layout.Controls.Add(changeShortcut, 3, 0);
        layout.SetRowSpan(changeShortcut, 3);

        var toggle = new ToggleSwitch
        {
            Name = "LocalOnlyModeToggle",
            AccessibleName = enabled ? "Turn Local-only mode off" : "Turn Local-only mode on",
            AccessibleRole = AccessibleRole.CheckButton,
            AutoSize = false,
            MinimumSize = Size.Empty,
            CompactTrackOnly = true,
            Text = string.Empty,
            Checked = enabled,
            Enabled = available,
            Anchor = AnchorStyles.None,
            Margin = Padding.Empty,
            Size = new Size(ScaleLogical(42), ScaleLogical(23))
        };
        toggle.CheckedChanged += async (_, _) =>
        {
            if (toggle.Checked == snapshot.EffectiveEnabled || !toggle.Enabled) return;
            await SetLocalOnlyModeEnabledAsync(toggle.Checked);
        };
        layout.Controls.Add(toggle, 4, 0);
        layout.SetRowSpan(toggle, 3);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildLocalOnlyShieldTile(bool enabled)
    {
        var tile = new RoundedPanel
        {
            Name = "RoutingLocalOnlyModeShieldTile",
            Size = new Size(ScaleLogical(28), ScaleLogical(28)),
            Anchor = AnchorStyles.None,
            BackColor = enabled ? Color.FromArgb(58, 53, 50) : ClipCordTheme.SurfaceControl,
            BorderColor = enabled ? Color.FromArgb(224, 151, 54) : ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(8),
            Padding = new Padding(ScaleLogical(6)),
            Margin = new Padding(0, 0, ScaleLogical(12), 0),
            TabStop = false
        };
        tile.Controls.Add(new FigmaIconControl
        {
            Name = "RoutingLocalOnlyModeShieldIcon",
            Asset = FigmaIconAsset.Shield,
            IconColor = enabled ? Color.FromArgb(224, 151, 54) : ClipCordTheme.TextSecondary,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        });
        return tile;
    }

    private Control BuildLocalOnlyShortcutHost(RoutingLocalOnlyModeViewSnapshot snapshot)
    {
        var host = new BufferedTableLayoutPanel
        {
            Name = "LocalOnlyModeShortcutHost",
            AccessibleName = "Local-only mode shortcut",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = new Padding(ScaleLogical(6), 0, ScaleLogical(6), 0),
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var keycaps = BuildShortcutKeycaps(snapshot.HotkeyDisplayText);
        keycaps.Visible = !_recordingLocalOnlyHotkey;
        host.Controls.Add(keycaps, 0, 0);

        var shortcutField = new TextBox
        {
            Name = "LocalOnlyModeShortcutField",
            AccessibleName = _recordingLocalOnlyHotkey
                ? "Record Local-only mode shortcut"
                : "Local-only mode shortcut",
            Text = _recordingLocalOnlyHotkey
                ? "Press a shortcut…"
                : snapshot.HotkeyDisplayText,
            ReadOnly = true,
            ShortcutsEnabled = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = ClipCordTheme.SurfaceControl,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.25f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, ScaleLogical(8), 0, ScaleLogical(8)),
            Visible = _recordingLocalOnlyHotkey,
            TabStop = _recordingLocalOnlyHotkey
        };
        shortcutField.KeyDown += LocalOnlyShortcutKeyDown;
        host.Controls.Add(shortcutField, 0, 0);
        if (_recordingLocalOnlyHotkey) shortcutField.BringToFront();
        return host;
    }

    private Control BuildShortcutKeycaps(string hotkeyDisplayText)
    {
        var display = new FlowLayoutPanel
        {
            Name = "LocalOnlyModeShortcutKeycaps",
            AccessibleName = string.IsNullOrWhiteSpace(hotkeyDisplayText)
                ? "Shortcut off"
                : hotkeyDisplayText,
            AccessibleRole = AccessibleRole.StaticText,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
            TabStop = false
        };
        var parts = string.IsNullOrWhiteSpace(hotkeyDisplayText)
            ? ["Off"]
            : hotkeyDisplayText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            display.Controls.Add(CreateShortcutKeycap(part));
        }
        return display;
    }

    private Control CreateShortcutKeycap(string text)
    {
        var width = Math.Max(ScaleLogical(20), ScaleLogical(14 + (text.Length * 5)));
        var keycap = new RoundedPanel
        {
            Name = $"LocalOnlyModeKeycap_{text}",
            AccessibleName = text,
            Size = new Size(width, ScaleLogical(19)),
            BackColor = ClipCordTheme.SurfaceControl,
            BorderColor = Color.FromArgb(108, 93, 70),
            CornerRadius = ScaleLogical(5),
            Margin = new Padding(0, 0, ScaleLogical(4), 0),
            Padding = Padding.Empty,
            TabStop = false
        };
        keycap.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(7.75f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false,
            TabStop = false
        });
        return keycap;
    }

    private Control BuildLocalOnlyMigrationNotice()
    {
        var notice = new RoundedPanel
        {
            Name = "LocalOnlyShortcutMigrationNotice",
            AccessibleName = "Local-only shortcut update",
            Dock = DockStyle.Top,
            Height = ScaleLogical(58),
            BackColor = Color.FromArgb(24, 42, 57),
            BorderColor = Color.FromArgb(55, 92, 119),
            CornerRadius = ScaleLogical(9),
            Margin = new Padding(0, 0, 0, ScaleLogical(12)),
            Padding = new Padding(ScaleLogical(14), ScaleLogical(8), ScaleLogical(10), ScaleLogical(8))
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(82)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var migratedHotkey = _localOnlyModeSnapshot?.HotkeyDisplayText;
        layout.Controls.Add(new Label
        {
            Name = "LocalOnlyShortcutMigrationTitleLabel",
            Text = string.IsNullOrWhiteSpace(migratedHotkey)
                ? "Your Local-only shortcut is currently disabled. Choose Change shortcut to add one."
                : $"Your existing {migratedHotkey} shortcut now controls Local-only mode",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(190, 218, 237),
            Font = ClipCordTheme.InterfaceFont(8.75f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "It applies to future clips only.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty
        }, 0, 1);
        var dismiss = CreateSmallButton("Got it", 70);
        dismiss.Name = "DismissLocalOnlyShortcutMigrationButton";
        dismiss.Anchor = AnchorStyles.None;
        dismiss.Enabled = !_localOnlyModeBusy;
        dismiss.Click += async (_, _) => await DismissLocalOnlyMigrationNoticeAsync();
        layout.Controls.Add(dismiss, 1, 0);
        layout.SetRowSpan(dismiss, 2);
        notice.Controls.Add(layout);
        return notice;
    }

    private Control BuildCutoverNotice()
    {
        var active = _runtimeState == RoutesRuntimeViewState.Active && _cutoverCommitted;
        var attention = _runtimeState is RoutesRuntimeViewState.RecoveryNeeded or
            RoutesRuntimeViewState.Blocked ||
            (_runtimeState == RoutesRuntimeViewState.Active && !_cutoverCommitted);
        var notice = new RoundedPanel
        {
            Name = "RoutingCutoverNotice",
            Dock = DockStyle.Top,
            Height = ScaleLogical(54),
            BackColor = active
                ? Color.FromArgb(23, 43, 56)
                : attention ? Color.FromArgb(55, 31, 34) : Color.FromArgb(52, 39, 24),
            BorderColor = active
                ? Color.FromArgb(55, 92, 119)
                : attention ? Color.FromArgb(143, 65, 71) : Color.FromArgb(137, 94, 36),
            CornerRadius = ScaleLogical(9),
            Margin = new Padding(0, 0, 0, ScaleLogical(18)),
            Padding = new Padding(ScaleLogical(14), 0, ScaleLogical(14), 0)
        };
        notice.Controls.Add(new Label
        {
            Name = "RoutingCutoverStatusLabel",
            Text = DescribeRuntimeStatus(),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = active
                ? Color.FromArgb(181, 211, 231)
                : attention ? Color.FromArgb(244, 174, 180) : Color.FromArgb(236, 201, 146),
            Font = ClipCordTheme.InterfaceFont(8.75f, FontStyle.Bold),
            AutoEllipsis = true,
            Margin = Padding.Empty
        });
        return notice;
    }

    private Control BuildSectionHeader(string title, string detail)
    {
        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(27),
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, ScaleLogical(2), 0, ScaleLogical(8)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = detail,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty
        }, 1, 0);
        return header;
    }

    private Control BuildRouteCard(
        RoutingRoute route,
        IReadOnlyList<RoutingRoute> orderedRoutes)
    {
        var displayedActions = route.Actions.Count(action => action.Enabled);
        var actionRowsLogicalHeight = Math.Max(27, Math.Max(1, displayedActions) * 28);
        var card = new RoundedPanel
        {
            Name = $"RouteCard_{route.RouteId:N}",
            Dock = DockStyle.Top,
            Height = ScaleLogical(31 + 27 + actionRowsLogicalHeight + 24),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(12), ScaleLogical(12), ScaleLogical(12)),
            AccessibleName = route.Name
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(242)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(31)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(27)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(actionRowsLogicalHeight)));

        var name = new Label
        {
            Text = route.Name,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = route.Enabled ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        layout.Controls.Add(name, 0, 0);
        var actions = BuildRouteActions(route, orderedRoutes);
        layout.Controls.Add(actions, 1, 0);
        layout.SetRowSpan(actions, 3);
        layout.Controls.Add(new Label
        {
            Text = $"WHEN  {DescribeTrigger(route)}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 1);
        layout.Controls.Add(BuildRouteActionRows(route), 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildRouteActionRows(RoutingRoute route)
    {
        var actions = route.Actions.Where(action => action.Enabled).ToArray();
        var rows = Math.Max(1, actions.Length);
        var host = new BufferedTableLayoutPanel
        {
            Name = $"RouteActionRows_{route.RouteId:N}",
            AccessibleName = $"{route.Name} actions",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(50)));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < rows; index++)
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(28)));
        host.Controls.Add(new Label
        {
            Text = "THEN",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false,
            TabStop = false
        }, 0, 0);
        if (actions.Length == 0)
        {
            host.Controls.Add(new Label
            {
                Name = $"RouteActionEmpty_{route.RouteId:N}",
                Text = "No enabled actions",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ClipCordTheme.TextTertiary,
                Font = ClipCordTheme.InterfaceFont(8.25f),
                Margin = Padding.Empty,
                UseMnemonic = false,
                TabStop = false
            }, 1, 0);
            return host;
        }
        for (var index = 0; index < actions.Length; index++)
            host.Controls.Add(BuildRouteActionRow(route, actions[index]), 1, index);
        return host;
    }

    private Control BuildRouteActionRow(RoutingRoute route, RoutingAction action)
    {
        var paused = route.Enabled &&
                     action.Kind == RoutingActionKind.Deliver &&
                     _localOnlyModeSnapshot?.EffectiveEnabled == true;
        var fileIntoLibrary = action.Kind == RoutingActionKind.FileIntoLibrary;
        var row = new RoundedPanel
        {
            Name = $"RouteAction_{action.ActionId:N}",
            AccessibleName = DescribeAction(action),
            AccessibleDescription = paused
                ? "Paused by Local-only mode"
                : fileIntoLibrary
                    ? "File into Library remains active"
                    : "Delivery action is active",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(7),
            Margin = new Padding(0, 1, 0, 1),
            Padding = new Padding(ScaleLogical(7), 0, ScaleLogical(7), 0),
            TabStop = false
        };
        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(22)));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(
            SizeType.Absolute,
            ScaleLogical(paused ? 154 : 50)));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new FigmaIconControl
        {
            Name = $"RouteActionIcon_{action.ActionId:N}",
            Asset = fileIntoLibrary
                ? FigmaIconAsset.Disk
                : action.Destination switch
                {
                    RoutingDestinationKind.Discord => FigmaIconAsset.Discord,
                    RoutingDestinationKind.YouTube => FigmaIconAsset.YouTube,
                    RoutingDestinationKind.TikTok => FigmaIconAsset.TikTok,
                    _ => FigmaIconAsset.Upload
                },
            IconColor = paused ? ClipCordTheme.TextTertiary : ClipCordTheme.TextSecondary,
            Dock = DockStyle.Fill,
            Padding = new Padding(ScaleLogical(4)),
            Margin = Padding.Empty
        }, 0, 0);
        content.Controls.Add(new Label
        {
            Name = $"RouteActionLabel_{action.ActionId:N}",
            Text = DescribeAction(action),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = paused ? ClipCordTheme.TextTertiary : ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty,
            UseMnemonic = false,
            TabStop = false
        }, 1, 0);
        var badge = new RoundedPanel
        {
            Name = paused
                ? $"RouteActionPausedBadge_{action.ActionId:N}"
                : $"RouteActionActiveBadge_{action.ActionId:N}",
            AccessibleName = paused ? "Paused by Local-only mode" : "Auto",
            Size = new Size(
                ScaleLogical(paused ? 147 : 43),
                ScaleLogical(paused ? 18 : 16)),
            Anchor = AnchorStyles.None,
            BackColor = paused ? Color.FromArgb(58, 53, 50) : ClipCordTheme.SurfaceControl,
            BorderColor = paused ? Color.FromArgb(224, 151, 54) : ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(6),
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false
        };
        badge.Controls.Add(new Label
        {
            Text = paused ? "Paused by Local-only mode" : fileIntoLibrary ? "AUTO" : "ACTIVE",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = paused ? Color.FromArgb(224, 151, 54) : ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(7.25f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false,
            TabStop = false
        });
        content.Controls.Add(badge, 2, 0);
        row.Controls.Add(content);
        return row;
    }

    private static string DescribeAction(RoutingAction action) => action.Kind switch
    {
        RoutingActionKind.FileIntoLibrary => action.LibraryArea == RoutingLibraryArea.LocalOnly
            ? "File into Library · Local only"
            : "File into Library · Uploaded",
        RoutingActionKind.Deliver =>
            $"{action.Destination} · {action.OutputRef} · {action.Mode}",
        _ => action.Kind.ToString()
    };

    private Control BuildRouteActions(
        RoutingRoute route,
        IReadOnlyList<RoutingRoute> orderedRoutes)
    {
        var editable = _runtimeState == RoutesRuntimeViewState.Active &&
                       _cutoverCommitted &&
                       route.Source == RoutingRouteSource.User;
        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = new Padding(0, 1, 0, 0),
            BackColor = Color.Transparent
        };
        var enabled = CreateSmallButton(
            route.Source == RoutingRouteSource.Migration
                ? "Managed"
                : route.Enabled
                    ? "On"
                    : "Off",
            route.Source == RoutingRouteSource.Migration ? 72 : 54);
        enabled.Name = $"RouteEnabled_{route.RouteId:N}";
        enabled.AccessibleRole = AccessibleRole.CheckButton;
        enabled.AccessibleName = $"{(route.Enabled ? "Disable" : "Enable")} {route.Name}";
        enabled.AccessibleDescription = route.Source == RoutingRouteSource.Migration
            ? "This fallback is pinned to the committed legacy cutover."
            : !editable
                ? "Route editing is unavailable until Routes is active."
                : null;
        enabled.Enabled = editable;
        enabled.ForeColor = route.Enabled ? Color.FromArgb(76, 210, 145) : ClipCordTheme.TextSecondary;
        enabled.Click += async (_, _) => await RunRouteCommandAsync(
            () => _routeManager.SetEnabledAsync(route.RouteId, !route.Enabled));
        host.Controls.Add(enabled);
        var remove = CreateSmallButton("Delete", 66);
        remove.Name = $"DeleteRoute_{route.RouteId:N}";
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(
                    this,
                    $"Delete ‘{route.Name}’? Existing delivery plans keep their frozen settings.",
                    "Delete route",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return;
            await RunRouteCommandAsync(() => _routeManager.DeleteAsync(route.RouteId));
        };
        remove.Enabled = editable;
        remove.AccessibleDescription = editable
            ? null
            : "The route cannot be deleted before safe cutover or when it preserves legacy behavior.";
        host.Controls.Add(remove);
        var sorted = orderedRoutes.OrderBy(item => item.Priority).ToArray();
        var index = Array.FindIndex(sorted, item => item.RouteId == route.RouteId);
        var previousIsEditable = index > 0 &&
                                 sorted[index - 1].Source == RoutingRouteSource.User;
        var nextIsEditable = index >= 0 && index < sorted.Length - 1 &&
                             sorted[index + 1].Source == RoutingRouteSource.User;
        var down = CreateSmallButton("↓", 34);
        down.Name = $"MoveRouteDown_{route.RouteId:N}";
        down.Enabled = editable && nextIsEditable;
        down.AccessibleName = $"Move {route.Name} down";
        down.Click += async (_, _) => await RunRouteCommandAsync(
            () => _routeManager.MoveAsync(route.RouteId, 1));
        host.Controls.Add(down);
        var up = CreateSmallButton("↑", 34);
        up.Name = $"MoveRouteUp_{route.RouteId:N}";
        up.Enabled = editable && previousIsEditable;
        up.AccessibleName = $"Move {route.Name} up";
        up.Click += async (_, _) => await RunRouteCommandAsync(
            () => _routeManager.MoveAsync(route.RouteId, -1));
        host.Controls.Add(up);
        return host;
    }

    private Control BuildEmptyState() => BuildInlineEmpty(
        _runtimeState == RoutesRuntimeViewState.Active && _cutoverCommitted
            ? "Build your first route"
            : _runtimeState == RoutesRuntimeViewState.Activating
                ? "Routes are starting"
                : _runtimeState == RoutesRuntimeViewState.LegacySetupNeeded
                    ? "Finish ClipCord setup"
                : _runtimeState == RoutesRuntimeViewState.LegacyActive
                    ? "Safe migration is deferred"
                    : "Routes need attention",
        _runtimeState == RoutesRuntimeViewState.Active && _cutoverCommitted
            ? "Choose what ClipCord should do when a new clip arrives."
            : _runtimeState == RoutesRuntimeViewState.Activating
                ? "ClipCord is recovering local work before it transfers processing authority."
                : _runtimeState == RoutesRuntimeViewState.LegacySetupNeeded
                    ? "Choose the watched folder, capture source, and Discord destination in Settings first."
                : _runtimeState == RoutesRuntimeViewState.LegacyActive
                    ? "Your existing watcher still handles every clip. Retry when you are ready."
                    : "Clip processing remains paused until Routing recovery succeeds.");

    private Control BuildUnavailableState(RoutingDocumentLoadStatus status) => BuildInlineEmpty(
        "Routes need attention",
        $"ClipCord did not modify the routing document because it is {status}. Restore or remove the invalid document before continuing.");

    private Control BuildInlineEmpty(string title, string detail)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(86),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(16)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(13), ScaleLogical(16), ScaleLogical(12))
        };
        var text = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        text.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(27)));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        text.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 0);
        text.Controls.Add(new Label
        {
            Text = detail,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.75f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 1);
        card.Controls.Add(text);
        return card;
    }

    private Control BuildConnectionCard(RoutingConnectionDisplay connection)
    {
        var card = new RoundedPanel
        {
            Name = $"ConnectionCard_{connection.ConnectionId}",
            Dock = DockStyle.Top,
            Height = ScaleLogical(94),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = new Padding(ScaleLogical(16), ScaleLogical(13), ScaleLogical(16), ScaleLogical(12))
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(42)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(92)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.Discord,
            IconColor = Color.FromArgb(176, 128, 255),
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 6, 10, 6)
        }, 0, 0);
        layout.SetRowSpan(layout.GetControlFromPosition(0, 0)!, 2);
        layout.Controls.Add(new Label
        {
            Text = connection.Name,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 1, 0);
        layout.Controls.Add(new Label
        {
            Text = connection.Detail,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.75f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 1, 1);
        var status = new Label
        {
            Text = connection.Available ? "READY" : "NEEDS ATTENTION",
            Dock = DockStyle.Fill,
            ForeColor = connection.Available ? Color.FromArgb(76, 210, 145) : Color.FromArgb(224, 151, 54),
            Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty
        };
        layout.Controls.Add(status, 2, 0);
        if (_connections is IRoutingConnectionManagerViewSource manager)
        {
            var remove = CreateSmallButton("Remove", 76);
            remove.Name = $"RemoveConnection_{connection.ConnectionId}";
            remove.Dock = DockStyle.None;
            remove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(
                        this,
                        $"Remove ‘{connection.Name}’? Routes and pending deliveries must be moved first.",
                        "Remove Discord connection",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning) != DialogResult.OK) return;
                await RunConnectionCommandAsync(
                    () => manager.RemoveDiscordAsync(connection.ConnectionId));
            };
            layout.Controls.Add(remove, 2, 1);
        }
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildConnectionToolbar()
    {
        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(40),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        var add = CreateSmallButton("+  Add Discord", 124);
        add.Name = "AddDiscordConnectionButton";
        add.Click += async (_, _) => await AddDiscordConnectionAsync();
        host.Controls.Add(add);
        return host;
    }

    private Control BuildConnectorRoadmap()
    {
        var card = BuildInlineEmpty(
            "More destinations are staged for ClipCord 2.0",
            "YouTube and TikTok will use account-based connectors. Discord is the first end-to-end route slice.");
        card.Name = "FutureConnectorsCard";
        return card;
    }

    internal async Task AddRouteAsync()
    {
        if (!CanAdmitRouteCommand()) return;
        var connections = SafeLoadConnections().Where(item => item.Available).ToArray();
        _editorInputSources = await PrepareInputSourcesAsync().ConfigureAwait(true);
        var draft = _editRoute(connections);
        if (draft is null) return;
        var added = await RunRouteCommandAsync(() => _routeManager.AddAsync(draft));
        if (!added || draft.WatchedSourceId is not { } sourceId) return;
        if (_inputSources is null ||
            !await _inputSources.EnableAsync(sourceId).ConfigureAwait(true))
        {
            _showInputSourceMessage(
                "The route was saved, but ClipCord kept its clip source off because the folder or its safe new-clips baseline needs attention. Open Routes → Connections to repair it; no clip will fall through to another route while the source is off.",
                "Clip source needs attention");
        }
        Reload();
    }

    private RoutingRouteDraft? ShowRouteEditor(
        IReadOnlyList<RoutingConnectionDisplay> connections)
    {
        var owner = FindForm();
        using var dialog = new RouteEditorDialog(
            connections,
            owner?.DeviceDpi,
            _editorInputSources,
            _inputSources is null
                ? null
                : (source, policy, cancellationToken) =>
                    _inputSources.Preflight(source, policy, cancellationToken),
            migratedInputSource: _migratedInputSource);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Draft : null;
    }

    private async Task<IReadOnlyList<RoutingInputSourceDisplay>> PrepareInputSourcesAsync()
    {
        if (_inputSources is null) return [];
        try
        {
            _ = await _inputSources.PrepareDefaultXboxSourceAsync().ConfigureAwait(true);
            var snapshot = _inputSources.InspectInputSources();
            return snapshot.IsUsable ? snapshot.Sources.ToArray() : [];
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not prepare its Xbox Routing source.", exception);
            _showInputSourceMessage(
                "ClipCord could not prepare the currently discovered Xbox source. " +
                "No source was changed. Open Connections to find the moved folder or register a replacement; existing routes and approved history remain bound to the replaced source.",
                "Xbox source not available");
            try
            {
                var existing = _inputSources.InspectInputSources();
                return existing.IsUsable ? existing.Sources.ToArray() : [];
            }
            catch (Exception inspectionException) when (
                inspectionException is InvalidDataException or InvalidOperationException or
                    IOException or UnauthorizedAccessException)
            {
                Log.Error(
                    "ClipCord could not inspect other Routing sources after Xbox preparation failed.",
                    inspectionException);
                return [];
            }
        }
    }

    private async Task AddDiscordConnectionAsync()
    {
        if (_busy || _connections is not IRoutingConnectionManagerViewSource manager) return;
        using var dialog = new DiscordConnectionDialog();
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var name = dialog.DisplayName;
        var webhook = dialog.WebhookUrl;
        try
        {
            await RunConnectionCommandAsync(() => manager.AddDiscordAsync(name, webhook));
        }
        finally
        {
            dialog.ClearSecret();
            webhook = string.Empty;
        }
    }

    private async Task RunConnectionCommandAsync(
        Func<Task<DiscordConnectionMutationResult>> command)
    {
        if (_busy) return;
        _busy = true;
        _newRouteButton.Enabled = false;
        try
        {
            var result = await command();
            if (!result.Succeeded)
            {
                MessageBox.Show(this, result.Reason, "Discord connection needs attention",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Reload();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException or CryptographicException)
        {
            Log.Error("A Discord connection command failed.", exception);
            MessageBox.Show(this, "ClipCord could not update the encrypted Discord connection.",
                "Discord connection needs attention", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    internal async Task<bool> RunRouteCommandAsync(Func<Task> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanAdmitRouteCommand()) return false;
        _busy = true;
        _newRouteButton.Enabled = false;
        try
        {
            await command();
            Reload();
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("A routing command could not be completed.", exception);
            MessageBox.Show(
                this,
                exception.Message,
                "Routes need attention",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private async Task AddInputSourceAsync()
    {
        if (_busy || _inputSources is null) return;
        var draft = _createInputSourceDraft();
        if (draft is null) return;
        var result = await RunInputSourceCommandAsync(
            () => _inputSources.RegisterAsync(
                draft.DisplayName,
                draft.Kind,
                draft.CanonicalRoot,
                enabled: false),
            showReplacementGuidance: false);
        if (result?.Succeeded == true)
        {
            _showInputSourceMessage(
                "Source added. Existing clips were left as your baseline. Create a route to start watching for new clips from now on.",
                "Clip source added");
        }
    }

    private RoutingInputSourceRegistrationDraft? ShowInputSourceDialog()
    {
        using var dialog = new InputSourceConnectionDialog(
            layoutDpi: FindForm()?.DeviceDpi ?? DeviceDpi);
        return dialog.ShowDialog(FindForm()) == DialogResult.OK ? dialog.Draft : null;
    }

    private RoutingLocalOnlyModeViewSnapshot SafeInspectLocalOnlyMode()
    {
        if (_localOnlyMode is null)
        {
            return new RoutingLocalOnlyModeViewSnapshot(
                IsAvailable: false,
                EffectiveEnabled: false,
                HotkeyDisplayText: string.Empty,
                FirstRunNoticeDismissed: true,
                StatusDetail: "Local-only mode becomes available after Routes is active.");
        }
        try
        {
            return _localOnlyMode.Inspect();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not inspect the Routing Local-only override.", exception);
            return new RoutingLocalOnlyModeViewSnapshot(
                IsAvailable: false,
                EffectiveEnabled: true,
                HotkeyDisplayText: string.Empty,
                FirstRunNoticeDismissed: false,
                StatusDetail: "Saved Local-only mode needs attention. External delivery remains paused.");
        }
    }

    private async Task SetLocalOnlyModeEnabledAsync(bool enabled)
    {
        if (_localOnlyMode is null || _localOnlyModeBusy) return;
        _localOnlyModeBusy = true;
        _localOnlyModeTransientError = null;
        try
        {
            var result = await _localOnlyMode.SetEnabledAsync(enabled).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _localOnlyModeTransientError = result.Error ??
                    "ClipCord could not save Local-only mode.";
            }
            else
            {
                LocalOnlyModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not change Routing Local-only mode.", exception);
            _localOnlyModeTransientError =
                "ClipCord could not save Local-only mode. Existing routing is unchanged.";
        }
        finally
        {
            _localOnlyModeBusy = false;
            Reload();
        }
    }

    private async Task SetLocalOnlyHotkeyAsync(string hotkeyDisplayText)
    {
        if (_localOnlyMode is null || _localOnlyModeBusy) return;
        _localOnlyModeBusy = true;
        _localOnlyModeTransientError = null;
        try
        {
            var result = await _localOnlyMode.SetHotkeyAsync(hotkeyDisplayText)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _localOnlyModeTransientError = result.Error ??
                    "ClipCord kept the previous Local-only mode shortcut.";
                _recordingLocalOnlyHotkey = result.HotkeyConflict;
            }
            else
            {
                _recordingLocalOnlyHotkey = false;
                LocalOnlyModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not change the Routing Local-only shortcut.", exception);
            _localOnlyModeTransientError =
                "ClipCord kept the previous Local-only mode shortcut.";
            _recordingLocalOnlyHotkey = false;
        }
        finally
        {
            _localOnlyModeBusy = false;
            Reload();
            if (_recordingLocalOnlyHotkey)
            {
                BeginInvoke((Action)(() =>
                    Controls.Find("LocalOnlyModeShortcutField", searchAllChildren: true)
                        .FirstOrDefault()?.Focus()));
            }
        }
    }

    private async Task DismissLocalOnlyMigrationNoticeAsync()
    {
        if (_localOnlyMode is null || _localOnlyModeBusy) return;
        _localOnlyModeBusy = true;
        try
        {
            var result = await _localOnlyMode.DismissFirstRunNoticeAsync()
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _localOnlyModeTransientError = result.Error ??
                    "ClipCord could not dismiss this notice yet.";
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not dismiss the Local-only shortcut notice.", exception);
            _localOnlyModeTransientError = "ClipCord could not dismiss this notice yet.";
        }
        finally
        {
            _localOnlyModeBusy = false;
            Reload();
        }
    }

    private async void LocalOnlyShortcutKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (!_recordingLocalOnlyHotkey || _localOnlyModeBusy) return;
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        if (eventArgs.KeyCode == Keys.Escape)
        {
            _recordingLocalOnlyHotkey = false;
            _localOnlyModeTransientError = null;
            Reload();
            return;
        }
        if (eventArgs.KeyCode == Keys.Back)
        {
            await SetLocalOnlyHotkeyAsync(string.Empty);
            return;
        }
        if (!GlobalHotkeyBinding.TryFromKeyData(eventArgs.KeyData, out var binding))
        {
            _localOnlyModeTransientError =
                "Use Ctrl or Alt with a letter, number, or function key.";
            Reload();
            _recordingLocalOnlyHotkey = true;
            BeginInvoke((Action)(() =>
                Controls.Find("LocalOnlyModeShortcutField", searchAllChildren: true)
                    .FirstOrDefault()?.Focus()));
            return;
        }
        await SetLocalOnlyHotkeyAsync(binding.DisplayText);
    }

    private async Task RelocateInputSourceAsync(RoutingInputSourceDisplay source)
    {
        string? candidate;
        if (source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive)
        {
            try
            {
                candidate = _discoverDefaultXboxRoot();
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException or
                    InvalidOperationException or NotSupportedException)
            {
                candidate = null;
            }
            if (string.IsNullOrWhiteSpace(candidate))
            {
                _showInputSourceMessage(
                    "ClipCord could not find an available Xbox Game DVR folder. No source was changed. Confirm OneDrive is running, then try again.",
                    "Moved folder not found");
                return;
            }
        }
        else
        {
            using var picker = new FolderBrowserDialog
            {
                Description = source.Kind == RoutingInputSourceKind.SteelSeriesGg
                    ? "Choose a replacement SteelSeries GG folder. It will be registered as a separate source."
                    : "Choose a replacement NVIDIA folder. It will be registered as a separate source.",
                SelectedPath = Directory.Exists(source.CanonicalRoot)
                    ? source.CanonicalRoot
                    : string.Empty,
                ShowNewFolderButton = false,
                UseDescriptionForTitle = true
            };
            if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
            candidate = picker.SelectedPath;
        }
        if (CanonicalPathsEqual(candidate, source.CanonicalRoot))
        {
            _showInputSourceMessage(
                source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "The currently discovered Xbox folder is the same saved location. No source was changed. Use Recheck after OneDrive is available."
                    : "That is already this source's saved folder. Reconnect it, then use Recheck; no replacement was registered.",
                source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive
                    ? "No moved folder found"
                    : "Same folder selected");
            return;
        }
        if (source.Kind is RoutingInputSourceKind.SteelSeriesGg or
            RoutingInputSourceKind.Nvidia)
        {
            await RegisterInputSourceReplacementAsync(source, candidate);
            return;
        }
        var result = await RunInputSourceCommandAsync(
            () => _inputSources!.RelocateAsync(
                source.SourceId,
                source.Revision,
                candidate),
            showReplacementGuidance: false);
        if (result?.Status == RoutingInputSourceViewActionStatus.ReplacementRequired)
            await RegisterInputSourceReplacementAsync(
                result.Source ?? source,
                candidate);
    }

    private async Task RegisterInputSourceReplacementAsync(
        RoutingInputSourceDisplay source,
        string? candidateCanonicalRoot = null)
    {
        var xbox = source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive;
        var decision = _confirmInputSourceReplacement(
            xbox
                ? "Register the different Xbox source as a replacement?\n\n" +
                  "Existing routes and approved history remain bound to the replaced source. " +
                  "The replacement starts separately, so create a new route and approve its history before ClipCord processes it."
                : "Register the different recorder folder as a replacement?\n\n" +
                  "Cancel and reconnect the original folder at its saved location if you want to restore this source. " +
                  "Continuing retires it: its routes, from-now baseline, and pending work stay bound to it. " +
                  "The replacement starts disabled with a new safe baseline and inherits none of them, so create a new route before ClipCord watches it.",
            "Register replacement source");
        if (decision != DialogResult.OK) return;
        await RunInputSourceCommandAsync(
            () => _inputSources!.RegisterReplacementAsync(
                source.SourceId,
                source.Revision,
                candidateCanonicalRoot ?? source.CanonicalRoot));
    }

    private async Task SkipBlockedInputSourceClipsAsync(
        RoutingInputSourceDisplay source)
    {
        var count = source.SkippableBlockedClipCount;
        if (count <= 0) return;
        var noun = count == 1 ? "clip" : "clips";
        var decision = _confirmInputSourceSkip(
            $"Skip {count} blocked {noun} for this Xbox source?\n\n" +
            $"The OneDrive {(count == 1 ? "original remains" : "originals remain")} untouched. " +
            $"The failed {(count == 1 ? "clip will" : "clips will")} not be routed, " +
            "and future clips resume only after every blocked item has been resolved.",
            "Skip blocked clips");
        if (decision != DialogResult.OK) return;
        var result = await RunInputSourceCommandAsync(
            () => _inputSources!.SkipBlockedOccurrencesAsync(
                source.SourceId,
                source.Revision));
        if (result?.Succeeded == true)
        {
            _showInputSourceMessage(
                result.AffectedCount == 0
                    ? result.Reason
                    : result.AffectedCount == 1
                    ? "1 blocked clip was skipped. Its OneDrive original remains untouched, and it will not be routed. Future clips can resume."
                    : $"{result.AffectedCount} blocked clips were skipped. Their OneDrive originals remain untouched, and they will not be routed. Future clips can resume.",
                "Blocked clips skipped");
        }
    }

    private async Task RetryBlockedInputSourceClipsAsync(
        RoutingInputSourceDisplay source)
    {
        var count = source.SkippableBlockedClipCount;
        if (count <= 0) return;
        var result = await RunInputSourceCommandAsync(
            () => _inputSources!.RetryBlockedOccurrencesAsync(
                source.SourceId,
                source.Revision));
        if (result?.Succeeded == true)
        {
            _showInputSourceMessage(
                result.AffectedCount == 0
                    ? result.Reason
                    : result.AffectedCount == 1
                        ? "1 blocked clip was queued for safe retry. Its OneDrive original remains untouched, and ClipCord will resume it before later clips."
                        : $"{result.AffectedCount} blocked clips were queued for safe retry. Their OneDrive originals remain untouched, and ClipCord will resume them before later clips.",
                "Blocked clips queued");
        }
    }

    private async Task ResumeBlockedInputSourceRetryAsync(
        RoutingInputSourceDisplay source)
    {
        if (!source.BlockedClipRetryPending) return;
        var result = await RunInputSourceCommandAsync(
            () => _inputSources!.RetryBlockedOccurrencesAsync(
                source.SourceId,
                source.Revision));
        if (result?.Succeeded == true)
        {
            _showInputSourceMessage(
                result.Reason,
                "Xbox retry resumed");
        }
    }

    private async Task ResumeBlockedInputSourceAsync(
        RoutingInputSourceDisplay source)
    {
        if (!source.BlockedClipRecoveryPending) return;
        var result = await RunInputSourceCommandAsync(
            () => _inputSources!.SkipBlockedOccurrencesAsync(
                source.SourceId,
                source.Revision));
        if (result?.Succeeded == true)
        {
            _showInputSourceMessage(
                result.Reason,
                "Xbox source resumed");
        }
    }

    private static bool CanonicalPathsEqual(string left, string right)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
                NotSupportedException)
        {
            return false;
        }
    }

    private async Task<RoutingInputSourceViewActionResult?> RunInputSourceCommandAsync(
        Func<Task<RoutingInputSourceViewActionResult>> command,
        bool showReplacementGuidance = true)
    {
        if (_busy) return null;
        _busy = true;
        ConfigureHeaderAction();
        RoutingInputSourceViewActionResult? outcome = null;
        try
        {
            outcome = await command().ConfigureAwait(true);
            if (!outcome.Succeeded &&
                (showReplacementGuidance || outcome.Status !=
                    RoutingInputSourceViewActionStatus.ReplacementRequired))
            {
                _showInputSourceMessage(
                    outcome.Status == RoutingInputSourceViewActionStatus.ReplacementRequired
                        ? "ClipCord verified a different native clip source. It cannot inherit existing routes or approved history, and it cannot reuse the old new-clips baseline. Register a replacement only if you want a separate source, then create a new route for it."
                        : outcome.Reason,
                    outcome.Status == RoutingInputSourceViewActionStatus.ReplacementRequired
                        ? "Replacement requires approval"
                        : "Clip source needs attention");
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Error("The clip-source command could not finish.", exception);
            _showInputSourceMessage(
                "ClipCord could not safely update that source. Its saved state was not assumed to have changed.",
                "Clip source needs attention");
        }
        finally
        {
            _busy = false;
            Reload();
        }
        return outcome;
    }

    private bool CanAdmitRouteCommand()
    {
        if (_busy) return false;
        var cutoverCommitted = ReadCutoverStatus();
        var runtimeState = ReadRuntimeState(cutoverCommitted);
        return cutoverCommitted && runtimeState == RoutesRuntimeViewState.Active;
    }

    private void ConfigureHeaderAction()
    {
        var showRouteActions = !_showConnections;
        _newRouteButton.Visible = showRouteActions;
        _localOnlyStatusPill.Visible = showRouteActions &&
                                           _runtimeState == RoutesRuntimeViewState.Active &&
                                           _cutoverCommitted &&
                                           _localOnlyModeSnapshot?.EffectiveEnabled == true;
        _headerActions.Visible = showRouteActions;
        if (_runtimeState == RoutesRuntimeViewState.Active && _cutoverCommitted)
        {
            _newRouteButton.Text = "+  New route";
            _newRouteButton.Size = new Size(ScaleLogical(114), ScaleLogical(33));
            _newRouteButton.AccessibleName = "Create a new routing rule";
            _newRouteButton.AccessibleDescription =
                "Create a routing rule for new clips.";
            _newRouteButton.Enabled = !_busy;
            return;
        }
        if (_runtimeState == RoutesRuntimeViewState.Activating)
        {
            _newRouteButton.Text = "Starting…";
            _newRouteButton.Size = new Size(ScaleLogical(136), ScaleLogical(33));
            _newRouteButton.AccessibleName = "Routes are starting";
            _newRouteButton.AccessibleDescription =
                "ClipCord is safely transferring clip processing to Routes.";
            _newRouteButton.Enabled = false;
            return;
        }
        if (_runtimeState == RoutesRuntimeViewState.LegacySetupNeeded)
        {
            _newRouteButton.Text = "Open Settings";
            _newRouteButton.Size = new Size(ScaleLogical(136), ScaleLogical(33));
            _newRouteButton.AccessibleName = "Open ClipCord settings";
            _newRouteButton.AccessibleDescription =
                "Finish the watched-folder and Discord setup before Routes can activate.";
            _newRouteButton.Enabled = !_busy;
            return;
        }
        if (_runtimeState == RoutesRuntimeViewState.LegacyActive &&
            _retryRuntimeAsync is not null)
        {
            _newRouteButton.Text = "Retry activation";
            _newRouteButton.Size = new Size(ScaleLogical(136), ScaleLogical(33));
            _newRouteButton.AccessibleName = "Retry Routes activation";
            _newRouteButton.AccessibleDescription =
                "Retry the safe migration while the existing watcher remains active.";
            _newRouteButton.Enabled = !_busy;
            return;
        }
        if (_runtimeState == RoutesRuntimeViewState.RecoveryNeeded &&
            _retryRuntimeAsync is not null)
        {
            _newRouteButton.Text = "Retry recovery";
            _newRouteButton.Size = new Size(ScaleLogical(136), ScaleLogical(33));
            _newRouteButton.AccessibleName = "Retry Routes recovery";
            _newRouteButton.AccessibleDescription =
                "Retry recovery before clip processing resumes.";
            _newRouteButton.Enabled = !_busy;
            return;
        }
        _newRouteButton.Text = _runtimeState == RoutesRuntimeViewState.Blocked
            ? "Needs attention"
            : "+  New route";
        _newRouteButton.Size = new Size(ScaleLogical(136), ScaleLogical(33));
        _newRouteButton.AccessibleName = _runtimeState == RoutesRuntimeViewState.Blocked
            ? "Routes need attention"
            : "Create a new routing rule";
        _newRouteButton.AccessibleDescription =
            "Route editing unlocks after the safe Routing migration is active.";
        _newRouteButton.Enabled = false;
    }

    private async Task RetryRuntimeAsync()
    {
        if (_busy || _retryRuntimeAsync is null ||
            _runtimeState is not (RoutesRuntimeViewState.LegacyActive or
                RoutesRuntimeViewState.RecoveryNeeded))
        {
            return;
        }
        _busy = true;
        ConfigureHeaderAction();
        try
        {
            _ = await _retryRuntimeAsync();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Error("The user-requested Routes recovery could not finish.", exception);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private RoutesRuntimeViewState ReadRuntimeState(bool cutoverCommitted)
    {
        if (_runtimeStateProvider is null)
            return cutoverCommitted
                ? RoutesRuntimeViewState.Active
                : RoutesRuntimeViewState.LegacyActive;
        try
        {
            var state = _runtimeStateProvider();
            return Enum.IsDefined(state) ? state : RoutesRuntimeViewState.Blocked;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Routes runtime status could not be verified.", exception);
            return RoutesRuntimeViewState.Blocked;
        }
    }

    private string DescribeRuntimeStatus() => _runtimeState switch
    {
        RoutesRuntimeViewState.Active when _cutoverCommitted =>
            _localOnlyModeSnapshot?.EffectiveEnabled == true
                ? "ROUTING ACTIVE · EXTERNAL DELIVERIES PAUSED · Future clips are filed into Library → Local only."
                : "ROUTING ACTIVE · New clips are evaluated by your saved routes. The migrated fallback preserves 1.x behavior.",
        RoutesRuntimeViewState.Activating =>
            "STARTING ROUTES · ClipCord is safely recovering local work before it transfers processing authority.",
        RoutesRuntimeViewState.LegacySetupNeeded =>
            "SETUP REQUIRED · Finish your watched-folder and Discord settings before ClipCord starts processing clips.",
        RoutesRuntimeViewState.LegacyActive =>
            "ROUTING NOT ACTIVE · Your existing watcher remains active. Retry the safe migration when ready.",
        RoutesRuntimeViewState.RecoveryNeeded =>
            "ROUTES NEED ATTENTION · Processing is paused until recovery succeeds. No competing watcher will start.",
        _ =>
            "ROUTES BLOCKED · Processing authority could not be verified. ClipCord will not process clips unsafely."
    };

    private IReadOnlyList<RoutingConnectionDisplay> SafeLoadConnections()
    {
        try
        {
            return _connections.LoadDiscordConnections()
                .Where(item => !string.IsNullOrWhiteSpace(item.ConnectionId))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            Log.Error("Routing connections could not be listed.", exception);
            return [];
        }
    }

    private RoutingInputSourceViewSnapshot SafeInspectInputSources()
    {
        if (_inputSources is null)
        {
            return new RoutingInputSourceViewSnapshot(
                IsUsable: false,
                Sources: [],
                "Clip-source management is unavailable in this build. No saved source state was assumed.");
        }
        try
        {
            var snapshot = _inputSources.InspectInputSources();
            return snapshot.IsUsable
                ? snapshot with
                {
                    Sources = snapshot.Sources
                        .Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
                        .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                }
                : snapshot with { Sources = [] };
        }
        catch (Exception exception)
        {
            Log.Error("Routing input sources could not be listed.", exception);
            return new RoutingInputSourceViewSnapshot(
                IsUsable: false,
                Sources: [],
                "Saved clip-source status could not be read. No source was removed or changed.");
        }
    }

    private bool ReadCutoverStatus()
    {
        try
        {
            return _isCutoverCommitted();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Routing cutover status could not be verified.", exception);
            return false;
        }
    }

    private static Func<bool> CreateCutoverStatusSource(RoutingRouteManager routeManager) =>
        () => routeManager.CanMutate();

    private static string DescribeTrigger(RoutingRoute route)
    {
        var trigger = route.Trigger switch
        {
            RoutingTriggerKind.AnyNewSourceClip => "Any new source clip",
            RoutingTriggerKind.InstantReplay => "Instant Replay saves a clip",
            RoutingTriggerKind.ManualRecording => "Manual Recording finishes",
            RoutingTriggerKind.WatchedFolder => "A watched-folder clip arrives",
            _ => route.Trigger.ToString()
        };
        var conditions = route.Conditions.Select(condition => condition.Field switch
        {
            RoutingConditionField.Game => $"Game is {condition.Value}",
            RoutingConditionField.ReactionCamera => $"Reaction camera is {condition.Value}",
            RoutingConditionField.ClipSource => $"Source is {condition.Value}",
            RoutingConditionField.CaptureType => $"Capture type is {condition.Value}",
            RoutingConditionField.Duration => $"Duration {condition.Operator} {condition.Value} ms",
            RoutingConditionField.SourceConnection => "Source is Xbox · OneDrive",
            RoutingConditionField.CapturedAt => DateTimeOffset.TryParse(
                    condition.Value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var cutoff)
                ? $"Captured after {cutoff.ToLocalTime():MMM d, yyyy h:mm tt}"
                : "Capture history window",
            _ => condition.Value
        }).ToArray();
        return conditions.Length == 0 ? trigger : $"{trigger} · {string.Join(" AND ", conditions)}";
    }

    private static string DescribeActions(RoutingRoute route)
    {
        var actions = route.Actions.Where(action => action.Enabled).Select(action => action.Kind switch
        {
            RoutingActionKind.FileIntoLibrary => action.LibraryArea == RoutingLibraryArea.LocalOnly
                ? "File into Library · Local only"
                : "File into Library · Uploaded",
            RoutingActionKind.Deliver =>
                $"{action.Destination} · {action.OutputRef} · {action.Mode}",
            _ => action.Kind.ToString()
        });
        return string.Join("   →   ", actions);
    }

    private OutlineButton CreateSmallButton(string text, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Size = new Size(ScaleLogical(width), ScaleLogical(30)),
        Margin = new Padding(ScaleLogical(5), 0, 0, 0),
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.5f)
    };

    internal static int ScaleLogicalMetric(int value, int dpi) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, dpi) / 96d));

    private int ScaleLogical(int value) =>
        ScaleLogicalMetric(value, _layoutDpi ?? DeviceDpi);
}

internal sealed class DiscordConnectionDialog : Form
{
    private readonly TextBox _name;
    private readonly TextBox _webhook;

    internal string DisplayName => _name.Text.Trim();
    internal string WebhookUrl => _webhook.Text.Trim();

    internal DiscordConnectionDialog()
    {
        Text = "Add Discord connection";
        ClientSize = new Size(520, 228);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = ClipCordTheme.SurfaceBase;
        ForeColor = ClipCordTheme.TextPrimary;
        Padding = new Padding(18);
        _name = CreateInput("Connection name");
        _name.Text = "Friends server";
        _webhook = CreateInput("Discord webhook URL");
        _webhook.UseSystemPasswordChar = true;

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Text = "Add Discord\r\nThe webhook is encrypted for this Windows account and never stored in a route.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = Padding.Empty
        }, 0, 0);
        root.Controls.Add(_name, 0, 1);
        root.Controls.Add(_webhook, 0, 2);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        var save = new GradientButton { Text = "Add connection", Size = new Size(132, 34) };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) ||
                !WebhookValidation.IsDiscordWebhook(_webhook.Text))
            {
                MessageBox.Show(this, "Enter a name and a valid Discord webhook URL.",
                    "Connection details required", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        var cancel = new OutlineButton
        {
            Text = "Cancel",
            Size = new Size(90, 34),
            SurfaceColor = ClipCordTheme.SurfaceControl,
            HoverColor = ClipCordTheme.SurfaceControlHover,
            OutlineColor = ClipCordTheme.BorderStrong,
            ForeColor = ClipCordTheme.TextPrimary
        };
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    internal void ClearSecret()
    {
        _webhook.Text = string.Empty;
        _webhook.ClearUndo();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearSecret();
        base.Dispose(disposing);
    }

    private static TextBox CreateInput(string accessibleName) => new()
    {
        AccessibleName = accessibleName,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = ClipCordTheme.SurfaceSunken,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Margin = new Padding(0, 5, 0, 5)
    };
}

internal sealed class InputSourceConnectionDialog : Form
{
    private const int BaseWidth = 680;
    private const int BaseHeight = 474;
    private readonly int _layoutDpi;
    private readonly TextBox _name;
    private readonly TextBox _folder;
    private readonly RadioButton _steelSeries;
    private readonly RadioButton _nvidia;
    private readonly RadioButton _xbox;
    private readonly Label _structureTitle;
    private readonly Label _structureDetail;
    private readonly Label _baselineDetail;
    private readonly Func<IWin32Window?, string?, string?> _chooseFolder;
    private readonly Action<string, string> _showValidation;
    private string _lastSuggestedName = string.Empty;

    internal RoutingInputSourceRegistrationDraft? Draft { get; private set; }

    internal InputSourceConnectionDialog(
        int? layoutDpi = null,
        Func<IWin32Window?, string?, string?>? chooseFolder = null,
        Action<string, string>? showValidation = null)
    {
        _layoutDpi = Math.Max(96, layoutDpi ?? DeviceDpi);
        _chooseFolder = chooseFolder ?? ChooseFolder;
        _showValidation = showValidation ?? ((message, caption) => MessageBox.Show(
            this,
            message,
            caption,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information));

        Text = "Add clip source";
        AccessibleName = "Add clip source";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(ScaleLogical(BaseWidth), ScaleLogical(BaseHeight));
        MinimumSize = Size;
        MaximumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ClipCordTheme.SurfaceBase;
        ForeColor = ClipCordTheme.TextPrimary;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        Padding = new Padding(ScaleLogical(20));

        _steelSeries = CreateSourceChoice(
            "InputSourceKindSteelSeries", "SteelSeries GG", selected: true);
        _nvidia = CreateSourceChoice("InputSourceKindNvidia", "NVIDIA");
        _xbox = CreateSourceChoice("InputSourceKindXbox", "Xbox Game DVR");
        _name = CreateInput("InputSourceName", "Clip source name");
        _folder = CreateInput("InputSourceFolder", "Recorder folder");
        _folder.ReadOnly = true;
        _folder.TabStop = false;
        _structureTitle = CreateCopyLabel(
            "InputSourceStructureTitle", string.Empty, 9f, FontStyle.Bold,
            ClipCordTheme.TextPrimary);
        _structureDetail = CreateCopyLabel(
            "InputSourceStructureDetail", string.Empty, 8.25f, FontStyle.Regular,
            ClipCordTheme.TextSecondary);
        _baselineDetail = CreateCopyLabel(
            "InputSourceBaselineDetail",
            "Safe default · Existing clips become the baseline. This source stays off until a route starts it, so adding a folder cannot upload a backlog.",
            8.25f,
            FontStyle.Regular,
            Color.FromArgb(175, 151, 231));

        var root = new BufferedTableLayoutPanel
        {
            Name = "InputSourceDialogRoot",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(58)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(70)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(55)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(55)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(82)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(57)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(1)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSourceChoices(), 0, 1);
        root.Controls.Add(BuildFieldRow(
            "NAME", _name, action: null), 0, 2);

        var browse = new OutlineButton
        {
            Name = "BrowseInputSourceFolderButton",
            Text = "Browse…",
            AccessibleName = "Choose recorder folder",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            SurfaceColor = ClipCordTheme.SurfaceControl,
            HoverColor = ClipCordTheme.SurfaceControlHover,
            OutlineColor = ClipCordTheme.BorderStrong,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.5f)
        };
        browse.Click += (_, _) => BrowseFolder();
        root.Controls.Add(BuildFieldRow("FOLDER", _folder, browse), 0, 3);
        root.Controls.Add(BuildStructureCard(), 0, 4);
        root.Controls.Add(BuildBaselineCard(), 0, 5);
        root.Controls.Add(new Panel
        {
            Name = "InputSourceDialogDivider",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.BorderDefault
        }, 0, 6);
        root.Controls.Add(BuildActions(), 0, 7);
        Controls.Add(root);

        foreach (var choice in new[] { _steelSeries, _nvidia, _xbox })
            choice.CheckedChanged += (_, _) =>
            {
                if (choice.Checked) UpdateSourcePresentation();
            };
        UpdateSourcePresentation();
    }

    private Control BuildHeader()
    {
        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(27)));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(CreateCopyLabel(
            "InputSourceDialogTitle", "Add a clip source", 13f, FontStyle.Bold,
            ClipCordTheme.TextPrimary), 0, 0);
        header.Controls.Add(CreateCopyLabel(
            "InputSourceDialogSubtitle",
            "Choose the recorder and the exact folder it already uses. ClipCord will not change that recorder's save location.",
            8.5f,
            FontStyle.Regular,
            ClipCordTheme.TextSecondary), 0, 1);
        return header;
    }

    private Control BuildSourceChoices()
    {
        var layout = new BufferedTableLayoutPanel
        {
            Name = "InputSourceKindChoices",
            AccessibleName = "Recorder type",
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, ScaleLogical(5), 0, ScaleLogical(8)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        for (var column = 0; column < 3; column++)
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        _steelSeries.Margin = new Padding(0, 0, ScaleLogical(5), 0);
        _nvidia.Margin = new Padding(ScaleLogical(5), 0, ScaleLogical(5), 0);
        _xbox.Margin = new Padding(ScaleLogical(5), 0, 0, 0);
        layout.Controls.Add(_steelSeries, 0, 0);
        layout.Controls.Add(_nvidia, 1, 0);
        layout.Controls.Add(_xbox, 2, 0);
        return layout;
    }

    private Control BuildFieldRow(string caption, Control field, Control? action)
    {
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = action is null ? 2 : 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleLogical(5), 0, ScaleLogical(5)),
            BackColor = ClipCordTheme.SurfaceBase
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(72)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (action is not null)
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(98)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateCopyLabel(
            $"InputSource{caption}Caption", caption, 7.75f, FontStyle.Bold,
            ClipCordTheme.TextTertiary), 0, 0);
        field.Margin = action is null
            ? Padding.Empty
            : new Padding(0, 0, ScaleLogical(8), 0);
        layout.Controls.Add(field, 1, 0);
        if (action is not null) layout.Controls.Add(action, 2, 0);
        return layout;
    }

    private Control BuildStructureCard()
    {
        var card = new RoundedPanel
        {
            Name = "InputSourceStructureCard",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, ScaleLogical(5), 0, ScaleLogical(7)),
            Padding = new Padding(ScaleLogical(13), ScaleLogical(9), ScaleLogical(13), ScaleLogical(8)),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(9)
        };
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(31)));
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(25)));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = new FigmaIconControl
        {
            Name = "InputSourceStructureIcon",
            Asset = FigmaIconAsset.Folder,
            IconColor = Color.FromArgb(176, 128, 255),
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 3, 8, 3)
        };
        copy.Controls.Add(icon, 0, 0);
        copy.SetRowSpan(icon, 2);
        copy.Controls.Add(_structureTitle, 1, 0);
        copy.Controls.Add(_structureDetail, 1, 1);
        card.Controls.Add(copy);
        return card;
    }

    private Control BuildBaselineCard()
    {
        var card = new RoundedPanel
        {
            Name = "InputSourceBaselineCard",
            AccessibleName = "New clips from now on",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, ScaleLogical(9)),
            Padding = new Padding(ScaleLogical(13), ScaleLogical(7), ScaleLogical(13), ScaleLogical(7)),
            BackColor = Color.FromArgb(38, 29, 61),
            BorderColor = Color.FromArgb(91, 68, 137),
            CornerRadius = ScaleLogical(9)
        };
        _baselineDetail.Dock = DockStyle.Fill;
        _baselineDetail.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(_baselineDetail);
        return card;
    }

    private Control BuildActions()
    {
        var actions = new FlowLayoutPanel
        {
            Name = "InputSourceDialogActions",
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleLogical(11), 0, 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        var save = new GradientButton
        {
            Name = "ConfirmAddInputSourceButton",
            Text = "Add source",
            AccessibleName = "Add clip source",
            Size = new Size(ScaleLogical(116), ScaleLogical(34)),
            Margin = Padding.Empty
        };
        save.Click += (_, _) => AcceptDraft();
        var cancel = new OutlineButton
        {
            Name = "CancelAddInputSourceButton",
            Text = "Cancel",
            Size = new Size(ScaleLogical(88), ScaleLogical(34)),
            Margin = new Padding(0, 0, ScaleLogical(8), 0),
            SurfaceColor = ClipCordTheme.SurfaceControl,
            HoverColor = ClipCordTheme.SurfaceControlHover,
            OutlineColor = ClipCordTheme.BorderStrong,
            ForeColor = ClipCordTheme.TextPrimary
        };
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        return actions;
    }

    private void BrowseFolder()
    {
        var selected = _chooseFolder(
            this,
            Directory.Exists(_folder.Text) ? _folder.Text : null);
        if (string.IsNullOrWhiteSpace(selected)) return;
        try
        {
            _folder.Text = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _showValidation("Choose a valid recorder folder.", "Folder required");
        }
    }

    private void UpdateSourcePresentation()
    {
        var kind = SelectedKind;
        var suggestedName = kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg => "SteelSeries GG clips",
            RoutingInputSourceKind.Nvidia => "NVIDIA clips",
            _ => "Xbox Game DVR clips"
        };
        if (string.IsNullOrWhiteSpace(_name.Text) ||
            _name.Text.Equals(_lastSuggestedName, StringComparison.Ordinal))
        {
            _name.Text = suggestedName;
        }
        _lastSuggestedName = suggestedName;
        (_structureTitle.Text, _structureDetail.Text) = kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg => (
                "Flat folder · MP4 files directly inside",
                "Choose the folder where SteelSeries GG writes finished clips. Subfolders are not scanned."),
            RoutingInputSourceKind.Nvidia => (
                "Game folders · <Game>\\<clip>.mp4",
                "Choose the NVIDIA root that contains one folder per game. Loose files and deeper folders are ignored."),
            _ => (
                "Xbox Game DVR · top-level MP4 files",
                "Choose the Xbox Game DVR folder synced by OneDrive. Originals remain in OneDrive after delivery.")
        };
    }

    private void AcceptDraft()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || name.Length > RoutingInputSourceCatalogModel.MaximumDisplayNameLength)
        {
            _showValidation(
                $"Enter a source name up to {RoutingInputSourceCatalogModel.MaximumDisplayNameLength} characters.",
                "Source name required");
            _name.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_folder.Text) || !Directory.Exists(_folder.Text))
        {
            _showValidation("Choose an existing recorder folder.", "Folder required");
            return;
        }
        string root;
        try
        {
            root = RoutingInputSourceCatalogModel.NormalizeCanonicalRoot(_folder.Text);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or NotSupportedException or
                PathTooLongException)
        {
            _showValidation(
                "Choose an ordinary folder, not an entire drive or filesystem root.",
                "Folder not supported");
            return;
        }
        Draft = new RoutingInputSourceRegistrationDraft(name, SelectedKind, root);
        DialogResult = DialogResult.OK;
    }

    private RoutingInputSourceKind SelectedKind =>
        _nvidia.Checked
            ? RoutingInputSourceKind.Nvidia
            : _xbox.Checked
                ? RoutingInputSourceKind.XboxGameDvrOneDrive
                : RoutingInputSourceKind.SteelSeriesGg;

    private string? ChooseFolder(IWin32Window? owner, string? selectedPath)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where this recorder saves completed MP4 clips",
            UseDescriptionForTitle = true,
            SelectedPath = selectedPath ?? string.Empty,
            ShowNewFolderButton = false
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private RadioButton CreateSourceChoice(string name, string text, bool selected = false) => new()
    {
        Name = name,
        AccessibleName = text,
        Text = text,
        Checked = selected,
        Appearance = Appearance.Button,
        Dock = DockStyle.Fill,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance =
        {
            BorderSize = 1,
            BorderColor = ClipCordTheme.BorderStrong,
            CheckedBackColor = ClipCordTheme.VioletMuted,
            MouseOverBackColor = ClipCordTheme.SurfaceControlHover
        },
        BackColor = ClipCordTheme.SurfaceControl,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleCenter,
        UseVisualStyleBackColor = false,
        TabStop = true
    };

    private TextBox CreateInput(string name, string accessibleName) => new()
    {
        Name = name,
        AccessibleName = accessibleName,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = ClipCordTheme.SurfaceSunken,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Margin = Padding.Empty
    };

    private Label CreateCopyLabel(
        string name,
        string text,
        float size,
        FontStyle style,
        Color color) => new()
    {
        Name = name,
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = false,
        ForeColor = color,
        BackColor = Color.Transparent,
        Font = ClipCordTheme.InterfaceFont(size, style),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * _layoutDpi / 96d));
}

internal sealed class RouteEditorDialog : Form
{
    private const int BaseWidth = 984;
    private const int BaseHeight = 700;
    private readonly int _layoutDpi;
    private readonly TextBox _name;
    private readonly TextBox _game;
    private readonly RadioButton _instantReplay;
    private readonly RadioButton _manualRecording;
    private readonly RadioButton _watchedFolder;
    private readonly RadioButton _anyClip;
    private readonly ComboBox _watchedSource;
    private readonly RadioButton _historyNewOnly;
    private readonly RadioButton _historyLastDay;
    private readonly RadioButton _historyLastWeek;
    private readonly Label _xboxPreflightStatus;
    private readonly IReadOnlyList<RoutingInputSourceDisplay> _inputSources;
    private readonly RoutingMigratedInputSourceDisplay? _migratedInputSource;
    private readonly Func<RoutingInputSourceDisplay, XboxDvrHistoryPolicy, CancellationToken,
        XboxDvrPreflightPreview>? _xboxPreflight;
    private readonly Func<Func<XboxDvrPreflightPreview>, CancellationToken,
        Task<XboxDvrPreflightPreview>> _xboxPreflightTaskRunner;
    private readonly Func<string, string, DialogResult> _confirmXboxHistory;
    private readonly Func<string, string, MessageBoxIcon, DialogResult> _showSaveGuardMessage;
    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _historyActivationUtc;
    private readonly RadioButton _discord;
    private readonly RadioButton _localOnly;
    private readonly RouteConnectionSelector _discordConnection;
    private readonly RadioButton _original;
    private readonly RadioButton _landscape;
    private readonly RadioButton _portrait;
    private readonly CheckBox _approval;
    private readonly CheckBox _fileIntoLibrary;
    private readonly IReadOnlyList<RoutingConnectionDisplay> _connections;
    private readonly Dictionary<RadioButton, RoundedPanel> _choiceCards = new();
    private readonly Dictionary<int, List<RoundedPanel>> _summaryRows = new();
    private readonly Label _headerSubtitle;
    private readonly Label _summaryWhen;
    private readonly Label _summaryIf;
    private readonly Label _summaryPrepare;
    private readonly Label _summaryThen;
    private readonly BrandedScrollHost _stageScroll;
    private readonly Control[] _steps;
    private readonly OutlineButton _backButton;
    private readonly GradientButton _nextButton;
    private readonly GradientButton _headerSaveButton;
    private readonly Label _stepCounter;
    private Control? _sourceConfiguration;
    private Control? _historyConfiguration;
    private Label? _sourceSafetyNote;
    private CancellationTokenSource? _preflightCancellation;
    private Task _xboxPreflightCompletion = Task.CompletedTask;
    private XboxDvrPreflightPreview? _latestPreflight;
    private int _preflightGeneration;
    private int _lastSelectableWatchedSourceIndex;
    private bool _restoringWatchedSourceSelection;
    private int _step = 1;
    private int _furthestStep = 1;

    internal RoutingRouteDraft? Draft { get; private set; }

    internal RouteEditorDialog(
        IReadOnlyList<RoutingConnectionDisplay> connections,
        int? layoutDpi = null,
        IReadOnlyList<RoutingInputSourceDisplay>? inputSources = null,
        Func<RoutingInputSourceDisplay, XboxDvrHistoryPolicy, CancellationToken,
            XboxDvrPreflightPreview>? xboxPreflight = null,
        Func<string, string, DialogResult>? xboxHistoryConfirmation = null,
        Func<DateTimeOffset>? clock = null,
        Func<string, string, MessageBoxIcon, DialogResult>? saveGuardMessage = null,
        Func<Func<XboxDvrPreflightPreview>, CancellationToken,
            Task<XboxDvrPreflightPreview>>? xboxPreflightTaskRunner = null,
        RoutingMigratedInputSourceDisplay? migratedInputSource = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _inputSources = inputSources?.ToArray() ?? [];
        _migratedInputSource = migratedInputSource;
        _xboxPreflight = xboxPreflight;
        _xboxPreflightTaskRunner = xboxPreflightTaskRunner ??
            ((operation, cancellationToken) =>
                Task.Run(operation, cancellationToken));
        _confirmXboxHistory = xboxHistoryConfirmation ?? ((message, caption) =>
            MessageBox.Show(
                this,
                message,
                caption,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2));
        _showSaveGuardMessage = saveGuardMessage ?? ((message, caption, icon) =>
            MessageBox.Show(
                this,
                message,
                caption,
                MessageBoxButtons.OK,
                icon));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _historyActivationUtc = RoutingValidation.Utc(_clock());
        _layoutDpi = Math.Max(96, layoutDpi ?? DeviceDpi);

        Text = "New route";
        AccessibleName = "New route builder";
        ClientSize = new Size(ScaleLogical(BaseWidth), ScaleLogical(BaseHeight));
        MinimumSize = new Size(ScaleLogical(820), ScaleLogical(540));
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ClipCordTheme.BorderStrong;
        ForeColor = ClipCordTheme.TextPrimary;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        Padding = new Padding(ScaleLogical(1));
        KeyPreview = true;

        _name = CreateEditor("RouteNameEditor", "Route name", "Battlefield highlights");
        _game = CreateEditor("RouteGameConditionEditor", "Optional exact game name", "Leave blank for every game");
        _instantReplay = CreateRadio("RouteTriggerInstantReplay", "Instant Replay", selected: true);
        _manualRecording = CreateRadio("RouteTriggerManualRecording", "Manual Recording");
        _watchedFolder = CreateRadio("RouteTriggerWatchedFolder", "External watched folder");
        _anyClip = CreateRadio("RouteTriggerAnySource", "Any new source clip");
        _watchedSource = new ComboBox
        {
            Name = "RouteWatchedSourceSelector",
            AccessibleName = "External clip source",
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat,
            ItemHeight = ScaleLogical(31),
            BackColor = ClipCordTheme.SurfaceSunken,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = Padding.Empty
        };
        _watchedSource.DrawItem += DrawWatchedSourceItem;
        _watchedSource.Items.Add(new WatchedSourceChoice(
            _migratedInputSource?.Name ?? "Current migrated folder · SteelSeries/NVIDIA",
            _migratedInputSource?.Detail ??
            "Preserved from ClipCord 1.x · add a named source in Routes → Connections",
            null,
            Selectable: true));
        foreach (var source in _inputSources)
        {
            _watchedSource.Items.Add(new WatchedSourceChoice(
                source.Name,
                source.Available
                    ? source.Detail
                    : source.Retired
                        ? $"{source.Detail} · Replaced — retained for existing routes"
                        : $"{source.Detail} · Needs attention — Recheck in Connections",
                source,
                source.Available));
        }
        _watchedSource.SelectedIndexChanged += WatchedSourceSelectionChanged;
        _watchedSource.SelectedIndex = 0;
        _historyNewOnly = CreateRadio(
            "RouteXboxHistoryNewOnly", "New clips from now on", selected: true);
        _historyLastDay = CreateRadio("RouteXboxHistoryLastDay", "Clips from the last 24 hours");
        _historyLastWeek = CreateRadio("RouteXboxHistoryLastWeek", "Clips from the last 7 days");
        _xboxPreflightStatus = CreateTextLabel(
            "RouteXboxPreflightStatus",
            "Choose a history window to preview matching clips.",
            8.25f,
            ClipCordTheme.TextSecondary);
        _xboxPreflightStatus.Dock = DockStyle.Fill;
        _xboxPreflightStatus.TextAlign = ContentAlignment.MiddleLeft;
        _discord = CreateRadio(
            "RouteDestinationDiscord", "Upload to Discord webhook", selected: _connections.Count > 0);
        _localOnly = CreateRadio(
            "RouteDestinationLibraryOnly", "File into Library only", selected: _connections.Count == 0);
        _discord.Enabled = _connections.Count > 0;
        _discord.TabStop = _discord.Enabled;
        _discordConnection = new RouteConnectionSelector(_connections)
        {
            Name = "RouteDiscordConnectionSelector",
            AccessibleName = "Discord connection",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceSunken,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9.5f),
            Margin = Padding.Empty
        };
        _discordConnection.SelectedIndex = -1;
        _original = CreateRadio("RouteOutputOriginal", "Original clip", selected: true);
        _landscape = CreateRadio("RouteOutputLandscape", "Landscape reaction camera");
        _portrait = CreateRadio("RouteOutputPortrait", "Portrait reaction camera");
        _approval = CreateCheck(
            "RouteApprovalRequired", "Require approval before sending");
        _fileIntoLibrary = CreateCheck(
            "RouteFileIntoLibrary", "File into Library after the route finishes", selected: true);

        WireExclusiveGroup(_instantReplay, _manualRecording, _watchedFolder, _anyClip);
        WireExclusiveGroup(_historyNewOnly, _historyLastDay, _historyLastWeek);
        WireExclusiveGroup(_original, _landscape, _portrait);
        WireExclusiveGroup(_discord, _localOnly);

        _headerSubtitle = CreateTextLabel(
            "RouteStepSubtitle",
            string.Empty,
            9.25f,
            ClipCordTheme.TextSecondary);
        _summaryWhen = CreateSummaryValueLabel("RouteSummaryWhenValue");
        _summaryIf = CreateSummaryValueLabel("RouteSummaryIfValue");
        _summaryPrepare = CreateSummaryValueLabel("RouteSummaryPrepareValue");
        _summaryThen = CreateSummaryValueLabel("RouteSummaryThenValue");
        _stepCounter = CreateTextLabel(
            "RouteStepCounter", string.Empty, 8.75f, ClipCordTheme.TextSecondary);
        _stepCounter.AutoSize = true;
        _stepCounter.TextAlign = ContentAlignment.MiddleCenter;

        _headerSaveButton = CreatePrimaryButton(
            "RouteHeaderSaveButton", "Save route", 112);
        _headerSaveButton.Enabled = false;
        _headerSaveButton.Click += (_, _) => SaveDraft();
        var cancel = CreateSecondaryButton("RouteCancelButton", "Cancel", 84);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        CancelButton = cancel;

        _backButton = CreateSecondaryButton("RouteBackButton", "‹  Back", 92);
        _backButton.Click += (_, _) => ShowStep(_step - 1);
        _nextButton = CreatePrimaryButton(
            "NextRouteStepButton", "Next: Prepare outputs  →", 208);
        _nextButton.Click += (_, _) =>
        {
            if (_step < 3) ShowStep(_step + 1);
            else SaveDraft();
        };
        AcceptButton = _nextButton;

        _name.TextChanged += (_, _) => UpdateSummary();
        _game.TextChanged += (_, _) =>
        {
            UpdateXboxPreflightStatus();
            UpdateSummary();
        };
        _discordConnection.SelectedIndexChanged += (_, _) => UpdateSummary();
        _approval.CheckedChanged += (_, _) => UpdateSummary();
        _watchedSource.SelectedIndexChanged += (_, _) => UpdateTriggerOptions();
        _watchedFolder.CheckedChanged += (_, _) => UpdateTriggerOptions();
        _historyNewOnly.CheckedChanged += (_, _) =>
        {
            if (_historyNewOnly.Checked) HistorySelectionChanged();
        };
        _historyLastDay.CheckedChanged += (_, _) =>
        {
            if (_historyLastDay.Checked) HistorySelectionChanged();
        };
        _historyLastWeek.CheckedChanged += (_, _) =>
        {
            if (_historyLastWeek.Checked) HistorySelectionChanged();
        };

        _steps = [BuildTriggerStep(), BuildPrepareStep(), BuildDestinationStep()];
        _stageScroll = new BrandedScrollHost
        {
            Name = "RouteEditorStageHost",
            AccessibleName = "Route configuration step",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase,
            TabStop = true
        };

        var root = new BufferedTableLayoutPanel
        {
            Name = "RouteEditorRoot",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(
                ScaleLogical(28), ScaleLogical(18), ScaleLogical(28), ScaleLogical(22)),
            BackColor = ClipCordTheme.SurfaceBase
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(66)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(cancel), 0, 0);
        root.Controls.Add(BuildBuilder(), 0, 1);
        Controls.Add(root);

        UpdateDestinationOptions();
        UpdateTriggerOptions();
        ShowStep(1);
        Shown += (_, _) =>
        {
            if (StartPosition != FormStartPosition.Manual || Location.X > -10000)
                FitToWorkingArea(Screen.FromControl(this).WorkingArea);
            _stageScroll.RefreshContentLayout(preservePosition: false);
        };
        FormClosed += (_, _) =>
        {
            _preflightCancellation?.Cancel();
            _preflightCancellation?.Dispose();
            _preflightCancellation = null;
        };
    }

    private Control BuildHeader(Button cancel)
    {
        var header = new BufferedTableLayoutPanel
        {
            Name = "RouteEditorHeader",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        title.Controls.Add(new Label
        {
            Name = "RouteDialogTitle",
            Text = "New route",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.DisplayFont(15.75f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 0, 0);
        _headerSubtitle.Dock = DockStyle.Fill;
        _headerSubtitle.TextAlign = ContentAlignment.TopLeft;
        title.Controls.Add(_headerSubtitle, 0, 1);
        header.Controls.Add(title, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Name = "RouteHeaderActions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        cancel.Margin = new Padding(0, 0, ScaleLogical(10), 0);
        actions.Controls.Add(cancel);
        actions.Controls.Add(_headerSaveButton);
        header.Controls.Add(actions, 1, 0);
        return header;
    }

    private Control BuildBuilder()
    {
        var builder = new BufferedTableLayoutPanel
        {
            Name = "RouteEditorBuilder",
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleLogical(4), 0, 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        builder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(292)));
        builder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(16)));
        builder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        builder.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        builder.Controls.Add(BuildSummary(), 0, 0);

        var stage = new BufferedTableLayoutPanel
        {
            Name = "RouteEditorStageColumn",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        stage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stage.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(52)));
        stage.Controls.Add(_stageScroll, 0, 0);
        stage.Controls.Add(BuildStepActions(), 0, 1);
        builder.Controls.Add(stage, 2, 0);
        return builder;
    }

    private Control BuildSummary()
    {
        var card = new RoundedPanel
        {
            Name = "RouteEditorSummary",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(ScaleLogical(14)),
            BackColor = Color.FromArgb(16, 27, 45),
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(14),
            AccessibleName = "Route summary"
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 11,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 10; index++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateEyebrow("ROUTE NAME"), 0, 0);
        _name.Height = ScaleLogical(36);
        _name.MinimumSize = new Size(0, ScaleLogical(36));
        _name.Margin = new Padding(0, ScaleLogical(7), 0, ScaleLogical(12));
        layout.Controls.Add(_name, 0, 1);
        layout.Controls.Add(CreateDivider(), 0, 2);
        var summaryHeading = CreateEyebrow("SUMMARY");
        summaryHeading.Margin = new Padding(0, ScaleLogical(12), 0, ScaleLogical(7));
        layout.Controls.Add(summaryHeading, 0, 3);
        layout.Controls.Add(CreateSummaryRow(1, "WHEN", _summaryWhen), 0, 4);
        layout.Controls.Add(CreateSummaryRow(1, "IF", _summaryIf), 0, 5);
        layout.Controls.Add(CreateSummaryRow(2, "PREPARE", _summaryPrepare), 0, 6);
        layout.Controls.Add(CreateSummaryRow(3, "THEN", _summaryThen), 0, 7);
        var divider = CreateDivider();
        divider.Margin = new Padding(0, ScaleLogical(12), 0, ScaleLogical(12));
        layout.Controls.Add(divider, 0, 8);
        layout.Controls.Add(CreateInfoNote(
            "RouteSummaryNote",
            "Every route files the clip into your Library. Discord delivery is independent, so one failed action never loses the clip.",
            76), 0, 9);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildTriggerStep()
    {
        var root = CreateStageRoot("RouteEditorStageContentStep1");
        root.Controls.Add(CreateStageHeading(
            "WHEN", "Choose what starts this route"), 0, 0);
        var choices = new BufferedTableLayoutPanel
        {
            Name = "RouteTriggerChoices",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, ScaleLogical(10), 0, ScaleLogical(16)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(78)));
        choices.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(78)));
        choices.Controls.Add(CreateChoiceCard(
            _anyClip,
            FigmaIconAsset.Film,
            "Any new source clip",
            "Every new clip, regardless of how it arrived",
            new Padding(0, 0, ScaleLogical(5), ScaleLogical(5))), 0, 0);
        choices.Controls.Add(CreateChoiceCard(
            _instantReplay,
            FigmaIconAsset.Bolt,
            "ClipCord Instant Replay",
            "A saved replay from ClipCord's rolling buffer",
            new Padding(ScaleLogical(5), 0, 0, ScaleLogical(5))), 1, 0);
        choices.Controls.Add(CreateChoiceCard(
            _manualRecording,
            FigmaIconAsset.Capture,
            "ClipCord Manual Recording",
            "A recording started and stopped in Capture",
            new Padding(0, ScaleLogical(5), ScaleLogical(5), 0)), 0, 1);
        choices.Controls.Add(CreateChoiceCard(
            _watchedFolder,
            FigmaIconAsset.Folder,
            "External watched folder",
            "A completed clip discovered from another recorder",
            new Padding(ScaleLogical(5), ScaleLogical(5), 0, 0)), 1, 1);
        root.Controls.Add(choices, 0, 1);
        _sourceConfiguration = BuildSourceConfiguration();
        root.Controls.Add(_sourceConfiguration, 0, 2);
        root.Controls.Add(CreateStageHeading(
            "IF · OPTIONAL", "Limit the route to one exact game name"), 0, 3);
        var conditionGroup = new BufferedTableLayoutPanel
        {
            Name = "RouteConditionGroup",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        conditionGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        conditionGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        conditionGroup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        conditionGroup.Controls.Add(BuildGameCondition(), 0, 0);
        conditionGroup.Controls.Add(CreateInfoNote(
            "RouteTriggerHelper",
            "Leave the game field blank to match every game. Xbox filenames are grouped by the game name before their timestamp, just like other flat recorder folders.",
            58), 0, 1);
        root.Controls.Add(conditionGroup, 0, 4);
        return root;
    }

    private Control BuildSourceConfiguration()
    {
        var root = new BufferedTableLayoutPanel
        {
            Name = "RouteSourceConfiguration",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0, 0, 0, ScaleLogical(14)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase,
            Visible = false,
            TabStop = false
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < root.RowCount; index++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(CreateStageHeading(
            "FROM", "Choose which external recorder supplies clips"), 0, 0);

        var selectorCard = new RoundedPanel
        {
            Name = "RouteSourceSelectorCard",
            Dock = DockStyle.Top,
            Height = ScaleLogical(58),
            MinimumSize = new Size(0, ScaleLogical(58)),
            Margin = new Padding(0, ScaleLogical(9), 0, ScaleLogical(12)),
            Padding = new Padding(ScaleLogical(11), ScaleLogical(9), ScaleLogical(11), ScaleLogical(9)),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10)
        };
        var selectorLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = selectorCard.BackColor
        };
        selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(28)));
        selectorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selectorLayout.Controls.Add(new FigmaIconControl
        {
            Name = "RouteSourceFolderIcon",
            Asset = FigmaIconAsset.Folder,
            IconColor = ClipCordTheme.TextSecondary,
            Anchor = AnchorStyles.None,
            Size = new Size(ScaleLogical(19), ScaleLogical(19)),
            Margin = Padding.Empty
        }, 0, 0);
        selectorLayout.Controls.Add(_watchedSource, 1, 0);
        selectorCard.Controls.Add(selectorLayout);
        root.Controls.Add(selectorCard, 0, 1);

        _historyConfiguration = BuildHistoryConfiguration();
        root.Controls.Add(_historyConfiguration, 0, 2);
        var safetyNote = CreateInfoNote(
            "RouteSourceSafetyNote",
            "Choose a source. New SteelSeries and NVIDIA connections start from now; their existing clips remain an untouched baseline.",
            56);
        _sourceSafetyNote = safetyNote.Controls.OfType<Label>().Single();
        root.Controls.Add(safetyNote, 0, 3);
        return root;
    }

    private Control BuildHistoryConfiguration()
    {
        var root = new BufferedTableLayoutPanel
        {
            Name = "RouteXboxHistoryConfiguration",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase,
            Visible = false,
            TabStop = false
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(CreateStageHeading(
            "HISTORY", "Choose how far back this route starts"), 0, 0);

        var choices = new BufferedTableLayoutPanel
        {
            Name = "RouteXboxHistoryChoices",
            Dock = DockStyle.Top,
            Height = ScaleLogical(58),
            MinimumSize = new Size(0, ScaleLogical(58)),
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, ScaleLogical(9), 0, ScaleLogical(9)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase,
            AccessibleName = "Xbox clip history window"
        };
        for (var index = 0; index < 3; index++)
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        choices.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(58)));
        choices.Controls.Add(CreateCompactChoiceCard(
            _historyNewOnly, "From now on", "Recommended", new Padding(0, 0, ScaleLogical(4), 0)), 0, 0);
        choices.Controls.Add(CreateCompactChoiceCard(
            _historyLastDay, "Last 24 hours", "Recent clips", new Padding(ScaleLogical(4), 0, ScaleLogical(4), 0)), 1, 0);
        choices.Controls.Add(CreateCompactChoiceCard(
            _historyLastWeek, "Last 7 days", "One week", new Padding(ScaleLogical(4), 0, 0, 0)), 2, 0);
        root.Controls.Add(choices, 0, 1);

        var preview = new RoundedPanel
        {
            Name = "RouteXboxPreflightCard",
            Dock = DockStyle.Top,
            Height = ScaleLogical(48),
            MinimumSize = new Size(0, ScaleLogical(48)),
            Margin = new Padding(0, 0, 0, ScaleLogical(10)),
            Padding = new Padding(ScaleLogical(11), ScaleLogical(8), ScaleLogical(11), ScaleLogical(8)),
            BackColor = Color.FromArgb(16, 27, 45),
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(9)
        };
        var previewLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = preview.BackColor
        };
        previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(28)));
        previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewLayout.Controls.Add(new FigmaIconControl
        {
            Name = "RouteXboxHistoryIcon",
            Asset = FigmaIconAsset.Clock,
            IconColor = Color.FromArgb(196, 154, 255),
            Anchor = AnchorStyles.None,
            Size = new Size(ScaleLogical(18), ScaleLogical(18)),
            Margin = Padding.Empty
        }, 0, 0);
        previewLayout.Controls.Add(_xboxPreflightStatus, 1, 0);
        preview.Controls.Add(previewLayout);
        root.Controls.Add(preview, 0, 2);
        return root;
    }

    private RoundedPanel CreateCompactChoiceCard(
        RadioButton choice,
        string title,
        string helper,
        Padding margin)
    {
        var card = new RoundedPanel
        {
            Name = $"{choice.Name}Card",
            Dock = DockStyle.Fill,
            Margin = margin,
            Padding = new Padding(ScaleLogical(7), ScaleLogical(6), ScaleLogical(8), ScaleLogical(6)),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(9),
            AccessibleName = title,
            AccessibleRole = AccessibleRole.RadioButton
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(18)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        choice.Dock = DockStyle.Fill;
        choice.Margin = Padding.Empty;
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var titleLabel = CreateTextLabel(
            $"{choice.Name}Title", title, 8.25f, ClipCordTheme.TextPrimary, FontStyle.Bold);
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.TextAlign = ContentAlignment.BottomLeft;
        var helperLabel = CreateTextLabel(
            $"{choice.Name}Helper", helper, 7.5f, ClipCordTheme.TextSecondary);
        helperLabel.Dock = DockStyle.Fill;
        helperLabel.TextAlign = ContentAlignment.TopLeft;
        copy.Controls.Add(titleLabel, 0, 0);
        copy.Controls.Add(helperLabel, 0, 1);
        layout.Controls.Add(choice, 0, 0);
        layout.Controls.Add(copy, 1, 0);
        card.Controls.Add(layout);
        _choiceCards[choice] = card;
        WireCardSelection(card, choice);
        return card;
    }

    private Control BuildPrepareStep()
    {
        var root = CreateStageRoot("RouteEditorStageContentStep2");
        root.Controls.Add(CreateStageHeading(
            "PREPARE", "Choose the one output this route sends"), 0, 0);
        var choices = new BufferedTableLayoutPanel
        {
            Name = "RouteOutputChoices",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, ScaleLogical(10), 0, ScaleLogical(14)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 3; index++)
            choices.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(72)));
        choices.Controls.Add(CreateChoiceCard(
            _original,
            FigmaIconAsset.Film,
            "Original clip",
            "Already in your Library at the quality Capture recorded",
            new Padding(0, 0, 0, ScaleLogical(5))), 0, 0);
        choices.Controls.Add(CreateChoiceCard(
            _landscape,
            FigmaIconAsset.Landscape,
            "Landscape reaction camera",
            "Uses the 16:9 reaction-camera rendition when available",
            new Padding(0, ScaleLogical(5), 0, ScaleLogical(5))), 0, 1);
        choices.Controls.Add(CreateChoiceCard(
            _portrait,
            FigmaIconAsset.Portrait,
            "Portrait reaction camera",
            "Uses the 9:16 reaction-camera rendition when available",
            new Padding(0, ScaleLogical(5), 0, 0)), 0, 2);
        root.Controls.Add(choices, 0, 1);
        root.Controls.Add(CreateInfoNote(
            "RoutePrepareHelper",
            "Capture remains the source of truth for which renditions exist. If the selected reaction-camera output is unavailable, this route continues with the original clip.",
            72), 0, 2);
        return root;
    }

    private Control BuildDestinationStep()
    {
        var root = CreateStageRoot("RouteEditorStageContentStep3");
        root.Controls.Add(CreateStageHeading(
            "THEN", "Choose where the selected output goes"), 0, 0);
        var choices = new BufferedTableLayoutPanel
        {
            Name = "RouteDestinationChoices",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, ScaleLogical(10), 0, ScaleLogical(14)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(78)));
        choices.Controls.Add(CreateChoiceCard(
            _discord,
            FigmaIconAsset.Discord,
            "Upload to Discord webhook",
            _connections.Count > 0
                ? "Send through one saved Discord connection"
                : "Add a Discord connection before using this action",
            new Padding(0, 0, ScaleLogical(5), 0)), 0, 0);
        choices.Controls.Add(CreateChoiceCard(
            _localOnly,
            FigmaIconAsset.Disk,
            "File into Library only",
            "Keep the clip local with no network delivery",
            new Padding(ScaleLogical(5), 0, 0, 0)), 1, 0);
        root.Controls.Add(choices, 0, 1);
        root.Controls.Add(BuildDiscordConnectionField(), 0, 2);
        root.Controls.Add(BuildPostActions(), 0, 3);
        return root;
    }

    private Control BuildGameCondition()
    {
        var card = new RoundedPanel
        {
            Name = "RouteConditionField",
            Dock = DockStyle.Top,
            Height = ScaleLogical(94),
            MinimumSize = new Size(0, ScaleLogical(94)),
            Margin = new Padding(0, ScaleLogical(10), 0, ScaleLogical(14)),
            Padding = new Padding(ScaleLogical(12), ScaleLogical(9), ScaleLogical(12), ScaleLogical(10)),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(20)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(38)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var gameLabel = CreateTextLabel(
            "RouteGameConditionLabel", "Game name matches", 9f,
            ClipCordTheme.TextPrimary, FontStyle.Bold);
        gameLabel.Dock = DockStyle.Fill;
        gameLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(gameLabel, 0, 0);
        _game.Margin = new Padding(0, ScaleLogical(2), 0, ScaleLogical(3));
        layout.Controls.Add(_game, 0, 1);
        var helper = CreateTextLabel(
            "RouteGameConditionHelper", "Example: Battlefield 6", 8f,
            ClipCordTheme.TextSecondary);
        helper.Dock = DockStyle.Fill;
        helper.TextAlign = ContentAlignment.BottomLeft;
        layout.Controls.Add(helper, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildDiscordConnectionField()
    {
        var card = new RoundedPanel
        {
            Name = "RouteDiscordConnectionField",
            Dock = DockStyle.Top,
            Height = ScaleLogical(80),
            MinimumSize = new Size(0, ScaleLogical(80)),
            Margin = new Padding(0, 0, 0, ScaleLogical(12)),
            Padding = new Padding(ScaleLogical(12), ScaleLogical(9), ScaleLogical(12), ScaleLogical(10)),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10)
        };
        var field = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(22)));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var connectionLabel = CreateTextLabel(
            "RouteDiscordConnectionLabel", "DISCORD CONNECTION", 7.75f,
            ClipCordTheme.TextSecondary, FontStyle.Bold);
        connectionLabel.Dock = DockStyle.Fill;
        connectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        field.Controls.Add(connectionLabel, 0, 0);
        field.Controls.Add(_discordConnection, 0, 1);
        card.Controls.Add(field);
        return card;
    }

    private Control BuildPostActions()
    {
        var card = new RoundedPanel
        {
            Name = "RoutePostActions",
            Dock = DockStyle.Top,
            Height = ScaleLogical(92),
            MinimumSize = new Size(0, ScaleLogical(92)),
            Margin = Padding.Empty,
            Padding = new Padding(ScaleLogical(12), ScaleLogical(7), ScaleLogical(12), ScaleLogical(7)),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _approval.Dock = DockStyle.Fill;
        _fileIntoLibrary.Visible = false;
        layout.Controls.Add(_approval, 0, 0);
        layout.Controls.Add(BuildAlwaysOnLibraryRow(), 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildStepActions()
    {
        var actions = new BufferedTableLayoutPanel
        {
            Name = "RouteDialogActions",
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleLogical(10), 0, 0),
            BackColor = ClipCordTheme.SurfaceBase
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(208)));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _backButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _stepCounter.Anchor = AnchorStyles.Right;
        _stepCounter.Margin = new Padding(0, 0, ScaleLogical(12), 0);
        _nextButton.Dock = DockStyle.Fill;
        actions.Controls.Add(_backButton, 0, 0);
        actions.Controls.Add(_stepCounter, 2, 0);
        actions.Controls.Add(_nextButton, 3, 0);
        return actions;
    }

    private BufferedTableLayoutPanel CreateStageRoot(string name)
    {
        var root = new BufferedTableLayoutPanel
        {
            Name = name,
            // BrandedScrollHost supplies the viewport width. Keeping this root out of
            // AutoSize prevents TableLayout from shrinking the Figma two-column cards
            // back to their narrow text-only preferred width after that assignment.
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, ScaleLogical(12), ScaleLogical(8)),
            BackColor = ClipCordTheme.SurfaceBase,
            MinimumSize = new Size(0, ScaleLogical(360))
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < root.RowCount; index++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return root;
    }

    private Control CreateStageHeading(string stage, string helper)
    {
        var heading = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var pill = new RoundedPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, ScaleLogical(8), 0),
            Padding = new Padding(ScaleLogical(8), ScaleLogical(3), ScaleLogical(8), ScaleLogical(3)),
            BackColor = ClipCordTheme.VioletMuted,
            BorderColor = ClipCordTheme.Violet,
            CornerRadius = ScaleLogical(6)
        };
        pill.Controls.Add(new Label
        {
            Text = stage,
            AutoSize = true,
            ForeColor = Color.FromArgb(196, 154, 255),
            BackColor = Color.Transparent,
            Font = ClipCordTheme.InterfaceFont(7.75f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false
        });
        var helperLabel = CreateTextLabel(
            $"Route{stage.Replace(" · ", string.Empty)}Heading",
            helper,
            8.5f,
            ClipCordTheme.TextSecondary);
        helperLabel.Dock = DockStyle.Fill;
        helperLabel.TextAlign = ContentAlignment.MiddleLeft;
        helperLabel.AutoEllipsis = true;
        heading.Controls.Add(pill, 0, 0);
        heading.Controls.Add(helperLabel, 1, 0);
        return heading;
    }

    private RoundedPanel CreateChoiceCard(
        RadioButton choice,
        FigmaIconAsset icon,
        string title,
        string helper,
        Padding margin)
    {
        var card = new RoundedPanel
        {
            Name = $"{choice.Name}Card",
            Dock = DockStyle.Fill,
            Margin = margin,
            Padding = new Padding(ScaleLogical(10), ScaleLogical(9), ScaleLogical(11), ScaleLogical(9)),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(10),
            AccessibleName = title,
            AccessibleRole = AccessibleRole.RadioButton
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(22)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(26)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        choice.Dock = DockStyle.Fill;
        choice.Margin = Padding.Empty;
        var iconControl = new FigmaIconControl
        {
            Name = $"{choice.Name}Icon",
            Asset = icon,
            IconColor = ClipCordTheme.TextSecondary,
            Anchor = AnchorStyles.None,
            Size = new Size(ScaleLogical(19), ScaleLogical(19)),
            MinimumSize = new Size(ScaleLogical(19), ScaleLogical(19)),
            MaximumSize = new Size(ScaleLogical(19), ScaleLogical(19)),
            Margin = Padding.Empty
        };
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        var titleLabel = CreateTextLabel(
            $"{choice.Name}Title", title, 9.25f,
            choice.Enabled ? ClipCordTheme.TextPrimary : ClipCordTheme.TextTertiary,
            FontStyle.Bold);
        titleLabel.Dock = DockStyle.Fill;
        titleLabel.TextAlign = ContentAlignment.BottomLeft;
        titleLabel.AutoEllipsis = true;
        var helperLabel = CreateTextLabel(
            $"{choice.Name}Helper", helper, 8f,
            choice.Enabled ? ClipCordTheme.TextSecondary : ClipCordTheme.TextTertiary);
        helperLabel.Dock = DockStyle.Fill;
        helperLabel.TextAlign = ContentAlignment.TopLeft;
        helperLabel.AutoEllipsis = true;
        copy.Controls.Add(titleLabel, 0, 0);
        copy.Controls.Add(helperLabel, 0, 1);
        layout.Controls.Add(choice, 0, 0);
        layout.Controls.Add(iconControl, 1, 0);
        layout.Controls.Add(copy, 2, 0);
        card.Controls.Add(layout);
        _choiceCards[choice] = card;
        WireCardSelection(card, choice);
        return card;
    }

    private RoundedPanel CreateSummaryRow(int step, string label, Label value)
    {
        var row = new RoundedPanel
        {
            Name = $"RouteSummary{label.Replace(" · ", string.Empty)}",
            Dock = DockStyle.Top,
            Height = ScaleLogical(38),
            MinimumSize = new Size(0, ScaleLogical(38)),
            Margin = new Padding(0, 0, 0, ScaleLogical(6)),
            Padding = new Padding(ScaleLogical(9), ScaleLogical(5), ScaleLogical(10), ScaleLogical(5)),
            BackColor = Color.FromArgb(13, 22, 38),
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(8)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = row.BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(10)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(66)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var dot = new RoundedPanel
        {
            Name = $"RouteSummary{label}Dot",
            Anchor = AnchorStyles.None,
            BackColor = ClipCordTheme.SuccessBorder,
            CornerRadius = ScaleLogical(3),
            Size = new Size(ScaleLogical(6), ScaleLogical(6)),
            Margin = Padding.Empty
        };
        var key = CreateTextLabel(
            $"RouteSummary{label}Label", label, 7.5f,
            ClipCordTheme.TextSecondary, FontStyle.Bold);
        key.Dock = DockStyle.Fill;
        key.TextAlign = ContentAlignment.MiddleLeft;
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(dot, 0, 0);
        layout.Controls.Add(key, 1, 0);
        layout.Controls.Add(value, 2, 0);
        row.Controls.Add(layout);
        if (!_summaryRows.TryGetValue(step, out var rows))
        {
            rows = [];
            _summaryRows[step] = rows;
        }
        rows.Add(row);
        return row;
    }

    private Control CreateInfoNote(string name, string text, int logicalHeight)
    {
        var note = new RoundedPanel
        {
            Name = name,
            Dock = DockStyle.Top,
            Height = ScaleLogical(logicalHeight),
            MinimumSize = new Size(0, ScaleLogical(logicalHeight)),
            Margin = Padding.Empty,
            Padding = new Padding(ScaleLogical(11), ScaleLogical(9), ScaleLogical(11), ScaleLogical(9)),
            BackColor = Color.FromArgb(16, 27, 45),
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleLogical(9)
        };
        note.Controls.Add(new Label
        {
            Name = $"{name}Text",
            Text = text,
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = false,
            ForeColor = ClipCordTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = ClipCordTheme.InterfaceFont(8f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            UseMnemonic = false
        });
        return note;
    }

    private void WireExclusiveGroup(params RadioButton[] choices)
    {
        foreach (var choice in choices)
        {
            choice.CheckedChanged += (_, _) =>
            {
                if (choice.Checked)
                {
                    foreach (var other in choices)
                    {
                        if (!ReferenceEquals(choice, other)) other.Checked = false;
                    }
                }
                if (ReferenceEquals(choice, _discord) || ReferenceEquals(choice, _localOnly))
                    UpdateDestinationOptions();
                else
                    UpdateSummary();
                UpdateChoiceCardStyles();
            };
        }
    }

    private RoutingInputSourceDisplay? SelectedNamedSource =>
        _watchedFolder.Checked &&
        _watchedSource.SelectedIndex is var selectedIndex &&
        selectedIndex >= 0 && selectedIndex < _watchedSource.Items.Count &&
        _watchedSource.Items[selectedIndex] is WatchedSourceChoice
            { Selectable: true, Source: { } source }
            ? source
            : null;

    private RoutingInputSourceDisplay? SelectedXboxSource =>
        SelectedNamedSource is { Kind: RoutingInputSourceKind.XboxGameDvrOneDrive } source
            ? source
            : null;

    private XboxDvrHistoryWindow SelectedHistoryWindow =>
        _historyLastWeek.Checked
            ? XboxDvrHistoryWindow.Last7Days
            : _historyLastDay.Checked
                ? XboxDvrHistoryWindow.Last24Hours
                : XboxDvrHistoryWindow.NewOnly;

    private XboxDvrHistoryPolicy CreateHistoryPolicy(DateTimeOffset? activationUtc = null) =>
        XboxDvrHistoryPolicy.Create(
            SelectedHistoryWindow,
            activationUtc ?? _historyActivationUtc,
            XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips);

    private void HistorySelectionChanged()
    {
        if (!_historyNewOnly.Checked && !_historyLastDay.Checked && !_historyLastWeek.Checked)
            return;
        UpdateChoiceCardStyles();
        _latestPreflight = null;
        BeginXboxPreflight();
        UpdateSummary();
    }

    private void UpdateTriggerOptions()
    {
        if (_sourceConfiguration is null || _historyConfiguration is null) return;
        SetConditionalVisibility(_sourceConfiguration, _watchedFolder.Checked);
        var showHistory = SelectedXboxSource is not null;
        UpdateSourceSafetyNote();
        UpdatePrepareAvailability(showHistory);
        SetConditionalVisibility(_historyConfiguration, showHistory);
        _latestPreflight = null;
        if (showHistory)
        {
            BeginXboxPreflight();
        }
        else
        {
            _preflightCancellation?.Cancel();
            _xboxPreflightCompletion = Task.CompletedTask;
            _xboxPreflightStatus.Text =
                "Choose a history window to preview matching clips.";
        }
        UpdateSummary();
        if (_steps is not null && _stageScroll is not null)
            _stageScroll.RefreshContentLayout(preservePosition: true);
    }

    private void UpdateSourceSafetyNote()
    {
        if (_sourceSafetyNote is null) return;
        _sourceSafetyNote.Text = SelectedNamedSource?.Kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg =>
                "SteelSeries GG watches MP4 files directly in this folder. Existing clips stay as the baseline; only new completed clips start this route.",
            RoutingInputSourceKind.Nvidia =>
                "NVIDIA watches <Game>\\<clip>.mp4 exactly one folder below this root. Existing clips stay as the baseline; only new completed clips start this route.",
            RoutingInputSourceKind.XboxGameDvrOneDrive =>
                "Xbox imports eligible clips into ClipCord's Library. The OneDrive original stays in place, and previewing history reads metadata only.",
            _ =>
                "This migrated recorder folder keeps its established structure. Add a named source in Routes → Connections to watch a different folder."
        };
    }

    private void UpdatePrepareAvailability(bool xboxSourceSelected)
    {
        if (xboxSourceSelected) _original.Checked = true;
        foreach (var reactionOutput in new[] { _landscape, _portrait })
        {
            reactionOutput.Enabled = !xboxSourceSelected;
            reactionOutput.TabStop = !xboxSourceSelected;
            reactionOutput.AccessibleDescription = xboxSourceSelected
                ? "Unavailable for Xbox Game DVR clips because they do not include a ClipCord Reaction Camera rendition."
                : null;
        }
        _original.AccessibleDescription = xboxSourceSelected
            ? "Xbox Game DVR routes use the original console clip."
            : null;
        UpdateChoiceCardStyles();
    }

    private async void BeginXboxPreflight()
    {
        var source = SelectedXboxSource;
        if (source is null || _xboxPreflight is null)
        {
            _xboxPreflightCompletion = Task.CompletedTask;
            _xboxPreflightStatus.Text =
                "Xbox metadata preview is unavailable. The route cannot be saved yet.";
            UpdateSummary();
            return;
        }
        _preflightCancellation?.Cancel();
        _preflightCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        _preflightCancellation = cancellation;
        var generation = Interlocked.Increment(ref _preflightGeneration);
        var policy = CreateHistoryPolicy();
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _xboxPreflightCompletion = completion.Task;
        _xboxPreflightStatus.Text = "Checking Xbox clip metadata… no files are being downloaded.";
        UpdateSummary();
        try
        {
            var preview = await _xboxPreflightTaskRunner(
                    () => _xboxPreflight(source, policy, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            if (IsDisposed || Disposing || cancellation.IsCancellationRequested ||
                generation != Volatile.Read(ref _preflightGeneration) ||
                SelectedXboxSource?.SourceId != source.SourceId)
            {
                return;
            }
            _latestPreflight = preview;
            UpdateXboxPreflightStatus();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            if (generation != Volatile.Read(ref _preflightGeneration)) return;
            _latestPreflight = null;
            _xboxPreflightStatus.Text =
                "Xbox metadata could not be checked. Confirm OneDrive is running, then choose the source again.";
            Log.Error("Xbox route metadata preflight failed.", exception);
        }
        finally
        {
            if (!IsDisposed && !Disposing &&
                generation == Volatile.Read(ref _preflightGeneration))
            {
                UpdateSummary();
            }
            completion.TrySetResult(null);
        }
    }

    internal Task XboxPreflightCompletion => _xboxPreflightCompletion;

    internal int XboxPreflightGeneration => Volatile.Read(ref _preflightGeneration);

    internal string XboxPreflightReadinessDiagnostic
    {
        get
        {
            var source = SelectedXboxSource;
            var preview = _latestPreflight;
            var currentPolicy = CreateHistoryPolicy();
            return
                $"sourceSelected={source is not null}; " +
                $"previewPresent={preview is not null}; " +
                $"sourceMatches={source is not null && preview?.SourceId == source.SourceId}; " +
                $"selectedWindow={currentPolicy.Window}; " +
                $"previewWindow={preview?.Policy.Window.ToString() ?? "none"}; " +
                $"policyMatches={preview?.Policy == currentPolicy}; " +
                $"selectedActivation={currentPolicy.ActivationUtc:O}; " +
                $"previewActivation={(preview is null ? "none" : preview.Policy.ActivationUtc.ToString("O"))}";
        }
    }

    internal bool XboxPreflightReadyForCurrentSelection =>
        _xboxPreflightCompletion.IsCompleted &&
        SelectedXboxSource is { } source &&
        _latestPreflight is { } preview &&
        preview.SourceId == source.SourceId &&
        preview.Policy == CreateHistoryPolicy();

    private void UpdateXboxPreflightStatus()
    {
        if (_latestPreflight is not { } preview) return;
        var selectedMatchCount = GetSelectedXboxWindowMatchCount(preview);
        if (selectedMatchCount > XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips)
        {
            _xboxPreflightStatus.Text =
                $"{selectedMatchCount:N0} clips match · shorten history before saving · maximum {XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips:N0}";
            return;
        }
        if (preview.Policy.Window == XboxDvrHistoryWindow.NewOnly)
        {
            _xboxPreflightStatus.Text =
                $"{preview.TotalClipCount} clips found · existing clips stay untouched · new clips start with this route";
        }
        else
        {
            var impact = GetSelectedXboxHistoryImpact(preview);
            _xboxPreflightStatus.Text =
                $"{preview.TotalClipCount} clips found · {impact.Count} match · about {FormatLogicalBytes(impact.LogicalBytes)} may download";
        }
        if (preview.NeedsAttentionCount > 0)
        {
            _xboxPreflightStatus.Text +=
                $" · {preview.NeedsAttentionCount} unsupported name{(preview.NeedsAttentionCount == 1 ? string.Empty : "s")} skipped";
        }
    }

    private (int Count, long LogicalBytes) GetSelectedXboxHistoryImpact(
        XboxDvrPreflightPreview preview)
    {
        var count = 0;
        long logicalBytes = 0;
        foreach (var item in GetSelectedXboxHistoricalItems(preview))
        {
            count++;
            if (item.LogicalBytes <= 0) continue;
            logicalBytes = item.LogicalBytes > long.MaxValue - logicalBytes
                ? long.MaxValue
                : logicalBytes + item.LogicalBytes;
        }
        return (count, logicalBytes);
    }

    private int GetSelectedXboxWindowMatchCount(XboxDvrPreflightPreview preview) =>
        string.IsNullOrWhiteSpace(_game.Text)
            ? preview.WindowMatchCount
            : GetSelectedXboxHistoricalItems(preview).Count;

    private IReadOnlyList<XboxDvrPreflightItem> GetSelectedXboxHistoricalItems(
        XboxDvrPreflightPreview preview)
    {
        var selectedGame = _game.Text.Trim();
        if (preview.Policy.Window == XboxDvrHistoryWindow.NewOnly) return [];
        if (selectedGame.Length == 0) return preview.EligibleHistorical;
        return preview.Items
            .Where(item => item.NeedsAttentionReason == XboxDvrNeedsAttentionReason.None &&
                           item.CapturedUtc is { } capturedUtc &&
                           capturedUtc >= preview.Policy.HistoricalCutoffUtc &&
                           capturedUtc <= preview.Policy.ActivationUtc &&
                           item.GameName is { } itemGame &&
                           XboxDvrGameMatch.Matches(
                               itemGame,
                               RoutingConditionOperator.Equals,
                               selectedGame))
            .ToArray();
    }

    private static void SetConditionalVisibility(Control control, bool visible)
    {
        control.Visible = visible;
        control.TabStop = visible;
        foreach (Control child in control.Controls)
            SetDescendantTabAvailability(child, visible);
    }

    private static void SetDescendantTabAvailability(Control control, bool available)
    {
        if (control is TextBoxBase or ComboBox or ButtonBase)
            control.TabStop = available && control.Enabled;
        foreach (Control child in control.Controls)
            SetDescendantTabAvailability(child, available);
    }

    private void DrawWatchedSourceItem(object? sender, DrawItemEventArgs eventArgs)
    {
        eventArgs.DrawBackground();
        if (eventArgs.Index < 0 || eventArgs.Index >= _watchedSource.Items.Count) return;
        var choice = (WatchedSourceChoice)_watchedSource.Items[eventArgs.Index]!;
        var selected = (eventArgs.State & DrawItemState.Selected) != 0;
        var unavailable = !choice.Selectable;
        using var titleBrush = new SolidBrush(
            unavailable
                ? ClipCordTheme.TextTertiary
                : selected ? Color.White : ClipCordTheme.TextPrimary);
        using var detailBrush = new SolidBrush(
            unavailable
                ? Color.FromArgb(186, 128, 73)
                : selected ? Color.FromArgb(226, 216, 244) : ClipCordTheme.TextSecondary);
        var titleFont = ClipCordTheme.InterfaceFont(8.75f, FontStyle.Bold);
        var detailFont = ClipCordTheme.InterfaceFont(7.5f);
        var left = eventArgs.Bounds.Left + ScaleLogical(8);
        var titleBounds = new Rectangle(
            left,
            eventArgs.Bounds.Top + ScaleLogical(2),
            Math.Max(1, eventArgs.Bounds.Width - ScaleLogical(16)),
            ScaleLogical(15));
        var detailBounds = new Rectangle(
            left,
            eventArgs.Bounds.Top + ScaleLogical(16),
            Math.Max(1, eventArgs.Bounds.Width - ScaleLogical(16)),
            ScaleLogical(13));
        eventArgs.Graphics.DrawString(
            choice.Name, titleFont,
            titleBrush, titleBounds);
        eventArgs.Graphics.DrawString(
            choice.Detail, detailFont,
            detailBrush, detailBounds);
        eventArgs.DrawFocusRectangle();
    }

    private void WatchedSourceSelectionChanged(object? sender, EventArgs eventArgs)
    {
        if (_restoringWatchedSourceSelection || _watchedSource.SelectedIndex < 0) return;
        if (_watchedSource.SelectedItem is WatchedSourceChoice { Selectable: true })
        {
            _lastSelectableWatchedSourceIndex = _watchedSource.SelectedIndex;
            _watchedSource.AccessibleDescription = null;
            UpdateTriggerOptions();
            return;
        }

        var unavailableChoice = (WatchedSourceChoice)_watchedSource.SelectedItem!;
        _restoringWatchedSourceSelection = true;
        try
        {
            _watchedSource.SelectedIndex = Math.Clamp(
                _lastSelectableWatchedSourceIndex,
                0,
                Math.Max(0, _watchedSource.Items.Count - 1));
            _watchedSource.AccessibleDescription =
                unavailableChoice.Source?.Retired == true
                    ? "That replaced clip source is retained for existing routes and cannot be selected for a new route."
                    : "That clip source needs attention. Recheck it from the Connections tab before creating a route.";
        }
        finally
        {
            _restoringWatchedSourceSelection = false;
        }
        UpdateTriggerOptions();
    }

    private static string FormatLogicalBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        var mebibytes = bytes / (1024d * 1024d);
        return mebibytes < 1024d
            ? $"{Math.Ceiling(mebibytes):N0} MB"
            : $"{mebibytes / 1024d:N1} GB";
    }

    private void WireCardSelection(Control control, RadioButton choice)
    {
        if (!ReferenceEquals(control, choice))
        {
            control.Cursor = choice.Enabled ? Cursors.Hand : Cursors.Default;
            control.Click += (_, _) =>
            {
                if (choice.Enabled) choice.Checked = true;
            };
        }
        foreach (Control child in control.Controls) WireCardSelection(child, choice);
    }

    private void UpdateChoiceCardStyles()
    {
        foreach (var (choice, card) in _choiceCards)
        {
            var selected = choice.Checked;
            card.Cursor = choice.Enabled ? Cursors.Hand : Cursors.Default;
            card.BackColor = !choice.Enabled
                ? Color.FromArgb(18, 27, 43)
                : selected
                    ? ClipCordTheme.VioletMuted
                    : ClipCordTheme.SurfaceRaised;
            card.BorderColor = selected && choice.Enabled
                ? ClipCordTheme.Violet
                : ClipCordTheme.BorderDefault;
            card.AccessibilityUnavailable = !choice.Enabled;
            UpdateDescendantSurfaces(card, card.BackColor);
            foreach (var icon in EnumerateControls(card).OfType<FigmaIconControl>())
                icon.IconColor = choice.Enabled
                    ? selected ? Color.FromArgb(196, 154, 255) : ClipCordTheme.TextSecondary
                    : ClipCordTheme.TextTertiary;
            card.Invalidate();
        }
    }

    private void UpdateDestinationOptions()
    {
        if (!_discord.Enabled && _discord.Checked)
            _localOnly.Checked = true;
        _fileIntoLibrary.Checked = true;
        _fileIntoLibrary.Enabled = false;
        _fileIntoLibrary.TabStop = false;
        _approval.Enabled = _discord.Checked;
        _approval.TabStop = _approval.Enabled;
        _discordConnection.Enabled = _discord.Checked && _connections.Count > 0;
        _discordConnection.TabStop = _discordConnection.Enabled;
        UpdateChoiceCardStyles();
        UpdateSummary();
    }

    private Control BuildAlwaysOnLibraryRow()
    {
        var row = new BufferedTableLayoutPanel
        {
            Name = "RouteLibraryAlwaysOnRow",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised,
            AccessibleName = "File into Library after the route finishes. Always on."
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(28)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var checkedBadge = new RoundedPanel
        {
            Anchor = AnchorStyles.None,
            Size = new Size(ScaleLogical(18), ScaleLogical(18)),
            MinimumSize = new Size(ScaleLogical(18), ScaleLogical(18)),
            MaximumSize = new Size(ScaleLogical(18), ScaleLogical(18)),
            BackColor = ClipCordTheme.SuccessSurface,
            BorderColor = ClipCordTheme.SuccessBorder,
            CornerRadius = ScaleLogical(5),
            Padding = new Padding(ScaleLogical(3))
        };
        checkedBadge.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.Check,
            IconColor = ClipCordTheme.SuccessText,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        });
        var label = CreateTextLabel(
            "RouteLibraryAlwaysOnLabel",
            "File into Library after the route finishes  ·  Always on",
            8.75f,
            ClipCordTheme.TextSecondary);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
        row.Controls.Add(checkedBadge, 0, 0);
        row.Controls.Add(label, 1, 0);
        return row;
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 1, 3);
        _furthestStep = Math.Max(_furthestStep, _step);
        _stageScroll.Content = _steps[_step - 1];
        _headerSubtitle.Text = _step switch
        {
            1 => "Step 1 of 3 · Choose what starts this route",
            2 => "Step 2 of 3 · Choose the output this route prepares",
            _ => "Step 3 of 3 · Choose where the output goes"
        };
        _stepCounter.Text = $"Step {_step} of 3";
        _backButton.Visible = _step > 1;
        _backButton.TabStop = _backButton.Visible;
        _nextButton.Text = _step switch
        {
            1 => "Next: Prepare outputs  →",
            2 => "Next: Destination action  →",
            _ => "Save route"
        };
        _headerSaveButton.Enabled = _step == 3 && CanSave;
        foreach (var (rowStep, rows) in _summaryRows)
        {
            foreach (var row in rows)
            {
                row.BackColor = rowStep == _step
                    ? ClipCordTheme.VioletMuted
                    : Color.FromArgb(13, 22, 38);
                row.BorderColor = rowStep == _step
                    ? ClipCordTheme.Violet
                    : ClipCordTheme.BorderDefault;
                UpdateDescendantSurfaces(row, row.BackColor);
                row.Invalidate();
            }
        }
        UpdateChoiceCardStyles();
        UpdateSummary();
        _stageScroll.RefreshContentLayout(preservePosition: false);
    }

    private void UpdateSummary()
    {
        if (_summaryWhen is null) return;
        _summaryWhen.Text = _manualRecording.Checked
            ? "Manual Recording"
            : _watchedFolder.Checked
                ? SelectedNamedSource is { } namedSource
                    ? namedSource.Kind switch
                    {
                        RoutingInputSourceKind.XboxGameDvrOneDrive => "Xbox · OneDrive",
                        RoutingInputSourceKind.SteelSeriesGg => "SteelSeries GG",
                        RoutingInputSourceKind.Nvidia => "NVIDIA",
                        _ => "External watched folder"
                    }
                    : _migratedInputSource?.Name ?? "Migrated watched folder"
                : _anyClip.Checked
                    ? "Any new source clip"
                    : "Instant Replay";
        var game = _game.Text.Trim();
        if (SelectedXboxSource is not null)
        {
            var history = SelectedHistoryWindow switch
            {
                XboxDvrHistoryWindow.Last24Hours => "Last 24h",
                XboxDvrHistoryWindow.Last7Days => "Last 7d",
                _ => "From now"
            };
            _summaryIf.Text =
                $"{(game.Length == 0 ? "Any game" : Truncate(game, 16))} · {history}";
        }
        else
        {
            _summaryIf.Text = game.Length == 0
                ? "No conditions"
                : $"Game · {Truncate(game, 24)}";
        }
        _summaryPrepare.Text = _furthestStep < 2
            ? "Not set yet"
            : _landscape.Checked
                ? "Landscape"
                : _portrait.Checked
                    ? "Portrait"
                    : "Original";
        _summaryThen.Text = _furthestStep < 3
            ? "Not set yet"
            : _discord.Checked
                ? _discordConnection.SelectedConnection is { } connection
                    ? $"Discord · {Truncate(connection.Name, 17)}"
                    : "Discord · choose connection"
                : "Library only";
        _headerSaveButton.Enabled = _step == 3 && CanSave;
        _nextButton.Enabled = _step < 3 || CanSave;
    }

    private bool CanSave =>
        !string.IsNullOrWhiteSpace(_name.Text) &&
        (!_discord.Checked || _discordConnection.SelectedConnection is not null) &&
        (SelectedXboxSource is null ||
         _latestPreflight is { } preview &&
         GetSelectedXboxWindowMatchCount(preview) <=
            XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips);

    private void SaveDraft()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _showSaveGuardMessage(
                "Give this route a name.",
                "Route name required",
                MessageBoxIcon.Information);
            _name.Focus();
            return;
        }
        var connection = _discord.Checked
            ? _discordConnection.SelectedConnection
            : null;
        if (_discord.Checked && connection is null)
        {
            ShowStep(3);
            _showSaveGuardMessage(
                "Choose the Discord connection this route should use.",
                "Choose a Discord connection",
                MessageBoxIcon.Warning);
            _discordConnection.Focus();
            return;
        }
        var selectedNamedSource = SelectedNamedSource;
        var xboxSource = SelectedXboxSource;
        XboxDvrHistoryPolicy? xboxHistory = null;
        IReadOnlyList<RoutingXboxHistoricalOccurrence>? xboxHistoricalOccurrences = null;
        if (xboxSource is not null)
        {
            var previewHistory = CreateHistoryPolicy();
            if (_latestPreflight is null ||
                _latestPreflight.SourceId != xboxSource.SourceId ||
                _latestPreflight.Policy != previewHistory)
            {
                ShowStep(1);
                _showSaveGuardMessage(
                    "Wait for ClipCord to finish the metadata-only Xbox preview before saving this route.",
                    "Xbox preview is still loading",
                    MessageBoxIcon.Information);
                return;
            }
            _preflightCancellation?.Cancel();
            Interlocked.Increment(ref _preflightGeneration);
            xboxHistory = CreateHistoryPolicy(RoutingValidation.Utc(_clock()));
            try
            {
                _latestPreflight = _xboxPreflight?.Invoke(
                    xboxSource,
                    xboxHistory,
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException or
                    System.Security.SecurityException)
            {
                _latestPreflight = null;
                ShowStep(1);
                _xboxPreflightStatus.Text =
                    "Xbox metadata changed or became unavailable while saving. Check OneDrive, then try again.";
                Log.Error("Xbox route final metadata snapshot failed.", exception);
                return;
            }
            if (_latestPreflight is null ||
                _latestPreflight.SourceId != xboxSource.SourceId ||
                _latestPreflight.Policy != xboxHistory)
            {
                ShowStep(1);
                _xboxPreflightStatus.Text =
                    "ClipCord could not freeze the final Xbox metadata snapshot. Try saving again.";
                return;
            }
            UpdateXboxPreflightStatus();
            if (GetSelectedXboxWindowMatchCount(_latestPreflight) >
                XboxDvrHistoryPolicy.MaximumAllowedHistoricalClips)
            {
                ShowStep(1);
                UpdateXboxPreflightStatus();
                _historyConfiguration?.Focus();
                return;
            }
            xboxHistoricalOccurrences = xboxHistory.Window == XboxDvrHistoryWindow.NewOnly
                ? []
                : GetSelectedXboxHistoricalItems(_latestPreflight)
                    .Select(item => new RoutingXboxHistoricalOccurrence(
                        item.OccurrenceIdentitySha256,
                        item.RevisionIdentitySha256))
                    .Distinct()
                    .OrderBy(item => item.OccurrenceId, StringComparer.Ordinal)
                    .ThenBy(item => item.RevisionId, StringComparer.Ordinal)
                    .ToArray();
            var historyImpact = GetSelectedXboxHistoryImpact(_latestPreflight);
            if (historyImpact.Count > 0)
            {
                var destination = _discord.Checked
                    ? _discordConnection.SelectedConnection is { } selected
                        ? $"Discord through {selected.Name}, then file them into your Library"
                        : "Discord, then file them into your Library"
                    : "your local Library";
                var confirmation = _confirmXboxHistory(
                    $"This route will retrieve {historyImpact.Count} existing Xbox clip{(historyImpact.Count == 1 ? string.Empty : "s")} currently found ({FormatLogicalBytes(historyImpact.LogicalBytes)}) and send them to {destination}. Only this displayed historical set is approved; later-arriving older clips are not added automatically. New matching clips captured after activation continue automatically. The OneDrive originals will stay in place.\n\nActivate this history window?",
                    "Import Xbox clip history?");
                if (confirmation != DialogResult.Yes) return;
            }
        }
        var trigger = _manualRecording.Checked
            ? RoutingTriggerKind.ManualRecording
            : _watchedFolder.Checked
                ? RoutingTriggerKind.WatchedFolder
                : _anyClip.Checked
                    ? RoutingTriggerKind.AnyNewSourceClip
                    : RoutingTriggerKind.InstantReplay;
        var output = _landscape.Checked
            ? RoutingOutputKind.Landscape
            : _portrait.Checked
                ? RoutingOutputKind.Portrait
                : RoutingOutputKind.Original;
        Draft = new RoutingRouteDraft(
            _name.Text,
            trigger,
            string.IsNullOrWhiteSpace(_game.Text) ? null : _game.Text,
            _discord.Checked ? RoutingDestinationKind.Discord : null,
            connection?.ConnectionId,
            output,
            _discord.Checked && _approval.Checked
                ? RoutingDeliveryMode.Approval
                : RoutingDeliveryMode.Automatic,
            RoutingMissingOutputBehavior.UseOriginal,
            FileIntoLibrary: true,
            WatchedSourceId: selectedNamedSource?.SourceId,
            EarliestCapturedUtc: xboxHistory?.HistoricalCutoffUtc,
            XboxHistorySelection: xboxSource is null || xboxHistory is null ||
                                  xboxHistoricalOccurrences is null
                ? null
                : new RoutingXboxHistorySelection(
                    xboxHistory.ActivationUtc,
                    xboxHistoricalOccurrences),
            WatchedSourceKind: selectedNamedSource?.Kind);
        DialogResult = DialogResult.OK;
    }

    internal void FitToWorkingArea(Rectangle workingArea)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0) return;
        var maximum = new Size(
            Math.Max(1, workingArea.Width - ScaleLogical(24)),
            Math.Max(1, workingArea.Height - ScaleLogical(24)));
        var desired = new Size(ScaleLogical(BaseWidth), ScaleLogical(BaseHeight));
        var fitted = new Size(
            Math.Min(desired.Width, maximum.Width),
            Math.Min(desired.Height, maximum.Height));
        MinimumSize = new Size(
            Math.Min(ScaleLogical(820), fitted.Width),
            Math.Min(ScaleLogical(540), fitted.Height));
        MaximumSize = maximum;
        Size = fitted;
        var centeringBounds = Owner is { Visible: true } owner
            ? Rectangle.Intersect(owner.Bounds, workingArea)
            : workingArea;
        if (centeringBounds.Width <= 0 || centeringBounds.Height <= 0)
            centeringBounds = workingArea;
        Location = new Point(
            centeringBounds.Left + Math.Max(0, (centeringBounds.Width - Width) / 2),
            centeringBounds.Top + Math.Max(0, (centeringBounds.Height - Height) / 2));
        _stageScroll.RefreshContentLayout();
    }

    private TextBox CreateEditor(string name, string accessibleName, string placeholder) => new()
    {
        Name = name,
        AccessibleName = accessibleName,
        PlaceholderText = placeholder,
        Dock = DockStyle.Fill,
        AutoSize = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = ClipCordTheme.SurfaceSunken,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Margin = Padding.Empty
    };

    private RadioButton CreateRadio(string name, string accessibleName, bool selected = false) => new RouteChoiceRadioButton()
    {
        Name = name,
        AccessibleName = accessibleName,
        Text = string.Empty,
        Checked = selected,
        AutoSize = false,
        ForeColor = ClipCordTheme.TextPrimary,
        BackColor = Color.Transparent,
        Font = ClipCordTheme.InterfaceFont(9f),
        Margin = Padding.Empty,
        UseVisualStyleBackColor = true
    };

    private CheckBox CreateCheck(string name, string text, bool selected = false) => new()
    {
        Name = name,
        AccessibleName = text,
        Text = text,
        Checked = selected,
        AutoSize = false,
        ForeColor = ClipCordTheme.TextSecondary,
        BackColor = ClipCordTheme.SurfaceRaised,
        Font = ClipCordTheme.InterfaceFont(8.75f),
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private Label CreateEyebrow(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = true,
        ForeColor = ClipCordTheme.TextSecondary,
        BackColor = Color.Transparent,
        Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private static Label CreateTextLabel(
        string name,
        string text,
        float size,
        Color color,
        FontStyle style = FontStyle.Regular) => new()
    {
        Name = name,
        Text = text,
        AutoSize = false,
        ForeColor = color,
        BackColor = Color.Transparent,
        Font = ClipCordTheme.InterfaceFont(size, style),
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private static Label CreateSummaryValueLabel(string name) => new()
    {
        Name = name,
        AutoSize = false,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.TextPrimary,
        BackColor = Color.Transparent,
        Font = ClipCordTheme.InterfaceFont(8.25f),
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private Control CreateDivider() => new Panel
    {
        Dock = DockStyle.Top,
        Height = ScaleLogical(1),
        MinimumSize = new Size(0, ScaleLogical(1)),
        BackColor = ClipCordTheme.BorderDefault,
        Margin = Padding.Empty
    };

    private GradientButton CreatePrimaryButton(string name, string text, int logicalWidth) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = text,
        AutoSize = false,
        Size = new Size(ScaleLogical(logicalWidth), ScaleLogical(36)),
        MinimumSize = new Size(ScaleLogical(logicalWidth), ScaleLogical(36)),
        Margin = Padding.Empty
    };

    private OutlineButton CreateSecondaryButton(string name, string text, int logicalWidth) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = text,
        AutoSize = false,
        Size = new Size(ScaleLogical(logicalWidth), ScaleLogical(36)),
        MinimumSize = new Size(ScaleLogical(logicalWidth), ScaleLogical(36)),
        Margin = Padding.Empty,
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary
    };

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateControls(child)) yield return descendant;
        }
    }

    private static void UpdateDescendantSurfaces(Control root, Color color)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TableLayoutPanel or FlowLayoutPanel)
                child.BackColor = color;
            UpdateDescendantSurfaces(child, color);
        }
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : $"{value[..Math.Max(1, maximumCharacters - 1)]}…";

    internal static int ScaleLogicalMetric(int value, int dpi) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, dpi) / 96d));

    private int ScaleLogical(int value) => ScaleLogicalMetric(value, _layoutDpi);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Increment(ref _preflightGeneration);
            _preflightCancellation?.Cancel();
            _preflightCancellation?.Dispose();
            _preflightCancellation = null;
            foreach (var step in _steps)
            {
                if (!step.IsDisposed) step.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private sealed record WatchedSourceChoice(
        string Name,
        string Detail,
        RoutingInputSourceDisplay? Source,
        bool Selectable)
    {
        public override string ToString() => Selectable
            ? Name
            : Source?.Retired == true
                ? $"{Name} · Replaced"
                : $"{Name} · Needs attention";
    }

    private sealed class RouteChoiceRadioButton : RadioButton
    {
        internal RouteChoiceRadioButton()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor,
                true);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(Parent?.BackColor ?? ClipCordTheme.SurfaceRaised);
            eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var diameter = Math.Min(
                Math.Max(12, (int)Math.Round(16 * DeviceDpi / 96d)),
                Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height) - 2));
            var bounds = new Rectangle(
                Math.Max(1, (ClientSize.Width - diameter) / 2),
                Math.Max(1, (ClientSize.Height - diameter) / 2),
                diameter,
                diameter);
            using var border = new Pen(
                Enabled && Checked ? ClipCordTheme.Violet : ClipCordTheme.TextTertiary,
                Math.Max(1f, DeviceDpi / 96f));
            eventArgs.Graphics.DrawEllipse(border, bounds);
            if (Checked)
            {
                var inset = Math.Max(3, diameter / 4);
                using var fill = new SolidBrush(Enabled
                    ? Color.FromArgb(196, 154, 255)
                    : ClipCordTheme.TextTertiary);
                eventArgs.Graphics.FillEllipse(fill, Rectangle.Inflate(bounds, -inset, -inset));
            }
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(bounds, 3, 3));
        }
    }

}

internal sealed class RouteConnectionSelector : Control
{
    private readonly IReadOnlyList<RoutingConnectionDisplay> _connections;
    private readonly RouteSelectorMenu _menu;
    private readonly Action<ContextMenuStrip, Control, Point> _popupPresenter;
    private int _selectedIndex = -1;
    private bool _hovered;

    internal RouteConnectionSelector(
        IReadOnlyList<RoutingConnectionDisplay> connections,
        Action<ContextMenuStrip, Control, Point>? popupPresenter = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _popupPresenter = popupPresenter ??
            ((menu, owner, location) => menu.Show(owner, location));
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ComboBox;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint, true);

        _menu = new RouteSelectorMenu
        {
            AutoSize = false,
            BackColor = ClipCordTheme.SurfaceRaised,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9.25f),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(4),
            Renderer = new ToolStripProfessionalRenderer(new RouteSelectorColorTable())
        };
        for (var index = 0; index < _connections.Count; index++)
        {
            var captured = index;
            var item = new ToolStripMenuItem(FormatConnection(_connections[index]))
            {
                AutoSize = false,
                Height = 32,
                BackColor = ClipCordTheme.SurfaceRaised,
                ForeColor = ClipCordTheme.TextPrimary,
                Margin = Padding.Empty,
                Padding = new Padding(8, 0, 8, 0)
            };
            item.Click += (_, _) => SelectedIndex = captured;
            _menu.Items.Add(item);
        }
    }

    internal event EventHandler? SelectedIndexChanged;

    internal int ItemCount => _connections.Count;

    internal int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < -1 || value >= _connections.Count)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal RoutingConnectionDisplay? SelectedConnection =>
        _selectedIndex >= 0 && _selectedIndex < _connections.Count
            ? _connections[_selectedIndex]
            : null;

    public override Size GetPreferredSize(Size proposedSize) => new(
        Math.Max(120, proposedSize.Width),
        Math.Max(34, (int)Math.Round(36 * DeviceDpi / 96d)));

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        var backgroundColor = !Enabled
            ? ClipCordTheme.SurfaceSunken
            : _hovered || Focused
                ? ClipCordTheme.SurfaceControlHover
                : ClipCordTheme.SurfaceSunken;
        using (var background = new SolidBrush(backgroundColor))
            eventArgs.Graphics.FillRectangle(background, bounds);
        using (var border = new Pen(
                   Focused && Enabled ? ClipCordTheme.Violet : ClipCordTheme.BorderStrong,
                   Math.Max(1f, DeviceDpi / 96f)))
            eventArgs.Graphics.DrawRectangle(border, bounds);

        var arrowWidth = Math.Max(24, (int)Math.Round(30 * DeviceDpi / 96d));
        var textBounds = new Rectangle(
            Math.Max(8, (int)Math.Round(10 * DeviceDpi / 96d)),
            0,
            Math.Max(1, Width - arrowWidth - Math.Max(14, (int)Math.Round(18 * DeviceDpi / 96d))),
            Height);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            SelectedConnection is { } connection
                ? FormatConnection(connection)
                : "Choose a Discord connection",
            Font,
            textBounds,
            Enabled ? ClipCordTheme.TextPrimary : ClipCordTheme.TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        var side = Math.Max(10, (int)Math.Round(13 * DeviceDpi / 96d));
        var state = eventArgs.Graphics.Save();
        eventArgs.Graphics.TranslateTransform(
            Width - arrowWidth / 2f,
            Height / 2f);
        eventArgs.Graphics.RotateTransform(90f);
        FigmaIconRenderer.Draw(
            eventArgs.Graphics,
            new Rectangle(-side / 2, -side / 2, side, side),
            FigmaIconAsset.ChevronRight,
            Enabled ? ClipCordTheme.TextSecondary : ClipCordTheme.TextTertiary);
        eventArgs.Graphics.Restore(state);
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        base.OnClick(eventArgs);
        if (Enabled) ShowMenu();
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (Enabled && eventArgs.KeyCode is Keys.Enter or Keys.Space or Keys.Down)
        {
            ShowMenu();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override void OnGotFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnGotFocus(eventArgs);
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnLostFocus(eventArgs);
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new RouteConnectionSelectorAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }

    internal static int ScalePopupMetric(int value, int dpi) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, dpi) / 96d));

    internal void ApplyPopupMetrics(int dpi)
    {
        dpi = Math.Max(96, dpi);
        LastAppliedPopupDpi = dpi;
        _menu.ApplyLayoutDpi(dpi);
        _menu.Width = Math.Max(Width, ScalePopupMetric(180, dpi));
        foreach (ToolStripItem item in _menu.Items)
        {
            item.Height = ScalePopupMetric(32, dpi);
            item.Width = _menu.ClientSize.Width - ScalePopupMetric(8, dpi);
            item.Margin = new Padding(
                ScalePopupMetric(4, dpi), 0,
                ScalePopupMetric(4, dpi), 0);
            item.Padding = new Padding(
                ScalePopupMetric(8, dpi), 0,
                ScalePopupMetric(8, dpi), 0);
        }
        _menu.Height = _menu.Items.Cast<ToolStripItem>()
                           .Sum(item => item.Height + item.Margin.Vertical) +
                       ScalePopupMetric(10, dpi);
    }

    internal int LastAppliedPopupDpi { get; private set; }
    internal Padding PopupPadding => _menu.Padding;
    internal int PopupWidth => _menu.Width;
    internal int PopupClientWidth => _menu.ClientSize.Width;
    internal int PopupHeight => _menu.Height;
    internal IReadOnlyList<(int Width, int Height, Padding Margin, Padding Padding)> PopupItemMetrics =>
        _menu.Items.Cast<ToolStripItem>()
            .Select(item => (item.Width, item.Height, item.Margin, item.Padding))
            .ToArray();

    internal void ActivatePopup() => OnClick(EventArgs.Empty);

    private void ShowMenu()
    {
        if (_connections.Count == 0 || IsDisposed || !IsHandleCreated) return;
        var dpi = DeviceDpi;
        ApplyPopupMetrics(dpi);
        _popupPresenter(_menu, this, new Point(0, Height));
    }

    private static string FormatConnection(RoutingConnectionDisplay connection)
    {
        var id = connection.ConnectionId;
        var suffix = id.Length <= 6 ? id : id[^6..];
        return $"{connection.Name} · …{suffix}";
    }

    private sealed class RouteSelectorColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => ClipCordTheme.SurfaceRaised;
        public override Color MenuBorder => ClipCordTheme.BorderStrong;
        public override Color MenuItemBorder => ClipCordTheme.Violet;
        public override Color MenuItemSelected => ClipCordTheme.VioletMuted;
        public override Color MenuItemSelectedGradientBegin => ClipCordTheme.VioletMuted;
        public override Color MenuItemSelectedGradientEnd => ClipCordTheme.VioletMuted;
        public override Color ImageMarginGradientBegin => ClipCordTheme.SurfaceRaised;
        public override Color ImageMarginGradientMiddle => ClipCordTheme.SurfaceRaised;
        public override Color ImageMarginGradientEnd => ClipCordTheme.SurfaceRaised;
    }

    private sealed class RouteSelectorMenu : ContextMenuStrip
    {
        private int _layoutDpi = 96;

        internal void ApplyLayoutDpi(int dpi)
        {
            _layoutDpi = Math.Max(96, dpi);
            Padding = DefaultPadding;
        }

        protected override Padding DefaultPadding =>
            new(ScalePopupMetric(4, _layoutDpi));
    }

    private sealed class RouteConnectionSelectorAccessibleObject(RouteConnectionSelector owner)
        : ControlAccessibleObject(owner)
    {
        public override string? Value => owner.SelectedConnection is { } connection
            ? FormatConnection(connection)
            : "No Discord connection selected";
    }
}
