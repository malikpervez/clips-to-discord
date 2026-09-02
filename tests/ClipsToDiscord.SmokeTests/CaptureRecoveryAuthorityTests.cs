using System.Reflection;
using System.Runtime.CompilerServices;
using ClipsToDiscord;

internal static class CaptureRecoveryAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 20, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertOriginalPromotionGuardStopsBeforeMoveAsync(
            Path.Combine(testRoot, "promotion-guard"));
        await AssertJournalProjectionGuardStopsBeforeSaveAsync(
            Path.Combine(testRoot, "journal-guard"));
        AssertStagingCleanupGuardsStopBeforeDelete(
            Path.Combine(testRoot, "staging-cleanup-guards"));
        await AssertProjectCleanupGuardsStopBeforeDeleteAsync(
            Path.Combine(testRoot, "project-cleanup-guards"));
        AssertTrayOwnsAuthorityAwareStartupCleanupPipeline();
    }

    private static async Task AssertOriginalPromotionGuardStopsBeforeMoveAsync(string root)
    {
        var deniedRoot = Path.Combine(root, "denied");
        var first = await CreateOriginalPromotionAsync(deniedRoot, "guard-throws", second: 0);
        var guardCalls = 0;
        var authorityFailure = new CaptureRecoveryAuthorityDeniedException();
        Exception? observed = null;
        try
        {
            await CaptureJournalStartupRecovery.RecoverAsync(
                deniedRoot,
                Now,
                beforeMutation: () =>
                {
                    guardCalls++;
                    Assert(
                        File.Exists(first.StagedPath) &&
                        !File.Exists(first.DestinationPath) &&
                        CaptureJournalStore.Load(deniedRoot, first.Intent.ClipId).Status ==
                        CaptureJournalLoadStatus.Missing,
                        "The recovery authority guard must run before the staged original is moved or journaled.");
                    throw authorityFailure;
                });
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        var afterDenied = await CaptureJournalPromotionIntentStore.InspectOriginalAsync(
            deniedRoot,
            first.Intent.ClipId);
        Assert(
            ReferenceEquals(observed, authorityFailure) &&
            guardCalls == 1 &&
            afterDenied.Status == CaptureJournalPromotionStatus.MoveRequired &&
            File.Exists(first.StagedPath) &&
            !File.Exists(first.DestinationPath) &&
            CaptureJournalStore.Load(deniedRoot, first.Intent.ClipId).Status ==
            CaptureJournalLoadStatus.Missing,
            "A denied recovery permit must stop immediately before original promotion with all durable state unchanged.");

        var cancellationRoot = Path.Combine(root, "cancelled");
        var second = await CreateOriginalPromotionAsync(
            cancellationRoot,
            "guard-cancels",
            second: 1);
        using var cancellation = new CancellationTokenSource();
        var cancellationGuardCalls = 0;
        var cancellationObserved = false;
        try
        {
            await CaptureJournalStartupRecovery.RecoverAsync(
                cancellationRoot,
                Now,
                cancellation.Token,
                () =>
                {
                    cancellationGuardCalls++;
                    Assert(
                        File.Exists(second.StagedPath) &&
                        !File.Exists(second.DestinationPath),
                        "Cancellation must be requested before the selected original is promoted.");
                    cancellation.Cancel();
                });
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        var afterCancellation = await CaptureJournalPromotionIntentStore.InspectOriginalAsync(
            cancellationRoot,
            second.Intent.ClipId);
        Assert(
            cancellationObserved &&
            cancellationGuardCalls == 1 &&
            afterCancellation.Status == CaptureJournalPromotionStatus.MoveRequired &&
            File.Exists(second.StagedPath) &&
            !File.Exists(second.DestinationPath) &&
            CaptureJournalStore.Load(cancellationRoot, second.Intent.ClipId).Status ==
            CaptureJournalLoadStatus.Missing,
            "Cancellation raised by the recovery guard must be rechecked before original promotion mutates files or journal state.");
    }

    private static async Task AssertJournalProjectionGuardStopsBeforeSaveAsync(string root)
    {
        var pending = await CreateOriginalPromotionAsync(root, "journal-guard", second: 0,
            reactionCameraRequested: true);
        _ = await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
            root,
            pending.Intent.ClipId);
        _ = await CaptureJournalPromotionIntentStore.CommitOriginalAsync(
            root,
            pending.Intent.ClipId,
            now: Now.AddMinutes(-10));
        await CaptureJournalPromotionIntentStore.CompleteOriginalAsync(
            root,
            pending.Intent.ClipId);

        var before = CaptureJournalStore.Load(root, pending.Intent.ClipId).Document ??
            throw new InvalidOperationException("The journal-guard fixture was not committed.");
        var journalPath = CaptureJournalStore.GetPath(root, pending.Intent.ClipId);
        var beforeBytes = File.ReadAllBytes(journalPath);
        Assert(before.State == CaptureJournalState.OriginalCommitted,
            "The journal-guard fixture must begin at OriginalCommitted.");

        var authorityFailure = new CaptureRecoveryAuthorityDeniedException();
        var guardCalls = 0;
        Exception? observed = null;
        try
        {
            await CaptureJournalStartupRecovery.RecoverAsync(
                root,
                Now,
                beforeMutation: () =>
                {
                    guardCalls++;
                    var current = CaptureJournalStore.Load(root, pending.Intent.ClipId).Document;
                    Assert(
                        current?.State == before.State &&
                        current.Generation == before.Generation &&
                        File.ReadAllBytes(journalPath).SequenceEqual(beforeBytes),
                        "The journal recovery guard must run immediately before the first generation-changing save.");
                    throw authorityFailure;
                });
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        var afterDenied = CaptureJournalStore.Load(root, pending.Intent.ClipId).Document;
        Assert(
            ReferenceEquals(observed, authorityFailure) &&
            guardCalls == 1 &&
            afterDenied?.State == before.State &&
            afterDenied.Generation == before.Generation &&
            File.ReadAllBytes(journalPath).SequenceEqual(beforeBytes),
            "A denied recovery permit must preserve the exact OriginalCommitted journal generation and failure state.");

        using var cancellation = new CancellationTokenSource();
        var cancellationGuardCalls = 0;
        var cancellationObserved = false;
        try
        {
            await CaptureJournalStartupRecovery.RecoverAsync(
                root,
                Now,
                cancellation.Token,
                () =>
                {
                    cancellationGuardCalls++;
                    cancellation.Cancel();
                });
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        Assert(
            cancellationObserved &&
            cancellationGuardCalls == 1 &&
            CaptureJournalStore.Load(root, pending.Intent.ClipId).Document?.State == before.State &&
            CaptureJournalStore.Load(root, pending.Intent.ClipId).Document?.Generation ==
            before.Generation &&
            File.ReadAllBytes(journalPath).SequenceEqual(beforeBytes),
            "Cancellation requested by the journal recovery guard must be observed before any CAS transition is saved.");
    }

    private static void AssertStagingCleanupGuardsStopBeforeDelete(string root)
    {
        var deniedStageRoot = Path.Combine(root, "stage-denied");
        var staging = CaptureLibraryLayout.GetStagingDirectory(deniedStageRoot);
        Directory.CreateDirectory(staging);

        var deniedStage = Path.Combine(
            staging,
            $"manual-capture-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(deniedStage, [1, 2, 3]);
        File.SetLastWriteTimeUtc(deniedStage, Now.AddDays(-2).UtcDateTime);
        var authorityFailure = new CaptureRecoveryAuthorityDeniedException();
        Exception? observed = null;
        try
        {
            _ = CaptureStagingRecovery.RemoveOrphanedManualCaptures(
                deniedStageRoot,
                Now,
                TimeSpan.FromHours(24),
                beforeDelete: () =>
                {
                    Assert(File.Exists(deniedStage),
                        "The staging cleanup guard must run before deleting its candidate.");
                    throw authorityFailure;
                });
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Assert(ReferenceEquals(observed, authorityFailure) && File.Exists(deniedStage),
            "A denied staging cleanup permit must preserve the candidate byte-for-byte.");

        var cancelledStageRoot = Path.Combine(root, "stage-cancelled");
        var cancelledStaging = CaptureLibraryLayout.GetStagingDirectory(cancelledStageRoot);
        Directory.CreateDirectory(cancelledStaging);
        var cancelledStage = Path.Combine(
            cancelledStaging,
            $"replay-save-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(cancelledStage, [4, 5, 6]);
        File.SetLastWriteTimeUtc(cancelledStage, Now.AddDays(-2).UtcDateTime);
        using var stagingCancellation = new CancellationTokenSource();
        var cancellationObserved = false;
        try
        {
            _ = CaptureStagingRecovery.RemoveOrphanedManualCaptures(
                cancelledStageRoot,
                Now,
                TimeSpan.FromHours(24),
                stagingCancellation.Token,
                () => stagingCancellation.Cancel());
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Assert(cancellationObserved && File.Exists(cancelledStage),
            "Staging cleanup must recheck cancellation after its callback and before file deletion.");

        var deniedTemporaryRoot = Path.Combine(root, "temporary-denied");
        var deniedProject = CreateTemporaryCameraProject(deniedTemporaryRoot, "a");
        observed = null;
        try
        {
            _ = CaptureStagingRecovery.RemoveOrphanedReactionCameraProjects(
                deniedTemporaryRoot,
                Now,
                TimeSpan.FromHours(24),
                beforeDelete: () =>
                {
                    Assert(deniedProject.Files.All(File.Exists),
                        "The temporary-project cleanup guard must run before deleting the first owned file.");
                    throw authorityFailure;
                });
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Assert(
            ReferenceEquals(observed, authorityFailure) &&
            Directory.Exists(deniedProject.Directory) &&
            deniedProject.Files.All(File.Exists),
            "A denied temporary-project cleanup permit must preserve its directory and every owned file.");

        var cancelledTemporaryRoot = Path.Combine(root, "temporary-cancelled");
        var cancelledProject = CreateTemporaryCameraProject(cancelledTemporaryRoot, "b");
        using var projectCancellation = new CancellationTokenSource();
        cancellationObserved = false;
        try
        {
            _ = CaptureStagingRecovery.RemoveOrphanedReactionCameraProjects(
                cancelledTemporaryRoot,
                Now,
                TimeSpan.FromHours(24),
                projectCancellation.Token,
                () => projectCancellation.Cancel());
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Assert(
            cancellationObserved &&
            Directory.Exists(cancelledProject.Directory) &&
            cancelledProject.Files.All(File.Exists),
            "Temporary-project cleanup must recheck cancellation before deleting the first file.");
    }

    private static async Task AssertProjectCleanupGuardsStopBeforeDeleteAsync(string root)
    {
        var deniedRoot = Path.Combine(root, "denied");
        var denied = await CreateOrphanedProjectAsync(deniedRoot, "denied", second: 0);
        var authorityFailure = new CaptureRecoveryAuthorityDeniedException();
        Exception? observed = null;
        try
        {
            _ = CaptureProjectStore.RemoveOrphanedProjects(
                deniedRoot,
                Now,
                TimeSpan.FromDays(30),
                beforeDelete: () =>
                {
                    Assert(
                        Directory.Exists(denied.ProjectDirectory) &&
                        denied.ProjectFiles.All(File.Exists),
                        "The committed-project cleanup guard must run before deleting the first owned file.");
                    throw authorityFailure;
                });
        }
        catch (Exception exception)
        {
            observed = exception;
        }
        Assert(
            ReferenceEquals(observed, authorityFailure) &&
            Directory.Exists(denied.ProjectDirectory) &&
            denied.ProjectFiles.All(File.Exists),
            "A denied committed-project cleanup permit must preserve the complete project.");

        var cancelledRoot = Path.Combine(root, "cancelled");
        var cancelled = await CreateOrphanedProjectAsync(
            cancelledRoot,
            "cancelled",
            second: 1);
        using var cancellation = new CancellationTokenSource();
        var cancellationObserved = false;
        try
        {
            _ = CaptureProjectStore.RemoveOrphanedProjects(
                cancelledRoot,
                Now,
                TimeSpan.FromDays(30),
                cancellation.Token,
                () => cancellation.Cancel());
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Assert(
            cancellationObserved &&
            Directory.Exists(cancelled.ProjectDirectory) &&
            cancelled.ProjectFiles.All(File.Exists),
            "Committed-project cleanup must recheck cancellation after its callback and before deleting the first file.");
    }

    private static void AssertTrayOwnsAuthorityAwareStartupCleanupPipeline()
    {
        const BindingFlags instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags staticInternal = BindingFlags.Static | BindingFlags.NonPublic;
        var trayType = typeof(TrayApplicationContext);
        var constructor = trayType.GetConstructors(instance).Single(candidate =>
            candidate.GetParameters().Length == 0);
        var recover = trayType.GetMethod(
            "RecoverCaptureJournalsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The Tray Capture recovery seam is unavailable.");
        var stateMachineType = recover.GetCustomAttribute<AsyncStateMachineAttribute>()?
            .StateMachineType ?? throw new InvalidOperationException(
                "Tray Capture recovery must remain an awaited asynchronous pipeline.");
        var moveNext = stateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The Tray Capture recovery state machine is unavailable.");
        var journalRecovery = typeof(CaptureJournalStartupRecovery).GetMethod(
            nameof(CaptureJournalStartupRecovery.RecoverAsync),
            staticInternal) ?? throw new InvalidOperationException(
                "The capture-journal startup recovery seam is unavailable.");
        var stagingCleanup = typeof(CaptureStagingRecovery).GetMethod(
            nameof(CaptureStagingRecovery.RemoveOrphanedManualCaptures),
            staticInternal) ?? throw new InvalidOperationException(
                "The manual staging cleanup seam is unavailable.");
        var temporaryProjectCleanup = typeof(CaptureStagingRecovery).GetMethod(
            nameof(CaptureStagingRecovery.RemoveOrphanedReactionCameraProjects),
            staticInternal) ?? throw new InvalidOperationException(
                "The temporary camera-project cleanup seam is unavailable.");
        var committedProjectCleanup = typeof(CaptureProjectStore).GetMethod(
            nameof(CaptureProjectStore.RemoveOrphanedProjects),
            staticInternal) ?? throw new InvalidOperationException(
                "The committed camera-project cleanup seam is unavailable.");

        var journalOffset = FirstDirectCallOffset(moveNext, journalRecovery);
        var stagingOffset = FirstDirectCallOffset(moveNext, stagingCleanup);
        var temporaryProjectOffset = FirstDirectCallOffset(moveNext, temporaryProjectCleanup);
        var committedProjectOffset = FirstDirectCallOffset(moveNext, committedProjectCleanup);
        Assert(
            CallsDirectly(constructor, recover) &&
            journalOffset >= 0 &&
            stagingOffset > journalOffset &&
            temporaryProjectOffset > stagingOffset &&
            committedProjectOffset > temporaryProjectOffset &&
            !CallsDirectly(constructor, stagingCleanup) &&
            !CallsDirectly(constructor, temporaryProjectCleanup) &&
            !CallsDirectly(constructor, committedProjectCleanup),
            "Tray startup must route promotion recovery and every destructive cleanup phase through its authority-aware recovery pipeline, in promotion-before-cleanup order, rather than deleting directly in the constructor.");
    }

    private static async Task<OriginalPromotionFixture> CreateOriginalPromotionAsync(
        string root,
        string name,
        int second,
        bool reactionCameraRequested = false)
    {
        var staging = CaptureLibraryLayout.GetStagingDirectory(root);
        var game = CaptureLibraryLayout.GetRecordingDirectory(root, "Authority Game");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(game);
        var stagedPath = Path.Combine(staging, $"{name}.mp4");
        var destinationPath = Path.Combine(
            game,
            $"Authority Game__2026-08-29__16-00-{second:00}.mp4");
        await File.WriteAllBytesAsync(stagedPath, [1, 3, 5, (byte)(7 + second)]);
        var requested = reactionCameraRequested
            ? new[] { CaptureJournalArtifactKinds.Landscape }
            : [];
        var intent = await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
            root,
            stagedPath,
            destinationPath,
            CaptureJournalSourceKind.ManualCapture,
            "Authority Game",
            Now.AddMinutes(-10).AddSeconds(second),
            TimeSpan.FromSeconds(8),
            1280,
            720,
            reactionCameraRequested,
            requested,
            now: Now.AddMinutes(-10).AddSeconds(second));
        return new OriginalPromotionFixture(intent, stagedPath, destinationPath);
    }

    private static TemporaryProjectFixture CreateTemporaryCameraProject(string root, string seed)
    {
        var projects = CaptureLibraryLayout.GetProjectsDirectory(root);
        Directory.CreateDirectory(projects);
        var projectId = new string(seed[0], 32);
        var directory = Directory.CreateDirectory(Path.Combine(
            projects,
            $".capture-project-{projectId}-{Guid.NewGuid():N}.tmp"));
        var camera = Path.Combine(directory.FullName, CaptureProjectStore.CameraLayerFileName);
        var manifest = Path.Combine(directory.FullName, CaptureProjectStore.ManifestFileName);
        File.WriteAllBytes(camera, [8, 9, 10]);
        File.WriteAllText(manifest, "{}");
        directory.LastWriteTimeUtc = Now.AddDays(-2).UtcDateTime;
        return new TemporaryProjectFixture(directory.FullName, [camera, manifest]);
    }

    private static async Task<OrphanedProjectFixture> CreateOrphanedProjectAsync(
        string root,
        string name,
        int second)
    {
        var game = CaptureLibraryLayout.GetRecordingDirectory(root, "Orphan Game");
        var staging = CaptureLibraryLayout.GetStagingDirectory(root);
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(staging);
        var gameplayPath = Path.Combine(
            game,
            $"Orphan Game__2026-06-01__12-00-{second:00}.mp4");
        var cameraStage = Path.Combine(staging, $"{name}-camera.mp4");
        await File.WriteAllBytesAsync(gameplayPath, [11, 12, 13, (byte)(14 + second)]);
        await File.WriteAllBytesAsync(cameraStage, [21, 22, 23, (byte)(24 + second)]);
        var createdUtc = Now.AddDays(-60).AddSeconds(second);
        var project = await CaptureProjectStore.SaveReactionCameraLayerAsync(
            root,
            gameplayPath,
            cameraStage,
            TimeSpan.FromSeconds(100),
            TimeSpan.FromSeconds(100),
            TimeSpan.FromSeconds(8),
            mirrorCamera: true,
            CaptureCompositionSnapshotFactory.Create(
                CaptureSettings.Default with { LibraryRoot = root },
                gameplayPath,
                mirrorCamera: true,
                createdUtc),
            createdUtc);
        File.Delete(gameplayPath);
        Directory.SetLastWriteTimeUtc(project.ProjectDirectory, Now.AddDays(-60).UtcDateTime);
        var projectFiles = Directory.EnumerateFiles(
                project.ProjectDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        return new OrphanedProjectFixture(project.ProjectDirectory, projectFiles);
    }

    private static bool CallsDirectly(MethodBase caller, MethodBase callee) =>
        FirstDirectCallOffset(caller, callee) >= 0;

    private static int FirstDirectCallOffset(MethodBase caller, MethodBase callee)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x28 or 0x6F)) continue;
            try
            {
                var called = caller.Module.ResolveMethod(
                    BitConverter.ToInt32(il, offset + 1),
                    caller.DeclaringType?.GetGenericArguments(),
                    caller is MethodInfo method ? method.GetGenericArguments() : null);
                if (called is not null && called.Module == callee.Module &&
                    called.MetadataToken == callee.MetadataToken)
                {
                    return offset;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or BadImageFormatException)
            {
            }
        }
        return -1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CaptureRecoveryAuthorityDeniedException : Exception;

    private sealed record OriginalPromotionFixture(
        CaptureJournalOriginalPromotionIntent Intent,
        string StagedPath,
        string DestinationPath);

    private sealed record TemporaryProjectFixture(string Directory, string[] Files);

    private sealed record OrphanedProjectFixture(string ProjectDirectory, string[] ProjectFiles);
}
