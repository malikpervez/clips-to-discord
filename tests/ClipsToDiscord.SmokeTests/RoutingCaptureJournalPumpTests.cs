using ClipsToDiscord;

internal static class RoutingCaptureJournalPumpTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 19, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<ClipCaptureSource> FullLegacyCoverage =
        new HashSet<ClipCaptureSource>
        {
            ClipCaptureSource.SteelSeriesGg,
            ClipCaptureSource.Nvidia
        };

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertStartupPollingAndRenditionSettlementAsync(Path.Combine(testRoot, "flow"));
        await AssertGateLossStopsBeforeAnotherPassAsync(Path.Combine(testRoot, "gate-loss"));
        await AssertCapturePermitLossStopsBeforePollingOrOutboxMutationAsync(
            Path.Combine(testRoot, "capture-permit-loss"));
        await AssertCancelledStartupQuiescesAsync(Path.Combine(testRoot, "cancel"));
    }

    private static async Task AssertStartupPollingAndRenditionSettlementAsync(string root)
    {
        var fixture = await Fixture.CreateAsync(root);
        try
        {
            WriteCorruptJournal(fixture.LibraryRoot);
            var preexisting = await fixture.CommitClipAsync(0, reactionCamera: false);
            var pending = await fixture.CommitClipAsync(1, reactionCamera: true);
            var planner = new RecordingPlanner();
            var bridge = fixture.CreateBridge(planner);
            var delay = new ManualDelay();
            var executorCalls = 0;
            await using var pump = new CaptureJournalRoutingPump(
                fixture.LibraryRoot,
                bridge,
                fixture.Gate,
                _ =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return Task.FromResult(new RoutingExecutorRunResult(
                        Enabled: true,
                        ProviderAttempts: 0,
                        FileAttempts: 0,
                        RecoveryInspections: 0,
                        PersistedTransitions: 0));
                },
                TestOptions(),
                delayAsync: delay.DelayAsync);

            await pump.StartAsync();
            var startup = fixture.LoadOutbox();
            Assert(startup.Plans.Select(plan => plan.SourceClipId).ToHashSet().SetEquals([
                       preexisting.Clip.ClipId,
                       pending.Clip.ClipId
                   ]) && planner.Calls == 2 && executorCalls >= 3,
                "Start must synchronously traverse every bounded page, isolate a corrupt item, plan all readable preexisting journals, and run the executor callback.");
            Assert(File.Exists(CaptureJournalStore.GetCanonicalArtifactPath(
                       fixture.LibraryRoot, preexisting.Clip, "original")) &&
                   !Directory.Exists(Path.Combine(fixture.LibraryRoot, "uploaded")),
                "The durability bridge itself must not deliver or file source bytes.");

            await delay.WaitUntilEnteredAsync();
            var arrived = await fixture.CommitClipAsync(2, reactionCamera: false);
            delay.Release();
            await delay.WaitUntilEnteredAsync();
            Assert(fixture.LoadOutbox().Plans.Any(plan =>
                       plan.SourceClipId == arrived.Clip.ClipId) && planner.Calls == 3,
                "A journal committed after startup must be planned on the next idempotent poll.");

            var generationTwo = await fixture.AdvanceSnapshotAsync();
            var afterRouteEdit = await fixture.CommitClipAsync(3, reactionCamera: false);
            delay.Release();
            await delay.WaitUntilEnteredAsync();
            var afterEdit = fixture.LoadOutbox();
            Assert(afterEdit.Plans.Count == 4 && planner.Calls == 4 &&
                   afterEdit.Plans.Single(plan =>
                       plan.SourceClipId == afterRouteEdit.Clip.ClipId).RoutingGeneration ==
                   generationTwo.Generation &&
                   afterEdit.Plans.Single(plan =>
                       plan.SourceClipId == preexisting.Clip.ClipId).RoutingGeneration == 1,
                "A legitimate route edit between poll passes must use a fresh generation for new clips without re-planning existing clips.");

            var ready = await fixture.SettleLandscapeAsync(pending);
            delay.Release();
            await delay.WaitUntilEnteredAsync();
            var settled = fixture.LoadOutbox();
            var pendingPlan = settled.Plans.Single(plan =>
                plan.SourceClipId == pending.Clip.ClipId);
            var pendingDelivery = settled.Deliveries.Single(delivery =>
                delivery.PlanId == pendingPlan.PlanId);
            Assert(ready.State == CaptureJournalState.RenditionsReady &&
                   settled.Plans.Count == 4 && planner.Calls == 4 &&
                   pendingDelivery.Output.Kind == RoutingOutputKind.Landscape &&
                   pendingDelivery.State == PlannedDeliveryState.Ready,
                "A later rendition generation must reconcile the existing plan without planning the source twice.");

            await pump.StopAsync();
            Assert(pump.Failure is null && pump.Completion.IsCompleted &&
                   fixture.RoutingLease.IsCurrent &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "Stop must cancel the pending delay, await full quiescence, and leave the borrowed Routing lease untouched.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertGateLossStopsBeforeAnotherPassAsync(string root)
    {
        var fixture = await Fixture.CreateAsync(root);
        try
        {
            _ = await fixture.CommitClipAsync(0, reactionCamera: false);
            var bridge = fixture.CreateBridge(new RecordingPlanner());
            var delay = new ManualDelay();
            var executorCalls = 0;
            await using var pump = new CaptureJournalRoutingPump(
                fixture.LibraryRoot,
                bridge,
                fixture.Gate,
                _ =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return Task.FromResult(new RoutingExecutorRunResult(true, 0, 0, 0, 0));
                },
                TestOptions(),
                delayAsync: delay.DelayAsync);
            await pump.StartAsync();
            await delay.WaitUntilEnteredAsync();
            var callsBeforeLoss = executorCalls;

            fixture.RoutingLease.Dispose();
            delay.Release();
            await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(pump.Failure is CaptureJournalRoutingPumpAuthorityException &&
                   executorCalls == callsBeforeLoss &&
                   fixture.Ownership.Owner is null,
                "A changed permit must stop the pump before it scans or executes another pass.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertCapturePermitLossStopsBeforePollingOrOutboxMutationAsync(
        string root)
    {
        var fixture = await Fixture.CreateAsync(root);
        try
        {
            _ = await fixture.CommitClipAsync(0, reactionCamera: false);
            var expected = RoutingCaptureLibraryBindingModel.Create(fixture.LibraryRoot);
            var mismatch = expected with
            {
                NativeDirectoryIdentityFingerprint = new string('C', 64)
            };
            var current = expected;
            var permit = new RoutingCaptureLibraryPermit(
                expected,
                () => Volatile.Read(ref current));
            var planner = new RecordingPlanner();
            var bridge = fixture.CreateBridge(planner, permit);
            var delay = new ManualDelay();
            var executorCalls = 0;
            await using var pump = new CaptureJournalRoutingPump(
                fixture.LibraryRoot,
                bridge,
                fixture.Gate,
                _ =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return Task.FromResult(new RoutingExecutorRunResult(true, 0, 0, 0, 0));
                },
                TestOptions(),
                delayAsync: delay.DelayAsync,
                captureLibraryPermit: permit);

            await pump.StartAsync();
            await delay.WaitUntilEnteredAsync();
            var before = File.ReadAllBytes(fixture.OutboxStore.Path);
            var callsBeforeLoss = executorCalls;
            var arrived = await fixture.CommitClipAsync(1, reactionCamera: false);
            Volatile.Write(ref current, mismatch);
            delay.Release();

            await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var after = File.ReadAllBytes(fixture.OutboxStore.Path);
            Assert(pump.Failure is CaptureJournalRoutingPumpAuthorityException &&
                   executorCalls == callsBeforeLoss && planner.Calls == 1 &&
                   before.SequenceEqual(after) &&
                   fixture.LoadOutbox().Plans.All(plan =>
                       plan.SourceClipId != arrived.Clip.ClipId),
                "A live Capture-library revocation must stop the pump before its next journal read, executor call, or outbox mutation.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertCancelledStartupQuiescesAsync(string root)
    {
        var fixture = await Fixture.CreateAsync(root);
        try
        {
            var bridge = fixture.CreateBridge(new RecordingPlanner());
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var executorCalls = 0;
            await using var pump = new CaptureJournalRoutingPump(
                fixture.LibraryRoot,
                bridge,
                fixture.Gate,
                _ =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return Task.FromResult(new RoutingExecutorRunResult(true, 0, 0, 0, 0));
                },
                TestOptions(),
                async (_, cancellationToken) =>
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The cancelled poll unexpectedly resumed.");
                },
                Task.Delay);
            using var cancellation = new CancellationTokenSource();
            var start = pump.StartAsync(cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() => start);
            await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(executorCalls == 0 && pump.Failure is null &&
                   fixture.RoutingLease.IsCurrent &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "Cancellation during startup polling must fully quiesce without executing work or disposing Routing authority.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static CaptureJournalRoutingPumpOptions TestOptions() => new(
        PollInterval: TimeSpan.Zero,
        MaximumEntriesPerPage: 1,
        MaximumPageDuration: TimeSpan.FromSeconds(10),
        MaximumPagesPerPass: 100);

    private static void WriteCorruptJournal(string libraryRoot)
    {
        var directory = CaptureJournalStore.GetJournalDirectory(libraryRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "00000000000000000000000000000001.json"),
            "{ not-json");
    }

    private sealed class ManualDelay
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly SemaphoreSlim _release = new(0);

        internal async Task DelayAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            _entered.Release();
            await _release.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal Task WaitUntilEnteredAsync() =>
            _entered.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(
                task =>
                {
                    if (!task.Result)
                    {
                        throw new TimeoutException("The Capture pump did not reach its poll delay.");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        internal void Release() => _release.Release();
    }

    private sealed class RecordingPlanner : IRoutingRuntimePlanner
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public RoutingPlanProposal BuildPlan(RoutingRuntimePlanningContext context)
        {
            Interlocked.Increment(ref _calls);
            return new RoutingEvaluatorRuntimePlanner().BuildPlan(context);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private int _clipSequence;

        private Fixture(
            string libraryRoot,
            RoutingSnapshotStore snapshotStore,
            RoutingOutboxStore outboxStore,
            RoutingRuntimeFeatureGate gate,
            ClipProcessingOwnershipCoordinator ownership,
            ClipProcessingOwnershipLease routingLease)
        {
            LibraryRoot = libraryRoot;
            SnapshotStore = snapshotStore;
            OutboxStore = outboxStore;
            Gate = gate;
            Ownership = ownership;
            RoutingLease = routingLease;
        }

        internal string LibraryRoot { get; }
        internal RoutingSnapshotStore SnapshotStore { get; }
        internal RoutingOutboxStore OutboxStore { get; }
        internal RoutingRuntimeFeatureGate Gate { get; }
        internal ClipProcessingOwnershipCoordinator Ownership { get; }
        internal ClipProcessingOwnershipLease RoutingLease { get; }

        internal static async Task<Fixture> CreateAsync(string root)
        {
            Directory.CreateDirectory(root);
            var libraryRoot = Directory.CreateDirectory(
                Path.Combine(root, "capture-library")).FullName;
            var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(libraryRoot);
            var stateDirectory = Path.Combine(root, "routing-state");
            Directory.CreateDirectory(stateDirectory);
            var legacyState = new WatchState
            {
                Version = 4,
                ClipsFolder = Path.GetFullPath(stateDirectory),
                CaptureSource = ClipCaptureSource.SteelSeriesGg
            };
            var legacyStateStore = new WatchStateStore(
                Path.Combine(stateDirectory, "legacy", "state.json"),
                Path.Combine(stateDirectory, "legacy", ".safe-baseline-required"));
            legacyStateStore.Save(legacyState);
            var legacySettings = new AppSettings(
                Path.GetFullPath(stateDirectory),
                string.Empty,
                StartWithWindows: false,
                AppSettings.DefaultCompressionTargetMb,
                "Routing Pump Test",
                UploadToDiscord: false,
                ModeToggleHotkey: string.Empty,
                ClipCaptureSource.SteelSeriesGg);
            IReadOnlyList<string> connectionIds = [];
            var readiness = LegacyRoutingMigrationPlanner.Evaluate(
                new LegacyRoutingMigrationInput(
                    legacySettings,
                    legacyState,
                    LegacyWorkerQuiesced: true,
                    connectionIds,
                    captureLibraryBinding),
                Now);
            var migration = readiness.Plan ?? throw new InvalidOperationException(
                $"The pump fixture migration is unavailable ({readiness.Status}).");

            var delivery = new RoutingAction(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Enabled: true,
                RoutingActionKind.Deliver,
                RoutingDestinationKind.Discord,
                "discord.friends",
                RoutingOutputKind.Landscape,
                RoutingMissingOutputBehavior.UseOriginal,
                RoutingDeliveryMode.Automatic,
                LibraryArea: null,
                new RoutingDeliverySettings(
                    null, null, null, RoutingVisibility.Unspecified, false));
            var file = new RoutingAction(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
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
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "Capture pump route",
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
                [new RoutingCondition(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    RoutingConditionField.Game,
                    RoutingConditionOperator.Equals,
                    "Runtime Test Game")],
                [delivery, file],
                Now,
                Now);
            var snapshotStore = new RoutingSnapshotStore(
                Path.Combine(stateDirectory, "routes.json"));
            await snapshotStore.SaveAsync(
                new RoutingSnapshotDocument(
                    RoutingSnapshotStore.CurrentSchemaVersion,
                    Generation: 1,
                    [route, migration.Route],
                    Now,
                    Now),
                expectedGeneration: 0);
            var markerStore = new LegacyRoutingMigrationMarkerStore(
                Path.Combine(stateDirectory, LegacyRoutingMigrationMarkerStore.FileName));
            var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(migration, Now);
            await markerStore.SaveAsync(prepared, expectedGeneration: 0);
            var committed = LegacyRoutingMigrationMarkerModel.Commit(
                prepared, Now.AddSeconds(1));
            await markerStore.SaveAsync(committed, prepared.Generation);
            var authorityStore = new RoutingExecutionAuthorityStore(
                Path.Combine(stateDirectory, RoutingExecutionAuthorityStore.FileName));
            await authorityStore.CommitAsync(RoutingExecutionAuthorityModel.Create(
                committed,
                ClipCaptureSource.SteelSeriesGg,
                Now.AddSeconds(2)));
            var evidence = new LegacyRoutingActivationEvidenceSource(
                legacyStateStore,
                () => legacySettings,
                () => connectionIds,
                () => captureLibraryBinding);
            var ownership = new ClipProcessingOwnershipCoordinator();
            if (!ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var routingLease) ||
                routingLease is null)
            {
                throw new InvalidOperationException(
                    "The pump fixture could not acquire Routing ownership.");
            }
            var gate = RoutingRuntimeFeatureGate.Evaluate(
                requestedEnabled: true,
                markerStore,
                snapshotStore,
                evidence,
                routingLease,
                FullLegacyCoverage,
                authorityStore);
            Assert(gate.Enabled, "The Capture pump fixture must have enabled authority.");
            return new Fixture(
                libraryRoot,
                snapshotStore,
                new RoutingOutboxStore(Path.Combine(stateDirectory, "outbox.json")),
                gate,
                ownership,
                routingLease);
        }

        internal async Task<CaptureJournalDocument> CommitClipAsync(
            int ordinal,
            bool reactionCamera)
        {
            var gameDirectory = CaptureLibraryLayout.GetRecordingDirectory(
                LibraryRoot, "Runtime Test Game");
            Directory.CreateDirectory(gameDirectory);
            var sequence = Interlocked.Increment(ref _clipSequence);
            var originalPath = Path.Combine(
                gameDirectory,
                $"2026-08-29_19-{ordinal:00}-{sequence:00}.mp4");
            await File.WriteAllBytesAsync(originalPath,
                [(byte)(10 + sequence), (byte)(20 + sequence), (byte)(30 + sequence)]);
            var journal = await CaptureJournalStore.CommitOriginalAsync(
                LibraryRoot,
                originalPath,
                CaptureJournalSourceKind.InstantReplay,
                "Runtime Test Game",
                Now.AddMinutes(ordinal),
                TimeSpan.FromSeconds(30),
                1920,
                1080,
                reactionCamera,
                reactionCamera ? [CaptureJournalArtifactKinds.Landscape] : [],
                now: Now.AddMinutes(ordinal));
            if (reactionCamera)
            {
                journal = await CaptureJournalStore.BeginCameraAsync(
                    LibraryRoot,
                    journal.Clip.ClipId,
                    journal.Generation,
                    now: Now.AddMinutes(ordinal).AddSeconds(1));
            }
            return journal;
        }

        internal async Task<CaptureJournalDocument> SettleLandscapeAsync(
            CaptureJournalDocument pending)
        {
            var cameraPath = CaptureProjectStore.GetCameraLayerPath(
                LibraryRoot, pending.Clip.ClipId);
            Directory.CreateDirectory(Path.GetDirectoryName(cameraPath)!);
            await File.WriteAllBytesAsync(cameraPath, [2, 4, 6, 8]);
            var camera = await CaptureJournalStore.CreateArtifactAsync(
                LibraryRoot,
                pending.Clip.ClipId,
                CaptureJournalArtifactKinds.ReactionCamera,
                cameraPath);
            var withCamera = await CaptureJournalStore.AttachCameraAsync(
                LibraryRoot,
                pending.Clip.ClipId,
                pending.Generation,
                camera,
                now: Now.AddMinutes(10));
            var landscapePath = CaptureJournalStore.GetCanonicalArtifactPath(
                LibraryRoot,
                withCamera.Clip,
                CaptureJournalArtifactKinds.Landscape);
            Directory.CreateDirectory(Path.GetDirectoryName(landscapePath)!);
            await File.WriteAllBytesAsync(landscapePath, [10, 12, 14, 16]);
            var landscape = await CaptureJournalStore.CreateArtifactAsync(
                LibraryRoot,
                withCamera.Clip.ClipId,
                CaptureJournalArtifactKinds.Landscape,
                landscapePath);
            return await CaptureJournalStore.AttachRenditionAsync(
                LibraryRoot,
                withCamera.Clip.ClipId,
                withCamera.Generation,
                landscape,
                now: Now.AddMinutes(11));
        }

        internal async Task<RoutingSnapshotDocument> AdvanceSnapshotAsync()
        {
            var loaded = SnapshotStore.Load();
            if (!loaded.LoadedFromDisk || loaded.Document is null)
            {
                throw new InvalidDataException(
                    $"The pump fixture snapshot cannot be loaded ({loaded.Status}).");
            }
            var next = RoutingSnapshotModel.ReplaceRoutes(
                loaded.Document,
                loaded.Document.Routes,
                loaded.Document.UpdatedUtc.AddSeconds(1));
            return await SnapshotStore.SaveAsync(next, loaded.Document.Generation);
        }

        internal RoutingRuntimeBridge CreateBridge(
            IRoutingRuntimePlanner planner,
            RoutingCaptureLibraryPermit? captureLibraryPermit = null) => new(
            LibraryRoot,
            SnapshotStore,
            OutboxStore,
            planner,
            Gate,
            () => Now.AddMinutes(20),
            captureLibraryPermit);

        internal RoutingOutboxDocument LoadOutbox()
        {
            var loaded = OutboxStore.Load();
            if (!loaded.LoadedFromDisk || loaded.Document is null)
            {
                throw new InvalidDataException(
                    $"The pump fixture outbox cannot be loaded ({loaded.Status}).");
            }
            return loaded.Document;
        }

        public void Dispose() => RoutingLease.Dispose();
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
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
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
