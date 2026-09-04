using ClipsToDiscord;

internal static class TrayProcessingOperationGateTests
{
    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertStartupMustSettleBeforeMutationAsync();
        await AssertMutationsAreExclusiveAsync();
        AssertSynchronousMutationFailsFastWhileBusy();
        await AssertCancellationAndStartupFailurePropagateAsync();
        await AssertAuthorityAwareStartupRecoveryOrderingAsync();
        await AssertRetryRestartsLegacyBeforeActivationAsync();
        AssertAdmissionPolicyFailsClosed();
        AssertRuntimeViewPresentationTracksRealTransitions();
        AssertFreshPreparedRouteDraftCanBeReconstructed();
        await AssertRoutingManualOperationQuiescesRecoversAndRestartsAsync();
        await AssertBlockedAuthorityManualOperationFailsClosedAsync(
            Path.Combine(testRoot, "blocked-authority"));
        AssertOperationalAuthorityExtendsCommittedMigrationFence(
            Path.Combine(testRoot, "committed-migration-fence"));
        AssertCaptureLibraryRootAuthorityGuard(
            Path.Combine(testRoot, "capture-library-authority"));
        await AssertConfirmedDispositionRecoveryIsLocalOnlyAsync(
            Path.Combine(testRoot, "confirmed-disposition"));
        await AssertFailedDispositionRecoveryRemainsPendingAsync(
            Path.Combine(testRoot, "failed-disposition"));
        AssertShutdownFallbacksContinueAfterFailures();
        AssertSettingsSavedMessageMatchesRoutingLifecycle();
    }

    private static async Task AssertStartupMustSettleBeforeMutationAsync()
    {
        var gate = new TrayProcessingOperationGate();
        var startup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetStartup(startup.Task);

        var entry = gate.EnterAsync(CancellationToken.None).AsTask();
        Assert(!entry.IsCompleted,
            "A settings or manual operation must not overlap the retained startup cutover.");

        startup.TrySetResult();
        using var lease = await entry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(entry.IsCompletedSuccessfully,
            "The first mutation did not enter after startup settled successfully.");
    }

    private static async Task AssertMutationsAreExclusiveAsync()
    {
        var gate = new TrayProcessingOperationGate();
        gate.SetStartup(Task.CompletedTask);
        var first = await gate.EnterAsync(CancellationToken.None);

        var secondEntry = gate.EnterAsync(CancellationToken.None).AsTask();
        Assert(!secondEntry.IsCompleted,
            "Settings, manual upload, and Capture-root mutations must share one exclusive lease.");

        first.Dispose();
        using var second = await secondEntry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(secondEntry.IsCompletedSuccessfully,
            "A queued mutation did not enter after the prior mutation released its lease.");
    }

    private static void AssertSynchronousMutationFailsFastWhileBusy()
    {
        var gate = new TrayProcessingOperationGate();
        gate.SetStartup(Task.CompletedTask);
        var active = gate.EnterSynchronously(CancellationToken.None);

        AssertThrows<InvalidOperationException>(() =>
            gate.EnterSynchronously(CancellationToken.None).Dispose());

        active.Dispose();
        using var afterRelease = gate.EnterSynchronously(CancellationToken.None);
    }

    private static async Task AssertCancellationAndStartupFailurePropagateAsync()
    {
        var gate = new TrayProcessingOperationGate();
        gate.SetStartup(Task.CompletedTask);
        using var active = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(async () =>
        {
            using var _ = await gate.EnterAsync(cancellation.Token);
        });

        var failedGate = new TrayProcessingOperationGate();
        var startupFailure = new InvalidDataException("startup-cutover-failed");
        failedGate.SetStartup(Task.FromException(startupFailure));
        Exception? observed = null;
        try
        {
            using var _ = await failedGate.EnterAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Assert(ReferenceEquals(observed, startupFailure),
            "A failed startup cutover must fail closed before a later mutation can enter.");
    }

    private static async Task AssertRoutingManualOperationQuiescesRecoversAndRestartsAsync()
    {
        var events = new List<string>();
        var result = await TrayRoutingExclusiveOperation.RunAsync(
            Authority(RoutingExecutionAuthorityInspectionState.RoutingRequired),
            _ =>
            {
                events.Add("routing-stop");
                return Task.CompletedTask;
            },
            _ =>
            {
                events.Add("manual-upload");
                return Task.FromResult("confirmed");
            },
            () =>
            {
                events.Add("recover-confirmed-dispositions");
                events.Add("capture-recovery");
                events.Add("routing-start");
                return Task.CompletedTask;
            },
            _ => events.Add("restart-failure"),
            CancellationToken.None);
        Assert(result == "confirmed" && events.SequenceEqual([
                   "routing-stop",
                   "manual-upload",
                   "recover-confirmed-dispositions",
                   "capture-recovery",
                   "routing-start"
               ]),
            "A Routing-owned manual upload must quiesce, finish the upload, recover durable local state, and restart Routing in order.");

        events.Clear();
        var operationFailure = new InvalidDataException("manual-operation-failed");
        Exception? observed = null;
        try
        {
            _ = await TrayRoutingExclusiveOperation.RunAsync<string>(
                Authority(RoutingExecutionAuthorityInspectionState.RoutingRequired),
                _ =>
                {
                    events.Add("routing-stop");
                    return Task.CompletedTask;
                },
                _ =>
                {
                    events.Add("manual-upload");
                    return Task.FromException<string>(operationFailure);
                },
                () =>
                {
                    events.Add("routing-recovery");
                    return Task.CompletedTask;
                },
                _ => events.Add("restart-failure"),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Assert(ReferenceEquals(observed, operationFailure) && events.SequenceEqual([
                   "routing-stop", "manual-upload", "routing-recovery"
               ]),
            "Once Routing is quiesced, failure or cancellation of the manual operation must still converge local state and restart without replacing the original error.");

        events.Clear();
        var restartFailure = new IOException("routing-restart-failed");
        Exception? reported = null;
        var authoritative = await TrayRoutingExclusiveOperation.RunAsync(
            Authority(RoutingExecutionAuthorityInspectionState.RoutingRequired),
            _ => Task.CompletedTask,
            _ => Task.FromResult(42),
            () => Task.FromException(restartFailure),
            exception => reported = exception,
            CancellationToken.None);
        Assert(authoritative == 42 && ReferenceEquals(reported, restartFailure),
            "A restart failure must be reported without turning a completed manual upload into an apparent upload failure.");

        events.Clear();
        _ = await TrayRoutingExclusiveOperation.RunAsync(
            Authority(RoutingExecutionAuthorityInspectionState.LegacyPermitted),
            _ =>
            {
                events.Add("routing-stop");
                return Task.CompletedTask;
            },
            _ =>
            {
                events.Add("legacy-operation");
                return Task.FromResult(1);
            },
            () =>
            {
                events.Add("routing-start");
                return Task.CompletedTask;
            },
            _ => { },
            CancellationToken.None);
        Assert(events.SequenceEqual(["legacy-operation"]),
            "The wrapper must not stop or start Routing when durable authority still permits Legacy.");
    }

    private static async Task AssertBlockedAuthorityManualOperationFailsClosedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clipPath = Path.Combine(root, "prepared-edit.mp4");
        var statePath = Path.Combine(root, "manual-edit-state.json");
        var clipBefore = new byte[] { 0x43, 0x4C, 0x49, 0x50, 0x01, 0x02 };
        var stateBefore = "{\"pending\":[\"sentinel\"],\"generation\":7}"u8.ToArray();
        await File.WriteAllBytesAsync(clipPath, clipBefore);
        await File.WriteAllBytesAsync(statePath, stateBefore);
        var callbacks = new List<string>();

        await AssertThrowsAsync<InvalidDataException>(async () =>
        {
            _ = await TrayRoutingExclusiveOperation.RunAsync(
                Authority(RoutingExecutionAuthorityInspectionState.Blocked),
                _ =>
                {
                    callbacks.Add("routing-stop");
                    File.WriteAllBytes(clipPath, [0x00]);
                    return Task.CompletedTask;
                },
                _ =>
                {
                    callbacks.Add("upload");
                    File.WriteAllBytes(statePath, [0x00]);
                    return Task.FromResult("uploaded");
                },
                () =>
                {
                    callbacks.Add("routing-restart");
                    File.WriteAllBytes(statePath, [0x01]);
                    return Task.CompletedTask;
                },
                _ =>
                {
                    callbacks.Add("restart-report");
                    File.WriteAllBytes(statePath, [0x02]);
                },
                CancellationToken.None);
        });

        var clipAfter = await File.ReadAllBytesAsync(clipPath);
        var stateAfter = await File.ReadAllBytesAsync(statePath);
        Assert(callbacks.Count == 0 &&
               clipAfter.SequenceEqual(clipBefore) &&
               stateAfter.SequenceEqual(stateBefore),
            "Blocked Routing authority must fail before any upload, stop/restart callback, or durable clip/state mutation.");
    }

    private static void AssertCaptureLibraryRootAuthorityGuard(string root)
    {
        var oldRoot = Directory.CreateDirectory(Path.Combine(root, "old-library")).FullName;
        var requestedRoot = Directory.CreateDirectory(Path.Combine(root, "new-library")).FullName;
        var oldArtifactPath = Path.Combine(oldRoot, "existing-capture.mp4");
        var outboxPath = Path.Combine(oldRoot, "routing-outbox.json");
        var artifactBefore = new byte[] { 0x43, 0x41, 0x50, 0x54, 0x55, 0x52, 0x45 };
        var outboxBefore = "{\"plans\":[\"sentinel\"],\"generation\":11}"u8.ToArray();
        File.WriteAllBytes(oldArtifactPath, artifactBefore);
        File.WriteAllBytes(outboxPath, outboxBefore);
        var mutations = 0;

        void AttemptRootChange(RoutingExecutionAuthorityInspection authority)
        {
            var changed = TrayCaptureLibraryRootAuthorityGuard.RequireAllowed(
                oldRoot,
                requestedRoot,
                authority);
            if (!changed) return;
            mutations++;
            File.WriteAllBytes(oldArtifactPath, [0x00]);
            File.WriteAllBytes(outboxPath, [0x00]);
        }

        AssertThrows<InvalidDataException>(() => AttemptRootChange(
            Authority(RoutingExecutionAuthorityInspectionState.Blocked)));
        Assert(mutations == 0 &&
               File.ReadAllBytes(oldArtifactPath).SequenceEqual(artifactBefore) &&
               File.ReadAllBytes(outboxPath).SequenceEqual(outboxBefore),
            "Blocked authority must reject a Capture LibraryRoot change before settings, hotkey, runtime, capture, or outbox mutation.");

        var routingChange = TrayCaptureLibraryRootAuthorityGuard.RequireAllowed(
            oldRoot,
            requestedRoot,
            Authority(RoutingExecutionAuthorityInspectionState.RoutingRequired));
        Assert(routingChange && mutations == 0 &&
               File.ReadAllBytes(oldArtifactPath).SequenceEqual(artifactBefore) &&
               File.ReadAllBytes(outboxPath).SequenceEqual(outboxBefore),
            "Committed Routing authority must admit a Capture LibraryRoot change only to the separate durable switch transaction.");

        var unchanged = TrayCaptureLibraryRootAuthorityGuard.RequireAllowed(
            oldRoot,
            oldRoot + Path.DirectorySeparatorChar,
            Authority(RoutingExecutionAuthorityInspectionState.RoutingRequired));
        Assert(!unchanged,
            "Committed Routing authority must still allow Capture setting changes that keep the canonical LibraryRoot unchanged.");

        AttemptRootChange(Authority(
            RoutingExecutionAuthorityInspectionState.LegacyPermitted));
        Assert(mutations == 1 &&
               File.ReadAllBytes(oldArtifactPath).AsSpan().SequenceEqual(new byte[] { 0x00 }) &&
               File.ReadAllBytes(outboxPath).AsSpan().SequenceEqual(new byte[] { 0x00 }),
            "Legacy-permitted authority must continue allowing Capture LibraryRoot changes.");
    }

    private static async Task AssertAuthorityAwareStartupRecoveryOrderingAsync()
    {
        var events = new List<string>();
        var legacyRecoveryFailure = new IOException("capture-recovery-failed");
        var missingAuthority = await TrayRoutingStartupSequence.RunAsync(
            routingRequired: false,
            _ =>
            {
                events.Add("recovery");
                return Task.FromException(legacyRecoveryFailure);
            },
            _ =>
            {
                events.Add("legacy-start");
                return Task.FromResult("legacy-running");
            },
            result => result == "legacy-running",
            _ =>
            {
                events.Add("routing-activate");
                return Task.FromResult("routing-running");
            },
            CancellationToken.None);
        Assert(missingAuthority.Result == "legacy-running" &&
               !missingAuthority.ActivationAttempted &&
               ReferenceEquals(missingAuthority.RecoveryError, legacyRecoveryFailure) &&
               events.SequenceEqual(["legacy-start", "recovery"]),
            "With missing authority, Legacy must start first and remain running when Capture recovery fails; Routing activation must not be attempted.");

        events.Clear();
        var successfulCutover = await TrayRoutingStartupSequence.RunAsync(
            routingRequired: false,
            _ =>
            {
                events.Add("recovery");
                return Task.CompletedTask;
            },
            _ =>
            {
                events.Add("legacy-start");
                return Task.FromResult("legacy-running");
            },
            result => result == "legacy-running",
            _ =>
            {
                events.Add("routing-activate");
                return Task.FromResult("routing-running");
            },
            CancellationToken.None);
        Assert(successfulCutover.Result == "routing-running" &&
               successfulCutover.ActivationAttempted &&
               successfulCutover.RecoveryError is null &&
               events.SequenceEqual(["legacy-start", "recovery", "routing-activate"]),
            "First activation must keep Legacy authoritative until recovery succeeds, then cut over exactly once.");

        events.Clear();
        var committedRecoveryFailure = new InvalidDataException(
            "committed-routing-recovery-failed");
        Exception? observed = null;
        try
        {
            _ = await TrayRoutingStartupSequence.RunAsync(
                routingRequired: true,
                _ =>
                {
                    events.Add("recovery");
                    return Task.FromException(committedRecoveryFailure);
                },
                _ =>
                {
                    events.Add("routing-start");
                    return Task.FromResult("routing-running");
                },
                _ => false,
                _ => throw new InvalidOperationException(
                    "Committed Routing must not use the first-activation callback."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Assert(ReferenceEquals(observed, committedRecoveryFailure) &&
               events.SequenceEqual(["recovery"]),
            "Committed Routing must require successful Capture and confirmed-disposition recovery before its runtime start callback is entered.");

        events.Clear();
        var committed = await TrayRoutingStartupSequence.RunAsync(
            routingRequired: true,
            _ =>
            {
                events.Add("recovery");
                return Task.CompletedTask;
            },
            _ =>
            {
                events.Add("routing-start");
                return Task.FromResult("routing-running");
            },
            _ => false,
            _ => throw new InvalidOperationException(
                "Committed Routing must not use the first-activation callback."),
            CancellationToken.None);
        Assert(committed.Result == "routing-running" &&
               !committed.ActivationAttempted && committed.RecoveryError is null &&
               events.SequenceEqual(["recovery", "routing-start"]),
            "Committed Routing must start only after recovery finishes successfully.");
    }

    private static async Task AssertRetryRestartsLegacyBeforeActivationAsync()
    {
        var events = new List<string>();
        var legacyReady = true;
        var retry = await TrayRoutingRetrySequence.RunAsync(
            _ =>
            {
                events.Add("recovery");
                return Task.CompletedTask;
            },
            () => legacyReady,
            _ =>
            {
                events.Add("legacy-start");
                legacyReady = false;
                return Task.FromResult("legacy-running");
            },
            result => result == "legacy-running",
            _ =>
            {
                events.Add("routing-activate");
                return Task.FromResult("routing-running");
            },
            CancellationToken.None);
        Assert(retry.Result == "routing-running" && retry.ActivationAttempted &&
               events.SequenceEqual(["recovery", "legacy-start", "routing-activate"]),
            "A user retry from LegacyReady must recover, start Legacy under its retained lease, and only then attempt Routing activation.");

        events.Clear();
        legacyReady = true;
        var failedStart = await TrayRoutingRetrySequence.RunAsync(
            _ =>
            {
                events.Add("recovery");
                return Task.CompletedTask;
            },
            () => legacyReady,
            _ =>
            {
                events.Add("legacy-start");
                return Task.FromResult("legacy-ready");
            },
            result => result == "legacy-running",
            _ =>
            {
                events.Add("routing-activate");
                return Task.FromResult("routing-running");
            },
            CancellationToken.None);
        Assert(failedStart.Result == "legacy-ready" &&
               !failedStart.ActivationAttempted &&
               events.SequenceEqual(["recovery", "legacy-start"]),
            "A failed Legacy restart must remain retryable without attempting an invalid Routing activation.");

        events.Clear();
        legacyReady = false;
        var runningLegacy = await TrayRoutingRetrySequence.RunAsync(
            _ =>
            {
                events.Add("recovery");
                return Task.CompletedTask;
            },
            () => legacyReady,
            _ => throw new InvalidOperationException(
                "An already-running Legacy runtime must not be started twice."),
            _ => false,
            _ =>
            {
                events.Add("routing-activate");
                return Task.FromResult("routing-running");
            },
            CancellationToken.None);
        Assert(runningLegacy.Result == "routing-running" &&
               runningLegacy.ActivationAttempted &&
               events.SequenceEqual(["recovery", "routing-activate"]),
            "A retry from LegacyRunning must recover and activate without starting a competing watcher.");
    }

    private static void AssertRuntimeViewPresentationTracksRealTransitions()
    {
        Assert(TrayRoutesRuntimePresentation.Map(
                   startupRunning: true,
                   RoutingApplicationLifecycleState.LegacyRunning,
                   LegacyRoutingMigrationAdmission.ValidLegacyUpgrade) ==
               RoutesRuntimeViewState.Activating,
            "An in-flight startup or manual retry must stay visibly Activating even while Lifecycle has not yet published its final state.");
        Assert(TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.LegacyReady,
                   LegacyRoutingMigrationAdmission.ValidLegacyUpgrade) ==
               RoutesRuntimeViewState.LegacyActive,
            "A valid legacy upgrade must expose migration retry, never the fresh-profile planner.");
        Assert(TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.SetupReady,
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile) ==
               RoutesRuntimeViewState.LegacySetupNeeded &&
               TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.LegacyReady,
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile) ==
               RoutesRuntimeViewState.LegacySetupNeeded,
            "Only a terminal fresh-profile decision in a setup-ready lifecycle may expose the guided first-route planner.");
        Assert(TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.LegacyRunning,
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile) ==
               RoutesRuntimeViewState.LegacyActive &&
               TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.LegacyReady,
                   LegacyRoutingMigrationAdmission.Deferred) ==
               RoutesRuntimeViewState.LegacyActive &&
               TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.SetupReady,
                   LegacyRoutingMigrationAdmission.Deferred) ==
               RoutesRuntimeViewState.Blocked &&
               TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.LegacyRunning,
                   LegacyRoutingMigrationAdmission.ValidLegacyUpgrade) ==
               RoutesRuntimeViewState.LegacyActive &&
               TrayRoutesRuntimePresentation.Map(
                   startupRunning: false,
                   RoutingApplicationLifecycleState.RoutingRunning,
                   LegacyRoutingMigrationAdmission.ValidLegacyUpgrade) ==
               RoutesRuntimeViewState.Active,
            "Settled runtime presentation must distinguish owners and keep ambiguous admission away from fresh setup.");
        Assert(TrayRoutesRuntimePresentation.IsActivationStatus(
                   "Routes activating — safely preparing existing clips") &&
               !TrayRoutesRuntimePresentation.IsActivationStatus(
                   "Discord open — watching for clips"),
            "A failed retry may restore its prior watcher status only while the temporary activation status is still current.");
    }

    private static void AssertAdmissionPolicyFailsClosed()
    {
        foreach (var status in Enum.GetValues<RoutingDocumentLoadStatus>()
                     .Where(status => status != RoutingDocumentLoadStatus.Loaded))
        {
            Assert(!TrayRoutingAdmissionPolicy.AllowsLegacyRuntime(
                    new LegacyRoutingMigrationAdmissionInspection(status, Document: null)),
                $"A non-loaded {status} admission must not authorize the Legacy runtime.");
        }

        var timestamp = new DateTimeOffset(2026, 9, 2, 16, 0, 0, TimeSpan.Zero);
        LegacyRoutingMigrationAdmissionDocument Document(
            LegacyRoutingMigrationAdmission admission) => new(
            LegacyRoutingMigrationAdmissionStore.CurrentSchemaVersion,
            Generation: 1,
            admission,
            LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion,
            timestamp);

        var fresh = new LegacyRoutingMigrationAdmissionInspection(
            RoutingDocumentLoadStatus.Loaded,
            Document(LegacyRoutingMigrationAdmission.FreshOrInvalidProfile));
        var legacy = new LegacyRoutingMigrationAdmissionInspection(
            RoutingDocumentLoadStatus.Loaded,
            Document(LegacyRoutingMigrationAdmission.ValidLegacyUpgrade));
        Assert(!TrayRoutingAdmissionPolicy.AllowsLegacyRuntime(fresh) &&
               TrayRoutingAdmissionPolicy.AllowsLegacyRuntime(legacy),
            "Only a loaded, durable positive legacy admission may authorize the compatibility watcher.");
    }

    private static void AssertFreshPreparedRouteDraftCanBeReconstructed()
    {
        const string sourceId = "source.0123456789abcdef0123456789abcdef";
        var original = new RoutingRouteDraft(
            "NVIDIA highlights → Discord",
            RoutingTriggerKind.WatchedFolder,
            "Halo Infinite",
            RoutingDestinationKind.Discord,
            "discord.0123456789abcdef0123456789abcdef",
            RoutingOutputKind.Landscape,
            RoutingDeliveryMode.Approval,
            RoutingMissingOutputBehavior.NeedsAttention,
            FileIntoLibrary: true,
            WatchedSourceId: sourceId,
            WatchedSourceKind: RoutingInputSourceKind.Nvidia);
        var route = RoutingRouteManager.CreateRoute(
            original,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            [
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("33333333-3333-3333-3333-333333333333")
            ],
            [
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                Guid.Parse("55555555-5555-5555-5555-555555555555")
            ],
            priority: 0,
            createdUtc: new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

        var reconstructed = TrayRoutingActivationPreparer.ReconstructFreshRouteDraft(
            route,
            id => id == sourceId ? RoutingInputSourceKind.Nvidia : null);
        Assert(reconstructed == original,
            "A prepared fresh marker must reconstruct its semantic route draft after restart without retaining the editor callback.");
    }

    private static async Task AssertConfirmedDispositionRecoveryIsLocalOnlyAsync(string root)
    {
        var fixture = await PendingDispositionFixture.CreateAsync(root, occupyDestination: false);
        var recovery = new TrayPendingEditedDispositionRecovery(
            fixture.Store,
            new EditedClipDispositionProcessor(fixture.Recycler));

        var recovered = await recovery.RecoverAsync(fixture.Settings);
        var state = await fixture.LoadStateAsync();
        Assert(recovered == 1 && state.PendingEditedUploads.Count == 0 &&
               File.Exists(fixture.DestinationPath) &&
               !File.Exists(fixture.EditedPath) &&
               !File.Exists(fixture.OriginalPath) &&
               fixture.Recycler.RecycledPaths.SequenceEqual([fixture.OriginalPath]),
            "Confirmed-edit recovery must only complete the durable move and original cleanup, then remove the pending record.");
    }

    private static async Task AssertFailedDispositionRecoveryRemainsPendingAsync(string root)
    {
        var fixture = await PendingDispositionFixture.CreateAsync(root, occupyDestination: true);
        var recovery = new TrayPendingEditedDispositionRecovery(
            fixture.Store,
            new EditedClipDispositionProcessor(fixture.Recycler));

        await AssertThrowsAsync<InvalidOperationException>(() =>
            recovery.RecoverAsync(fixture.Settings));
        var state = await fixture.LoadStateAsync();
        Assert(state.PendingEditedUploads.Single().Id == fixture.PendingId &&
               File.Exists(fixture.EditedPath) &&
               File.Exists(fixture.OriginalPath) &&
               fixture.Recycler.RecycledPaths.Count == 0,
            "A failed local disposition must retain the exact pending record and both source artifacts for retry without another Discord send.");
    }

    private static RoutingExecutionAuthorityInspection Authority(
        RoutingExecutionAuthorityInspectionState state) => new(
        state,
        state switch
        {
            RoutingExecutionAuthorityInspectionState.LegacyPermitted =>
                RoutingDocumentLoadStatus.Missing,
            RoutingExecutionAuthorityInspectionState.RoutingRequired =>
                RoutingDocumentLoadStatus.Loaded,
            _ => RoutingDocumentLoadStatus.Corrupt
        },
        Document: null);

    private static void AssertOperationalAuthorityExtendsCommittedMigrationFence(
        string root)
    {
        var persisted = Authority(
            RoutingExecutionAuthorityInspectionState.LegacyPermitted);
        var legacy = TrayRoutingOperationalAuthority.Evaluate(
            persisted,
            legacyOperationsPermitted: true);
        var fenced = TrayRoutingOperationalAuthority.Evaluate(
            persisted,
            legacyOperationsPermitted: false);
        Assert(ReferenceEquals(legacy, persisted) &&
               fenced.Blocked &&
               fenced.LoadStatus == RoutingDocumentLoadStatus.Unavailable &&
               fenced.Document is null,
            "A committed migration fence must override a still-missing execution-authority file for every legacy-only tray operation.");

        AssertThrows<InvalidDataException>(() =>
            TrayRoutingExclusiveOperation.RequireManualUploadAllowed(fenced));
        var currentRoot = Directory.CreateDirectory(
            Path.Combine(root, "current")).FullName;
        var requestedRoot = Directory.CreateDirectory(
            Path.Combine(root, "requested")).FullName;
        AssertThrows<InvalidDataException>(() =>
            TrayCaptureLibraryRootAuthorityGuard.RequireAllowed(
                currentRoot,
                requestedRoot,
                fenced));
        Assert(!TrayCaptureLibraryRootAuthorityGuard.RequireAllowed(
                currentRoot,
                currentRoot + Path.DirectorySeparatorChar,
                fenced),
            "A committed migration fence may allow unrelated Capture settings only when the canonical library root is unchanged.");
    }

    private static void AssertShutdownFallbacksContinueAfterFailures()
    {
        var events = new List<string>();
        var firstFailure = new IOException("routing-session-stop-failed");
        var recoveryFailure = new InvalidDataException("capture-recovery-failed");
        var reported = new List<(string Message, Exception Error)>();

        TrayShutdownFallbacks.Run(
            (message, exception) =>
            {
                events.Add($"reported:{message}");
                reported.Add((message, exception));
            },
            ("routing-session", () =>
            {
                events.Add("routing-session");
                throw firstFailure;
            }),
            ("legacy-controller", () => events.Add("legacy-controller")),
            ("capture-recovery", () =>
            {
                events.Add("capture-recovery");
                throw recoveryFailure;
            }),
            ("startup-observer", () => events.Add("startup-observer")),
            ("session-observer", () => events.Add("session-observer")));

        Assert(events.SequenceEqual([
                   "routing-session",
                   "reported:routing-session",
                   "legacy-controller",
                   "capture-recovery",
                   "reported:capture-recovery",
                   "startup-observer",
                   "session-observer"
               ]) &&
               reported.Count == 2 &&
               reported[0].Message == "routing-session" &&
               ReferenceEquals(reported[0].Error, firstFailure) &&
               reported[1].Message == "capture-recovery" &&
               ReferenceEquals(reported[1].Error, recoveryFailure),
            "Shutdown fallback convergence must report each failure and still run every later stop and observation step in order.");

        events.Clear();
        TrayShutdownFallbacks.Run(
            (_, _) => throw new IOException("diagnostic-report-failed"),
            ("failing-step", () => throw firstFailure),
            ("later-step", () => events.Add("later-step")));
        Assert(events.SequenceEqual(["later-step"]),
            "A failed shutdown diagnostic reporter must not prevent later convergence steps.");
    }

    private static void AssertSettingsSavedMessageMatchesRoutingLifecycle()
    {
        const string activeMessage =
            "Settings saved. Active Routes continue processing new clips.";
        const string attentionMessage =
            "Settings saved, but Routes need attention before clip processing can continue.";

        foreach (var state in Enum.GetValues<RoutingApplicationLifecycleState>())
        {
            var message = TrayApplicationContext.GetSettingsSavedMessage(
                routingRequired: true,
                state,
                uploadToDiscord: true);
            Assert(
                state == RoutingApplicationLifecycleState.SetupReady
                    ? message ==
                      "Settings saved. Create your first route to start clip processing."
                    : state == RoutingApplicationLifecycleState.RoutingRunning
                    ? message == activeMessage
                    : message == attentionMessage &&
                      !message.Contains("Active Routes", StringComparison.Ordinal),
                $"Routing settings notification was not truthful for lifecycle state {state}.");
        }

        Assert(TrayApplicationContext.GetSettingsSavedMessage(
                   routingRequired: false,
                   RoutingApplicationLifecycleState.LegacyRunning,
                   uploadToDiscord: true) ==
               "Settings saved. New clips will upload to Discord." &&
               TrayApplicationContext.GetSettingsSavedMessage(
                   routingRequired: false,
                   RoutingApplicationLifecycleState.LegacyRunning,
                   uploadToDiscord: false) ==
               "Settings saved. Local-only mode will keep new clips on this PC.",
            "Legacy settings notifications must preserve their upload and local-only messages.");
    }

    private sealed class PendingDispositionFixture
    {
        private PendingDispositionFixture(
            AppSettings settings,
            WatchStateStore store,
            RecordingOriginalClipRecycler recycler,
            Guid pendingId,
            string editedPath,
            string originalPath,
            string destinationPath)
        {
            Settings = settings;
            Store = store;
            Recycler = recycler;
            PendingId = pendingId;
            EditedPath = editedPath;
            OriginalPath = originalPath;
            DestinationPath = destinationPath;
        }

        internal AppSettings Settings { get; }
        internal WatchStateStore Store { get; }
        internal RecordingOriginalClipRecycler Recycler { get; }
        internal Guid PendingId { get; }
        internal string EditedPath { get; }
        internal string OriginalPath { get; }
        internal string DestinationPath { get; }

        internal static async Task<PendingDispositionFixture> CreateAsync(
            string root,
            bool occupyDestination)
        {
            Directory.CreateDirectory(root);
            var clipsRoot = Directory.CreateDirectory(
                Path.Combine(root, "clips")).FullName;
            var localGame = Directory.CreateDirectory(Path.Combine(
                UploadedFolder.GetOrCreateLocalOnly(clipsRoot),
                "Recovery Game")).FullName;
            var uploadedGame = UploadedFolder.GetOrCreateForGame(
                clipsRoot,
                "Recovery Game");
            var pendingId = Guid.NewGuid();
            var staging = Directory.CreateDirectory(Path.Combine(
                clipsRoot,
                ".clipcord-editing",
                pendingId.ToString("N"))).FullName;
            var originalPath = Path.Combine(localGame, "Original.mp4");
            var editedPath = Path.Combine(staging, "Edited.mp4");
            var destinationPath = Path.Combine(uploadedGame, "Edited.mp4");
            await File.WriteAllBytesAsync(originalPath, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(editedPath, [5, 6, 7, 8, 9]);
            if (occupyDestination)
            {
                await File.WriteAllBytesAsync(destinationPath, [99, 98, 97]);
            }
            var originalHash = await ContentIdentity.ComputeSha256Async(
                originalPath,
                CancellationToken.None);
            var editedHash = await ContentIdentity.ComputeSha256Async(
                editedPath,
                CancellationToken.None);
            var data = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
            var store = new WatchStateStore(
                Path.Combine(data, "state.json"),
                Path.Combine(data, ".safe-baseline-required"));
            var settings = new AppSettings(
                clipsRoot,
                "no-provider-call-is-valid-for-confirmed-recovery",
                StartWithWindows: false,
                AppSettings.DefaultCompressionTargetMb,
                "Recovery test",
                UploadToDiscord: false,
                ModeToggleHotkey: string.Empty,
                ClipCaptureSource.SteelSeriesGg);
            var state = await store.LoadOrInitializeAsync(
                clipsRoot,
                _ => { },
                CancellationToken.None,
                settings.CaptureSource);
            state.PendingEditedUploads.Add(new PendingEditedClipDisposition
            {
                Id = pendingId,
                ClipsFolder = clipsRoot,
                EditedPath = editedPath,
                DestinationPath = destinationPath,
                OriginalLocalOnlyPath = originalPath,
                EditedContentHash = editedHash,
                OriginalContentHash = originalHash,
                KeepOriginal = false,
                OutputBytes = new FileInfo(editedPath).Length
            });
            store.Save(state);
            var recycleRoot = Directory.CreateDirectory(
                Path.Combine(root, "recycle")).FullName;
            return new PendingDispositionFixture(
                settings,
                store,
                new RecordingOriginalClipRecycler(recycleRoot),
                pendingId,
                editedPath,
                originalPath,
                destinationPath);
        }

        internal Task<WatchState> LoadStateAsync() => Store.LoadOrInitializeAsync(
            Settings.ClipsFolder,
            _ => { },
            CancellationToken.None,
            Settings.CaptureSource);
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
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
