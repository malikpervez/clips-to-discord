using ClipsToDiscord;

internal static class CaptureFoundationTests
{
    public static void Run(string testRoot)
    {
        AssertCapabilityProbeShape();
        AssertCaptureProfiles();
        AssertCaptureSettings();
        AssertLibraryLayout(testRoot);
        AssertOutputPolicy(testRoot);
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
        File.WriteAllBytes(Path.Combine(captureGame, "Valorant__2026-08-22__14-35-41.mp4"), [2]);

        var snapshot = GalleryCatalog.Scan(
            externalRoot,
            CancellationToken.None,
            captureLibraryRoot: captureRoot,
            externalSource: GalleryClipSource.Nvidia);
        var clips = snapshot.Games.Single(game => game.Name == "Valorant").Clips;
        Assert(
            clips.Count == 2 &&
            clips.Any(clip => clip.Source == GalleryClipSource.Nvidia && clip.SourceLabel == "NVIDIA") &&
            clips.Any(clip => clip.Source == GalleryClipSource.ClipCord && clip.SourceLabel == "ClipCord") &&
            clips.All(clip => clip.Route == GalleryClipRoute.LocalOnly),
            "Gallery must unify external and ClipCord recordings while preserving their source provenance.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
