using System.Diagnostics;
using System.Reflection;
using ClipsToDiscord;

internal static class CaptureLibraryShutdownCoordinationTests
{
    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertReplayPauseAndDrainIsARealAdmissionBarrierAsync();
        AssertSilhouetteStrictDisposeJoinsActiveChild(testRoot);
        await AssertSilhouetteWorkerParentLifetimeIsFailClosedAsync(testRoot);
        await AssertProductionSilhouetteWorkerJobContainmentAsync();
        AssertTraySwitchOrdersEveryCaptureLibraryBarrier();
        await AssertLateCaptureHostResponseCannotRestoreRevokedStateAsync();
    }

    private static async Task AssertReplayPauseAndDrainIsARealAdmissionBarrierAsync()
    {
        using var lifetime = new CancellationTokenSource();
        var controller = new BlockingReplayController();
        var failures = new List<Exception>();
        var settings = CaptureSettings.Default with { InstantReplayEnabled = true };
        var coordinator = new ReplayAutomaticStartCoordinator(
            controller,
            () => settings,
            () => true,
            (_, _) => Task.CompletedTask,
            failures.Add,
            lifetime.Token);

        var admittedStart = coordinator.RequestAsync();
        await controller.FirstStartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var drain = coordinator.PauseAndDrainAsync();
        Assert(!drain.IsCompleted,
            "PauseAndDrain must join the automatic replay start already admitted through the single-flight gate.");

        var rejectedWhilePaused = coordinator.RequestAsync();
        await rejectedWhilePaused.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(controller.StartCount == 1 && controller.ActiveStarts == 1,
            "A paused replay-start coordinator must reject new starts while retaining the admitted start until it settles.");

        controller.ReleaseFirstStart();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        await admittedStart.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(controller.StartCount == 1 && controller.ActiveStarts == 0 && failures.Count == 0,
            "PauseAndDrain may complete only after the admitted replay start has exited, without scheduling a replacement start.");

        coordinator.Resume();
        await coordinator.RequestAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert(controller.StartCount == 2 &&
               controller.ReplayStatus.State == ReplayCaptureState.Buffering &&
               controller.ActiveStarts == 0 && failures.Count == 0,
            "Resuming after a fully drained switch barrier must admit exactly one later replay start.");
        lifetime.Cancel();
    }

    private static void AssertSilhouetteStrictDisposeJoinsActiveChild(string testRoot)
    {
        var libraryRoot = Path.Combine(testRoot, "strict-silhouette-stop");
        Directory.CreateDirectory(libraryRoot);
        using var coordinator = new SilhouetteProcessingCoordinator(libraryRoot);
        using var child = StartHarmlessLongRunningChild();
        Assert(!child.HasExited,
            "The strict silhouette-stop fixture requires a live harmless child process.");

        var coordinatorType = typeof(SilhouetteProcessingCoordinator);
        var processGate = coordinatorType.GetField(
            "_processGate",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(coordinator) ??
            throw new InvalidOperationException(
                "The silhouette coordinator process-ownership gate is unavailable.");
        var activeWorker = coordinatorType.GetField(
            "_activeWorker",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The silhouette coordinator active-worker field is unavailable.");
        lock (processGate) activeWorker.SetValue(coordinator, child);

        var elapsed = Stopwatch.StartNew();
        coordinator.DisposeAndRequireStopped();
        elapsed.Stop();
        Assert(child.HasExited && child.WaitForExit(0) &&
               elapsed.Elapsed < TimeSpan.FromSeconds(8),
            "DisposeAndRequireStopped must terminate and join its active silhouette child before returning authority to a root switch or shutdown.");
    }

    private static Process StartHarmlessLongRunningChild()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ??
            Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo(commandProcessor)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 31 127.0.0.1 >nul");
        return Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "Windows did not start the harmless silhouette shutdown probe.");
    }

    private static async Task AssertSilhouetteWorkerParentLifetimeIsFailClosedAsync(
        string testRoot)
    {
        var libraryRoot = Path.GetFullPath(Path.Combine(
            testRoot,
            "silhouette-parent-lifetime"));
        Directory.CreateDirectory(libraryRoot);
        var projectId = new string('b', 32);
        var expectedBinding = RoutingCaptureLibraryBindingModel.Create(libraryRoot);
        using var parent = StartHarmlessLongRunningChild();
        var parentCreationTime = SilhouetteWorkerParentHandle.ReadCreationTimeFileTime(
            parent.Id);
        var startInfo = SilhouetteWorkerLaunch.CreateStartInfo(
            libraryRoot,
            projectId,
            forceCpu: true,
            executablePath: Environment.ProcessPath,
            parentProcessId: parent.Id,
            parentCreationTimeFileTime: parentCreationTime,
            expectedLibraryBinding: expectedBinding);
        Assert(startInfo.ArgumentList.Count == 14 &&
               startInfo.ArgumentList.SequenceEqual([
                   SilhouetteWorkerLaunchOptions.WorkerArgument,
                   SilhouetteWorkerLaunchOptions.LibraryRootArgument,
                   libraryRoot,
                   SilhouetteWorkerLaunchOptions.ProjectIdArgument,
                   projectId,
                   SilhouetteWorkerLaunchOptions.ParentArgument,
                   parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   SilhouetteWorkerLaunchOptions.ParentCreationArgument,
                   parentCreationTime.ToString(
                       System.Globalization.CultureInfo.InvariantCulture),
                   SilhouetteWorkerLaunchOptions.LibraryPathFingerprintArgument,
                   expectedBinding.CanonicalPathFingerprint,
                   SilhouetteWorkerLaunchOptions.LibraryIdentityFingerprintArgument,
                   expectedBinding.NativeDirectoryIdentityFingerprint,
                   SilhouetteWorkerLaunchOptions.CpuArgument
               ]) &&
               SilhouetteWorkerLaunchOptions.TryParse(
                   startInfo.ArgumentList.ToArray(),
                   out var roundTrip) &&
               roundTrip == new SilhouetteWorkerLaunchOptions(
                   libraryRoot,
                   projectId,
                   ForceCpu: true,
                   parent.Id,
                   parentCreationTime,
                   expectedBinding) &&
               !SilhouetteWorkerLaunchOptions.TryParse(
                   ReplaceArgument(
                       startInfo.ArgumentList,
                       SilhouetteWorkerLaunchOptions.ParentArgument,
                       "0"),
                   out _) &&
               !SilhouetteWorkerLaunchOptions.TryParse(
                   ReplaceArgument(
                       startInfo.ArgumentList,
                       SilhouetteWorkerLaunchOptions.ParentArgument,
                       Environment.ProcessId.ToString(
                           System.Globalization.CultureInfo.InvariantCulture)),
                   out _) &&
               !SilhouetteWorkerLaunchOptions.TryParse(
                   ReplaceArgument(
                       startInfo.ArgumentList,
                       SilhouetteWorkerLaunchOptions.ParentCreationArgument,
                       "0"),
                   out _) &&
               !SilhouetteWorkerLaunchOptions.TryParse(
                   ReplaceArgument(
                       startInfo.ArgumentList,
                       SilhouetteWorkerLaunchOptions.ParentCreationArgument,
                       "0" + parentCreationTime.ToString(
                           System.Globalization.CultureInfo.InvariantCulture)),
                   out _) &&
               !SilhouetteWorkerLaunchOptions.TryParse(
                   ReplaceArgument(
                       startInfo.ArgumentList,
                       SilhouetteWorkerLaunchOptions.LibraryPathFingerprintArgument,
                       expectedBinding.CanonicalPathFingerprint.ToLowerInvariant()),
                   out _),
            "The strict 14-argument silhouette launch must round-trip canonical parent creation identity and exact root-binding fingerprints while rejecting self, zero, non-canonical, or lowercase authority evidence.");

        var noCpuStartInfo = SilhouetteWorkerLaunch.CreateStartInfo(
            libraryRoot,
            projectId,
            forceCpu: false,
            executablePath: Environment.ProcessPath,
            parentProcessId: parent.Id,
            parentCreationTimeFileTime: parentCreationTime,
            expectedLibraryBinding: expectedBinding);
        Assert(noCpuStartInfo.ArgumentList.Count == 13 &&
               SilhouetteWorkerLaunchOptions.TryParse(
                   noCpuStartInfo.ArgumentList.ToArray(),
                   out var noCpuRoundTrip) &&
               noCpuRoundTrip is { ForceCpu: false } &&
               noCpuRoundTrip.ExpectedLibraryBinding == expectedBinding,
            "The non-CPU silhouette launch must use the fixed 13-argument contract without dropping lifetime or root authority.");

        using var retainedParent = SilhouetteWorkerParentHandle.OpenValidated(
            parent.Id,
            parentCreationTime);
        Assert(!retainedParent.IsSignaled &&
               retainedParent.CreationTimeFileTime == parentCreationTime &&
               Throws<InvalidDataException>(() =>
                   SilhouetteWorkerParentHandle.OpenValidated(
                       parent.Id,
                       parentCreationTime + 1)),
            "The worker must validate parent creation time on one retained native handle, rejecting the same PID with a different creation identity.");

        var pinnedRoot = RoutingWatchedFileSystem.OpenOrdinaryRoot(libraryRoot);
        using (pinnedRoot.Handle)
        {
            var sameHandleBinding = RoutingCaptureLibraryBindingModel.Create(pinnedRoot);
            RoutingCaptureLibraryBindingModel.RequireExact(
                expectedBinding,
                sameHandleBinding);
            Assert(sameHandleBinding == expectedBinding,
                "The worker root pin and root identity must be derived from the same retained directory handle.");
        }

        using var canceledOperation = new CancellationTokenSource();
        canceledOperation.Cancel();
        var requestedExitCodes = new List<int>();
        var workerResult = await SilhouetteWorkerProcess.RunAsync(
                new SilhouetteWorkerLaunchOptions(
                    libraryRoot,
                    projectId,
                    ForceCpu: true,
                    parent.Id,
                    parentCreationTime,
                    expectedBinding),
                canceledOperation.Token,
                requestedExitCodes.Add)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert(workerResult != SilhouetteWorkerProcess.InvalidArgumentsExitCode &&
               workerResult != SilhouetteWorkerProcess.SuccessExitCode &&
               requestedExitCodes.Count == 0 && !retainedParent.IsSignaled,
            "Direct RunAsync tests must carry full valid parent/root authority and reach the cancellable worker path without invoking hard exit while its retained parent remains alive.");

        var parentExitObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var watcher = retainedParent.WatchAsync(
            () =>
            {
                Interlocked.Increment(ref callbackCount);
                parentExitObserved.TrySetResult();
            });
        parent.Kill(entireProcessTree: true);
        Assert(parent.WaitForExit(5_000),
            "The parent-lifetime fixture must terminate its hidden parent process.");
        await parentExitObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await watcher.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(callbackCount == 1 && retainedParent.IsSignaled,
            "The same retained parent handle used for creation-time validation must observe exit promptly and signal worker cancellation exactly once.");

        AssertSilhouetteWorkerUsesParentCancellationForProcessing();
    }

    private static async Task AssertProductionSilhouetteWorkerJobContainmentAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException(
            "The smoke-test executable path is unavailable for the Job Object probe.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--silhouette-job-kill-probe");
        using var probe = Process.Start(startInfo) ?? throw new InvalidOperationException(
            "Windows did not start the silhouette Job Object probe.");
        var output = await probe.StandardOutput.ReadToEndAsync();
        var error = await probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(probe.ExitCode == 0,
            "The production KILL_ON_JOB_CLOSE probe could not establish containment: " + error);
        var childLine = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SingleOrDefault(line => line.StartsWith("CHILD_PID=", StringComparison.Ordinal));
        var childProcessId = 0;
        Assert(childLine is not null &&
               int.TryParse(
                   childLine["CHILD_PID=".Length..],
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out childProcessId) &&
               childProcessId > 0,
            "The Job Object probe must report the exact harmless child it placed under production containment.");

        var childExited = SpinWait.SpinUntil(
            () => !IsProcessAlive(childProcessId),
            TimeSpan.FromSeconds(3));
        if (!childExited)
        {
            try
            {
                using var leaked = Process.GetProcessById(childProcessId);
                leaked.Kill(entireProcessTree: true);
                leaked.WaitForExit(5_000);
            }
            catch (ArgumentException) { }
        }
        Assert(childExited,
            "Closing the production silhouette Job handle at worker-process exit must terminate its harmless child process tree.");

        AssertProductionProgramEstablishesSilhouetteContainmentFirst();
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AssertProductionProgramEstablishesSilhouetteContainmentFirst()
    {
        const BindingFlags staticAll =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var main = typeof(ClipsToDiscord.Program).GetMethod(
            "Main",
            staticAll) ?? throw new InvalidOperationException(
                "The production entry point is unavailable.");
        var establish = typeof(SilhouetteWorkerJobLifetime).GetMethod(
            nameof(SilhouetteWorkerJobLifetime.TryEstablish),
            staticAll) ?? throw new InvalidOperationException(
                "The production silhouette containment seam is unavailable.");
        var run = typeof(SilhouetteWorkerProcess).GetMethod(
            nameof(SilhouetteWorkerProcess.RunAsync),
            staticAll) ?? throw new InvalidOperationException(
                "The production silhouette worker seam is unavailable.");
        var establishOffset = FirstDirectCallOffset(main, establish);
        var runOffset = FirstDirectCallOffset(main, run);

        var jobType = typeof(SilhouetteWorkerJobLifetime);
        var killOnClose = jobType.GetField(
            "JobObjectLimitKillOnJobClose",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The KILL_ON_JOB_CLOSE limit is unavailable.");
        var rootedJob = jobType.GetField(
            "_rootedJob",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The rooted silhouette Job handle is unavailable.");
        var setLimits = jobType.GetMethod(
            "SetInformationJobObject",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The Job Object limit configuration seam is unavailable.");
        var assign = jobType.GetMethod(
            "AssignProcessToJobObject",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The worker Job assignment seam is unavailable.");
        var establishBodySetOffset = FirstDirectCallOffset(establish, setLimits);
        var establishBodyAssignOffset = FirstDirectCallOffset(establish, assign);

        Assert(establishOffset >= 0 && runOffset > establishOffset &&
               (uint)killOnClose.GetRawConstantValue()! == 0x00002000u &&
               rootedJob.FieldType == typeof(Microsoft.Win32.SafeHandles.SafeFileHandle) &&
               establishBodySetOffset >= 0 &&
               establishBodyAssignOffset > establishBodySetOffset,
            "Production Program must establish a statically rooted KILL_ON_JOB_CLOSE Job, configure its limit, and assign the worker before RunAsync can open the Capture library.");
    }

    private static void AssertSilhouetteWorkerUsesParentCancellationForProcessing()
    {
        const BindingFlags staticInternal = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var run = typeof(SilhouetteWorkerProcess).GetMethod(
            nameof(SilhouetteWorkerProcess.RunAsync),
            staticInternal) ?? throw new InvalidOperationException(
                "The silhouette worker entry point is unavailable.");
        var moveNext = GetAsyncMoveNext(run);
        var createLinked = typeof(CancellationTokenSource).GetMethod(
            nameof(CancellationTokenSource.CreateLinkedTokenSource),
            BindingFlags.Static | BindingFlags.Public,
            [typeof(CancellationToken)]) ?? throw new InvalidOperationException(
                "The linked worker-cancellation seam is unavailable.");
        var cancel = typeof(CancellationTokenSource).GetMethod(
            nameof(CancellationTokenSource.Cancel),
            Type.EmptyTypes) ?? throw new InvalidOperationException(
                "The worker-cancellation callback seam is unavailable.");
        var hardExit = typeof(Action<int>).GetMethod(
            nameof(Action<int>.Invoke),
            instance) ?? throw new InvalidOperationException(
                "The worker hard-exit callback seam is unavailable.");
        var openParent = typeof(SilhouetteWorkerParentHandle).GetMethod(
            nameof(SilhouetteWorkerParentHandle.OpenValidated),
            staticInternal) ?? throw new InvalidOperationException(
                "The retained parent-handle validation seam is unavailable.");
        var watch = typeof(SilhouetteWorkerParentHandle).GetMethod(
            nameof(SilhouetteWorkerParentHandle.WatchAsync),
            instance) ??
            throw new InvalidOperationException(
                "The cancellable parent-lifetime watcher is unavailable.");
        var openRoot = typeof(RoutingWatchedFileSystem).GetMethod(
            nameof(RoutingWatchedFileSystem.OpenOrdinaryRoot),
            staticInternal) ?? throw new InvalidOperationException(
                "The retained Capture-root handle seam is unavailable.");
        var createBinding = typeof(RoutingCaptureLibraryBindingModel).GetMethods(
                staticInternal)
            .Single(method =>
                method.Name == nameof(RoutingCaptureLibraryBindingModel.Create) &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(RoutingWatchedRootHandle));
        var requireBinding = typeof(RoutingCaptureLibraryBindingModel).GetMethod(
            nameof(RoutingCaptureLibraryBindingModel.RequireExact),
            staticInternal) ?? throw new InvalidOperationException(
                "The exact Capture-root binding guard is unavailable.");
        var process = typeof(SilhouetteProjectProcessor).GetMethod(
            nameof(SilhouetteProjectProcessor.ProcessAsync),
            instance) ?? throw new InvalidOperationException(
                "The silhouette processor entry point is unavailable.");

        var linkedOffset = FirstDirectCallOffset(moveNext, createLinked);
        var openParentOffset = FirstDirectCallOffset(moveNext, openParent);
        var openRootOffset = FirstDirectCallOffset(moveNext, openRoot);
        var createBindingOffset = FirstDirectCallOffset(moveNext, createBinding);
        var requireBindingOffset = FirstDirectCallOffset(moveNext, requireBinding);
        var watchOffset = FirstDirectCallOffset(moveNext, watch);
        var processingOffset = FirstDirectCallOffset(moveNext, process);
        var parentExitCallback = typeof(SilhouetteWorkerProcess)
            .GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .SingleOrDefault(method =>
                FirstDirectCallOffset(method, cancel) >= 0 &&
                FirstDirectCallOffset(method, hardExit) >= 0);
        var callbackCancelOffset = parentExitCallback is null
            ? -1
            : FirstDirectCallOffset(parentExitCallback, cancel);
        var callbackExitOffset = parentExitCallback is null
            ? -1
            : FirstDirectCallOffset(parentExitCallback, hardExit);
        Assert(linkedOffset >= 0 &&
               openParentOffset > linkedOffset &&
               openRootOffset > openParentOffset &&
               createBindingOffset > openRootOffset &&
               requireBindingOffset > createBindingOffset &&
               watchOffset > requireBindingOffset &&
               processingOffset > watchOffset &&
               parentExitCallback is not null &&
               callbackCancelOffset >= 0 && callbackExitOffset > callbackCancelOffset,
            "The silhouette worker must establish linked cancellation, retain and validate one parent handle, pin and validate one exact root handle, and start its watcher before processing; parent exit must cancel work before hard process exit.");
    }

    private static string[] ReplaceArgument(
        IEnumerable<string> arguments,
        string name,
        string replacement)
    {
        var copy = arguments.ToArray();
        var index = Array.IndexOf(copy, name);
        if (index < 0 || index + 1 >= copy.Length)
        {
            throw new InvalidOperationException(
                $"The expected worker argument '{name}' is unavailable.");
        }
        copy[index + 1] = replacement;
        return copy;
    }

    private static void AssertTraySwitchOrdersEveryCaptureLibraryBarrier()
    {
        const BindingFlags instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        var trayType = typeof(TrayApplicationContext);
        var switchMethod = trayType.GetMethod(
            "SaveAndApplyCaptureSettings",
            instancePrivate) ?? throw new InvalidOperationException(
                "The Tray Capture-library switch method is unavailable.");
        var switchLatch = trayType.GetField(
            "_captureLibrarySwitchInProgress",
            instancePrivate) ?? throw new InvalidOperationException(
                "The Tray Capture-library switch latch is unavailable.");
        var silhouetteOwner = trayType.GetField(
            "_silhouetteProcessingCoordinator",
            instancePrivate) ?? throw new InvalidOperationException(
                "The Tray silhouette coordinator owner is unavailable.");
        var pause = typeof(ReplayAutomaticStartCoordinator).GetMethod(
            nameof(ReplayAutomaticStartCoordinator.PauseAndDrainAsync),
            instance) ?? throw new InvalidOperationException(
                "The replay admission barrier is unavailable.");
        var requireIdle = trayType.GetMethod(
            "RequireCaptureLibrarySwitchIdle",
            instancePrivate) ?? throw new InvalidOperationException(
                "The Capture-library idle guard is unavailable.");
        var strictStop = typeof(SilhouetteProcessingCoordinator).GetMethod(
            nameof(SilhouetteProcessingCoordinator.DisposeAndRequireStopped),
            instance) ?? throw new InvalidOperationException(
                "The strict silhouette stop seam is unavailable.");
        var drainSettlements = trayType.GetMethod(
            "DrainSilhouetteSettlementsAsync",
            instancePrivate) ?? throw new InvalidOperationException(
                "The silhouette settlement drain is unavailable.");
        var currentRecovery = trayType.GetMethod(
            "CurrentCaptureRecoveryTask",
            instancePrivate) ?? throw new InvalidOperationException(
                "The Capture recovery ownership seam is unavailable.");
        var beginRecovery = trayType.GetMethod(
            "BeginCaptureRecovery",
            instancePrivate) ?? throw new InvalidOperationException(
                "The candidate Capture recovery seam is unavailable.");

        var latchOffset = FirstFieldAccessOffset(switchMethod, switchLatch);
        var pauseOffset = FirstDirectCallOffset(switchMethod, pause);
        var idleOffset = FirstDirectCallOffset(switchMethod, requireIdle);
        var strictStopOffset = FirstDirectCallOffset(switchMethod, strictStop);
        var settlementDrainOffset = FirstDirectCallOffset(switchMethod, drainSettlements);
        var currentRecoveryOffset = FirstDirectCallOffset(switchMethod, currentRecovery);
        var beginRecoveryOffset = FirstDirectCallOffset(switchMethod, beginRecovery);
        var silhouetteOwnerOffsets = FieldAccessOffsets(switchMethod, silhouetteOwner);

        Assert(currentRecoveryOffset >= 0 && latchOffset >= 0 &&
               pauseOffset > latchOffset && idleOffset > pauseOffset &&
               strictStopOffset > idleOffset &&
               silhouetteOwnerOffsets.Any(offset => offset < strictStopOffset) &&
               silhouetteOwnerOffsets.Any(offset => offset > strictStopOffset) &&
               settlementDrainOffset > strictStopOffset &&
               beginRecoveryOffset > settlementDrainOffset,
            "A Capture-library switch must latch before replay admission and idle checks, retain the old silhouette coordinator until strict child shutdown, drain settlement ownership, and only then recover the candidate root.");
    }

    internal static async Task AssertLateCaptureHostResponseCannotRestoreRevokedStateAsync()
    {
        var client = new LateResponseCaptureHostClient();
        using var recorder = new CaptureHostManualRecorder(client);
        _ = await recorder.SelectTargetAsync(new FakeWindow());
        var stateChanges = 0;
        var cameraChanges = 0;
        var committedProjects = 0;
        var journalChanges = 0;
        recorder.StateChanged += (_, _) => stateChanges++;
        recorder.ReactionCameraStateChanged += (_, _) => cameraChanges++;
        recorder.ProjectCommitted += (_, _) => committedProjects++;
        recorder.JournalChanged += (_, _) => journalChanges++;

        var start = recorder.StartAsync(CaptureSettings.Default);
        await client.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await recorder.AbortForCaptureLibraryAuthorityRevocationAsync()
            .WaitAsync(TimeSpan.FromSeconds(2));
        var notificationsAfterRevocation = stateChanges + cameraChanges;

        client.CompleteLateStart(new CaptureHostRecorderSnapshot(
            ManualCaptureState.Recording,
            new ManualCaptureTarget("Stale game", 1920, 1080),
            null,
            ReactionCameraStatus: new ReactionCameraRuntimeStatus(true, false)));
        await AssertThrowsAsync<InvalidOperationException>(
            () => start,
            "A host response completed after authority revocation must be rejected before any post-await state is applied.");

        Assert(recorder.State == ManualCaptureState.Failed &&
               recorder.Target is null &&
               recorder.ReactionCameraStatus.State == ReactionCameraRuntimeState.Failed &&
               stateChanges + cameraChanges == notificationsAfterRevocation &&
               committedProjects == 0 && journalChanges == 0 && client.AbortCalls == 1,
            "Late host responses and their catch/finally setters must not restore state, publish a project, or emit another notification after authority is revoked.");
    }

    private static int FirstDirectCallOffset(MethodBase caller, MethodBase callee)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x28 or 0x6F)) continue;
            var called = TryResolveMethod(caller, BitConverter.ToInt32(il, offset + 1));
            if (called is not null && called.Module == callee.Module &&
                called.MetadataToken == callee.MetadataToken)
            {
                return offset;
            }
        }
        return -1;
    }

    private static MethodInfo GetAsyncMoveNext(MethodInfo method)
    {
        var stateMachine = method.GetCustomAttribute<
            System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType ??
            throw new InvalidOperationException(
                $"The method '{method.Name}' is not asynchronous.");
        return stateMachine.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"The method '{method.Name}' has no async state-machine body.");
    }

    private static int FirstFieldAccessOffset(MethodBase caller, FieldInfo field) =>
        FieldAccessOffsets(caller, field).DefaultIfEmpty(-1).First();

    private static IReadOnlyList<int> FieldAccessOffsets(MethodBase caller, FieldInfo field)
    {
        var offsets = new List<int>();
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80)) continue;
            try
            {
                var accessed = caller.Module.ResolveField(
                    BitConverter.ToInt32(il, offset + 1),
                    caller.DeclaringType?.GetGenericArguments(),
                    caller is MethodInfo method ? method.GetGenericArguments() : null);
                if (accessed is not null && accessed.Module == field.Module &&
                    accessed.MetadataToken == field.MetadataToken)
                {
                    offsets.Add(offset);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or BadImageFormatException)
            {
            }
        }
        return offsets;
    }

    private static MethodBase? TryResolveMethod(MethodBase caller, int metadataToken)
    {
        try
        {
            return caller.Module.ResolveMethod(
                metadataToken,
                caller.DeclaringType?.GetGenericArguments(),
                caller is MethodInfo method ? method.GetGenericArguments() : null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BlockingReplayController : IReplayCaptureController
    {
        private readonly TaskCompletionSource _firstStartEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstStart = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private ReplayCaptureStatus _status = new(
            ReplayCaptureState.Off,
            null,
            null,
            TimeSpan.Zero,
            0);
        private int _startCount;
        private int _activeStarts;

        internal TaskCompletionSource FirstStartEntered => _firstStartEntered;
        internal int StartCount => Volatile.Read(ref _startCount);
        internal int ActiveStarts => Volatile.Read(ref _activeStarts);
        public ReplayCaptureStatus ReplayStatus => Volatile.Read(ref _status);
        public event EventHandler? ReplayStateChanged;

        internal void ReleaseFirstStart() => _releaseFirstStart.TrySetResult();

        public async Task StartReplayAsync(
            CaptureSettings settings,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _startCount);
            Interlocked.Increment(ref _activeStarts);
            try
            {
                if (call == 1)
                {
                    _firstStartEntered.TrySetResult();
                    await _releaseFirstStart.Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                Volatile.Write(ref _status, ReplayStatus with
                {
                    State = ReplayCaptureState.Buffering,
                    Target = new ManualCaptureTarget("Next game", 1920, 1080)
                });
                ReplayStateChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                Interlocked.Decrement(ref _activeStarts);
            }
        }

        public Task StopReplayAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ManualCaptureResult?> SaveReplayAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ManualCaptureResult?>(null);
    }

    private sealed class LateResponseCaptureHostClient : ICaptureHostRecorderClient,
        ICaptureLibraryAuthorityAbort
    {
        private readonly TaskCompletionSource<CaptureHostRecorderSnapshot> _lateStart = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource StartEntered => _startEntered;
        internal int AbortCalls { get; private set; }

        internal void CompleteLateStart(CaptureHostRecorderSnapshot snapshot) =>
            _lateStart.TrySetResult(snapshot);

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
            _startEntered.TrySetResult();
            // Deliberately ignore the operation token to emulate a host response that won the
            // cancellation race and arrived after the authority latch was set.
            return await _lateStart.Task.ConfigureAwait(false);
        }

        public Task<CaptureHostRecorderSnapshot> StopRecordingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> StartReplayAsync(
            CaptureSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> StopReplayAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> SaveReplayAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task<CaptureHostRecorderSnapshot> DisableReactionCameraAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadySnapshot());

        public Task AbortForCaptureLibraryAuthorityRevocationAsync()
        {
            AbortCalls++;
            return Task.CompletedTask;
        }

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

    private sealed class FakeWindow : System.Windows.Forms.IWin32Window
    {
        public nint Handle => (nint)1;
    }
}
