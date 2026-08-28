namespace ClipsToDiscord;

internal enum RoutingOperatorOutcome
{
    Applied,
    AlreadyApplied,
    NotFound,
    InvalidState,
    Archived
}

internal enum RoutingOperatorTargetKind
{
    Delivery,
    FileDisposition
}

internal sealed record RoutingOperatorResult(
    RoutingOperatorOutcome Outcome,
    RoutingOperatorTargetKind TargetKind,
    Guid TargetId,
    long? OutboxGeneration);

/// <summary>
/// User-authorized outbox state transitions. This layer deliberately has no delivery provider or
/// library filer dependency: it can only persist operator intent, leaving side effects to the
/// fenced outbox executor.
/// </summary>
internal sealed class RoutingOperatorControl
{
    private const int MaximumSaveAttempts = 4;

    private readonly RoutingOutboxStore _outboxStore;
    private readonly RoutingDeliveryHistoryReader _history;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RoutingOperatorControl(
        RoutingOutboxStore outboxStore,
        Func<DateTimeOffset>? utcNow = null)
    {
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _history = new RoutingDeliveryHistoryReader(outboxStore);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal Task<RoutingOperatorResult> RetryFailedDeliveryAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(
            deliveryId,
            delivery => delivery.State == PlannedDeliveryState.Failed,
            delivery => delivery.State == PlannedDeliveryState.Ready &&
                        delivery.Attempts > 0 &&
                        delivery.ApprovedUtc is null &&
                        delivery.DuplicateRiskAcceptedUtc is null &&
                        delivery.CurrentAttemptId is null &&
                        delivery.AttemptStartedUtc is null &&
                        delivery.ErrorCode is null,
            RoutingOutboxModel.RetryFailedDelivery,
            cancellationToken);

    internal Task<RoutingOperatorResult> ApproveDeliveryAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(
            deliveryId,
            delivery => delivery.State == PlannedDeliveryState.WaitingForApproval,
            delivery => delivery.ApprovedUtc is not null,
            RoutingOutboxModel.ApproveDelivery,
            cancellationToken);

    internal Task<RoutingOperatorResult> ResolveNeedsAttentionAsync(
        Guid deliveryId,
        RoutingNeedsAttentionResolution resolution,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(resolution))
            throw new ArgumentOutOfRangeException(nameof(resolution));
        return UpdateDeliveryAsync(
            deliveryId,
            delivery => delivery.State == PlannedDeliveryState.NeedsAttention,
            delivery => NeedsAttentionResolutionAlreadyApplied(delivery, resolution),
            (current, id, now) =>
                RoutingOutboxModel.ResolveNeedsAttention(current, id, resolution, now),
            cancellationToken);
    }

    internal Task<RoutingOperatorResult> ResolveUnknownAsDeliveredAsync(
        Guid deliveryId,
        string remoteReceiptReference,
        CancellationToken cancellationToken = default)
    {
        RoutingValidation.RequireOpaqueId(
            remoteReceiptReference, 256, "remote receipt reference");
        return UpdateDeliveryAsync(
            deliveryId,
            delivery => delivery.State == PlannedDeliveryState.DeliveryUnknown,
            delivery => delivery.State == PlannedDeliveryState.Delivered &&
                        delivery.RemoteReceiptReference == remoteReceiptReference,
            (current, id, now) => RoutingOutboxModel.ResolveUnknownAsDelivered(
                current, id, remoteReceiptReference, now),
            cancellationToken);
    }

