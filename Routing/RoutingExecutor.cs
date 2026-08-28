using System.Security.Cryptography;

namespace ClipsToDiscord;

internal enum RoutingDeliveryAttemptOutcome
{
    Confirmed,
    Failed,
    Unknown
}

internal sealed record RoutingDeliveryAttemptResult(
    RoutingDeliveryAttemptOutcome Outcome,
    string? RemoteReceiptReference,
    string? ErrorCode,
    string? ProviderResumeReference)
{
    internal static RoutingDeliveryAttemptResult Confirmed(string receipt) =>
        new(RoutingDeliveryAttemptOutcome.Confirmed, receipt, null, null);

    internal static RoutingDeliveryAttemptResult Failed(
        string errorCode,
        string? providerResumeReference = null) =>
        new(RoutingDeliveryAttemptOutcome.Failed, null, errorCode, providerResumeReference);

    internal static RoutingDeliveryAttemptResult Unknown(string errorCode) =>
        new(RoutingDeliveryAttemptOutcome.Unknown, null, errorCode, null);
}

internal enum RoutingFileAttemptOutcome
{
    Confirmed,
    Failed,
    Unknown
}

internal sealed record RoutingFileAttemptResult(
    RoutingFileAttemptOutcome Outcome,
    string? FinalLibraryItemReference,
    string? ErrorCode)
{
    internal static RoutingFileAttemptResult Confirmed(string libraryItemReference) =>
        new(RoutingFileAttemptOutcome.Confirmed, libraryItemReference, null);

    internal static RoutingFileAttemptResult Failed(string errorCode) =>
        new(RoutingFileAttemptOutcome.Failed, null, errorCode);

    internal static RoutingFileAttemptResult Unknown(string errorCode) =>
        new(RoutingFileAttemptOutcome.Unknown, null, errorCode);
}

internal enum RoutingFileRecoveryOutcome
{
    Completed,
    RetrySafe,
    Unresolved
}

internal sealed record RoutingFileRecoveryResult(
    RoutingFileRecoveryOutcome Outcome,
    string? FinalLibraryItemReference)
{
    internal static RoutingFileRecoveryResult Completed(string libraryItemReference) =>
        new(RoutingFileRecoveryOutcome.Completed, libraryItemReference);

    internal static RoutingFileRecoveryResult RetrySafe { get; } =
        new(RoutingFileRecoveryOutcome.RetrySafe, null);

    internal static RoutingFileRecoveryResult Unresolved { get; } =
        new(RoutingFileRecoveryOutcome.Unresolved, null);
}

/// <summary>
/// A validated artifact and an open read lease. The lease prevents a capture file from being
/// modified, renamed, or deleted between fingerprint validation and provider completion.
/// </summary>
internal sealed class RoutingResolvedArtifact : IAsyncDisposable
{
    private Stream? _readLease;

    internal RoutingResolvedArtifact(
        string path,
        string displayFileName,
        string gameName,
        long byteLength,
        string sha256,
        Stream? readLease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);
        RoutingValidation.Require(byteLength >= 0, "A resolved artifact length is invalid.");
        RoutingValidation.RequireSha256(sha256, "resolved artifact hash");
        Path = System.IO.Path.GetFullPath(path);
        DisplayFileName = displayFileName;
        GameName = gameName;
        ByteLength = byteLength;
        Sha256 = sha256.ToLowerInvariant();
        _readLease = readLease;
    }

    internal string Path { get; }
    internal string DisplayFileName { get; }
    internal string GameName { get; }
    internal long ByteLength { get; }
    internal string Sha256 { get; }

    public async ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref _readLease, null);
        if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
    }
}

internal interface IRoutingDeliveryProvider
{
    bool Supports(RoutingDestinationKind destination);

    Task<RoutingDeliveryAttemptResult> SendAsync(
        PlannedDelivery delivery,
        RoutingResolvedArtifact artifact,
        CancellationToken cancellationToken);
}

