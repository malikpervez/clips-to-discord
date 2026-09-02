using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

/// <summary>
/// Immutable final state for one source clip. The deterministic per-source file is both the
/// permanent no-replan tombstone and the complete history record used by future Activity views.
/// </summary>
internal sealed record RoutingArchivedPlanDocument(
    int SchemaVersion,
    long Generation,
    string SourceKeySha256,
    RoutingPlanDecision Plan,
    IReadOnlyList<PlannedDelivery> Deliveries,
    IReadOnlyList<PlannedFileDisposition> FileDispositions,
    DateTimeOffset ArchivedUtc)
{
    public bool Equals(RoutingArchivedPlanDocument? other) =>
        ReferenceEquals(this, other) ||
        other is not null && SchemaVersion == other.SchemaVersion &&
        Generation == other.Generation && SourceKeySha256 == other.SourceKeySha256 &&
        Plan == other.Plan && ArchivedUtc == other.ArchivedUtc &&
        RoutingStructural.SequenceEqual(Deliveries, other.Deliveries) &&
        RoutingStructural.SequenceEqual(FileDispositions, other.FileDispositions);

    public override int GetHashCode() => RoutingStructural.Hash(
        SchemaVersion, Generation, SourceKeySha256, Plan, Deliveries, FileDispositions, ArchivedUtc);
}

internal static class RoutingArchiveModel
{
    internal static RoutingArchivedPlanDocument Create(
        RoutingOutboxDocument outbox,
        Guid planId)
    {
        RoutingOutboxModel.Validate(outbox);
        var plan = outbox.Plans.SingleOrDefault(item => item.PlanId == planId)
            ?? throw new InvalidDataException("The terminal routing plan does not exist.");
        var deliveries = outbox.Deliveries.Where(item => item.PlanId == planId).ToArray();
        var dispositions = outbox.FileDispositions.Where(item => item.PlanId == planId).ToArray();
        RequireTerminal(deliveries, dispositions);
        var archivedUtc = deliveries.Select(item => item.UpdatedUtc)
            .Concat(dispositions.Select(item => item.UpdatedUtc))
            .Append(plan.CreatedUtc)
            .Max();
        var result = new RoutingArchivedPlanDocument(
            RoutingPlanArchiveStore.CurrentSchemaVersion,
            Generation: 1,
            RoutingPlanArchiveStore.SourceKey(plan.SourceClipId),
            plan,
            deliveries,
            dispositions,
            archivedUtc);
        Validate(result);
        return result;
    }

    internal static bool CanArchive(RoutingOutboxDocument outbox, Guid planId)
    {
        var plan = outbox.Plans.SingleOrDefault(item => item.PlanId == planId);
        if (plan is null) return false;
        var deliveries = outbox.Deliveries.Where(item => item.PlanId == planId).ToArray();
        var dispositions = outbox.FileDispositions.Where(item => item.PlanId == planId).ToArray();
        return deliveries.All(IsTerminal) && dispositions.All(IsTerminal);
    }

    internal static void Validate(RoutingArchivedPlanDocument archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        RoutingValidation.Require(
            archive.SchemaVersion == RoutingPlanArchiveStore.CurrentSchemaVersion &&
            archive.Generation == 1,
            "The routing plan archive schema is unsupported.");
        ArgumentNullException.ThrowIfNull(archive.Plan);
        var deliveries = archive.Deliveries ??
            throw new InvalidDataException("Archived routing deliveries are missing.");
        var dispositions = archive.FileDispositions ??
            throw new InvalidDataException("Archived file dispositions are missing.");
        RoutingValidation.Require(
            archive.SourceKeySha256 == RoutingPlanArchiveStore.SourceKey(archive.Plan.SourceClipId),
            "The routing archive source key is invalid.");
        RequireTerminal(deliveries, dispositions);
        var expectedUtc = deliveries.Select(item => item.UpdatedUtc)
            .Concat(dispositions.Select(item => item.UpdatedUtc))
            .Append(archive.Plan.CreatedUtc)
            .Max();
        RoutingValidation.Require(archive.ArchivedUtc == expectedUtc,
            "The routing archive terminal timestamp is not canonical.");

        var synthetic = new RoutingOutboxDocument(
            RoutingOutboxStore.CurrentSchemaVersion,
            Generation: 1,
            [archive.Plan],
            deliveries,
            dispositions,
            CreatedUtc: archive.Plan.CreatedUtc,
            UpdatedUtc: expectedUtc);
        RoutingOutboxModel.Validate(synthetic);
    }

