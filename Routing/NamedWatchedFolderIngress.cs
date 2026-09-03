namespace ClipsToDiscord;

/// <summary>
/// Admission boundary for additive named SteelSeries/NVIDIA sources. It shares the global Routing
/// permit and outbox, but derives source authority from the input-source catalog instead of the
/// immutable 1.x migration marker.
/// </summary>
internal sealed class RoutingNamedWatchedFolderIngress
{
    private readonly RoutingSnapshotStore _snapshots;
    private readonly RoutingWatchedSourceJournalStore _journals;
    private readonly RoutingWatchedJournalFactory _journalFactory;
    private readonly RoutingNamedWatchedBaselineStore _baselines;
    private readonly RoutingInputSourceCatalog _sources;
    private readonly RoutingPlanCommitter _planCommitter;
    private readonly RoutingRuntimeFeatureGate _featureGate;

    internal RoutingNamedWatchedFolderIngress(
        RoutingSnapshotStore snapshots,
        RoutingWatchedSourceJournalStore journals,
        RoutingWatchedJournalFactory journalFactory,
        RoutingNamedWatchedBaselineStore baselines,
        RoutingInputSourceCatalog sources,
        RoutingPlanCommitter planCommitter,
        RoutingRuntimeFeatureGate featureGate)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _journalFactory = journalFactory ?? throw new ArgumentNullException(nameof(journalFactory));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _planCommitter = planCommitter ?? throw new ArgumentNullException(nameof(planCommitter));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
    }

    internal async Task<RoutingWatchedIngressResult> AdmitPathAsync(
        RoutingInputSourceRecord expectedSource,
        IRoutingWatchedSourceAdapter adapter,
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        ArgumentNullException.ThrowIfNull(adapter);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceAuthority = RequireCurrentSource(expectedSource, requireEnabled: true);
        RequireAdapter(sourceAuthority, adapter);
        var baselineLoad = _baselines.Load(sourceAuthority.SourceId, cancellationToken);
        if (!baselineLoad.LoadedFromDisk || baselineLoad.Document is null)
        {
            throw new InvalidDataException(
                "The named watched source has no trusted from-now baseline.");
        }

        var occurrence = await adapter.InspectOccurrenceAsync(
                sourceAuthority.CanonicalRoot,
                candidatePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (_baselines.IsIgnored(baselineLoad.Document, sourceAuthority, occurrence))
        {
            return new RoutingWatchedIngressResult(
                RoutingWatchedIngressStatus.Excluded,
                SourceClipId: null,
                PlanId: null,
                AdmissionKind: null);
        }

        var source = await adapter.OpenAndFingerprintAsync(
                sourceAuthority.CanonicalRoot,
                candidatePath,
                cancellationToken)
            .ConfigureAwait(false);
        sourceAuthority = RequireCurrentSource(sourceAuthority, requireEnabled: true);
        RequireSourceAuthority(sourceAuthority, source);
        return await AdmitAsync(sourceAuthority, source, adapter, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RoutingWatchedIngressResult> AdmitAsync(
        RoutingInputSourceRecord expectedSource,
        RoutingWatchedSourceFile source,
        IRoutingWatchedSourceAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapter);
        cancellationToken.ThrowIfCancellationRequested();
        var permit = _featureGate.Inspect();
        if (!permit.Enabled || permit.MarkerPayloadFingerprint is not { Length: 64 })
            return Disabled();
        var sourceAuthority = RequireCurrentSource(expectedSource, requireEnabled: true);
        RequireAdapter(sourceAuthority, adapter);
        RequireSourceAuthority(sourceAuthority, source);

        var snapshotLoad = _snapshots.Load(cancellationToken);
        if (!snapshotLoad.LoadedFromDisk || snapshotLoad.Document is null ||
            snapshotLoad.Document.Generation != permit.RoutingGeneration)
        {
            return Disabled();
        }
        var sourceFingerprint =
            RoutingWatchedJournalFactory.CreateNamedAuthorityFingerprint(sourceAuthority);
        var occurrence = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            source, sourceFingerprint);
        var sourceClipId = RoutingWatchedJournalModel.SourcePrefix + occurrence;
        var existing = _journals.Load(sourceClipId, cancellationToken);
        if (existing.LoadedFromDisk && existing.Document is { } existingDocument)
        {
            RequireSameAuthority(
                existingDocument,
                sourceAuthority,
                source,
                sourceFingerprint,
                permit.MarkerPayloadFingerprint);
            return await ReconcileJournalAsync(
                    existingDocument, sourceAuthority, permit, cancellationToken)
                .ConfigureAwait(false);
        }
        if (existing.Status != RoutingWatchedJournalLoadStatus.Missing)
        {
            throw new InvalidDataException(
                $"The named watched-source journal cannot be trusted ({existing.Status}).");
        }

        var prepared = await _journalFactory.CreateNamedAsync(
                source,
                adapter,
                sourceAuthority,
                permit.MarkerPayloadFingerprint,
                snapshotLoad.Document,
                cancellationToken)
            .ConfigureAwait(false);
        RoutingWatchedSourceJournalDocument durable;
        try
        {
            durable = await _journals.PersistExactAsync(
                    prepared,
                    cancellationToken,
                    beforeCommit: () => RequireCurrentJournalAuthority(
                        sourceAuthority,
                        permit))
                .ConfigureAwait(false);
        }
        catch (JournalAuthorityRevokedException)
        {
            return Disabled();
        }
        return await ReconcileJournalAsync(
                durable, sourceAuthority, permit, cancellationToken)
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
            var loaded = _journals.Load(sourceClipId, cancellationToken);
            if (!loaded.LoadedFromDisk || loaded.Document is null)
            {
                throw new InvalidDataException(
                    $"A watched source journal cannot be reconciled ({loaded.Status}).");
            }
            if (loaded.Document.SourceConnectionId is not { } sourceId) continue;
            var source = FindSource(sourceId);
            if (source is null)
            {
                results.Add(Disabled());
                continue;
            }
            results.Add(await ReconcileJournalAsync(
                    loaded.Document, source, permit, cancellationToken)
                .ConfigureAwait(false));
        }
        return results;
    }

    private async Task<RoutingWatchedIngressResult> ReconcileJournalAsync(
        RoutingWatchedSourceJournalDocument journal,
        RoutingInputSourceRecord source,
        RoutingRuntimeGateInspection permit,
        CancellationToken cancellationToken)
    {
        RoutingWatchedJournalModel.Validate(journal);
        if (journal.SourceConnectionId is null ||
            !journal.SourceConnectionId.Equals(source.SourceId, StringComparison.Ordinal) ||
            !journal.SourceRootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal) ||
            !journal.MarkerSourceFingerprint.Equals(
                RoutingWatchedJournalFactory.CreateNamedAuthorityFingerprint(source),
                StringComparison.Ordinal) ||
            !journal.MarkerPayloadFingerprint.Equals(
                permit.MarkerPayloadFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !permit.SamePermit(_featureGate.Inspect()) ||
            !HasCurrentSourceAuthority(source))
        {
            return Disabled();
        }
        if (journal.FrozenPlan is null)
        {
            return new RoutingWatchedIngressResult(
                RoutingWatchedIngressStatus.Excluded,
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
                    "The named watched-source plan commit status is unsupported.")
            },
            journal.SourceClipId,
            committed.PlanId,
            journal.AdmissionKind);
    }

    private RoutingInputSourceRecord RequireCurrentSource(
        RoutingInputSourceRecord expected,
        bool requireEnabled)
    {
        var current = FindSource(expected.SourceId) ??
                      throw new InvalidDataException(
                          "The named watched source is no longer registered.");
        RoutingValidation.Require(
            !current.Retired &&
            current.Kind == expected.Kind &&
            current.CanonicalRoot.Equals(
                expected.CanonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            current.RootIdentitySha256.Equals(
                expected.RootIdentitySha256, StringComparison.Ordinal) &&
            current.Health == RoutingInputSourceHealth.Ready &&
            (!requireEnabled || current.Enabled),
            "The named watched source changed or is not currently ready.");
        return current;
    }

    private void RequireCurrentJournalAuthority(
        RoutingInputSourceRecord expectedSource,
        RoutingRuntimeGateInspection expectedPermit)
    {
        if (!expectedPermit.SamePermit(_featureGate.Inspect()) ||
            !HasCurrentSourceAuthority(expectedSource))
        {
            throw new JournalAuthorityRevokedException();
        }
    }

    private bool HasCurrentSourceAuthority(RoutingInputSourceRecord expectedSource)
    {
        try
        {
            var current = RequireCurrentSource(expectedSource, requireEnabled: true);
            return RoutingWatchedJournalFactory.CreateNamedAuthorityFingerprint(current).Equals(
                RoutingWatchedJournalFactory.CreateNamedAuthorityFingerprint(expectedSource),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private RoutingInputSourceRecord? FindSource(string sourceId)
    {
        var snapshot = _sources.Inspect();
        if (!snapshot.IsUsable)
            throw new InvalidDataException("The named watched-source catalog is unavailable.");
        return snapshot.Sources.SingleOrDefault(item =>
            item.SourceId.Equals(sourceId, StringComparison.Ordinal));
    }

    private static void RequireAdapter(
        RoutingInputSourceRecord source,
        IRoutingWatchedSourceAdapter adapter)
    {
        var expected = source.Kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg => ClipCaptureSource.SteelSeriesGg,
            RoutingInputSourceKind.Nvidia => ClipCaptureSource.Nvidia,
            _ => throw new InvalidDataException(
                "The named input source is not a watched-folder recorder.")
        };
        RoutingValidation.Require(adapter.Source == expected,
            "The named watched source uses the wrong recorder adapter.");
    }

    private static void RequireSourceAuthority(
        RoutingInputSourceRecord authority,
        RoutingWatchedSourceFile source)
    {
        RoutingValidation.Require(
            authority.CanonicalRoot.Equals(
                source.CanonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            authority.RootIdentitySha256.Equals(
                source.RootIdentitySha256, StringComparison.Ordinal),
            "The named watched clip is outside its current source authority.");
    }

    private static void RequireSameAuthority(
        RoutingWatchedSourceJournalDocument journal,
        RoutingInputSourceRecord authority,
        RoutingWatchedSourceFile source,
        string sourceFingerprint,
        string routingAuthorityFingerprint)
    {
        RoutingWatchedJournalModel.Validate(journal);
        RoutingValidation.Require(
            journal.SourceConnectionId == authority.SourceId &&
            journal.CaptureSource == source.Source &&
            journal.SourceRootIdentitySha256 == source.RootIdentitySha256 &&
            journal.MarkerSourceFingerprint == sourceFingerprint &&
            journal.MarkerPayloadFingerprint.Equals(
                routingAuthorityFingerprint, StringComparison.OrdinalIgnoreCase) &&
            journal.SourceRelativePath == source.PortableRelativePath &&
            journal.DisplayFileName == source.DisplayFileName &&
            journal.GameName == source.GameName &&
            journal.FileIdentity == source.NativeFileIdentity &&
            journal.ContentSha256.Equals(source.ContentSha256, StringComparison.OrdinalIgnoreCase),
            "The named watched occurrence conflicts with its durable journal.");
    }

    private static RoutingWatchedIngressResult Disabled() => new(
        RoutingWatchedIngressStatus.Disabled,
        null,
        null,
        null);

    private sealed class JournalAuthorityRevokedException : Exception
    {
    }
}
