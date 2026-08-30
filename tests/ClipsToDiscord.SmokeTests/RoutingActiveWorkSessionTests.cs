using System.Collections.Concurrent;
using ClipsToDiscord;

internal static class RoutingActiveWorkSessionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 22, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertStartupAndStopOrderingBorrowLeaseAsync(
            Path.Combine(testRoot, "lifecycle"));
        await AssertUnexpectedHostExitFailsClosedAsync(
            Path.Combine(testRoot, "terminal-failure"));
        await AssertProductionFactoryRejectsReplacedCaptureLibraryAsync(
            Path.Combine(testRoot, "capture-library-replaced-before-create"));
        await AssertRunningSessionWithRetainedPinBlocksReplacementAsync(
            Path.Combine(testRoot, "capture-library-pinned-while-running"));
        await AssertProductionFactorySurvivesRouteEditAndExecutesAsync(
            Path.Combine(testRoot, "production-route-edit"));
    }

    private static async Task AssertStartupAndStopOrderingBorrowLeaseAsync(string root)
    {
        using var authority = await AuthorityFixture.CreateAsync(root);
        var events = new ConcurrentQueue<string>();
        var capture = new FakeHost("capture", events);
        var watchedRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var watched = new FakeHost("watched", events, watchedRelease.Task);
        var watchedEntered = watched.StartEntered;
        await using var session = RoutingActiveWorkSession.CreateForTesting(
            authority.Lease,
            authority.Gate,
            Operations(capture, watched, () =>
            {
                events.Enqueue("dispose-owned");
                return ValueTask.CompletedTask;
            }));

        var start = session.StartAsync();
        await watchedEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!start.IsCompleted && events.ToArray().SequenceEqual([
                   "capture-start",
                   "watched-start"
               ]),
            "Start must await complete Capture reconciliation before entering watched reconciliation, and must not return early.");
        watchedRelease.TrySetResult();
        await start;
        Assert(session.State == RoutingActiveWorkSessionState.Running &&
               !session.Completion.IsCompleted && authority.Lease.IsCurrent,
            "The aggregate session must enter Running without mutating its borrowed lease.");

        await session.StopAsync(new CancellationToken(canceled: true));
        Assert(events.ToArray().SequenceEqual([
                   "capture-start",
                   "watched-start",
                   "capture-stop",
                   "watched-stop",
                   "dispose-owned"
               ]) &&
               capture.StopCalls == 1 && watched.StopCalls == 1 &&
               session.Completion.IsCompletedSuccessfully &&
               session.State == RoutingActiveWorkSessionState.Stopped &&
               authority.Lease.IsCurrent &&
               authority.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
            "Stop must ignore caller cancellation, quiesce Capture before watched work, release owned components once, and leave coordinator ownership current.");
    }

    private static async Task AssertUnexpectedHostExitFailsClosedAsync(string root)
    {
        using var authority = await AuthorityFixture.CreateAsync(root);
        var events = new ConcurrentQueue<string>();
        var capture = new FakeHost("capture", events);
        var watched = new FakeHost("watched", events);
        var disposed = 0;
        await using var session = RoutingActiveWorkSession.CreateForTesting(
            authority.Lease,
            authority.Gate,
            Operations(capture, watched, () =>
            {
                Interlocked.Increment(ref disposed);
                events.Enqueue("dispose-owned");
                return ValueTask.CompletedTask;
            }));
        await session.StartAsync();
        var terminal = new InvalidDataException("capture-runtime-terminal");
        capture.Fail(terminal);
        await AssertSameFailureAsync(session.Completion, terminal);

        Assert(ReferenceEquals(session.Failure, terminal) &&
               session.State == RoutingActiveWorkSessionState.Failed &&
               capture.StopCalls == 1 && watched.StopCalls == 1 &&
               Volatile.Read(ref disposed) == 1 &&
               events.ToArray().SequenceEqual([
                   "capture-start",
                   "watched-start",
                   "capture-stop",
                   "watched-stop",
                   "dispose-owned"
               ]) &&
               authority.Lease.IsCurrent &&
               authority.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
            "An unexpected host exit must durably fault Completion, quiesce both producers, dispose shared work once, and never release the borrowed Routing lease.");
    }

    private static async Task AssertProductionFactorySurvivesRouteEditAndExecutesAsync(
        string root)
    {
        using var authority = await AuthorityFixture.CreateAsync(root);
        var options = new RoutingActiveWorkSessionOptions(
            new CaptureJournalRoutingPumpOptions(
                PollInterval: TimeSpan.FromMilliseconds(2),
                MaximumEntriesPerPage: 100,
                MaximumPageDuration: TimeSpan.FromSeconds(5),
                MaximumPagesPerPass: 100),
            new RoutingWatchedFolderRuntimeHostOptions(
                PollInterval: TimeSpan.FromMilliseconds(2),
                ErrorRetryInterval: TimeSpan.FromMilliseconds(2),
                MaximumErrorRetryInterval: TimeSpan.FromMilliseconds(10),
                MaximumConsecutiveLoopFailures: 3,
                MaximumCandidatesPerScan: 100));
        var watchedWithTrailingSeparator = authority.Settings with
        {
            ClipsFolder = authority.Settings.ClipsFolder + Path.DirectorySeparatorChar
        };
        var captureWithTrailingSeparator = authority.CaptureSettings with
        {
            LibraryRoot = authority.CaptureSettings.LibraryRoot + Path.DirectorySeparatorChar
        };
        await using var session = RoutingActiveWorkSession.CreateProduction(
            watchedWithTrailingSeparator,
            () => watchedWithTrailingSeparator,
            captureWithTrailingSeparator,
            () => captureWithTrailingSeparator,
            authority.Lease,
            authority.Gate,
            authority.Storage,
            options);
        await session.StartAsync();
        Assert(session.State == RoutingActiveWorkSessionState.Running &&
               authority.Gate.Inspect().RoutingGeneration == 1,
            "The production aggregate did not finish both empty startup reconciliations.");

        var before = authority.Snapshots.Load();
        if (!before.LoadedFromDisk || before.Document is null)
        {
            throw new InvalidOperationException("The route-edit fixture snapshot is unavailable.");
        }
        var edited = RoutingSnapshotModel.ReplaceRoutes(
            before.Document,
            before.Document.Routes,
            Now.AddMinutes(5));
        _ = await authority.Snapshots.SaveAsync(
            edited,
            before.Document.Generation);
        Assert(authority.Gate.Inspect() is
               { Enabled: true, RoutingGeneration: 2 },
            "The route-edit fixture did not expose the new enabled generation.");

        var game = CaptureLibraryLayout.GetRecordingDirectory(
            authority.CaptureSettings.LibraryRoot,
            "Route Edit Game");
        Directory.CreateDirectory(game);
        var source = Path.Combine(game, "2026-08-29_22-05-00.mp4");
        await File.WriteAllBytesAsync(source, [1, 3, 5, 7, 9]);
        var journal = await CaptureJournalStore.CommitOriginalAsync(
            authority.CaptureSettings.LibraryRoot,
            source,
            CaptureJournalSourceKind.InstantReplay,
            "Route Edit Game",
            Now.AddMinutes(5),
            TimeSpan.FromSeconds(20),
            1920,
            1080,
            reactionCameraRequested: false,
            requestedRenditions: [],
            now: Now.AddMinutes(5));

        var completed = await WaitForCompletedDispositionAsync(
            authority.Storage.OutboxPath,
            journal.Clip.ClipId);
        Assert(completed && session.State == RoutingActiveWorkSessionState.Running &&
               session.Failure is null && !session.Completion.IsCompleted &&
               authority.Lease.IsCurrent,
            "A route generation edit after Running must be adopted by a later Capture pass and remain executable under the same durable authority.");
        await session.StopAsync();
        Assert(session.Completion.IsCompletedSuccessfully && authority.Lease.IsCurrent,
            "The real production aggregate did not quiesce cleanly after route-edit execution.");
    }

    private static async Task AssertProductionFactoryRejectsReplacedCaptureLibraryAsync(
        string root)
    {
        using var authority = await AuthorityFixture.CreateAsync(root);
        var captureRoot = authority.CaptureSettings.LibraryRoot;
        var parkedOriginal = captureRoot + "-parked";
        Directory.Move(captureRoot, parkedOriginal);
        Directory.CreateDirectory(captureRoot);
        RoutingActiveWorkSession? unexpectedSession = null;
        var rejected = false;
        try
        {
            try
            {
                unexpectedSession = RoutingActiveWorkSession.CreateProduction(
                    authority.Settings,
                    () => authority.Settings,
                    authority.CaptureSettings,
                    () => authority.CaptureSettings,
                    authority.Lease,
                    authority.Gate,
                    authority.Storage);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Assert(rejected,
                "The production aggregate must reject a same-path Capture directory replacement before constructing either host.");
        }
        finally
        {
            if (unexpectedSession is not null)
            {
                await unexpectedSession.DisposeAsync();
            }
            Directory.Delete(captureRoot, recursive: true);
            Directory.Move(parkedOriginal, captureRoot);
        }
    }

    private static async Task AssertRunningSessionWithRetainedPinBlocksReplacementAsync(
        string root)
    {
        using var authority = await AuthorityFixture.CreateAsync(root);
        var options = new RoutingActiveWorkSessionOptions(
            new CaptureJournalRoutingPumpOptions(
                PollInterval: TimeSpan.FromMilliseconds(2),
                MaximumEntriesPerPage: 100,
                MaximumPageDuration: TimeSpan.FromSeconds(5),
                MaximumPagesPerPass: 100),
            new RoutingWatchedFolderRuntimeHostOptions(
                PollInterval: TimeSpan.FromMilliseconds(2),
                ErrorRetryInterval: TimeSpan.FromMilliseconds(2),
                MaximumErrorRetryInterval: TimeSpan.FromMilliseconds(10),
                MaximumConsecutiveLoopFailures: 3,
                MaximumCandidatesPerScan: 100));
        var captureRoot = authority.CaptureSettings.LibraryRoot;
        var parkedOriginal = captureRoot + "-parked";
        RoutingWatchedRootHandle? captureRootPin =
            RoutingWatchedFileSystem.OpenOrdinaryRoot(captureRoot);
        RoutingActiveWorkSession? session = null;
        try
        {
            session = RoutingActiveWorkSession.CreateProduction(
                authority.Settings,
                () => authority.Settings,
                authority.CaptureSettings,
                () => authority.CaptureSettings,
                authority.Lease,
                authority.Gate,
                authority.Storage,
                options);
            await session.StartAsync();

            var renameBlocked = false;
            try
            {
                Directory.Move(captureRoot, parkedOriginal);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                renameBlocked = true;
            }

            Assert(renameBlocked &&
                   session.State == RoutingActiveWorkSessionState.Running &&
                   session.Failure is null &&
                   !session.Completion.IsCompleted &&
                   Directory.Exists(captureRoot) &&
                   !Directory.Exists(parkedOriginal),
                "A running production lifetime must retain its native Capture-root handle and " +
                "deny same-path replacement while the session is active.");

            await session.DisposeAsync();
            session = null;
            captureRootPin.Handle.Dispose();
            captureRootPin = null;
            Directory.Move(captureRoot, parkedOriginal);
            Assert(!Directory.Exists(captureRoot) && Directory.Exists(parkedOriginal),
                "Disposing the session and its retained Capture-root pin must allow the " +
                "directory to be moved afterward.");
            Directory.Move(parkedOriginal, captureRoot);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            captureRootPin?.Handle.Dispose();
            if (Directory.Exists(parkedOriginal))
            {
                if (Directory.Exists(captureRoot))
                {
                    Directory.Delete(captureRoot, recursive: true);
                }
                Directory.Move(parkedOriginal, captureRoot);
            }
        }
    }

    private static RoutingActiveWorkSessionOperations Operations(
        FakeHost capture,
        FakeHost watched,
        Func<ValueTask> disposeOwned) => new(
        capture.StartAsync,
        capture.StopAsync,
        () => capture.Completion,
        () => capture.Failure,
        watched.StartAsync,
        watched.StopAsync,
        () => watched.Completion,
        () => watched.Failure,
        disposeOwned);

    private static async Task<bool> WaitForCompletedDispositionAsync(
        string outboxPath,
        string sourceClipId)
    {
        var timeout = DateTime.UtcNow.AddSeconds(8);
        var outbox = new RoutingOutboxStore(outboxPath);
        while (DateTime.UtcNow < timeout)
        {
            var load = outbox.Load();
            if (load.LoadedFromDisk && load.Document is not null)
            {
                var plan = load.Document.Plans.SingleOrDefault(item =>
                    item.SourceClipId.Equals(sourceClipId, StringComparison.Ordinal));
                if (plan is not null && load.Document.FileDispositions.Any(item =>
                        item.PlanId == plan.PlanId &&
                        item.State == PlannedFileDispositionState.Completed))
                {
                    return true;
                }
            }
            await Task.Delay(10);
        }
        return false;
    }

    private static async Task AssertSameFailureAsync(Task completion, Exception expected)
    {
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (ReferenceEquals(exception, expected))
        {
            return;
        }
        throw new InvalidOperationException(
            "The aggregate Completion did not expose the exact terminal host failure.");
    }

    private sealed class FakeHost
    {
        private readonly string _name;
        private readonly ConcurrentQueue<string> _events;
        private readonly Task _startRelease;
        private readonly TaskCompletionSource _startEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        private int _stopCalls;

        internal FakeHost(
            string name,
            ConcurrentQueue<string> events,
            Task? startRelease = null)
        {
            _name = name;
            _events = events;
            _startRelease = startRelease ?? Task.CompletedTask;
        }

        internal Task StartEntered => _startEntered.Task;
        internal Task Completion => _completion.Task;
        internal Exception? Failure => Volatile.Read(ref _failure);
        internal int StopCalls => Volatile.Read(ref _stopCalls);

        internal async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            _events.Enqueue(_name + "-start");
            _startEntered.TrySetResult();
            await _startRelease.WaitAsync(cancellationToken);
        }

        internal ValueTask StopAsync(CancellationToken _)
        {
            if (Interlocked.Increment(ref _stopCalls) == 1)
            {
                _events.Enqueue(_name + "-stop");
                _completion.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        internal void Fail(Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            _completion.TrySetException(exception);
        }
    }

    private sealed class AuthorityFixture : IDisposable
    {
        private AuthorityFixture(
            AppSettings settings,
            CaptureSettings captureSettings,
            RoutingActiveWorkSessionStorage storage,
            RoutingSnapshotStore snapshots,
            RoutingRuntimeFeatureGate gate,
            ClipProcessingOwnershipCoordinator ownership,
            ClipProcessingOwnershipLease lease)
        {
            Settings = settings;
            CaptureSettings = captureSettings;
            Storage = storage;
            Snapshots = snapshots;
            Gate = gate;
            Ownership = ownership;
            Lease = lease;
        }

        internal AppSettings Settings { get; }
        internal CaptureSettings CaptureSettings { get; }
        internal RoutingActiveWorkSessionStorage Storage { get; }
        internal RoutingSnapshotStore Snapshots { get; }
        internal RoutingRuntimeFeatureGate Gate { get; }
        internal ClipProcessingOwnershipCoordinator Ownership { get; }
        internal ClipProcessingOwnershipLease Lease { get; }

        internal static async Task<AuthorityFixture> CreateAsync(string root)
        {
            Directory.CreateDirectory(root);
            var watchedRoot = Directory.CreateDirectory(
                Path.Combine(root, "watched")).FullName;
            var captureRoot = Directory.CreateDirectory(
                Path.Combine(root, "capture-library")).FullName;
            var routingRoot = Directory.CreateDirectory(
                Path.Combine(root, "routing-state")).FullName;
            var storage = new RoutingActiveWorkSessionStorage(routingRoot).Normalize();
            var state = new WatchState
            {
                Version = 4,
                ClipsFolder = watchedRoot,
                CaptureSource = ClipCaptureSource.SteelSeriesGg
            };
            var stateStore = new WatchStateStore(
                Path.Combine(routingRoot, "legacy", "state.json"),
                Path.Combine(routingRoot, "legacy", ".safe-baseline-required"));
            stateStore.Save(state);
            var settings = new AppSettings(
                watchedRoot,
                string.Empty,
                StartWithWindows: false,
                AppSettings.DefaultCompressionTargetMb,
                "Active Session Test",
                UploadToDiscord: false,
                ModeToggleHotkey: string.Empty,
                ClipCaptureSource.SteelSeriesGg);
            var captureSettings = CaptureSettings.Normalize(
                CaptureSettings.Default with { LibraryRoot = captureRoot });
            var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(captureRoot);
            IReadOnlyList<string> connections = [];
            var readiness = LegacyRoutingMigrationPlanner.Evaluate(
                new LegacyRoutingMigrationInput(
                    settings,
                    state,
                    LegacyWorkerQuiesced: true,
                    connections,
                    captureLibraryBinding),
                Now);
            var migration = readiness.Plan ?? throw new InvalidOperationException(
                $"The active-session migration fixture is unavailable ({readiness.Status}).");
            var snapshots = new RoutingSnapshotStore(storage.SnapshotPath);
            _ = await snapshots.SaveAsync(
                new RoutingSnapshotDocument(
                    RoutingSnapshotStore.CurrentSchemaVersion,
                    Generation: 1,
                    Routes: [migration.Route],
                    CreatedUtc: Now,
                    UpdatedUtc: Now),
                expectedGeneration: 0);
            var markers = new LegacyRoutingMigrationMarkerStore(
                storage.MigrationMarkerPath);
            var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(migration, Now);
            _ = await markers.SaveAsync(prepared, expectedGeneration: 0);
            var committed = await markers.SaveAsync(
                LegacyRoutingMigrationMarkerModel.Commit(prepared, Now.AddSeconds(1)),
                prepared.Generation);
            var executionAuthority = new RoutingExecutionAuthorityStore(
                Path.Combine(routingRoot, RoutingExecutionAuthorityStore.FileName));
            _ = await executionAuthority.CommitAsync(
                RoutingExecutionAuthorityModel.Create(
                    committed,
                    ClipCaptureSource.SteelSeriesGg,
                    Now.AddSeconds(2)));
            var evidence = new LegacyRoutingActivationEvidenceSource(
                stateStore,
                () => settings,
                () => connections,
                () => captureLibraryBinding);
            var ownership = new ClipProcessingOwnershipCoordinator();
            if (!ownership.TryAcquire(
                    ClipProcessingRuntimeOwner.Routing,
                    out var lease) || lease is null)
            {
                throw new InvalidOperationException(
                    "The active-session fixture could not acquire Routing ownership.");
            }
            var gate = RoutingRuntimeFeatureGate.Evaluate(
                requestedEnabled: true,
                markers,
                snapshots,
                evidence,
                lease,
                RoutingWatchedSourceAdapters.CoveredSources,
                executionAuthority);
            Assert(gate.Enabled,
                "The active-session fixture must begin under complete Routing authority.");
            return new AuthorityFixture(
                settings,
                captureSettings,
                storage,
                snapshots,
                gate,
                ownership,
                lease);
        }

        public void Dispose() => Lease.Dispose();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