    internal Task<RoutingOperatorResult> AuthorizeUnknownSendAgainAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default) =>
        UpdateDeliveryAsync(
            deliveryId,
            delivery => delivery.State == PlannedDeliveryState.DeliveryUnknown,
            delivery => delivery.DuplicateRiskAcceptedUtc is not null,
            RoutingOutboxModel.AuthorizeUnknownSendAgain,
            cancellationToken);

    internal Task<RoutingOperatorResult> RetryFailedFileDispositionAsync(
        Guid dispositionId,
        CancellationToken cancellationToken = default) =>
        UpdateDispositionAsync(
            dispositionId,
            disposition => disposition.State is PlannedFileDispositionState.Failed or
                PlannedFileDispositionState.RecoveryPending,
            disposition => disposition.State == PlannedFileDispositionState.Ready &&
                           disposition.Attempts > 0 &&
                           disposition.CurrentAttemptId is null &&
                           disposition.AttemptStartedUtc is null &&
                           disposition.ErrorCode is null,
            RoutingOutboxModel.RetryFileDisposition,
            cancellationToken);

    private async Task<RoutingOperatorResult> UpdateDeliveryAsync(
        Guid deliveryId,
        Func<PlannedDelivery, bool> canApply,
        Func<PlannedDelivery, bool> alreadyApplied,
        Func<RoutingOutboxDocument, Guid, DateTimeOffset, RoutingOutboxDocument> update,
        CancellationToken cancellationToken)
    {
        if (deliveryId == Guid.Empty) throw new ArgumentException(
            "A delivery id is required.", nameof(deliveryId));
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = _outboxStore.Load(cancellationToken);
            if (loaded.Status == RoutingDocumentLoadStatus.Missing)
                return await MissingDeliveryResultAsync(deliveryId, cancellationToken)
                    .ConfigureAwait(false);
            if (!loaded.LoadedFromDisk || loaded.Document is null)
                throw new InvalidDataException(
                    $"The routing outbox cannot be loaded safely ({loaded.Status}).");

            var current = loaded.Document;
            var delivery = current.Deliveries.SingleOrDefault(
                item => item.DeliveryId == deliveryId);
            if (delivery is null)
                return await MissingDeliveryResultAsync(deliveryId, cancellationToken)
                    .ConfigureAwait(false);
            if (!canApply(delivery))
            {
                if (await IsArchivedDeliveryAsync(deliveryId, cancellationToken)
                        .ConfigureAwait(false))
                    return Result(RoutingOperatorOutcome.Archived,
                        RoutingOperatorTargetKind.Delivery, deliveryId, current.Generation);
                return Result(
                    alreadyApplied(delivery)
                        ? RoutingOperatorOutcome.AlreadyApplied
                        : RoutingOperatorOutcome.InvalidState,
                    RoutingOperatorTargetKind.Delivery, deliveryId, current.Generation);
            }

            var candidate = update(current, deliveryId, OperationTime(current));
            try
            {
                var saved = await _outboxStore.SaveAsync(
                        candidate, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return Result(RoutingOperatorOutcome.Applied,
                    RoutingOperatorTargetKind.Delivery, deliveryId, saved.Generation);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Re-evaluate intent against the winning generation. This makes double-clicks and
                // another process applying the same action converge without replaying side effects.
            }
        }
        throw new RoutingConcurrencyException(
            "The routing outbox kept changing while an operator action was saved.");
    }

    private async Task<RoutingOperatorResult> UpdateDispositionAsync(
        Guid dispositionId,
        Func<PlannedFileDisposition, bool> canApply,
        Func<PlannedFileDisposition, bool> alreadyApplied,
        Func<RoutingOutboxDocument, Guid, DateTimeOffset, RoutingOutboxDocument> update,
        CancellationToken cancellationToken)
    {
        if (dispositionId == Guid.Empty) throw new ArgumentException(
            "A file disposition id is required.", nameof(dispositionId));
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = _outboxStore.Load(cancellationToken);
            if (loaded.Status == RoutingDocumentLoadStatus.Missing)
                return await MissingDispositionResultAsync(dispositionId, cancellationToken)
                    .ConfigureAwait(false);
            if (!loaded.LoadedFromDisk || loaded.Document is null)
                throw new InvalidDataException(
                    $"The routing outbox cannot be loaded safely ({loaded.Status}).");

            var current = loaded.Document;
            var disposition = current.FileDispositions.SingleOrDefault(
                item => item.DispositionId == dispositionId);
            if (disposition is null)
                return await MissingDispositionResultAsync(dispositionId, cancellationToken)
                    .ConfigureAwait(false);
            if (!canApply(disposition))
            {
                if (await IsArchivedDispositionAsync(dispositionId, cancellationToken)
                        .ConfigureAwait(false))
                    return Result(RoutingOperatorOutcome.Archived,
                        RoutingOperatorTargetKind.FileDisposition,
                        dispositionId, current.Generation);
                return Result(
                    alreadyApplied(disposition)
                        ? RoutingOperatorOutcome.AlreadyApplied
                        : RoutingOperatorOutcome.InvalidState,
                    RoutingOperatorTargetKind.FileDisposition,
                    dispositionId, current.Generation);
            }

            var candidate = update(current, dispositionId, OperationTime(current));
            try
            {
                var saved = await _outboxStore.SaveAsync(
                        candidate, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return Result(RoutingOperatorOutcome.Applied,
                    RoutingOperatorTargetKind.FileDisposition,
                    dispositionId, saved.Generation);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reload and re-evaluate the winning generation.
            }
        }
        throw new RoutingConcurrencyException(
            "The routing outbox kept changing while an operator action was saved.");
    }

    private async Task<RoutingOperatorResult> MissingDeliveryResultAsync(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Result(
            await IsArchivedDeliveryAsync(deliveryId, cancellationToken).ConfigureAwait(false)
                ? RoutingOperatorOutcome.Archived
                : RoutingOperatorOutcome.NotFound,
            RoutingOperatorTargetKind.Delivery, deliveryId, null);

    private async Task<RoutingOperatorResult> MissingDispositionResultAsync(
        Guid dispositionId,
        CancellationToken cancellationToken) =>
        Result(
            await IsArchivedDispositionAsync(dispositionId, cancellationToken).ConfigureAwait(false)
                ? RoutingOperatorOutcome.Archived
                : RoutingOperatorOutcome.NotFound,
            RoutingOperatorTargetKind.FileDisposition, dispositionId, null);

    private Task<bool> IsArchivedDeliveryAsync(
        Guid deliveryId,
        CancellationToken cancellationToken) => Task.FromResult(
        _history.Read(cancellationToken).Any(item => item.IsArchived &&
            item.Deliveries.Any(delivery => delivery.DeliveryId == deliveryId)));

    private Task<bool> IsArchivedDispositionAsync(
        Guid dispositionId,
        CancellationToken cancellationToken) => Task.FromResult(
        _history.Read(cancellationToken).Any(item => item.IsArchived &&
            item.FileDispositions.Any(disposition =>
                disposition.DispositionId == dispositionId)));

    private DateTimeOffset OperationTime(RoutingOutboxDocument current)
    {
        var requested = RoutingValidation.Utc(_utcNow());
        return requested < current.UpdatedUtc ? current.UpdatedUtc : requested;
    }

    private static bool NeedsAttentionResolutionAlreadyApplied(
        PlannedDelivery delivery,
        RoutingNeedsAttentionResolution resolution) => resolution switch
        {
            RoutingNeedsAttentionResolution.UseOriginal =>
                delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
                delivery.Output == delivery.OriginalOutput,
            RoutingNeedsAttentionResolution.Skip =>
                delivery.State == PlannedDeliveryState.Skipped &&
                delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.Skipped,
            RoutingNeedsAttentionResolution.RetryOrRebuild =>
                delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.RequestedOutput &&
                delivery.Output == delivery.RequestedOutput &&
                delivery.ArtifactErrorCode is null,
            _ => false
        };

    private static RoutingOperatorResult Result(
        RoutingOperatorOutcome outcome,
        RoutingOperatorTargetKind kind,
        Guid targetId,
        long? generation) => new(outcome, kind, targetId, generation);
}

