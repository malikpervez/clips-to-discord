using ClipsToDiscord;

internal static class CaptureFoundationTests
{
    public static void Run(string testRoot)
    {
        AssertCapabilityProbeShape();
        AssertOutputPolicy(testRoot);
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
