using ClipsToDiscord;

internal static class RoutingRuntimeBridgeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 21, 0, 0, TimeSpan.Zero);

    internal static void Run(string testRoot) => RunAsync(testRoot).GetAwaiter().GetResult();

    private static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "routing-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AssertOwnershipLeaseIsExclusive();
            await AssertActiveLegacyControllerCannotBeRevokedAsync(
                Path.Combine(root, "active-legacy-owner"));
            AssertCutoverGateFailsClosed(Path.Combine(root, "gate"));
            AssertDiscordConnectionEvidenceIsFresh(Path.Combine(root, "connection-evidence"));
            await AssertDefaultBridgeIsInertAsync(Path.Combine(root, "disabled"));
            await AssertRouteSnapshotLossFailsClosedAsync(Path.Combine(root, "snapshot-loss"));
            await AssertPlanIsFrozenOnceAndSurvivesRestartAsync(Path.Combine(root, "restart"));
            await AssertArchivedPlanPreventsReplanningAsync(Path.Combine(root, "archived-restart"));
            await AssertCapacityAttentionIsTypedAsync(Path.Combine(root, "capacity-attention"));
            await AssertEveryJournalStatePlansWithStableOutputsAsync(Path.Combine(root, "states"));
            await AssertReconciliationSkipsUnreadableBeforeValidAsync(Path.Combine(root, "reconcile-skip"));
            await AssertGateRevocationDuringPlanningPreventsAppendAsync(Path.Combine(root, "gate-revoke"));
            await AssertUnrequestedFailedOutputUsesNotProducedAsync(Path.Combine(root, "unrequested-failure"));
            await AssertNonSourceEventsCannotCreatePlansAsync(Path.Combine(root, "non-source"));
            await AssertInvalidMediaAndPlannerLiesAreRejectedAsync(Path.Combine(root, "validation"));
            await AssertConcurrentSourceArrivalsAppendOnePlanAsync(Path.Combine(root, "concurrent"));
            await AssertExistingPlanReconcilesReadyArtifactsAtomicallyAsync(
                Path.Combine(root, "artifact-ready"));
            await AssertExistingPlanReconcilesFailedArtifactsIdempotentlyAsync(
                Path.Combine(root, "artifact-failed"));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static void AssertCutoverGateFailsClosed(string root)
    {
        Directory.CreateDirectory(root);
        var drained = CreateLegacyState(root);
        var stateStore = CreateLegacyStateStore(root, drained);
        var currentSettings = CreateLegacySettings(root);
        IReadOnlyList<string> connectionIds = [];
        var evidence = new LegacyRoutingActivationEvidenceSource(
            stateStore,
            () => currentSettings,
            () => connectionIds);
        var migrationPlan = CreateMigrationPlan(currentSettings, drained, connectionIds);
        var migrationRoute = migrationPlan.Route;
        var snapshotStore = CreateSnapshotStore(root, migrationRoute);
        var marker = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(root, LegacyRoutingMigrationMarkerStore.FileName));
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var routingLease) &&
               routingLease is not null,
            "The gate fixture must acquire routing ownership.");
        using var owned = routingLease ?? throw new InvalidOperationException(
            "The gate fixture routing lease is missing.");

        Assert(!RoutingRuntimeFeatureGate.Disabled.Enabled &&
               RoutingRuntimeFeatureGate.Disabled.State ==
               RoutingRuntimeGateState.DisabledByDefault,
            "The routing runtime feature gate must be disabled by default.");
        Assert(RoutingRuntimeFeatureGate.Evaluate(
                   false, marker, snapshotStore, evidence, owned).State ==
               RoutingRuntimeGateState.DisabledByDefault,
            "A committed marker must not override the explicit disabled default.");
        Assert(RoutingRuntimeFeatureGate.Evaluate(
                   true, marker, snapshotStore, evidence, owned).State ==
               RoutingRuntimeGateState.MigrationMarkerMissing,
            "A requested cutover without a committed marker must fail closed.");

        File.WriteAllText(marker.Path, "status=committed");
        Assert(RoutingRuntimeFeatureGate.Evaluate(
                   true, marker, snapshotStore, evidence, owned).State ==
               RoutingRuntimeGateState.MigrationMarkerNotCommitted,
            "A lookalike or old migration marker must not activate routing.");

        File.Delete(marker.Path);
        marker = CreateMarkerStore(root, migrationPlan, commit: false);
        Assert(RoutingRuntimeFeatureGate.Evaluate(
                   true, marker, snapshotStore, evidence, owned).State ==
               RoutingRuntimeGateState.MigrationMarkerNotCommitted,
            "A valid but merely prepared migration marker must not activate routing.");
        var prepared = marker.Load().Document!;
        _ = marker.SaveAsync(
            LegacyRoutingMigrationMarkerModel.Commit(prepared, Now.AddSeconds(1)),
            prepared.Generation).GetAwaiter().GetResult();
        var pending = CreateLegacyState(root);
        pending.PendingMoves.Add(Path.Combine(root, "pending-uploaded.mp4"));
        pending.PendingLocalOnlyMoves.Add(Path.Combine(root, "pending-local.mp4"));
        pending.PendingEditedUploads.Add(new PendingEditedClipDisposition
        {
            Id = Guid.NewGuid()
        });
        stateStore.Save(pending);
        var blocked = RoutingRuntimeFeatureGate.Evaluate(
            true, marker, snapshotStore, evidence, owned);
        Assert(blocked.State == RoutingRuntimeGateState.LegacyQueuesPending &&
               blocked.PendingLegacyMoves == 1 &&
               blocked.PendingLegacyLocalOnlyMoves == 1 &&
               blocked.PendingLegacyEditedUploads == 1,
            "Every legacy WatchState pending queue must drain before routing can activate.");

        File.WriteAllText(stateStore.StatePath, "{\"version\":4}");
        Assert(RoutingRuntimeFeatureGate.Evaluate(
                   true, marker, snapshotStore, evidence, owned).State ==
               RoutingRuntimeGateState.LegacyStateUnavailable,
            "Missing legacy queue state must fail closed rather than being treated as empty.");

        stateStore.Save(drained);
        var enabled = RoutingRuntimeFeatureGate.Evaluate(
            true, marker, snapshotStore, evidence, owned);
        Assert(enabled.Enabled,
            "Only an exact committed marker plus fully drained legacy queues may enable the gate.");
        drained.KnownContentHashes.Add(new string('B', 64));
        stateStore.Save(drained);
        Assert(enabled.State == RoutingRuntimeGateState.MigrationEvidenceMismatch,
            "Changing durable legacy exclusions after cutover must invalidate the marker fingerprint.");
        drained.KnownContentHashes.Clear();
        stateStore.Save(drained);
        currentSettings = currentSettings with { CaptureSource = ClipCaptureSource.Nvidia };
        Assert(enabled.State == RoutingRuntimeGateState.MigrationEvidenceMismatch,
            "Changing the configured legacy capture source must invalidate activation evidence.");
        currentSettings = CreateLegacySettings(root);
        Assert(enabled.Enabled,
            "Restoring exact settings and exclusions must restore the same committed evidence.");
        drained.PendingMoves.Add(Path.Combine(root, "late-legacy-work.mp4"));
        stateStore.Save(drained);
        Assert(enabled.State == RoutingRuntimeGateState.LegacyQueuesPending,
            "A gate must reload disk and revoke activation if legacy pending work appears later.");
        drained.PendingMoves.Clear();
        drained.IgnoredFileKeys.Add("legacy-baseline-key");
        stateStore.Save(drained);
        Assert(enabled.State == RoutingRuntimeGateState.LegacyQueuesPending &&
               enabled.IgnoredLegacyFileKeys == 1,
            "A live ignored-file baseline must revoke routing activation.");
        drained.IgnoredFileKeys.Clear();
        stateStore.Save(drained);
        File.Delete(marker.Path);
        Assert(enabled.State == RoutingRuntimeGateState.MigrationMarkerMissing,
            "A gate must revoke activation if its committed migration marker disappears.");

        owned.Dispose();
        Assert(enabled.State == RoutingRuntimeGateState.OwnershipUnavailable,
            "A released routing lease must revoke activation immediately.");
    }

    private static void AssertOwnershipLeaseIsExclusive()
    {
        var coordinator = new ClipProcessingOwnershipCoordinator();
        Assert(coordinator.TryAcquire(ClipProcessingRuntimeOwner.Legacy, out var legacy) &&
               legacy is { IsCurrent: true },
            "The first legacy runtime must acquire clip-processing ownership.");
        var legacyLease = legacy ?? throw new InvalidOperationException(
            "The legacy ownership lease is missing.");
        Assert(!coordinator.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var blocked) &&
               blocked is null,
            "Routing must not acquire ownership while the legacy runtime owns it.");
        Assert(coordinator.TryTransfer(
                   legacyLease, ClipProcessingRuntimeOwner.Transition, out var transition) &&
               transition is { IsCurrent: true } && !legacyLease.IsCurrent,
            "A cutover reservation must atomically invalidate the legacy lease.");
        legacyLease.Dispose();
        var transitionLease = transition ?? throw new InvalidOperationException(
            "The transition ownership lease is missing.");
        Assert(transitionLease.IsCurrent,
            "Disposing a stale legacy lease must not release the transition owner.");
        Assert(coordinator.TryTransfer(
                   transitionLease, ClipProcessingRuntimeOwner.Routing, out var routing) &&
               routing is { IsCurrent: true } && !transitionLease.IsCurrent,
            "A completed cutover must atomically transfer authority to routing.");
        var routingLease = routing ?? throw new InvalidOperationException(
            "The routing ownership lease is missing.");
        routingLease.Dispose();
        ClipProcessingOwnershipLease? restarted = null;
        Assert(coordinator.Owner is null && coordinator.TryAcquire(
                   ClipProcessingRuntimeOwner.Legacy, out restarted),
            "Releasing routing must permit exactly one later owner.");
        (restarted ?? throw new InvalidOperationException(
            "The restarted legacy ownership lease is missing.")).Dispose();

        var concurrent = new ClipProcessingOwnershipCoordinator();
        var winners = new System.Collections.Concurrent.ConcurrentBag<ClipProcessingOwnershipLease>();
        Parallel.For(0, 32, _ =>
        {
            if (concurrent.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease))
            {
                winners.Add(lease!);
            }
        });
        Assert(winners.Count == 1,
            "Concurrent runtime starts must produce exactly one ownership winner.");
        foreach (var winner in winners) winner.Dispose();

        var left = new ClipProcessingOwnershipCoordinator();
        var right = new ClipProcessingOwnershipCoordinator();
        var leftAcquired = left.TryAcquire(
            ClipProcessingRuntimeOwner.Legacy, out var leftLeaseCandidate);
        var rightAcquired = right.TryAcquire(
            ClipProcessingRuntimeOwner.Legacy, out var rightLeaseCandidate);
        Assert(leftAcquired && rightAcquired &&
               leftLeaseCandidate is not null && rightLeaseCandidate is not null,
            "Independent ownership coordinators must create their own leases.");
        var leftLease = leftLeaseCandidate ?? throw new InvalidOperationException(
            "The left ownership lease is missing.");
        var rightLease = rightLeaseCandidate ?? throw new InvalidOperationException(
            "The right ownership lease is missing.");
        Assert(!left.TryTransfer(
                   rightLease, ClipProcessingRuntimeOwner.Routing, out var foreignTransfer) &&
               foreignTransfer is null && leftLease.IsCurrent && rightLease.IsCurrent,
            "A coincident epoch from another coordinator must never authorize a transfer.");
        leftLease.Dispose();
        rightLease.Dispose();
    }

    private static async Task AssertActiveLegacyControllerCannotBeRevokedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var state = CreateLegacyState(root);
        var stateStore = CreateLegacyStateStore(root, state);
        var settings = CreateLegacySettings(root);
        IReadOnlyList<string> connectionIds = [];
        var migrationPlan = CreateMigrationPlan(settings, state, connectionIds);
        var marker = CreateMarkerStore(root, migrationPlan, commit: true);
        var snapshotStore = CreateSnapshotStore(root, migrationPlan.Route);
        var evidence = new LegacyRoutingActivationEvidenceSource(
            stateStore,
            () => settings,
            () => connectionIds);
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(
                   ClipProcessingRuntimeOwner.Legacy,
                   out var callerLeaseCandidate) && callerLeaseCandidate is not null,
            "The active-watcher fixture must acquire legacy ownership.");
        var callerLease = callerLeaseCandidate ?? throw new InvalidOperationException(
            "The active-watcher fixture legacy lease is missing.");

        var watcherStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        async Task BlockingWatcher(
            AppSettings ignoredSettings,
            Action<string> ignoredStatus,
            CancellationToken cancellationToken)
        {
            watcherStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
                throw;
            }
        }

        var options = new DiscordControllerOptions(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            AbsentPollThreshold: 2,
            DisposeWaitTimeout: TimeSpan.FromSeconds(1));
        var controller = new DiscordAwareController(
            AppSettings.Empty,
            _ => { },
            () => true,
            BlockingWatcher,
            options,
            callerLease);
        Task? stopTask = null;
        try
        {
            await watcherStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!callerLease.IsCurrent &&
                   ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "The controller must atomically replace the caller's legacy handle with its own private lease.");

            callerLease.Dispose();
            Assert(!ownership.TryTransfer(
                       callerLease,
                       ClipProcessingRuntimeOwner.Transition,
                       out var stolenTransition) && stolenTransition is null &&
                   !ownership.TryAcquire(
                       ClipProcessingRuntimeOwner.Routing,
                       out var overlappingRouting) && overlappingRouting is null,
                "A stale caller handle must not revoke or transfer ownership while its watcher is active.");
            var blockedGate = RoutingRuntimeFeatureGate.Evaluate(
                requestedEnabled: true,
                marker,
                snapshotStore,
                evidence,
                callerLease);
            Assert(blockedGate.State == RoutingRuntimeGateState.OwnershipUnavailable,
                "Direct caller-handle revocation must not enable routing while the legacy watcher continues.");

            stopTask = controller.StopAsync();
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(!stopTask.IsCompleted &&
                   !ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out _),
                "Routing ownership must remain unavailable throughout legacy watcher cleanup.");
            releaseCleanup.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert(ownership.TryAcquire(
                       ClipProcessingRuntimeOwner.Routing,
                       out var routingLeaseCandidate) && routingLeaseCandidate is not null,
                "Routing may acquire ownership only after the legacy watcher fully stops.");
            using var routingLease = routingLeaseCandidate ?? throw new InvalidOperationException(
                "The post-shutdown routing lease is missing.");
            var enabledGate = RoutingRuntimeFeatureGate.Evaluate(
                requestedEnabled: true,
                marker,
                snapshotStore,
                evidence,
                routingLease);
            Assert(enabledGate.Enabled,
                "Exact cutover evidence may enable routing after legacy shutdown releases ownership.");
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (stopTask is null)
            {
                stopTask = controller.StopAsync();
            }
            try { await stopTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
            controller.Dispose();
            callerLease.Dispose();
        }
    }

    private static void AssertDiscordConnectionEvidenceIsFresh(string root)
    {
        Directory.CreateDirectory(root);
        var state = CreateLegacyState(root);
        var stateStore = CreateLegacyStateStore(root, state);
        var settings = CreateLegacySettings(root, uploadToDiscord: true);
        IReadOnlyList<string> connectionIds = ["discord.connection.original"];
        var plan = CreateMigrationPlan(settings, state, connectionIds);
        var routes = CreateSnapshotStore(root, plan.Route);
        var markers = CreateMarkerStore(root, plan, commit: true);
        var evidence = new LegacyRoutingActivationEvidenceSource(
            stateStore,
            () => settings,
            () => connectionIds);
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease) &&
               lease is not null,
            "The Discord evidence fixture must acquire routing ownership.");
        using var owned = lease ?? throw new InvalidOperationException(
            "The Discord evidence fixture routing lease is missing.");
        var gate = RoutingRuntimeFeatureGate.Evaluate(
            true,
            markers,
            routes,
            evidence,
            owned);
        Assert(gate.Enabled,
            "Exact current Discord connection evidence must authorize its committed marker.");
        connectionIds = ["discord.connection.changed"];
        Assert(gate.State == RoutingRuntimeGateState.MigrationEvidenceMismatch,
            "Changing the opaque Discord connection id must invalidate the committed source fingerprint.");
    }

    private static async Task AssertReconciliationSkipsUnreadableBeforeValidAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: false);
        Assert(fixture.Item.CanPlanDeliveries,
            "Every validated routable fixture state must advertise CanPlanDeliveries.");
        var planner = new RecordingPlanner();
        var bridge = fixture.CreateBridge(planner);
        var unreadable = new CaptureJournalReconciliationItem(
            "corrupt-before-valid",
            CaptureJournalLoadStatus.Corrupt,
            null,
            new Dictionary<string, CaptureJournalArtifactValidationStatus>());
        await bridge.ReconcileAsync(unreadable, CancellationToken.None);
        await bridge.ReconcileAsync(fixture.Item, CancellationToken.None);
        Assert(planner.Calls == 1 && fixture.OutboxStore.Load().Document!.Plans.Count == 1,
            "An unreadable journal must be ignored by reconciliation without starving the next valid source.");
    }

    private static async Task AssertDefaultBridgeIsInertAsync(string root)
    {
        Directory.CreateDirectory(root);
        var planner = new ThrowingPlanner();
        var snapshotPath = Path.Combine(root, "state", "routes.json");
        var outboxPath = Path.Combine(root, "state", "outbox.json");
        var bridge = new RoutingRuntimeBridge(
            root,
            new RoutingSnapshotStore(snapshotPath),
            new RoutingOutboxStore(outboxPath),
            planner,
            utcNow: () => Now);
        var result = await bridge.PlanAsync(new RoutingRuntimeSourceEvent(
            RoutingRuntimeSourceEventKind.SourceClipCommitted,
            JournalItem: null));
        Assert(result.Status == RoutingRuntimePlanStatus.Disabled &&
               planner.Calls == 0 &&
               !File.Exists(snapshotPath) && !File.Exists(outboxPath),
            "The default bridge must return before validation, planning, or persistence.");
    }

    private static async Task AssertRouteSnapshotLossFailsClosedAsync(string root)
    {
        var missing = await CreateFixtureAsync(Path.Combine(root, "missing"), reactionCamera: false);
        File.Delete(missing.SnapshotStore.Path);
        var missingPlanner = new RecordingPlanner();
        var missingResult = await missing.CreateBridge(missingPlanner)
            .PlanAsync(SourceEvent(missing.Item));
        Assert(missingResult.Status == RoutingRuntimePlanStatus.Disabled &&
               missingPlanner.Calls == 0 &&
               !File.Exists(missing.SnapshotStore.Path) &&
               !File.Exists(missing.OutboxStore.Path),
            "A missing committed route snapshot must pause routing without creating an empty snapshot or outbox.");

        var corrupt = await CreateFixtureAsync(Path.Combine(root, "corrupt"), reactionCamera: false);
        File.WriteAllText(corrupt.SnapshotStore.Path, "not-json");
        var corruptPlanner = new RecordingPlanner();
        var corruptResult = await corrupt.CreateBridge(corruptPlanner)
            .PlanAsync(SourceEvent(corrupt.Item));
        Assert(corruptResult.Status == RoutingRuntimePlanStatus.Disabled &&
               corruptPlanner.Calls == 0 &&
               corrupt.Gate.State == RoutingRuntimeGateState.RoutingSnapshotUnavailable &&
               !File.Exists(corrupt.OutboxStore.Path),
            "A corrupt route snapshot must pause before planning or outbox initialization.");

        var mismatch = await CreateFixtureAsync(Path.Combine(root, "mismatch"), reactionCamera: false);
        var migration = mismatch.Snapshot.Routes.Single(route =>
            route.Source == RoutingRouteSource.Migration);
        var changedMigration = migration with
        {
            Name = "Changed migration fallback",
            Revision = checked(migration.Revision + 1),
            ModifiedUtc = Now.AddMinutes(1)
        };
        var mismatchedSnapshot = RoutingSnapshotModel.ReplaceRoutes(
            mismatch.Snapshot,
            mismatch.Snapshot.Routes.Select(route =>
                route.RouteId == migration.RouteId ? changedMigration : route).ToArray(),
            Now.AddMinutes(1));
        await mismatch.SnapshotStore.SaveAsync(
            mismatchedSnapshot,
            mismatch.Snapshot.Generation);
        Assert(mismatch.Gate.State == RoutingRuntimeGateState.MigrationRouteMismatch,
            "A committed marker must not authorize a changed migration route.");

        var edited = await CreateFixtureAsync(Path.Combine(root, "edited"), reactionCamera: false);
        var user = edited.Snapshot.Routes.Single(route => route.Source == RoutingRouteSource.User);
        var next = RoutingSnapshotModel.ReplaceRoutes(
            edited.Snapshot,
            edited.Snapshot.Routes.Select(route => route.RouteId == user.RouteId
                ? route with
                {
                    Name = "Edited while planning",
                    Revision = checked(route.Revision + 1),
                    ModifiedUtc = Now.AddMinutes(1)
                }
                : route).ToArray(),
            Now.AddMinutes(1));
        var editPlanner = new MutatingPlanner(() =>
            edited.SnapshotStore.SaveAsync(next, edited.Snapshot.Generation)
                .GetAwaiter().GetResult());
        var editedResult = await edited.CreateBridge(editPlanner)
            .PlanAsync(SourceEvent(edited.Item));
        var editedOutbox = edited.OutboxStore.Load();
        Assert(editedResult.Status == RoutingRuntimePlanStatus.Disabled &&
               editPlanner.Calls == 1 && editedOutbox.LoadedFromDisk &&
               editedOutbox.Document!.Plans.Count == 0,
            "A route-generation change during evaluation must invalidate the planning permit before CAS.");

        var deleted = await CreateFixtureAsync(Path.Combine(root, "deleted"), reactionCamera: false);
        var deletePlanner = new MutatingPlanner(() => File.Delete(deleted.SnapshotStore.Path));
        var deletedResult = await deleted.CreateBridge(deletePlanner)
            .PlanAsync(SourceEvent(deleted.Item));
        Assert(deletedResult.Status == RoutingRuntimePlanStatus.Disabled &&
               deletePlanner.Calls == 1 &&
               deleted.OutboxStore.Load().Document!.Plans.Count == 0,
            "Route loss after evaluation starts must still block the outbox append.");
    }

    private static async Task AssertPlanIsFrozenOnceAndSurvivesRestartAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: true);
        var planner = new RecordingPlanner();
        var bridge = fixture.CreateBridge(planner);
        var first = await bridge.PlanAsync(SourceEvent(fixture.Item));
        Assert(first.Status == RoutingRuntimePlanStatus.Planned && planner.Calls == 1,
            "The first validated source arrival must append one durable plan.");

        var persisted = fixture.OutboxStore.Load();
        Assert(persisted.LoadedFromDisk && persisted.Document is not null &&
               persisted.Document.Deliveries.Count == 1 &&
               persisted.Document.FileDispositions.Count == 1,
            "The bridge must atomically append the complete delivery and disposition plan.");
        var delivery = persisted.Document!.Deliveries.Single();
        Assert(delivery.SourceClipId == fixture.Item.ClipId &&
               delivery.Route.RoutingGeneration == fixture.Snapshot.Generation &&
               delivery.State == PlannedDeliveryState.WaitingForArtifact &&
               delivery.RequestedOutput.Kind == RoutingOutputKind.Landscape &&
               delivery.RequestedOutput.Revision.Length == 64 &&
               !delivery.RequestedOutput.Revision.Equals(
                   fixture.Item.Document!.Clip.Original.Fingerprint.Sha256,
                   StringComparison.OrdinalIgnoreCase),
            "The frozen plan must pin the route generation and retain a logical pending rendition.");

        var restartPlanner = new RecordingPlanner();
        var restarted = fixture.CreateBridge(restartPlanner);
        var second = await restarted.PlanAsync(new RoutingRuntimeSourceEvent(
            RoutingRuntimeSourceEventKind.CaptureJournalReconciled,
            fixture.Item));
        Assert(second.Status == RoutingRuntimePlanStatus.AlreadyPlanned &&
               second.PlanId == first.PlanId && restartPlanner.Calls == 0,
            "A restart must recognize SourceClipId before invoking the evaluator again.");

        var route = fixture.Snapshot.Routes.Single(candidate =>
            candidate.Source == RoutingRouteSource.User);
        var migrationRoute = fixture.Snapshot.Routes.Single(candidate =>
            candidate.Source == RoutingRouteSource.Migration);
        var newerSnapshot = RoutingSnapshotModel.ReplaceRoutes(
            fixture.Snapshot,
            [route with
            {
                Revision = 2,
                Name = "Changed after source planning",
                ModifiedUtc = Now.AddMinutes(1)
            }, migrationRoute],
            Now.AddMinutes(1));
        await fixture.SnapshotStore.SaveAsync(
            newerSnapshot,
            fixture.Snapshot.Generation);
        var afterRouteEditPlanner = new RecordingPlanner();
        var afterRouteEdit = await fixture.CreateBridge(afterRouteEditPlanner)
            .PlanAsync(SourceEvent(fixture.Item));
        Assert(afterRouteEdit.Status == RoutingRuntimePlanStatus.AlreadyPlanned &&
               afterRouteEditPlanner.Calls == 0 &&
               fixture.OutboxStore.Load().Document!.Deliveries.Single().Route.RoutingGeneration ==
               fixture.Snapshot.Generation,
            "Editing routes after planning must not re-resolve or rewrite an existing source plan.");
    }

    private static async Task AssertArchivedPlanPreventsReplanningAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: false);
        var empty = await fixture.OutboxStore.LoadOrCreateAsync(Now);
        var archivedPlanId = Guid.NewGuid();
        var proposal = new RoutingPlanProposal(
            archivedPlanId,
            fixture.Item.ClipId,
            fixture.Snapshot.Generation,
            [], [], null, [], [],
            RequiresAtomicResolvedAppend: false);
        var hot = RoutingOutboxModel.AppendEvaluatedPlan(
            empty, proposal, Now.AddSeconds(1));
        hot = await fixture.OutboxStore.SaveAsync(hot, empty.Generation);
        var archive = RoutingArchiveModel.Create(hot, archivedPlanId);
        await fixture.OutboxStore.ArchiveStore.PersistExactAsync(archive);

        var overlapPlanner = new RecordingPlanner();
        var overlap = await fixture.CreateBridge(overlapPlanner).PlanAsync(SourceEvent(fixture.Item));
        Assert(overlap.Status == RoutingRuntimePlanStatus.AlreadyPlanned &&
               overlap.PlanId == archivedPlanId && overlapPlanner.Calls == 0,
            "An archive-first crash overlap must still reuse the exact hot plan without re-evaluation.");

        var compacted = await fixture.OutboxStore.CompactTerminalPlansAsync(Now.AddSeconds(2));
        Assert(compacted.ArchivedPlanCount == 1 && compacted.Document.Plans.Count == 0,
            "A terminal no-op plan must compact after its immutable archive is durable.");
        var archiveOnlyPlanner = new RecordingPlanner();
        var archiveOnly = await fixture.CreateBridge(archiveOnlyPlanner)
            .PlanAsync(SourceEvent(fixture.Item));
        Assert(archiveOnly.Status == RoutingRuntimePlanStatus.AlreadyArchived &&
               archiveOnly.PlanId == archivedPlanId && archiveOnlyPlanner.Calls == 0 &&
               fixture.OutboxStore.Load().Document!.Plans.Count == 0,
            "An archive-only source tombstone must permanently prevent replanning and reupload.");

        File.WriteAllText(
            fixture.OutboxStore.ArchiveStore.PathForSource(fixture.Item.ClipId),
            "{ corrupt archive");
        var corruptPlanner = new RecordingPlanner();
        await AssertThrowsAsync<InvalidDataException>(
            () => fixture.CreateBridge(corruptPlanner).PlanAsync(SourceEvent(fixture.Item)),
            "A corrupt source tombstone must fail closed instead of treating the clip as unplanned.");
        Assert(corruptPlanner.Calls == 0 &&
               fixture.OutboxStore.Load().Document!.Plans.Count == 0,
            "A corrupt archive must prevent both planner execution and replacement outbox work.");
    }

    private static async Task AssertCapacityAttentionIsTypedAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: false);
        fixture = fixture with
        {
            OutboxStore = new RoutingOutboxStore(
                fixture.OutboxStore.Path, planningAdmissionBytes: 1024)
        };
        var planner = new RecordingPlanner();
        var result = await fixture.CreateBridge(planner).PlanAsync(SourceEvent(fixture.Item));
        Assert(result.Status == RoutingRuntimePlanStatus.CapacityNeedsAttention &&
               planner.Calls == 1 &&
               fixture.OutboxStore.Load().Document!.Plans.Count == 0,
            "A proposal that cannot fit below the admission ceiling must return typed capacity " +
            "attention without appending partial work.");
    }

    private static async Task AssertNonSourceEventsCannotCreatePlansAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: false);
        var planner = new RecordingPlanner();
        var bridge = fixture.CreateBridge(planner);
        foreach (var eventKind in new[]
                 {
                     RoutingRuntimeSourceEventKind.DerivedArtifactCommitted,
                     RoutingRuntimeSourceEventKind.FileArchived
                 })
        {
            var result = await bridge.PlanAsync(new RoutingRuntimeSourceEvent(
                eventKind,
                fixture.Item));
            Assert(result.Status == RoutingRuntimePlanStatus.IgnoredNonSourceEvent,
                "A derived artifact or archive event must be rejected as a new routing source.");
        }
        Assert(planner.Calls == 0 && !File.Exists(fixture.OutboxStore.Path),
            "Rejected feedback events must not evaluate routes or create outbox state.");
    }

    private static async Task AssertEveryJournalStatePlansWithStableOutputsAsync(string root)
    {
        foreach (var state in new[]
                 {
                     CaptureJournalState.OriginalCommitted,
                     CaptureJournalState.CameraPending,
                     CaptureJournalState.RenditionsReady,
                     CaptureJournalState.RenditionsFailed
                 })
        {
            var fixture = await CreateFixtureAsync(
                Path.Combine(root, state.ToString()),
                reactionCamera: true,
                state);
            Assert(fixture.Item.CanPlanDeliveries,
                "All four valid capture journal states must remain eligible for route planning.");
            var result = await fixture.CreateBridge(new RecordingPlanner())
                .PlanAsync(SourceEvent(fixture.Item));
            Assert(result.Status == RoutingRuntimePlanStatus.Planned,
                $"A validated {state} journal must be accepted as a startup source.");
            var delivery = fixture.OutboxStore.Load().Document!.Deliveries.Single();
            var expected = RoutingEvaluator.CreateLogicalOutputReference(
                fixture.Item.ClipId,
                fixture.Item.Document!.Clip.Original.Fingerprint.Sha256,
                RoutingOutputKind.Landscape);
            Assert(delivery.RequestedOutput == expected,
                $"The {state} plan must use the stable logical landscape identity.");
            if (state is CaptureJournalState.OriginalCommitted or CaptureJournalState.CameraPending)
            {
                Assert(delivery.State == PlannedDeliveryState.WaitingForArtifact &&
                       delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.RequestedOutput,
                    $"A {state} rendition must remain pending without premature fallback.");
            }
            else if (state == CaptureJournalState.RenditionsReady)
            {
                Assert(delivery.State == PlannedDeliveryState.Ready &&
                       delivery.Output == expected,
                    "A ready rendition must retain the same logical identity and become ready.");
            }
            else
            {
                Assert(delivery.State == PlannedDeliveryState.Ready &&
                       delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
                       delivery.Output.Kind == RoutingOutputKind.Original &&
                       delivery.ArtifactErrorCode == "camera-layer-unavailable",
                    "A failed rendition must be atomically appended with its Use-original resolution.");
            }
        }
    }

    private static async Task AssertGateRevocationDuringPlanningPreventsAppendAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: false);
        var planner = new MutatingPlanner(() =>
        {
            fixture.LegacyState.IgnoredFileKeys.Add("appeared-during-planning");
            fixture.LegacyStateStore.Save(fixture.LegacyState);
        });
        var result = await fixture.CreateBridge(planner).PlanAsync(SourceEvent(fixture.Item));
        var persisted = fixture.OutboxStore.Load();
        Assert(result.Status == RoutingRuntimePlanStatus.Disabled && planner.Calls == 1 &&
               persisted.LoadedFromDisk && persisted.Document!.Plans.Count == 0,
            "Revoking cutover safety during planning must be rechecked immediately before CAS save.");
    }

    private static async Task AssertUnrequestedFailedOutputUsesNotProducedAsync(string root)
    {
        var fixture = await CreateFixtureAsync(
            root, reactionCamera: true, targetState: CaptureJournalState.RenditionsFailed);
        var user = fixture.Snapshot.Routes.Single(item => item.Source == RoutingRouteSource.User);
        var migration = fixture.Snapshot.Routes.Single(item => item.Source == RoutingRouteSource.Migration);
        var changed = user with
        {
            Revision = 2,
            Prepare = new RoutingPrepareSettings(false, true, RoutingMissingOutputBehavior.UseOriginal),
            Actions = [user.Actions[0] with { OutputRef = RoutingOutputKind.Portrait }, user.Actions[1]],
            ModifiedUtc = Now.AddMinutes(1)
        };
        var snapshot = RoutingSnapshotModel.ReplaceRoutes(
            fixture.Snapshot, [changed, migration], Now.AddMinutes(1));
        await fixture.SnapshotStore.SaveAsync(snapshot, fixture.Snapshot.Generation);
        var result = await fixture.CreateBridge(new RecordingPlanner())
            .PlanAsync(SourceEvent(fixture.Item));
        var delivery = fixture.OutboxStore.Load().Document!.Deliveries.Single();
        Assert(result.Status == RoutingRuntimePlanStatus.Planned &&
               delivery.RequestedOutput.Kind == RoutingOutputKind.Portrait &&
               delivery.ArtifactErrorCode == "output-not-produced",
            "A failed-rendition reason must apply only to requested kinds; an unrequested output is not produced.");
    }

    private static async Task AssertInvalidMediaAndPlannerLiesAreRejectedAsync(string root)
    {
        var mediaRoot = Path.Combine(root, "media");
        var mediaFixture = await CreateFixtureAsync(mediaRoot, reactionCamera: false);
        var invalidStatuses = new Dictionary<string, CaptureJournalArtifactValidationStatus>(
            StringComparer.Ordinal)
        {
            ["original"] = CaptureJournalArtifactValidationStatus.Mismatch
        };
        var invalidItem = mediaFixture.Item with { ArtifactStatuses = invalidStatuses };
        await AssertThrowsAsync<InvalidDataException>(
            () => mediaFixture.CreateBridge(new ThrowingPlanner()).PlanAsync(SourceEvent(invalidItem)),
            "A source whose fingerprint no longer validates must never reach the planner.");
        Assert(!File.Exists(mediaFixture.OutboxStore.Path),
            "Invalid source evidence must be rejected before outbox initialization.");

        var originalPath = CaptureJournalStore.GetCanonicalArtifactPath(
            mediaFixture.LibraryRoot,
            mediaFixture.Item.Document!.Clip,
            "original");
        await File.WriteAllBytesAsync(originalPath, [99, 98, 97]);
        await AssertThrowsAsync<InvalidDataException>(
            () => mediaFixture.CreateBridge(new ThrowingPlanner())
                .PlanAsync(SourceEvent(mediaFixture.Item)),
            "The bridge must revalidate media instead of trusting stale scan evidence.");
        Assert(!File.Exists(mediaFixture.OutboxStore.Path),
            "A post-scan fingerprint change must be rejected before outbox initialization.");

        var missingEvidence = mediaFixture.Item with
        {
            ArtifactStatuses = new Dictionary<string, CaptureJournalArtifactValidationStatus>()
        };
        await AssertThrowsAsync<InvalidDataException>(
            () => mediaFixture.CreateBridge(new ThrowingPlanner())
                .PlanAsync(SourceEvent(missingEvidence)),
            "A source event missing readiness evidence must never reach the planner.");

        var wrongIdentity = mediaFixture.Item with
        {
            ClipId = new string('f', 32)
        };
        await AssertThrowsAsync<InvalidDataException>(
            () => mediaFixture.CreateBridge(new ThrowingPlanner())
                .PlanAsync(SourceEvent(wrongIdentity)),
            "A journal event cannot steer planning to another SourceClipId.");

        var generationRoot = Path.Combine(root, "generation");
        var generationFixture = await CreateFixtureAsync(generationRoot, reactionCamera: true);
        await AssertThrowsAsync<InvalidDataException>(
            () => generationFixture.CreateBridge(new RecordingPlanner(wrongGeneration: true))
                .PlanAsync(SourceEvent(generationFixture.Item)),
            "Every plan member must pin the exact route generation supplied to the planner.");
        Assert(generationFixture.OutboxStore.Load().Document is { Deliveries.Count: 0 },
            "A proposal with a fabricated route generation must not append partial work.");

        var readinessRoot = Path.Combine(root, "readiness");
        var readinessFixture = await CreateFixtureAsync(readinessRoot, reactionCamera: true);
        await AssertThrowsAsync<InvalidDataException>(
            () => readinessFixture.CreateBridge(new RecordingPlanner(forceArtifactReady: true))
                .PlanAsync(SourceEvent(readinessFixture.Item)),
            "The planner cannot mark a pending rendition ready without validated artifact bytes.");
        Assert(readinessFixture.OutboxStore.Load().Document is { Deliveries.Count: 0 },
            "A readiness lie must not append partial outbox work.");
    }

    private static async Task AssertConcurrentSourceArrivalsAppendOnePlanAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: true);
        var firstPlanner = new RecordingPlanner();
        var secondPlanner = new RecordingPlanner();
        var tasks = new[]
        {
            fixture.CreateBridge(firstPlanner).PlanAsync(SourceEvent(fixture.Item)),
            fixture.CreateBridge(secondPlanner).PlanAsync(SourceEvent(fixture.Item))
        };
        var results = await Task.WhenAll(tasks);
        var persisted = fixture.OutboxStore.Load();
        Assert(results.Count(result => result.Status == RoutingRuntimePlanStatus.Planned) == 1 &&
               results.Count(result => result.Status == RoutingRuntimePlanStatus.AlreadyPlanned) == 1 &&
               persisted.Document is { Deliveries.Count: 1, FileDispositions.Count: 1 } &&
               persisted.Document.Deliveries.Single().PlanId ==
               persisted.Document.FileDispositions.Single().PlanId,
            "Concurrent source arrivals must converge on one atomically persisted plan.");
    }

    private static async Task AssertExistingPlanReconcilesReadyArtifactsAtomicallyAsync(
        string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: true);
        var user = fixture.Snapshot.Routes.Single(route =>
            route.Source == RoutingRouteSource.User);
        var firstDelivery = user.Actions.Single(action =>
            action.Kind == RoutingActionKind.Deliver);
        var fileAction = user.Actions.Single(action =>
            action.Kind == RoutingActionKind.FileIntoLibrary);
        var secondDelivery = firstDelivery with
        {
            ActionId = Guid.NewGuid(),
            Destination = RoutingDestinationKind.YouTube,
            ConnectionId = "youtube.primary"
        };
        var changedUser = user with
        {
            Revision = checked(user.Revision + 1),
            Actions = [firstDelivery, secondDelivery, fileAction],
            ModifiedUtc = Now.AddMilliseconds(500)
        };
        var changedSnapshot = RoutingSnapshotModel.ReplaceRoutes(
            fixture.Snapshot,
            fixture.Snapshot.Routes.Select(route => route.RouteId == user.RouteId
                ? changedUser
                : route).ToArray(),
            Now.AddMilliseconds(500));
        await fixture.SnapshotStore.SaveAsync(
            changedSnapshot,
            fixture.Snapshot.Generation);

        var planner = new RecordingPlanner();
        var bridge = fixture.CreateBridge(planner);
        await bridge.ReconcileAsync(fixture.Item, CancellationToken.None);
        var waiting = fixture.OutboxStore.Load().Document ??
                      throw new InvalidOperationException(
                          "The pending artifact plan was not persisted.");
        var frozenPlan = waiting.Plans.Single();
        var frozenIds = waiting.Deliveries.Select(delivery => delivery.DeliveryId)
            .Order()
            .ToArray();
        Assert(planner.Calls == 1 && waiting.Deliveries.Count == 2 &&
               waiting.Deliveries.All(delivery =>
                   delivery.State == PlannedDeliveryState.WaitingForArtifact),
            "The initial source reconciliation must freeze both rendition deliveries as waiting.");

        var readyItem = await AdvanceJournalToReadyAsync(
            fixture.LibraryRoot,
            fixture.Item.Document ?? throw new InvalidOperationException(
                "The initial journal fixture is missing."));
        await bridge.ReconcileAsync(readyItem, CancellationToken.None);
        var ready = fixture.OutboxStore.Load().Document ??
                    throw new InvalidOperationException(
                        "The ready artifact plan was not persisted.");
        Assert(planner.Calls == 1 && ready.Generation == waiting.Generation + 1 &&
               ready.Plans.Single() == frozenPlan &&
               ready.Deliveries.Select(delivery => delivery.DeliveryId).Order()
                   .SequenceEqual(frozenIds) &&
               ready.Deliveries.All(delivery =>
                   delivery.State == PlannedDeliveryState.Ready &&
                   delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.RequestedOutput &&
                   delivery.Output == delivery.RequestedOutput),
            "One ready journal snapshot must advance every matching frozen delivery atomically without re-planning.");

        await fixture.CreateBridge(new ThrowingPlanner())
            .ReconcileAsync(readyItem, CancellationToken.None);
        var replayed = fixture.OutboxStore.Load().Document ??
                       throw new InvalidOperationException(
                           "The replayed artifact plan could not be loaded.");
        Assert(replayed.Generation == ready.Generation && replayed == ready,
            "Replaying identical ready evidence must be an exact no-op without a new generation.");
    }

    private static async Task AssertExistingPlanReconcilesFailedArtifactsIdempotentlyAsync(
        string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: true);
        var planner = new RecordingPlanner();
        var bridge = fixture.CreateBridge(planner);
        await bridge.ReconcileAsync(fixture.Item, CancellationToken.None);
        var waiting = fixture.OutboxStore.Load().Document ??
                      throw new InvalidOperationException(
                          "The pending failure plan was not persisted.");
        var frozen = waiting.Deliveries.Single();

        var original = fixture.Item.Document ?? throw new InvalidOperationException(
            "The failure journal fixture is missing.");
        var pending = await CaptureJournalStore.BeginCameraAsync(
            fixture.LibraryRoot,
            original.Clip.ClipId,
            original.Generation,
            now: Now.AddSeconds(2));
        var failed = await CaptureJournalStore.FailRenditionsAsync(
            fixture.LibraryRoot,
            pending.Clip.ClipId,
            pending.Generation,
            "camera-layer-unavailable",
            now: Now.AddSeconds(3));
        var failedItem = await CreateReconciliationItemAsync(fixture.LibraryRoot, failed);

        await bridge.ReconcileAsync(failedItem, CancellationToken.None);
        var resolved = fixture.OutboxStore.Load().Document ??
                       throw new InvalidOperationException(
                           "The failed artifact plan was not persisted.");
        var delivery = resolved.Deliveries.Single();
        Assert(planner.Calls == 1 && resolved.Generation == waiting.Generation + 1 &&
               delivery.DeliveryId == frozen.DeliveryId && delivery.PlanId == frozen.PlanId &&
               delivery.State == PlannedDeliveryState.Ready &&
               delivery.RequestedOutput == frozen.RequestedOutput &&
               delivery.Output == delivery.OriginalOutput &&
               delivery.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
               delivery.ArtifactErrorCode == "camera-layer-unavailable",
            "A durable rendition failure must apply the frozen fallback without changing delivery identity or re-planning.");

        await bridge.ReconcileAsync(failedItem, CancellationToken.None);
        var replayed = fixture.OutboxStore.Load().Document ??
                       throw new InvalidOperationException(
                           "The replayed failure plan could not be loaded.");
        Assert(replayed.Generation == resolved.Generation && replayed == resolved,
            "Replaying identical failed evidence must not apply fallback or advance generation twice.");
    }

    private static async Task<CaptureJournalReconciliationItem> AdvanceJournalToReadyAsync(
        string libraryRoot,
        CaptureJournalDocument original)
    {
        var pending = await CaptureJournalStore.BeginCameraAsync(
            libraryRoot,
            original.Clip.ClipId,
            original.Generation,
            now: Now.AddSeconds(2));
        var cameraPath = CaptureProjectStore.GetCameraLayerPath(
            libraryRoot,
            original.Clip.ClipId);
        Directory.CreateDirectory(Path.GetDirectoryName(cameraPath)!);
        await File.WriteAllBytesAsync(cameraPath, [22, 24, 26, 28]);
        var camera = await CaptureJournalStore.CreateArtifactAsync(
            libraryRoot,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.ReactionCamera,
            cameraPath);
        pending = await CaptureJournalStore.AttachCameraAsync(
            libraryRoot,
            original.Clip.ClipId,
            pending.Generation,
            camera,
            now: Now.AddSeconds(3));
        var landscapePath = CaptureJournalStore.GetCanonicalArtifactPath(
            libraryRoot,
            original.Clip,
            CaptureJournalArtifactKinds.Landscape);
        await File.WriteAllBytesAsync(landscapePath, [30, 32, 34, 36]);
        var landscape = await CaptureJournalStore.CreateArtifactAsync(
            libraryRoot,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape,
            landscapePath);
        var ready = await CaptureJournalStore.AttachRenditionAsync(
            libraryRoot,
            original.Clip.ClipId,
            pending.Generation,
            landscape,
            now: Now.AddSeconds(4));
        return await CreateReconciliationItemAsync(libraryRoot, ready);
    }

    private static async Task<CaptureJournalReconciliationItem> CreateReconciliationItemAsync(
        string libraryRoot,
        CaptureJournalDocument journal)
    {
        var statuses = new Dictionary<string, CaptureJournalArtifactValidationStatus>(
            StringComparer.Ordinal);
        foreach (var artifact in new[] { journal.Clip.Original }.Concat(journal.Artifacts))
        {
            var validation = await CaptureJournalStore.ValidateArtifactAsync(
                libraryRoot,
                artifact);
            Assert(validation.Status == CaptureJournalArtifactValidationStatus.Valid,
                "Every changed journal artifact must validate before routing reconciliation.");
            statuses.Add(artifact.Kind, validation.Status);
        }
        return new CaptureJournalReconciliationItem(
            journal.Clip.ClipId,
            CaptureJournalLoadStatus.Loaded,
            journal,
            statuses);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        bool reactionCamera,
        CaptureJournalState targetState = CaptureJournalState.OriginalCommitted)
    {
        Directory.CreateDirectory(root);
        var gameDirectory = CaptureLibraryLayout.GetRecordingDirectory(root, "Runtime Test Game");
        Directory.CreateDirectory(gameDirectory);
        var originalPath = Path.Combine(gameDirectory, "2026-08-27_21-00-00.mp4");
        await File.WriteAllBytesAsync(originalPath, [1, 3, 5, 7, 9]);
        var requested = reactionCamera
            ? new[] { CaptureJournalArtifactKinds.Landscape }
            : [];
        var journal = await CaptureJournalStore.CommitOriginalAsync(
            root,
            originalPath,
            CaptureJournalSourceKind.InstantReplay,
            "Runtime Test Game",
            Now,
            TimeSpan.FromSeconds(30),
            1920,
            1080,
            reactionCamera,
            requested,
            now: Now);
        if (targetState != CaptureJournalState.OriginalCommitted)
        {
            journal = await CaptureJournalStore.BeginCameraAsync(
                root,
                journal.Clip.ClipId,
                journal.Generation,
                now: Now.AddSeconds(1));
        }
        if (targetState == CaptureJournalState.RenditionsFailed)
        {
            journal = await CaptureJournalStore.FailRenditionsAsync(
                root,
                journal.Clip.ClipId,
                journal.Generation,
                "camera-layer-unavailable",
                now: Now.AddSeconds(2));
        }
        else if (targetState == CaptureJournalState.RenditionsReady)
        {
            var cameraPath = CaptureProjectStore.GetCameraLayerPath(root, journal.Clip.ClipId);
            Directory.CreateDirectory(Path.GetDirectoryName(cameraPath)!);
            await File.WriteAllBytesAsync(cameraPath, [2, 4, 6, 8]);
            var camera = await CaptureJournalStore.CreateArtifactAsync(
                root, journal.Clip.ClipId, CaptureJournalArtifactKinds.ReactionCamera, cameraPath);
            journal = await CaptureJournalStore.AttachCameraAsync(
                root, journal.Clip.ClipId, journal.Generation, camera, now: Now.AddSeconds(2));
            var landscapePath = CaptureJournalStore.GetCanonicalArtifactPath(
                root, journal.Clip, CaptureJournalArtifactKinds.Landscape);
            await File.WriteAllBytesAsync(landscapePath, [10, 12, 14, 16]);
            var landscape = await CaptureJournalStore.CreateArtifactAsync(
                root, journal.Clip.ClipId, CaptureJournalArtifactKinds.Landscape, landscapePath);
            journal = await CaptureJournalStore.AttachRenditionAsync(
                root, journal.Clip.ClipId, journal.Generation, landscape, now: Now.AddSeconds(3));
        }
        Assert(journal.State == targetState,
            "The runtime fixture did not reach its requested journal state.");
        var statuses = new Dictionary<string, CaptureJournalArtifactValidationStatus>(StringComparer.Ordinal);
        foreach (var artifact in new[] { journal.Clip.Original }.Concat(journal.Artifacts))
        {
            var validation = await CaptureJournalStore.ValidateArtifactAsync(root, artifact);
            Assert(validation.Status == CaptureJournalArtifactValidationStatus.Valid,
                "Every runtime fixture artifact must validate before planning.");
            statuses[artifact.Kind] = validation.Status;
        }
        var item = new CaptureJournalReconciliationItem(
            journal.Clip.ClipId,
            CaptureJournalLoadStatus.Loaded,
            journal,
            statuses);

        var settings = new RoutingDeliverySettings(
            Message: null,
            Title: null,
            Caption: null,
            RoutingVisibility.Unspecified,
            NotifyFollowers: false);
        var delivery = new RoutingAction(
            Guid.NewGuid(),
            Enabled: true,
            RoutingActionKind.Deliver,
            RoutingDestinationKind.Discord,
            "discord.friends",
            RoutingOutputKind.Landscape,
            RoutingMissingOutputBehavior.UseOriginal,
            RoutingDeliveryMode.Automatic,
            LibraryArea: null,
            settings);
        var file = new RoutingAction(
            Guid.NewGuid(),
            Enabled: true,
            RoutingActionKind.FileIntoLibrary,
            Destination: null,
            ConnectionId: null,
            OutputRef: null,
            OnMissingOutput: null,
            RoutingDeliveryMode.Automatic,
            RoutingLibraryArea.Uploaded,
            DeliverySettings: null);
        var route = new RoutingRoute(
            Guid.NewGuid(),
            "Runtime Test Game highlights",
            Enabled: true,
            Priority: 1,
            Revision: 1,
            RoutingRouteSource.User,
            RoutingRouteKind.Specific,
            RoutingTriggerKind.AnyNewSourceClip,
            new RoutingPrepareSettings(
                Landscape: true,
                Portrait: false,
                RoutingMissingOutputBehavior.UseOriginal),
            Conditions:
            [
                new RoutingCondition(
                    Guid.NewGuid(),
                    RoutingConditionField.Game,
                    RoutingConditionOperator.Equals,
                    "Runtime Test Game")
            ],
            Actions: [delivery, file],
            CreatedUtc: Now,
            ModifiedUtc: Now);
        var stateDirectory = Path.Combine(root, "runtime-state");
        var legacyState = CreateLegacyState(stateDirectory);
        var legacyStateStore = CreateLegacyStateStore(stateDirectory, legacyState);
        var legacySettings = CreateLegacySettings(stateDirectory);
        IReadOnlyList<string> connectionIds = [];
        var migrationPlan = CreateMigrationPlan(
            legacySettings,
            legacyState,
            connectionIds);
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [route, migrationPlan.Route],
            CreatedUtc: Now,
            UpdatedUtc: Now);
        var snapshotStore = new RoutingSnapshotStore(Path.Combine(stateDirectory, "routes.json"));
        var outboxStore = new RoutingOutboxStore(Path.Combine(stateDirectory, "outbox.json"));
        await snapshotStore.SaveAsync(snapshot, expectedGeneration: 0);
        var marker = CreateMarkerStore(stateDirectory, migrationPlan, commit: true);
        var evidence = new LegacyRoutingActivationEvidenceSource(
            legacyStateStore,
            () => legacySettings,
            () => connectionIds);
        var ownership = new ClipProcessingOwnershipCoordinator();
        if (!ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var routingLease) ||
            routingLease is null)
        {
            throw new InvalidOperationException(
                "The focused runtime fixture could not acquire routing ownership.");
        }
        var gate = RoutingRuntimeFeatureGate.Evaluate(
            requestedEnabled: true,
            marker,
            snapshotStore,
            evidence,
            routingLease);
        Assert(gate.Enabled, "The focused runtime fixture must carry explicit safe cutover evidence.");
        return new Fixture(
            root,
            item,
            snapshot,
            snapshotStore,
            outboxStore,
            gate,
            legacyState,
            legacyStateStore,
            ownership,
            routingLease);
    }

    private static RoutingSnapshotStore CreateSnapshotStore(
        string root,
        RoutingRoute migrationRoute)
    {
        var store = new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName));
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [migrationRoute],
            CreatedUtc: migrationRoute.CreatedUtc,
            UpdatedUtc: migrationRoute.ModifiedUtc);
        _ = store.SaveAsync(snapshot, expectedGeneration: 0).GetAwaiter().GetResult();
        return store;
    }

    private static WatchState CreateLegacyState(string root) => new()
    {
        Version = 4,
        ClipsFolder = Path.GetFullPath(root),
        CaptureSource = ClipCaptureSource.SteelSeriesGg
    };

    private static WatchStateStore CreateLegacyStateStore(string root, WatchState state)
    {
        var directory = Path.Combine(root, "legacy-state");
        var store = new WatchStateStore(
            Path.Combine(directory, "state.json"),
            Path.Combine(directory, ".safe-baseline-required"));
        store.Save(state);
        return store;
    }

    private static AppSettings CreateLegacySettings(
        string root,
        bool uploadToDiscord = false) => new(
        Path.GetFullPath(root),
        uploadToDiscord
            ? "https://discord.com/api/webhooks/123456789012345678/test-token"
            : string.Empty,
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Routing Test",
        uploadToDiscord,
        ModeToggleHotkey: string.Empty,
        ClipCaptureSource.SteelSeriesGg);

    private static LegacyRoutingMigrationPlan CreateMigrationPlan(
        AppSettings settings,
        WatchState state,
        IReadOnlyList<string> connectionIds)
    {
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(
            new LegacyRoutingMigrationInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                connectionIds),
            Now);
        return readiness.Plan ?? throw new InvalidOperationException(
            $"The activation fixture migration plan is unavailable ({readiness.Status}).");
    }

    private static LegacyRoutingMigrationMarkerStore CreateMarkerStore(
        string root,
        LegacyRoutingMigrationPlan plan,
        bool commit)
    {
        Directory.CreateDirectory(root);
        var store = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(root, LegacyRoutingMigrationMarkerStore.FileName));
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(plan, Now);
        _ = store.SaveAsync(prepared, expectedGeneration: 0).GetAwaiter().GetResult();
        if (commit)
        {
            _ = store.SaveAsync(
                LegacyRoutingMigrationMarkerModel.Commit(prepared, Now.AddSeconds(1)),
                prepared.Generation).GetAwaiter().GetResult();
        }
        return store;
    }

    private static RoutingRuntimeSourceEvent SourceEvent(
        CaptureJournalReconciliationItem item) => new(
        RoutingRuntimeSourceEventKind.SourceClipCommitted,
        item);

    private sealed record Fixture(
        string LibraryRoot,
        CaptureJournalReconciliationItem Item,
        RoutingSnapshotDocument Snapshot,
        RoutingSnapshotStore SnapshotStore,
        RoutingOutboxStore OutboxStore,
        RoutingRuntimeFeatureGate Gate,
        WatchState LegacyState,
        WatchStateStore LegacyStateStore,
        ClipProcessingOwnershipCoordinator Ownership,
        ClipProcessingOwnershipLease RoutingLease)
    {
        internal RoutingRuntimeBridge CreateBridge(IRoutingRuntimePlanner planner) => new(
            LibraryRoot,
            SnapshotStore,
            OutboxStore,
            planner,
            Gate,
            () => Now.AddSeconds(1));
    }

    private sealed class RecordingPlanner : IRoutingRuntimePlanner
    {
        private readonly bool _wrongGeneration;
        private readonly bool _forceArtifactReady;
        private int _calls;

        internal RecordingPlanner(
            bool wrongGeneration = false,
            bool forceArtifactReady = false)
        {
            _wrongGeneration = wrongGeneration;
            _forceArtifactReady = forceArtifactReady;
        }

        internal int Calls => Volatile.Read(ref _calls);

        public RoutingPlanProposal BuildPlan(RoutingRuntimePlanningContext context)
        {
            Interlocked.Increment(ref _calls);
            var proposal = new RoutingEvaluatorRuntimePlanner().BuildPlan(context);
            if (_wrongGeneration)
            {
                return proposal with
                {
                    RoutingGeneration = checked(proposal.RoutingGeneration + 1)
                };
            }
            if (_forceArtifactReady)
            {
                return proposal with
                {
                    Deliveries = proposal.Deliveries.Select(delivery => delivery with
                    {
                        State = PlannedDeliveryState.Ready
                    }).ToArray()
                };
            }
            return proposal;
        }
    }

    private sealed class MutatingPlanner : IRoutingRuntimePlanner
    {
        private readonly Action _mutate;
        private int _calls;

        internal MutatingPlanner(Action mutate) => _mutate = mutate;
        internal int Calls => Volatile.Read(ref _calls);

        public RoutingPlanProposal BuildPlan(RoutingRuntimePlanningContext context)
        {
            Interlocked.Increment(ref _calls);
            var proposal = new RoutingEvaluatorRuntimePlanner().BuildPlan(context);
            _mutate();
            return proposal;
        }
    }

    private sealed class ThrowingPlanner : IRoutingRuntimePlanner
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public RoutingPlanProposal BuildPlan(RoutingRuntimePlanningContext context)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The planner must not be called in this test.");
        }
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
