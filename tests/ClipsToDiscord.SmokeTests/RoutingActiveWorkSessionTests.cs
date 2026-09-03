using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
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
        await AssertRequiredNamedHostExitIsObservedAsync();
        await AssertOptionalXboxFailureRepairsWithoutAggregateRestartAsync(
            Path.Combine(testRoot, "optional-xbox-restart"));
        await AssertOptionalXboxCanceledStartupStopsItsRetryLoopAsync(
            Path.Combine(testRoot, "optional-xbox-canceled-start"));
        await AssertOptionalXboxStopFailureDoesNotShortCircuitWatchedStopAsync();
        await AssertOptionalXboxDisposeFailureDoesNotShortCircuitOwnedCleanupAsync();
        await AssertProductionFactoryRejectsReplacedCaptureLibraryAsync(
            Path.Combine(testRoot, "capture-library-replaced-before-create"));
        await AssertRunningSessionWithRetainedPinBlocksReplacementAsync(
            Path.Combine(testRoot, "capture-library-pinned-while-running"));
        await AssertProductionFactorySurvivesRouteEditAndExecutesAsync(
            Path.Combine(testRoot, "production-route-edit"));
        await AssertProductionWatchedDiscordDeliveryIsRestartSafeAsync(
            Path.Combine(testRoot, "d"));
    }

    internal static Task RunProductionWatchedDiscordE2EAsync(string testRoot) =>
        AssertProductionWatchedDiscordDeliveryIsRestartSafeAsync(testRoot);

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

    private static async Task AssertRequiredNamedHostExitIsObservedAsync()
    {
        var migrated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var named = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = RoutingRequiredExternalSourceCompletion.ObserveAsync(
            migrated.Task,
            named.Task);
        var failure = new InvalidDataException("named-watched-runtime-terminal");

        named.TrySetException(failure);
        await AssertSameFailureAsync(completion, failure);

        Assert(!migrated.Task.IsCompleted,
            "A named watched-source runtime failure must wake the required external-source supervisor without waiting for the migrated watcher to stop.");
    }

    private static async Task AssertOptionalXboxFailureRepairsWithoutAggregateRestartAsync(
        string root)
    {
        using var authority = await AuthorityFixture.CreateAsync(root);
        var events = new ConcurrentQueue<string>();
        var capture = new FakeHost("capture", events);
        var watched = new FakeHost("watched", events);
        var startupFailure = new InvalidOperationException("xbox-startup-failure");
        var first = new FakeOptionalXboxInstance(startFailure: startupFailure)
        {
            ThrowOnDispose = true
        };
        var second = new FakeOptionalXboxInstance();
        var third = new FakeOptionalXboxInstance();
        var instances = new Queue<FakeOptionalXboxInstance>([first, second, third]);
        var retryEntered = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var repairReleased = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var retryCall = 0;
        var optionalFailures = new ConcurrentQueue<Exception>();
        async Task DelayUntilRepairAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref retryCall) - 1;
            retryEntered[index].TrySetResult();
            await repairReleased[index].Task.WaitAsync(cancellationToken);
        }
        var supervisor = new OptionalXboxRuntimeSupervisor(
            () => instances.Dequeue().CreateRuntimeInstance(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(4),
            DelayUntilRepairAsync,
            (_, exception) => optionalFailures.Enqueue(exception));

        async ValueTask StartExternalAsync(CancellationToken cancellationToken)
        {
            await watched.StartAsync(cancellationToken);
            try
            {
                await supervisor.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await supervisor.StopAsync(CancellationToken.None);
                await watched.StopAsync(CancellationToken.None);
                throw;
            }
        }
        async ValueTask StopExternalAsync(CancellationToken _)
        {
            await supervisor.StopAsync(CancellationToken.None);
            await watched.StopAsync(CancellationToken.None);
        }
        async ValueTask DisposeOwnedAsync()
        {
            await RoutingOwnedWorkGraphDisposer.DisposeAsync(
                () => ValueTask.CompletedTask,
                supervisor.DisposeAsync,
                () => ValueTask.CompletedTask,
                () => events.Enqueue("executor-dispose"),
                exception => optionalFailures.Enqueue(exception));
        }
        var operations = new RoutingActiveWorkSessionOperations(
            capture.StartAsync,
            capture.StopAsync,
            () => capture.Completion,
            () => capture.Failure,
            StartExternalAsync,
            StopExternalAsync,
            () => watched.Completion,
            () => watched.Failure,
            DisposeOwnedAsync);
        await using var session = RoutingActiveWorkSession.CreateForTesting(
            authority.Lease, authority.Gate, operations);

        await session.StartAsync();
        await retryEntered[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(session.State == RoutingActiveWorkSessionState.Running &&
               !session.Completion.IsCompleted &&
               capture.StopCalls == 0 && watched.StopCalls == 0,
            "An optional Xbox startup failure must degrade independently without quiescing the aggregate session.");

        repairReleased[0].TrySetResult();
        await second.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => supervisor.Inspect().Status == OptionalXboxRuntimeStatus.Running);
        second.Fail(new IOException("xbox-terminal-after-start"));
        await retryEntered[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(session.State == RoutingActiveWorkSessionState.Running &&
               capture.StopCalls == 0 && watched.StopCalls == 0,
            "A terminal Xbox instance must not stop ordinary watched or Capture work while repair is pending.");

        repairReleased[1].TrySetResult();
        await third.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() =>
        {
            var inspection = supervisor.Inspect();
            return inspection.Status == OptionalXboxRuntimeStatus.Running &&
                   inspection.RestartCount == 2;
        });
        Assert(session.State == RoutingActiveWorkSessionState.Running &&
               !session.Completion.IsCompleted &&
               optionalFailures.Any(exception =>
                   ReferenceEquals(exception, startupFailure)),
            "Repair must create a fresh Xbox host inside the same aggregate session and retain inspectable failure evidence.");

        third.Fail(new IOException("xbox-terminal-before-stop"));
        await retryEntered[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync();
        Assert(session.Completion.IsCompletedSuccessfully &&
               session.State == RoutingActiveWorkSessionState.Stopped &&
               capture.StopCalls == 1 && watched.StopCalls == 1 &&
               instances.Count == 0,
            "Stopping during Xbox retry backoff must cancel recreation and still stop the aggregate normally.");
    }

    private static async Task AssertOptionalXboxCanceledStartupStopsItsRetryLoopAsync(string root)
    {
        _ = root;
        var startRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var instance = new FakeOptionalXboxInstance(startRelease.Task);
        var factoryCalls = 0;
        await using var supervisor = new OptionalXboxRuntimeSupervisor(
            () =>
            {
                Interlocked.Increment(ref factoryCalls);
                return instance.CreateRuntimeInstance();
            },
            TimeSpan.Zero,
            TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var start = supervisor.StartAsync(cancellation.Token);
        await instance.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await AssertCanceledAsync(start,
            "Canceling aggregate startup must cancel the optional Xbox supervisor wait.");
        await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(factoryCalls == 1 && instance.StopCalls == 1 &&
               supervisor.Inspect().Status == OptionalXboxRuntimeStatus.Stopped,
            "Canceled Xbox startup must leave no current instance or retry loop behind.");
    }

    private static async Task AssertOptionalXboxDisposeFailureDoesNotShortCircuitOwnedCleanupAsync()
    {
        var events = new ConcurrentQueue<string>();
        var optionalFailures = 0;
        await RoutingOwnedWorkGraphDisposer.DisposeAsync(
            () =>
            {
                events.Enqueue("capture-dispose");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                events.Enqueue("xbox-dispose");
                return ValueTask.FromException(
                    new IOException("optional-xbox-dispose-failure"));
            },
            () =>
            {
                events.Enqueue("watched-dispose");
                return ValueTask.CompletedTask;
            },
            () => events.Enqueue("executor-dispose"),
            _ => Interlocked.Increment(ref optionalFailures));
        Assert(events.ToArray().SequenceEqual([
                   "capture-dispose",
                   "xbox-dispose",
                   "watched-dispose",
                   "executor-dispose"
               ]) && optionalFailures == 1,
            "An optional Xbox dispose failure must be reported without preventing watched or executor cleanup.");
    }

    private static async Task AssertOptionalXboxStopFailureDoesNotShortCircuitWatchedStopAsync()
    {
        var events = new ConcurrentQueue<string>();
        var optionalFailure = new IOException("optional-xbox-stop-failure");
        Exception? observed = null;
        try
        {
            await RoutingExternalSourceStopper.StopAsync(
                () =>
                {
                    events.Enqueue("xbox-stop");
                    return ValueTask.FromException(optionalFailure);
                },
                () =>
                {
                    events.Enqueue("watched-stop");
                    return ValueTask.CompletedTask;
                });
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Assert(ReferenceEquals(observed, optionalFailure) &&
               events.ToArray().SequenceEqual([
                   "xbox-stop",
                   "watched-stop"
               ]),
            "An optional Xbox stop failure must remain observable without preventing the ordinary watched host from stopping.");
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

    private static async Task AssertProductionWatchedDiscordDeliveryIsRestartSafeAsync(
        string root)
    {
        using var authority = await AuthorityFixture.CreateAsync(
            root,
            uploadToDiscord: true);
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
        var posts = new DiscordPostRecorder();
        Func<DiscordWebhookClient> clientFactory = () =>
            new(new RecordingDiscordHandler(posts));
        var source = Path.Combine(
            authority.Settings.ClipsFolder,
            "E2E Game 2026.08.29 - 22.06.00.00.DVR.mp4");
        CreateValidVideo(source);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-1));

        await using (var first = RoutingActiveWorkSession.CreateProduction(
                         authority.Settings,
                         () => authority.Settings,
                         authority.CaptureSettings,
                         () => authority.CaptureSettings,
                         authority.Lease,
                         authority.Gate,
                         authority.Storage,
                         options,
                         discordClientFactory: clientFactory))
        {
            await first.StartAsync();
            var completed = await WaitForCompletedWatchedPlanAsync(
                authority.Storage.OutboxPath,
                TimeSpan.FromSeconds(25));
            Assert(completed is not null &&
                   completed.Deliveries is
                   [
                       {
                           State: PlannedDeliveryState.Delivered,
                           Attempts: 1,
                           RemoteReceiptReference: "discord:777777777777777777"
                       }
                   ] &&
                   completed.FileDispositions is
                   [
                       {
                           State: PlannedFileDispositionState.Completed,
                           LibraryArea: RoutingLibraryArea.Uploaded
                       }
                   ] &&
                   posts.CallCount == 1 &&
                   posts.AllRequestedWaitReceipts &&
                   !File.Exists(source),
                "The production watched graph must plan one Discord delivery, receive one durable receipt, and file the source into Uploaded exactly once.");

            var journals = new RoutingWatchedSourceJournalStore(
                authority.Storage.WatchedJournalRoot);
            var journalIds = journals.EnumerateSourceClipIds();
            Assert(journalIds.Count == 1,
                "The watched production graph must persist one immutable source journal.");
            var journal = journals.Load(journalIds[0]).Document;
            var disposition = completed!.FileDispositions.Single();
            Assert(journal is not null &&
                   File.Exists(WatchedFolderLibraryLayout.GetDestinationPath(
                       authority.Settings.ClipsFolder,
                       journal,
                       disposition,
                       createDirectories: false)),
                "The completed watched disposition must point to a real managed Uploaded file.");
            await first.StopAsync();
        }

        var beforeRestart = new RoutingOutboxStore(
            authority.Storage.OutboxPath).Load().Document!;
        await using (var restarted = RoutingActiveWorkSession.CreateProduction(
                         authority.Settings,
                         () => authority.Settings,
                         authority.CaptureSettings,
                         () => authority.CaptureSettings,
                         authority.Lease,
                         authority.Gate,
                         authority.Storage,
                         options,
                         discordClientFactory: clientFactory))
        {
            await restarted.StartAsync();
            await Task.Delay(100);
            var afterRestart = new RoutingOutboxStore(
                authority.Storage.OutboxPath).Load().Document!;
            var receipts = new RoutingDeliveryReceiptStore(
                authority.Storage.DeliveryReceiptPath).Load().Document!;
            Assert(posts.CallCount == 1 &&
                   afterRestart.Deliveries.Single().Attempts == 1 &&
                   afterRestart.Deliveries.Single().State ==
                   PlannedDeliveryState.Delivered &&
                   afterRestart.FileDispositions.Single().State ==
                   PlannedFileDispositionState.Completed &&
                   afterRestart.Generation == beforeRestart.Generation &&
                   receipts.Claims is
                   [
                       {
                           State: RoutingDeliveryClaimState.Confirmed,
                           RemoteReceiptReference: "discord:777777777777777777"
                       }
                   ] &&
                   restarted.State == RoutingActiveWorkSessionState.Running &&
                   restarted.Failure is null,
                "Restart reconciliation must preserve the confirmed receipt and completed filing without a second Discord POST or outbox mutation.");
            await restarted.StopAsync();
        }
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

    private static async Task<RoutingOutboxDocument?> WaitForCompletedWatchedPlanAsync(
        string outboxPath,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var outbox = new RoutingOutboxStore(outboxPath);
        while (DateTime.UtcNow < deadline)
        {
            var load = outbox.Load();
            if (load.LoadedFromDisk && load.Document is { } document &&
                document.Plans.Count == 1 &&
                RoutingWatchedJournalModel.IsWatchedSourceClipId(
                    document.Plans[0].SourceClipId) &&
                document.Deliveries.Count == 1 &&
                document.Deliveries[0].State == PlannedDeliveryState.Delivered &&
                document.FileDispositions.Count == 1 &&
                document.FileDispositions[0].State ==
                PlannedFileDispositionState.Completed)
            {
                return document;
            }
            await Task.Delay(10);
        }
        return null;
    }

    private static void CreateValidVideo(string path)
    {
        var ffmpeg = FfmpegCompressor.FindExecutable() ??
                     throw new FileNotFoundException(
                         "The production watched Discord E2E requires ffmpeg.exe.");
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", "color=c=black:s=320x240:r=15:d=0.25",
                     "-c:v", "mpeg4", "-pix_fmt", "yuv420p", "-an", path
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException(
                                "The production watched Discord E2E could not start ffmpeg.");
        if (!process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            throw new TimeoutException(
                "The production watched Discord E2E ffmpeg fixture timed out.");
        }
        var standardError = process.StandardError.ReadToEnd();
        Assert(process.ExitCode == 0 && File.Exists(path),
            "The production watched Discord E2E could not create its valid MP4 fixture: " +
            standardError);
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

    private static async Task AssertCanceledAsync(Task task, string message)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(1);
        Assert(condition(), "The optional Xbox lifecycle did not reach its expected state.");
    }

    private sealed class FakeOptionalXboxInstance
    {
        private readonly Task _startRelease;
        private readonly Exception? _startFailure;
        private readonly TaskCompletionSource _startEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        private int _stopCalls;
        private int _disposeCalls;

        internal FakeOptionalXboxInstance(
            Task? startRelease = null,
            Exception? startFailure = null)
        {
            _startRelease = startRelease ?? Task.CompletedTask;
            _startFailure = startFailure;
        }

        internal bool ThrowOnDispose { get; init; }
        internal Task StartEntered => _startEntered.Task;
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal OptionalXboxRuntimeInstance CreateRuntimeInstance() => new(
            StartAsync,
            StopAsync,
            _completion.Task,
            () => Volatile.Read(ref _failure),
            DisposeAsync);

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            _startEntered.TrySetResult();
            await _startRelease.WaitAsync(cancellationToken);
            if (_startFailure is not null) throw _startFailure;
        }

        private ValueTask StopAsync(CancellationToken _)
        {
            Interlocked.Increment(ref _stopCalls);
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            return ThrowOnDispose
                ? ValueTask.FromException(new IOException("injected-xbox-dispose-failure"))
                : ValueTask.CompletedTask;
        }

        internal void Fail(Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            _completion.TrySetException(exception);
        }
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

    private sealed class DiscordPostRecorder
    {
        private int _callCount;
        private int _requestsWithoutWait;

        internal int CallCount => Volatile.Read(ref _callCount);
        internal bool AllRequestedWaitReceipts =>
            Volatile.Read(ref _requestsWithoutWait) == 0;

        internal HttpResponseMessage Send(HttpRequestMessage request)
        {
            Interlocked.Increment(ref _callCount);
            if (request.RequestUri?.Query.Contains(
                    "wait=true", StringComparison.Ordinal) != true)
            {
                Interlocked.Increment(ref _requestsWithoutWait);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"777777777777777777\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class RecordingDiscordHandler(DiscordPostRecorder recorder) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(recorder.Send(request));
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

        internal static async Task<AuthorityFixture> CreateAsync(
            string root,
            bool uploadToDiscord = false)
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
            const string webhook =
                "https://discord.com/api/webhooks/123456789012345678/production-e2e-token";
            var settings = new AppSettings(
                watchedRoot,
                uploadToDiscord ? webhook : string.Empty,
                StartWithWindows: false,
                AppSettings.DefaultCompressionTargetMb,
                "Active Session Test",
                UploadToDiscord: uploadToDiscord,
                ModeToggleHotkey: string.Empty,
                ClipCaptureSource.SteelSeriesGg);
            var captureSettings = CaptureSettings.Normalize(
                CaptureSettings.Default with { LibraryRoot = captureRoot });
            var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(captureRoot);
            IReadOnlyList<string> connections = [];
            if (uploadToDiscord)
            {
                Assert(DiscordRoutingConnectionIdentity.TryCreate(
                           webhook, out var connectionId),
                    "The production watched Discord E2E webhook must have a stable connection id.");
                connections = [connectionId];
            }
            var readiness = LegacyRoutingMigrationPlanner.Evaluate(
                new LegacyRoutingMigrationInput(
                    settings,
                    state,
                    LegacyWorkerQuiesced: true,
                    connections,
                    captureLibraryBinding,
                    LegacyRoutingMigrationAdmission.ValidLegacyUpgrade),
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
                () => captureLibraryBinding,
                () => LegacyRoutingMigrationAdmission.ValidLegacyUpgrade);
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