internal sealed record RoutingDeliveryHistoryItem(
    RoutingPlanDecision Plan,
    IReadOnlyList<PlannedDelivery> Deliveries,
    IReadOnlyList<PlannedFileDisposition> FileDispositions,
    bool IsArchived,
    DateTimeOffset? ArchivedUtc);

/// <summary>
/// Provides one fail-closed history stream across the mutable hot outbox and immutable archive.
/// A terminal plan may briefly exist in both stores while compaction is between its archive and
/// hot-removal commits; exact copies collapse to one item, while any disagreement is corruption.
/// </summary>
internal sealed class RoutingDeliveryHistoryReader
{
    private const int MaximumArchiveDocuments = 100_000;

    private readonly RoutingOutboxStore _outboxStore;

    internal RoutingDeliveryHistoryReader(RoutingOutboxStore outboxStore)
    {
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
    }

    internal IReadOnlyList<RoutingDeliveryHistoryItem> Read(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var byPlanId = new Dictionary<Guid, RoutingDeliveryHistoryItem>();
        var hot = _outboxStore.Load(cancellationToken);
        if (hot.Status != RoutingDocumentLoadStatus.Missing)
        {
            if (!hot.LoadedFromDisk || hot.Document is null)
                throw new InvalidDataException(
                    $"The routing outbox cannot be loaded safely ({hot.Status}).");
            foreach (var plan in hot.Document.Plans)
            {
                byPlanId.Add(plan.PlanId, new RoutingDeliveryHistoryItem(
                    plan,
                    hot.Document.Deliveries.Where(item => item.PlanId == plan.PlanId).ToArray(),
                    hot.Document.FileDispositions.Where(item => item.PlanId == plan.PlanId).ToArray(),
                    IsArchived: false,
                    ArchivedUtc: null));
            }
        }

        foreach (var archive in ReadArchives(cancellationToken))
        {
            var archived = new RoutingDeliveryHistoryItem(
                archive.Plan,
                archive.Deliveries,
                archive.FileDispositions,
                IsArchived: true,
                archive.ArchivedUtc);
            if (!byPlanId.TryGetValue(archive.Plan.PlanId, out var existing))
            {
                byPlanId.Add(archive.Plan.PlanId, archived);
                continue;
            }
            if (!SamePlanEvidence(existing, archived))
                throw new InvalidDataException(
                    "Hot and archived routing history disagree for the same plan id.");
            byPlanId[archive.Plan.PlanId] = archived;
        }

        return byPlanId.Values
            .OrderByDescending(item => item.Plan.CreatedUtc)
            .ThenBy(item => item.Plan.PlanId)
            .ToArray();
    }

