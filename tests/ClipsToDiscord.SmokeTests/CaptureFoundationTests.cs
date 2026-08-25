using ClipsToDiscord;
using NAudio.CoreAudioApi;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class CaptureFoundationTests
{
    public static void Run(string testRoot)
    {
        AssertCapabilityProbeShape();
        AssertCaptureProfiles();
        AssertCaptureSettings();
        AssertReactionCameraSettingsAndMemory();
        AssertReactionCameraTrayIcons();
        AssertAudioSessionTrackPlans(testRoot);
        AssertManualCaptureStatePolicy();
        AssertGameWindowDiscovery();
        AssertExactWindowCaptureInterop();
        AssertReplayVideoSegmentsWhenEnabled(testRoot);
        AssertEncodedReplayRing();
        AssertReplayArmingPolicy();
        AssertReplayTargetLifetimeSubscriptions();
        AssertReplayAudioRing();
        AssertCaptureAudioMuxContract();
        AssertFfmpegPublicFailure();
        AssertCaptureHostLifecycle();
        ReactionCameraStartupTests.Run();
        AssertLibraryLayout(testRoot);
        AssertCaptureStagingRecovery(testRoot);
        AssertCaptureProjectStore(testRoot);
        AssertCaptureCompositionStore(testRoot);
        SilhouetteRenditionTests.Run(testRoot);
        GalleryRenditionTests.Run(testRoot);
        AssertOutputPolicy(testRoot);
        AssertReplaySaveFailureCleanup(testRoot);
        AssertReplayCameraPersistenceCannotBlockGameplay(testRoot);
        AssertGallerySourceProvenance(testRoot);
    }

    private static void AssertCapabilityProbeShape()
    {
        var graphicsProbeCalls = 0;
        var unsupported = new WindowsCaptureCapabilityProbe(
            () => false,
            () =>
            {
                graphicsProbeCalls++;
                return true;
            }).Inspect();
        Assert(
            unsupported.Readiness == CaptureReadiness.UnsupportedWindowsVersion &&
            !unsupported.OperatingSystemSupported &&
            !unsupported.GraphicsCaptureSupported &&
            !unsupported.CanStartCaptureSpike &&
            graphicsProbeCalls == 0,
            "The capture probe must reject unsupported Windows builds before calling WinRT.");

        var unavailable = new WindowsCaptureCapabilityProbe(() => true, () => false).Inspect();
        Assert(
            unavailable.Readiness == CaptureReadiness.GraphicsCaptureUnavailable &&
            unavailable.OperatingSystemSupported &&
            !unavailable.GraphicsCaptureSupported,
            "The capture probe must distinguish an unavailable Graphics Capture API.");

        var ready = new WindowsCaptureCapabilityProbe(() => true, () => true).Inspect();
        Assert(
            ready.Readiness == CaptureReadiness.Ready && ready.CanStartCaptureSpike,
            "The capture probe must admit a supported Windows Graphics Capture environment.");

        var failed = new WindowsCaptureCapabilityProbe(
            () => true,
            () => throw new InvalidOperationException("machine-specific driver detail")).Inspect();
        Assert(
            failed.Readiness == CaptureReadiness.ProbeFailed &&
            failed.OperatingSystemSupported &&
            !failed.GraphicsCaptureSupported,
            "Capability exceptions must collapse to the stable probe-failed state.");
    }

    private static void AssertOutputPolicy(string testRoot)
    {
        var outputRoot = Directory.CreateDirectory(Path.Combine(testRoot, "capture-output")).FullName;
        var localWallClock = new DateTime(2026, 8, 22, 14, 35, 41, 237, DateTimeKind.Unspecified);
        var capturedAt = new DateTimeOffset(localWallClock, TimeZoneInfo.Local.GetUtcOffset(localWallClock));
        var fileName = CaptureOutputPolicy.CreateFileName("Counter-Strike 2", capturedAt);
        Assert(
            fileName == "Counter-Strike 2__2026-08-22__14-35-41.mp4",
            "ClipCord recording names must use the stable game and local timestamp contract.");
        Assert(
            UploadedFolder.GetGameFolderName(fileName) == "Counter-Strike 2",
            "ClipCord recording names must remain compatible with existing game inference.");

        var hostileName = CaptureOutputPolicy.CreateFileName("..\\CON:/  ranked", capturedAt);
        Assert(
            Path.GetFileName(hostileName) == hostileName &&
            hostileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !hostileName.Contains('\\') &&
            !hostileName.Contains('/'),
            "Game metadata must never steer a recording outside the selected output folder.");

        var firstPath = CaptureOutputPolicy.CreateAvailablePath(outputRoot, "Valorant", capturedAt);
        File.WriteAllBytes(firstPath, [1, 2, 3]);
        var secondPath = CaptureOutputPolicy.CreateAvailablePath(outputRoot, "Valorant", capturedAt);
        Assert(
            !firstPath.Equals(secondPath, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(secondPath).EndsWith(".001.mp4", StringComparison.Ordinal),
            "Same-millisecond recordings must receive deterministic collision suffixes.");
        Assert(
            UploadedFolder.GetGameFolderName(Path.GetFileName(secondPath)) == "Valorant",
            "Collision suffixes must preserve existing game inference.");
        File.WriteAllBytes(secondPath, [4, 5, 6]);
        var thirdPath = CaptureOutputPolicy.CreateAvailablePath(outputRoot, "Valorant", capturedAt);
        Assert(
            Path.GetFileName(thirdPath).EndsWith(".002.mp4", StringComparison.Ordinal),
            "Repeated same-second recordings must advance collision suffixes without overwriting an earlier clip.");

        var stagedMove = Path.Combine(outputRoot, "staged-move.mp4");
        var occupiedMove = Path.Combine(outputRoot, "occupied-move.mp4");
        File.WriteAllBytes(stagedMove, [7, 8, 9]);
        File.WriteAllBytes(occupiedMove, [10, 11, 12]);
        var collisionRejected = false;
        try
        {
            CaptureOutputPolicy.MoveCompletedFile(stagedMove, occupiedMove);
        }
        catch (IOException)
        {
            collisionRejected = true;
        }
        Assert(
            collisionRejected && File.Exists(stagedMove) &&
            File.ReadAllBytes(occupiedMove).SequenceEqual(new byte[] { 10, 11, 12 }),
            "The final atomic move must reject a late destination collision without replacing either user's clip.");

        var missingRootRejected = false;
        try
        {
            CaptureOutputPolicy.CreateAvailablePath(
                Path.Combine(testRoot, "missing-output"),
                "Game",
                capturedAt);
        }
        catch (DirectoryNotFoundException)
        {
            missingRootRejected = true;
        }
        Assert(missingRootRejected, "Capture must not silently redirect a missing output folder.");
    }

    private static void AssertCaptureProfiles()
    {
        var estimates = new List<CaptureSizeEstimate>();
        foreach (var resolution in Enum.GetValues<CaptureResolution>())
        {
            foreach (var fps in new[] { 30, 60 })
            {
                var profile = new CaptureProfile(resolution, fps, TimeSpan.FromSeconds(60));
                var dimensions = CaptureProfileCatalog.GetDimensions(resolution);
                var estimate = CaptureProfileCatalog.Estimate(profile);
                estimates.Add(estimate);
                Assert(
                    dimensions.Width > 0 && dimensions.Height > 0 &&
                    estimate.LowerBoundBytes < estimate.ExpectedBytes &&
                    estimate.ExpectedBytes < estimate.UpperBoundBytes,
                    "Every supported quality profile must expose dimensions and an honest VBR range.");
            }
        }

        Assert(
            estimates.Zip(estimates.Skip(1), (left, right) => right.ExpectedBytes > left.ExpectedBytes).All(value => value),
            "Estimated clip size must increase from 30 to 60 fps and from 1080p through 4K.");

        var baseProfile = new CaptureProfile(
            CaptureResolution.FullHd1080p,
            60,
            TimeSpan.FromSeconds(60));
        var withoutCamera = CaptureProfileCatalog.Estimate(baseProfile);
        var withCamera = CaptureProfileCatalog.Estimate(baseProfile with { IncludeReactionCamera = true });
        var withoutAudio = CaptureProfileCatalog.Estimate(baseProfile, includeAudio: false);
        Assert(
            withCamera.ExpectedBytes > withoutCamera.ExpectedBytes &&
            withCamera.ReactionCameraBitrateKbps == CaptureProfileCatalog.ReactionCameraBitrateKbps,
            "The size estimate must account for the separate reaction-camera layer.");
        Assert(
            withoutCamera.AudioBitrateKbps == CaptureProfileCatalog.MixedAudioBitrateKbps &&
            withoutAudio.AudioBitrateKbps == 0 &&
            withoutCamera.ExpectedBytes - withoutAudio.ExpectedBytes ==
                (long)Math.Ceiling(CaptureProfileCatalog.MixedAudioBitrateKbps * 1000d / 8d * 60d),
            "All enabled audio inputs must compile into exactly one optional 192 kbps stream.");

        var invalidRejected = false;
        try
        {
            CaptureProfileCatalog.Estimate(baseProfile with { FramesPerSecond = 144 });
        }
        catch (ArgumentException)
        {
            invalidRejected = true;
        }
        Assert(invalidRejected, "Unimplemented capture rates must not receive misleading estimates.");
    }

    private static void AssertLibraryLayout(string testRoot)
    {
        var root = Path.Combine(testRoot, "library-root");
        var recordingDirectory = CaptureLibraryLayout.GetRecordingDirectory(
            root,
            "Counter-Strike 2");
        Assert(
            recordingDirectory == Path.Combine(
                Path.GetFullPath(root),
                "Library",
                "Game",
                "Counter-Strike 2"),
            "Original recordings must use the simple Library/Game/<Game> hierarchy.");

        var gameRoot = Directory.CreateDirectory(Path.Combine(
            root,
            CaptureLibraryLayout.LibraryFolderName,
            CaptureLibraryLayout.GameFolderName)).FullName;
        var established = Directory.CreateDirectory(Path.Combine(gameRoot, "Battlefield™ 6"));
        var laterDuplicate = Directory.CreateDirectory(Path.Combine(gameRoot, "Battlefield-6"));
        Directory.SetCreationTimeUtc(established.FullName, new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc));
        Directory.SetCreationTimeUtc(laterDuplicate.FullName, new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(gameRoot, "bf6"));
        Assert(
            CaptureLibraryLayout.CreateGameIdentity("Battlefield™ 6") ==
                CaptureLibraryLayout.CreateGameIdentity("Battlefield-6") &&
            CaptureLibraryLayout.CreateGameIdentity("Battlefield 6 TM") ==
                CaptureLibraryLayout.CreateGameIdentity("Battlefield-6") &&
            CaptureLibraryLayout.CreateGameIdentity("bf6") !=
                CaptureLibraryLayout.CreateGameIdentity("Battlefield-6") &&
            CaptureLibraryLayout.GetRecordingDirectory(root, "Battlefield-6") == established.FullName,
            "A captured game must reuse its oldest equivalent human-facing library folder instead of creating a punctuation or trademark duplicate.");

        var legacyRoot = Path.Combine(testRoot, "legacy-tm-library-root");
        var legacyGameRoot = Directory.CreateDirectory(Path.Combine(
            legacyRoot,
            CaptureLibraryLayout.LibraryFolderName,
            CaptureLibraryLayout.GameFolderName)).FullName;
        var legacyBattlefield = Directory.CreateDirectory(Path.Combine(
            legacyGameRoot,
            "Battlefield 6 TM"));
        Assert(
            CaptureLibraryLayout.GetRecordingDirectory(legacyRoot, "Battlefield-6") ==
            legacyBattlefield.FullName,
            "A legacy Battlefield 6 TM folder must be reused instead of creating another Battlefield spelling.");

        var hostileRecordingDirectory = CaptureLibraryLayout.GetRecordingDirectory(
            root,
            "..\\..\\CON:/");
        Assert(
            hostileRecordingDirectory.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Game metadata must not escape the ClipCord library root.");
        Assert(
            CaptureLibraryLayout.GetExportDirectory(root, "YouTube", "Counter-Strike 2") ==
            Path.Combine(Path.GetFullPath(root), "Exports", "YouTube", "Counter-Strike 2"),
            "Destination exports must remain separate from the one original recording.");
        Assert(
            CaptureLibraryLayout.GetProjectsDirectory(root).Contains(Path.Combine(".clipcord", "Projects"), StringComparison.Ordinal) &&
            CaptureLibraryLayout.GetStagingDirectory(root).Contains(Path.Combine(".clipcord", "Staging"), StringComparison.Ordinal) &&
            CaptureLibraryLayout.GetThumbnailsDirectory(root).Contains(Path.Combine(".clipcord", "Thumbnails"), StringComparison.Ordinal),
            "Projects, staging files, and thumbnails must stay out of the user-facing original library.");
    }

    private static void AssertCaptureStagingRecovery(string testRoot)
    {
        var libraryRoot = Directory.CreateDirectory(Path.Combine(testRoot, "capture-recovery")).FullName;
        var stagingRoot = Directory.CreateDirectory(
            CaptureLibraryLayout.GetStagingDirectory(libraryRoot)).FullName;
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var orphan = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.mp4");
        var orphanVideo = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.video.mp4");
        var orphanCamera = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.camera.mp4");
        var orphanAudio = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.game.wav");
        var orphanMicrophone = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.microphone.wav");
        var orphanChat = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.chat.wav");
        var replayToken = Guid.NewGuid().ToString("N");
        var orphanReplaySegment = Path.Combine(stagingRoot, $"replay-save-{replayToken}.0000.mp4");
        var orphanReplayList = Path.Combine(stagingRoot, $"replay-save-{replayToken}.concat.txt");
        var orphanReplayAudio = Path.Combine(stagingRoot, $"replay-save-{replayToken}.chat.wav");
        var orphanReplayCameraSegment = Path.Combine(stagingRoot, $"replay-save-{replayToken}.camera.0000.mp4");
        var orphanReplayCameraList = Path.Combine(stagingRoot, $"replay-save-{replayToken}.camera.concat.txt");
        var orphanReplayCamera = Path.Combine(stagingRoot, $"replay-save-{replayToken}.camera.mp4");
        var active = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.mp4");
        var unrelated = Path.Combine(stagingRoot, "user-recording.mp4");
        var hostileLookalike = Path.Combine(stagingRoot, $"manual-capture-{Guid.NewGuid():N}.user.wav");
        File.WriteAllBytes(orphan, [1]);
        File.WriteAllBytes(orphanVideo, [1]);
        File.WriteAllBytes(orphanCamera, [1]);
        File.WriteAllBytes(orphanAudio, [1]);
        File.WriteAllBytes(orphanMicrophone, [1]);
        File.WriteAllBytes(orphanChat, [1]);
        File.WriteAllBytes(orphanReplaySegment, [1]);
        File.WriteAllBytes(orphanReplayList, [1]);
        File.WriteAllBytes(orphanReplayAudio, [1]);
        File.WriteAllBytes(orphanReplayCameraSegment, [1]);
        File.WriteAllBytes(orphanReplayCameraList, [1]);
        File.WriteAllBytes(orphanReplayCamera, [1]);
        File.WriteAllBytes(active, [2]);
        File.WriteAllBytes(unrelated, [3]);
        File.WriteAllBytes(hostileLookalike, [4]);
        File.SetLastWriteTimeUtc(orphan, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanVideo, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanCamera, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanAudio, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanMicrophone, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanChat, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanReplaySegment, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanReplayList, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanReplayAudio, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanReplayCameraSegment, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanReplayCameraList, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(orphanReplayCamera, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(active, now.UtcDateTime - TimeSpan.FromMinutes(10));
        File.SetLastWriteTimeUtc(hostileLookalike, now.UtcDateTime - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(unrelated, now.UtcDateTime - TimeSpan.FromDays(2));

        var removed = CaptureStagingRecovery.RemoveOrphanedManualCaptures(
            libraryRoot,
            now,
            TimeSpan.FromHours(24));
        Assert(
            removed == 12 &&
            !File.Exists(orphan) &&
            !File.Exists(orphanVideo) &&
            !File.Exists(orphanCamera) &&
            !File.Exists(orphanAudio) &&
            !File.Exists(orphanMicrophone) &&
            !File.Exists(orphanChat) &&
            !File.Exists(orphanReplaySegment) &&
            !File.Exists(orphanReplayList) &&
            !File.Exists(orphanReplayAudio) &&
            !File.Exists(orphanReplayCameraSegment) &&
            !File.Exists(orphanReplayCameraList) &&
            !File.Exists(orphanReplayCamera) &&
            File.Exists(active) &&
            File.Exists(unrelated) &&
            File.Exists(hostileLookalike),
            "Capture recovery must remove only stale ClipCord-owned manual stages while preserving recent and unrelated files.");

        var projectsRoot = Directory.CreateDirectory(
            CaptureLibraryLayout.GetProjectsDirectory(libraryRoot)).FullName;
        var projectId = new string('a', 32);
        var oldTemporaryProject = Directory.CreateDirectory(Path.Combine(
            projectsRoot,
            $".capture-project-{projectId}-{Guid.NewGuid():N}.tmp"));
        File.WriteAllBytes(
            Path.Combine(oldTemporaryProject.FullName, CaptureProjectStore.CameraLayerFileName),
            [1]);
        File.WriteAllText(
            Path.Combine(oldTemporaryProject.FullName, CaptureProjectStore.ManifestFileName),
            "{}");
        var recentTemporaryProject = Directory.CreateDirectory(Path.Combine(
            projectsRoot,
            $".capture-project-{projectId}-{Guid.NewGuid():N}.tmp"));
        var hostileTemporaryProject = Directory.CreateDirectory(Path.Combine(
            projectsRoot,
            $".capture-project-{projectId}-{Guid.NewGuid():N}.tmp"));
        File.WriteAllText(Path.Combine(hostileTemporaryProject.FullName, "keep.txt"), "keep");
        var unrelatedProject = Directory.CreateDirectory(Path.Combine(projectsRoot, "user-project.tmp"));
        oldTemporaryProject.LastWriteTimeUtc = now.UtcDateTime - TimeSpan.FromDays(2);
        recentTemporaryProject.LastWriteTimeUtc = now.UtcDateTime - TimeSpan.FromMinutes(5);
        hostileTemporaryProject.LastWriteTimeUtc = now.UtcDateTime - TimeSpan.FromDays(2);
        unrelatedProject.LastWriteTimeUtc = now.UtcDateTime - TimeSpan.FromDays(2);

        var removedProjects = CaptureStagingRecovery.RemoveOrphanedReactionCameraProjects(
            libraryRoot,
            now,
            TimeSpan.FromHours(24));
        Assert(
            removedProjects == 1 &&
            !oldTemporaryProject.Exists &&
            recentTemporaryProject.Exists &&
            hostileTemporaryProject.Exists &&
            File.Exists(Path.Combine(hostileTemporaryProject.FullName, "keep.txt")) &&
            unrelatedProject.Exists,
            "Startup recovery must remove only stale, exact ClipCord camera-project stages and preserve recent or unfamiliar project data.");
    }

    private static void AssertCaptureProjectStore(string testRoot)
    {
        var root = Path.Combine(testRoot, "reaction-camera-project-store");
        var gameDirectory = Path.Combine(root, "Library", "Game", "Duskfade");
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(root);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(stagingDirectory);

        var gameplayPath = Path.Combine(gameDirectory, "Duskfade__2026-08-24__18-30-00.mp4");
        var gameplayBytes = Enumerable.Range(1, 96).Select(value => (byte)value).ToArray();
        File.WriteAllBytes(gameplayPath, gameplayBytes);
        var gameplayLastWriteUtc = File.GetLastWriteTimeUtc(gameplayPath);
        var cameraStage = Path.Combine(stagingDirectory, "manual-capture-camera.mp4");
        var cameraBytes = Enumerable.Range(0, 128).Select(value => (byte)(255 - value)).ToArray();
        File.WriteAllBytes(cameraStage, cameraBytes);

        var gameplayStart = TimeSpan.FromSeconds(800);
        var cameraStart = gameplayStart - TimeSpan.FromMilliseconds(375);
        var duration = TimeSpan.FromSeconds(18.25);
        var createdUtc = new DateTimeOffset(2026, 8, 24, 22, 30, 0, TimeSpan.Zero);
        var expectedRelativeGameplay = "Library/Game/Duskfade/Duskfade__2026-08-24__18-30-00.mp4";
        var expectedProjectId = CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(
            expectedRelativeGameplay.ToLowerInvariant().Replace('/', '\\'));
        Assert(
            CaptureProjectStore.CreateProjectId(root, gameplayPath) == expectedProjectId,
            "Reaction Camera project identity must be deterministic across Windows path casing and separators.");
        var compositionSnapshot = CaptureCompositionSnapshotFactory.Create(
            CaptureSettings.Default with { LibraryRoot = root },
            gameplayPath,
            mirrorCamera: true,
            createdUtc);

        var result = CaptureProjectStore.SaveReactionCameraLayerAsync(
                root,
                gameplayPath,
                cameraStage,
                gameplayStart,
                cameraStart,
                duration,
                mirrorCamera: true,
                compositionSnapshot,
                createdUtc)
            .GetAwaiter()
            .GetResult();
        var manifestJson = File.ReadAllText(result.ManifestPath);
        var manifestFromDisk = JsonSerializer.Deserialize<CaptureProjectManifest>(
            manifestJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var expectedCameraRelative =
            $".clipcord/Projects/{expectedProjectId}/{CaptureProjectStore.CameraLayerFileName}";
        var committedCompositionPath = CaptureCompositionStore.GetPath(root, expectedProjectId);
        var committedComposition = CaptureCompositionStore.LoadOrDefault(
            root,
            expectedProjectId,
            createdUtc);
        Assert(
            result.ProjectId == expectedProjectId &&
            result.ProjectDirectory == CaptureProjectStore.GetProjectDirectory(root, expectedProjectId) &&
            result.ManifestPath == CaptureProjectStore.GetManifestPath(root, expectedProjectId) &&
            result.CameraLayerPath == CaptureProjectStore.GetCameraLayerPath(root, expectedProjectId) &&
            File.Exists(result.ManifestPath) && File.Exists(result.CameraLayerPath) &&
            File.Exists(committedCompositionPath) &&
            committedComposition.Status == CaptureCompositionLoadStatus.Loaded &&
            committedComposition.Document.ProjectId == expectedProjectId &&
            committedComposition.Document.SchemaVersion == CaptureCompositionStore.CurrentSchemaVersion &&
            committedComposition.Document.SilhouetteLayouts.Select(layout => layout.ProfileId)
                .SequenceEqual(CompositionOrientationIds.All) &&
            !File.Exists(cameraStage),
            "A completed Reaction Camera layer, manifest, and immutable composition snapshot must be atomically promoted as one deterministic project directory.");
        Assert(
            manifestFromDisk is not null &&
            manifestFromDisk == result.Manifest &&
            manifestFromDisk.SchemaVersion == CaptureProjectStore.CurrentSchemaVersion &&
            manifestFromDisk.ProjectId == expectedProjectId &&
            manifestFromDisk.GameplayRelativePath == expectedRelativeGameplay &&
            manifestFromDisk.CameraLayerRelativePath == expectedCameraRelative &&
            manifestFromDisk.CameraStartOffsetTicks == TimeSpan.FromMilliseconds(-375).Ticks &&
            manifestFromDisk.DurationTicks == duration.Ticks &&
            manifestFromDisk.GameplayFingerprint.ByteLength == gameplayBytes.Length &&
            manifestFromDisk.GameplayFingerprint.Sha256.Length == 64 &&
            manifestFromDisk.CameraLayerFingerprint.ByteLength == cameraBytes.Length &&
            manifestFromDisk.CameraLayerFingerprint.Sha256.Length == 64 &&
            manifestFromDisk.MirrorCamera &&
            manifestFromDisk.CreatedUtc == createdUtc &&
            !Path.IsPathRooted(manifestFromDisk.GameplayRelativePath) &&
            !Path.IsPathRooted(manifestFromDisk.CameraLayerRelativePath) &&
            !manifestJson.Contains(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
            "The camera project manifest must preserve exact QPC timing and portable relative paths without leaking an absolute library path.");
        Assert(
            File.ReadAllBytes(gameplayPath).SequenceEqual(gameplayBytes) &&
            File.GetLastWriteTimeUtc(gameplayPath) == gameplayLastWriteUtc &&
            File.ReadAllBytes(result.CameraLayerPath).SequenceEqual(cameraBytes),
            "Promoting a camera sidecar must not move, rewrite, or timestamp-touch the immutable gameplay MP4.");

        File.WriteAllBytes(gameplayPath, gameplayBytes.Reverse().ToArray());
        Assert(
            !CaptureProjectStore.LoadProjectIndex(root).ContainsKey(gameplayPath),
            "A same-length gameplay replacement must fail the committed SHA-256 identity check and never be joined to its camera project.");
        File.WriteAllBytes(gameplayPath, gameplayBytes);
        File.WriteAllBytes(result.CameraLayerPath, cameraBytes.Reverse().ToArray());
        Assert(
            !CaptureProjectStore.LoadProjectIndex(root).ContainsKey(gameplayPath),
            "A same-length camera-layer replacement must fail the committed SHA-256 identity check and never be exposed as a valid project.");
        File.WriteAllBytes(result.CameraLayerPath, cameraBytes);
        Assert(
            CaptureProjectStore.LoadProjectIndex(root).ContainsKey(gameplayPath),
            "Restoring both exact immutable sources must restore the valid project identity without relying on path or byte length alone.");

        using (var preCancelled = new CancellationTokenSource())
        {
            preCancelled.Cancel();
            var cancellationObserved = false;
            try
            {
                _ = CaptureProjectStore.LoadProjectIndex(root, preCancelled.Token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            Assert(
                cancellationObserved,
                "A pre-cancelled Gallery project-index request must stop before touching project data.");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var callbackEntered = false;
            var cancellationObserved = false;
            try
            {
                _ = CaptureProjectStore.LoadProjectIndex(
                    root,
                    cancellation.Token,
                    _ =>
                    {
                        callbackEntered = true;
                        cancellation.Cancel();
                    });
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            Assert(
                callbackEntered && cancellationObserved,
                "Gallery project indexing must honor cancellation between project directories rather than draining every manifest first.");
        }

        var traversalRejected = false;
        try
        {
            _ = CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath("../outside.mp4");
        }
        catch (ArgumentException)
        {
            traversalRejected = true;
        }
        Assert(traversalRejected,
            "Deterministic project-id helpers must reject a gameplay path that escapes the library.");
        foreach (var rootedPath in new[]
                 {
                     "/outside.mp4",
                     "\\outside.mp4",
                     @"C:\outside.mp4",
                     "//server/share/outside.mp4"
                 })
        {
            var rootedRejected = false;
            try
            {
                _ = CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(rootedPath);
            }
            catch (ArgumentException)
            {
                rootedRejected = true;
            }
            Assert(
                rootedRejected,
                "Deterministic project-id helpers must reject rooted, drive-qualified, and UNC gameplay paths before normalization.");
        }

        var outsideCamera = Path.Combine(root, "camera-outside-staging.mp4");
        File.WriteAllBytes(outsideCamera, [1, 2, 3]);
        var outsideRejected = false;
        try
        {
            _ = CaptureProjectStore.SaveReactionCameraLayerAsync(
                    root,
                    gameplayPath,
                    outsideCamera,
                    gameplayStart,
                    cameraStart,
                    duration,
                    mirrorCamera: false,
                    CaptureCompositionSnapshotFactory.Create(
                        CaptureSettings.Default with { LibraryRoot = root },
                        gameplayPath,
                        mirrorCamera: false,
                        createdUtc),
                    createdUtc)
                .GetAwaiter()
                .GetResult();
        }
        catch (IOException)
        {
            outsideRejected = true;
        }
        Assert(
            outsideRejected && File.Exists(outsideCamera) &&
            File.ReadAllBytes(gameplayPath).SequenceEqual(gameplayBytes),
            "Camera promotion must reject media outside private Staging without touching either source file.");

        var secondGameplayPath = Path.Combine(gameDirectory, "Duskfade__2026-08-24__18-31-00.mp4");
        var secondGameplayBytes = new byte[] { 91, 92, 93, 94 };
        File.WriteAllBytes(secondGameplayPath, secondGameplayBytes);
        var lockedCameraStage = Path.Combine(stagingDirectory, "locked-camera-stage.mp4");
        File.WriteAllBytes(lockedCameraStage, [31, 32, 33, 34]);
        var projectsRoot = CaptureLibraryLayout.GetProjectsDirectory(root);
        var unrelatedProjectFile = Path.Combine(projectsRoot, "keep-unrelated.txt");
        File.WriteAllText(unrelatedProjectFile, "keep");
        var secondProjectId = CaptureProjectStore.CreateProjectId(root, secondGameplayPath);
        var copyFailureObserved = false;
        using (var cameraLock = new FileStream(
                   lockedCameraStage,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            try
            {
                _ = CaptureProjectStore.SaveReactionCameraLayerAsync(
                        root,
                        secondGameplayPath,
                        lockedCameraStage,
                        gameplayStart,
                        cameraStart,
                        duration,
                        mirrorCamera: false,
                        CaptureCompositionSnapshotFactory.Create(
                            CaptureSettings.Default with { LibraryRoot = root },
                            secondGameplayPath,
                            mirrorCamera: false,
                            createdUtc),
                        createdUtc)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (IOException)
            {
                copyFailureObserved = true;
            }
        }
        Assert(
            copyFailureObserved &&
            !Directory.Exists(CaptureProjectStore.GetProjectDirectory(root, secondProjectId)) &&
            !Directory.EnumerateDirectories(
                    projectsRoot,
                    ".capture-project-*.tmp",
                    SearchOption.TopDirectoryOnly)
                .Any() &&
            File.Exists(lockedCameraStage) &&
            File.Exists(unrelatedProjectFile) &&
            File.ReadAllBytes(secondGameplayPath).SequenceEqual(secondGameplayBytes),
            "A failed camera copy must remove only its owned temporary project and leave gameplay, source stage, and unrelated project data intact.");

        File.WriteAllText(
            Path.Combine(result.ProjectDirectory, SilhouetteRenditionStore.FileName),
            "{}");
        File.Delete(gameplayPath);
        Directory.SetLastWriteTimeUtc(
            result.ProjectDirectory,
            createdUtc.UtcDateTime - TimeSpan.FromDays(40));
        var malformedProject = Directory.CreateDirectory(Path.Combine(projectsRoot, new string('b', 32)));
        File.WriteAllText(
            Path.Combine(malformedProject.FullName, CaptureProjectStore.ManifestFileName),
            "{}");
        File.WriteAllBytes(
            Path.Combine(malformedProject.FullName, CaptureProjectStore.CameraLayerFileName),
            [5, 6, 7]);
        malformedProject.LastWriteTimeUtc = createdUtc.UtcDateTime - TimeSpan.FromDays(40);
        var orphanedRemoved = CaptureProjectStore.RemoveOrphanedProjects(
            root,
            createdUtc + TimeSpan.FromDays(40),
            TimeSpan.FromDays(30));
        Assert(
            orphanedRemoved == 1 &&
            !Directory.Exists(result.ProjectDirectory) &&
            malformedProject.Exists &&
            File.Exists(Path.Combine(malformedProject.FullName, CaptureProjectStore.CameraLayerFileName)) &&
            File.Exists(secondGameplayPath) &&
            File.Exists(unrelatedProjectFile),
            "Bounded project reconciliation must remove only an old valid project whose gameplay clip is gone and preserve live, malformed, or unrelated data.");
    }

    private static void AssertCaptureCompositionStore(string testRoot)
    {
        var root = Path.Combine(testRoot, "capture-composition-store");
        var settingsDirectory = Path.Combine(testRoot, "silhouette-preferences");
        var gameDirectory = Path.Combine(root, "Library", "Game", "Duskfade");
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(root);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var gameplayPath = Path.Combine(gameDirectory, "Duskfade__2026-08-24__20-15-00.mp4");
        var cameraStage = Path.Combine(stagingDirectory, "composition-camera.mp4");
        File.WriteAllBytes(gameplayPath, [1, 2, 3, 4, 5]);
        File.WriteAllBytes(cameraStage, [9, 8, 7, 6, 5]);
        var projectCreatedUtc = new DateTimeOffset(
            2026,
            8,
            25,
            0,
            15,
            0,
            TimeSpan.Zero);
        var initialCompositionSnapshot = CaptureCompositionSnapshotFactory.Create(
            CaptureSettings.Default with { LibraryRoot = root },
            gameplayPath,
            mirrorCamera: true,
            projectCreatedUtc);
        var project = CaptureProjectStore.SaveReactionCameraLayerAsync(
                root,
                gameplayPath,
                cameraStage,
                TimeSpan.FromSeconds(100),
                TimeSpan.FromSeconds(100.125),
                TimeSpan.FromSeconds(15),
                mirrorCamera: true,
                initialCompositionSnapshot,
                projectCreatedUtc)
            .GetAwaiter()
            .GetResult();
        var fallbackTime = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);
        File.Delete(CaptureCompositionStore.GetPath(root, project.ProjectId));

        var missing = CaptureCompositionStore.LoadOrDefault(root, project.ProjectId, fallbackTime);
        Assert(
            missing.Status == CaptureCompositionLoadStatus.Missing &&
            !missing.LoadedFromDisk &&
            missing.Document.ProjectId == project.ProjectId &&
            missing.Document.SchemaVersion == CaptureCompositionStore.CurrentSchemaVersion &&
            missing.Document.ModifiedUtc == fallbackTime &&
            missing.Document.SilhouetteLayouts.Select(layout => layout.ProfileId)
                .SequenceEqual(CompositionOrientationIds.All) &&
            !File.Exists(CaptureCompositionStore.GetPath(root, project.ProjectId)),
            "A camera project without composition state must receive the two deterministic orientation defaults without writing a file.");

        var landscapeDefault = FindLayout(
            missing.Document,
            CompositionOrientationIds.Landscape);
        var portraitDefault = FindLayout(
            missing.Document,
            CompositionOrientationIds.Portrait);
        var portraitCompositionDefault = missing.Document.PortraitComposition;
        Assert(
            landscapeDefault.Enabled &&
            landscapeDefault.Transform is
            {
                Visible: true,
                AnchorX: 0.76,
                AnchorY: 0.96,
                HeightFraction: 0.40,
                MirrorHorizontally: true,
                Outline: SilhouetteOutlineStyle.Soft,
                Preset: SilhouettePlacementPreset.BottomRight
            } &&
            !portraitDefault.Enabled &&
            portraitDefault.Transform is
            {
                Visible: true,
                AnchorX: 0.50,
                AnchorY: 0.745,
                HeightFraction: 0.34,
                MirrorHorizontally: true,
                Outline: SilhouetteOutlineStyle.Soft,
                Preset: SilhouettePlacementPreset.RaisedBottomCenter
            } &&
            portraitCompositionDefault.Layout == PortraitGameplayLayoutMode.Context &&
            portraitCompositionDefault.Gameplay.WidthFraction == 800d / 1080d &&
            portraitCompositionDefault.Gameplay.CenterX == 0.50 &&
            portraitCompositionDefault.Gameplay.TopY == 270d / 1920d &&
            portraitCompositionDefault.FocusCrop.Zoom == 1.00 &&
            portraitCompositionDefault.FocusCrop.FocalCenterX == 0.50,
            "Schema-v2 defaults must match the approved Figma S24 landscape and portrait geometry exactly, with only Landscape enabled.");

        var bothEnabled = CaptureCompositionModel.SetOutputEnabled(
            missing.Document,
            CompositionOrientationIds.Portrait,
            enabled: true);
        var portraitOnly = CaptureCompositionModel.SetOutputEnabled(
            bothEnabled,
            CompositionOrientationIds.Landscape,
            enabled: false);
        var disablingLastOutputRejected = false;
        try
        {
            _ = CaptureCompositionModel.SetOutputEnabled(
                portraitOnly,
                CompositionOrientationIds.Portrait,
                enabled: false);
        }
        catch (InvalidOperationException)
        {
            disablingLastOutputRejected = true;
        }
        var normalizedBothOff = CaptureCompositionModel.Normalize(
            missing.Document with
            {
                SilhouetteLayouts = missing.Document.SilhouetteLayouts
                    .Select(layout => layout with { Enabled = false })
                    .ToArray()
            },
            project.ProjectId,
            mirrorCamera: true);
        Assert(
            !FindLayout(portraitOnly, CompositionOrientationIds.Landscape).Enabled &&
            FindLayout(portraitOnly, CompositionOrientationIds.Portrait).Enabled &&
            disablingLastOutputRejected &&
            FindLayout(normalizedBothOff, CompositionOrientationIds.Landscape).Enabled &&
            !FindLayout(normalizedBothOff, CompositionOrientationIds.Portrait).Enabled,
            "The orientation model must allow either output independently but reject or safely repair a state with both outputs disabled.");

        var hostileLandscape = landscapeDefault.Transform with
        {
            Visible = false,
            AnchorX = double.NaN,
            AnchorY = -5,
            HeightFraction = 4,
            MirrorHorizontally = false,
            Outline = (SilhouetteOutlineStyle)99,
            Preset = (SilhouettePlacementPreset)99
        };
        var customPortrait = portraitDefault.Transform with
        {
            AnchorX = 0.37,
            AnchorY = 0.73,
            HeightFraction = 0.28,
            MirrorHorizontally = false,
            Outline = SilhouetteOutlineStyle.Strong,
            Preset = SilhouettePlacementPreset.Custom
        };
        var hostileDocument = missing.Document with
        {
            SilhouetteLayouts = missing.Document.SilhouetteLayouts
                .Select(layout => layout.ProfileId switch
                {
                    CompositionOrientationIds.Landscape => layout with
                    {
                        Transform = hostileLandscape
                    },
                    CompositionOrientationIds.Portrait => layout with
                    {
                        Enabled = true,
                        Transform = customPortrait
                    },
                    _ => layout
                })
                .ToArray(),
            PortraitComposition = new PortraitCompositionSettings(
                (PortraitGameplayLayoutMode)99,
                new PortraitGameplayTransform(
                    WidthFraction: 4,
                    CenterX: -2,
                    TopY: 3),
                new PortraitFocusCrop(
                    Zoom: 99,
                    FocalCenterX: double.NaN))
        };
        var normalized = CaptureCompositionModel.Normalize(
            hostileDocument,
            project.ProjectId,
            mirrorCamera: true);
        var normalizedLandscape = FindLayout(
            normalized,
            CompositionOrientationIds.Landscape).Transform;
        var normalizedPortrait = FindLayout(
            normalized,
            CompositionOrientationIds.Portrait).Transform;
        Assert(
            !normalizedLandscape.Visible &&
            normalizedLandscape.AnchorX == landscapeDefault.Transform.AnchorX &&
            normalizedLandscape.HeightFraction == CaptureCompositionModel.MaximumHeightFraction &&
            normalizedLandscape.AnchorY == CaptureCompositionModel.MaximumHeightFraction &&
            !normalizedLandscape.MirrorHorizontally &&
            normalizedLandscape.Outline == SilhouetteOutlineStyle.Soft &&
            normalizedLandscape.Preset == SilhouettePlacementPreset.Custom &&
            normalizedPortrait == customPortrait &&
            normalized.PortraitComposition.Layout == PortraitGameplayLayoutMode.Context &&
            normalized.PortraitComposition.Gameplay.WidthFraction ==
                CaptureCompositionModel.MaximumPortraitGameplayWidthFraction &&
            normalized.PortraitComposition.Gameplay.CenterX == 0.50 &&
            normalized.PortraitComposition.Gameplay.TopY >= 0 &&
            normalized.PortraitComposition.Gameplay.TopY < 1 &&
            normalized.PortraitComposition.FocusCrop.Zoom ==
                CaptureCompositionModel.MaximumFocusCropZoom &&
            normalized.PortraitComposition.FocusCrop.FocalCenterX == 0.50,
            "Each orientation and the portrait gameplay mode must normalize independently: hostile landscape state cannot erase valid portrait placement, and invalid portrait canvas values must clamp to safe bounds.");

        var validPortraitComposition = new PortraitCompositionSettings(
            PortraitGameplayLayoutMode.FocusCrop,
            new PortraitGameplayTransform(
                WidthFraction: 0.80,
                CenterX: 0.55,
                TopY: 0.12),
            new PortraitFocusCrop(
                Zoom: 1.75,
                FocalCenterX: 0.61));
        var normalizedIndependent = CaptureCompositionModel.Normalize(
            missing.Document with
            {
                SilhouetteLayouts = missing.Document.SilhouetteLayouts
                    .Select(layout => layout.ProfileId == CompositionOrientationIds.Portrait
                        ? layout with { Transform = customPortrait }
                        : layout)
                    .ToArray(),
                PortraitComposition = validPortraitComposition
            },
            project.ProjectId,
            mirrorCamera: true);
        Assert(
            FindLayout(normalizedIndependent, CompositionOrientationIds.Landscape).Transform ==
                landscapeDefault.Transform &&
            FindLayout(normalizedIndependent, CompositionOrientationIds.Portrait).Transform ==
                customPortrait &&
            normalizedIndependent.PortraitComposition == validPortraitComposition,
            "A valid portrait gameplay mode and silhouette transform must round-trip independently from the landscape layout.");

        var offCanvas = landscapeDefault.Transform with
        {
            AnchorX = 4,
            AnchorY = -2,
            HeightFraction = CaptureCompositionModel.MaximumHeightFraction,
            Preset = SilhouettePlacementPreset.Custom
        };
        var fittedHd = CaptureCompositionModel.FitToCanvas(
            offCanvas,
            layerAspectRatio: 0.75,
            canvasAspectRatio: 1920d / 1080d);
        var fitted4K = CaptureCompositionModel.FitToCanvas(
            offCanvas,
            layerAspectRatio: 0.75,
            canvasAspectRatio: 3840d / 2160d);
        var fittedPortrait = CaptureCompositionModel.FitToCanvas(
            offCanvas,
            layerAspectRatio: 2,
            canvasAspectRatio: 1080d / 1920d);
        Assert(
            fittedHd == fitted4K &&
            fittedHd.Bounds.Left >= 0 && fittedHd.Bounds.Top >= 0 &&
            fittedHd.Bounds.Right <= 1 && fittedHd.Bounds.Bottom <= 1 &&
            fittedPortrait.Bounds.Left == 0 && fittedPortrait.Bounds.Right == 1 &&
            fittedPortrait.Bounds.Top >= 0 && fittedPortrait.Bounds.Bottom <= 1 &&
            Math.Abs(
                fittedPortrait.Bounds.Width / fittedPortrait.Bounds.Height -
                (2 / (1080d / 1920d))) < 0.0000001,
            "Final preview/export geometry must be resolution-independent, preserve the matte aspect ratio, and fit custom landscape or portrait placement wholly inside the canvas.");

        var preferencesMissing = SilhouettePreferencesStore.LoadOrDefault(
            settingsDirectory,
            mirrorCamera: true,
            fallbackTime);
        var preferencesPath = SilhouettePreferencesStore.GetPath(settingsDirectory);
        Assert(
            preferencesMissing.Status == SilhouettePreferencesLoadStatus.Missing &&
            !preferencesMissing.LoadedFromDisk &&
            preferencesMissing.Document.SchemaVersion ==
                SilhouettePreferencesStore.CurrentSchemaVersion &&
            preferencesMissing.Document.ModifiedUtc == fallbackTime &&
            preferencesMissing.Document.Layouts.Select(layout => layout.OrientationId)
                .SequenceEqual(CompositionOrientationIds.All) &&
            FindPreference(
                preferencesMissing.Document,
                CompositionOrientationIds.Landscape).Transform == landscapeDefault.Transform &&
            FindPreference(
                preferencesMissing.Document,
                CompositionOrientationIds.Portrait).Transform == portraitDefault.Transform &&
            preferencesMissing.Document.PortraitComposition == portraitCompositionDefault &&
            !File.Exists(preferencesPath),
            "Missing reusable silhouette preferences must return the approved defaults without creating state on disk.");

        var customLandscape = landscapeDefault.Transform with
        {
            AnchorX = 0.22,
            AnchorY = 0.88,
            HeightFraction = 0.31,
            MirrorHorizontally = false,
            Outline = SilhouetteOutlineStyle.Strong,
            Preset = SilhouettePlacementPreset.Custom
        };
        var editedPreferences = preferencesMissing.Document with
        {
            ModifiedUtc = fallbackTime + TimeSpan.FromMinutes(5),
            Layouts = preferencesMissing.Document.Layouts
                .Select(layout => layout.OrientationId switch
                {
                    CompositionOrientationIds.Landscape => layout with
                    {
                        Transform = customLandscape
                    },
                    CompositionOrientationIds.Portrait => layout with
                    {
                        Transform = customPortrait
                    },
                    _ => layout
                })
                .ToArray(),
            PortraitComposition = validPortraitComposition
        };

        var portraitSnapshot = SilhouettePreferencesModel.CreateProjectSnapshot(
            project.ProjectId,
            editedPreferences,
            landscapeEnabled: false,
            portraitEnabled: true,
            mirrorCamera: true,
            fallbackTime + TimeSpan.FromMinutes(6));
        var repairedSnapshot = SilhouettePreferencesModel.CreateProjectSnapshot(
            project.ProjectId,
            editedPreferences,
            landscapeEnabled: false,
            portraitEnabled: false,
            mirrorCamera: true,
            fallbackTime + TimeSpan.FromMinutes(7));
        Assert(
            !FindLayout(portraitSnapshot, CompositionOrientationIds.Landscape).Enabled &&
            FindLayout(portraitSnapshot, CompositionOrientationIds.Portrait).Enabled &&
            FindLayout(portraitSnapshot, CompositionOrientationIds.Landscape).Transform ==
                customLandscape &&
            FindLayout(portraitSnapshot, CompositionOrientationIds.Portrait).Transform ==
                customPortrait &&
            portraitSnapshot.PortraitComposition == validPortraitComposition &&
            FindLayout(repairedSnapshot, CompositionOrientationIds.Landscape).Enabled &&
            !FindLayout(repairedSnapshot, CompositionOrientationIds.Portrait).Enabled &&
            FindPreference(editedPreferences, CompositionOrientationIds.Landscape).Transform ==
                customLandscape &&
            FindPreference(editedPreferences, CompositionOrientationIds.Portrait).Transform ==
                customPortrait,
            "A project snapshot must copy both reusable layouts independently, preserve the requested orientation booleans, and repair only the impossible both-off state without mutating preferences.");

        var savedPreferences = SilhouettePreferencesStore.SaveAsync(
                settingsDirectory,
                editedPreferences,
                mirrorCamera: true)
            .GetAwaiter()
            .GetResult();
        var loadedPreferences = SilhouettePreferencesStore.LoadOrDefault(
            settingsDirectory,
            mirrorCamera: true,
            fallbackTime);
        var persistedPreferencesJson = File.ReadAllText(preferencesPath);
        Assert(
            PreferencesEqual(savedPreferences, loadedPreferences.Document) &&
            loadedPreferences.Status == SilhouettePreferencesLoadStatus.Loaded &&
            loadedPreferences.LoadedFromDisk &&
            !persistedPreferencesJson.Contains(
                Path.GetFullPath(settingsDirectory),
                StringComparison.OrdinalIgnoreCase) &&
            persistedPreferencesJson.Contains("\"focusCrop\"", StringComparison.Ordinal) &&
            persistedPreferencesJson.Contains("\"strong\"", StringComparison.Ordinal),
            "Reusable preferences must atomically round-trip both independent layouts and portrait mode without storing local paths.");

        var edited = portraitSnapshot;
        var saved = CaptureCompositionStore.SaveAsync(root, project.ProjectId, edited)
            .GetAwaiter()
            .GetResult();
        var compositionPath = CaptureCompositionStore.GetPath(root, project.ProjectId);
        var persistedJson = File.ReadAllText(compositionPath);
        var loaded = CaptureCompositionStore.LoadOrDefault(root, project.ProjectId, fallbackTime);
        Assert(
            DocumentsEqual(saved, loaded.Document) &&
            loaded.Status == CaptureCompositionLoadStatus.Loaded &&
            loaded.LoadedFromDisk &&
            !FindLayout(loaded.Document, CompositionOrientationIds.Landscape).Enabled &&
            FindLayout(loaded.Document, CompositionOrientationIds.Portrait).Enabled &&
            FindLayout(loaded.Document, CompositionOrientationIds.Landscape).Transform ==
                customLandscape &&
            FindLayout(loaded.Document, CompositionOrientationIds.Portrait).Transform ==
                customPortrait &&
            loaded.Document.PortraitComposition == validPortraitComposition &&
            !persistedJson.Contains(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
            persistedJson.Contains("\"strong\"", StringComparison.Ordinal) &&
            persistedJson.Contains("\"focusCrop\"", StringComparison.Ordinal),
            "A project composition must round-trip both output booleans, layouts, and portrait mode independently without media paths or absolute-library disclosure.");

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        };

        const string legacyDiscord = "discord.landscape.16x9";
        const string legacyYouTube = "youtube.landscape.16x9";
        const string legacyShorts = "youtube-shorts.portrait.9x16";
        const string legacyTikTok = "tiktok.portrait.9x16";
        var legacyLandscapeDefault = landscapeDefault.Transform;
        var legacyShortsDefault = portraitDefault.Transform with { AnchorY = 0.82 };
        var legacyTikTokDefault = portraitDefault.Transform with { AnchorY = 0.80 };

        string SerializeLegacy(
            DateTimeOffset modifiedUtc,
            params (string ProfileId, SilhouetteTransform Transform)[] layouts) =>
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = CaptureCompositionStore.LegacyDestinationSchemaVersion,
                    projectId = project.ProjectId,
                    silhouetteLayouts = layouts.Select(layout => new
                    {
                        profileId = layout.ProfileId,
                        transform = layout.Transform
                    }).ToArray(),
                    modifiedUtc
                },
                jsonOptions);

        var legacyDefaultsJson = SerializeLegacy(
            fallbackTime + TimeSpan.FromMinutes(10),
            (legacyDiscord, legacyLandscapeDefault),
            (legacyYouTube, legacyLandscapeDefault),
            (legacyShorts, legacyShortsDefault),
            (legacyTikTok, legacyTikTokDefault));
        File.WriteAllText(compositionPath, legacyDefaultsJson);
        var migratedDefaults = CaptureCompositionStore.LoadOrDefault(
            root,
            project.ProjectId,
            fallbackTime);
        Assert(
            migratedDefaults.Status == CaptureCompositionLoadStatus.Migrated &&
            migratedDefaults.LoadedFromDisk &&
            migratedDefaults.Document.SchemaVersion == CaptureCompositionStore.CurrentSchemaVersion &&
            FindLayout(migratedDefaults.Document, CompositionOrientationIds.Landscape).Transform ==
                landscapeDefault.Transform &&
            FindLayout(migratedDefaults.Document, CompositionOrientationIds.Portrait).Transform ==
                portraitDefault.Transform &&
            FindLayout(migratedDefaults.Document, CompositionOrientationIds.Landscape).Enabled &&
            !FindLayout(migratedDefaults.Document, CompositionOrientationIds.Portrait).Enabled &&
            migratedDefaults.Document.PortraitComposition == portraitCompositionDefault &&
            File.ReadAllText(compositionPath) == legacyDefaultsJson,
            "Loading an untouched schema-v1 four-destination document must migrate in memory to the approved orientation defaults without rewriting the legacy file.");

        var youtubeOnlyEdit = legacyLandscapeDefault with
        {
            AnchorX = 0.18,
            Preset = SilhouettePlacementPreset.Custom
        };
        var shortsOnlyEdit = legacyShortsDefault with
        {
            AnchorY = 0.69,
            HeightFraction = 0.29,
            Preset = SilhouettePlacementPreset.Custom
        };
        var legacyAlternateEditJson = SerializeLegacy(
            fallbackTime + TimeSpan.FromMinutes(11),
            (legacyDiscord, legacyLandscapeDefault),
            (legacyYouTube, youtubeOnlyEdit),
            (legacyShorts, shortsOnlyEdit),
            (legacyTikTok, legacyTikTokDefault));
        File.WriteAllText(compositionPath, legacyAlternateEditJson);
        var migratedAlternateEdits = CaptureCompositionStore.LoadOrDefault(
            root,
            project.ProjectId,
            fallbackTime);
        Assert(
            migratedAlternateEdits.Status == CaptureCompositionLoadStatus.Migrated &&
            FindLayout(
                migratedAlternateEdits.Document,
                CompositionOrientationIds.Landscape).Transform == youtubeOnlyEdit &&
            FindLayout(
                migratedAlternateEdits.Document,
                CompositionOrientationIds.Portrait).Transform == shortsOnlyEdit &&
            File.ReadAllText(compositionPath) == legacyAlternateEditJson,
            "When only the alternate legacy destination was edited, its user placement must win orientation migration without an implicit write.");

        var discordConflictEdit = legacyLandscapeDefault with
        {
            AnchorX = 0.27,
            Preset = SilhouettePlacementPreset.Custom
        };
        var youtubeConflictEdit = legacyLandscapeDefault with
        {
            AnchorX = 0.71,
            Preset = SilhouettePlacementPreset.Custom
        };
        var shortsConflictEdit = legacyShortsDefault with
        {
            AnchorX = 0.30,
            Preset = SilhouettePlacementPreset.Custom
        };
        var tikTokConflictEdit = legacyTikTokDefault with
        {
            AnchorX = 0.66,
            Preset = SilhouettePlacementPreset.Custom
        };
        var legacyConflictJson = SerializeLegacy(
            fallbackTime + TimeSpan.FromMinutes(12),
            (legacyDiscord, discordConflictEdit),
            (legacyYouTube, youtubeConflictEdit),
            (legacyShorts, shortsConflictEdit),
            (legacyTikTok, tikTokConflictEdit));
        File.WriteAllText(compositionPath, legacyConflictJson);
        var migratedConflicts = CaptureCompositionStore.LoadOrDefault(
            root,
            project.ProjectId,
            fallbackTime);
        Assert(
            migratedConflicts.Status == CaptureCompositionLoadStatus.Migrated &&
            FindLayout(
                migratedConflicts.Document,
                CompositionOrientationIds.Landscape).Transform == discordConflictEdit &&
            FindLayout(
                migratedConflicts.Document,
                CompositionOrientationIds.Portrait).Transform == tikTokConflictEdit &&
            File.ReadAllText(compositionPath) == legacyConflictJson,
            "Conflicting schema-v1 edits must resolve deterministically to Discord for Landscape and TikTok for Portrait, still without rewriting on load.");

        var upgraded = CaptureCompositionStore.SaveAsync(
                root,
                project.ProjectId,
                migratedConflicts.Document)
            .GetAwaiter()
            .GetResult();
        var upgradedJson = File.ReadAllText(compositionPath);
        var upgradedLoad = CaptureCompositionStore.LoadOrDefault(
            root,
            project.ProjectId,
            fallbackTime);
        Assert(
            upgraded.SchemaVersion == CaptureCompositionStore.CurrentSchemaVersion &&
            upgradedLoad.Status == CaptureCompositionLoadStatus.Loaded &&
            DocumentsEqual(upgraded, upgradedLoad.Document) &&
            upgradedJson.Contains("\"schemaVersion\": 2", StringComparison.Ordinal) &&
            upgradedJson.Contains(CompositionOrientationIds.Landscape, StringComparison.Ordinal) &&
            upgradedJson.Contains(CompositionOrientationIds.Portrait, StringComparison.Ordinal) &&
            !upgradedJson.Contains(legacyDiscord, StringComparison.Ordinal) &&
            !upgradedJson.Contains(legacyTikTok, StringComparison.Ordinal),
            "Only an explicit save may atomically upgrade a validated schema-v1 document to the two schema-v2 orientations.");

        const string corruptJson = "{ this is not composition json";
        File.WriteAllText(compositionPath, corruptJson);
        var corrupt = CaptureCompositionStore.LoadOrDefault(root, project.ProjectId, fallbackTime);
        Assert(
            corrupt.Status == CaptureCompositionLoadStatus.Corrupt &&
            DocumentsEqual(corrupt.Document, missing.Document) &&
            File.ReadAllText(compositionPath) == corruptJson,
            "Corrupt composition state must fall back safely without overwriting the user's evidence.");

        var unsupportedDocument = edited with { SchemaVersion = 99 };
        var unsupportedJson = JsonSerializer.Serialize(unsupportedDocument, jsonOptions);
        File.WriteAllText(compositionPath, unsupportedJson);
        var unsupported = CaptureCompositionStore.LoadOrDefault(root, project.ProjectId, fallbackTime);
        Assert(
            unsupported.Status == CaptureCompositionLoadStatus.UnsupportedSchema &&
            DocumentsEqual(unsupported.Document, missing.Document) &&
            File.ReadAllText(compositionPath) == unsupportedJson,
            "A newer composition schema must fall back without being parsed as current data or rewritten by an older build.");
        var newerSchemaOverwriteRejected = false;
        try
        {
            _ = CaptureCompositionStore.SaveAsync(root, project.ProjectId, edited)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidDataException)
        {
            newerSchemaOverwriteRejected = true;
        }
        Assert(
            newerSchemaOverwriteRejected &&
            File.ReadAllText(compositionPath) == unsupportedJson,
            "An older ClipCord build must refuse to overwrite composition state written by a newer schema, even after showing fallback defaults.");

        var oversizedBytes = Enumerable.Repeat(
                (byte)'x',
                CaptureCompositionStore.MaximumDocumentBytes + 1)
            .ToArray();
        File.WriteAllBytes(compositionPath, oversizedBytes);
        var oversized = CaptureCompositionStore.LoadOrDefault(root, project.ProjectId, fallbackTime);
        Assert(
            oversized.Status == CaptureCompositionLoadStatus.Invalid &&
            File.ReadAllBytes(compositionPath).SequenceEqual(oversizedBytes),
            "Composition loading must enforce its byte ceiling without adopting or rewriting an oversized file.");

        var invalidUtf8 = new byte[]
        {
            (byte)'{', (byte)'"', (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'a',
            (byte)'V', (byte)'e', (byte)'r', (byte)'s', (byte)'i', (byte)'o', (byte)'n', (byte)'"',
            (byte)':', (byte)'1', (byte)',', (byte)'"', (byte)'x', (byte)'"', (byte)':', (byte)'"',
            0xC3, 0x28, (byte)'"', (byte)'}'
        };
        File.WriteAllBytes(compositionPath, invalidUtf8);
        var malformedEncoding = CaptureCompositionStore.LoadOrDefault(
            root,
            project.ProjectId,
            fallbackTime);
        Assert(
            malformedEncoding.Status == CaptureCompositionLoadStatus.Invalid &&
            File.ReadAllBytes(compositionPath).SequenceEqual(invalidUtf8),
            "Composition loading must decode bounded JSON as strict UTF-8 and preserve malformed input for recovery.");

        var mismatchedDocument = edited with { ProjectId = new string('a', 32) };
        var mismatchedJson = JsonSerializer.Serialize(mismatchedDocument, jsonOptions);
        File.WriteAllText(compositionPath, mismatchedJson);
        var mismatched = CaptureCompositionStore.LoadOrDefault(root, project.ProjectId, fallbackTime);
        Assert(
            mismatched.Status == CaptureCompositionLoadStatus.Invalid &&
            mismatched.Document.ProjectId == project.ProjectId &&
            File.ReadAllText(compositionPath) == mismatchedJson,
            "Composition state from another project must never be adopted or silently rewritten.");

        var duplicatedDocument = edited with
        {
            SilhouetteLayouts = edited.SilhouetteLayouts
                .Append(FindLayout(edited, CompositionOrientationIds.Portrait))
                .ToArray()
        };
        var duplicatedJson = JsonSerializer.Serialize(duplicatedDocument, jsonOptions);
        File.WriteAllText(compositionPath, duplicatedJson);
        var duplicated = CaptureCompositionStore.LoadOrDefault(root, project.ProjectId, fallbackTime);
        Assert(
            duplicated.Status == CaptureCompositionLoadStatus.Invalid &&
            DocumentsEqual(duplicated.Document, missing.Document) &&
            File.ReadAllText(compositionPath) == duplicatedJson,
            "Duplicate destination layouts must fail closed instead of introducing order-dependent placement.");

        const string corruptPreferencesJson = "{ not valid silhouette preferences";
        File.WriteAllText(preferencesPath, corruptPreferencesJson);
        var corruptPreferences = SilhouettePreferencesStore.LoadOrDefault(
            settingsDirectory,
            mirrorCamera: true,
            fallbackTime);
        Assert(
            corruptPreferences.Status == SilhouettePreferencesLoadStatus.Corrupt &&
            PreferencesEqual(corruptPreferences.Document, preferencesMissing.Document) &&
            File.ReadAllText(preferencesPath) == corruptPreferencesJson,
            "Corrupt reusable preferences must fall back without overwriting recovery evidence.");

        var newerPreferencesDocument = editedPreferences with { SchemaVersion = 99 };
        var newerPreferencesJson = JsonSerializer.Serialize(
            newerPreferencesDocument,
            jsonOptions);
        File.WriteAllText(preferencesPath, newerPreferencesJson);
        var newerPreferences = SilhouettePreferencesStore.LoadOrDefault(
            settingsDirectory,
            mirrorCamera: true,
            fallbackTime);
        var newerPreferencesOverwriteRejected = false;
        try
        {
            _ = SilhouettePreferencesStore.SaveAsync(
                    settingsDirectory,
                    editedPreferences,
                    mirrorCamera: true)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidDataException)
        {
            newerPreferencesOverwriteRejected = true;
        }
        Assert(
            newerPreferences.Status == SilhouettePreferencesLoadStatus.UnsupportedSchema &&
            PreferencesEqual(newerPreferences.Document, preferencesMissing.Document) &&
            newerPreferencesOverwriteRejected &&
            File.ReadAllText(preferencesPath) == newerPreferencesJson,
            "A newer reusable-preference schema must neither be adopted nor overwritten by an older ClipCord build.");

        var oversizedPreferencesBytes = Enumerable.Repeat(
                (byte)'p',
                SilhouettePreferencesStore.MaximumDocumentBytes + 1)
            .ToArray();
        File.WriteAllBytes(preferencesPath, oversizedPreferencesBytes);
        var oversizedPreferences = SilhouettePreferencesStore.LoadOrDefault(
            settingsDirectory,
            mirrorCamera: true,
            fallbackTime);
        Assert(
            oversizedPreferences.Status == SilhouettePreferencesLoadStatus.Invalid &&
            File.ReadAllBytes(preferencesPath).SequenceEqual(oversizedPreferencesBytes),
            "Reusable-preference loading must enforce its byte ceiling without adopting or rewriting oversized input.");

        File.Delete(preferencesPath);
        _ = SilhouettePreferencesStore.SaveAsync(
                settingsDirectory,
                editedPreferences,
                mirrorCamera: true)
            .GetAwaiter()
            .GetResult();
        var committedPreferencesBytes = File.ReadAllBytes(preferencesPath);
        var replacementPreferences = editedPreferences with
        {
            Layouts = editedPreferences.Layouts
                .Select(layout => layout.OrientationId == CompositionOrientationIds.Portrait
                    ? layout with
                    {
                        Transform = layout.Transform with { AnchorX = 0.81 }
                    }
                    : layout)
                .ToArray()
        };
        var preferencesReplacementFailed = false;
        using (var destinationLock = new FileStream(
                   preferencesPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            try
            {
                _ = SilhouettePreferencesStore.SaveAsync(
                        settingsDirectory,
                        replacementPreferences,
                        mirrorCamera: true)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                preferencesReplacementFailed = true;
            }
        }
        Assert(
            preferencesReplacementFailed &&
            File.ReadAllBytes(preferencesPath).SequenceEqual(committedPreferencesBytes) &&
            !Directory.EnumerateFiles(
                    settingsDirectory,
                    $".{SilhouettePreferencesStore.FileName}.*.tmp",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "A failed atomic reusable-preference replacement must preserve the committed document and remove only its owned temporary file.");

        var linkedSettingsDirectory = Path.Combine(
            testRoot,
            "silhouette-preferences-linked");
        var linkedSettingsTarget = Directory.CreateDirectory(Path.Combine(
            testRoot,
            "silhouette-preferences-link-target")).FullName;
        var linkedTargetPath = Path.Combine(
            linkedSettingsTarget,
            SilhouettePreferencesStore.FileName);
        File.WriteAllBytes(linkedTargetPath, committedPreferencesBytes);
        try
        {
            Directory.CreateSymbolicLink(linkedSettingsDirectory, linkedSettingsTarget);
            var linkedLoad = SilhouettePreferencesStore.LoadOrDefault(
                linkedSettingsDirectory,
                mirrorCamera: true,
                fallbackTime);
            var linkedSaveRejected = false;
            try
            {
                _ = SilhouettePreferencesStore.SaveAsync(
                        linkedSettingsDirectory,
                        editedPreferences,
                        mirrorCamera: true)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (InvalidDataException)
            {
                linkedSaveRejected = true;
            }
            Assert(
                linkedLoad.Status == SilhouettePreferencesLoadStatus.Invalid &&
                linkedSaveRejected &&
                File.ReadAllBytes(linkedTargetPath).SequenceEqual(committedPreferencesBytes),
                "Reusable preferences must reject reads and writes through a symbolic-link or junction settings directory without touching its target.");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or NotSupportedException or
                System.Security.SecurityException)
        {
            Console.WriteLine(
                "  (skipped the silhouette-preference symlink check: this Windows environment cannot create a test link)");
        }

        File.Delete(compositionPath);
        _ = CaptureCompositionStore.SaveAsync(root, project.ProjectId, edited)
            .GetAwaiter()
            .GetResult();
        var committedBytes = File.ReadAllBytes(compositionPath);
        var changedLandscape = customLandscape with
        {
            AnchorX = 0.44,
            Preset = SilhouettePlacementPreset.Custom
        };
        var replacement = edited with
        {
            SilhouetteLayouts = edited.SilhouetteLayouts
                .Select(layout => layout.ProfileId == CompositionOrientationIds.Landscape
                    ? layout with { Transform = changedLandscape }
                    : layout)
                .ToArray()
        };
        var replacementFailed = false;
        using (var destinationLock = new FileStream(
                   compositionPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            try
            {
                _ = CaptureCompositionStore.SaveAsync(root, project.ProjectId, replacement)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                replacementFailed = true;
            }
        }
        Assert(
            replacementFailed &&
            File.ReadAllBytes(compositionPath).SequenceEqual(committedBytes) &&
            !Directory.EnumerateFiles(
                    project.ProjectDirectory,
                    $".{CaptureCompositionStore.FileName}.*.tmp",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "A failed atomic composition replacement must preserve the previous document and remove only its owned temporary file.");

        var blockedSaveCancelled = false;
        using (var saveMutex = new Mutex(
                   initiallyOwned: false,
                   CaptureCompositionStore.GetSaveMutexName(project.ProjectId)))
        {
            var lockTaken = false;
            try
            {
                lockTaken = saveMutex.WaitOne(TimeSpan.FromSeconds(2));
                Assert(lockTaken, "The composition concurrency test must acquire the project save lock.");
                using var cancellation = new CancellationTokenSource();
                Exception? saveException = null;
                var saveThread = new Thread(() =>
                {
                    try
                    {
                        _ = CaptureCompositionStore.SaveAsync(
                                root,
                                project.ProjectId,
                                replacement,
                                cancellation.Token)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception exception)
                    {
                        saveException = exception;
                    }
                })
                {
                    IsBackground = true,
                    Name = "ClipCord concurrent composition save test"
                };
                saveThread.Start();
                if (!saveThread.Join(TimeSpan.FromMilliseconds(250))) cancellation.Cancel();
                Assert(
                    saveThread.Join(TimeSpan.FromSeconds(2)),
                    "A composition save waiting for another writer must stop promptly after cancellation.");
                blockedSaveCancelled = saveException is OperationCanceledException;
            }
            finally
            {
                if (lockTaken) saveMutex.ReleaseMutex();
            }
        }
        Assert(
            blockedSaveCancelled,
            "Concurrent composition saves must serialize per project and remain cancellable while waiting.");
        Assert(
            File.ReadAllBytes(compositionPath).SequenceEqual(committedBytes),
            "Cancelling a composition save while it waits for another writer must leave committed state untouched.");
        Assert(
            !Directory.EnumerateFiles(
                project.ProjectDirectory,
                $".{CaptureCompositionStore.FileName}.*.tmp",
                SearchOption.TopDirectoryOnly).Any(),
            "Cancelling a composition save while it waits for another writer must not leave a temporary file.");

        static SilhouetteLayoutVariant FindLayout(
            CaptureCompositionDocument document,
            string profileId) =>
            document.SilhouetteLayouts.Single(layout =>
                layout.ProfileId.Equals(profileId, StringComparison.Ordinal));

        static SilhouetteLayoutPreference FindPreference(
            SilhouettePreferencesDocument document,
            string orientationId) =>
            document.Layouts.Single(layout =>
                layout.OrientationId.Equals(orientationId, StringComparison.Ordinal));

        static bool DocumentsEqual(
            CaptureCompositionDocument left,
            CaptureCompositionDocument right) =>
            left.SchemaVersion == right.SchemaVersion &&
            left.ProjectId == right.ProjectId &&
            left.ModifiedUtc == right.ModifiedUtc &&
            left.SilhouetteLayouts.Count == right.SilhouetteLayouts.Count &&
            left.SilhouetteLayouts.Zip(
                    right.SilhouetteLayouts,
                    (leftLayout, rightLayout) =>
                        leftLayout.ProfileId == rightLayout.ProfileId &&
                        leftLayout.Enabled == rightLayout.Enabled &&
                        leftLayout.Transform == rightLayout.Transform)
                .All(equal => equal) &&
            left.PortraitComposition == right.PortraitComposition;

        static bool PreferencesEqual(
            SilhouettePreferencesDocument left,
            SilhouettePreferencesDocument right) =>
            left.SchemaVersion == right.SchemaVersion &&
            left.ModifiedUtc == right.ModifiedUtc &&
            left.Layouts.Count == right.Layouts.Count &&
            left.Layouts.Zip(
                    right.Layouts,
                    (leftLayout, rightLayout) =>
                        leftLayout.OrientationId == rightLayout.OrientationId &&
                        leftLayout.Transform == rightLayout.Transform)
                .All(equal => equal) &&
            left.PortraitComposition == right.PortraitComposition;
    }

    private static void AssertCaptureSettings()
    {
        var defaults = CaptureSettings.Default;
        Assert(
            !defaults.InstantReplayEnabled &&
            defaults.Resolution == CaptureResolution.FullHd1080p &&
            defaults.FramesPerSecond == 60 &&
            defaults.ReplaySeconds == 60 &&
            defaults.RecordGameAudio &&
            !defaults.IncludeMicrophone &&
            !defaults.IncludeVoiceChat &&
            defaults.HasAudio,
            "ClipCord Capture must remain opt-in with the approved 1080p60/60-second defaults.");
        Assert(
            !string.Equals(defaults.SaveHotkey, GlobalHotkeyBinding.DefaultDisplayText, StringComparison.OrdinalIgnoreCase),
            "ClipCord Capture must use a shortcut distinct from the existing upload-mode shortcut.");

        var external = AppSettings.Empty with { ModeToggleHotkey = defaults.SaveHotkey };
        Assert(defaults.HasHotkeyConflict(external),
            "Capture must detect a collision with ClipCord's existing global shortcut.");
        Assert(
            CapturePathPolicy.PathsOverlap(@"C:\Clips", @"C:\Clips\Library") &&
            CapturePathPolicy.PathsOverlap(@"C:\Clips\Library", @"C:\Clips") &&
            !CapturePathPolicy.PathsOverlap(@"C:\External", @"C:\ClipCord"),
            "Built-in and external capture roots must reject either direction of path overlap.");
        Assert(
            !SettingsForm.CanUseWatchedFolder(@"C:\Clips", @"C:\Clips\Library") &&
            !SettingsForm.CanUseWatchedFolder(@"C:\Clips\Library", @"C:\Clips") &&
            SettingsForm.CanUseWatchedFolder(@"C:\External", @"C:\ClipCord"),
            "Settings must reject watched-folder overlap in either direction, not merely warn after saving.");

        var normalized = CaptureSettings.Normalize(defaults with
        {
            Resolution = (CaptureResolution)999,
            FramesPerSecond = 144,
            ReplaySeconds = 1,
            SaveHotkey = "unsafe",
            GameAudioDevice = " ",
            VoiceChatDevice = " "
        });
        Assert(
            normalized.Resolution == defaults.Resolution &&
            normalized.FramesPerSecond == defaults.FramesPerSecond &&
            normalized.ReplaySeconds == defaults.ReplaySeconds &&
            normalized.SaveHotkey == CaptureSettings.DefaultSaveHotkey &&
            normalized.GameAudioDevice == CaptureSettings.DefaultOutputDevice &&
            normalized.VoiceChatDevice == CaptureSettings.DefaultVoiceChatDevice,
            "Capture settings migration must normalize corrupt profile, shortcut, and device values.");

        var transientPreferences = SilhouettePreferencesModel.CreateDefault(
            mirrorCamera: true,
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        var transportSettings = CaptureSettings.Normalize(defaults with
        {
            SilhouettePreferencesSnapshot = transientPreferences
        });
        var persistentSettings = CaptureSettingsStore.PrepareForPersistence(transportSettings);
        Assert(
            transportSettings.SilhouettePreferencesSnapshot is not null &&
            persistentSettings.SilhouettePreferencesSnapshot is null,
            "Capture commands may carry one immutable silhouette snapshot, but capture-settings persistence must drop it so a stale command cannot override newer reusable defaults.");
    }

    private static void AssertReactionCameraSettingsAndMemory()
    {
        var requestedWithoutConsent = CaptureSettings.Default with
        {
            IncludeReactionCamera = true,
            CameraDevice = "  Logitech StreamCam  ",
            CameraDeviceId = "  camera-device-id-42  ",
            ReactionCameraConsentGranted = false
        };
        var denied = CaptureSettings.Normalize(requestedWithoutConsent);
        Assert(
            !denied.IncludeReactionCamera &&
            !denied.Profile.IncludeReactionCamera &&
            !denied.ReactionCameraConsentGranted &&
            denied.CameraDevice == "Logitech StreamCam" &&
            denied.CameraDeviceId == "camera-device-id-42",
            "Reaction Camera must remain off until explicit consent while preserving the selected stable device id.");

        var enabled = CaptureSettings.Normalize(requestedWithoutConsent with
        {
            ReactionCameraConsentGranted = true
        });
        var roundTrip = CaptureSettings.Normalize(
            JsonSerializer.Deserialize<CaptureSettings>(JsonSerializer.Serialize(enabled)));
        Assert(
            enabled.IncludeReactionCamera && enabled.Profile.IncludeReactionCamera &&
            roundTrip.IncludeReactionCamera && roundTrip.ReactionCameraConsentGranted &&
            roundTrip.CameraDevice == "Logitech StreamCam" &&
            roundTrip.CameraDeviceId == "camera-device-id-42",
            "A consented Reaction Camera selection must survive the capture-settings JSON round trip by stable device id.");

        var disabled = CaptureSettings.Normalize(enabled with { IncludeReactionCamera = false });
        var disabledEstimate = CaptureProfileCatalog.Estimate(disabled.Profile, disabled.HasAudio);
        var enabledEstimate = CaptureProfileCatalog.Estimate(enabled.Profile, enabled.HasAudio);
        const long expectedSixtySecondCameraBytes = 45_000_000;
        Assert(
            disabledEstimate.ReactionCameraBitrateKbps == 0 &&
            enabledEstimate.ReactionCameraBitrateKbps == 6_000 &&
            enabledEstimate.ExpectedBytes - disabledEstimate.ExpectedBytes == expectedSixtySecondCameraBytes,
            "A consented 60-second Reaction Camera layer must add exactly one 6 Mbps stream, never affect camera-off estimates.");
        Assert(
            ReplayMemoryPolicy.GetEstimatedResidentBytes(enabled) -
                ReplayMemoryPolicy.GetEstimatedResidentBytes(disabled) == expectedSixtySecondCameraBytes &&
            ReplayMemoryPolicy.GetGameplayMaximumBytes(enabled) ==
                ReplayMemoryPolicy.GetGameplayMaximumBytes(disabled) &&
            ReplayMemoryPolicy.GetReactionCameraMaximumBytes(disabled) == 0 &&
            ReplayMemoryPolicy.GetReactionCameraMaximumBytes(enabled) == 52_080_001,
            "Replay memory must budget gameplay and the bounded 6 Mbps Reaction Camera ring independently.");
    }

    private static void AssertReactionCameraTrayIcons()
    {
        using var starting = ReactionCameraTrayIconFactory.Create(ReactionCameraRuntimeState.Starting);
        using var active = ReactionCameraTrayIconFactory.Create(ReactionCameraRuntimeState.Active);
        using var attention = ReactionCameraTrayIconFactory.Create(
            ReactionCameraRuntimeState.ReleaseNeedsAttention);
        using var startingBitmap = starting.ToBitmap();
        using var activeBitmap = active.ToBitmap();
        using var attentionBitmap = attention.ToBitmap();
        var differentPixels = 0;
        var attentionDifferentPixels = 0;
        var visibleActivePixels = 0;
        for (var y = 0; y < activeBitmap.Height; y++)
        {
            for (var x = 0; x < activeBitmap.Width; x++)
            {
                if (startingBitmap.GetPixel(x, y).ToArgb() != activeBitmap.GetPixel(x, y).ToArgb())
                {
                    differentPixels++;
                }
                if (attentionBitmap.GetPixel(x, y).ToArgb() != activeBitmap.GetPixel(x, y).ToArgb())
                {
                    attentionDifferentPixels++;
                }
                if (activeBitmap.GetPixel(x, y).A > 0) visibleActivePixels++;
            }
        }

        var offRejected = false;
        try
        {
            using var unused = ReactionCameraTrayIconFactory.Create(ReactionCameraRuntimeState.Off);
        }
        catch (ArgumentOutOfRangeException)
        {
            offRejected = true;
        }

        Assert(
            startingBitmap.Size == new Size(32, 32) &&
            activeBitmap.Size == new Size(32, 32) &&
            attentionBitmap.Size == new Size(32, 32) &&
            visibleActivePixels > 300 &&
            differentPixels > 300 &&
            attentionDifferentPixels > 300 &&
            ReactionCameraTrayIconFactory.SelectState(new ReactionCameraRuntimeStatus(true, false)) ==
                ReactionCameraRuntimeState.Active &&
            ReactionCameraTrayIconFactory.SelectState(new ReactionCameraRuntimeStatus(
                false,
                false,
                ManualCaptureStarting: true)) == ReactionCameraRuntimeState.Starting &&
            ReactionCameraTrayIconFactory.SelectState(new ReactionCameraRuntimeStatus(
                false,
                false,
                "camera unavailable")) == ReactionCameraRuntimeState.Off &&
            ReactionCameraTrayIconFactory.SelectState(new ReactionCameraRuntimeStatus(
                false,
                false,
                "camera is still releasing",
                ReleaseNeedsAttention: true)) == ReactionCameraRuntimeState.ReleaseNeedsAttention &&
            offRejected,
            "Reaction Camera must use distinct persistent Starting, Active, and release-attention tray icons built from approved Figma assets, while ordinary failures restore the app icon.");
    }

    private static void AssertAudioSessionTrackPlans(string testRoot)
    {
        var voiceOnly = CaptureSettings.Default with
        {
            RecordGameAudio = false,
            IncludeMicrophone = false,
            IncludeVoiceChat = true,
            VoiceChatDevice = "Synthetic voice-chat endpoint"
        };
        var manualVoice = CaptureAudioSession.CreateTrackPlan(
            voiceOnly,
            testRoot,
            "audio-plan");
        Assert(
            manualVoice is
            [
                {
                    Kind: CaptureAudioSourceKind.VoiceChat,
                    SelectedDevice: "Synthetic voice-chat endpoint",
                    Flow: DataFlow.Render,
                    DefaultRole: Role.Communications
                }
            ] &&
            manualVoice[0].Path == Path.Combine(
                testRoot,
                "manual-capture-audio-plan.chat.wav"),
            "Manual audio capture must plan an enabled voice-chat render/communications source and its private staging file.");

        var replayVoice = ReplayAudioSession.CreateTrackPlan(voiceOnly);
        Assert(
            replayVoice is
            [
                {
                    Kind: CaptureAudioSourceKind.VoiceChat,
                    SelectedDevice: "Synthetic voice-chat endpoint",
                    Flow: DataFlow.Render,
                    DefaultRole: Role.Communications
                }
            ],
            "Instant Replay audio must plan an enabled voice-chat render/communications source.");

        var voiceDisabled = voiceOnly with { IncludeVoiceChat = false };
        Assert(
            CaptureAudioSession.CreateTrackPlan(voiceDisabled, testRoot, "audio-plan").Count == 0,
            "Manual audio capture must omit voice chat when its independent input is disabled.");
        Assert(
            ReplayAudioSession.CreateTrackPlan(voiceDisabled).Count == 0,
            "Instant Replay audio must omit voice chat when its independent input is disabled.");
    }

    private static void AssertManualCaptureStatePolicy()
    {
        Assert(
            ManualCaptureStatePolicy.CanStart(ManualCaptureState.Ready) &&
            !ManualCaptureStatePolicy.CanStart(ManualCaptureState.Starting) &&
            ManualCaptureStatePolicy.CanStop(ManualCaptureState.Recording) &&
            !ManualCaptureStatePolicy.CanStop(ManualCaptureState.Finalizing),
            "Manual capture must only start from Ready and only stop from Recording.");
        Assert(
            ManualCaptureStatePolicy.IsPipelineBusy(ManualCaptureState.Starting) &&
            ManualCaptureStatePolicy.IsPipelineBusy(ManualCaptureState.Recording) &&
            ManualCaptureStatePolicy.IsPipelineBusy(ManualCaptureState.Finalizing) &&
            !ManualCaptureStatePolicy.IsPipelineBusy(ManualCaptureState.Ready) &&
            !ManualCaptureStatePolicy.IsPipelineBusy(ManualCaptureState.Failed),
            "Permission, encoding, and finalization must all lock the capture pipeline.");
        Assert(
            ManualCaptureStatePolicy.CanCommitStart(
                ManualCaptureState.Starting,
                expectedTargetIsAttached: true,
                hasTarget: true,
                cancellationRequested: false) &&
            !ManualCaptureStatePolicy.CanCommitStart(
                ManualCaptureState.Starting,
                expectedTargetIsAttached: false,
                hasTarget: false,
                cancellationRequested: false) &&
            !ManualCaptureStatePolicy.CanCommitStart(
                ManualCaptureState.Starting,
                expectedTargetIsAttached: true,
                hasTarget: true,
                cancellationRequested: true) &&
            !ManualCaptureStatePolicy.CanCommitStart(
                ManualCaptureState.Recording,
                expectedTargetIsAttached: true,
                hasTarget: true,
                cancellationRequested: false),
            "Manual startup may enter Recording only while its exact target is still attached and the pending start has not been cancelled.");
    }

    private static void AssertGameWindowDiscovery()
    {
        var now = new DateTimeOffset(2026, 8, 23, 16, 0, 0, TimeSpan.Zero);
        var game = new GameWindowSnapshot(
            (nint)101,
            501,
            "Duskfade",
            "Duskfade-Win64-Shipping",
            "Duskfade-Win64-Shipping",
            2560,
            1440,
            IsKnownGame: true);
        var candidate = GameWindowCandidatePolicy.Create(game, now, Environment.ProcessId);
        Assert(
            candidate is { WindowHandle: 101, ProcessId: 501, DisplayName: "Duskfade" } &&
            candidate.Width == 2560 && candidate.Height == 1440,
            "Foreground game detection must retain the exact HWND and normalize shipping-build names.");
        var protectedFishing = GameWindowCandidatePolicy.Create(
            game with
            {
                WindowHandle = (nint)112,
                ProcessId = 512,
                WindowTitle = "Fishing",
                ProcessName = "fsh",
                DisplayName = string.Empty
            },
            now,
            Environment.ProcessId);
        var protectedBattlefield = GameWindowCandidatePolicy.Create(
            game with
            {
                WindowHandle = (nint)113,
                ProcessId = 513,
                WindowTitle = "Battlefield™ 6",
                ProcessName = "bf6",
                DisplayName = string.Empty
            },
            now,
            Environment.ProcessId);
        Assert(
            protectedFishing?.DisplayName == "Fishing" &&
            protectedBattlefield?.DisplayName == "Battlefield-6",
            "Protected games must use a canonical human-facing window title for library naming, never their abbreviated process name.");

        foreach (var rejected in new[]
                 {
                     game with { WindowHandle = (nint)102, ProcessId = Environment.ProcessId },
                     game with { WindowHandle = (nint)103, ProcessName = "explorer" },
                     game with { WindowHandle = (nint)104, IsMinimized = true },
                     game with { WindowHandle = (nint)105, IsCloaked = true },
                     game with { WindowHandle = (nint)106, IsRootWindow = false },
                     game with { WindowHandle = (nint)107, IsToolWindow = true },
                     game with { WindowHandle = (nint)108, Width = 639 },
                     game with { WindowHandle = (nint)109, WindowTitle = " " },
                     game with { WindowHandle = (nint)110, IsKnownGame = false },
                     game with
                     {
                         WindowHandle = (nint)111,
                         ProcessName = "EAAntiCheat.GameService",
                         WindowTitle = "EA Javelin Anticheat",
                         DisplayName = "EA Javelin Anticheat"
                     }
                 })
        {
            Assert(
                GameWindowCandidatePolicy.Create(rejected, now, Environment.ProcessId) is null,
                "Foreground game detection must reject app, shell, hidden, child, tool, and undersized windows.");
        }

        var monitor = new ForegroundGameWindowMonitor();
        Assert(monitor.Observe(candidate, now) is null, "A single focus sample must not become a game target.");
        Assert(monitor.Observe(candidate, now + TimeSpan.FromMilliseconds(400)) is null,
            "A transient second focus sample must not become a game target.");
        var stable = monitor.Observe(candidate, now + TimeSpan.FromMilliseconds(800));
        Assert(stable?.WindowHandle == candidate!.WindowHandle,
            "Three consecutive foreground samples must establish a stable exact-window target.");

        var processAlive = true;
        var lifetimeMonitor = new ForegroundGameWindowMonitor(_ => processAlive);
        _ = lifetimeMonitor.Observe(candidate, now);
        _ = lifetimeMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(400));
        Assert(lifetimeMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(800)) is not null,
            "A live stable game process must remain eligible for capture.");
        processAlive = false;
        Assert(lifetimeMonitor.Current(now + TimeSpan.FromSeconds(1)) is null,
            "A stable target whose game process exited must be forgotten immediately, not retained for automatic replay restart.");

        var windowOwnedByProcess = true;
        var windowLifetimeMonitor = new ForegroundGameWindowMonitor(
            _ => true,
            (_, _) => windowOwnedByProcess);
        _ = windowLifetimeMonitor.Observe(candidate, now);
        _ = windowLifetimeMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(400));
        Assert(windowLifetimeMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(800)) is not null,
            "A live stable game window must remain eligible for capture.");
        windowOwnedByProcess = false;
        Assert(windowLifetimeMonitor.Current(now + TimeSpan.FromSeconds(1)) is null,
            "A stable target whose HWND closed or changed owners must be forgotten even if its original process remains alive.");

        var observeOwnership = true;
        var observeLifetimeMonitor = new ForegroundGameWindowMonitor(
            _ => true,
            (_, _) => observeOwnership);
        _ = observeLifetimeMonitor.Observe(candidate, now);
        _ = observeLifetimeMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(400));
        Assert(observeLifetimeMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(800)) is not null,
            "The Observe path must establish a target while the HWND still belongs to its game process.");
        observeOwnership = false;
        Assert(observeLifetimeMonitor.Observe(candidate, now + TimeSpan.FromSeconds(1.2)) is null,
            "The Observe path itself must drop a recycled HWND whose owning process no longer matches.");

        var gameSwitchMonitor = new ForegroundGameWindowMonitor(_ => true, (_, _) => true);
        _ = gameSwitchMonitor.Observe(protectedBattlefield, now);
        _ = gameSwitchMonitor.Observe(protectedBattlefield, now + TimeSpan.FromMilliseconds(400));
        Assert(
            gameSwitchMonitor.Observe(protectedBattlefield, now + TimeSpan.FromMilliseconds(800))?.DisplayName ==
            "Battlefield-6",
            "Battlefield must become the stable replay target after the required foreground observations.");
        gameSwitchMonitor.Invalidate(protectedBattlefield!.WindowHandle, protectedBattlefield.ProcessId);
        Assert(gameSwitchMonitor.Current(now + TimeSpan.FromSeconds(1)) is null,
            "The replay engine's target-closed signal must immediately invalidate Battlefield's stale target.");
        _ = gameSwitchMonitor.Observe(candidate, now + TimeSpan.FromSeconds(1.2));
        _ = gameSwitchMonitor.Observe(candidate, now + TimeSpan.FromSeconds(1.6));
        Assert(
            gameSwitchMonitor.Observe(candidate, now + TimeSpan.FromSeconds(2))?.DisplayName == "Duskfade",
            "Duskfade must replace Battlefield as the stable replay target after Battlefield exits.");

        var targetSource = new FakeReplayTargetUnavailableSource();
        using (var targetTracker = new CaptureHostGameTargetTracker(
                   targetSource,
                   _ => true,
                   (_, _) => true))
        {
            _ = targetTracker.Observe(protectedBattlefield, now);
            _ = targetTracker.Observe(protectedBattlefield, now + TimeSpan.FromMilliseconds(400));
            Assert(
                targetTracker.Observe(protectedBattlefield, now + TimeSpan.FromMilliseconds(800))?.DisplayName ==
                "Battlefield-6" &&
                targetSource.SubscriberCount == 1,
                "The capture host target tracker must subscribe to replay target-lifetime notifications and retain a live Battlefield target.");
            targetSource.Raise(protectedBattlefield!);
            Assert(targetTracker.Current(now + TimeSpan.FromSeconds(1)) is null,
                "The replay engine's TargetUnavailable event must invalidate the host's stable Battlefield target.");
        }
        Assert(targetSource.SubscriberCount == 0,
            "Disposing the capture host target tracker must remove its replay-lifetime subscription.");

        using (var productionLifetimeTracker = new CaptureHostGameTargetTracker(
                   new FakeReplayTargetUnavailableSource(),
                   isWindowOwnedByProcess: (_, _) => true))
        {
            var impossibleProcess = candidate with { ProcessId = int.MaxValue };
            _ = productionLifetimeTracker.Observe(impossibleProcess, now);
            _ = productionLifetimeTracker.Observe(
                impossibleProcess,
                now + TimeSpan.FromMilliseconds(400));
            Assert(
                productionLifetimeTracker.Observe(
                    impossibleProcess,
                    now + TimeSpan.FromMilliseconds(800)) is null,
                "The production capture host tracker must apply Windows process-lifetime validation instead of retaining a dead game PID.");
        }

        var acquisitionFactoryCalled = false;
        var ownershipMismatchRejected = false;
        try
        {
            _ = ReplayCaptureTargetAcquisition.Create(
                protectedBattlefield,
                (_, _) => false,
                _ =>
                {
                    acquisitionFactoryCalled = true;
                    return new object();
                });
        }
        catch (InvalidOperationException exception) when (
            ReplayCapturePolicy.IsUnavailableGameTargetFailure(exception.Message))
        {
            ownershipMismatchRejected = true;
        }
        Assert(ownershipMismatchRejected && !acquisitionFactoryCalled,
            "Replay acquisition must reject a recycled HWND that no longer belongs to Battlefield before Windows capture is invoked.");

        var invalidWindowFailure = new System.Runtime.InteropServices.COMException(
            "The window handle is invalid.",
            unchecked((int)0x80070578));
        var acquisitionRaceNormalized = false;
        try
        {
            _ = ReplayCaptureTargetAcquisition.Create<object>(
                candidate,
                (_, _) => true,
                _ => throw invalidWindowFailure);
        }
        catch (InvalidOperationException exception) when (
            ReplayCapturePolicy.IsUnavailableGameTargetFailure(exception.Message) &&
            ReferenceEquals(exception.InnerException, invalidWindowFailure))
        {
            acquisitionRaceNormalized = true;
        }
        Assert(
            acquisitionRaceNormalized &&
            GraphicsCaptureItemFactory.IsUnavailableWindowFailure(
                new System.Runtime.InteropServices.COMException(
                    "Invalid argument.",
                    unchecked((int)0x80070057))) &&
            !GraphicsCaptureItemFactory.IsUnavailableWindowFailure(
                new InvalidOperationException("The hardware encoder failed.")),
            "A window that closes during capture acquisition must resume detection, while unrelated capture failures stay terminal.");

        var queriedMonitor = new ForegroundGameWindowMonitor();
        Assert(
            queriedMonitor.Observe(candidate, now) is null &&
            queriedMonitor.Current(now + TimeSpan.FromMilliseconds(100)) is null &&
            queriedMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(400)) is null &&
            queriedMonitor.Current(now + TimeSpan.FromMilliseconds(500)) is null &&
            queriedMonitor.Observe(candidate, now + TimeSpan.FromMilliseconds(800))?.WindowHandle ==
                candidate.WindowHandle,
            "Reading the current automatic target must not reset the consecutive-observation detector.");

        var second = candidate with { WindowHandle = (nint)202, ProcessId = 502, DisplayName = "Second game" };
        Assert(monitor.Observe(second, now + TimeSpan.FromSeconds(1.2))?.WindowHandle == candidate.WindowHandle &&
               monitor.Observe(second, now + TimeSpan.FromSeconds(1.6))?.WindowHandle == candidate.WindowHandle,
            "A brief focus change must not replace the stable game target.");
        Assert(monitor.Observe(second, now + TimeSpan.FromSeconds(2))?.WindowHandle == second.WindowHandle,
            "A newly stable game window must replace the prior target before recording begins.");
        Assert(monitor.Current(now + TimeSpan.FromSeconds(48)) is null,
            "A stale game target must expire instead of being captured much later by accident.");
        Assert(
            WindowsGameRegistration.IsKnownGameExecutable(
                @"C:\Program Files (x86)\Steam\steamapps\common\Duskfade\Duskfade.exe") &&
            WindowsGameRegistration.IsKnownGameExecutable(
                @"C:\XboxGames\Battlefield\Content\Battlefield.exe") &&
            !WindowsGameRegistration.IsKnownGameExecutable(
                @"C:\Program Files\WindowsApps\Microsoft.Paint_11.2\PaintApp\mspaint.exe") &&
            !WindowsGameRegistration.IsKnownGameExecutable(
                @"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.2\Spotify.exe") &&
            !WindowsGameRegistration.IsKnownGameExecutable(
                @"C:\Program Files\Productivity\Editor.exe"),
            "Automatic targeting must recognize actual game libraries without admitting arbitrary Microsoft Store applications.");

        var registeredNow = now;
        var registeredPaths = new[] { @"C:\Games\Existing\Existing.exe" };
        var registrationLoads = 0;
        var refreshingRegistrations = new RefreshingPathSet(
            () =>
            {
                registrationLoads++;
                return registeredPaths;
            },
            () => registeredNow,
            TimeSpan.FromSeconds(30));
        Assert(
            refreshingRegistrations.Contains(@"C:\Games\Existing\Existing.exe") &&
            refreshingRegistrations.ContainsExecutableName("Existing") &&
            refreshingRegistrations.ContainsExecutableName("Existing.exe") &&
            registrationLoads == 1,
            "Windows game registrations must identify protected games by registered process name when executable metadata is unavailable.");
        registeredPaths = [@"C:\Games\Newly Installed\NewGame.exe"];
        Assert(
            !refreshingRegistrations.Contains(@"C:\Games\Newly Installed\NewGame.exe") &&
            registrationLoads == 1,
            "Game registration polling must retain a short cache instead of reading the registry every frame.");
        registeredNow += TimeSpan.FromSeconds(31);
        Assert(
            refreshingRegistrations.Contains(@"c:\games\newly installed\newgame.exe") &&
            refreshingRegistrations.ContainsExecutableName("NewGame") &&
            registrationLoads == 2,
            "A newly installed Windows game must become detectable without restarting ClipCord.");
    }

    private static void AssertExactWindowCaptureInterop()
    {
        if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new Form
                {
                    Text = "ClipCord exact-window capture probe",
                    ClientSize = new Size(800, 450),
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-20_000, -20_000)
                };
                window.Show();
                Application.DoEvents();
                var item = GraphicsCaptureItemFactory.CreateForWindow(window.Handle);
                Assert(
                    item.Size.Width >= 800 && item.Size.Height >= 450,
                    "HWND interop must create a valid capture item for that exact window.");
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(10)),
            "Exact-window capture interop must not hang while resolving an HWND.");
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Exact-window Graphics Capture interop failed.",
                failure);
        }
    }

    private static void AssertEncodedReplayRing()
    {
        var ring = new EncodedReplayRing(TimeSpan.FromSeconds(15), maximumBytes: 1_000);
        for (var index = 0; index < 10; index++)
        {
            ring.Append(new EncodedReplaySegment(
                Enumerable.Repeat((byte)index, 100).ToArray(),
                TimeSpan.FromSeconds(index * 2),
                TimeSpan.FromSeconds((index + 1) * 2)));
        }
        var snapshot = ring.Snapshot();
        Assert(
            snapshot is not null &&
            snapshot.Segments.Count == 8 &&
            snapshot.StartTimestamp == TimeSpan.FromSeconds(4) &&
            snapshot.EndTimestamp == TimeSpan.FromSeconds(20) &&
            snapshot.ActualDuration == TimeSpan.FromSeconds(16) &&
            snapshot.TotalBytes == 800 &&
            ring.BufferedDuration == TimeSpan.FromSeconds(16),
            "The encoded replay ring must retain one GOP of pre-roll and snapshot only complete decodable segments.");

        var byteBounded = new EncodedReplayRing(TimeSpan.FromSeconds(15), maximumBytes: 250);
        for (var index = 0; index < 3; index++)
        {
            byteBounded.Append(new EncodedReplaySegment(
                new byte[100],
                TimeSpan.FromSeconds(index * 2),
                TimeSpan.FromSeconds((index + 1) * 2)));
        }
        Assert(
            byteBounded.SegmentCount == 2 && byteBounded.TotalBytes == 200 &&
            byteBounded.Snapshot()?.StartTimestamp == TimeSpan.FromSeconds(2),
            "The replay ring must evict oldest encoded GOPs when its byte budget is reached.");

        var mutationRejected = false;
        var ownedBytes = new byte[] { 1, 2, 3 };
        var ownershipRing = new EncodedReplayRing(TimeSpan.FromSeconds(15), maximumBytes: 100);
        ownershipRing.Append(new EncodedReplaySegment(
            ownedBytes,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2)));
        ownedBytes[0] = 99;
        Assert(ownershipRing.Snapshot()!.Segments[0].Mp4Bytes.Span[0] == 1,
            "The replay ring must own encoded memory instead of retaining a mutable encoder buffer.");
        try
        {
            ownershipRing.Append(new EncodedReplaySegment(
                new byte[] { 4 },
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3)));
        }
        catch (InvalidOperationException)
        {
            mutationRejected = true;
        }
        Assert(mutationRejected,
            "Overlapping or out-of-order encoded segments must be rejected before corrupting replay order.");

        var exactRangeRing = new EncodedReplayRing(TimeSpan.FromSeconds(15), maximumBytes: 1_000);
        for (var index = 0; index < 5; index++)
        {
            exactRangeRing.Append(new EncodedReplaySegment(
                Enumerable.Repeat((byte)(10 + index), 10).ToArray(),
                TimeSpan.FromSeconds(index * 2),
                TimeSpan.FromSeconds((index + 1) * 2)));
        }
        var exactRange = exactRangeRing.Snapshot(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(6));
        var gopExpandedRange = exactRangeRing.Snapshot(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
        Assert(
            exactRange is not null &&
            exactRange.Segments.Count == 2 &&
            exactRange.Segments[0].Mp4Bytes.Span[0] == 11 &&
            exactRange.Segments[1].Mp4Bytes.Span[0] == 12 &&
            exactRange.RequestedDuration == TimeSpan.FromSeconds(4) &&
            exactRange.ActualDuration == TimeSpan.FromSeconds(4) &&
            exactRange.StartTimestamp == TimeSpan.FromSeconds(2) &&
            exactRange.EndTimestamp == TimeSpan.FromSeconds(6) &&
            exactRange.TotalBytes == 20,
            "An exact Reaction Camera snapshot range must exclude GOPs that only touch either requested boundary.");
        Assert(
            gopExpandedRange is not null &&
            gopExpandedRange.Segments.Count == 2 &&
            gopExpandedRange.RequestedDuration == TimeSpan.FromSeconds(2) &&
            gopExpandedRange.StartTimestamp == TimeSpan.FromSeconds(2) &&
            gopExpandedRange.EndTimestamp == TimeSpan.FromSeconds(6) &&
            gopExpandedRange.ActualDuration == TimeSpan.FromSeconds(4),
            "A mid-GOP camera range must retain only the two overlapping decodable segments and report its actual bounds.");

        Assert(
            ReplayMemoryPolicy.GetMaximumBytes(CaptureSettings.Default) >= 64L * 1024 * 1024 &&
            ReplayMemoryPolicy.GetMaximumBytes(CaptureSettings.Default with
            {
                Resolution = CaptureResolution.UltraHd4K,
                FramesPerSecond = 60,
                ReplaySeconds = 300
            }) <= 3L * 1024 * 1024 * 1024,
            "Replay memory accounting must remain bounded for every supported capture profile.");
        var concatArguments = ReplaySnapshotMaterializer.BuildConcatArguments(
            "segments.concat.txt",
            "video.mp4");
        Assert(
            HasPair(concatArguments, "-f", "concat") &&
            HasPair(concatArguments, "-safe", "1") &&
            HasPair(concatArguments, "-i", "segments.concat.txt") &&
            HasPair(concatArguments, "-c:v", "copy") &&
            concatArguments.Contains("-an") && concatArguments[^1] == "video.mp4",
            "Replay saving must concatenate only ClipCord's generated list and must never re-encode buffered video.");
    }

    private static void AssertReplayArmingPolicy()
    {
        var waiting = new ReplayCaptureStatus(
            ReplayCaptureState.Off,
            null,
            null,
            TimeSpan.Zero,
            0);
        var buffering = waiting with
        {
            State = ReplayCaptureState.Buffering,
            Target = new ManualCaptureTarget("Game", 1920, 1080),
            BufferedDuration = TimeSpan.FromSeconds(5),
            ResidentBytes = 1_000
        };
        var gameClosed = waiting with
        {
            State = ReplayCaptureState.Failed,
            LastError = "The detected game window closed."
        };
        var encoderFailed = waiting with
        {
            State = ReplayCaptureState.Failed,
            LastError = "The hardware encoder failed."
        };

        Assert(
            ReplayCapturePolicy.IsWaitingForGame(true, waiting) &&
            ReplayCapturePolicy.ShouldAttemptAutomaticStart(true, true, waiting) &&
            !ReplayCapturePolicy.CanSave(true, waiting) &&
            !ReplayCapturePolicy.ShouldAttemptAutomaticStart(true, false, waiting) &&
            !ReplayCapturePolicy.ShouldAttemptAutomaticStart(false, true, waiting),
            "Armed Instant Replay must wait for a game without allowing the shortcut to create a clip.");
        Assert(
            ReplayCapturePolicy.CanSave(true, buffering) &&
            !ReplayCapturePolicy.CanSave(false, buffering) &&
            !ReplayCapturePolicy.ShouldAttemptAutomaticStart(true, true, buffering),
            "Only an enabled, actively buffering replay may produce a saved clip.");
        Assert(
            ReplayCapturePolicy.ClassifySaveAvailability(false, waiting) == ReplaySaveAvailability.Off &&
            ReplayCapturePolicy.ClassifySaveAvailability(true, waiting) == ReplaySaveAvailability.WaitingForGame &&
            ReplayCapturePolicy.ClassifySaveAvailability(true, buffering) == ReplaySaveAvailability.Ready &&
            ReplayCapturePolicy.ClassifySaveAvailability(true, encoderFailed) == ReplaySaveAvailability.NeedsAttention,
            "The replay shortcut must distinguish waiting from a real encoder failure so its user message remains truthful.");
        Assert(
            ReplayCapturePolicy.IsWaitingForGame(true, gameClosed) &&
            ReplayCapturePolicy.ShouldAttemptAutomaticStart(true, true, gameClosed) &&
            !ReplayCapturePolicy.IsWaitingForGame(true, encoderFailed) &&
            !ReplayCapturePolicy.ShouldAttemptAutomaticStart(true, true, encoderFailed),
            "Closing a game must return the armed recorder to detection without hiding an encoder failure as a waiting state.");
        Assert(
            ReplayCapturePolicy.IsUnavailableGameTargetFailure(
                "The detected game window closed before capture could start.") &&
            ReplayCapturePolicy.IsUnavailableGameTargetFailure(
                "Windows did not return a capture item for that game window.") &&
            ReplayCapturePolicy.IsUnavailableGameTargetFailure(
                "A valid game window is required.") &&
            !ReplayCapturePolicy.IsUnavailableGameTargetFailure(
                "The hardware encoder failed."),
            "Only target-window acquisition races may return Instant Replay to game detection; encoder failures must remain terminal.");

        var saveSucceededAfterClose = ReplayCapturePolicy.CompleteSaveSuccess(
            ReplayCaptureState.Failed,
            gameClosed.LastError);
        var saveFailedAfterClose = ReplayCapturePolicy.CompleteSaveFailure(
            ReplayCaptureState.Failed,
            gameClosed.LastError,
            encoderRunning: false,
            "A later save failure");
        Assert(
            saveSucceededAfterClose == (ReplayCaptureState.Failed, gameClosed.LastError) &&
            saveFailedAfterClose == (ReplayCaptureState.Failed, gameClosed.LastError) &&
            ReplayCapturePolicy.CompleteSaveSuccess(ReplayCaptureState.Saving, null) ==
                (ReplayCaptureState.Buffering, null) &&
            ReplayCapturePolicy.CompleteSaveFailure(
                ReplayCaptureState.Saving,
                null,
                encoderRunning: false,
                "Save failed") == (ReplayCaptureState.Failed, "Save failed"),
            "A late save completion must never overwrite a concurrent game-closed failure or its recovery marker.");

        var duskfadeBuffering = buffering with
        {
            Target = new ManualCaptureTarget("Duskfade", 2560, 1440)
        };
        var enabledSettings = CaptureSettings.Default with { InstantReplayEnabled = true };
        var retryingController = new ScriptedReplayController(
            ("The detected game window closed.", gameClosed),
            ("ClipCord has not detected a stable game window yet.", gameClosed),
            (null, duskfadeBuffering));
        var retryDelays = 0;
        ReplayAutomaticStartRunner.RunAsync(
                retryingController,
                () => enabledSettings,
                () => true,
                (delay, _) =>
                {
                    Assert(delay == ReplayAutomaticStartRunner.RetryDelay,
                        "Automatic replay recovery must use its bounded retry delay.");
                    retryDelays++;
                    return Task.CompletedTask;
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert(
            retryingController.StartCount == 3 &&
            retryDelays == 2 &&
            retryingController.ReplayStatus is
            {
                State: ReplayCaptureState.Buffering,
                Target.DisplayName: "Duskfade"
            },
            "Automatic replay recovery must survive Battlefield closing and the no-target gap, then buffer Duskfade without an outside retry trigger.");

        var failingController = new ScriptedReplayController(
            ("The hardware encoder failed.", encoderFailed));
        var hardFailureSurfaced = false;
        try
        {
            ReplayAutomaticStartRunner.RunAsync(
                    failingController,
                    () => enabledSettings,
                    () => true,
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (CaptureHostCommandException exception) when (
            exception.Message.Contains("hardware encoder", StringComparison.OrdinalIgnoreCase))
        {
            hardFailureSurfaced = true;
        }
        Assert(hardFailureSurfaced && failingController.StartCount == 1,
            "Automatic replay recovery must not loop over a real encoder failure.");
    }

    private static void AssertReplayVideoSegmentsWhenEnabled(string testRoot)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CLIPCORD_RUN_HARDWARE_CAPTURE_TESTS"),
                "1",
                StringComparison.Ordinal) ||
            !Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
        {
            return;
        }
        var ffmpeg = FfmpegCompressor.FindExecutable();
        if (ffmpeg is null)
        {
            Console.WriteLine("  (skipped the hardware replay finalization check: ffmpeg.exe was not found)");
            return;
        }

        Exception? failure = null;
        EncodedReplaySnapshot? snapshot = null;
        ManualCaptureResult? saved = null;
        ManualCaptureResult? engineSaved = null;
        ManualCaptureResult? engineSavedAgain = null;
        ReplayCaptureStatus? engineSavingStatus = null;
        ReplayCaptureStatus? engineStoppedStatus = null;
        var thread = new Thread(() =>
        {
            using var window = new Form
            {
                Text = "ClipCord replay encoder probe",
                ClientSize = new Size(800, 450),
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40)
            };
            using var animation = new System.Windows.Forms.Timer { Interval = 16 };
            var frame = 0;
            animation.Tick += (_, _) =>
            {
                frame++;
                window.BackColor = Color.FromArgb(
                    20 + frame % 180,
                    35 + frame * 3 % 160,
                    50 + frame * 7 % 140);
                window.Invalidate();
            };
            window.Shown += async (_, _) =>
            {
                try
                {
                    using (var pausedWindow = new Form
                    {
                        Text = "ClipCord paused-game readiness probe",
                        ClientSize = new Size(800, 450),
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-20_000, -20_000)
                    })
                    {
                        pausedWindow.Show();
                        Application.DoEvents();
                        var pausedItem = GraphicsCaptureItemFactory.CreateForWindow(pausedWindow.Handle);
                        pausedWindow.Hide();
                        var pausedRing = new EncodedReplayRing(
                            TimeSpan.FromSeconds(15),
                            64L * 1024 * 1024);
                        using var pausedEncoder = new ReplayVideoSegmentEncoder(
                            pausedItem,
                            new Windows.Graphics.SizeInt32(800, 450),
                            bitrate: 2_000_000,
                            frameRate: 30,
                            suppressSystemBorder: false,
                            pausedRing);
                        var readinessStarted = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            await pausedEncoder.StartAsync().WaitAsync(TimeSpan.FromSeconds(4));
                        }
                        catch (TimeoutException)
                        {
                            Assert(false,
                                "Replay startup must report pipeline readiness without waiting for a paused game to deliver a complete GOP.");
                        }
                        readinessStarted.Stop();
                        Assert(
                            readinessStarted.Elapsed < TimeSpan.FromSeconds(4) &&
                            pausedRing.SegmentCount == 0 && pausedEncoder.IsRunning,
                            "Replay startup must report pipeline readiness without waiting for a paused game to deliver a complete GOP.");
                        await pausedEncoder.StopAsync();
                    }

                    animation.Start();
                    var item = GraphicsCaptureItemFactory.CreateForWindow(window.Handle);
                    var ring = new EncodedReplayRing(TimeSpan.FromSeconds(15), 64L * 1024 * 1024);
                    using var encoder = new ReplayVideoSegmentEncoder(
                        item,
                        new Windows.Graphics.SizeInt32(800, 450),
                        bitrate: 2_000_000,
                        frameRate: 30,
                        suppressSystemBorder: false,
                        ring);
                    await encoder.StartAsync();
                    var segmentDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                    while (ring.SegmentCount == 0 && DateTimeOffset.UtcNow < segmentDeadline)
                    {
                        await Task.Delay(100);
                    }
                    await encoder.StopAsync();
                    snapshot = ring.Snapshot();
                    if (snapshot is not null)
                    {
                        saved = await ReplaySnapshotMaterializer.SaveAsync(
                            snapshot,
                            audioSession: null,
                            CaptureSettings.Default with
                            {
                                RecordGameAudio = false,
                                LibraryRoot = Path.Combine(testRoot, "replay-hardware")
                            },
                            "Replay Probe");
                    }

                    var engineSettings = CaptureSettings.Default with
                    {
                        ReplaySeconds = 15,
                        RecordGameAudio = false,
                        IncludeMicrophone = false,
                        IncludeVoiceChat = false,
                        LibraryRoot = Path.Combine(testRoot, "replay-engine")
                    };
                    using var engine = new WindowsReplayCaptureEngine();
                    await engine.StartAsync(
                        new GameWindowCandidate(
                            window.Handle,
                            Environment.ProcessId,
                            "Replay Engine Probe",
                            window.Width,
                            window.Height,
                            DateTimeOffset.UtcNow),
                        engineSettings);
                    var engineDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                    while (engine.Status.State == ReplayCaptureState.Starting &&
                           DateTimeOffset.UtcNow < engineDeadline)
                    {
                        await Task.Delay(100);
                    }
                    engineSaved = await engine.SaveAsync();
                    engineSavingStatus = engine.Status;
                    var nextSegmentDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                    var previousDuration = engineSavingStatus.BufferedDuration;
                    while (engine.Status.BufferedDuration <= previousDuration &&
                           DateTimeOffset.UtcNow < nextSegmentDeadline)
                    {
                        await Task.Delay(100);
                    }
                    engineSavedAgain = await engine.SaveAsync();
                    await engine.StopAsync();
                    engineStoppedStatus = engine.Status;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    animation.Stop();
                    window.Close();
                }
            };
            Application.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(45)),
            "The in-memory hardware replay probe must finish without hanging.");
        if (failure is not null)
        {
            throw new InvalidOperationException("The in-memory hardware replay probe failed.", failure);
        }
        Assert(
            snapshot is { Segments.Count: >= 1, TotalBytes: > 1_024 } &&
            snapshot.Segments.All(segment =>
                segment.Mp4Bytes.Span.IndexOf("ftyp"u8) is >= 0 and < 32) &&
            saved is not null && File.Exists(saved.FilePath) &&
            new FileInfo(saved.FilePath).Length > 1_024 &&
            engineSaved is not null && File.Exists(engineSaved.FilePath) &&
            new FileInfo(engineSaved.FilePath).Length > 1_024 &&
            engineSavedAgain is not null && File.Exists(engineSavedAgain.FilePath) &&
            new FileInfo(engineSavedAgain.FilePath).Length > 1_024 &&
            !string.Equals(engineSaved.FilePath, engineSavedAgain.FilePath, StringComparison.OrdinalIgnoreCase) &&
            engineSavingStatus is
            {
                State: ReplayCaptureState.Buffering,
                LastResult: not null,
                ResidentBytes: > 0
            } &&
            engineStoppedStatus is { State: ReplayCaptureState.Off, ResidentBytes: 0 },
            "The Windows replay engine must buffer encoded MP4 GOPs, save consecutive Gallery clips, resume buffering, and release its memory when stopped.");

        foreach (var result in new[] { saved!, engineSaved!, engineSavedAgain! })
        {
            var probe = FfmpegCompressor.ProbeMediaAsync(
                result.FilePath,
                ffmpeg,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(probe.Duration > TimeSpan.FromMilliseconds(250),
                "Every materialized replay must expose a valid positive media duration.");
            FfmpegCompressor.RunAsync(
                ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-i", result.FilePath,
                    "-frames:v", "1", "-f", "null", "NUL"
                ],
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static void AssertReplayAudioRing()
    {
        var ownershipRing = new PcmReplayRing(TimeSpan.FromSeconds(5), maximumBytes: 20);
        var mutable = new byte[] { 1, 2, 3 };
        ownershipRing.Append(new PcmReplayPacket(mutable, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        mutable[0] = 99;
        Assert(
            ownershipRing.Snapshot(TimeSpan.Zero, TimeSpan.FromSeconds(1))!
                .Packets[0].Bytes.Span[0] == 1,
            "The PCM replay ring must own packet bytes instead of retaining mutable WASAPI buffers.");

        var ring = new PcmReplayRing(TimeSpan.FromSeconds(5), maximumBytes: 10);
        for (var index = 0; index < 4; index++)
        {
            ring.Append(new PcmReplayPacket(
                new byte[] { (byte)index, 2, 3 },
                TimeSpan.FromSeconds(index),
                TimeSpan.FromSeconds(index + 1)));
        }
        var snapshot = ring.Snapshot(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3.5));
        Assert(
            ring.TotalBytes == 9 &&
            snapshot is { Packets.Count: 3, TotalBytes: 9 } &&
            snapshot.StartTimestamp == TimeSpan.FromSeconds(1) &&
            snapshot.EndTimestamp == TimeSpan.FromSeconds(4) &&
            snapshot.Packets[0].Bytes.Span[0] == 1,
            "The PCM replay ring must evict by budget and include boundary packets for QPC alignment.");

        var noAudio = CaptureSettings.Default with
        {
            RecordGameAudio = false,
            IncludeMicrophone = false,
            IncludeVoiceChat = false
        };
        var everyAudioSource = CaptureSettings.Default with
        {
            IncludeMicrophone = true,
            IncludeVoiceChat = true
        };
        Assert(
            ReplayMemoryPolicy.GetEstimatedResidentBytes(everyAudioSource) >
            ReplayMemoryPolicy.GetEstimatedResidentBytes(noAudio),
            "The live memory estimate must include enabled raw PCM rings instead of showing encoded clip size alone.");
    }

    private static void AssertReplayTargetLifetimeSubscriptions()
    {
        var battlefield = new GameWindowCandidate(
            (nint)501,
            1501,
            "Battlefield-6",
            2560,
            1440,
            DateTimeOffset.UtcNow);
        var duskfade = battlefield with
        {
            WindowHandle = (nint)502,
            ProcessId = 1502,
            DisplayName = "Duskfade"
        };

        var processSignal = new FakeReplayTargetLifetimeSignal();
        var captureSignal = new FakeReplayTargetLifetimeSignal();
        var unavailable = new List<GameWindowCandidate>();
        using (var subscription = new ReplayTargetLifetimeSubscription(
                   battlefield,
                   processSignal,
                   captureSignal,
                   unavailable.Add))
        {
            subscription.Attach();
            Assert(
                processSignal.StartCount == 1 &&
                captureSignal.StartCount == 1 &&
                unavailable.Count == 0,
                "Replay target lifetime monitoring must subscribe to both process exit and capture-item closure exactly once.");
            processSignal.Raise();
            Assert(subscription.IsUnavailable && unavailable.Count == 0,
                "A target that closes during startup must be latched until the replay engine installs its active candidate.");
            subscription.Arm();
            captureSignal.Raise();
            Assert(
                unavailable is [{ WindowHandle: 501, ProcessId: 1501 }] &&
                ReferenceEquals(unavailable[0], battlefield),
                "Arming must deliver a latched process exit once, and a later capture-item closure must not duplicate it.");
        }
        processSignal.Raise();
        captureSignal.Raise();
        Assert(
            processSignal.DisposeCount == 1 &&
            captureSignal.DisposeCount == 1 &&
            unavailable.Count == 1,
            "Disposing replay target lifetime monitoring must detach both signals and ignore stale callbacks.");

        var secondProcessSignal = new FakeReplayTargetLifetimeSignal();
        var secondCaptureSignal = new FakeReplayTargetLifetimeSignal();
        var captureUnavailable = new List<GameWindowCandidate>();
        using (var subscription = new ReplayTargetLifetimeSubscription(
                   duskfade,
                   secondProcessSignal,
                   secondCaptureSignal,
                   captureUnavailable.Add))
        {
            subscription.Attach();
            subscription.Arm();
            secondCaptureSignal.Raise();
            Assert(
                secondProcessSignal.StartCount == 1 &&
                secondCaptureSignal.StartCount == 1 &&
                captureUnavailable is [{ WindowHandle: 502, ProcessId: 1502 }],
                "Capture-item closure must independently mark the exact active Duskfade target unavailable.");
        }

        Assert(
            ReplayCapturePolicy.IsSameTarget(battlefield, battlefield) &&
            !ReplayCapturePolicy.IsSameTarget(duskfade, battlefield) &&
            !ReplayCapturePolicy.IsSameTarget(null, battlefield),
            "A late Battlefield lifetime callback must never fail a replacement Duskfade replay session.");
    }

    private static void AssertCaptureAudioMuxContract()
    {
        ISet<string> endpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert(
            CaptureAudioEndpointPolicy.TryReserve(endpointIds, "SONAR-GAMING") &&
            !CaptureAudioEndpointPolicy.TryReserve(endpointIds, "sonar-gaming") &&
            CaptureAudioEndpointPolicy.TryReserve(endpointIds, "SONAR-CHAT") &&
            endpointIds.Count == 2,
            "Manual and replay capture must suppress duplicate Windows endpoint IDs without merging distinct Sonar routes.");

        var videoStart = TimeSpan.FromSeconds(100);
        var tracks = new[]
        {
            new CaptureAudioMuxTrack("game.wav", videoStart - TimeSpan.FromMilliseconds(250)),
            new CaptureAudioMuxTrack("chat.wav", videoStart + TimeSpan.FromMilliseconds(125))
        };
        var arguments = CaptureAudioMuxer.BuildArguments(
            "video.mp4",
            tracks,
            videoStart,
            "completed.mp4");
        var filterIndex = Array.IndexOf(arguments.ToArray(), "-filter_complex");
        var filter = arguments[filterIndex + 1];
        Assert(
            HasPair(arguments, "-i", "video.mp4") &&
            HasPair(arguments, "-i", "game.wav") &&
            HasPair(arguments, "-i", "chat.wav") &&
            filter.Contains("atrim=start=0.250000", StringComparison.Ordinal) &&
            filter.Contains("adelay=125:all=1", StringComparison.Ordinal) &&
            filter.Contains("amix=inputs=2:duration=longest:normalize=0", StringComparison.Ordinal) &&
            filter.Contains("alimiter=limit=0.95:level=disabled,apad", StringComparison.Ordinal) &&
            HasPair(arguments, "-map", "0:v:0") &&
            HasPair(arguments, "-map", FfmpegCompressor.MixedAudioLabel) &&
            HasPair(arguments, "-c:v", "copy") &&
            HasPair(arguments, "-c:a", "aac") &&
            HasPair(arguments, "-b:a", "192k") &&
            arguments.Contains("-shortest") &&
            arguments[^1] == "completed.mp4",
            "Capture finalization must align early and late endpoints, mix once, stream-copy video, and bound audio to the clip.");

        var invalidClockRejected = false;
        try
        {
            CaptureAudioMuxer.BuildArguments(
                "video.mp4",
                [new CaptureAudioMuxTrack("game.wav", videoStart + TimeSpan.FromMinutes(1))],
                videoStart,
                "completed.mp4");
        }
        catch (InvalidOperationException)
        {
            invalidClockRejected = true;
        }
        Assert(invalidClockRejected,
            "Audio muxing must reject clocks too far apart instead of silently creating a badly desynchronized clip.");
    }

    private static void AssertFfmpegPublicFailure()
    {
        const string privateUser = "private-user";
        const string clipName = "secret tournament final.mp4";
        const string windowsPath = @"C:\Users\private-user\Videos\secret tournament final.mp4";
        const string uncPath = @"\\private-nas\private-share\secret tournament final.mp4";
        const string privateUrl =
            "https://media.example.test/download/secret-tournament-final.mp4?token=private-bearer-token";
        const string webhook =
            "https://discord.com/api/webhooks/123456789012345678/private-webhook-token";
        var hostileDiagnostic = string.Join(
            Environment.NewLine,
            $"ffmpeg -hide_banner -i \"{windowsPath}\" -filter_complex private-user-overlay output.mp4",
            $"ffmpeg -i {uncPath} -metadata source={privateUrl}",
            $"Request context: {webhook}",
            $"Error opening input file {windowsPath}: Permission denied",
            "Invalid data found when processing input",
            "No space left on device",
            "Error code: -22; HRESULT error code: 0x80070005");
        var safeFailure = MediaToolDiagnosticSanitizer.BuildFfmpegFailureMessage(
            MediaToolOperation.General,
            7,
            hostileDiagnostic);
        var privateValues = new[]
        {
            privateUser,
            clipName,
            windowsPath,
            uncPath,
            "private-nas",
            "private-share",
            privateUrl,
            "private-bearer-token",
            webhook,
            "private-webhook-token",
            "ffmpeg -hide_banner",
            "-filter_complex",
            "output.mp4"
        };
        Assert(
            safeFailure.Contains("FFmpeg exited with code 7.", StringComparison.Ordinal) &&
            safeFailure.Contains("permission denied", StringComparison.Ordinal) &&
            safeFailure.Contains("invalid media data", StringComparison.Ordinal) &&
            safeFailure.Contains("storage is full", StringComparison.Ordinal) &&
            safeFailure.Contains("-22", StringComparison.Ordinal) &&
            safeFailure.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) &&
            safeFailure.Length <= 512 &&
            !safeFailure.Contains(@":\", StringComparison.Ordinal) &&
            !safeFailure.Contains(@"\\", StringComparison.Ordinal) &&
            !safeFailure.Contains("https://", StringComparison.OrdinalIgnoreCase) &&
            !safeFailure.Contains(".mp4", StringComparison.OrdinalIgnoreCase) &&
            privateValues.All(value =>
                !safeFailure.Contains(value, StringComparison.OrdinalIgnoreCase)),
            $"Media-tool failure logs must retain only useful allow-listed categories and codes: {safeFailure}");

        var unclassified = MediaToolDiagnosticSanitizer.BuildFfmpegFailureMessage(
            MediaToolOperation.SilhouetteMatte,
            9,
            $"{windowsPath} {uncPath} {privateUrl} {webhook} {privateUser} {clipName}");
        Assert(
            unclassified ==
                "FFmpeg failed during silhouette matte generation with code 9. " +
                "Diagnostic category: unclassified media-tool failure." &&
            privateValues.All(value =>
                !unclassified.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "An unrecognized FFmpeg diagnostic must collapse to a fixed safe category instead of copying text.");

        var categoryFlood = string.Join(
            Environment.NewLine,
            "Access denied",
            "File not found",
            "Invalid data found",
            "Invalid argument",
            "Unknown encoder",
            "Error initializing output stream",
            "Error opening input",
            "Error opening output",
            "Error while filtering",
            "Conversion failed",
            "No space left on device",
            "Device or resource busy",
            "Broken pipe",
            "Cannot allocate memory",
            "Hardware acceleration failed",
            "Error code: -1",
            "Error code: -2",
            "Error code: -3",
            "Error code: -4",
            "Error code: -5");
        var longDiagnostic =
            new string('x', 70_000) + Environment.NewLine +
            hostileDiagnostic + Environment.NewLine + categoryFlood;
        var bounded = MediaToolDiagnosticSanitizer.BuildFfmpegFailureMessage(
            MediaToolOperation.SilhouetteMatteInspection,
            11,
            longDiagnostic);
        Assert(
            bounded.Length <= 512 &&
            bounded.Contains("bounded diagnostic tail", StringComparison.Ordinal) &&
            bounded.Contains("permission denied", StringComparison.Ordinal) &&
            !bounded.Contains("-5", StringComparison.Ordinal) &&
            privateValues.All(value =>
                !bounded.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "Large media-tool diagnostics must be inspected and emitted through fixed independent bounds.");

        var categoryCapSummary = MediaToolDiagnosticSanitizer.Summarize(categoryFlood);
        var cappedCategories = ExtractDiagnosticCategories(categoryCapSummary);
        Assert(
            cappedCategories.Length == 8 &&
            !categoryCapSummary.Contains("media filter failed", StringComparison.Ordinal),
            $"Media-tool summaries must emit exactly the first eight matched safe categories: {categoryCapSummary}");

        var hostileCodeProbes = new[]
        {
            @"error C:\Users\bob\clip.mp4",
            "code = alice",
            @"errno: \\host\share",
            "error: 0xDEADBEEFCAFEBABE1234"
        };
        foreach (var hostileCodeProbe in hostileCodeProbes)
        {
            var summary = MediaToolDiagnosticSanitizer.Summarize(hostileCodeProbe);
            var emittedCodes = ExtractDiagnosticCodes(summary);
            Assert(
                emittedCodes.Length == 0 && emittedCodes.All(IsAllowedDiagnosticCode),
                $"Text, paths, user data, and oversized hexadecimal values must never enter the diagnostic-code channel: {summary}");
        }

        var validCodeSummary = MediaToolDiagnosticSanitizer.Summarize(
            "error code: -22; errno = 13; code=0x80070005; code = alice; error: 0xDEADBEEFCAFEBABE1234");
        var validCodes = ExtractDiagnosticCodes(validCodeSummary);
        Assert(
            validCodes.SequenceEqual(["-22", "13", "0x80070005"], StringComparer.OrdinalIgnoreCase) &&
            validCodes.All(IsAllowedDiagnosticCode),
            $"The diagnostic-code channel must retain only bounded decimal or hexadecimal codes: {validCodeSummary}");

        var observedLogLines = new List<string>();
        using (Log.ObserveForTests(observedLogLines.Add))
        {
            try
            {
                FfmpegCompressor.RunAsync(
                    Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    ["/d", "/c", $"echo {windowsPath}: Permission denied 1>&2 & exit /b 7"],
                    CancellationToken.None).GetAwaiter().GetResult();
                throw new InvalidOperationException("The failing media-process probe unexpectedly succeeded.");
            }
            catch (InvalidOperationException exception)
            {
                Assert(
                    exception.Message == "ClipCord's local media processor could not complete the requested operation." &&
                    !exception.Message.Contains(privateUser, StringComparison.OrdinalIgnoreCase) &&
                    !exception.Message.Contains(clipName, StringComparison.OrdinalIgnoreCase),
                    "FFmpeg diagnostics must never become a path-bearing public error.");
            }
        }
        Assert(
            observedLogLines.Count == 1 &&
            observedLogLines[0].Contains("FFmpeg exited with code 7.", StringComparison.Ordinal) &&
            observedLogLines[0].Contains("permission denied", StringComparison.Ordinal) &&
            !observedLogLines[0].Contains(@":\", StringComparison.Ordinal) &&
            !observedLogLines[0].Contains(@"\\", StringComparison.Ordinal) &&
            !observedLogLines[0].Contains("https://", StringComparison.OrdinalIgnoreCase) &&
            !observedLogLines[0].Contains(".mp4", StringComparison.OrdinalIgnoreCase) &&
            privateValues.All(value =>
                !observedLogLines[0].Contains(value, StringComparison.OrdinalIgnoreCase)),
            "The real media-process failure path must log only the centralized safe summary.");
    }

    private static string[] ExtractDiagnosticCategories(string summary)
    {
        const string marker = "Diagnostic categories: ";
        var start = summary.IndexOf(marker, StringComparison.Ordinal);
        Assert(start >= 0, $"Expected a categorized media-tool summary, but received: {summary}");
        start += marker.Length;
        var end = summary.IndexOf('.', start);
        Assert(end > start, $"Expected a bounded category list, but received: {summary}");
        return summary[start..end].Split(", ", StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] ExtractDiagnosticCodes(string summary)
    {
        const string marker = " Diagnostic codes: ";
        var start = summary.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        start += marker.Length;
        var end = summary.IndexOf('.', start);
        Assert(end > start, $"Expected a bounded diagnostic-code list, but received: {summary}");
        return summary[start..end].Split(", ", StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsAllowedDiagnosticCode(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length is < 3 or > 10)
            {
                return false;
            }

            for (var index = 2; index < value.Length; index++)
            {
                if (!char.IsAsciiHexDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        var digitStart = value.Length > 0 && value[0] is '-' or '+' ? 1 : 0;
        var digitCount = value.Length - digitStart;
        if (digitCount is < 1 or > 10)
        {
            return false;
        }

        for (var index = digitStart; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertCaptureHostLifecycle()
    {
        var singleFlight = new SingleFlightGate();
        Assert(
            singleFlight.TryEnter() && !singleFlight.TryEnter(),
            "Automatic replay startup must admit only one concurrent detection loop.");
        singleFlight.Exit();
        Assert(singleFlight.TryEnter(),
            "Automatic replay startup must become available after the active detection loop exits.");
        singleFlight.Exit();

        var lostWakeController = new LostWakeReplayController();
        using (var coordinatorLifetime = new CancellationTokenSource())
        {
            var failures = new List<Exception>();
            var enabledSettings = CaptureSettings.Default with { InstantReplayEnabled = true };
            var coordinator = new ReplayAutomaticStartCoordinator(
                lostWakeController,
                () => enabledSettings,
                () => true,
                (_, _) => Task.CompletedTask,
                failures.Add,
                coordinatorLifetime.Token);
            var firstRequest = coordinator.RequestAsync();
            Assert(
                lostWakeController.FirstStartEntered.Task.Wait(TimeSpan.FromSeconds(2)),
                "The dropped-wake regression fixture must hold the first automatic replay start inside the single-flight gate.");
            coordinator.RequestAsync().GetAwaiter().GetResult();
            lostWakeController.ReleaseFirstStart();
            firstRequest.GetAwaiter().GetResult();
            Assert(
                SpinWait.SpinUntil(
                    () => lostWakeController.ReplayStatus.State == ReplayCaptureState.Buffering,
                    TimeSpan.FromSeconds(2)) &&
                lostWakeController.StartCount == 2 &&
                lostWakeController.MaximumConcurrentStarts == 1 &&
                failures.Count == 0,
                "A replay-start request dropped while the gate is held must be recovered after release without allowing concurrent starts.");
        }

        var hotkeyRegistrar = new FakeGlobalHotkeyRegistrar();
        using (var hotkeyManager = new GlobalHotkeyManager(hotkeyRegistrar))
        {
            var enabledSettings = CaptureSettings.Default with { InstantReplayEnabled = true };
            Assert(
                CaptureHotkeyRegistration.TryApply(
                    hotkeyManager,
                    enabledSettings,
                    out var initiallyAvailable,
                    out _) && initiallyAvailable,
                "The configured replay shortcut must begin available.");
            hotkeyRegistrar.RegisterResults.Enqueue(false);
            hotkeyRegistrar.RegisterResults.Enqueue(true);
            var conflictingSettings = enabledSettings with { SaveHotkey = "Ctrl + Shift + F11" };
            Assert(
                !CaptureHotkeyRegistration.TryApply(
                    hotkeyManager,
                    conflictingSettings,
                    out var availableAfterRollback,
                    out _) &&
                availableAfterRollback &&
                hotkeyManager.GetBinding(GlobalHotkeyManager.CaptureHotkeyIdentifier) is not null,
                "A failed replay-hotkey change must keep the rolled-back shortcut available to automatic capture.");
        }

        var parentExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentStart = new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        parentStart.ArgumentList.Add("/d");
        parentStart.ArgumentList.Add("/c");
        parentStart.ArgumentList.Add("exit /b 0");
        using (var parentProbe = System.Diagnostics.Process.Start(parentStart) ??
               throw new InvalidOperationException("Could not launch the capture parent-lifetime probe."))
        {
            CaptureHostParentLifetime.WatchAsync(
                    parentProbe.Id,
                    () => parentExited.TrySetResult())
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
        }
        Assert(parentExited.Task.IsCompleted,
            "The isolated capture worker must observe parent termination and request its own exit.");

        var validArguments = new[]
        {
            CaptureHostLaunchOptions.HostArgument,
            CaptureHostLaunchOptions.PipeArgument,
            CaptureHostLaunchOptions.PipePrefix + "123-0123456789abcdef",
            CaptureHostLaunchOptions.UrgentPipeArgument,
            CaptureHostLaunchOptions.UrgentPipePrefix + "123-0123456789abcdef",
            CaptureHostLaunchOptions.ParentArgument,
            (Environment.ProcessId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        Assert(
            CaptureHostLaunchOptions.TryParse(validArguments, out var parsed) &&
            parsed is not null &&
            parsed.PipeName == validArguments[2] &&
            parsed.UrgentPipeName == validArguments[4] &&
            !CaptureHostLaunchOptions.TryParse(
                [CaptureHostLaunchOptions.HostArgument, CaptureHostLaunchOptions.PipeArgument, @"..\hostile"],
                out _),
            "Capture-host launch arguments must require distinct private ordinary and urgent pipe shapes.");

        var request = new CaptureHostMessage(
            CaptureHostProtocol.Version,
            CaptureHostProtocol.Ping,
            41);
        Assert(
            CaptureHostProtocol.Deserialize(CaptureHostProtocol.Serialize(request)) == request,
            "Capture-host protocol messages must round-trip without losing their request identity.");
        Assert(
            CaptureHostProtocol.Version == 8 &&
            CaptureHostProtocol.DetectTarget == "detectTarget",
            "Capture-host protocol v8 must expose automatic targeting, Instant Replay, and start-time silhouette preference snapshots.");
        Assert(
            CaptureHostProtocol.StartReplay == "startReplay" &&
            CaptureHostProtocol.StopReplay == "stopReplay" &&
            CaptureHostProtocol.SaveReplay == "saveReplay" &&
            CaptureHostProtocol.DisableReactionCamera == "disableReactionCamera" &&
            CaptureHostCommandRouting.UsesUrgentChannel(
                CaptureHostProtocol.DisableReactionCamera) &&
            !CaptureHostCommandRouting.UsesUrgentChannel(CaptureHostProtocol.SaveReplay),
            "Capture-host protocol v8 must route only the privacy-critical camera-off command over its urgent channel.");
        Assert(
            CaptureHostUrgentChannelPolicy.GetAction(
                hostProcessRunning: true,
                mainChannelConnected: true,
                urgentChannelConnected: false) == CaptureHostUrgentChannelAction.FailClosed &&
            CaptureHostUrgentChannelPolicy.GetAction(true, true, true) ==
                CaptureHostUrgentChannelAction.Send &&
            CaptureHostUrgentChannelPolicy.GetAction(true, false, true) ==
                CaptureHostUrgentChannelAction.Send &&
            CaptureHostUrgentChannelPolicy.GetAction(true, false, false) ==
                CaptureHostUrgentChannelAction.FailClosed &&
            CaptureHostUrgentChannelPolicy.GetAction(false, false, false) ==
                CaptureHostUrgentChannelAction.StartHost,
            "A missing urgent channel on a live ordinary host must fail closed immediately instead of queuing behind a blocked replay save.");

        AssertCaptureHostCallbackInversionSuppressesEarlierStart();
        AssertCaptureHostCommandSequencePrecedesLifecycleGate();
        AssertCaptureHostRejectsStaleReactionCameraOffAcknowledgement();
        AssertUrgentCaptureHostChannelBypassesBlockedMainCommand();
        Assert(
            CapturePipelineArbitration.CanStartManual(ReplayCaptureState.Off) &&
            !CapturePipelineArbitration.CanStartManual(ReplayCaptureState.Starting) &&
            !CapturePipelineArbitration.CanStartManual(ReplayCaptureState.Buffering) &&
            CapturePipelineArbitration.CanStartReplay(ManualCaptureState.NoTarget) &&
            CapturePipelineArbitration.CanStartReplay(ManualCaptureState.Ready) &&
            !CapturePipelineArbitration.CanStartReplay(ManualCaptureState.Starting) &&
            !CapturePipelineArbitration.CanStartReplay(ManualCaptureState.Recording) &&
            !CapturePipelineArbitration.CanStartReplay(ManualCaptureState.Finalizing),
            "The isolated worker must enforce manual/replay pipeline exclusion in both directions.");

        var manualBlocked = false;
        var replayBlocked = false;
        try
        {
            CaptureHostCommandGate.EnsureAllowed(
                CaptureHostProtocol.StartRecording,
                ManualCaptureState.Ready,
                ReplayCaptureState.Buffering);
        }
        catch (InvalidOperationException exception)
        {
            manualBlocked = exception.Message.Contains("Instant Replay off", StringComparison.Ordinal);
        }
        try
        {
            CaptureHostCommandGate.EnsureAllowed(
                CaptureHostProtocol.StartReplay,
                ManualCaptureState.Recording,
                ReplayCaptureState.Off);
        }
        catch (InvalidOperationException exception)
        {
            replayBlocked = exception.Message.Contains("Stop the manual game recording", StringComparison.Ordinal);
        }
        Assert(manualBlocked && replayBlocked,
            "The production capture-host command gate must reject manual/replay pipeline overlap in both directions.");

        var cameraRequestedSettings = CaptureSettings.Default with
        {
            IncludeReactionCamera = true,
            ReactionCameraConsentGranted = true,
            CameraDeviceId = "camera-probe"
        };
        var manualAfterReplayAttention =
            CaptureHostReactionCameraPolicy.PrepareForStart(
                cameraRequestedSettings,
                otherPipelineReleaseNeedsAttention: true,
                startCommandSequence: 31,
                latestOffCommandSequence: 0);
        var replayAfterManualAttention =
            CaptureHostReactionCameraPolicy.PrepareForStart(
                cameraRequestedSettings,
                otherPipelineReleaseNeedsAttention: true,
                startCommandSequence: 31,
                latestOffCommandSequence: 0);
        var ordinaryStart = CaptureHostReactionCameraPolicy.PrepareForStart(
            cameraRequestedSettings,
            otherPipelineReleaseNeedsAttention: false,
            startCommandSequence: 31,
            latestOffCommandSequence: 0);
        long latestOffSequence = 0;
        Assert(
            CaptureHostReactionCameraOrder.RecordOff(ref latestOffSequence, 42) == 42 &&
            CaptureHostReactionCameraOrder.RecordOff(ref latestOffSequence, 40) == 42,
            "Urgent Reaction Camera off must latch the newest causal sequence even when pipe dispatch order is inverted.");
        var delayedEarlierStart = CaptureHostReactionCameraPolicy.PrepareForStart(
            cameraRequestedSettings,
            otherPipelineReleaseNeedsAttention: false,
            startCommandSequence: 41,
            latestOffCommandSequence: latestOffSequence);
        var sameGenerationStart = CaptureHostReactionCameraPolicy.PrepareForStart(
            cameraRequestedSettings,
            otherPipelineReleaseNeedsAttention: false,
            startCommandSequence: 42,
            latestOffCommandSequence: latestOffSequence);
        var laterExplicitStart = CaptureHostReactionCameraPolicy.PrepareForStart(
            cameraRequestedSettings,
            otherPipelineReleaseNeedsAttention: false,
            startCommandSequence: 43,
            latestOffCommandSequence: latestOffSequence);
        var delayedGameplayStarted = false;
        var delayedCameraStarted = false;
        void ExecuteDelayedStart(CaptureSettings effectiveSettings)
        {
            delayedGameplayStarted = true;
            delayedCameraStarted = effectiveSettings.IncludeReactionCamera;
        }
        // Deliberately deliver the later urgent off first, then dispatch the earlier-issued Start.
        ExecuteDelayedStart(delayedEarlierStart);
        Assert(
            !manualAfterReplayAttention.IncludeReactionCamera &&
            !replayAfterManualAttention.IncludeReactionCamera &&
            ordinaryStart.IncludeReactionCamera &&
            !delayedEarlierStart.IncludeReactionCamera &&
            !sameGenerationStart.IncludeReactionCamera &&
            laterExplicitStart.IncludeReactionCamera &&
            delayedGameplayStarted &&
            !delayedCameraStarted &&
            manualAfterReplayAttention.ReactionCameraConsentGranted &&
            manualAfterReplayAttention.CameraDeviceId == "camera-probe",
            "A cross-pipeline release warning or later-dispatched off must suppress only the optional camera, while gameplay and a genuinely later camera-enabled start remain available.");

        const long replayResidentBytes = 31_337;
        var response = CaptureHostResponseFactory.Create(
            request,
            ok: false,
            hostProcessId: 77,
            ManualCaptureState.Ready,
            recorderTarget: null,
            recorderError: null,
            new ReplayCaptureStatus(
                ReplayCaptureState.Buffering,
                Target: null,
                LastError: null,
                TimeSpan.FromSeconds(8),
                replayResidentBytes,
                ReactionCameraActive: true,
                ReactionCameraError: "camera warning",
                ReactionCameraResidentBytes: 7_777,
                ReactionCameraStarting: true,
                ReactionCameraReleaseNeedsAttention: true),
            error: "probe",
            manualReactionCameraActive: false,
            manualReactionCameraError: null,
            manualReactionCameraStarting: true,
            manualReactionCameraReleaseNeedsAttention: true);
        var roundTrippedResponse = CaptureHostProtocol.Deserialize(
            CaptureHostProtocol.Serialize(response));
        var commandFailure = new CaptureHostCommandException("probe", roundTrippedResponse);
        Assert(
            roundTrippedResponse.ReplayResidentBytes == replayResidentBytes &&
            roundTrippedResponse.ReplayReactionCameraActive &&
            roundTrippedResponse.ManualReactionCameraStarting &&
            roundTrippedResponse.ReplayReactionCameraStarting &&
            roundTrippedResponse.ManualReactionCameraReleaseNeedsAttention &&
            roundTrippedResponse.ReplayReactionCameraReleaseNeedsAttention &&
            roundTrippedResponse.ReplayReactionCameraResidentBytes == 7_777 &&
            commandFailure.Snapshot.ReplayStatus is
            {
                ResidentBytes: replayResidentBytes,
                ReactionCameraActive: true,
                ReactionCameraError: "camera warning",
                ReactionCameraResidentBytes: 7_777,
                ReactionCameraStarting: true,
                ReactionCameraReleaseNeedsAttention: true
            } &&
            commandFailure.Snapshot.ReactionCameraStatus is
            {
                ManualCaptureActive: false,
                InstantReplayActive: true,
                LastError: "camera warning",
                ManualCaptureStarting: true,
                InstantReplayStarting: true,
                ReleaseNeedsAttention: true
            },
            "Capture-host IPC must preserve gameplay and authoritative Reaction Camera startup telemetry through the client snapshot.");

        var applicationPath = Path.Combine(AppContext.BaseDirectory, "ClipsToDiscord.exe");
        var malformedStart = new System.Diagnostics.ProcessStartInfo(applicationPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        malformedStart.ArgumentList.Add(CaptureHostLaunchOptions.HostArgument);
        using (var malformedProcess = System.Diagnostics.Process.Start(malformedStart) ??
               throw new InvalidOperationException("Could not launch the malformed host probe."))
        {
            Assert(
                malformedProcess.WaitForExit(5_000) && malformedProcess.ExitCode == 22,
                "Malformed capture-host mode must fail closed instead of opening a second ClipCord tray process.");
        }

        using var client = new CaptureHostClient(applicationPath);
        var first = client.EnsureReadyAsync().GetAwaiter().GetResult();
        var second = client.EnsureReadyAsync().GetAwaiter().GetResult();
        var recorderStatus = client.GetRecorderStatusAsync().GetAwaiter().GetResult();
        Assert(
            first.ProtocolVersion == CaptureHostProtocol.Version &&
            first.EngineState == "Ready" &&
            first.ProcessId != Environment.ProcessId &&
            second.ProcessId == first.ProcessId &&
            recorderStatus.State == ManualCaptureState.NoTarget &&
            recorderStatus.Target is null &&
            client.IsRunning,
            "Capture must start one isolated host process, reuse it, and expose its recorder state over IPC.");

        client.StopAsync().GetAwaiter().GetResult();
        Assert(!client.IsRunning,
            "A requested capture-host shutdown must release the child process and private pipe.");
        var restarted = client.EnsureReadyAsync().GetAwaiter().GetResult();
        Assert(
            restarted.ProcessId != first.ProcessId && client.IsRunning,
            "Capture must recover by starting a fresh host after an ordinary shutdown.");
        var cameraOffSnapshot = client.DisableReactionCameraAsync().GetAwaiter().GetResult();
        Assert(
            cameraOffSnapshot.ReactionCameraStatus is
            {
                ManualCaptureActive: false,
                InstantReplayActive: false,
                ManualCaptureStarting: false,
                InstantReplayStarting: false,
                ReleaseNeedsAttention: false
            } &&
            client.IsRunning,
            "The real isolated host must acknowledge camera-off over its urgent pipe without stopping gameplay capture.");

        client.StopAsync().GetAwaiter().GetResult();
        Assert(!client.IsRunning,
            "The real capture host must stop cleanly after the urgent acknowledgement probe.");
        AssertCaptureHostUrgentLossTerminatesProcess();
    }

    private static void AssertCaptureHostCallbackInversionSuppressesEarlierStart()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var token = Guid.NewGuid().ToString("N");
                using var context = new CaptureHostApplicationContext(
                    new CaptureHostLaunchOptions(
                        CaptureHostLaunchOptions.PipePrefix + "callback-" + token,
                        CaptureHostLaunchOptions.UrgentPipePrefix + "callback-" + token,
                        Environment.ProcessId));
                var contextType = typeof(CaptureHostApplicationContext);
                var dispatcher = contextType.GetField(
                        "_dispatcher",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(context) as Form ??
                    throw new InvalidOperationException(
                        "Could not inspect the capture-host UI dispatcher.");
                var latestOffField = contextType.GetField(
                        "_latestReactionCameraOffSequence",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic) ??
                    throw new InvalidOperationException(
                        "Could not inspect the capture-host camera-off generation.");
                var urgentHandler = contextType.GetMethod(
                        "HandleUrgentRequestAsync",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic) ??
                    throw new InvalidOperationException(
                        "Could not inspect the capture-host urgent command handler.");

                dispatcher.CreateControl();
                _ = dispatcher.Handle;
                const long earlierStartSequence = 101;
                const long laterOffSequence = 102;
                var cameraSettings = CaptureSettings.Default with
                {
                    IncludeReactionCamera = true,
                    ReactionCameraConsentGranted = true,
                    CameraDeviceId = "callback-inversion-camera"
                };
                var preparedStart = new TaskCompletionSource<CaptureSettings>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Task<CaptureHostMessage>? urgentResponseTask = null;
                var startRanBeforeUrgentCallback = false;

                Task.Run(() => dispatcher.BeginInvoke(new Action(() =>
                    {
                        startRanBeforeUrgentCallback = urgentResponseTask is { IsCompleted: false };
                        preparedStart.TrySetResult(context.PrepareReactionCameraForStart(
                            cameraSettings,
                            otherPipelineReleaseNeedsAttention: false,
                            earlierStartSequence));
                    })))
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();

                var urgentRequest = new CaptureHostMessage(
                    CaptureHostProtocol.Version,
                    CaptureHostProtocol.DisableReactionCamera,
                    0,
                    CommandSequence: laterOffSequence);
                urgentResponseTask = (Task<CaptureHostMessage>?)urgentHandler.Invoke(
                    context,
                    [urgentRequest]) ??
                    throw new InvalidOperationException(
                        "The capture-host urgent callback probe did not return a task.");

                // Do not pump the UI yet. Production must latch the later off synchronously on the
                // urgent pipe thread, even though the earlier Start callback is first in the queue.
                var latchedBeforeUiDrain =
                    (long)(latestOffField.GetValue(context) ?? 0L) == laterOffSequence;
                var callbackDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
                while ((!preparedStart.Task.IsCompleted || !urgentResponseTask.IsCompleted) &&
                       DateTimeOffset.UtcNow < callbackDeadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(1);
                }

                var effectiveStart = preparedStart.Task
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();
                var urgentResponse = urgentResponseTask
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();
                Assert(
                    latchedBeforeUiDrain &&
                    startRanBeforeUrgentCallback &&
                    !effectiveStart.IncludeReactionCamera &&
                    effectiveStart.ReactionCameraConsentGranted &&
                    effectiveStart.CameraDeviceId == "callback-inversion-camera" &&
                    urgentResponse.AppliedReactionCameraOffSequence == laterOffSequence,
                    "A later urgent Reaction Camera off must latch before UI dispatch so an earlier queued Start drains gameplay-only instead of reopening the camera.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert(
            thread.Join(TimeSpan.FromSeconds(10)),
            "The capture-host callback-inversion regression probe must finish within its bounded timeout.");
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The capture-host callback-inversion regression probe failed.",
                failure);
        }
    }

    private static void AssertCaptureHostCommandSequencePrecedesLifecycleGate()
    {
        var token = Guid.NewGuid().ToString("N");
        var mainPipeName = $"clipcord-sequence-main-{token}";
        var urgentPipeName = $"clipcord-sequence-urgent-{token}";
        using var mainServer = new NamedPipeServerStream(
            mainPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var urgentServer = new NamedPipeServerStream(
            urgentPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var mainHost = new NamedPipeClientStream(
            ".", mainPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var urgentHost = new NamedPipeClientStream(
            ".", urgentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task.WhenAll(
                mainServer.WaitForConnectionAsync(),
                urgentServer.WaitForConnectionAsync(),
                mainHost.ConnectAsync(),
                urgentHost.ConnectAsync())
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();

        using var hostProcess = StartLongRunningCaptureHostFixture();
        var mainChannel = new CaptureHostCommandChannel(mainServer, hostProcess.Id);
        var urgentChannel = new CaptureHostCommandChannel(urgentServer, hostProcess.Id);
        var client = new CaptureHostClient(Path.Combine(AppContext.BaseDirectory, "ClipsToDiscord.exe"));
        var clientType = typeof(CaptureHostClient);
        var processField = clientType.GetField(
                "_process",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the capture-host process field.");
        var mainChannelField = clientType.GetField(
                "_mainChannel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the capture-host main channel field.");
        var urgentChannelField = clientType.GetField(
                "_urgentChannel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the capture-host urgent channel field.");
        var commandSequenceField = clientType.GetField(
                "_commandSequence",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the capture-host command sequence.");
        var lifecycleGate = clientType.GetField(
                "_gate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(client) as SemaphoreSlim ??
            throw new InvalidOperationException("Could not inspect the capture-host lifecycle gate.");

        processField.SetValue(client, hostProcess);
        mainChannelField.SetValue(client, mainChannel);
        urgentChannelField.SetValue(client, urgentChannel);
        var mainStartRequest = new TaskCompletionSource<CaptureHostMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var urgentOffRequest = new TaskCompletionSource<CaptureHostMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mainHostTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(
                mainHost, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(
                mainHost, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            for (var index = 0; index < 2; index++)
            {
                var payload = await reader.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false) ??
                    throw new InvalidOperationException("The sequence fixture lost its main channel.");
                var request = CaptureHostProtocol.Deserialize(payload);
                if (index == 0 && request.Type != CaptureHostProtocol.Ping)
                {
                    throw new InvalidOperationException("The sequence fixture expected a readiness ping first.");
                }
                if (index == 1)
                {
                    if (request.Type != CaptureHostProtocol.StartRecording)
                    {
                        throw new InvalidOperationException("The sequence fixture expected the queued Start command second.");
                    }
                    mainStartRequest.TrySetResult(request);
                }
                var response = new CaptureHostMessage(
                    CaptureHostProtocol.Version,
                    CaptureHostProtocol.Response,
                    request.RequestId,
                    HostProcessId: hostProcess.Id,
                    RecorderState: ManualCaptureState.Ready,
                    ReplayState: ReplayCaptureState.Off);
                await writer.WriteLineAsync(CaptureHostProtocol.Serialize(response))
                    .ConfigureAwait(false);
            }
        });
        var urgentHostTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(
                urgentHost, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(
                urgentHost, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var payload = await reader.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false) ??
                throw new InvalidOperationException("The sequence fixture lost its urgent channel.");
            var request = CaptureHostProtocol.Deserialize(payload);
            urgentOffRequest.TrySetResult(request);
            var response = new CaptureHostMessage(
                CaptureHostProtocol.Version,
                CaptureHostProtocol.Response,
                request.RequestId,
                HostProcessId: hostProcess.Id,
                RecorderState: ManualCaptureState.Ready,
                ReplayState: ReplayCaptureState.Off,
                AppliedReactionCameraOffSequence: request.CommandSequence);
            await writer.WriteLineAsync(CaptureHostProtocol.Serialize(response)).ConfigureAwait(false);
        });

        var gateHeld = false;
        using var operationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task<CaptureHostRecorderSnapshot>? startTask = null;
        try
        {
            gateHeld = lifecycleGate.Wait(0);
            Assert(gateHeld,
                "The command-sequence fixture must hold the ordinary lifecycle gate.");
            var cameraSettings = CaptureSettings.Default with
            {
                IncludeReactionCamera = true,
                ReactionCameraConsentGranted = true,
                CameraDeviceId = "sequence-camera"
            };
            startTask = client.StartRecordingAsync(
                cameraSettings,
                operationCancellation.Token);
            var startSequencePublishedBeforeGate =
                (long)(commandSequenceField.GetValue(client) ?? 0L);
            var offSnapshot = client.DisableReactionCameraAsync(operationCancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();
            var bothSequencesPublishedWhileGateHeld =
                (long)(commandSequenceField.GetValue(client) ?? 0L);
            var offRequest = urgentOffRequest.Task
                .WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();

            lifecycleGate.Release();
            gateHeld = false;
            startTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var startRequest = mainStartRequest.Task
                .WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();
            var effectiveStart = CaptureHostReactionCameraPolicy.PrepareForStart(
                startRequest.Settings ?? throw new InvalidOperationException(
                    "The queued Start command did not preserve capture settings."),
                otherPipelineReleaseNeedsAttention: false,
                startRequest.CommandSequence,
                offRequest.CommandSequence);
            Task.WhenAll(mainHostTask, urgentHostTask)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();

            Assert(
                startSequencePublishedBeforeGate == 1 &&
                bothSequencesPublishedWhileGateHeld == 2 &&
                startRequest.CommandSequence > 0 &&
                startRequest.CommandSequence < offRequest.CommandSequence &&
                !effectiveStart.IncludeReactionCamera &&
                offSnapshot.ReactionCameraStatus is { IsActive: false },
                "CommandSequence must be assigned before either client gate so a later urgent off causally suppresses an earlier blocked Start.");
        }
        finally
        {
            if (gateHeld) lifecycleGate.Release();
            operationCancellation.Cancel();
            processField.SetValue(client, null);
            mainChannelField.SetValue(client, null);
            urgentChannelField.SetValue(client, null);
            client.Dispose();
            mainChannel.Dispose();
            urgentChannel.Dispose();
            TerminateCaptureHostFixture(hostProcess);
        }
    }

    private static void AssertCaptureHostRejectsStaleReactionCameraOffAcknowledgement()
    {
        var token = Guid.NewGuid().ToString("N");
        var urgentPipeName = $"clipcord-stale-ack-{token}";
        using var urgentServer = new NamedPipeServerStream(
            urgentPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var urgentHost = new NamedPipeClientStream(
            ".", urgentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task.WhenAll(urgentServer.WaitForConnectionAsync(), urgentHost.ConnectAsync())
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();

        using var liveHostProbe = StartLongRunningCaptureHostFixture();
        using var injectedProcess = System.Diagnostics.Process.GetProcessById(liveHostProbe.Id);
        var urgentChannel = new CaptureHostCommandChannel(urgentServer, liveHostProbe.Id);
        using var client = new CaptureHostClient(
            Path.Combine(AppContext.BaseDirectory, "ClipsToDiscord.exe"));
        var clientType = typeof(CaptureHostClient);
        var processField = clientType.GetField(
                "_process",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the stale-ack host process field.");
        var urgentChannelField = clientType.GetField(
                "_urgentChannel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the stale-ack urgent channel field.");
        processField.SetValue(client, injectedProcess);
        urgentChannelField.SetValue(client, urgentChannel);
        var hostTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(
                urgentHost, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(
                urgentHost, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var payload = await reader.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false) ??
                throw new InvalidOperationException("The stale-ack fixture lost its urgent channel.");
            var request = CaptureHostProtocol.Deserialize(payload);
            var staleResponse = new CaptureHostMessage(
                CaptureHostProtocol.Version,
                CaptureHostProtocol.Response,
                request.RequestId,
                HostProcessId: liveHostProbe.Id,
                RecorderState: ManualCaptureState.Ready,
                ReplayState: ReplayCaptureState.Off,
                AppliedReactionCameraOffSequence: request.CommandSequence - 1);
            await writer.WriteLineAsync(CaptureHostProtocol.Serialize(staleResponse))
                .ConfigureAwait(false);
        });

        var staleAcknowledgementRejected = false;
        var corruptChannelTerminatedHost = false;
        try
        {
            client.DisableReactionCameraAsync()
                .WaitAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidDataException exception)
        {
            staleAcknowledgementRejected = exception.Message.Contains(
                "did not acknowledge the latest Reaction Camera off request",
                StringComparison.Ordinal);
        }
        finally
        {
            hostTask.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            try
            {
                corruptChannelTerminatedHost = liveHostProbe.WaitForExit(2_000);
            }
            finally
            {
                TerminateCaptureHostFixture(liveHostProbe);
            }
        }
        Assert(
            staleAcknowledgementRejected && corruptChannelTerminatedHost,
            "CaptureHostClient must reject a stale AppliedReactionCameraOffSequence and terminate the corrupt-channel host instead of reporting an older camera-off generation as successful.");
    }

    private static void AssertCaptureHostUrgentLossTerminatesProcess()
    {
        var token = Guid.NewGuid().ToString("N");
        var mainPipeName = $"clipcord-fail-closed-main-{token}";
        var urgentPipeName = $"clipcord-fail-closed-urgent-{token}";
        using var mainServer = new NamedPipeServerStream(
            mainPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var urgentServer = new NamedPipeServerStream(
            urgentPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var mainHost = new NamedPipeClientStream(
            ".", mainPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var urgentHost = new NamedPipeClientStream(
            ".", urgentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task.WhenAll(
                mainServer.WaitForConnectionAsync(),
                urgentServer.WaitForConnectionAsync(),
                mainHost.ConnectAsync(),
                urgentHost.ConnectAsync())
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();

        using var liveHostProbe = StartLongRunningCaptureHostFixture();
        using var injectedProcess = System.Diagnostics.Process.GetProcessById(liveHostProbe.Id);
        var mainChannel = new CaptureHostCommandChannel(mainServer, liveHostProbe.Id);
        var urgentChannel = new CaptureHostCommandChannel(urgentServer, liveHostProbe.Id);
        using var client = new CaptureHostClient(
            Path.Combine(AppContext.BaseDirectory, "ClipsToDiscord.exe"));
        var clientType = typeof(CaptureHostClient);
        var processField = clientType.GetField(
                "_process",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the fail-closed host process field.");
        var mainChannelField = clientType.GetField(
                "_mainChannel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the fail-closed main channel field.");
        var urgentChannelField = clientType.GetField(
                "_urgentChannel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Could not inspect the fail-closed urgent channel field.");
        processField.SetValue(client, injectedProcess);
        mainChannelField.SetValue(client, mainChannel);
        urgentChannelField.SetValue(client, urgentChannel);
        var lifecycleGate = clientType.GetField(
                "_gate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(client) as SemaphoreSlim ??
            throw new InvalidOperationException("Could not inspect the fail-closed lifecycle gate.");

        var lifecycleGateHeld = lifecycleGate.Wait(0);
        Assert(lifecycleGateHeld,
            "The fail-closed fixture must hold the ordinary lifecycle gate.");
        var urgentLossFailedClosed = false;
        var urgentLossCommandCompleted = false;
        var liveHostActuallyTerminated = false;
        var urgentLossTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            urgentChannel.Dispose();
            try
            {
                client.DisableReactionCameraAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .GetAwaiter()
                    .GetResult();
                urgentLossCommandCompleted = true;
            }
            catch (InvalidOperationException exception)
            {
                urgentLossCommandCompleted = true;
                urgentLossFailedClosed = exception.Message.Contains(
                    "urgent Reaction Camera control channel",
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            if (lifecycleGateHeld) lifecycleGate.Release();
            try
            {
                liveHostActuallyTerminated = liveHostProbe.WaitForExit(2_000);
            }
            finally
            {
                // A terminateProcess regression must fail fast without orphaning this exact child.
                TerminateCaptureHostFixture(liveHostProbe);
            }
        }
        urgentLossTimer.Stop();
        Assert(
            urgentLossFailedClosed &&
            urgentLossCommandCompleted &&
            liveHostActuallyTerminated &&
            urgentLossTimer.Elapsed < TimeSpan.FromSeconds(3),
            "Urgent-channel loss must terminate the independently observed host process and throw within the bounded timeout instead of only clearing client state.");
    }

    private static System.Diagnostics.Process StartLongRunningCaptureHostFixture()
    {
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new System.Diagnostics.ProcessStartInfo(powerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        var process = System.Diagnostics.Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not launch the bounded capture-host fixture process.");
        Assert(!process.HasExited,
            "The bounded capture-host fixture process must remain alive during the IPC probe.");
        return process;
    }

    private static void TerminateCaptureHostFixture(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void AssertUrgentCaptureHostChannelBypassesBlockedMainCommand()
    {
        var token = Guid.NewGuid().ToString("N");
        var mainPipeName = $"clipcord-test-main-{token}";
        var urgentPipeName = $"clipcord-test-urgent-{token}";
        var mainServer = new NamedPipeServerStream(
            mainPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var urgentServer = new NamedPipeServerStream(
            urgentPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var mainHost = new NamedPipeClientStream(
            ".", mainPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var urgentHost = new NamedPipeClientStream(
            ".", urgentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task.WhenAll(
                mainServer.WaitForConnectionAsync(),
                urgentServer.WaitForConnectionAsync(),
                mainHost.ConnectAsync(),
                urgentHost.ConnectAsync())
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();

        using var mainChannel = new CaptureHostCommandChannel(
            mainServer,
            Environment.ProcessId);
        using var urgentChannel = new CaptureHostCommandChannel(
            urgentServer,
            Environment.ProcessId);
        var mainEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMain = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var mainHostTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(
                mainHost, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(
                mainHost, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var payload = await reader.ReadLineAsync().ConfigureAwait(false) ??
                throw new InvalidOperationException("The ordinary command probe disconnected.");
            var request = CaptureHostProtocol.Deserialize(payload);
            if (request.Type != CaptureHostProtocol.SaveReplay)
            {
                throw new InvalidOperationException("The ordinary command probe received the wrong command.");
            }
            mainEntered.TrySetResult();
            await releaseMain.Task.ConfigureAwait(false);
            var response = new CaptureHostMessage(
                CaptureHostProtocol.Version,
                CaptureHostProtocol.Response,
                request.RequestId,
                HostProcessId: Environment.ProcessId,
                RecorderState: ManualCaptureState.NoTarget,
                ReplayState: ReplayCaptureState.Buffering);
            await writer.WriteLineAsync(CaptureHostProtocol.Serialize(response)).ConfigureAwait(false);
        });

        var urgentHostTask = Task.Run(async () =>
        {
            using var reader = new StreamReader(
                urgentHost, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(
                urgentHost, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var payload = await reader.ReadLineAsync().ConfigureAwait(false) ??
                throw new InvalidOperationException("The urgent command probe disconnected.");
            var request = CaptureHostProtocol.Deserialize(payload);
            if (request.Type != CaptureHostProtocol.DisableReactionCamera)
            {
                throw new InvalidOperationException("The urgent command probe received the wrong command.");
            }
            var response = new CaptureHostMessage(
                CaptureHostProtocol.Version,
                CaptureHostProtocol.Response,
                request.RequestId,
                HostProcessId: Environment.ProcessId,
                RecorderState: ManualCaptureState.NoTarget,
                ReplayState: ReplayCaptureState.Saving,
                ManualReactionCameraActive: false,
                ReplayReactionCameraActive: false,
                AppliedReactionCameraOffSequence: request.CommandSequence);
            await writer.WriteLineAsync(CaptureHostProtocol.Serialize(response)).ConfigureAwait(false);
        });

        var blockedSave = mainChannel.SendAsync(
            new CaptureHostMessage(
                CaptureHostProtocol.Version,
                CaptureHostProtocol.SaveReplay,
                0,
                CommandSequence: 51),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert(
            mainEntered.Task.Wait(TimeSpan.FromSeconds(2)) && !blockedSave.IsCompleted,
            "The urgent-channel regression fixture must hold an ordinary replay save open.");

        var urgentResponse = urgentChannel.SendAsync(
                new CaptureHostMessage(
                    CaptureHostProtocol.Version,
                    CaptureHostProtocol.DisableReactionCamera,
                    0,
                    CommandSequence: 52),
                TimeSpan.FromSeconds(2),
                CancellationToken.None,
                honorCallerCancellationAfterAdmission: false)
            .WaitAsync(TimeSpan.FromSeconds(2))
            .GetAwaiter()
            .GetResult();
        var urgentSnapshot = CaptureHostMessageMapper.ToRecorderSnapshot(urgentResponse);
        Assert(
            !blockedSave.IsCompleted &&
            urgentResponse.AppliedReactionCameraOffSequence == 52 &&
            urgentSnapshot.ReplayStatus?.State == ReplayCaptureState.Saving &&
            urgentSnapshot.ReactionCameraStatus is
            {
                ManualCaptureActive: false,
                InstantReplayActive: false,
                ManualCaptureStarting: false,
                InstantReplayStarting: false,
                ReleaseNeedsAttention: false
            },
            "Reaction Camera off must complete over a separate channel and publish truthful status while an ordinary replay save is still blocked.");

        releaseMain.TrySetResult();
        blockedSave.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Task.WhenAll(mainHostTask, urgentHostTask)
            .WaitAsync(TimeSpan.FromSeconds(2))
            .GetAwaiter()
            .GetResult();
    }

    private static void AssertReplaySaveFailureCleanup(string testRoot)
    {
        var libraryRoot = Path.Combine(testRoot, "replay-save-failure-cleanup");
        var snapshot = new EncodedReplaySnapshot(
            [new EncodedReplaySegment(new byte[] { 1, 2, 3, 4 }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))],
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(2),
            4,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3));
        var failedAsRequested = false;
        try
        {
            ReplaySnapshotMaterializer.SaveAsync(
                    snapshot,
                    audioSession: null,
                    CaptureSettings.Default with
                    {
                        LibraryRoot = libraryRoot,
                        RecordGameAudio = false,
                        IncludeMicrophone = false,
                        IncludeVoiceChat = false
                    },
                    "Failure Probe",
                    CancellationToken.None,
                    ffmpegOverride: "test-ffmpeg.exe",
                    concatRunner: (_, _, _) => throw new InvalidOperationException("forced concat failure"))
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Equals("forced concat failure", StringComparison.Ordinal))
        {
            failedAsRequested = true;
        }

        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(libraryRoot);
        Assert(
            failedAsRequested &&
            Directory.Exists(stagingDirectory) &&
            !Directory.EnumerateFiles(stagingDirectory, "replay-save-*", SearchOption.TopDirectoryOnly).Any(),
            "A replay save that fails after staging begins must remove every segment, concat list, and partial output.");
    }

    private static void AssertReplayCameraPersistenceCannotBlockGameplay(string testRoot)
    {
        var libraryRoot = Path.Combine(testRoot, "replay-camera-persistence-timeout");
        var gameplay = new EncodedReplaySnapshot(
            [new EncodedReplaySegment(new byte[] { 1, 2, 3, 4 }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))],
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(2),
            4,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3));
        var camera = new EncodedReplaySnapshot(
            [new EncodedReplaySegment(new byte[] { 5, 6, 7, 8 }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))],
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(2),
            4,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3));
        var persistenceEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersistence = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = ReplaySnapshotMaterializer.SaveAsync(
                gameplay,
                audioSession: null,
                CaptureSettings.Default with
                {
                    LibraryRoot = libraryRoot,
                    RecordGameAudio = false,
                    IncludeMicrophone = false,
                    IncludeVoiceChat = false,
                    IncludeReactionCamera = true,
                    ReactionCameraConsentGranted = true,
                    CameraDeviceId = "camera-test-id",
                    CameraDevice = "Camera test"
                },
                "Persistence Probe",
                CancellationToken.None,
                ffmpegOverride: "test-ffmpeg.exe",
                concatRunner: (_, arguments, _) =>
                {
                    File.WriteAllBytes(arguments[^1], new byte[] { 9, 8, 7, 6 });
                    return Task.CompletedTask;
                },
                reactionCameraSnapshot: camera,
                reactionCameraPersistenceTimeout: TimeSpan.FromMilliseconds(250),
                reactionCameraPersistenceProbe: _ =>
                {
                    persistenceEntered.TrySetResult();
                    return releasePersistence.Task;
                })
            .WaitAsync(TimeSpan.FromSeconds(2))
            .GetAwaiter()
            .GetResult();

        Assert(
            persistenceEntered.Task.IsCompleted &&
            File.Exists(result.FilePath) &&
            result.ReactionCameraLayer is null &&
            result.ReactionCameraWarning?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true,
            "A never-completing optional camera persistence step must return a successful, durable gameplay result with a camera warning.");

        var secondProbeEntered = false;
        var second = ReplaySnapshotMaterializer.SaveAsync(
                gameplay,
                audioSession: null,
                CaptureSettings.Default with
                {
                    LibraryRoot = libraryRoot,
                    RecordGameAudio = false,
                    IncludeMicrophone = false,
                    IncludeVoiceChat = false,
                    IncludeReactionCamera = true,
                    ReactionCameraConsentGranted = true,
                    CameraDeviceId = "camera-test-id",
                    CameraDevice = "Camera test"
                },
                "Persistence Probe Second",
                CancellationToken.None,
                ffmpegOverride: "test-ffmpeg.exe",
                concatRunner: (_, arguments, _) =>
                {
                    File.WriteAllBytes(arguments[^1], new byte[] { 9, 8, 7, 6 });
                    return Task.CompletedTask;
                },
                reactionCameraSnapshot: camera,
                reactionCameraPersistenceTimeout: TimeSpan.FromMilliseconds(250),
                reactionCameraPersistenceProbe: _ =>
                {
                    secondProbeEntered = true;
                    return Task.CompletedTask;
                })
            .WaitAsync(TimeSpan.FromSeconds(2))
            .GetAwaiter()
            .GetResult();
        Assert(
            File.Exists(second.FilePath) &&
            second.ReactionCameraLayer is null &&
            !secondProbeEntered &&
            second.ReactionCameraWarning?.Contains("previous camera layer", StringComparison.OrdinalIgnoreCase) == true,
            "While one detached camera persistence job is pending, later gameplay saves must succeed without starting another camera persistence job.");

        releasePersistence.TrySetResult();
        Assert(
            SpinWait.SpinUntil(
                () => !ReactionCameraPersistenceGate.IsPending,
                TimeSpan.FromSeconds(2)) &&
            ReactionCameraPersistencePolicy.MaximumWait > OptionalCaptureShutdownPolicy.MaximumWait &&
            ReactionCameraPersistencePolicy.MaximumWait < TimeSpan.FromMinutes(2),
            "The camera persistence gate must clear only after the detached job actually completes, using a disk-sized budget below the IPC timeout.");

        var delayed = ReplaySnapshotMaterializer.SaveAsync(
                gameplay,
                audioSession: null,
                CaptureSettings.Default with
                {
                    LibraryRoot = libraryRoot,
                    RecordGameAudio = false,
                    IncludeMicrophone = false,
                    IncludeVoiceChat = false,
                    IncludeReactionCamera = true,
                    ReactionCameraConsentGranted = true,
                    CameraDeviceId = "camera-test-id",
                    CameraDevice = "Camera test"
                },
                "Persistence Probe Delayed",
                CancellationToken.None,
                ffmpegOverride: "test-ffmpeg.exe",
                concatRunner: (_, arguments, _) =>
                {
                    File.WriteAllBytes(arguments[^1], new byte[] { 9, 8, 7, 6 });
                    return Task.CompletedTask;
                },
                reactionCameraSnapshot: camera,
                reactionCameraPersistenceTimeout: TimeSpan.FromMilliseconds(500),
                reactionCameraPersistenceProbe: async token =>
                    await Task.Delay(100, token).ConfigureAwait(false))
            .WaitAsync(TimeSpan.FromSeconds(2))
            .GetAwaiter()
            .GetResult();
        Assert(
            File.Exists(delayed.FilePath) &&
            delayed.ReactionCameraLayer is not null &&
            File.Exists(delayed.ReactionCameraLayer.CameraLayerPath),
            "A delayed but in-budget camera persistence job must be retained rather than treated like the short device-release budget.");
    }

    private static void AssertGallerySourceProvenance(string testRoot)
    {
        var externalRoot = Directory.CreateDirectory(Path.Combine(testRoot, "external-gallery")).FullName;
        var externalGame = Directory.CreateDirectory(Path.Combine(externalRoot, "local-only", "Valorant")).FullName;
        File.WriteAllBytes(Path.Combine(externalGame, "external.mp4"), [1]);

        var captureRoot = Directory.CreateDirectory(Path.Combine(testRoot, "capture-gallery")).FullName;
        var captureGame = Directory.CreateDirectory(Path.Combine(
            captureRoot,
            CaptureLibraryLayout.LibraryFolderName,
            CaptureLibraryLayout.GameFolderName,
            "Valorant")).FullName;
        var projectGameplay = Path.Combine(captureGame, "Valorant__2026-08-22__14-35-41.mp4");
        var gameplayOnly = Path.Combine(captureGame, "Valorant__2026-08-22__14-36-41.mp4");
        var corruptProjectGameplay = Path.Combine(captureGame, "Valorant__2026-08-22__14-37-41.mp4");
        var oversizedProjectGameplay = Path.Combine(captureGame, "Valorant__2026-08-22__14-38-41.mp4");
        File.WriteAllBytes(projectGameplay, [2]);
        File.WriteAllBytes(gameplayOnly, [3]);
        File.WriteAllBytes(corruptProjectGameplay, [7]);
        File.WriteAllBytes(oversizedProjectGameplay, [8]);
        var staging = Directory.CreateDirectory(
            CaptureLibraryLayout.GetStagingDirectory(captureRoot)).FullName;
        var cameraStage = Path.Combine(staging, "gallery-camera.mp4");
        File.WriteAllBytes(cameraStage, [4, 5, 6]);
        var galleryComposition = CaptureCompositionSnapshotFactory.Create(
            CaptureSettings.Default with { LibraryRoot = captureRoot },
            projectGameplay,
            mirrorCamera: true,
            DateTimeOffset.UtcNow);
        var savedProject = CaptureProjectStore.SaveReactionCameraLayerAsync(
                captureRoot,
                projectGameplay,
                cameraStage,
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(40.125),
                TimeSpan.FromSeconds(8),
                mirrorCamera: true,
                galleryComposition)
            .GetAwaiter()
            .GetResult();

        var corruptProjectId = CaptureProjectStore.CreateProjectId(
            captureRoot,
            corruptProjectGameplay);
        var corruptProjectDirectory = Directory.CreateDirectory(
            CaptureProjectStore.GetProjectDirectory(captureRoot, corruptProjectId));
        File.WriteAllBytes(
            Path.Combine(corruptProjectDirectory.FullName, CaptureProjectStore.CameraLayerFileName),
            [9]);
        File.WriteAllText(
            Path.Combine(corruptProjectDirectory.FullName, CaptureProjectStore.ManifestFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = CaptureProjectStore.CurrentSchemaVersion,
                projectId = corruptProjectId,
                gameplayRelativePath = Path.GetRelativePath(captureRoot, corruptProjectGameplay)
                    .Replace('\\', '/'),
                cameraLayerRelativePath = (string?)null,
                cameraStartOffsetTicks = 0,
                durationTicks = TimeSpan.FromSeconds(4).Ticks,
                mirrorCamera = false,
                createdUtc = DateTimeOffset.UtcNow
            }));

        var oversizedProjectId = CaptureProjectStore.CreateProjectId(
            captureRoot,
            oversizedProjectGameplay);
        var oversizedProjectDirectory = Directory.CreateDirectory(
            CaptureProjectStore.GetProjectDirectory(captureRoot, oversizedProjectId));
        File.WriteAllBytes(
            Path.Combine(oversizedProjectDirectory.FullName, CaptureProjectStore.CameraLayerFileName),
            [10]);
        File.WriteAllText(
            Path.Combine(oversizedProjectDirectory.FullName, CaptureProjectStore.ManifestFileName),
            new string('x', CaptureProjectStore.MaximumManifestBytes + 1));

        var snapshot = GalleryCatalog.Scan(
            externalRoot,
            CancellationToken.None,
            captureLibraryRoot: captureRoot,
            externalSource: GalleryClipSource.Nvidia);
        var clips = snapshot.Games.Single(game => game.Name == "Valorant").Clips;
        var composedSource = clips.Single(clip => clip.Path == projectGameplay);
        var gameplayOnlySource = clips.Single(clip => clip.Path == gameplayOnly);
        var corruptProjectSource = clips.Single(clip => clip.Path == corruptProjectGameplay);
        var oversizedProjectSource = clips.Single(clip => clip.Path == oversizedProjectGameplay);
        Assert(
            clips.Count == 5 &&
            clips.Any(clip => clip.Source == GalleryClipSource.Nvidia && clip.SourceLabel == "NVIDIA") &&
            clips.Any(clip => clip.Source == GalleryClipSource.ClipCord && clip.SourceLabel == "ClipCord") &&
            clips.All(clip => clip.Route == GalleryClipRoute.LocalOnly) &&
            composedSource.HasReactionCameraProject &&
            composedSource.CaptureProject is
            {
                MirrorCamera: true,
                CameraStartOffset: var cameraOffset,
                Duration: var cameraDuration
            } &&
            composedSource.CaptureProject.ProjectId == savedProject.ProjectId &&
            composedSource.CaptureProject.GameplayPath == projectGameplay &&
            composedSource.CaptureProject.CameraLayerPath == savedProject.CameraLayerPath &&
            cameraOffset == TimeSpan.FromMilliseconds(125) &&
            cameraDuration == TimeSpan.FromSeconds(8) &&
            !gameplayOnlySource.HasReactionCameraProject &&
            !corruptProjectSource.HasReactionCameraProject &&
            !oversizedProjectSource.HasReactionCameraProject,
            "Gallery must preserve source provenance, join only a validated camera project to its exact ClipCord gameplay clip, and degrade corrupt or oversized manifests without aborting the scan.");

        var withoutExternalFolder = GalleryCatalog.Scan(
            Path.Combine(testRoot, "missing-external-gallery"),
            CancellationToken.None,
            captureLibraryRoot: captureRoot,
            externalSource: GalleryClipSource.Nvidia);
        var clipCordOnly = withoutExternalFolder.Games.Single(game => game.Name == "Valorant").Clips;
        Assert(
            clipCordOnly.Count == 4 &&
            clipCordOnly.All(clip => clip.Source == GalleryClipSource.ClipCord) &&
            clipCordOnly.All(clip => clip.SourceLabel == "ClipCord") &&
            clipCordOnly.Count(clip => clip.HasReactionCameraProject) == 1 &&
            withoutExternalFolder.Warnings.Contains("The external clips folder is not available."),
            "Gallery must retain ClipCord recordings and provenance when the external watcher folder is unavailable.");

        var probedCaptureRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "capture-gallery-chain-probe")).FullName;
        var probedLibrary = Directory.CreateDirectory(Path.Combine(
            probedCaptureRoot,
            CaptureLibraryLayout.LibraryFolderName,
            CaptureLibraryLayout.GameFolderName)).FullName;
        var redirectedAncestor = Path.Combine(
            probedCaptureRoot,
            CaptureLibraryLayout.LibraryFolderName);
        var inspectedDirectories = new List<string>();
        var syntheticRedirectRejected = !GalleryCatalog.HasOrdinaryCaptureLibraryChain(
            probedCaptureRoot,
            probedLibrary,
            path =>
            {
                inspectedDirectories.Add(path);
                return !path.Equals(redirectedAncestor, StringComparison.OrdinalIgnoreCase);
            });
        Assert(
            syntheticRedirectRejected &&
            inspectedDirectories.Contains(probedCaptureRoot, StringComparer.OrdinalIgnoreCase) &&
            inspectedDirectories.Contains(redirectedAncestor, StringComparer.OrdinalIgnoreCase) &&
            !inspectedDirectories.Contains(probedLibrary, StringComparer.OrdinalIgnoreCase),
            "ClipCord Gallery containment must inspect each ancestor and stop before traversing a redirected Library folder.");

        var linkedCaptureRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "capture-gallery-linked")).FullName;
        var linkTarget = Directory.CreateDirectory(
            Path.Combine(testRoot, "capture-gallery-link-target")).FullName;
        var escapedGame = Directory.CreateDirectory(Path.Combine(
            linkTarget,
            CaptureLibraryLayout.GameFolderName,
            "Outside Game")).FullName;
        File.WriteAllBytes(Path.Combine(escapedGame, "outside.mp4"), [11]);
        var libraryLink = Path.Combine(linkedCaptureRoot, CaptureLibraryLayout.LibraryFolderName);
        try
        {
            Directory.CreateSymbolicLink(libraryLink, linkTarget);
            var linkedSnapshot = GalleryCatalog.Scan(
                externalRoot,
                CancellationToken.None,
                captureLibraryRoot: linkedCaptureRoot,
                externalSource: GalleryClipSource.Nvidia);
            Assert(
                !GalleryCatalog.HasOrdinaryCaptureLibraryChain(
                    linkedCaptureRoot,
                    Path.Combine(libraryLink, CaptureLibraryLayout.GameFolderName)) &&
                linkedSnapshot.Games.All(game => game.Name != "Outside Game") &&
                linkedSnapshot.Warnings.Contains(
                    "The ClipCord Capture library cannot be read through a symbolic link or junction."),
                "Gallery must reject a ClipCord Library or Game ancestor that redirects through a symbolic link or junction.");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or NotSupportedException or
                System.Security.SecurityException)
        {
            Console.WriteLine(
                "  (skipped the ClipCord Gallery symlink check: this Windows environment cannot create a test link)");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool HasPair(IReadOnlyList<string> values, string key, string value)
    {
        for (var index = 0; index < values.Count - 1; index++)
        {
            if (values[index] == key && values[index + 1] == value) return true;
        }
        return false;
    }

    private sealed class ScriptedReplayController : IReplayCaptureController
    {
        private readonly Queue<(string? Error, ReplayCaptureStatus Status)> _outcomes;

        internal ScriptedReplayController(
            params (string? Error, ReplayCaptureStatus Status)[] outcomes)
        {
            _outcomes = new Queue<(string? Error, ReplayCaptureStatus Status)>(outcomes);
            ReplayStatus = new ReplayCaptureStatus(
                ReplayCaptureState.Off,
                null,
                null,
                TimeSpan.Zero,
                0);
        }

        public ReplayCaptureStatus ReplayStatus { get; private set; }
        public int StartCount { get; private set; }
        public event EventHandler? ReplayStateChanged;

        public Task StartReplayAsync(
            CaptureSettings settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            if (_outcomes.Count == 0)
            {
                throw new InvalidOperationException("The scripted replay controller ran out of outcomes.");
            }
            var outcome = _outcomes.Dequeue();
            ReplayStatus = outcome.Status;
            ReplayStateChanged?.Invoke(this, EventArgs.Empty);
            if (outcome.Error is null) return Task.CompletedTask;
            throw new CaptureHostCommandException(
                outcome.Error,
                new CaptureHostMessage(
                    CaptureHostProtocol.Version,
                    CaptureHostProtocol.Response,
                    StartCount,
                    Ok: false,
                    Error: outcome.Error,
                    ReplayState: outcome.Status.State,
                    ReplayTarget: outcome.Status.Target,
                    ReplayError: outcome.Status.LastError,
                    ReplayBufferedMilliseconds: (long)outcome.Status.BufferedDuration.TotalMilliseconds,
                    ReplayResidentBytes: outcome.Status.ResidentBytes));
        }

        public Task StopReplayAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayStatus = ReplayStatus with { State = ReplayCaptureState.Off };
            ReplayStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<ManualCaptureResult?> SaveReplayAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ManualCaptureResult?>(null);
        }
    }

    private sealed class LostWakeReplayController : IReplayCaptureController
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
        private int _maximumConcurrentStarts;

        internal TaskCompletionSource FirstStartEntered => _firstStartEntered;
        internal int StartCount => Volatile.Read(ref _startCount);
        internal int MaximumConcurrentStarts => Volatile.Read(ref _maximumConcurrentStarts);
        public ReplayCaptureStatus ReplayStatus => Volatile.Read(ref _status);
        public event EventHandler? ReplayStateChanged;

        internal void ReleaseFirstStart() => _releaseFirstStart.TrySetResult();

        public async Task StartReplayAsync(
            CaptureSettings settings,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _startCount);
            var active = Interlocked.Increment(ref _activeStarts);
            var observedMaximum = Volatile.Read(ref _maximumConcurrentStarts);
            while (active > observedMaximum)
            {
                var prior = Interlocked.CompareExchange(
                    ref _maximumConcurrentStarts,
                    active,
                    observedMaximum);
                if (prior == observedMaximum) break;
                observedMaximum = prior;
            }
            try
            {
                if (call == 1)
                {
                    _firstStartEntered.TrySetResult();
                    await _releaseFirstStart.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                Volatile.Write(ref _status, ReplayStatus with
                {
                    State = ReplayCaptureState.Buffering,
                    Target = new ManualCaptureTarget("Duskfade", 2560, 1440)
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

    private sealed class FakeReplayTargetUnavailableSource : IReplayTargetUnavailableSource
    {
        private Action<GameWindowCandidate>? _targetUnavailable;

        internal int SubscriberCount => _targetUnavailable?.GetInvocationList().Length ?? 0;

        public event Action<GameWindowCandidate>? TargetUnavailable
        {
            add => _targetUnavailable += value;
            remove => _targetUnavailable -= value;
        }

        internal void Raise(GameWindowCandidate candidate) => _targetUnavailable?.Invoke(candidate);
    }

    private sealed class FakeReplayTargetLifetimeSignal : IReplayTargetLifetimeSignal
    {
        private Action? _unavailable;

        internal int StartCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public void Start(Action unavailable)
        {
            ArgumentNullException.ThrowIfNull(unavailable);
            StartCount++;
            _unavailable = unavailable;
        }

        internal void Raise() => _unavailable?.Invoke();

        public void Dispose()
        {
            DisposeCount++;
            _unavailable = null;
        }
    }
}
