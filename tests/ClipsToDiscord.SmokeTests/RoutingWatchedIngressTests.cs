using ClipsToDiscord;

internal static class RoutingWatchedIngressTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 18, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertJournalBeforeOutboxRecoversAfterRestartAsync(
            Path.Combine(testRoot, "journal-before-outbox"));
        await AssertFrozenProposalSurvivesRouteEditsAsync(
            Path.Combine(testRoot, "frozen-route-edit"));
        await AssertLegacyExclusionsBypassProbeAsync(
            Path.Combine(testRoot, "exclusions"));
        await AssertContentDuplicateCreatesIndependentPlanAsync(
            Path.Combine(testRoot, "content-duplicate"));
        await AssertLegacyDuplicateJournalRemainsReadableAsync(
            Path.Combine(testRoot, "legacy-duplicate-read"));
        await AssertSameOccurrenceIsIdempotentAsync(
            Path.Combine(testRoot, "same-occurrence"));
        await AssertKnownPathAvoidsRepeatFingerprintingAsync(
            Path.Combine(testRoot, "known-path-fast-path"));
        await AssertFreshOriginUsesCurrentSnapshotAsync(
            Path.Combine(testRoot, "fresh-current-snapshot"));
        await AssertLegacyOriginStillRequiresImportedRouteAsync(
            Path.Combine(testRoot, "legacy-route-authority"));
        await AssertGateRevocationStopsAdmissionAndReconciliationAsync(
            Path.Combine(testRoot, "gate-revocation"));
    }

    private static async Task AssertJournalBeforeOutboxRecoversAfterRestartAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var source = await fixture.CreateSourceAsync(
            "Ingress Game 2026.08.29 - 18.00.00.00.DVR.mp4",
            new string('1', 64),
            fileIndex: 1);
        var adapter = new RecordingAdapter(source.Source);
        var frozen = await fixture.JournalFactory.CreateAsync(
            source,
            adapter,
            fixture.Marker,
            fixture.Snapshot,
            fixture.Gate.Inspect(),
            cancellationToken: CancellationToken.None);
        _ = await fixture.Journals.PersistExactAsync(frozen);

        Assert(fixture.Outbox.Load().Status == RoutingDocumentLoadStatus.Missing &&
               fixture.Journals.Load(frozen.SourceClipId).LoadedFromDisk,
            "The write-ahead journal must be durable before the outbox exists.");

        var restartedJournals = new RoutingWatchedSourceJournalStore(
            fixture.Journals.Root);
        var restartedOutbox = new RoutingOutboxStore(fixture.Outbox.Path);
        var restarted = new RoutingWatchedFolderIngress(
            fixture.Snapshots,
            fixture.Markers,
            restartedJournals,
            new RoutingWatchedJournalFactory(new RecordingProbe()),
            new RoutingPlanCommitter(restartedOutbox, fixture.Gate),
            fixture.Gate);
        var first = await restarted.ReconcileAllAsync();
        Assert(first.Count == 1 &&
               first[0].Status == RoutingWatchedIngressStatus.Planned &&
               first[0].SourceClipId == frozen.SourceClipId &&
               first[0].PlanId == frozen.FrozenPlan!.PlanId,
            "Startup reconciliation must append the exact journaled plan after a journal-before-outbox crash.");
        AssertOutboxMatchesFrozen(restartedOutbox, frozen);

        var secondRestart = new RoutingWatchedFolderIngress(
            fixture.Snapshots,
            fixture.Markers,
            new RoutingWatchedSourceJournalStore(fixture.Journals.Root),
            new RoutingWatchedJournalFactory(new RecordingProbe()),
            new RoutingPlanCommitter(
                new RoutingOutboxStore(fixture.Outbox.Path),
                fixture.Gate),
            fixture.Gate);
        var repeated = await secondRestart.ReconcileAllAsync();
        Assert(repeated.Count == 1 &&
               repeated[0].Status == RoutingWatchedIngressStatus.AlreadyPlanned,
            "A second startup reconciliation must recognize the already-committed exact plan.");
        AssertOutboxMatchesFrozen(new RoutingOutboxStore(fixture.Outbox.Path), frozen);
    }

    private static async Task AssertFrozenProposalSurvivesRouteEditsAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var source = await fixture.CreateSourceAsync(
            "Ingress Game 2026.08.29 - 18.01.00.00.DVR.mp4",
            new string('2', 64),
            fileIndex: 2);
        var adapter = new RecordingAdapter(source.Source);
        var frozen = await fixture.JournalFactory.CreateAsync(
            source,
            adapter,
            fixture.Marker,
            fixture.Snapshot,
            fixture.Gate.Inspect());
        _ = await fixture.Journals.PersistExactAsync(frozen);
        var frozenDelivery = frozen.FrozenPlan!.Deliveries.Single();
        Assert(frozenDelivery.ConnectionId == Fixture.OriginalConnectionId &&
               frozenDelivery.Route.RouteRevision == 1 &&
               frozen.FrozenPlan.RoutingGeneration == 1,
            "The fixture must freeze the original route before editing it.");

        var changedRoute = fixture.CustomRoute with
        {
            Revision = 2,
            Actions = fixture.CustomRoute.Actions.Select(action =>
                    action.Kind == RoutingActionKind.Deliver
                        ? action with
                        {
                            ConnectionId = Fixture.ChangedConnectionId,
                            DeliverySettings = action.DeliverySettings! with
                            {
                                Title = "Changed after discovery"
                            }
                        }
                        : action)
                .ToArray(),
            ModifiedUtc = Now.AddMinutes(5)
        };
        var changedSnapshot = fixture.Snapshot with
        {
            Generation = 2,
            Routes = [changedRoute, fixture.Marker.Route],
            UpdatedUtc = Now.AddMinutes(5)
        };
        _ = await fixture.Snapshots.SaveAsync(
            changedSnapshot,
            fixture.Snapshot.Generation);
        Assert(fixture.Gate.Enabled && fixture.Gate.Inspect().RoutingGeneration == 2,
            "A valid non-migration route edit must keep cutover authority enabled at the new generation.");

        var result = await fixture.Ingress.ReconcileAllAsync();
        var outbox = RequireOutbox(fixture.Outbox);
        var actualDelivery = outbox.Deliveries.Single();
        Assert(result.Single().Status == RoutingWatchedIngressStatus.Planned &&
               outbox.Plans.Single().PlanId == frozen.FrozenPlan.PlanId &&
               outbox.Plans.Single().RoutingGeneration == 1 &&
               actualDelivery.ConnectionId == Fixture.OriginalConnectionId &&
               actualDelivery.ConnectionId != Fixture.ChangedConnectionId &&
               actualDelivery.Route == frozenDelivery.Route &&
               actualDelivery.Settings == frozenDelivery.Settings,
            "Reconciliation must commit the exact proposal frozen before a later route edit, never re-evaluate generation two.");
    }

    private static async Task AssertLegacyExclusionsBypassProbeAsync(string root)
    {
        var uploadedHash = new string('a', 64);
        var localOnlyHash = new string('b', 64);
        var knownOnlyHash = new string('c', 64);
        using var fixture = await Fixture.CreateAsync(
            root,
            knownHashes: [uploadedHash, localOnlyHash, knownOnlyHash],
            uploadedHashes: [uploadedHash],
            localOnlyHashes: [localOnlyHash]);
        var probe = new RecordingProbe();
        var planIdCalls = 0;
        var factory = new RoutingWatchedJournalFactory(
            probe,
            createPlanId: () =>
            {
                Interlocked.Increment(ref planIdCalls);
                return Guid.NewGuid();
            },
            utcNow: () => Now.AddMinutes(1));
        var adapter = new RecordingAdapter(ClipCaptureSource.SteelSeriesGg);

        async Task<RoutingWatchedSourceJournalDocument> CreateAsync(
            string name,
            string hash,
            long fileIndex) => await factory.CreateAsync(
            await fixture.CreateSourceAsync(name, hash, fileIndex),
            adapter,
            fixture.Marker,
            fixture.Snapshot,
            fixture.Gate.Inspect());

        var uploaded = await CreateAsync("uploaded.mp4", uploadedHash, 10);
        var localOnly = await CreateAsync("local-only.mp4", localOnlyHash, 11);
        var known = await CreateAsync("known.mp4", knownOnlyHash, 12);
        Assert(uploaded.AdmissionKind ==
                   RoutingWatchedJournalAdmissionKind.LegacyUploadedExcluded &&
               localOnly.AdmissionKind ==
                   RoutingWatchedJournalAdmissionKind.LegacyLocalOnlyExcluded &&
               known.AdmissionKind ==
                   RoutingWatchedJournalAdmissionKind.LegacyKnownExcluded &&
               new[] { uploaded, localOnly, known }.All(document =>
                   document.FrozenPlan is null &&
                   document.DurationMilliseconds == 0 &&
                   document.Width == 0 && document.Height == 0) &&
               probe.Calls == 0 && adapter.RevalidationCalls == 0 &&
               Volatile.Read(ref planIdCalls) == 0,
            "Every migrated hash exclusion must become terminal before media probing, revalidation, or plan creation.");
    }

    private static async Task AssertContentDuplicateCreatesIndependentPlanAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var hash = new string('3', 64);
        var firstSource = await fixture.CreateSourceAsync(
            "Ingress Game first.mp4",
            hash,
            fileIndex: 20);
        var secondSource = await fixture.CreateSourceAsync(
            "Ingress Game second.mp4",
            hash,
            fileIndex: 21);
        var adapter = new RecordingAdapter(ClipCaptureSource.SteelSeriesGg);

        var first = await fixture.Ingress.AdmitAsync(firstSource, adapter);
        var afterFirst = RequireOutbox(fixture.Outbox);
        var second = await fixture.Ingress.AdmitAsync(secondSource, adapter);
        var afterSecond = RequireOutbox(fixture.Outbox);
        var firstJournal = fixture.Journals.Load(first.SourceClipId!).Document!;
        var secondJournal = fixture.Journals.Load(second.SourceClipId!).Document!;
        var sourceIds = new HashSet<string>(StringComparer.Ordinal)
        {
            first.SourceClipId!,
            second.SourceClipId!
        };
        Assert(first.Status == RoutingWatchedIngressStatus.Planned &&
               second.Status == RoutingWatchedIngressStatus.Planned &&
               first.AdmissionKind == RoutingWatchedJournalAdmissionKind.PreparedPlan &&
               second.AdmissionKind == RoutingWatchedJournalAdmissionKind.PreparedPlan &&
               second.SourceClipId != first.SourceClipId &&
               first.PlanId != second.PlanId &&
               firstJournal.FrozenPlan is not null && secondJournal.FrozenPlan is not null &&
               first.PlanId == firstJournal.FrozenPlan.PlanId &&
               second.PlanId == secondJournal.FrozenPlan.PlanId &&
               firstJournal.DuplicateOfSourceClipId is null &&
               secondJournal.DuplicateOfSourceClipId is null &&
               fixture.Journals.EnumerateSourceClipIds().Count == 2 &&
               afterSecond.Generation == afterFirst.Generation + 1 &&
               afterSecond.Plans.Count == 2 && afterSecond.Deliveries.Count == 2 &&
               afterSecond.FileDispositions.Count == 2 &&
               afterSecond.FileDispositions.Select(disposition => disposition.DispositionId)
                   .Distinct().Count() == 2 &&
               afterSecond.Plans.Select(plan => plan.SourceClipId)
                   .ToHashSet(StringComparer.Ordinal).SetEquals(sourceIds) &&
               afterSecond.Deliveries.All(delivery =>
                   sourceIds.Contains(delivery.SourceClipId) &&
                   delivery.ConnectionId == Fixture.OriginalConnectionId &&
                   delivery.Output.Revision.Equals(hash, StringComparison.OrdinalIgnoreCase)) &&
               afterSecond.FileDispositions.All(disposition =>
                   sourceIds.Contains(disposition.SourceClipId) &&
                   disposition.SourceContentSha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) &&
               adapter.RevalidationCalls == 2,
            "Each physical occurrence must freeze its own plan and file disposition; provider receipts own cross-occurrence delivery deduplication.");
        AssertOutboxMatchesFrozen(fixture.Outbox, firstJournal);
        AssertOutboxMatchesFrozen(fixture.Outbox, secondJournal);
    }

    private static async Task AssertSameOccurrenceIsIdempotentAsync(string root)
    {
        var probe = new RecordingProbe();
        using var fixture = await Fixture.CreateAsync(root, probe: probe);
        var source = await fixture.CreateSourceAsync(
            "Ingress Game repeat.mp4",
            new string('4', 64),
            fileIndex: 30);
        var adapter = new RecordingAdapter(source.Source);

        var first = await fixture.Ingress.AdmitAsync(source, adapter);
        var afterFirst = RequireOutbox(fixture.Outbox);
        var durable = fixture.Journals.Load(first.SourceClipId!).Document ??
                      throw new InvalidOperationException(
                          "The idempotence fixture did not persist its first journal.");
        var existingAuthorityChecks = 0;
        _ = await fixture.Journals.PersistExactAsync(
            durable,
            beforeCommit: () => Interlocked.Increment(ref existingAuthorityChecks));
        var second = await fixture.Ingress.AdmitAsync(source, adapter);
        var afterSecond = RequireOutbox(fixture.Outbox);
        Assert(first.Status == RoutingWatchedIngressStatus.Planned &&
               second.Status == RoutingWatchedIngressStatus.AlreadyPlanned &&
               first.SourceClipId == second.SourceClipId &&
               first.PlanId == second.PlanId &&
               fixture.Journals.EnumerateSourceClipIds().Count == 1 &&
               afterSecond.Plans.Count == 1 &&
               afterSecond.Deliveries.Count == 1 &&
               afterSecond.Generation == afterFirst.Generation &&
               probe.Calls == 1 && adapter.RevalidationCalls == 1 &&
               existingAuthorityChecks == 1,
            "Retrying the same native occurrence must reuse one journal and one exact outbox plan without probing again, while the immutable-store return path still rechecks commit authority.");
    }

    private static async Task AssertKnownPathAvoidsRepeatFingerprintingAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var source = await fixture.CreateSourceAsync(
            "Ingress Game fast path.mp4",
            new string('8', 64),
            fileIndex: 31);
        var path = Path.Combine(fixture.ClipsRoot, source.PortableRelativePath);
        var adapter = new PathRecordingAdapter(source);

        var first = await fixture.Ingress.AdmitPathAsync(
            adapter,
            fixture.ClipsRoot,
            path);
        var repeated = await fixture.Ingress.AdmitPathAsync(
            adapter,
            fixture.ClipsRoot,
            path);
        Assert(first.Status == RoutingWatchedIngressStatus.Planned &&
               repeated.Status == RoutingWatchedIngressStatus.AlreadyPlanned &&
               adapter.InspectionCalls == 2 && adapter.FingerprintCalls == 1,
            "An unchanged journaled path must use native occurrence identity instead of hashing its entire MP4 again.");

        var restarted = new RoutingWatchedFolderIngress(
            fixture.Snapshots,
            fixture.Markers,
            new RoutingWatchedSourceJournalStore(fixture.Journals.Root),
            new RoutingWatchedJournalFactory(new RecordingProbe()),
            new RoutingPlanCommitter(
                new RoutingOutboxStore(fixture.Outbox.Path),
                fixture.Gate),
            fixture.Gate);
        _ = await restarted.ReconcileAllAsync();
        var restartedAdapter = new PathRecordingAdapter(source);
        var afterRestart = await restarted.AdmitPathAsync(
            restartedAdapter,
            fixture.ClipsRoot,
            path);
        Assert(afterRestart.Status == RoutingWatchedIngressStatus.AlreadyPlanned &&
               restartedAdapter.InspectionCalls == 1 &&
               restartedAdapter.FingerprintCalls == 0,
            "Startup journal reconciliation must rebuild the native occurrence index so unchanged baseline clips are not rehashed after every restart.");

        var changed = source with
        {
            NativeFileIdentity = source.NativeFileIdentity with
            {
                LastWriteUtcTicks = source.NativeFileIdentity.LastWriteUtcTicks + 1
            },
            ContentSha256 = new string('9', 64)
        };
        var changedAdapter = new PathRecordingAdapter(changed);
        var changedResult = await restarted.AdmitPathAsync(
            changedAdapter,
            fixture.ClipsRoot,
            path);
        Assert(changedResult.Status == RoutingWatchedIngressStatus.Planned &&
               changedAdapter.InspectionCalls == 1 &&
               changedAdapter.FingerprintCalls == 1 &&
               changedResult.SourceClipId != first.SourceClipId,
            "A changed native occurrence must miss the fast path, receive a fresh content hash, and journal as new work.");
    }

    private static async Task AssertLegacyDuplicateJournalRemainsReadableAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var source = await fixture.CreateSourceAsync(
            "Ingress Game legacy duplicate.mp4",
            new string('7', 64),
            fileIndex: 22);
        var occurrence = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            source,
            fixture.Marker.SourceFingerprint);
        var priorSourceClipId = RoutingWatchedJournalModel.SourcePrefix + new string('f', 64);
        if (priorSourceClipId.Equals(
                RoutingWatchedJournalModel.SourcePrefix + occurrence,
                StringComparison.Ordinal))
        {
            priorSourceClipId = RoutingWatchedJournalModel.SourcePrefix + new string('e', 64);
        }
        var legacy = RoutingWatchedJournalModel.Create(
            RoutingWatchedJournalAdmissionKind.ContentDuplicateNeedsAttention,
            occurrence,
            source.RootIdentitySha256,
            fixture.Marker.SourceFingerprint,
            fixture.Marker.PayloadFingerprint,
            source.Source,
            source.PortableRelativePath,
            source.DisplayFileName,
            source.GameName,
            source.NativeFileIdentity,
            source.ContentSha256,
            durationMilliseconds: 30_000,
            width: 1920,
            height: 1080,
            frozenPlan: null,
            duplicateOfSourceClipId: priorSourceClipId,
            Now.AddMinutes(1));
        _ = await fixture.Journals.PersistExactAsync(legacy);

        var load = fixture.Journals.Load(legacy.SourceClipId);
        var result = await fixture.Ingress.ReconcileAllAsync();
        Assert(load.LoadedFromDisk && load.Document == legacy && result.Count == 1 &&
               result[0].Status == RoutingWatchedIngressStatus.ContentDuplicateNeedsAttention &&
               result[0].SourceClipId == legacy.SourceClipId &&
               fixture.Outbox.Load().Status == RoutingDocumentLoadStatus.Missing,
            "Pre-receipt duplicate-attention journals must remain readable without inventing a plan.");
    }

    private static async Task AssertFreshOriginUsesCurrentSnapshotAsync(string root)
    {
        var excludedHash = new string('a', 64);
        using var fixture = await Fixture.CreateAsync(
            root,
            knownHashes: [excludedHash],
            fresh: true);
        var adapter = new RecordingAdapter(ClipCaptureSource.SteelSeriesGg);
        Assert(fixture.Marker.Origin == RoutingActivationOrigin.FreshSetup &&
               fixture.Gate.Enabled,
            "The fresh watched-ingress fixture must begin under committed execution authority.");

        var excludedSource = await fixture.CreateSourceAsync(
            "Fresh baseline excluded.mp4",
            excludedHash,
            fileIndex: 50);
        var excluded = await fixture.Ingress.AdmitAsync(excludedSource, adapter);
        var excludedJournal = fixture.Journals.Load(excluded.SourceClipId!).Document!;
        Assert(excluded.Status == RoutingWatchedIngressStatus.Excluded &&
               excluded.AdmissionKind == RoutingWatchedJournalAdmissionKind.LegacyKnownExcluded &&
               excludedJournal.MarkerSourceFingerprint.Equals(
                   fixture.Marker.SourceFingerprint, StringComparison.OrdinalIgnoreCase) &&
               excludedJournal.MarkerPayloadFingerprint.Equals(
                   fixture.Marker.PayloadFingerprint, StringComparison.OrdinalIgnoreCase),
            "A fresh baseline exclusion must remain terminal and bind its journal to the exact fresh source and authority payload.");

        var initialSource = await fixture.CreateSourceAsync(
            "Fresh initial route.mp4",
            new string('b', 64),
            fileIndex: 51);
        await AssertThrowsAsync<InvalidDataException>(
            () => fixture.JournalFactory.CreateAsync(
                initialSource,
                adapter,
                fixture.Marker,
                fixture.Snapshot,
                RoutingRuntimeFeatureGate.Disabled.Inspect()),
            "A committed fresh marker and route snapshot must not bypass the live execution feature gate.");
        var initial = await fixture.Ingress.AdmitAsync(initialSource, adapter);
        var initialPlan = fixture.Journals.Load(initial.SourceClipId!).Document!.FrozenPlan!;
        Assert(initial.Status == RoutingWatchedIngressStatus.Planned &&
               initialPlan.RoutingGeneration == fixture.Snapshot.Generation &&
               initialPlan.MatchedRouteIds.SequenceEqual([fixture.Marker.Route.RouteId]) &&
               initialPlan.HasWork,
            "A fresh committed marker must admit ordinary watched-source work through its exact first snapshot.");

        var editedRoute = fixture.Marker.Route with
        {
            Enabled = false,
            Revision = fixture.Marker.Route.Revision + 1,
            ModifiedUtc = Now.AddMinutes(2)
        };
        var editedSnapshot = RoutingSnapshotModel.ReplaceRoutes(
            fixture.Snapshot,
            [editedRoute],
            Now.AddMinutes(2));
        _ = await fixture.Snapshots.SaveAsync(
            editedSnapshot,
            fixture.Snapshot.Generation);
        Assert(fixture.Gate.Enabled,
            "Editing the fresh first route after authority must retain watched-source execution authority.");

        var editedSource = await fixture.CreateSourceAsync(
            "Fresh edited route.mp4",
            new string('c', 64),
            fileIndex: 52);
        var edited = await fixture.Ingress.AdmitAsync(editedSource, adapter);
        var editedPlan = fixture.Journals.Load(edited.SourceClipId!).Document!.FrozenPlan!;
        Assert(edited.Status == RoutingWatchedIngressStatus.Planned &&
               editedPlan.RoutingGeneration == editedSnapshot.Generation &&
               editedPlan.MatchedRouteIds.Count == 0 && !editedPlan.HasWork,
            "Fresh watched ingress must evaluate the current edited snapshot instead of requiring or replaying the frozen first route.");

        var emptySnapshot = RoutingSnapshotModel.ReplaceRoutes(
            editedSnapshot,
            [],
            Now.AddMinutes(3));
        _ = await fixture.Snapshots.SaveAsync(emptySnapshot, editedSnapshot.Generation);
        Assert(fixture.Gate.Enabled,
            "Deleting the fresh first route after authority must retain watched-source execution authority.");

        var deletedSource = await fixture.CreateSourceAsync(
            "Fresh deleted route.mp4",
            new string('d', 64),
            fileIndex: 53);
        var deleted = await fixture.Ingress.AdmitAsync(deletedSource, adapter);
        var deletedPlan = fixture.Journals.Load(deleted.SourceClipId!).Document!.FrozenPlan!;
        Assert(deleted.Status == RoutingWatchedIngressStatus.Planned &&
               deletedPlan.RoutingGeneration == emptySnapshot.Generation &&
               deletedPlan.MatchedRouteIds.Count == 0 && !deletedPlan.HasWork,
            "A valid empty fresh snapshot may produce no actions, but it must not be rejected as an authority failure.");
    }

    private static async Task AssertLegacyOriginStillRequiresImportedRouteAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var source = await fixture.CreateSourceAsync(
            "Legacy exact route.mp4",
            new string('e', 64),
            fileIndex: 54);
        await AssertThrowsAsync<InvalidDataException>(
            () => fixture.JournalFactory.CreateAsync(
                source,
                new RecordingAdapter(source.Source),
                fixture.Marker,
                fixture.Snapshot,
                RoutingRuntimeFeatureGate.Disabled.Inspect()),
            "Legacy-origin journal preparation must require a current live execution permit.");
        var withoutImportedRoute = fixture.Snapshot with
        {
            Routes = [fixture.CustomRoute]
        };
        await AssertThrowsAsync<InvalidDataException>(
            () => fixture.JournalFactory.CreateAsync(
                source,
                new RecordingAdapter(source.Source),
                fixture.Marker,
                withoutImportedRoute,
                fixture.Gate.Inspect()),
            "Legacy-origin watched ingress must continue to require the exact imported migration route.");
    }

    private static async Task AssertGateRevocationStopsAdmissionAndReconciliationAsync(
        string root)
    {
        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "during-admission")))
        {
            var source = await fixture.CreateSourceAsync(
                "Ingress Game revoked.mp4",
                new string('5', 64),
                fileIndex: 40);
            var adapter = new RecordingAdapter(
                source.Source,
                onRevalidate: fixture.RevokeGate);
            var result = await fixture.Ingress.AdmitAsync(source, adapter);
            Assert(result.Status == RoutingWatchedIngressStatus.Disabled &&
                   !fixture.Journals.EnumerateSourceClipIds().Any() &&
                   fixture.Outbox.Load().Status == RoutingDocumentLoadStatus.Missing,
                "Revoking execution authority during probe revalidation must be rechecked at the atomic journal commit boundary and return Disabled without journal or outbox persistence.");
        }

        using (var fixture = await Fixture.CreateAsync(Path.Combine(root, "before-reconcile")))
        {
            var source = await fixture.CreateSourceAsync(
                "Ingress Game journaled then revoked.mp4",
                new string('6', 64),
                fileIndex: 41);
            var adapter = new RecordingAdapter(source.Source);
            var journal = await fixture.JournalFactory.CreateAsync(
                source,
                adapter,
                fixture.Marker,
                fixture.Snapshot,
                fixture.Gate.Inspect());
            _ = await fixture.Journals.PersistExactAsync(journal);
            fixture.RevokeGate();

            var result = await fixture.Ingress.ReconcileAllAsync();
            Assert(result.Count == 1 &&
                   result[0].Status == RoutingWatchedIngressStatus.Disabled &&
                   fixture.Journals.Load(journal.SourceClipId).LoadedFromDisk &&
                   fixture.Outbox.Load().Status == RoutingDocumentLoadStatus.Missing,
                "A durable journal must remain recoverable while revoked authority prevents its outbox append.");
        }
    }

    private static void AssertOutboxMatchesFrozen(
        RoutingOutboxStore store,
        RoutingWatchedSourceJournalDocument journal)
    {
        var proposal = journal.FrozenPlan ??
                       throw new InvalidOperationException(
                           "The test journal has no frozen plan.");
        var outbox = RequireOutbox(store);
        var plan = outbox.Plans.Single(item => item.SourceClipId == journal.SourceClipId);
        Assert(plan.PlanId == proposal.PlanId &&
               plan.RoutingGeneration == proposal.RoutingGeneration &&
               plan.MatchedRouteIds.SequenceEqual(proposal.MatchedRouteIds) &&
               outbox.Deliveries.Where(item => item.PlanId == proposal.PlanId)
                   .SequenceEqual(proposal.Deliveries) &&
               (proposal.FileDisposition is null
                   ? outbox.FileDispositions.All(item => item.PlanId != proposal.PlanId)
                   : outbox.FileDispositions.Single(item => item.PlanId == proposal.PlanId) ==
                     proposal.FileDisposition),
            "The outbox must contain the exact immutable proposal from the watched journal.");
    }

    private static RoutingOutboxDocument RequireOutbox(RoutingOutboxStore store)
    {
        var load = store.Load();
        if (!load.LoadedFromDisk || load.Document is null)
        {
            throw new InvalidOperationException(
                $"The focused outbox was unavailable ({load.Status}).");
        }
        return load.Document;
    }

    private sealed class RecordingProbe(Action? onProbe = null) :
        IRoutingWatchedFolderMediaProbe
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            onProbe?.Invoke();
            return Task.FromResult(new RoutingWatchedFolderMediaInfo(
                TimeSpan.FromSeconds(30),
                1920,
                1080));
        }
    }

    private sealed class RecordingAdapter(
        ClipCaptureSource source,
        Action? onRevalidate = null) : IRoutingWatchedSourceAdapter
    {
        private int _revalidationCalls;
        internal int RevalidationCalls => Volatile.Read(ref _revalidationCalls);
        public ClipCaptureSource Source { get; } = source;

        public string InspectRootIdentity(string clipsRoot) => new string('d', 64);

        public IReadOnlyList<string> EnumerateCandidates(
            string clipsRoot,
            CancellationToken cancellationToken = default) => [];

        public Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Focused ingress tests enter after source inspection.");

        public Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "Focused ingress tests enter after source fingerprinting.");

        public Task<RoutingWatchedSourceFile> RevalidateAsync(
            RoutingWatchedSourceFile prior,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prior.Source != Source)
            {
                throw new InvalidDataException(
                    "The test adapter received a source from another adapter.");
            }
            Interlocked.Increment(ref _revalidationCalls);
            onRevalidate?.Invoke();
            return Task.FromResult(prior);
        }
    }

    private sealed class PathRecordingAdapter(RoutingWatchedSourceFile source) :
        IRoutingWatchedSourceAdapter
    {
        private int _inspectionCalls;
        private int _fingerprintCalls;

        internal int InspectionCalls => Volatile.Read(ref _inspectionCalls);
        internal int FingerprintCalls => Volatile.Read(ref _fingerprintCalls);
        public ClipCaptureSource Source => source.Source;

        public string InspectRootIdentity(string clipsRoot) => source.RootIdentitySha256;

        public IReadOnlyList<string> EnumerateCandidates(
            string clipsRoot,
            CancellationToken cancellationToken = default) =>
            [Path.Combine(clipsRoot, source.PortableRelativePath)];

        public Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _inspectionCalls);
            return Task.FromResult(new RoutingWatchedSourceOccurrence(
                source.Source,
                source.CanonicalRoot,
                source.PortableRelativePath,
                source.GameName,
                source.DisplayFileName,
                source.RootIdentitySha256,
                source.NativeFileIdentity));
        }

        public Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _fingerprintCalls);
            return Task.FromResult(source);
        }

        public Task<RoutingWatchedSourceFile> RevalidateAsync(
            RoutingWatchedSourceFile prior,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(prior);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string OriginalConnectionId = "discord.original";
        internal const string ChangedConnectionId = "discord.changed";
        private const string LegacyConnectionId = "discord.friends";
        private readonly ClipProcessingOwnershipLease _routingLease;

        private Fixture(
            string clipsRoot,
            RoutingSnapshotDocument snapshot,
            RoutingRoute customRoute,
            RoutingSnapshotStore snapshots,
            LegacyRoutingMigrationMarker marker,
            LegacyRoutingMigrationMarkerStore markers,
            RoutingWatchedSourceJournalStore journals,
            RoutingOutboxStore outbox,
            RoutingRuntimeFeatureGate gate,
            RoutingWatchedJournalFactory journalFactory,
            RoutingWatchedFolderIngress ingress,
            ClipProcessingOwnershipLease routingLease)
        {
            ClipsRoot = clipsRoot;
            Snapshot = snapshot;
            CustomRoute = customRoute;
            Snapshots = snapshots;
            Marker = marker;
            Markers = markers;
            Journals = journals;
            Outbox = outbox;
            Gate = gate;
            JournalFactory = journalFactory;
            Ingress = ingress;
            _routingLease = routingLease;
        }

        internal string ClipsRoot { get; }
        internal RoutingSnapshotDocument Snapshot { get; }
        internal RoutingRoute CustomRoute { get; }
        internal RoutingSnapshotStore Snapshots { get; }
        internal LegacyRoutingMigrationMarker Marker { get; }
        internal LegacyRoutingMigrationMarkerStore Markers { get; }
        internal RoutingWatchedSourceJournalStore Journals { get; }
        internal RoutingOutboxStore Outbox { get; }
        internal RoutingRuntimeFeatureGate Gate { get; }
        internal RoutingWatchedJournalFactory JournalFactory { get; }
        internal RoutingWatchedFolderIngress Ingress { get; }

        internal static async Task<Fixture> CreateAsync(
            string root,
            RecordingProbe? probe = null,
            IReadOnlyList<string>? knownHashes = null,
            IReadOnlyList<string>? uploadedHashes = null,
            IReadOnlyList<string>? localOnlyHashes = null,
            bool fresh = false)
        {
            Directory.CreateDirectory(root);
            var clipsRoot = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
            var captureLibrary = Directory.CreateDirectory(
                Path.Combine(root, "capture-library")).FullName;
            var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(captureLibrary);
            var stateRoot = Directory.CreateDirectory(Path.Combine(root, "routing-state")).FullName;
            var state = new WatchState
            {
                Version = 4,
                ClipsFolder = clipsRoot,
                CaptureSource = ClipCaptureSource.SteelSeriesGg,
                KnownContentHashes = new HashSet<string>(
                    knownHashes ?? [],
                    StringComparer.OrdinalIgnoreCase),
                UploadedContentHashes = new HashSet<string>(
                    uploadedHashes ?? [],
                    StringComparer.OrdinalIgnoreCase),
                LocalOnlyContentHashes = new HashSet<string>(
                    localOnlyHashes ?? [],
                    StringComparer.OrdinalIgnoreCase)
            };
            var stateStore = new WatchStateStore(
                Path.Combine(stateRoot, "legacy-state", "state.json"),
                Path.Combine(stateRoot, "legacy-state", ".safe-baseline-required"));
            stateStore.Save(state);
            var settings = new AppSettings(
                clipsRoot,
                fresh
                    ? string.Empty
                    : "https://discord.com/api/webhooks/123456789012345678/test-token",
                StartWithWindows: false,
                AppSettings.DefaultCompressionTargetMb,
                "Ingress Test",
                UploadToDiscord: !fresh,
                ModeToggleHotkey: string.Empty,
                ClipCaptureSource.SteelSeriesGg);
            IReadOnlyList<string> connectionIds = fresh ? [] : [LegacyConnectionId];
            var readiness = fresh
                ? FreshRoutingSetupPlanner.Evaluate(
                    new FreshRoutingSetupInput(
                        settings,
                        state,
                        LegacyWorkerQuiesced: true,
                        new RoutingRouteDraft(
                            "My first route",
                            RoutingTriggerKind.AnyNewSourceClip,
                            Game: null,
                            Destination: null,
                            ConnectionId: null,
                            RoutingOutputKind.Original,
                            RoutingDeliveryMode.Automatic,
                            RoutingMissingOutputBehavior.UseOriginal,
                            FileIntoLibrary: true),
                        captureLibraryBinding,
                        RoutingWatchedSourceAdapters.Get(settings.CaptureSource)
                            .InspectRootIdentity(settings.ClipsFolder),
                        LegacyRoutingMigrationAdmission.FreshOrInvalidProfile),
                    Now)
                : LegacyRoutingMigrationPlanner.Evaluate(
                    new LegacyRoutingMigrationInput(
                        settings,
                        state,
                        LegacyWorkerQuiesced: true,
                        connectionIds,
                        captureLibraryBinding,
                        LegacyRoutingMigrationAdmission.ValidLegacyUpgrade),
                    Now);
            var migrationPlan = readiness.Plan ?? throw new InvalidOperationException(
                $"The watched-ingress migration fixture is unavailable ({readiness.Status}).");
            var customRoute = CreateCustomRoute();
            var snapshot = new RoutingSnapshotDocument(
                RoutingSnapshotStore.CurrentSchemaVersion,
                Generation: 1,
                Routes: fresh ? [migrationPlan.Route] : [customRoute, migrationPlan.Route],
                CreatedUtc: Now,
                UpdatedUtc: Now);
            var snapshots = new RoutingSnapshotStore(
                Path.Combine(stateRoot, RoutingSnapshotStore.FileName));
            _ = await snapshots.SaveAsync(snapshot, expectedGeneration: 0);

            var markers = new LegacyRoutingMigrationMarkerStore(
                Path.Combine(stateRoot, LegacyRoutingMigrationMarkerStore.FileName));
            var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(
                migrationPlan,
                Now);
            _ = await markers.SaveAsync(prepared, expectedGeneration: 0);
            var marker = await markers.SaveAsync(
                LegacyRoutingMigrationMarkerModel.Commit(prepared, Now.AddSeconds(1)),
                prepared.Generation);
            var authorityStore = new RoutingExecutionAuthorityStore(
                Path.Combine(stateRoot, RoutingExecutionAuthorityStore.FileName));
            _ = await authorityStore.CommitAsync(
                RoutingExecutionAuthorityModel.Create(
                    marker,
                    ClipCaptureSource.SteelSeriesGg,
                    Now.AddSeconds(2)));
            var evidence = new LegacyRoutingActivationEvidenceSource(
                stateStore,
                () => settings,
                () => connectionIds,
                () => captureLibraryBinding,
                () => fresh
                    ? LegacyRoutingMigrationAdmission.FreshOrInvalidProfile
                    : LegacyRoutingMigrationAdmission.ValidLegacyUpgrade);
            var freshEvidence = fresh
                ? new FreshRoutingActivationEvidenceSource(
                    stateStore,
                    () => settings,
                    () => captureLibraryBinding)
                : null;
            var ownership = new ClipProcessingOwnershipCoordinator();
            if (!ownership.TryAcquire(
                    ClipProcessingRuntimeOwner.Routing,
                    out var routingLease) || routingLease is null)
            {
                throw new InvalidOperationException(
                    "The watched-ingress fixture could not acquire Routing ownership.");
            }
            var gate = RoutingRuntimeFeatureGate.Evaluate(
                requestedEnabled: true,
                markers,
                snapshots,
                evidence,
                routingLease,
                RoutingWatchedSourceAdapters.CoveredSources,
                authorityStore,
                freshEvidence);
            Assert(gate.Enabled,
                "The watched-ingress fixture must begin with complete cutover authority.");

            var journals = new RoutingWatchedSourceJournalStore(
                Path.Combine(stateRoot, "watched-journal", "v1"));
            var outbox = new RoutingOutboxStore(
                Path.Combine(stateRoot, RoutingOutboxStore.FileName));
            var journalFactory = new RoutingWatchedJournalFactory(
                probe ?? new RecordingProbe(),
                createPlanId: Guid.NewGuid,
                utcNow: () => Now.AddMinutes(1));
            var ingress = new RoutingWatchedFolderIngress(
                snapshots,
                markers,
                journals,
                journalFactory,
                new RoutingPlanCommitter(outbox, gate),
                gate);
            return new Fixture(
                clipsRoot,
                snapshot,
                customRoute,
                snapshots,
                marker,
                markers,
                journals,
                outbox,
                gate,
                journalFactory,
                ingress,
                routingLease);
        }

        internal async Task<RoutingWatchedSourceFile> CreateSourceAsync(
            string fileName,
            string contentSha256,
            long fileIndex)
        {
            var path = Path.Combine(ClipsRoot, fileName);
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            return new RoutingWatchedSourceFile(
                ClipCaptureSource.SteelSeriesGg,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(ClipsRoot)),
                fileName,
                "Ingress Game",
                fileName,
                new string('d', 64),
                new RoutingWatchedNativeFileIdentity(
                    VolumeSerialNumber: 0x12345678,
                    FileIdHex: fileIndex.ToString("x16",
                        System.Globalization.CultureInfo.InvariantCulture),
                    ByteLength: 4,
                    CreationUtcTicks: Now.UtcTicks,
                    LastWriteUtcTicks: Now.UtcTicks),
                contentSha256.ToLowerInvariant());
        }

        internal void RevokeGate() => _routingLease.Dispose();

        public void Dispose()
        {
            _routingLease.Dispose();
        }

        private static RoutingRoute CreateCustomRoute()
        {
            var delivery = new RoutingAction(
                Guid.Parse("00000000-0000-0000-0000-000000000101"),
                Enabled: true,
                RoutingActionKind.Deliver,
                RoutingDestinationKind.Discord,
                OriginalConnectionId,
                RoutingOutputKind.Original,
                RoutingMissingOutputBehavior.UseOriginal,
                RoutingDeliveryMode.Automatic,
                LibraryArea: null,
                new RoutingDeliverySettings(
                    Message: null,
                    Title: "Original frozen route",
                    Caption: null,
                    RoutingVisibility.Unspecified,
                    NotifyFollowers: false));
            var file = new RoutingAction(
                Guid.Parse("00000000-0000-0000-0000-000000000102"),
                Enabled: true,
                RoutingActionKind.FileIntoLibrary,
                Destination: null,
                ConnectionId: null,
                OutputRef: null,
                OnMissingOutput: null,
                RoutingDeliveryMode.Automatic,
                RoutingLibraryArea.Uploaded,
                DeliverySettings: null);
            return new RoutingRoute(
                Guid.Parse("00000000-0000-0000-0000-000000000100"),
                "Ingress Game uploads",
                Enabled: true,
                Priority: 1,
                Revision: 1,
                RoutingRouteSource.User,
                RoutingRouteKind.Specific,
                RoutingTriggerKind.AnyNewSourceClip,
                new RoutingPrepareSettings(
                    Landscape: false,
                    Portrait: false,
                    RoutingMissingOutputBehavior.UseOriginal),
                Conditions:
                [
                    new RoutingCondition(
                        Guid.Parse("00000000-0000-0000-0000-000000000103"),
                        RoutingConditionField.Game,
                        RoutingConditionOperator.Equals,
                        "Ingress Game")
                ],
                Actions: [delivery, file],
                CreatedUtc: Now,
                ModifiedUtc: Now);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