    private IReadOnlyList<RoutingArchivedPlanDocument> ReadArchives(
        CancellationToken cancellationToken)
    {
        var root = _outboxStore.ArchiveStore.Root;
        if (!Directory.Exists(root)) return [];
        RequireOrdinaryDirectory(root);
        var result = new List<RoutingArchivedPlanDocument>();
        foreach (var bucket in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOrdinaryDirectory(bucket);
            var bucketName = Path.GetFileName(bucket);
            if (bucketName.Length != 2 || !bucketName.All(IsLowerHex))
                throw new InvalidDataException("The routing archive contains an invalid bucket.");
            if (Directory.EnumerateDirectories(bucket, "*", SearchOption.TopDirectoryOnly).Any())
                throw new InvalidDataException("The routing archive contains unexpected nesting.");
            foreach (var path in Directory.EnumerateFiles(bucket, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Count >= MaximumArchiveDocuments)
                    throw new InvalidDataException("The routing history archive is too large.");
                var archive = LoadArchive(path, cancellationToken);
                var expectedPath = Path.GetFullPath(
                    _outboxStore.ArchiveStore.PathForSource(archive.Plan.SourceClipId));
                if (!Path.GetFullPath(path).Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "A routing archive document is stored under the wrong source key.");
                result.Add(archive);
            }
        }
        return result;
    }

    private static RoutingArchivedPlanDocument LoadArchive(
        string path,
        CancellationToken cancellationToken)
    {
        var store = new RoutingAtomicJsonStore<RoutingArchivedPlanDocument>(
            path,
            RoutingPlanArchiveStore.MaximumDocumentBytes,
            RoutingPlanArchiveStore.CurrentSchemaVersion,
            archive => archive.SchemaVersion,
            archive => archive.Generation,
            RoutingArchiveModel.Validate,
            (_, _) => throw new InvalidDataException("Routing plan archives are immutable."),
            archive =>
            {
                RoutingArchiveModel.Validate(archive);
                RoutingValidation.Require(archive.Generation == 1,
                    "The first routing plan archive generation is not canonical.");
            });
        var loaded = store.Load(cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is null)
            throw new InvalidDataException(
                $"A routing archive document cannot be loaded safely ({loaded.Status}).");
        return loaded.Document;
    }

    private static bool SamePlanEvidence(
        RoutingDeliveryHistoryItem first,
        RoutingDeliveryHistoryItem second) =>
        first.Plan == second.Plan &&
        first.Deliveries.SequenceEqual(second.Deliveries) &&
        first.FileDispositions.SequenceEqual(second.FileDispositions);

    private static void RequireOrdinaryDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The routing archive path is not an ordinary directory.");
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
