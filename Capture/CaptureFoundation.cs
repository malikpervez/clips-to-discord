using Windows.Graphics.Capture;

namespace ClipsToDiscord;

internal enum CaptureReadiness
{
    Ready = 0,
    UnsupportedWindowsVersion = 1,
    GraphicsCaptureUnavailable = 2,
    ProbeFailed = 3
}

internal sealed record CaptureCapabilitySnapshot(
    CaptureReadiness Readiness,
    bool OperatingSystemSupported,
    bool GraphicsCaptureSupported)
{
    public bool CanStartCaptureSpike => Readiness == CaptureReadiness.Ready;
}

internal interface ICaptureCapabilityProbe
{
    CaptureCapabilitySnapshot Inspect();
}

/// <summary>
/// Performs the smallest safe runtime check needed before the 2.0 capture spike touches
/// Windows.Graphics.Capture. Hardware encoder, audio, and camera capabilities remain separate
/// probes so that an unavailable optional device never disables gameplay capture.
/// </summary>
internal sealed class WindowsCaptureCapabilityProbe : ICaptureCapabilityProbe
{
    internal const int MinimumWindowsBuild = 18362;

    private readonly Func<bool> _isOperatingSystemSupported;
    private readonly Func<bool> _isGraphicsCaptureSupported;

    public WindowsCaptureCapabilityProbe()
        : this(
            () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild),
            QueryGraphicsCaptureSupport)
    {
    }

    internal WindowsCaptureCapabilityProbe(
        Func<bool> isOperatingSystemSupported,
        Func<bool> isGraphicsCaptureSupported)
    {
        _isOperatingSystemSupported = isOperatingSystemSupported ??
            throw new ArgumentNullException(nameof(isOperatingSystemSupported));
        _isGraphicsCaptureSupported = isGraphicsCaptureSupported ??
            throw new ArgumentNullException(nameof(isGraphicsCaptureSupported));
    }

    public CaptureCapabilitySnapshot Inspect()
    {
        if (!_isOperatingSystemSupported())
        {
            return new CaptureCapabilitySnapshot(
                CaptureReadiness.UnsupportedWindowsVersion,
                OperatingSystemSupported: false,
                GraphicsCaptureSupported: false);
        }

        try
        {
            var graphicsCaptureSupported = _isGraphicsCaptureSupported();
            return new CaptureCapabilitySnapshot(
                graphicsCaptureSupported
                    ? CaptureReadiness.Ready
                    : CaptureReadiness.GraphicsCaptureUnavailable,
                OperatingSystemSupported: true,
                graphicsCaptureSupported);
        }
        catch
        {
            // Capability errors are intentionally reduced to a stable state. Exception text can
            // contain driver or machine details and should not become user-facing diagnostics.
            return new CaptureCapabilitySnapshot(
                CaptureReadiness.ProbeFailed,
                OperatingSystemSupported: true,
                GraphicsCaptureSupported: false);
        }
    }

    private static bool QueryGraphicsCaptureSupport()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild)) return false;
        return GraphicsCaptureSession.IsSupported();
    }
}

/// <summary>
/// Names completed ClipCord recordings so the existing game inference, Gallery, routing, and
/// uploader pipeline can consume them without a second catalog or migration path.
/// </summary>
internal static class CaptureOutputPolicy
{
    internal static string CreateFileName(string? gameName, DateTimeOffset capturedAt)
    {
        var safeGameName = UploadedFolder.SanitizeGameFolderName(gameName);
        var localTimestamp = capturedAt.ToLocalTime();
        return $"{safeGameName}__{localTimestamp:yyyy-MM-dd}__{localTimestamp:HH-mm-ss}.mp4";
    }

    internal static string CreateAvailablePath(
        string outputDirectory,
        string? gameName,
        DateTimeOffset capturedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("The recording output folder does not exist.");
        }

        var directory = new DirectoryInfo(outputDirectory);
        if (directory.LinkTarget is not null)
        {
            throw new IOException("The recording output folder cannot be a symbolic link or junction.");
        }

        var fileName = CreateFileName(gameName, capturedAt);
        var candidate = Path.Combine(directory.FullName, fileName);
        if (!File.Exists(candidate)) return candidate;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var collision = 1; collision <= 999; collision++)
        {
            candidate = Path.Combine(directory.FullName, $"{baseName}.{collision:D3}.mp4");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new IOException("ClipCord could not reserve a unique recording file name.");
    }
}