    internal static RoutingOutboxDocument RemoveArchivedPlans(
        RoutingOutboxDocument current,
        IReadOnlyList<RoutingArchivedPlanDocument> archives,
        DateTimeOffset now)
    {
        ValidateArchiveSet(current, archives);
        var removedIds = archives.Select(item => item.Plan.PlanId).ToHashSet();
        var requestedUtc = RoutingValidation.Utc(now);
        var candidate = current with
        {
            Generation = RoutingValidation.NextGeneration(current.Generation),
            Plans = current.Plans.Where(item => !removedIds.Contains(item.PlanId)).ToArray(),
            Deliveries = current.Deliveries.Where(item => !removedIds.Contains(item.PlanId)).ToArray(),
            FileDispositions = current.FileDispositions
                .Where(item => !removedIds.Contains(item.PlanId)).ToArray(),
            UpdatedUtc = current.UpdatedUtc > requestedUtc ? current.UpdatedUtc : requestedUtc
        };
        ValidateCompactionSuccessor(current, candidate, archives);
        return candidate;
    }

    internal static void ValidateCompactionSuccessor(
        RoutingOutboxDocument current,
        RoutingOutboxDocument candidate,
        IReadOnlyList<RoutingArchivedPlanDocument> archives)
    {
        RoutingOutboxModel.Validate(current);
        RoutingOutboxModel.Validate(candidate);
        ValidateArchiveSet(current, archives);
        RoutingValidation.Require(candidate.Generation == checked(current.Generation + 1) &&
                                  candidate.CreatedUtc == current.CreatedUtc &&
                                  candidate.UpdatedUtc >= current.UpdatedUtc,
            "Routing archive compaction changed the outbox envelope incorrectly.");
        var removedIds = archives.Select(item => item.Plan.PlanId).ToHashSet();
        RoutingValidation.Require(
            candidate.Plans.SequenceEqual(current.Plans.Where(item => !removedIds.Contains(item.PlanId))) &&
            candidate.Deliveries.SequenceEqual(
                current.Deliveries.Where(item => !removedIds.Contains(item.PlanId))) &&
            candidate.FileDispositions.SequenceEqual(
                current.FileDispositions.Where(item => !removedIds.Contains(item.PlanId))),
            "Routing archive compaction changed or removed unarchived work.");
    }

    private static void ValidateArchiveSet(
        RoutingOutboxDocument current,
        IReadOnlyList<RoutingArchivedPlanDocument> archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        RoutingValidation.Require(archives.Count > 0 &&
                                  archives.Select(item => item.Plan.PlanId).Distinct().Count() ==
                                  archives.Count &&
                                  archives.Select(item => item.Plan.SourceClipId)
                                      .Distinct(StringComparer.Ordinal).Count() == archives.Count,
            "Routing archive compaction requires a unique non-empty plan set.");
        foreach (var archive in archives)
        {
            Validate(archive);
            var expected = Create(current, archive.Plan.PlanId);
            RoutingValidation.Require(expected == archive,
                "Durable archive evidence does not exactly match the hot terminal plan.");
        }
    }

    private static void RequireTerminal(
        IReadOnlyList<PlannedDelivery> deliveries,
        IReadOnlyList<PlannedFileDisposition> dispositions) =>
        RoutingValidation.Require(deliveries.All(IsTerminal) && dispositions.All(IsTerminal),
            "Only irreversible terminal routing work can be archived.");

    private static bool IsTerminal(PlannedDelivery item) => item.State is
        PlannedDeliveryState.Delivered or PlannedDeliveryState.Skipped or
        PlannedDeliveryState.Cancelled or PlannedDeliveryState.Expired;

