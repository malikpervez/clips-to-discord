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
            AssertCutoverGateFailsClosed(Path.Combine(root, "gate"));
            await AssertDefaultBridgeIsInertAsync(Path.Combine(root, "disabled"));
            await AssertPlanIsFrozenOnceAndSurvivesRestartAsync(Path.Combine(root, "restart"));
            await AssertNoOpPlanIsFrozenAcrossRestartAsync(Path.Combine(root, "no-op-restart"));
            await AssertEveryJournalStatePlansWithStableOutputsAsync(Path.Combine(root, "states"));
            await AssertReconciliationSkipsUnreadableBeforeValidAsync(Path.Combine(root, "reconcile-skip"));
            await AssertGateRevocationDuringPlanningPreventsAppendAsync(Path.Combine(root, "gate-revoke"));
            await AssertUnrequestedFailedOutputUsesNotProducedAsync(Path.Combine(root, "unrequested-failure"));
            await AssertNonSourceEventsCannotCreatePlansAsync(Path.Combine(root, "non-source"));
            await AssertInvalidMediaAndPlannerLiesAreRejectedAsync(Path.Combine(root, "validation"));
            await AssertConcurrentSourceArrivalsAppendOnePlanAsync(Path.Combine(root, "concurrent"));
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
        var marker = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(root, LegacyRoutingMigrationMarkerStore.FileName));
        var drained = new WatchState();

        Assert(!RoutingRuntimeFeatureGate.Disabled.Enabled &&
               RoutingRuntimeFeatureGate.Disabled.State ==
               RoutingRuntimeGateState.DisabledByDefault,
            "The routing runtime feature gate must be disabled by default.");
        Assert(RoutingRuntimeFeatureGate.Evaluate(false, marker, drained).State ==
               RoutingRuntimeGateState.DisabledByDefault,
            "A committed marker must not override the explicit disabled default.");
        Assert(RoutingRuntimeFeatureGate.Evaluate(true, marker, drained).State ==
               RoutingRuntimeGateState.MigrationMarkerMissing,
            "A requested cutover without a committed marker must fail closed.");

        File.WriteAllText(marker.Path, "status=committed");
        Assert(RoutingRuntimeFeatureGate.Evaluate(true, marker, drained).State ==
               RoutingRuntimeGateState.MigrationMarkerNotCommitted,
            "A lookalike or old migration marker must not activate routing.");

        File.Delete(marker.Path);
        marker = CreateMarkerStore(root, CreateMigrationRoute(priority: 1), commit: false);
        Assert(RoutingRuntimeFeatureGate.Evaluate(true, marker, drained).State ==
               RoutingRuntimeGateState.MigrationMarkerNotCommitted,
            "A valid but merely prepared migration marker must not activate routing.");
        var prepared = marker.Load().Document!;
        _ = marker.SaveAsync(
            LegacyRoutingMigrationMarkerModel.Commit(prepared, Now.AddSeconds(1)),
            prepared.Generation).GetAwaiter().GetResult();
        var pending = new WatchState();
        pending.PendingMoves.Add("pending-uploaded.mp4");
        pending.PendingLocalOnlyMoves.Add("pending-local.mp4");
        pending.PendingEditedUploads.Add(new PendingEditedClipDisposition
        {
            Id = Guid.NewGuid()
        });
        var blocked = RoutingRuntimeFeatureGate.Evaluate(true, marker, pending);
        Assert(blocked.State == RoutingRuntimeGateState.LegacyQueuesPending &&
               blocked.PendingLegacyMoves == 1 &&
               blocked.PendingLegacyLocalOnlyMoves == 1 &&
               blocked.PendingLegacyEditedUploads == 1,
            "Every legacy WatchState pending queue must drain before routing can activate.");

        var malformed = new WatchState
        {
            PendingMoves = null!,
            PendingLocalOnlyMoves = null!,
            PendingEditedUploads = null!
        };
        Assert(RoutingRuntimeFeatureGate.Evaluate(true, marker, malformed).State ==
               RoutingRuntimeGateState.LegacyQueuesPending,
            "Missing legacy queue state must be treated as unsafe, not empty.");

        var enabled = RoutingRuntimeFeatureGate.Evaluate(true, marker, drained);
        Assert(enabled.Enabled,
            "Only an exact committed marker plus fully drained legacy queues may enable the gate.");
        drained.PendingMoves.Add("late-legacy-work.mp4");
        Assert(enabled.State == RoutingRuntimeGateState.LegacyQueuesPending,
            "A gate must revoke activation if legacy pending work appears after evaluation.");
        drained.PendingMoves.Clear();
        drained.IgnoredFileKeys.Add("legacy-baseline-key");
        Assert(enabled.State == RoutingRuntimeGateState.LegacyQueuesPending &&
               enabled.IgnoredLegacyFileKeys == 1,
            "A live ignored-file baseline must revoke routing activation.");
        drained.IgnoredFileKeys.Clear();
        File.Delete(marker.Path);
        Assert(enabled.State == RoutingRuntimeGateState.MigrationMarkerMissing,
            "A gate must revoke activation if its committed migration marker disappears.");
    }

    private static async Task AssertNoOpPlanIsFrozenAcrossRestartAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root, reactionCamera: false);
        var emptySnapshot = RoutingSnapshotModel.ReplaceRoutes(
            fixture.Snapshot, [], Now.AddMinutes(1));
        await fixture.SnapshotStore.SaveAsync(emptySnapshot, fixture.Snapshot.Generation);
        var planner = new RecordingPlanner();
        var first = await fixture.CreateBridge(planner).PlanAsync(SourceEvent(fixture.Item));
        var persisted = fixture.OutboxStore.Load().Document!;
        Assert(first.Status == RoutingRuntimePlanStatus.Planned && planner.Calls == 1 &&
               persisted.Plans.Count == 1 && persisted.Deliveries.Count == 0 &&
               persisted.FileDispositions.Count == 0 &&
               persisted.Plans[0].RoutingGeneration == emptySnapshot.Generation &&
               persisted.Plans[0].MatchedRouteIds.Count == 0,
            "A no-match source must persist one generation-pinned no-op decision.");

        var route = fixture.Snapshot.Routes.First(candidate =>
            candidate.Source == RoutingRouteSource.User) with
        {
            Revision = 1,
            CreatedUtc = Now.AddMinutes(2),
            ModifiedUtc = Now.AddMinutes(2)
        };
        var later = RoutingSnapshotModel.ReplaceRoutes(
            emptySnapshot, [route], Now.AddMinutes(2));
        await fixture.SnapshotStore.SaveAsync(later, emptySnapshot.Generation);
        var restartPlanner = new RecordingPlanner();
        var restarted = await fixture.CreateBridge(restartPlanner).PlanAsync(SourceEvent(fixture.Item));
        Assert(restarted.Status == RoutingRuntimePlanStatus.AlreadyPlanned &&
               restarted.PlanId == first.PlanId && restartPlanner.Calls == 0 &&
               fixture.OutboxStore.Load().Document!.Plans[0].RoutingGeneration ==
                   emptySnapshot.Generation,
            "Restart after route edits must find the no-op plan header and never re-evaluate.");
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
            fixture.LegacyState.IgnoredFileKeys.Add("appeared-during-planning"));
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
            Priority: 0,
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
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [route, CreateMigrationRoute(priority: 1)],
            CreatedUtc: Now,
            UpdatedUtc: Now);
        var stateDirectory = Path.Combine(root, "runtime-state");
        var snapshotStore = new RoutingSnapshotStore(Path.Combine(stateDirectory, "routes.json"));
        var outboxStore = new RoutingOutboxStore(Path.Combine(stateDirectory, "outbox.json"));
        await snapshotStore.SaveAsync(snapshot, expectedGeneration: 0);
        var marker = CreateMarkerStore(
            stateDirectory,
            snapshot.Routes.Single(candidate => candidate.Source == RoutingRouteSource.Migration),
            commit: true);
        var gate = RoutingRuntimeFeatureGate.Evaluate(
            requestedEnabled: true,
            marker,
            new WatchState());
        Assert(gate.Enabled, "The focused runtime fixture must carry explicit safe cutover evidence.");
        var legacyState = new WatchState();
        gate = RoutingRuntimeFeatureGate.Evaluate(requestedEnabled: true, marker, legacyState);
        return new Fixture(root, item, snapshot, snapshotStore, outboxStore, gate, legacyState);
    }

    private static LegacyRoutingMigrationMarkerStore CreateMarkerStore(
        string root,
        RoutingRoute migrationRoute,
        bool commit)
    {
        Directory.CreateDirectory(root);
        var store = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(root, LegacyRoutingMigrationMarkerStore.FileName));
        var fingerprint = new string('A', 64);
        var plan = new LegacyRoutingMigrationPlan(
            LegacyRoutingMigrationPlanner.DeterministicGuid(fingerprint, "migration"),
            LegacyRoutingMode.LocalOnly,
            LegacyRoutingCutoverScope.FutureClipsOnly,
            fingerprint,
            migrationRoute,
            new LegacyContentHashExclusions([], [], []));
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

    private static RoutingRoute CreateMigrationRoute(int priority)
    {
        var fingerprint = new string('A', 64);
        return new RoutingRoute(
            LegacyRoutingMigrationPlanner.DeterministicGuid(fingerprint, "route"),
            "Everything else → Local only",
            Enabled: true,
            priority,
            Revision: 1,
            RoutingRouteSource.Migration,
            RoutingRouteKind.Fallback,
            RoutingTriggerKind.AnyNewSourceClip,
            new RoutingPrepareSettings(false, false, RoutingMissingOutputBehavior.UseOriginal),
            Conditions: [],
            Actions:
            [
                new RoutingAction(
                    LegacyRoutingMigrationPlanner.DeterministicGuid(fingerprint, "file-action"),
                    Enabled: true,
                    RoutingActionKind.FileIntoLibrary,
                    Destination: null,
                    ConnectionId: null,
                    OutputRef: null,
                    OnMissingOutput: null,
                    RoutingDeliveryMode.Automatic,
                    RoutingLibraryArea.LocalOnly,
                    DeliverySettings: null)
            ],
            CreatedUtc: Now,
            ModifiedUtc: Now);
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
        WatchState LegacyState)
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