internal interface IRoutingArtifactResolver
{
    Task<RoutingResolvedArtifact> ResolveAsync(
        PlannedDelivery delivery,
        CancellationToken cancellationToken);
}

internal interface IRoutingLibraryFiler
{
    Task<RoutingFileAttemptResult> FileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken);

    Task<RoutingFileRecoveryResult> ReconcileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken);
}

internal sealed record RoutingExecutorRunResult(
    bool Enabled,
    int ProviderAttempts,
    int FileAttempts,
    int RecoveryInspections,
    int PersistedTransitions)
{
    internal bool MadeProgress => PersistedTransitions > 0;
}

/// <summary>
/// A bounded executor for already-frozen routing plans. The caller must own the routing pipeline
/// lease for the executor's entire lifetime. Every network send and library operation is fenced by
/// a durable outbox attempt written before the injected side effect begins.
/// </summary>
internal sealed class RoutingOutboxExecutor : IDisposable
{
    private const int MaximumSaveAttempts = 4;
    private readonly RoutingOutboxStore _outboxStore;
    private readonly IRoutingDeliveryProvider _provider;
    private readonly IRoutingArtifactResolver _artifactResolver;
    private readonly IRoutingLibraryFiler _libraryFiler;
    private readonly Func<bool> _canExecute;
    private readonly Func<Guid> _createAttemptId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly int _maximumSideEffectsPerRun;
    private readonly int _maximumRecoveryInspectionsPerRun;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private (DateTimeOffset CreatedUtc, Guid DispositionId)? _lastRecoveryInspection;
    private bool _startupRecoveryApplied;
    private bool _disposed;

