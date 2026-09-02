namespace ClipsToDiscord;

/// <summary>
/// Projects the authoritative Routing history into the shared Activity vocabulary. Nothing is
/// written to activity.json: hot outbox and immutable archive evidence remain the only source of
/// truth, while PlanId supplies a stable row identity across restart and compaction.
/// </summary>
internal sealed class RoutingActivityProjection
{
    private readonly RoutingDeliveryHistoryReader _history;
    private readonly string _captureLibraryRoot;
    private readonly string _watchedRoot;
    private readonly RoutingWatchedSourceJournalStore _watchedJournals;
    private readonly Func<RoutingWatchedSourceJournalDocument, string>? _resolveWatchedRoot;
    private readonly Func<RoutingWatchedSourceJournalDocument, bool>? _isWatchedRootCurrent;

    internal RoutingActivityProjection(
        RoutingDeliveryHistoryReader history,
        string captureLibraryRoot,
        string watchedRoot,
        RoutingWatchedSourceJournalStore watchedJournals,
        Func<RoutingWatchedSourceJournalDocument, string>? resolveWatchedRoot = null,
        Func<RoutingWatchedSourceJournalDocument, bool>? isWatchedRootCurrent = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _captureLibraryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(captureLibraryRoot ?? throw new ArgumentNullException(
                nameof(captureLibraryRoot))));
        _watchedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(watchedRoot ?? throw new ArgumentNullException(nameof(watchedRoot))));
        _watchedJournals = watchedJournals ??
                           throw new ArgumentNullException(nameof(watchedJournals));
        _resolveWatchedRoot = resolveWatchedRoot;
        _isWatchedRootCurrent = isWatchedRootCurrent;
    }

    internal IReadOnlyList<ClipActivityEntry> Read(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return _history.Read(cancellationToken)
                .Select(item => ProjectBestEffort(item, cancellationToken))
                .OfType<ClipActivityEntry>()
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.Id)
                .Take(ActivityHistoryStore.MaximumEntries)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Error(
                "Could not project durable Routing history into Activity; routing will continue.",
                exception);
            return [];
        }
    }

    private ClipActivityEntry? ProjectBestEffort(
        RoutingDeliveryHistoryItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Deliveries.Count == 0 && item.FileDispositions.Count == 0) return null;
            var source = ResolveSource(item.Plan.SourceClipId, item.FileDispositions, cancellationToken);
            if (source is null) return null;
            var presentation = ProjectState(item.Deliveries, item.FileDispositions);
            if (presentation is null) return null;
            var updatedUtc = item.Deliveries.Select(value => value.UpdatedUtc)
                .Concat(item.FileDispositions.Select(value => value.UpdatedUtc))
                .Append(item.ArchivedUtc ?? item.Plan.CreatedUtc)
                .Max()
                .UtcDateTime;

            return new ClipActivityEntry
            {
                Id = item.Plan.PlanId,
                CreatedUtc = source.CapturedUtc.UtcDateTime,
                UpdatedUtc = updatedUtc,
                FileName = source.FileName,
                GameName = source.GameName,
                SourcePath = SensitiveDataRedactor.Redact(source.SourcePath),
                CurrentPath = source.CurrentPath is null
                    ? null
                    : SensitiveDataRedactor.Redact(source.CurrentPath),
                State = presentation.State,
                Route = presentation.Route,
                AttemptCount = item.Deliveries.Count == 0
                    ? item.FileDispositions.Select(value => value.Attempts).DefaultIfEmpty().Max()
                    : item.Deliveries.Select(value => value.Attempts).DefaultIfEmpty().Max(),
                Detail = presentation.Detail,
                Error = presentation.Error,
                OriginalBytes = source.OriginalBytes,
                CompressedBytes = null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Error(
                "Could not project one durable Routing plan into Activity; other plans remain visible.",
                exception);
            return null;
        }
    }

    private RoutingActivitySource? ResolveSource(
        string sourceClipId,
        IReadOnlyList<PlannedFileDisposition> dispositions,
        CancellationToken cancellationToken)
    {
        if (CaptureJournalModel.IsClipId(sourceClipId))
        {
            var loaded = CaptureJournalStore.Load(
                _captureLibraryRoot,
                sourceClipId,
                cancellationToken);
            if (!loaded.LoadedFromDisk || loaded.Document is null) return null;
            var journal = loaded.Document;
            var originalPath = CaptureJournalStore.GetCanonicalArtifactPath(
                _captureLibraryRoot,
                journal.Clip,
                "original");
            return new RoutingActivitySource(
                Path.GetFileName(originalPath),
                journal.Clip.GameName,
                originalPath,
                originalPath,
                journal.Clip.CapturedUtc,
                journal.Clip.Original.Fingerprint.ByteLength);
        }

        if (!RoutingWatchedJournalModel.IsWatchedSourceClipId(sourceClipId)) return null;
        var watchedLoad = _watchedJournals.Load(sourceClipId, cancellationToken);
        if (!watchedLoad.LoadedFromDisk || watchedLoad.Document is null) return null;
        var watched = watchedLoad.Document;
        var watchedRoot = _resolveWatchedRoot?.Invoke(watched) ??
                          (watched.SourceConnectionId is null
                              ? _watchedRoot
                              : throw new InvalidDataException(
                                  "A named watched Activity entry has no source-root resolver."));
        var sourcePath = Path.GetFullPath(Path.Combine(
            watchedRoot,
            watched.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        CaptureJournalStore.EnsurePathIsInside(
            watchedRoot,
            sourcePath,
            "watched Activity source");
        var rootIsCurrent = _isWatchedRootCurrent?.Invoke(watched) ?? true;
        var completedDisposition = dispositions
            .Where(value => value.State == PlannedFileDispositionState.Completed)
            .OrderByDescending(value => value.UpdatedUtc)
            .FirstOrDefault();
        var currentPath = !rootIsCurrent
            ? null
            : completedDisposition is null
                ? sourcePath
                : WatchedFolderLibraryLayout.GetDestinationPath(
                    watchedRoot,
                    watched,
                    completedDisposition,
                    createDirectories: false);
        return new RoutingActivitySource(
            watched.DisplayFileName,
            watched.GameName,
            sourcePath,
            currentPath,
            new DateTimeOffset(watched.FileIdentity.CreationUtcTicks, TimeSpan.Zero),
            watched.FileIdentity.ByteLength);
    }

    private static RoutingActivityPresentation? ProjectState(
        IReadOnlyList<PlannedDelivery> deliveries,
        IReadOnlyList<PlannedFileDisposition> dispositions)
    {
        if (deliveries.Any(value => value.State == PlannedDeliveryState.Sending))
        {
            return new RoutingActivityPresentation(
                ClipActivityState.Uploading,
                ClipActivityRoute.Uploaded,
                "Routing is sending this clip",
                null);
        }

        var failure = deliveries
            .Where(value => value.State is PlannedDeliveryState.Failed or
                PlannedDeliveryState.DeliveryUnknown or PlannedDeliveryState.NeedsAttention)
            .OrderByDescending(value => value.UpdatedUtc)
            .Select(value => value.ErrorCode ?? value.ArtifactErrorCode ??
                             (value.State == PlannedDeliveryState.DeliveryUnknown
                                 ? "delivery-result-unknown"
                                 : "routing-needs-attention"))
            .FirstOrDefault() ?? dispositions
            .Where(value => value.State is PlannedFileDispositionState.Failed or
                PlannedFileDispositionState.RecoveryPending)
            .OrderByDescending(value => value.UpdatedUtc)
            .Select(value => value.ErrorCode ?? "library-filing-needs-attention")
            .FirstOrDefault();
        if (failure is not null)
        {
            return new RoutingActivityPresentation(
                ClipActivityState.Failed,
                deliveries.Count > 0 ? ClipActivityRoute.Uploaded : ClipActivityRoute.LocalOnly,
                "Routing needs attention",
                failure);
        }

        if (deliveries.Any(value => value.State == PlannedDeliveryState.Ready))
        {
            return new RoutingActivityPresentation(
                ClipActivityState.Queued,
                ClipActivityRoute.Uploaded,
                "Queued for delivery by Routing",
                null);
        }
        if (deliveries.Any(value => value.State is PlannedDeliveryState.WaitingForArtifact or
                PlannedDeliveryState.WaitingForApproval))
        {
            return new RoutingActivityPresentation(
                ClipActivityState.Waiting,
                ClipActivityRoute.Uploaded,
                deliveries.Any(value => value.State == PlannedDeliveryState.WaitingForApproval)
                    ? "Waiting for delivery approval"
                    : "Waiting for the selected clip format",
                null);
        }
        if (deliveries.Any(value => value.State == PlannedDeliveryState.Delivered))
        {
            if (dispositions.Count > 0 &&
                dispositions.Any(value => value.State != PlannedFileDispositionState.Completed))
            {
                return new RoutingActivityPresentation(
                    ClipActivityState.Queued,
                    ClipActivityRoute.Uploaded,
                    "Delivery confirmed; finishing local filing",
                    null);
            }
            var destinations = string.Join(
                " + ",
                deliveries.Where(value => value.State == PlannedDeliveryState.Delivered)
                    .Select(value => value.Destination.ToString())
                    .Distinct()
                    .OrderBy(value => value, StringComparer.Ordinal));
            return new RoutingActivityPresentation(
                ClipActivityState.Completed,
                ClipActivityRoute.Uploaded,
                $"Delivered to {destinations}",
                null);
        }

        var completedLocal = dispositions.FirstOrDefault(value =>
            value.State == PlannedFileDispositionState.Completed &&
            value.LibraryArea == RoutingLibraryArea.LocalOnly);
        if (completedLocal is not null)
        {
            return new RoutingActivityPresentation(
                ClipActivityState.Archived,
                ClipActivityRoute.LocalOnly,
                "Filed into the local ClipCord Library",
                null);
        }
        if (dispositions.Any(value => value.State is PlannedFileDispositionState.Ready or
                PlannedFileDispositionState.WaitingForDependencies or
                PlannedFileDispositionState.Moving))
        {
            return new RoutingActivityPresentation(
                ClipActivityState.Queued,
                ClipActivityRoute.LocalOnly,
                "Filing into the local ClipCord Library",
                null);
        }
        return null;
    }

    private sealed record RoutingActivitySource(
        string FileName,
        string GameName,
        string SourcePath,
        string? CurrentPath,
        DateTimeOffset CapturedUtc,
        long OriginalBytes);

    private sealed record RoutingActivityPresentation(
        ClipActivityState State,
        ClipActivityRoute Route,
        string Detail,
        string? Error);
}
