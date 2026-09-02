namespace ClipsToDiscord;

internal enum RoutingWatchedIngressStatus
{
    Disabled,
    Planned,
    AlreadyJournaled,
    AlreadyPlanned,
    AlreadyArchived,
    Excluded,
    // Read compatibility for journals written before physical-content dedup moved to receipts.
    ContentDuplicateNeedsAttention,
    CapacityNeedsAttention
}

internal sealed record RoutingWatchedIngressResult(
    RoutingWatchedIngressStatus Status,
    string? SourceClipId,
    Guid? PlanId,
    RoutingWatchedJournalAdmissionKind? AdmissionKind);

/// <summary>
/// Durable watched-source admission and startup reconciliation. This is intentionally separate
/// from UploaderWorker: the legacy watcher must already be quiesced and Routing must hold both the
/// process lease and sticky execution authority before any method can create a journal or outbox
/// decision.
/// </summary>
internal sealed class RoutingWatchedFolderIngress
{
    private readonly RoutingSnapshotStore _snapshots;
    private readonly LegacyRoutingMigrationMarkerStore _markers;
    private readonly RoutingWatchedSourceJournalStore _journals;
    private readonly RoutingWatchedJournalFactory _journalFactory;
    private readonly RoutingPlanCommitter _planCommitter;
    private readonly RoutingRuntimeFeatureGate _featureGate;
    private readonly object _knownOccurrencesSync = new();
    private readonly Dictionary<KnownOccurrenceKey, HashSet<string>> _knownOccurrences = [];