    internal RoutingOutboxExecutor(
        RoutingOutboxStore outboxStore,
        IRoutingDeliveryProvider provider,
        IRoutingArtifactResolver artifactResolver,
        IRoutingLibraryFiler libraryFiler,
        Func<bool> canExecute,
        int maximumSideEffectsPerRun = 32,
        Func<Guid>? createAttemptId = null,
        Func<DateTimeOffset>? utcNow = null,
        int? maximumRecoveryInspectionsPerRun = null)
    {
        if (maximumSideEffectsPerRun is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumSideEffectsPerRun));
        var recoveryBudget = maximumRecoveryInspectionsPerRun ??
                             Math.Min(8, maximumSideEffectsPerRun);
        if (recoveryBudget is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumRecoveryInspectionsPerRun));
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _libraryFiler = libraryFiler ?? throw new ArgumentNullException(nameof(libraryFiler));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _maximumSideEffectsPerRun = maximumSideEffectsPerRun;
        _maximumRecoveryInspectionsPerRun = recoveryBudget;
        _createAttemptId = createAttemptId ?? Guid.NewGuid;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<RoutingExecutorRunResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_canExecute()) return new RoutingExecutorRunResult(false, 0, 0, 0, 0);

            var transitions = 0;
            if (!_startupRecoveryApplied)
            {
                var before = await _outboxStore.LoadOrCreateAsync(
                        UtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                var recovered = await _outboxStore.LoadAndRecoverAsync(
                        UtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                if (recovered.Generation != before.Generation) transitions++;
                _startupRecoveryApplied = true;
            }

            var providerAttempts = 0;
            var fileAttempts = 0;
            var recoveryInspections = 0;
            var inspectedRecovery = new HashSet<Guid>();
            var transitionBudget = checked(
                _maximumSideEffectsPerRun * 4 + _maximumRecoveryInspectionsPerRun + 64);

            // Recovery is a bounded read-only inspection lane, not part of the external-side-effect
            // budget. Runnable work leads and recovery catches up one-for-one, so large hash checks
            // cannot enter an upload's critical path and a steady upload queue cannot starve them.
            while (_canExecute() &&
                   (providerAttempts + fileAttempts < _maximumSideEffectsPerRun ||
                    recoveryInspections < _maximumRecoveryInspectionsPerRun &&
                    recoveryInspections < providerAttempts + fileAttempts) &&
                   transitionBudget-- > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await _outboxStore.LoadOrCreateAsync(
                        UtcNow(), cancellationToken)
                    .ConfigureAwait(false);

                var sideEffectAttempts = providerAttempts + fileAttempts;
                var hasRunnableWork = HasRunnableWork(current);
                var recovery = recoveryInspections < _maximumRecoveryInspectionsPerRun &&
                               (!hasRunnableWork || recoveryInspections < sideEffectAttempts)
                    ? SelectRecoveryCandidate(current, inspectedRecovery)
                    : null;
                if (recovery is not null)
                {
                    inspectedRecovery.Add(recovery.DispositionId);
                    _lastRecoveryInspection = (recovery.CreatedUtc, recovery.DispositionId);
                    recoveryInspections++;
                    var recoveryAttemptId = recovery.CurrentAttemptId ??
                                            throw new InvalidDataException(
                                                "A recovery-pending file disposition has no attempt fence.");
                    RoutingFileRecoveryResult result;
                    try
                    {
                        result = await _libraryFiler.ReconcileAsync(
                                recovery,
                                CancellationToken.None)
                            .ConfigureAwait(false) ?? RoutingFileRecoveryResult.Unresolved;
                    }
                    catch
                    {
                        result = RoutingFileRecoveryResult.Unresolved;
                    }

                    if (result.Outcome == RoutingFileRecoveryOutcome.Completed &&
                        IsOpaqueId(result.FinalLibraryItemReference, 256))
                    {
                        if (await TryCompleteRecoveredDispositionAsync(
                                recovery.DispositionId,
                                recoveryAttemptId,
                                result.FinalLibraryItemReference!,
                                CancellationToken.None)
                            .ConfigureAwait(false)) transitions++;
                    }
                    else if (result.Outcome == RoutingFileRecoveryOutcome.RetrySafe)
                    {
                        if (await TryRetryDispositionAsync(
                                recovery.DispositionId,
                                recoveryAttemptId,
                                CancellationToken.None)
                            .ConfigureAwait(false)) transitions++;
                    }
                    continue;
                }

                // A final recovery inspection may catch up after the side-effect lane reaches its
                // cap, but that catch-up iteration must never admit one extra provider/file call.
                if (sideEffectAttempts >= _maximumSideEffectsPerRun) break;

                var readyDelivery = current.Deliveries
                    .Where(item => item.State == PlannedDeliveryState.Ready &&
                                   _provider.Supports(item.Destination))
                    .OrderBy(item => item.CreatedUtc)
                    .ThenBy(item => item.Route.Priority)
                    .ThenBy(item => item.Route.Order)
                    .ThenBy(item => item.DeliveryId)
                    .FirstOrDefault();
                if (readyDelivery is not null)
                {
                    RoutingResolvedArtifact artifact;
                    try
                    {
                        artifact = await _artifactResolver.ResolveAsync(
                                readyDelivery,
                                cancellationToken)
                            .ConfigureAwait(false) ?? throw new InvalidDataException(
                            "The routing artifact resolver returned no artifact.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        if (await TryFailDeliveryBeforeProviderAsync(
                                readyDelivery.DeliveryId,
                                "artifact-resolution-failed",
                                CancellationToken.None)
                            .ConfigureAwait(false)) transitions++;
                        continue;
                    }
                    await using var artifactLease = artifact;
                    var attemptId = CreateAttemptId();
                    var started = await TryStartDeliveryAsync(
                            readyDelivery.DeliveryId,
                            attemptId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (started is null) continue;
                    transitions++;

                    RoutingDeliveryAttemptResult outcome;
                    if (!_canExecute())
                    {
                        outcome = RoutingDeliveryAttemptResult.Failed(
                            "routing-disabled-before-provider");
                    }
                    else
                    {
                        providerAttempts++;
                        try
                        {
                            outcome = await _provider.SendAsync(
                                    started,
                                    artifact,
                                    CancellationToken.None)
                                .ConfigureAwait(false) ??
                                RoutingDeliveryAttemptResult.Unknown("provider-result-missing");
                        }
                        catch
                        {
                            outcome = RoutingDeliveryAttemptResult.Unknown(
                                "provider-result-unknown");
                        }
                    }

                    outcome = Normalize(outcome);
                    if (await PersistDeliveryOutcomeAsync(
                            started.DeliveryId,
                            attemptId,
                            outcome,
                            CancellationToken.None)
                        .ConfigureAwait(false)) transitions++;
                    continue;
                }

                var refreshable = current.FileDispositions
                    .Where(item => item.State == PlannedFileDispositionState.WaitingForDependencies &&
                                   DependenciesSettled(current, item))
                    .OrderBy(item => item.CreatedUtc)
                    .ThenBy(item => item.DispositionId)
                    .FirstOrDefault();
                if (refreshable is not null)
                {
                    if (await TryRefreshDispositionAsync(
                            refreshable.DispositionId,
                            cancellationToken)
                        .ConfigureAwait(false)) transitions++;
                    continue;
                }

                var readyDisposition = current.FileDispositions
                    .Where(item => item.State == PlannedFileDispositionState.Ready)
                    .OrderBy(item => item.CreatedUtc)
                    .ThenBy(item => item.Route.Priority)
                    .ThenBy(item => item.Route.Order)
                    .ThenBy(item => item.DispositionId)
                    .FirstOrDefault();
                if (readyDisposition is not null)
                {
                    fileAttempts++;
                    var attemptId = CreateAttemptId();
                    var started = await TryStartDispositionAsync(
                            readyDisposition.DispositionId,
                            attemptId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (started is null) continue;
                    transitions++;

                    RoutingFileAttemptResult outcome;
                    if (!_canExecute())
                    {
                        outcome = RoutingFileAttemptResult.Failed(
                            "routing-disabled-before-file");
                    }
                    else
                    {
                        try
                        {
                            outcome = await _libraryFiler.FileAsync(
                                    started,
                                    CancellationToken.None)
                                .ConfigureAwait(false) ??
                                RoutingFileAttemptResult.Unknown("library-result-missing");
                        }
                        catch
                        {
                            outcome = RoutingFileAttemptResult.Unknown(
                                "library-result-unknown");
                        }
                    }

                    outcome = Normalize(outcome);
                    if (await PersistFileOutcomeAsync(
                            started.DispositionId,
                            attemptId,
                            outcome,
                            CancellationToken.None)
                        .ConfigureAwait(false)) transitions++;
                    continue;
                }

                break;
            }

            return new RoutingExecutorRunResult(
                true,
                providerAttempts,
                fileAttempts,
                recoveryInspections,
                transitions);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private bool HasRunnableWork(RoutingOutboxDocument current) =>
        current.Deliveries.Any(item =>
            item.State == PlannedDeliveryState.Ready && _provider.Supports(item.Destination)) ||
        current.FileDispositions.Any(item =>
            item.State == PlannedFileDispositionState.Ready ||
            item.State == PlannedFileDispositionState.WaitingForDependencies &&
            DependenciesSettled(current, item));

    private PlannedFileDisposition? SelectRecoveryCandidate(
        RoutingOutboxDocument current,
        IReadOnlySet<Guid> inspectedRecovery)
    {
        var candidates = current.FileDispositions
            .Where(item => item.State == PlannedFileDispositionState.RecoveryPending &&
                           !inspectedRecovery.Contains(item.DispositionId))
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.DispositionId)
            .ToArray();
        if (candidates.Length == 0) return null;

        if (_lastRecoveryInspection is { } cursor)
        {
            var next = candidates.FirstOrDefault(item =>
                item.CreatedUtc > cursor.CreatedUtc ||
                item.CreatedUtc == cursor.CreatedUtc &&
                item.DispositionId.CompareTo(cursor.DispositionId) > 0);
            if (next is not null) return next;
        }

        return candidates[0];
    }

    private async Task<PlannedDelivery?> TryStartDeliveryAsync(
        Guid deliveryId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            var current = await _outboxStore.LoadOrCreateAsync(UtcNow(), cancellationToken)
                .ConfigureAwait(false);
            var delivery = current.Deliveries.SingleOrDefault(item => item.DeliveryId == deliveryId);
            if (delivery?.State != PlannedDeliveryState.Ready) return null;
            var candidate = RoutingOutboxModel.StartDelivery(
                current, deliveryId, attemptId, TransitionTime(current));
            try
            {
                var saved = await _outboxStore.SaveAsync(
                        candidate, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return saved.Deliveries.Single(item => item.DeliveryId == deliveryId);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reload and retry the same attempt fence only if the item is still ready.
            }
        }
        throw new RoutingConcurrencyException("The delivery kept changing before it could start.");
    }

    private async Task<PlannedFileDisposition?> TryStartDispositionAsync(
        Guid dispositionId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            var current = await _outboxStore.LoadOrCreateAsync(UtcNow(), cancellationToken)
                .ConfigureAwait(false);
            var disposition = current.FileDispositions.SingleOrDefault(
                item => item.DispositionId == dispositionId);
            if (disposition?.State != PlannedFileDispositionState.Ready) return null;
            var candidate = RoutingOutboxModel.StartFileDisposition(
                current, dispositionId, attemptId, TransitionTime(current));
            try
            {
                var saved = await _outboxStore.SaveAsync(
                        candidate, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return saved.FileDispositions.Single(item => item.DispositionId == dispositionId);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reload and retry the same attempt fence only if the item is still ready.
            }
        }
        throw new RoutingConcurrencyException("The file disposition kept changing before it could start.");
    }

    private Task<bool> PersistDeliveryOutcomeAsync(
        Guid deliveryId,
        Guid attemptId,
        RoutingDeliveryAttemptResult outcome,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var delivery = current.Deliveries.SingleOrDefault(item => item.DeliveryId == deliveryId) ??
                           throw new InvalidDataException("The started delivery disappeared.");
            if (delivery.State != PlannedDeliveryState.Sending ||
                delivery.CurrentAttemptId != attemptId) return current;
            var now = TransitionTime(current);
            return outcome.Outcome switch
            {
                RoutingDeliveryAttemptOutcome.Confirmed => RoutingOutboxModel.CompleteDelivery(
                    current, deliveryId, attemptId, outcome.RemoteReceiptReference!, now),
                RoutingDeliveryAttemptOutcome.Failed => RoutingOutboxModel.FailDelivery(
                    current, deliveryId, attemptId, outcome.ErrorCode!,
                    outcome.ProviderResumeReference, now),
                RoutingDeliveryAttemptOutcome.Unknown => RoutingOutboxModel.MarkDeliveryUnknown(
                    current, deliveryId, attemptId, outcome.ErrorCode!, now),
                _ => throw new InvalidDataException("The provider outcome is unsupported.")
            };
        }, cancellationToken);

    private Task<bool> TryFailDeliveryBeforeProviderAsync(
        Guid deliveryId,
        string errorCode,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var delivery = current.Deliveries.SingleOrDefault(item => item.DeliveryId == deliveryId);
            return delivery?.State == PlannedDeliveryState.Ready
                ? RoutingOutboxModel.FailDeliveryBeforeProvider(
                    current, deliveryId, errorCode, TransitionTime(current))
                : current;
        }, cancellationToken);

    private Task<bool> PersistFileOutcomeAsync(
        Guid dispositionId,
        Guid attemptId,
        RoutingFileAttemptResult outcome,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var disposition = current.FileDispositions.SingleOrDefault(
                                  item => item.DispositionId == dispositionId) ??
                              throw new InvalidDataException("The started file disposition disappeared.");
            if (disposition.State != PlannedFileDispositionState.Moving ||
                disposition.CurrentAttemptId != attemptId) return current;
            var now = TransitionTime(current);
            return outcome.Outcome switch
            {
                RoutingFileAttemptOutcome.Confirmed => RoutingOutboxModel.CompleteFileDisposition(
                    current, dispositionId, attemptId, outcome.FinalLibraryItemReference!, now),
                RoutingFileAttemptOutcome.Failed => RoutingOutboxModel.FailFileDisposition(
                    current, dispositionId, attemptId, outcome.ErrorCode!, now),
                RoutingFileAttemptOutcome.Unknown =>
                    RoutingOutboxModel.MarkFileDispositionRecoveryPending(
                        current, dispositionId, attemptId, outcome.ErrorCode!, now),
                _ => throw new InvalidDataException("The library outcome is unsupported.")
            };
        }, cancellationToken);

    private Task<bool> TryRefreshDispositionAsync(
        Guid dispositionId,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var disposition = current.FileDispositions.SingleOrDefault(
                item => item.DispositionId == dispositionId);
            return disposition?.State == PlannedFileDispositionState.WaitingForDependencies &&
                   DependenciesSettled(current, disposition)
                ? RoutingOutboxModel.RefreshFileDisposition(
                    current, dispositionId, TransitionTime(current))
                : current;
        }, cancellationToken);

    private Task<bool> TryCompleteRecoveredDispositionAsync(
        Guid dispositionId,
        Guid expectedAttemptId,
        string finalLibraryItemReference,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var disposition = current.FileDispositions.SingleOrDefault(
                item => item.DispositionId == dispositionId);
            return disposition?.State == PlannedFileDispositionState.RecoveryPending &&
                   disposition.CurrentAttemptId == expectedAttemptId
                ? RoutingOutboxModel.CompleteRecoveredFileDisposition(
                    current, dispositionId, finalLibraryItemReference, TransitionTime(current))
                : current;
        }, cancellationToken);

    private Task<bool> TryRetryDispositionAsync(
        Guid dispositionId,
        Guid expectedAttemptId,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var disposition = current.FileDispositions.SingleOrDefault(
                item => item.DispositionId == dispositionId);
            return disposition?.State == PlannedFileDispositionState.RecoveryPending &&
                   disposition.CurrentAttemptId == expectedAttemptId
                ? RoutingOutboxModel.RetryFileDisposition(
                    current, dispositionId, TransitionTime(current))
                : current;
        }, cancellationToken);

    private async Task<bool> MutateAsync(
        Func<RoutingOutboxDocument, RoutingOutboxDocument> mutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            var current = await _outboxStore.LoadOrCreateAsync(UtcNow(), cancellationToken)
                .ConfigureAwait(false);
            var candidate = mutation(current);
            if (ReferenceEquals(candidate, current) || candidate == current) return false;
            try
            {
                _ = await _outboxStore.SaveAsync(
                        candidate, current.Generation, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                // Reapply the attempt-fenced transition to the winning generation.
            }
        }
        throw new RoutingConcurrencyException("The routing outbox kept changing during execution.");
    }

    private static bool DependenciesSettled(
        RoutingOutboxDocument outbox,
        PlannedFileDisposition disposition)
    {
        var byId = outbox.Deliveries.ToDictionary(item => item.DeliveryId);
        if (!disposition.PrerequisiteDeliveryIds.All(byId.ContainsKey)) return false;
        if (disposition.LibraryArea == RoutingLibraryArea.Uploaded)
        {
            return disposition.PrerequisiteDeliveryIds.Count > 0 &&
                   disposition.PrerequisiteDeliveryIds.All(id =>
                       byId[id].State == PlannedDeliveryState.Delivered ||
                       byId[id].State == PlannedDeliveryState.Skipped &&
                       byId[id].ArtifactOutcome is RoutingMissingArtifactOutcome.Skipped or
                           RoutingMissingArtifactOutcome.DuplicateSuppressed) &&
                   disposition.PrerequisiteDeliveryIds.Any(id =>
                       byId[id].State == PlannedDeliveryState.Delivered);
        }
        return disposition.PrerequisiteDeliveryIds.All(id =>
            byId[id].State is PlannedDeliveryState.Delivered or PlannedDeliveryState.Failed or
                PlannedDeliveryState.Cancelled or PlannedDeliveryState.Expired);
    }

    private RoutingDeliveryAttemptResult Normalize(RoutingDeliveryAttemptResult result)
    {
        try
        {
            switch (result.Outcome)
            {
                case RoutingDeliveryAttemptOutcome.Confirmed:
                    RoutingValidation.RequireOpaqueId(
                        result.RemoteReceiptReference, 256, "remote receipt reference");
                    RoutingValidation.Require(result.ErrorCode is null &&
                                              result.ProviderResumeReference is null,
                        "A confirmed provider result contains failure state.");
                    return result;
                case RoutingDeliveryAttemptOutcome.Failed:
                    RoutingValidation.RequireErrorCode(result.ErrorCode!);
                    RoutingValidation.RequireOptionalOpaqueId(
                        result.ProviderResumeReference, 256, "provider resume reference");
                    RoutingValidation.Require(result.RemoteReceiptReference is null,
                        "A failed provider result contains a receipt.");
                    return result;
                case RoutingDeliveryAttemptOutcome.Unknown:
                    RoutingValidation.RequireErrorCode(result.ErrorCode!);
                    RoutingValidation.Require(result.RemoteReceiptReference is null &&
                                              result.ProviderResumeReference is null,
                        "An unknown provider result contains completion state.");
                    return result;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentNullException)
        {
            // A malformed provider result is itself ambiguous after the provider call started.
        }
        return RoutingDeliveryAttemptResult.Unknown("provider-result-invalid");
    }

    private RoutingFileAttemptResult Normalize(RoutingFileAttemptResult result)
    {
        try
        {
            switch (result.Outcome)
            {
                case RoutingFileAttemptOutcome.Confirmed:
                    RoutingValidation.RequireOpaqueId(
                        result.FinalLibraryItemReference, 256, "final library item reference");
                    RoutingValidation.Require(result.ErrorCode is null,
                        "A confirmed library result contains an error.");
                    return result;
                case RoutingFileAttemptOutcome.Failed:
                case RoutingFileAttemptOutcome.Unknown:
                    RoutingValidation.RequireErrorCode(result.ErrorCode!);
                    RoutingValidation.Require(result.FinalLibraryItemReference is null,
                        "A failed library result contains completion state.");
                    return result;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentNullException)
        {
            // An invalid result cannot prove whether the injected operation completed.
        }
        return RoutingFileAttemptResult.Unknown("library-result-invalid");
    }

    private Guid CreateAttemptId()
    {
        var result = _createAttemptId();
        if (result == Guid.Empty) throw new InvalidDataException("The routing attempt id is missing.");
        return result;
    }

    private DateTimeOffset UtcNow() => RoutingValidation.Utc(_utcNow());

    private DateTimeOffset TransitionTime(RoutingOutboxDocument current)
    {
        var now = UtcNow();
        return now < current.UpdatedUtc ? current.UpdatedUtc : now;
    }

    private static bool IsOpaqueId(string? value, int maximum)
    {
        try
        {
            RoutingValidation.RequireOpaqueId(value, maximum, "routing executor identifier");
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runGate.Dispose();
    }
}

/// <summary>Resolves only immutable, capture-journal-owned artifacts.</summary>
internal sealed class CaptureJournalRoutingArtifactResolver(string libraryRoot) : IRoutingArtifactResolver
{
    private readonly string _libraryRoot = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(libraryRoot)
            ? throw new ArgumentException("The capture library root is missing.", nameof(libraryRoot))
            : libraryRoot);

    public async Task<RoutingResolvedArtifact> ResolveAsync(
        PlannedDelivery delivery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var load = CaptureJournalStore.Load(_libraryRoot, delivery.SourceClipId, cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null)
            throw new InvalidDataException("The delivery capture journal is unavailable.");
        var journal = load.Document;
        var artifact = delivery.Output.Kind switch
        {
            RoutingOutputKind.Original => journal.Clip.Original,
            RoutingOutputKind.Landscape => journal.Artifacts.SingleOrDefault(item =>
                item.Kind.Equals(CaptureJournalArtifactKinds.Landscape, StringComparison.Ordinal)),
            RoutingOutputKind.Portrait => journal.Artifacts.SingleOrDefault(item =>
                item.Kind.Equals(CaptureJournalArtifactKinds.Portrait, StringComparison.Ordinal)),
            _ => null
        } ?? throw new InvalidDataException("The planned delivery artifact is not committed.");
        var expectedOutput = RoutingEvaluator.CreateLogicalOutputReference(
            journal.Clip.ClipId,
            journal.Clip.Original.Fingerprint.Sha256,
            delivery.Output.Kind);
        if (delivery.Output != expectedOutput)
        {
            throw new InvalidDataException("The planned output revision no longer matches its journal.");
        }

        var validation = await CaptureJournalStore.ValidateArtifactAsync(
                _libraryRoot, artifact, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
            throw new InvalidDataException("The planned delivery artifact failed physical validation.");

        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            _libraryRoot,
            artifact.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        FileStream? lease = null;
        try
        {
            lease = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (lease.Length != artifact.Fingerprint.ByteLength)
                throw new InvalidDataException("The leased delivery artifact changed length.");
            var observed = Convert.ToHexString(
                    await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!observed.Equals(artifact.Fingerprint.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("The leased delivery artifact changed content.");
            lease.Position = 0;
            var result = new RoutingResolvedArtifact(
                path,
                System.IO.Path.GetFileName(path),
                journal.Clip.GameName,
                lease.Length,
                observed,
                lease);
            lease = null;
            return result;
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Capture originals are promoted into Library/Game before their journal becomes visible. Filing
/// one is therefore a hash-verified canonical no-op; this implementation never moves an external
/// watched-file source or invents a destination after a crash.
/// </summary>
internal sealed class CaptureJournalRoutingLibraryFiler(string libraryRoot) : IRoutingLibraryFiler
{
    private readonly string _libraryRoot = System.IO.Path.GetFullPath(
        string.IsNullOrWhiteSpace(libraryRoot)
            ? throw new ArgumentException("The capture library root is missing.", nameof(libraryRoot))
            : libraryRoot);

    public Task<RoutingFileAttemptResult> FileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken) =>
        ValidateCanonicalAsync(disposition, cancellationToken);

    public async Task<RoutingFileRecoveryResult> ReconcileAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken)
    {
        var result = await ValidateCanonicalAsync(disposition, cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome == RoutingFileAttemptOutcome.Confirmed
            ? RoutingFileRecoveryResult.Completed(result.FinalLibraryItemReference!)
            : RoutingFileRecoveryResult.Unresolved;
    }

    private async Task<RoutingFileAttemptResult> ValidateCanonicalAsync(
        PlannedFileDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        var load = CaptureJournalStore.Load(_libraryRoot, disposition.SourceClipId, cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null)
            return RoutingFileAttemptResult.Failed("capture-journal-unavailable");
        var original = load.Document.Clip.Original;
        if (!original.Fingerprint.Sha256.Equals(
                disposition.SourceContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return RoutingFileAttemptResult.Failed("library-source-hash-mismatch");
        }
        var validation = await CaptureJournalStore.ValidateArtifactAsync(
                _libraryRoot, original, cancellationToken)
            .ConfigureAwait(false);
        return validation.IsValid
            ? RoutingFileAttemptResult.Confirmed(
                $"capture:{disposition.SourceClipId}:original")
            : RoutingFileAttemptResult.Failed("library-source-unavailable");
    }
}
