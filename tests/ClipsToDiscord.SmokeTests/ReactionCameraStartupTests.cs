using ClipsToDiscord;

internal static class ReactionCameraStartupTests
{
    internal static void Run()
    {
        AssertManualStartupIsImmediatelyVisible();
        AssertHostStartingSnapshotsRemainAuthoritative();
        AssertReplayCameraFailureReconcilesWithoutFailingGameplay();
        AssertTransportFailureClearsStarting();
        AssertPendingCameraStartupCancelsPromptly();
        AssertStuckOptionalShutdownIsBoundedAndAbandonable();
        AssertManualCameraStopRemainsSingleFlightAfterTimeout();
        AssertFailedCameraLayerCannotCommit();
        AssertReplayCameraStartupJoinsItsFrameSource();
        AssertSilhouettePreferencesAreSnapshottedAtStart();
    }

    private static void AssertSilhouettePreferencesAreSnapshottedAtStart()
    {
        var settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"clipcord-silhouette-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsDirectory);
        try
        {
            var first = SilhouettePreferencesModel.CreateDefault(
                mirrorCamera: true,
                new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
            var firstPortrait = first.Layouts.Single(layout =>
                layout.OrientationId == CompositionOrientationIds.Portrait);
            first = first with
            {
                Layouts = first.Layouts.Select(layout =>
                    layout.OrientationId == CompositionOrientationIds.Portrait
                        ? layout with
                        {
                            Transform = firstPortrait.Transform with
                            {
                                AnchorX = 0.42,
                                AnchorY = 0.71,
                                HeightFraction = 0.29,
                                Preset = SilhouettePlacementPreset.Custom
                            }
                        }
                        : layout).ToArray()
            };
            first = SilhouettePreferencesStore.SaveAsync(
                    settingsDirectory,
                    first,
                    mirrorCamera: true)
                .GetAwaiter()
                .GetResult();

            var manualClient = new DelayedCaptureHostClient();
            using (var recorder = new CaptureHostManualRecorder(
                       manualClient,
                       settingsDirectory))
            {
                recorder.SelectTargetAsync(new FakeWindow()).GetAwaiter().GetResult();
                var start = recorder.StartAsync(CameraSettings());
                var sent = manualClient.LastManualSettings?.SilhouettePreferencesSnapshot;
                Assert(
                    sent is not null &&
                    sent.Layouts.Single(layout =>
                        layout.OrientationId == CompositionOrientationIds.Portrait).Transform ==
                    first.Layouts.Single(layout =>
                        layout.OrientationId == CompositionOrientationIds.Portrait).Transform,
                    "The UI process must resolve reusable Silhouette defaults once and include them in the capture-host start request.");

                var changedAfterStart = first with
                {
                    ModifiedUtc = first.ModifiedUtc + TimeSpan.FromMinutes(1),
                    Layouts = first.Layouts.Select(layout =>
                        layout.OrientationId == CompositionOrientationIds.Portrait
                            ? layout with { Transform = layout.Transform with { AnchorX = 0.81 } }
                            : layout).ToArray()
                };
                _ = SilhouettePreferencesStore.SaveAsync(
                        settingsDirectory,
                        changedAfterStart,
                        mirrorCamera: true)
                    .GetAwaiter()
                    .GetResult();
                Assert(
                    manualClient.LastManualSettings!.SilhouettePreferencesSnapshot!.Layouts
                        .Single(layout => layout.OrientationId == CompositionOrientationIds.Portrait)
                        .Transform.AnchorX == 0.42,
                    "Changing reusable defaults after capture starts must not move the layout already sent to the isolated worker.");
                manualClient.CompleteManualStart(new CaptureHostRecorderSnapshot(
                    ManualCaptureState.Recording,
                    new ManualCaptureTarget("Game", 1920, 1080),
                    null));
                start.GetAwaiter().GetResult();
            }

            var explicitSnapshot = SilhouettePreferencesModel.CreateDefault(
                mirrorCamera: false,
                first.ModifiedUtc + TimeSpan.FromMinutes(2));
            var replayClient = new DelayedCaptureHostClient();
            using (var replayRecorder = new CaptureHostManualRecorder(
                       replayClient,
                       settingsDirectory))
            {
                var replayStart = replayRecorder.StartReplayAsync(CameraSettings() with
                {
                    InstantReplayEnabled = true,
                    SilhouettePreferencesSnapshot = explicitSnapshot
                });
                Assert(
                    replayClient.LastReplaySettings?.SilhouettePreferencesSnapshot is { } replaySnapshot &&
                    replaySnapshot.ModifiedUtc == explicitSnapshot.ModifiedUtc &&
                    replaySnapshot.Layouts.SequenceEqual(explicitSnapshot.Layouts),
                    "An explicitly injected preference snapshot must win over the mutable defaults file for replay capture.");
                replayClient.CompleteReplayStart(new CaptureHostRecorderSnapshot(
                    ManualCaptureState.NoTarget,
                    null,
                    null,
                    ReplayStatus: new ReplayCaptureStatus(
                        ReplayCaptureState.Buffering,
                        new ManualCaptureTarget("Game", 1920, 1080),
                        null,
                        TimeSpan.Zero,
                        0)));
                replayStart.GetAwaiter().GetResult();
            }

            var offClient = new DelayedCaptureHostClient();
            using (var offRecorder = new CaptureHostManualRecorder(offClient, settingsDirectory))
            {
                offRecorder.SelectTargetAsync(new FakeWindow()).GetAwaiter().GetResult();
                var offStart = offRecorder.StartAsync(CameraSettings() with
                {
                    IncludeReactionCamera = false,
                    SilhouettePreferencesSnapshot = explicitSnapshot
                });
                Assert(
                    offClient.LastManualSettings?.SilhouettePreferencesSnapshot is null,
                    "Gameplay-only capture must not send or retain an unused Silhouette preference snapshot.");
                offClient.CompleteManualStart(new CaptureHostRecorderSnapshot(
                    ManualCaptureState.Recording,
                    new ManualCaptureTarget("Game", 1920, 1080),
                    null));
                offStart.GetAwaiter().GetResult();
            }
        }
        finally
        {
            try { Directory.Delete(settingsDirectory, recursive: true); }
            catch { }
        }
    }

    private static void AssertHostStartingSnapshotsRemainAuthoritative()
    {
        var manualClient = new DelayedCaptureHostClient();
        using (var recorder = new CaptureHostManualRecorder(manualClient))
        {
            recorder.SelectTargetAsync(new FakeWindow()).GetAwaiter().GetResult();
            var start = recorder.StartAsync(CameraSettings());
            manualClient.CompleteManualStart(new CaptureHostRecorderSnapshot(
                ManualCaptureState.Recording,
                new ManualCaptureTarget("Game", 1920, 1080),
                null,
                ReactionCameraStatus: new ReactionCameraRuntimeStatus(
                    false,
                    false,
                    ManualCaptureStarting: true)));
            start.GetAwaiter().GetResult();
            Assert(
                recorder.State == ManualCaptureState.Recording &&
                recorder.ReactionCameraStatus is
                {
                    State: ReactionCameraRuntimeState.Starting,
                    ManualCaptureStarting: true
                },
                "A successful gameplay start response must preserve the host's pending manual-camera state until a later status poll reconciles it.");
        }

        var replayClient = new DelayedCaptureHostClient();
        using var replayRecorder = new CaptureHostManualRecorder(replayClient);
        var replayStart = replayRecorder.StartReplayAsync(CameraSettings() with
        {
            InstantReplayEnabled = true
        });
        replayClient.CompleteReplayStart(new CaptureHostRecorderSnapshot(
            ManualCaptureState.NoTarget,
            null,
            null,
            ReplayStatus: new ReplayCaptureStatus(
                ReplayCaptureState.Starting,
                new ManualCaptureTarget("Game", 1920, 1080),
                null,
                TimeSpan.Zero,
                0,
                ReactionCameraStarting: true),
            ReactionCameraStatus: new ReactionCameraRuntimeStatus(
                false,
                false,
                InstantReplayStarting: true)));
        replayStart.GetAwaiter().GetResult();
        Assert(
            replayRecorder.ReactionCameraStatus is
            {
                State: ReactionCameraRuntimeState.Starting,
                InstantReplayStarting: true
            },
            "A successful replay start response must preserve the host's pending camera state until a later status poll reconciles it.");
    }

    private static void AssertManualStartupIsImmediatelyVisible()
    {
        var client = new DelayedCaptureHostClient();
        using var recorder = new CaptureHostManualRecorder(client);
        recorder.SelectTargetAsync(new FakeWindow()).GetAwaiter().GetResult();
        var observed = new List<ReactionCameraRuntimeState>();
        recorder.ReactionCameraStateChanged += (_, _) =>
            observed.Add(recorder.ReactionCameraStatus.State);

        var start = recorder.StartAsync(CameraSettings());
        Assert(
            recorder.ReactionCameraStatus is
            {
                State: ReactionCameraRuntimeState.Starting,
                ManualCaptureStarting: true,
                InstantReplayStarting: false,
                IsActive: false
            },
            "Manual capture must expose Reaction Camera startup before the isolated host command returns.");

        client.CompleteManualStart(new CaptureHostRecorderSnapshot(
            ManualCaptureState.Recording,
            new ManualCaptureTarget("Game", 1920, 1080),
            null,
            ReactionCameraStatus: new ReactionCameraRuntimeStatus(true, false)));
        start.GetAwaiter().GetResult();

        Assert(
            observed.Contains(ReactionCameraRuntimeState.Starting) &&
            observed.Contains(ReactionCameraRuntimeState.Active) &&
            recorder.ReactionCameraStatus is
            {
                State: ReactionCameraRuntimeState.Active,
                ManualCaptureStarting: false,
                ManualCaptureActive: true
            },
            "Manual Reaction Camera startup must raise state changes and reconcile to active.");
    }

    private static void AssertReplayCameraFailureReconcilesWithoutFailingGameplay()
    {
        var client = new DelayedCaptureHostClient();
        using var recorder = new CaptureHostManualRecorder(client);
        var observed = new List<ReactionCameraRuntimeState>();
        recorder.ReactionCameraStateChanged += (_, _) =>
            observed.Add(recorder.ReactionCameraStatus.State);

        var start = recorder.StartReplayAsync(CameraSettings() with
        {
            InstantReplayEnabled = true
        });
        Assert(
            recorder.ReactionCameraStatus is
            {
                State: ReactionCameraRuntimeState.Starting,
                InstantReplayStarting: true,
                ManualCaptureStarting: false
            },
            "Instant Replay must expose Reaction Camera startup before the isolated host command returns.");

        const string cameraError = "Camera privacy access was denied.";
        client.CompleteReplayStart(new CaptureHostRecorderSnapshot(
            ManualCaptureState.NoTarget,
            null,
            null,
            ReplayStatus: new ReplayCaptureStatus(
                ReplayCaptureState.Buffering,
                new ManualCaptureTarget("Game", 1920, 1080),
                null,
                TimeSpan.FromSeconds(2),
                1_024),
            ReactionCameraStatus: new ReactionCameraRuntimeStatus(false, false, cameraError)));
        start.GetAwaiter().GetResult();

        Assert(
            recorder.ReplayStatus.State == ReplayCaptureState.Buffering &&
            observed.Contains(ReactionCameraRuntimeState.Starting) &&
            observed.Contains(ReactionCameraRuntimeState.Failed) &&
            recorder.ReactionCameraStatus is
            {
                State: ReactionCameraRuntimeState.Failed,
                InstantReplayStarting: false,
                IsActive: false,
                LastError: cameraError
            },
            "A failed replay camera start must clear Starting and leave gameplay buffering active.");
    }

    private static void AssertTransportFailureClearsStarting()
    {
        var client = new DelayedCaptureHostClient();
        using var recorder = new CaptureHostManualRecorder(client);
        recorder.SelectTargetAsync(new FakeWindow()).GetAwaiter().GetResult();
        var start = recorder.StartAsync(CameraSettings());
        client.FailManualStart(new IOException("isolated worker disconnected"));

        var failed = false;
        try
        {
            start.GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            failed = true;
        }

        Assert(
            failed &&
            recorder.State == ManualCaptureState.Failed &&
            recorder.ReactionCameraStatus is
            {
                State: ReactionCameraRuntimeState.Failed,
                ManualCaptureStarting: false,
                IsActive: false
            },
            "A transport failure must clear Reaction Camera Starting and reconcile it to a visible failure.");
    }

    private static void AssertPendingCameraStartupCancelsPromptly()
    {
        using var startup = new OptionalCaptureStartup();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        startup.Start(async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        });
        entered.Task.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        Assert(startup.IsStarting,
            "An optional camera open must be visible as pending before it completes.");

        startup.CancelAndWaitAsync()
            .WaitAsync(TimeSpan.FromSeconds(1))
            .GetAwaiter()
            .GetResult();
        Assert(!startup.IsStarting,
            "A camera-off request must cancel and join a cooperative pending camera open promptly.");
    }

