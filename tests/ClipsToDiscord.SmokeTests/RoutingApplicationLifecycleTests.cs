using ClipsToDiscord;

internal static class RoutingApplicationLifecycleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 20, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertMissingAuthorityStartsLegacyThenCutsOverAsync(
            Path.Combine(testRoot, "missing"));
        await AssertCommittedAuthorityColdStartsRoutingOnlyAsync(
            Path.Combine(testRoot, "cold"));
        await AssertCommittedMarkerColdRecoveryNeverStartsLegacyAsync(
            Path.Combine(testRoot, "committed-marker-recovery"));
        await AssertRoutingManualOperationQuiescesAndRestartsWithoutLegacyAsync(
            Path.Combine(testRoot, "manual-exclusive"));
        await AssertBlockedAuthorityStartsNeitherAsync(
            Path.Combine(testRoot, "blocked"));
        await AssertCancellationAndFailuresRemainFencedAsync(
            Path.Combine(testRoot, "failure"));
        await AssertStickySettingsGuardAsync(Path.Combine(testRoot, "settings"));
    }

    private static async Task AssertMissingAuthorityStartsLegacyThenCutsOverAsync(string root)
    {
        Directory.CreateDirectory(root);
        var authority = Store(root);
        var fixture = new RuntimeFixture();
        var lifecycle = RoutingApplicationLifecycle.Create(
            authority,
            fixture.Ownership,
            fixture.Legacy,
            fixture.Routing);
        Assert(lifecycle.AuthorityInspection.LegacyPermitted &&
               lifecycle.AuthorityInspection.LoadStatus == RoutingDocumentLoadStatus.Missing &&
               lifecycle.State == RoutingApplicationLifecycleState.LegacyReady &&
               lifecycle.LegacyOperationsPermitted &&
               lifecycle.Coordinator is not null &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy &&
               fixture.Events.Count == 0,
            "Missing authority must synchronously select Legacy without starting either runtime.");

        var started = await lifecycle.StartAsync();
        Assert(started.Status == RoutingApplicationLifecycleStatus.LegacyStarted &&
               lifecycle.State == RoutingApplicationLifecycleState.LegacyRunning &&
               lifecycle.LegacyOperationsPermitted &&
               fixture.Legacy.StartCalls == 1 && fixture.Routing.StartCalls == 0 &&
               fixture.Events.SequenceEqual(["legacy-start"]),
            "The first lifecycle start must start only the selected Legacy watcher.");

        var activated = await lifecycle.ActivateRoutingAsync();
        Assert(activated.Status == RoutingApplicationLifecycleStatus.RoutingStarted &&
               lifecycle.State == RoutingApplicationLifecycleState.RoutingRunning &&
               !lifecycle.LegacyOperationsPermitted &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
               fixture.Legacy.StopCalls == 1 && fixture.Routing.PrepareCalls == 1 &&
               fixture.Routing.CommitCalls == 1 && fixture.Routing.StartCalls == 1 &&
               fixture.Events.SequenceEqual([
                   "legacy-start", "legacy-stop", "routing-prepare", "routing-commit",
                   "routing-start"
               ]),
            "The explicit first-activation cutover must quiesce Legacy before Routing prepares, commits authority, and starts.");

        var stopped = await lifecycle.StopAsync();
        Assert(stopped.Status == RoutingApplicationLifecycleStatus.Stopped &&
               lifecycle.State == RoutingApplicationLifecycleState.RoutingQuiesced &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
               fixture.Legacy.StartCalls == 1,
            "Stopping sticky Routing must retain Routing ownership and must never revive Legacy.");
    }

    private static async Task AssertCommittedAuthorityColdStartsRoutingOnlyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = Store(root);
        var captureLibraryBinding = await CommitAuthorityAsync(
            store,
            Path.Combine(root, "clips"));

        for (var restart = 0; restart < 2; restart++)
        {
            var fixture = new RuntimeFixture();
            var lifecycle = RoutingApplicationLifecycle.Create(
                new RoutingExecutionAuthorityStore(store.Path),
                fixture.Ownership,
                fixture.Legacy,
                fixture.Routing,
                currentCaptureLibraryBinding: () => captureLibraryBinding);
            Assert(lifecycle.AuthorityInspection.RoutingRequired &&
                   lifecycle.State == RoutingApplicationLifecycleState.RoutingReady &&
                   !lifecycle.LegacyOperationsPermitted &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StartCalls == 0 && fixture.Legacy.StopCalls == 0 &&
                   fixture.Routing.PrepareCalls == 0,
                "Every cold process must adopt committed Routing authority before any runtime callback.");

            var started = await lifecycle.StartAsync();
            Assert(started.Status == RoutingApplicationLifecycleStatus.RoutingStarted &&
                   lifecycle.State == RoutingApplicationLifecycleState.RoutingRunning &&
                   fixture.Legacy.StartCalls == 0 && fixture.Legacy.StopCalls == 0 &&
                   fixture.Routing.PrepareCalls == 0 && fixture.Routing.CommitCalls == 1 &&
                   fixture.Routing.StartCalls == 1 &&
                   fixture.Events.SequenceEqual(["routing-commit", "routing-start"]),
                "Committed authority must recommit idempotently and start Routing with zero Legacy or preparation calls.");

            _ = await lifecycle.StopAsync();
            Assert(lifecycle.State == RoutingApplicationLifecycleState.RoutingQuiesced &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StartCalls == 0,
                "A cold-started Routing runtime must quiesce without releasing the sticky process fence.");
        }
    }

    private static async Task AssertRoutingManualOperationQuiescesAndRestartsWithoutLegacyAsync(
        string root)
    {
        Directory.CreateDirectory(root);
        var store = Store(root);
        var captureLibraryBinding = await CommitAuthorityAsync(
            store,
            Path.Combine(root, "clips"));
        var fixture = new RuntimeFixture();
        var lifecycle = RoutingApplicationLifecycle.Create(
            store,
            fixture.Ownership,
            fixture.Legacy,
            fixture.Routing,
            currentCaptureLibraryBinding: () => captureLibraryBinding);

        var initial = await lifecycle.StartAsync();
        Assert(initial.State == RoutingApplicationLifecycleState.RoutingRunning,
            "The manual-operation fixture did not cold-start committed Routing.");

        var quiesced = await lifecycle.StopAsync();
        Assert(quiesced.State == RoutingApplicationLifecycleState.RoutingQuiesced,
            "A manual edited upload must quiesce Routing before touching a managed clip.");
        fixture.Events.Add("manual-upload");
        var restarted = await lifecycle.StartAsync();

        Assert(restarted.State == RoutingApplicationLifecycleState.RoutingRunning &&
               fixture.Legacy.StartCalls == 0 && fixture.Legacy.StopCalls == 0 &&
               fixture.Routing.StartCalls == 2 && fixture.Routing.StopCalls == 1 &&
               fixture.Events.SequenceEqual([
                   "routing-commit", "routing-start",
                   "routing-stop", "manual-upload",
                   "routing-commit", "routing-start"
               ]),
            "A Routing-owned manual upload must run inside quiesce/restart and must never revive Legacy.");

        _ = await lifecycle.StopAsync();
    }

    private static async Task AssertCommittedMarkerColdRecoveryNeverStartsLegacyAsync(
        string root)
    {
        Directory.CreateDirectory(root);
        var clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(clips);
        var captureLibraryRoot = Path.Combine(root, "capture-library");
        Directory.CreateDirectory(captureLibraryRoot);
        var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(
            captureLibraryRoot);
        var markerStore = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            root,
            "routing-state",
            LegacyRoutingMigrationMarkerStore.FileName));
        var settings = Settings(clips);
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(
            new LegacyRoutingMigrationInput(
                settings,
                new WatchState
                {
                    Version = 4,
                    ClipsFolder = clips,
                    CaptureSource = ClipCaptureSource.SteelSeriesGg
                },
                LegacyWorkerQuiesced: true,
                DiscordConnectionIds: [],
                captureLibraryBinding),
            Now);
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(
            readiness.Plan ?? throw new InvalidOperationException(
                "The committed-marker recovery fixture could not prepare migration evidence."),
            Now);
        _ = await markerStore.SaveAsync(prepared, 0);
        _ = await markerStore.SaveAsync(
            LegacyRoutingMigrationMarkerModel.Commit(prepared, Now.AddSeconds(1)),
            prepared.Generation);

        var fixture = new RuntimeFixture();
        var lifecycle = RoutingApplicationLifecycle.Create(
            Store(root),
            fixture.Ownership,
            fixture.Legacy,
            fixture.Routing,
            migrationMarkers: markerStore,
            currentCaptureLibraryBinding: () => captureLibraryBinding);
        Assert(lifecycle.State == RoutingApplicationLifecycleState.RoutingReady &&
               !lifecycle.LegacyOperationsPermitted &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
               fixture.Events.Count == 0,
            "A committed migration marker without execution authority must synchronously fence Legacy before any runtime callback.");
        var stoppedBeforeStart = await lifecycle.StopAsync();
        Assert(stoppedBeforeStart.State ==
                   RoutingApplicationLifecycleState.RoutingQuiesced &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
               fixture.Legacy.StartCalls == 0 && fixture.Legacy.StopCalls == 0,
            "Stopping before committed-marker recovery starts must retain the durable Routing fence and never revive Legacy.");
        var recoveryFailure = new IOException("committed marker verification failed");
        fixture.Routing.PrepareError = recoveryFailure;
        var failedRecovery = await lifecycle.StartAsync();
        Assert(failedRecovery.State ==
                   RoutingApplicationLifecycleState.RoutingRecoveryNeeded &&
               !lifecycle.LegacyOperationsPermitted &&
               ReferenceEquals(failedRecovery.Error, recoveryFailure) &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
               fixture.Legacy.StartCalls == 0 && fixture.Legacy.StopCalls == 0,
            "Failed committed-marker recovery must remain fenced under Routing ownership.");
        _ = await lifecycle.StopAsync();
        Assert(fixture.Legacy.StartCalls == 0 &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
            "Stopping after failed committed-marker recovery must not fall back to Legacy.");
        fixture.Routing.PrepareError = null;
        var started = await lifecycle.StartAsync();
        Assert(started.State == RoutingApplicationLifecycleState.RoutingRunning &&
               fixture.Legacy.StartCalls == 0 && fixture.Legacy.StopCalls == 0 &&
               fixture.Routing.PrepareCalls == 2 && fixture.Routing.CommitCalls == 1 &&
               fixture.Routing.StartCalls == 1 &&
               fixture.Events.Where(item => item != "routing-stop").SequenceEqual([
                   "routing-prepare", "routing-prepare",
                   "routing-commit", "routing-start"
               ]),
            "Cold recovery must verify the committed marker and finish authority commit without ever starting Legacy.");

        var corruptRoot = Path.Combine(root, "corrupt");
        var corruptMarkers = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            corruptRoot,
            "routing-state",
            LegacyRoutingMigrationMarkerStore.FileName));
        Directory.CreateDirectory(Path.GetDirectoryName(corruptMarkers.Path)!);
        await File.WriteAllTextAsync(corruptMarkers.Path, "{corrupt-marker");
        var corruptFixture = new RuntimeFixture();
        var blocked = RoutingApplicationLifecycle.Create(
            Store(corruptRoot),
            corruptFixture.Ownership,
            corruptFixture.Legacy,
            corruptFixture.Routing,
            migrationMarkers: corruptMarkers);
        var blockedStart = await blocked.StartAsync();
        Assert(blocked.State == RoutingApplicationLifecycleState.NeedsAttention &&
               !blocked.LegacyOperationsPermitted &&
               blockedStart.Status == RoutingApplicationLifecycleStatus.NeedsAttention &&
               corruptFixture.Ownership.Owner is null &&
               corruptFixture.Events.Count == 0,
            "Corrupt pre-authority migration evidence must start neither Legacy nor Routing.");
    }

    private static async Task AssertBlockedAuthorityStartsNeitherAsync(string root)
    {
        Directory.CreateDirectory(root);
        var corrupt = Store(Path.Combine(root, "corrupt"));
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt.Path)!);
        await File.WriteAllTextAsync(corrupt.Path, "{not-json");
        await AssertBlockedAsync(corrupt, RoutingDocumentLoadStatus.Corrupt);

        var unsupported = Store(Path.Combine(root, "unsupported"));
        Directory.CreateDirectory(Path.GetDirectoryName(unsupported.Path)!);
        await File.WriteAllTextAsync(unsupported.Path, "{\"schemaVersion\":999}");
        await AssertBlockedAsync(unsupported, RoutingDocumentLoadStatus.UnsupportedSchema);

        var unavailable = Store(Path.Combine(root, "unavailable"));
        Directory.CreateDirectory(Path.GetDirectoryName(unavailable.Path)!);
        await File.WriteAllTextAsync(unavailable.Path, "{\"schemaVersion\":1}");
        await using (var locked = new FileStream(
                         unavailable.Path,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            await AssertBlockedAsync(unavailable, RoutingDocumentLoadStatus.Unavailable);
        }

        var cancelledRoot = Path.Combine(root, "cancelled-inspection");
        Directory.CreateDirectory(cancelledRoot);
        var fixture = new RuntimeFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => Task.Run(() =>
            RoutingApplicationLifecycle.Create(
                Store(cancelledRoot),
                fixture.Ownership,
                fixture.Legacy,
                fixture.Routing,
                cancellation.Token)));
        Assert(fixture.Ownership.Owner is null && fixture.Events.Count == 0,
            "Cancelled authority inspection must occur before ownership or runtime activity.");
    }

    private static async Task AssertBlockedAsync(
        RoutingExecutionAuthorityStore store,
        RoutingDocumentLoadStatus expectedStatus)
    {
        var fixture = new RuntimeFixture();
        var lifecycle = RoutingApplicationLifecycle.Create(
            store,
            fixture.Ownership,
            fixture.Legacy,
            fixture.Routing);
        var start = await lifecycle.StartAsync();
        Assert(lifecycle.AuthorityInspection.Blocked &&
               lifecycle.AuthorityInspection.LoadStatus == expectedStatus &&
               lifecycle.State == RoutingApplicationLifecycleState.NeedsAttention &&
               lifecycle.Coordinator is null &&
               start.Status == RoutingApplicationLifecycleStatus.NeedsAttention &&
               fixture.Ownership.Owner is null && fixture.Events.Count == 0,
            $"{expectedStatus} authority must acquire and start neither runtime.");
    }

    private static async Task AssertCancellationAndFailuresRemainFencedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var cancelledFixture = new RuntimeFixture();
        var cancelledLifecycle = RoutingApplicationLifecycle.Create(
            Store(Path.Combine(root, "cancelled-legacy")),
            cancelledFixture.Ownership,
            cancelledFixture.Legacy,
            cancelledFixture.Routing);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() =>
                cancelledLifecycle.StartAsync(cancellation.Token));
        }
        Assert(cancelledLifecycle.State == RoutingApplicationLifecycleState.LegacyReady &&
               cancelledFixture.Legacy.StartCalls == 0 &&
               cancelledFixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
            "Pre-start cancellation must leave the selected Legacy lease retryable without touching a runtime.");
        _ = await cancelledLifecycle.StopAsync();
        Assert(cancelledFixture.Ownership.Owner is null,
            "Stopping before Legacy starts must release the unadopted startup lease.");

        var failedLegacy = new RuntimeFixture();
        failedLegacy.Legacy.StartError = new InvalidOperationException("legacy start failed");
        var failedLifecycle = RoutingApplicationLifecycle.Create(
            Store(Path.Combine(root, "failed-legacy")),
            failedLegacy.Ownership,
            failedLegacy.Legacy,
            failedLegacy.Routing);
        var failed = await failedLifecycle.StartAsync();
        Assert(failed.Status == RoutingApplicationLifecycleStatus.StartFailed &&
               failedLifecycle.State == RoutingApplicationLifecycleState.LegacyReady &&
               failedLegacy.Legacy.StopCalls == 1 &&
               failedLegacy.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy &&
               failedLegacy.Routing.StartCalls == 0,
            "A failed Legacy start must quiesce partial work and replace only its unstarted Legacy lease for retry.");
        failedLegacy.Legacy.StartError = null;
        Assert((await failedLifecycle.StartAsync()).Status ==
               RoutingApplicationLifecycleStatus.LegacyStarted,
            "A clean Legacy start failure must remain retryable.");

        var stickyRoot = Path.Combine(root, "sticky-routing");
        var stickyStore = Store(stickyRoot);
        var stickyCaptureLibraryBinding = await CommitAuthorityAsync(
            stickyStore,
            Path.Combine(stickyRoot, "clips"));
        var failedRouting = new RuntimeFixture();
        failedRouting.Routing.StartError = new InvalidOperationException("routing start failed");
        var stickyLifecycle = RoutingApplicationLifecycle.Create(
            stickyStore,
            failedRouting.Ownership,
            failedRouting.Legacy,
            failedRouting.Routing,
            currentCaptureLibraryBinding: () => stickyCaptureLibraryBinding);
        var routingFailure = await stickyLifecycle.StartAsync();
        Assert(routingFailure.Status == RoutingApplicationLifecycleStatus.NeedsAttention &&
               stickyLifecycle.State == RoutingApplicationLifecycleState.RoutingRecoveryNeeded &&
               failedRouting.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
               failedRouting.Legacy.StartCalls == 0 && failedRouting.Legacy.StopCalls == 0 &&
               failedRouting.Routing.StopCalls == 1,
            "A cold Routing start failure must quiesce and retain Routing without touching Legacy.");
        failedRouting.Routing.StartError = null;
        Assert((await stickyLifecycle.StartAsync()).Status ==
                   RoutingApplicationLifecycleStatus.RoutingStarted &&
               failedRouting.Routing.PrepareCalls == 0 &&
               failedRouting.Routing.CommitCalls == 2 &&
               failedRouting.Legacy.StartCalls == 0,
            "Recovery must retry Routing under the same sticky authority without migration preparation or Legacy.");
    }

    private static async Task AssertStickySettingsGuardAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = Store(root);
        var clips = Path.Combine(root, "clips");
        _ = await CommitAuthorityAsync(store, clips);
        var authority = store.Inspect();
        var current = Settings(clips);
        var safe = current with
        {
            StartWithWindows = !current.StartWithWindows,
            CompressionTargetMb = 72,
            UploaderName = "Another uploader",
            ModeToggleHotkey = "Ctrl+F10"
        };
        Assert(RoutingSettingsAuthorityGuard.Evaluate(authority, current, safe).Allowed,
            "Startup, compression, uploader-name, and hotkey changes must remain safe under Routing authority.");

        var protectedCandidate = current with
        {
            ClipsFolder = Path.Combine(root, "other-clips"),
            CaptureSource = ClipCaptureSource.Nvidia,
            UploadToDiscord = !current.UploadToDiscord,
            WebhookUrl = current.WebhookUrl + "-different"
        };
        var protectedResult = RoutingSettingsAuthorityGuard.Evaluate(
            authority, current, protectedCandidate);
        Assert(protectedResult.Status == RoutingSettingsGuardStatus.RequiresSwitchTransaction &&
               protectedResult.ProtectedChanges ==
               (RoutingProtectedSetting.WatchedClipsFolder |
                RoutingProtectedSetting.CaptureSource |
                RoutingProtectedSetting.UploadToDiscord |
                RoutingProtectedSetting.LegacyWebhookIdentity),
            "Every source/destination field must require a future durable switch transaction after Routing authority.");

        var missing = Store(Path.Combine(root, "missing")).Inspect();
        Assert(RoutingSettingsAuthorityGuard.Evaluate(
                   missing, current, protectedCandidate).Allowed,
            "Before Routing authority, the existing Legacy settings flow may change protected fields.");
        var blocked = new RoutingExecutionAuthorityInspection(
            RoutingExecutionAuthorityInspectionState.Blocked,
            RoutingDocumentLoadStatus.Corrupt,
            null);
        Assert(RoutingSettingsAuthorityGuard.Evaluate(blocked, current, safe).Allowed &&
               RoutingSettingsAuthorityGuard.Evaluate(
                   blocked, current, protectedCandidate).Status ==
               RoutingSettingsGuardStatus.AuthorityNeedsAttention,
            "Unreadable authority must allow unrelated settings but fail closed for source/destination changes.");
    }

    private static RoutingExecutionAuthorityStore Store(string root) => new(
        Path.Combine(root, "routing-state", RoutingExecutionAuthorityStore.FileName));

    private static async Task<RoutingCaptureLibraryBinding> CommitAuthorityAsync(
        RoutingExecutionAuthorityStore store,
        string clips)
    {
        Directory.CreateDirectory(clips);
        var captureLibraryRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(clips))!,
            "capture-library");
        Directory.CreateDirectory(captureLibraryRoot);
        var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(
            captureLibraryRoot);
        var settings = Settings(clips);
        var state = new WatchState
        {
            Version = 4,
            ClipsFolder = clips,
            CaptureSource = ClipCaptureSource.SteelSeriesGg,
            KnownContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            UploadedContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            LocalOnlyContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            IgnoredFileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingLocalOnlyMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingEditedUploads = []
        };
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(
            new LegacyRoutingMigrationInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                DiscordConnectionIds: [],
                captureLibraryBinding),
            Now);
        Assert(readiness.CanCommit && readiness.Plan is not null,
            "The lifecycle authority fixture requires a valid drained migration.");
        var marker = LegacyRoutingMigrationMarkerModel.Commit(
            LegacyRoutingMigrationMarkerModel.CreatePrepared(readiness.Plan!, Now),
            Now.AddSeconds(1));
        await store.CommitAsync(RoutingExecutionAuthorityModel.Create(
            marker,
            ClipCaptureSource.SteelSeriesGg,
            Now.AddSeconds(2)));
        return captureLibraryBinding;
    }

    private static AppSettings Settings(string clips) => new(
        clips,
        "https://discord.com/api/webhooks/123456789012345678/lifecycle-secret-token",
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Lifecycle tester",
        UploadToDiscord: false,
        GlobalHotkeyBinding.DefaultDisplayText,
        ClipCaptureSource.SteelSeriesGg);

    private sealed class RuntimeFixture
    {
        internal RuntimeFixture()
        {
            Legacy = new FakeLegacyRuntime(Events);
            Routing = new FakeRoutingRuntime(Events);
        }

        internal List<string> Events { get; } = [];
        internal ClipProcessingOwnershipCoordinator Ownership { get; } = new();
        internal FakeLegacyRuntime Legacy { get; }
        internal FakeRoutingRuntime Routing { get; }
    }

    private sealed class FakeLegacyRuntime(List<string> events) : ILegacyClipProcessingRuntime
    {
        private ClipProcessingOwnershipLease? _owned;
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal Exception? StartError { get; set; }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            events.Add("legacy-start");
            cancellationToken.ThrowIfCancellationRequested();
            if (StartError is not null) throw StartError;
            if (ownership.Owner != ClipProcessingRuntimeOwner.Legacy ||
                !ownership.IsCurrent ||
                !ownership.TryReissue(out var adopted) || adopted is null)
            {
                throw new InvalidOperationException(
                    "The fake Legacy runtime could not adopt its lease.");
            }
            _owned = adopted;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            events.Add("legacy-stop");
            _owned?.Dispose();
            _owned = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRoutingRuntime(List<string> events) : IRoutingClipProcessingRuntime
    {
        internal int PrepareCalls { get; private set; }
        internal int CommitCalls { get; private set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal Exception? PrepareError { get; set; }
        internal Exception? StartError { get; set; }

        public ValueTask PrepareActivationAsync(CancellationToken cancellationToken)
        {
            PrepareCalls++;
            events.Add("routing-prepare");
            cancellationToken.ThrowIfCancellationRequested();
            if (PrepareError is not null) throw PrepareError;
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitExecutionAuthorityAsync(
            ClipProcessingOwnershipLease ownership)
        {
            CommitCalls++;
            events.Add("routing-commit");
            Assert(ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   ownership.IsCurrent,
                "Routing authority commit requires current Routing ownership.");
            return ValueTask.CompletedTask;
        }

        public RoutingRuntimeGateInspection InspectActivation(
            ClipProcessingOwnershipLease ownership) => new(
                RoutingRuntimeGateState.Enabled,
                0,
                0,
                0,
                0,
                RoutingGeneration: 1,
                MarkerPayloadFingerprint: new string('A', 64),
                OwnershipEpoch: ownership.Epoch,
                RequiredLegacySource: ClipCaptureSource.SteelSeriesGg,
                CoveredLegacySources: new HashSet<ClipCaptureSource>
                {
                    ClipCaptureSource.SteelSeriesGg,
                    ClipCaptureSource.Nvidia
                },
                ExecutionAuthorityActivationId:
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            events.Add("routing-start");
            cancellationToken.ThrowIfCancellationRequested();
            if (StartError is not null) throw StartError;
            Assert(ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   ownership.IsCurrent,
                "Routing start requires current Routing ownership.");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            events.Add("routing-stop");
            return ValueTask.CompletedTask;
        }
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