    internal RoutingWatchedFolderIngress(
        RoutingSnapshotStore snapshots,
        LegacyRoutingMigrationMarkerStore markers,
        RoutingWatchedSourceJournalStore journals,
        RoutingWatchedJournalFactory journalFactory,
        RoutingPlanCommitter planCommitter,
        RoutingRuntimeFeatureGate featureGate)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _journalFactory = journalFactory ?? throw new ArgumentNullException(nameof(journalFactory));
        _planCommitter = planCommitter ?? throw new ArgumentNullException(nameof(planCommitter));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
    }

    internal async Task<RoutingWatchedIngressResult> AdmitPathAsync(
        IRoutingWatchedSourceAdapter adapter,
        string clipsRoot,
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var occurrence = await adapter.InspectOccurrenceAsync(
                clipsRoot,
                candidatePath,
                cancellationToken)
            .ConfigureAwait(false);
        var known = await TryReconcileKnownOccurrenceAsync(
                occurrence,
                adapter,
                cancellationToken)
            .ConfigureAwait(false);
        if (known is not null) return known;

        var source = await adapter.OpenAndFingerprintAsync(
                clipsRoot,
                candidatePath,
                cancellationToken)
            .ConfigureAwait(false);
        return await AdmitAsync(source, adapter, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RoutingWatchedIngressResult> AdmitAsync(
        RoutingWatchedSourceFile source,
        IRoutingWatchedSourceAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        cancellationToken.ThrowIfCancellationRequested();
        var permit = _featureGate.Inspect();
        if (!permit.Enabled || source.Source != permit.RequiredLegacySource ||
            adapter.Source != source.Source)
        {
            return Disabled();
        }

        var markerLoad = _markers.Load(cancellationToken);
        var snapshotLoad = _snapshots.Load(cancellationToken);
        if (!markerLoad.LoadedFromDisk || markerLoad.Document is null ||
            markerLoad.Document.Phase != LegacyRoutingMigrationMarkerPhase.Committed ||
            !snapshotLoad.LoadedFromDisk || snapshotLoad.Document is null ||
            snapshotLoad.Document.Generation != permit.RoutingGeneration ||
            !markerLoad.Document.PayloadFingerprint.Equals(
                permit.MarkerPayloadFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Disabled();
        }
        var marker = markerLoad.Document;
        var occurrence = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            source,
            marker.SourceFingerprint);
        var sourceClipId = RoutingWatchedJournalModel.SourcePrefix + occurrence;
        var existing = _journals.Load(sourceClipId, cancellationToken);
        if (existing.LoadedFromDisk && existing.Document is not null)
        {
            RequireSameOccurrenceAuthority(existing.Document, source, marker, occurrence);
            Remember(existing.Document);
            return await ReconcileJournalAsync(existing.Document, permit, cancellationToken)
                .ConfigureAwait(false);
        }
        if (existing.Status != RoutingWatchedJournalLoadStatus.Missing)
        {
            throw new InvalidDataException(
                $"The watched source journal cannot be trusted ({existing.Status}).");
        }

        var prepared = await _journalFactory.CreateAsync(
                source,
                adapter,
                marker,
                snapshotLoad.Document,
                cancellationToken)
            .ConfigureAwait(false);
        if (!permit.SamePermit(_featureGate.Inspect())) return Disabled();

        var durable = await _journals.PersistExactAsync(prepared, cancellationToken)
            .ConfigureAwait(false);
        Remember(durable);
        return await ReconcileJournalAsync(durable, permit, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<RoutingWatchedIngressResult>> ReconcileAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permit = _featureGate.Inspect();
        if (!permit.Enabled) return [Disabled()];
        var results = new List<RoutingWatchedIngressResult>();
        foreach (var sourceClipId in _journals.EnumerateSourceClipIds(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!permit.SamePermit(_featureGate.Inspect()))
            {
                results.Add(Disabled());
                break;
            }
            var load = _journals.Load(sourceClipId, cancellationToken);
            if (!load.LoadedFromDisk || load.Document is null)
            {
                throw new InvalidDataException(
                    $"A watched source journal cannot be reconciled ({load.Status}).");
            }
            // Additive named sources share the immutable journal store but own their own
            // catalog authority and reconciliation loop.
            if (load.Document.SourceConnectionId is not null) continue;
            Remember(load.Document);
            results.Add(await ReconcileJournalAsync(
                    load.Document,
                    permit,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    private async Task<RoutingWatchedIngressResult?> TryReconcileKnownOccurrenceAsync(
        RoutingWatchedSourceOccurrence occurrence,
        IRoutingWatchedSourceAdapter adapter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permit = _featureGate.Inspect();
        if (!permit.Enabled || occurrence.Source != permit.RequiredLegacySource ||
            adapter.Source != occurrence.Source)
        {
            return Disabled();
        }

        var markerLoad = _markers.Load(cancellationToken);
        var snapshotLoad = _snapshots.Load(cancellationToken);
        if (!markerLoad.LoadedFromDisk || markerLoad.Document is null ||
            markerLoad.Document.Phase != LegacyRoutingMigrationMarkerPhase.Committed ||
            !snapshotLoad.LoadedFromDisk || snapshotLoad.Document is null ||
            snapshotLoad.Document.Generation != permit.RoutingGeneration ||
            !markerLoad.Document.PayloadFingerprint.Equals(
                permit.MarkerPayloadFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Disabled();
        }
        var marker = markerLoad.Document;
        var key = KnownOccurrenceKey.Create(occurrence, marker.SourceFingerprint);
        string? sourceClipId;
        lock (_knownOccurrencesSync)
        {
            sourceClipId = _knownOccurrences.TryGetValue(key, out var matches) &&
                           matches.Count == 1
                ? matches.Single()
                : null;
        }
        if (sourceClipId is null) return null;

        var load = _journals.Load(sourceClipId, cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null)
        {
            throw new InvalidDataException(
                $"A known watched source journal cannot be trusted ({load.Status}).");
        }
        RequireSameOccurrenceAuthority(load.Document, occurrence, marker);
        return await ReconcileJournalAsync(load.Document, permit, cancellationToken)
            .ConfigureAwait(false);
    }

    private void Remember(RoutingWatchedSourceJournalDocument journal)
    {
        RoutingWatchedJournalModel.Validate(journal);
        var key = KnownOccurrenceKey.Create(journal);
        lock (_knownOccurrencesSync)
        {
            if (!_knownOccurrences.TryGetValue(key, out var matches))
            {
                matches = new HashSet<string>(StringComparer.Ordinal);
                _knownOccurrences.Add(key, matches);
            }
            matches.Add(journal.SourceClipId);
        }
    }

    private async Task<RoutingWatchedIngressResult> ReconcileJournalAsync(
        RoutingWatchedSourceJournalDocument journal,
        RoutingRuntimeGateInspection permit,
        CancellationToken cancellationToken)
    {
        RoutingWatchedJournalModel.Validate(journal);
        if (!journal.MarkerPayloadFingerprint.Equals(
                permit.MarkerPayloadFingerprint, StringComparison.OrdinalIgnoreCase) ||
            journal.CaptureSource != permit.RequiredLegacySource)
        {
            return Disabled();
        }
        if (journal.FrozenPlan is null)
        {
            return new RoutingWatchedIngressResult(
                journal.AdmissionKind ==
                    RoutingWatchedJournalAdmissionKind.ContentDuplicateNeedsAttention
                    ? RoutingWatchedIngressStatus.ContentDuplicateNeedsAttention
                    : RoutingWatchedIngressStatus.Excluded,
                journal.SourceClipId,
                null,
                journal.AdmissionKind);
        }

        var committed = await _planCommitter.CommitExactAsync(
                journal.FrozenPlan,
                journal.CreatedUtc,
                permit,
                cancellationToken)
            .ConfigureAwait(false);
        return new RoutingWatchedIngressResult(
            committed.Status switch
            {
                RoutingPlanCommitStatus.Disabled => RoutingWatchedIngressStatus.Disabled,
                RoutingPlanCommitStatus.Committed => RoutingWatchedIngressStatus.Planned,
                RoutingPlanCommitStatus.AlreadyCommitted =>
                    RoutingWatchedIngressStatus.AlreadyPlanned,
                RoutingPlanCommitStatus.AlreadyArchived =>
                    RoutingWatchedIngressStatus.AlreadyArchived,
                RoutingPlanCommitStatus.CapacityNeedsAttention =>
                    RoutingWatchedIngressStatus.CapacityNeedsAttention,
                _ => throw new InvalidDataException(
                    "The watched source plan commit status is unsupported.")
            },
            journal.SourceClipId,
            committed.PlanId,
            journal.AdmissionKind);
    }

    private static void RequireSameOccurrenceAuthority(
        RoutingWatchedSourceJournalDocument journal,
        RoutingWatchedSourceFile source,
        LegacyRoutingMigrationMarker marker,
        string occurrence)
    {
        RequireSameOccurrenceAuthority(
            journal,
            new RoutingWatchedSourceOccurrence(
                source.Source,
                source.CanonicalRoot,
                source.PortableRelativePath,
                source.GameName,
                source.DisplayFileName,
                source.RootIdentitySha256,
                source.NativeFileIdentity),
            marker);
        RoutingValidation.Require(
            journal.OccurrenceIdentitySha256.Equals(occurrence, StringComparison.Ordinal) &&
            journal.ContentSha256.Equals(
                source.ContentSha256, StringComparison.OrdinalIgnoreCase),
            "The watched occurrence conflicts with its durable source journal.");
    }

    private static void RequireSameOccurrenceAuthority(
        RoutingWatchedSourceJournalDocument journal,
        RoutingWatchedSourceOccurrence occurrence,
        LegacyRoutingMigrationMarker marker)
    {
        RoutingWatchedJournalModel.Validate(journal);
        RoutingValidation.Require(
            journal.SourceRootIdentitySha256.Equals(
                occurrence.RootIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            journal.MarkerSourceFingerprint.Equals(
                marker.SourceFingerprint, StringComparison.OrdinalIgnoreCase) &&
            journal.MarkerPayloadFingerprint.Equals(
                marker.PayloadFingerprint, StringComparison.OrdinalIgnoreCase) &&
            journal.CaptureSource == occurrence.Source &&
            journal.SourceRelativePath.Equals(
                occurrence.PortableRelativePath, StringComparison.Ordinal) &&
            journal.DisplayFileName.Equals(
                occurrence.DisplayFileName, StringComparison.Ordinal) &&
            journal.GameName.Equals(occurrence.GameName, StringComparison.Ordinal) &&
            journal.FileIdentity == occurrence.NativeFileIdentity,
            "The watched occurrence conflicts with its durable source journal.");
    }

    private sealed record KnownOccurrenceKey(
        string MarkerSourceFingerprint,
        ClipCaptureSource CaptureSource,
        string RootIdentitySha256,
        string PortableRelativePath,
        RoutingWatchedNativeFileIdentity NativeFileIdentity)
    {
        internal static KnownOccurrenceKey Create(
            RoutingWatchedSourceOccurrence occurrence,
            string markerSourceFingerprint) => new(
            markerSourceFingerprint.ToLowerInvariant(),
            occurrence.Source,
            occurrence.RootIdentitySha256.ToLowerInvariant(),
            RoutingWatchedJournalModel.NormalizePortableRelativePath(
                    occurrence.PortableRelativePath)
                .ToUpperInvariant(),
            occurrence.NativeFileIdentity);

        internal static KnownOccurrenceKey Create(
            RoutingWatchedSourceJournalDocument journal) => new(
            journal.MarkerSourceFingerprint,
            journal.CaptureSource,
            journal.SourceRootIdentitySha256,
            journal.SourceRelativePath.ToUpperInvariant(),
            journal.FileIdentity);
    }

    private static RoutingWatchedIngressResult Disabled() => new(
        RoutingWatchedIngressStatus.Disabled,
        null,
        null,
        null);
}