    private static void AssertStuckOptionalShutdownIsBoundedAndAbandonable()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert(
            !OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                    never.Task,
                    TimeSpan.Zero)
                .GetAwaiter()
                .GetResult(),
            "A stuck optional camera teardown must time out instead of blocking gameplay finalization.");
        Assert(
            ReactionCameraPersistencePolicy.GetRemainingBudget(TimeSpan.Zero) ==
                ReactionCameraPersistencePolicy.MaximumWait &&
            ReactionCameraPersistencePolicy.GetRemainingBudget(TimeSpan.FromSeconds(100)) ==
                TimeSpan.FromSeconds(10) &&
            ReactionCameraPersistencePolicy.GetRemainingBudget(TimeSpan.FromSeconds(110)) ==
                TimeSpan.Zero,
            "Post-gameplay camera persistence must consume only the remaining host-command budget and preserve its safety margin.");

        using var startup = new OptionalCaptureStartup();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        startup.Start(_ => releaseFirst.Task);
        startup.Abandon();
        Assert(
            !startup.IsStarting &&
            !ReactionCameraStartPolicy.CanStart(requested: true, releaseNeedsAttention: true) &&
            !ReactionCameraStartPolicy.CanStart(
                requested: true,
                releaseNeedsAttention: false,
                suppressedForCapture: true) &&
            ReactionCameraStartPolicy.CanStart(requested: true, releaseNeedsAttention: false) &&
            new ReactionCameraRuntimeStatus(
                false,
                false,
                "release needs attention",
                ReleaseNeedsAttention: true).State ==
                ReactionCameraRuntimeState.ReleaseNeedsAttention,
            "An abandoned or explicitly suppressed camera open must block every new camera open for that capture while gameplay remains allowed.");
        releaseFirst.TrySetResult();
    }

    private static void AssertReplayCameraStartupJoinsItsFrameSource()
    {
        var pendingSource = new TaskCompletionSource<IVideoFrameSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var encoder = new ReplayVideoSegmentEncoder(
            _ => pendingSource.Task,
            new Windows.Graphics.SizeInt32(1280, 720),
            6_000_000,
            30,
            new EncodedReplayRing(TimeSpan.FromSeconds(30), 32 * 1024 * 1024));
        using var cancellation = new CancellationTokenSource();
        var start = encoder.StartAsync(cancellation.Token);
        cancellation.Cancel();
        Assert(
            !OptionalCaptureShutdownPolicy.CompletesWithinAsync(start, TimeSpan.Zero)
                .GetAwaiter()
                .GetResult(),
            "A cancelled replay-camera start must not report completion while its frame-source factory can still own the webcam.");

        pendingSource.TrySetCanceled();
        try { start.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        Assert(start.IsCompleted,
            "Replay-camera startup may complete only after its frame-source task has released.");
    }

    private static void AssertManualCameraStopRemainsSingleFlightAfterTimeout()
    {
        var stop = new OptionalCaptureStopSingleFlight<string>();
        var releaseDevice = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarts = 0;
        Task<string> StartStop() => stop.GetOrStart(() =>
        {
            Interlocked.Increment(ref stopStarts);
            return releaseDevice.Task;
        });

        var cameraOffStop = StartStop();
        Assert(
            !OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                    cameraOffStop,
                    TimeSpan.Zero)
                .GetAwaiter()
                .GetResult(),
            "The fixture must reproduce a camera-off teardown timeout.");

        var gameplayStop = StartStop();
        Assert(
            ReferenceEquals(cameraOffStop, gameplayStop) &&
            stopStarts == 1 &&
            !gameplayStop.IsCompleted,
            "Gameplay Stop after a camera-off timeout must join the exact pending camera worker instead of publishing a false no-op release.");

        releaseDevice.TrySetResult("released camera");
        Assert(
            gameplayStop.GetAwaiter().GetResult() == "released camera" &&
            cameraOffStop.Status == TaskStatus.RanToCompletion &&
            stop.Clear(cameraOffStop),
            "Only completion of the exact captured camera worker may confirm release and clear the single-flight operation.");
    }

    private static void AssertFailedCameraLayerCannotCommit()
    {
        var cameraStart = TimeSpan.FromSeconds(10);
        var cameraEnd = TimeSpan.FromSeconds(20);
        var gameplayStart = TimeSpan.FromSeconds(9);
        var stagedPath = Path.Combine(
            Path.GetTempPath(),
            $"clipcord-failed-camera-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(stagedPath, new byte[] { 1, 2, 3, 4 });
        try
        {
            var fakeEncoder = new object();
            var usableEncoder = ReactionCameraLayerCommitPolicy.CompleteStop(
                fakeEncoder,
                stoppedCleanly: false,
                stagedPath);
            Assert(
                usableEncoder is null &&
                !File.Exists(stagedPath) &&
                ReactionCameraLayerCommitPolicy.CanCommit(
                    stoppedCleanly: true,
                    cameraStart,
                    cameraEnd,
                    gameplayStart,
                    "nonempty.camera.mp4") &&
                !ReactionCameraLayerCommitPolicy.CanCommit(
                    stoppedCleanly: false,
                    cameraStart,
                    cameraEnd,
                    gameplayStart,
                    "nonempty.camera.mp4"),
                "A camera encoder that faults after joining must remain released, delete its staged layer, and never be promoted even with valid timestamps.");
        }
        finally
        {
            try { File.Delete(stagedPath); }
            catch { }
        }
    }

    private static CaptureSettings CameraSettings() =>
        CaptureSettings.Normalize(CaptureSettings.Default with
        {
            IncludeReactionCamera = true,
            ReactionCameraConsentGranted = true,
            CameraDeviceId = "test-camera-id",
            CameraDevice = "Test camera"
        });

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeWindow : IWin32Window
    {
        public nint Handle => (nint)1;
    }

    private sealed class DelayedCaptureHostClient : ICaptureHostRecorderClient
    {
        private readonly TaskCompletionSource<CaptureHostRecorderSnapshot> _manualStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CaptureHostRecorderSnapshot> _replayStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CaptureSettings? LastManualSettings { get; private set; }
        internal CaptureSettings? LastReplaySettings { get; private set; }

        internal void CompleteManualStart(CaptureHostRecorderSnapshot snapshot) =>
            _manualStart.TrySetResult(snapshot);

        internal void FailManualStart(Exception exception) =>
            _manualStart.TrySetException(exception);

        internal void CompleteReplayStart(CaptureHostRecorderSnapshot snapshot) =>
            _replayStart.TrySetResult(snapshot);

        public Task<CaptureHostRecorderSnapshot> GetRecorderStatusAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> SelectTargetAsync(
            nint ownerWindowHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> DetectTargetAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public async Task<CaptureHostRecorderSnapshot> StartRecordingAsync(
            CaptureSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastManualSettings = settings;
            return await _manualStart.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<CaptureHostRecorderSnapshot> StopRecordingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public async Task<CaptureHostRecorderSnapshot> StartReplayAsync(
            CaptureSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastReplaySettings = settings;
            return await _replayStart.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<CaptureHostRecorderSnapshot> StopReplayAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> SaveReplayAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> DisableReactionCameraAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        private static CaptureHostRecorderSnapshot ReadySnapshot() => new(
            ManualCaptureState.Ready,
            new ManualCaptureTarget("Game", 1920, 1080),
            null,
            ReplayStatus: new ReplayCaptureStatus(
                ReplayCaptureState.Off,
                null,
                null,
                TimeSpan.Zero,
                0),
            ReactionCameraStatus: new ReactionCameraRuntimeStatus(false, false));
    }
}
