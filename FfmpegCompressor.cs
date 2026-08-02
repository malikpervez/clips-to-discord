using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

internal static partial class FfmpegCompressor
{
    public static string? FindExecutable()
    {
        var besideApp = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(besideApp)) return besideApp;

        var toolsFolder = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (File.Exists(toolsFolder)) return toolsFolder;

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static async Task<string> CompressAsync(
        string inputPath,
        string ffmpegPath,
        int targetMegabytes,
        CancellationToken cancellationToken)
    {
        var probe = await RunAsync(
            ffmpegPath,
            ["-hide_banner", "-i", inputPath],
            cancellationToken,
            allowFailure: true);
        var match = DurationPattern().Match(probe.StandardError);
        if (!match.Success)
        {
            throw new InvalidOperationException("FFmpeg could not determine the clip duration.");
        }

        var duration = TimeSpan.FromHours(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) +
                       TimeSpan.FromMinutes(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) +
                       TimeSpan.FromSeconds(double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        if (duration.TotalSeconds <= 0)
        {
            throw new InvalidOperationException("The clip duration is invalid.");
        }

        if (targetMegabytes is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMegabytes));
        }

        var targetBytes = (long)targetMegabytes * 1024 * 1024;
        var totalKbps = Math.Floor((targetBytes * 8d / duration.TotalSeconds / 1000d) * 0.94d);
        var audioKbps = totalKbps >= 500 ? 96 : 64;
        var videoKbps = Math.Max(180, Math.Min(6000, totalKbps - audioKbps));

        var temporaryFolder = Path.Combine(Path.GetTempPath(), "ClipsToDiscord");
        Directory.CreateDirectory(temporaryFolder);
        var token = Guid.NewGuid().ToString("N");
        var outputPath = Path.Combine(temporaryFolder, token + ".mp4");
        var passLog = Path.Combine(temporaryFolder, token);
        var videoRate = $"{Math.Floor(videoKbps).ToString(CultureInfo.InvariantCulture)}k";

        try
        {
            await RunAsync(ffmpegPath,
            [
                "-y", "-i", inputPath,
                "-vf", "scale=min(1280\\,iw):-2",
                "-c:v", "libx264", "-preset", "veryfast", "-b:v", videoRate,
                "-pass", "1", "-passlogfile", passLog,
                "-an", "-f", "mp4", "NUL"
            ], cancellationToken);

            await RunAsync(ffmpegPath,
            [
                "-y", "-i", inputPath,
                "-vf", "scale=min(1280\\,iw):-2",
                "-c:v", "libx264", "-preset", "veryfast", "-b:v", videoRate,
                "-pass", "2", "-passlogfile", passLog,
                "-c:a", "aac", "-b:a", $"{audioKbps}k",
                "-pix_fmt", "yuv420p", "-movflags", "+faststart", outputPath
            ], cancellationToken);

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("FFmpeg did not create a compressed clip.");
            }

            return outputPath;
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(temporaryFolder, token + "*"))
            {
                if (!path.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(path);
                }
            }
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var result = new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
        if (!allowFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {result.ExitCode}. {LastUsefulLine(result.StandardError)}");
        }

        return result;
    }

    private static string LastUsefulLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