    private static bool IsTerminal(PlannedFileDisposition item) => item.State is
        PlannedFileDispositionState.Completed or PlannedFileDispositionState.Cancelled;
}

internal sealed class RoutingPlanArchiveStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumDocumentBytes = RoutingOutboxStore.MaximumDocumentBytes;
    private readonly string _root;

    internal RoutingPlanArchiveStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    internal string Root => _root;

    internal RoutingDocumentLoadResult<RoutingArchivedPlanDocument> LoadBySource(
        string sourceClipId,
        CancellationToken cancellationToken = default)
    {
        RoutingValidation.RequireOpaqueId(sourceClipId, 256, "archive source clip id");
        var result = StoreFor(sourceClipId).Load(cancellationToken);
        if (!result.LoadedFromDisk || result.Document is null) return result;
        return result.Document.Plan.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal)
            ? result
            : new RoutingDocumentLoadResult<RoutingArchivedPlanDocument>(
                null, RoutingDocumentLoadStatus.Invalid);
    }

    internal async Task<RoutingArchivedPlanDocument> PersistExactAsync(
        RoutingArchivedPlanDocument archive,
        CancellationToken cancellationToken = default)
    {
        RoutingArchiveModel.Validate(archive);
        var store = StoreFor(archive.Plan.SourceClipId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = LoadBySource(archive.Plan.SourceClipId, cancellationToken);
            if (existing.LoadedFromDisk && existing.Document is not null)
            {
                RequireSame(existing.Document, archive);
                return existing.Document;
            }
            if (existing.Status != RoutingDocumentLoadStatus.Missing)
                throw new InvalidDataException(
                    $"The routing plan archive cannot be loaded safely ({existing.Status}).");
            try
            {
                return await store.SaveAsync(archive, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (RoutingConcurrencyException) when (attempt < 3)
            {
                // Another instance may have committed the same immutable tombstone.
            }
        }
        throw new RoutingConcurrencyException(
            "The routing plan archive kept changing while it was created.");
    }

    internal void RequireExact(
        RoutingArchivedPlanDocument expected,
        CancellationToken cancellationToken = default)
    {
        var loaded = LoadBySource(expected.Plan.SourceClipId, cancellationToken);
        if (!loaded.LoadedFromDisk || loaded.Document is null)
            throw new InvalidDataException(
                $"The durable routing archive is unavailable ({loaded.Status}).");
        RequireSame(loaded.Document, expected);
    }

    internal IDisposable OpenValidatedLease(
        RoutingArchivedPlanDocument expected,
        CancellationToken cancellationToken = default)
    {
        RequireExact(expected, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(
            PathForSource(expected.Plan.SourceClipId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        try
        {
            // Close the lookup/open race before returning a handle that denies replacement.
            RequireExact(expected, cancellationToken);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static string SourceKey(string sourceClipId)
    {
        RoutingValidation.RequireOpaqueId(sourceClipId, 256, "archive source clip id");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceClipId)))
            .ToLowerInvariant();
    }

    private RoutingAtomicJsonStore<RoutingArchivedPlanDocument> StoreFor(string sourceClipId)
    {
        var path = PathForSource(sourceClipId);
        return new RoutingAtomicJsonStore<RoutingArchivedPlanDocument>(
            path,
            MaximumDocumentBytes,
            CurrentSchemaVersion,
            item => item.SchemaVersion,
            item => item.Generation,
            RoutingArchiveModel.Validate,
            (_, _) => throw new InvalidDataException("Routing plan archives are immutable."),
            archive =>
            {
                RoutingArchiveModel.Validate(archive);
                RoutingValidation.Require(archive.Generation == 1,
                    "The first routing plan archive generation is not canonical.");
            });
    }

    internal string PathForSource(string sourceClipId)
    {
        var key = SourceKey(sourceClipId);
        return Path.Combine(_root, key[..2], key + ".json");
    }

    private static void RequireSame(
        RoutingArchivedPlanDocument existing,
        RoutingArchivedPlanDocument expected) =>
        RoutingValidation.Require(existing == expected,
            "A different routing decision is already archived for this source clip.");
}

internal sealed record RoutingOutboxAdmissionResult(
    RoutingOutboxDocument Document,
    bool CanAcceptNewPlan,
    int ArchivedPlanCount);

internal sealed class RoutingOutboxCompactor(
    RoutingOutboxStore outboxStore,
    RoutingPlanArchiveStore archiveStore)
{
    private const int MaximumSaveAttempts = 4;
    private const int MaximumPlansPerBatch = 256;
    private const int MaximumBatchesPerAdmission = 8;

    internal async Task<RoutingOutboxAdmissionResult> PrepareForPlanningAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Action? beforeMutation = null)
    {
        var archivedCount = 0;
        RoutingOutboxDocument? current = null;
        for (var batch = 0; batch < MaximumBatchesPerAdmission; batch++)
        {
            current = await outboxStore.LoadOrCreateAsync(
                    now,
                    cancellationToken,
                    beforeMutation)
                .ConfigureAwait(false);
            var bytes = outboxStore.MeasureSerializedBytes(current);
            // Preserve 256 KiB for the next ordinary plan. A larger single proposal is rejected
            // explicitly by the bridge rather than risking the physical document ceiling.
            var reserve = Math.Min(256 * 1024, outboxStore.PlanningAdmissionLimitBytes / 4);
            if (bytes <= outboxStore.PlanningAdmissionLimitBytes - reserve)
                return new RoutingOutboxAdmissionResult(current, true, archivedCount);

            var result = await CompactBatchAsync(
                    current,
                    now,
                    cancellationToken,
                    beforeMutation)
                .ConfigureAwait(false);
            current = result.Document;
            archivedCount += result.ArchivedPlanCount;
            if (result.ArchivedPlanCount == 0)
            {
                return new RoutingOutboxAdmissionResult(
                    current,
                    outboxStore.MeasureSerializedBytes(current) <
                    outboxStore.PlanningAdmissionLimitBytes,
                    archivedCount);
            }
        }

        current ??= await outboxStore.LoadOrCreateAsync(
                now,
                cancellationToken,
                beforeMutation)
            .ConfigureAwait(false);
        return new RoutingOutboxAdmissionResult(
            current,
            outboxStore.MeasureSerializedBytes(current) < outboxStore.PlanningAdmissionLimitBytes,
            archivedCount);
    }

    internal async Task<RoutingOutboxAdmissionResult> CompactTerminalPlansAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await outboxStore.LoadOrCreateAsync(now, cancellationToken)
            .ConfigureAwait(false);
        return await CompactBatchAsync(current, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RoutingOutboxAdmissionResult> CompactBatchAsync(
        RoutingOutboxDocument initial,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Action? beforeMutation = null)
    {
        var current = initial;
        for (var attempt = 0; attempt < MaximumSaveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = current.Plans
                .Where(plan => RoutingArchiveModel.CanArchive(current, plan.PlanId))
                .OrderBy(plan => plan.CreatedUtc)
                .ThenBy(plan => plan.PlanId)
                .Take(MaximumPlansPerBatch)
                .Select(plan => RoutingArchiveModel.Create(current, plan.PlanId))
                .ToArray();
            if (selected.Length == 0)
                return new RoutingOutboxAdmissionResult(current, false, 0);

            foreach (var archive in selected)
            {
                beforeMutation?.Invoke();
                await archiveStore.PersistExactAsync(archive, cancellationToken)
                    .ConfigureAwait(false);
            }
            var candidate = RoutingArchiveModel.RemoveArchivedPlans(current, selected, now);
            try
            {
                var saved = await outboxStore.SaveCompactedAsync(
                        candidate,
                        current.Generation,
                        selected,
                        cancellationToken,
                        beforeMutation)
                    .ConfigureAwait(false);
                return new RoutingOutboxAdmissionResult(saved, true, selected.Length);
            }
            catch (RoutingConcurrencyException) when (attempt < MaximumSaveAttempts - 1)
            {
                current = await outboxStore.LoadOrCreateAsync(
                        now,
                        cancellationToken,
                        beforeMutation)
                    .ConfigureAwait(false);
            }
        }
        throw new RoutingConcurrencyException(
            "The routing outbox kept changing during archive compaction.");
    }
}
