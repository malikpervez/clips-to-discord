using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

internal static partial class FfmpegCompressor
{
    private static string? _cachedExecutablePath;

    public static string? FindExecutable()
    {
        if (_cachedExecutablePath is not null) return _cachedExecutablePath;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appBase = Path.GetFullPath(AppContext.BaseDirectory);
        candidates.Add(appBase);
        candidates.Add(Path.Combine(appBase, "tools"));

        var cursor = Directory.GetParent(appBase);
        while (cursor is not null)
        {
            candidates.Add(cursor.FullName);
            candidates.Add(Path.Combine(cursor.FullName, "tools"));
            candidates.Add(Path.Combine(cursor.FullName, "artifacts", "tools"));
            cursor = cursor.Parent;
            if (candidates.Count > 128) break;
        }

        foreach (var directory in candidates)
        {
            var path = Path.Combine(directory, "ffmpeg.exe");
            if (File.Exists(path))
            {
                _cachedExecutablePath = path;
                return path;
            }

            path = Path.Combine(directory, "tools", "ffmpeg.exe");
            if (File.Exists(path))
            {
                _cachedExecutablePath = path;
                return path;
            }

            var artifactsTools = Path.Combine(directory, "artifacts", "tools");
            if (Directory.Exists(artifactsTools))
            {
                foreach (var candidate in Directory.EnumerateDirectories(artifactsTools))
                {
                    path = Path.Combine(candidate, "ffmpeg.exe");
                    if (File.Exists(path))
                    {
                        _cachedExecutablePath = path;
                        return path;
                    }
                }

                path = Path.Combine(artifactsTools, "ffmpeg.exe");
                if (File.Exists(path))
                {
                    _cachedExecutablePath = path;
                    return path;
                }
            }
        }

        return null;
    }

    public static async Task<string> CompressAsync(
        string inputPath,
        string ffmpegPath,
        int targetMegabytes,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "FFmpeg two-pass compression uses the Windows NUL device and is only supported on Windows.");
        }

        var duration = await ProbeDurationAsync(inputPath, ffmpegPath, cancellationToken);
        return await CompressAsync(
            inputPath,
            ffmpegPath,
            targetMegabytes,
            duration,
            cancellationToken);
    }

    internal static async Task<TimeSpan> ProbeDurationAsync(
        string inputPath,
        string ffmpegPath,
        CancellationToken cancellationToken) =>
        (await ProbeMediaAsync(inputPath, ffmpegPath, cancellationToken)).Duration;

    /// <summary>
    /// Reads the duration and the audio stream count from a single FFmpeg inspection.
    /// The stream count matters because a recording can carry the microphone on its own
    /// audio stream next to game and chat audio, and anything that keeps only the first
    /// stream silently discards the rest.
    /// </summary>
    internal static async Task<MediaProbe> ProbeMediaAsync(
        string inputPath,
        string ffmpegPath,
        CancellationToken cancellationToken)
    {
        var probe = await RunAsync(
            ffmpegPath,
            ["-hide_banner", "-i", inputPath],
            cancellationToken,
            allowFailure: true);
        return ParseProbe(probe.StandardError);
    }

    internal static MediaProbe ParseProbe(string ffmpegOutput)
    {
        var match = DurationPattern().Match(ffmpegOutput ?? string.Empty);
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

        return new MediaProbe(duration, AudioStreamPattern().Matches(ffmpegOutput!).Count);
    }

    /// <summary>
    /// The output label of the mixed audio track built by <see cref="BuildAudioMixFilter"/>.
    /// </summary>
    internal const string MixedAudioLabel = "[clipcordmix]";

    /// <summary>
    /// Sums every audio stream of the input into one track. Discord clips and NVIDIA
    /// recordings keep the microphone on a separate stream from game and chat audio, and
    /// both Discord's player and a single-stream MP4 only ever surface one track, so the
    /// tracks are mixed rather than carried through separately.
    /// </summary>
    internal static string BuildAudioMixFilter(int audioStreamCount, params string[] additionalFilters)
    {
        if (audioStreamCount < 2) throw new ArgumentOutOfRangeException(nameof(audioStreamCount));
        var inputs = string.Concat(
            Enumerable.Range(0, audioStreamCount)
                .Select(index => string.Create(CultureInfo.InvariantCulture, $"[0:a:{index}]")));
        // normalize=0 keeps each track at its recorded level instead of dividing every
        // track by the stream count, which would quieten a clip that was already correct.
        // The sum can then exceed full scale, so it is limited rather than hard-clipped;
        // level=disabled stops the limiter from also raising quiet clips to 0 dBFS.
        var chain = string.Create(
            CultureInfo.InvariantCulture,
            $"{inputs}amix=inputs={audioStreamCount}:duration=longest:normalize=0," +
            $"alimiter=limit=0.95:level=disabled");
        foreach (var filter in additionalFilters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filter);
            chain += "," + filter;
        }
        return chain + MixedAudioLabel;
    }

    /// <summary>
    /// Builds one pass of the two-pass compression. Both passes are produced here so they
    /// cannot drift apart: a 10-bit HDR source (NVIDIA records HEVC Main 10) analysed in
    /// 10-bit but encoded in 8-bit leaves libx264 unable to open against the mismatched
    /// two-pass statistics, which fails the whole upload.
    /// </summary>
    internal static IReadOnlyList<string> BuildCompressionArguments(
        int pass,
        string inputPath,
        string passLogPath,
        string videoRate,
        int audioKbps,
        string outputPath,
        int audioStreamCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(passLogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoRate);
        if (pass is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(pass));
        if (audioStreamCount < 0) throw new ArgumentOutOfRangeException(nameof(audioStreamCount));

        var arguments = new List<string>
        {
            "-y", "-i", inputPath,
            // setparams rewrites the frame metadata itself. The -color_* output options
            // alone only reach the matrix, because libx264 signals primaries and transfer
            // from the incoming frames.
            "-vf", "scale=min(1280\\,iw):-2,setparams=" +
                   "color_primaries=bt709:color_trc=bt709:colorspace=bt709",
            "-c:v", "libx264", "-preset", "veryfast", "-b:v", videoRate,
            "-pass", pass.ToString(CultureInfo.InvariantCulture), "-passlogfile", passLogPath,
            // Both passes must request the same pixel format for the statistics to match.
            "-pix_fmt", "yuv420p",
            // The encode is 8-bit SDR, so label it that way. A 10-bit HDR source (NVIDIA
            // records HEVC Main 10 with PQ/BT.2020) would otherwise leave its HDR tags on
            // SDR pixels, and players would tone-map footage that was already converted.
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"
        };
        if (pass == 1)
        {
            arguments.AddRange(["-an", "-f", "mp4", "NUL"]);
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            if (audioStreamCount > 1)
            {
                // Explicit maps disable FFmpeg's default stream selection, which would
                // otherwise keep a single audio stream and drop the microphone track.
                arguments.AddRange([
                    "-filter_complex", BuildAudioMixFilter(audioStreamCount),
                    "-map", "0:v:0", "-map", MixedAudioLabel
                ]);
            }
            arguments.AddRange([
                "-c:a", "aac", "-b:a", $"{audioKbps.ToString(CultureInfo.InvariantCulture)}k",
                "-movflags", "+faststart", outputPath
            ]);
        }
        return arguments;
    }

    internal static async Task<string> CompressAsync(
        string inputPath,
        string ffmpegPath,
        int targetMegabytes,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "FFmpeg two-pass compression uses the Windows NUL device and is only supported on Windows.");
        }

        if (targetMegabytes is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMegabytes));
        }

        if (!CompressionTargetPlanner.TryCreateBitrates(duration, targetMegabytes, out var bitrates))
        {
            throw new CompressionTargetUnachievableException(
                $"A {targetMegabytes} MB target cannot preserve the minimum video bitrate for this {duration.TotalMinutes:F1}-minute clip.");
        }

        var temporaryFolder = Path.Combine(Path.GetTempPath(), "ClipsToDiscord");
        Directory.CreateDirectory(temporaryFolder);
        var token = Guid.NewGuid().ToString("N");
        var outputPath = Path.Combine(temporaryFolder, token + ".mp4");
        var passLog = Path.Combine(temporaryFolder, token);
        var videoRate = $"{bitrates.VideoKbps.ToString(CultureInfo.InvariantCulture)}k";

        var audioStreamCount = (await ProbeMediaAsync(inputPath, ffmpegPath, cancellationToken)).AudioStreamCount;

        try
        {
            await RunAsync(
                ffmpegPath,
                BuildCompressionArguments(1, inputPath, passLog, videoRate, bitrates.AudioKbps, outputPath),
                cancellationToken);

            await RunAsync(
                ffmpegPath,
                BuildCompressionArguments(
                    2,
                    inputPath,
                    passLog,
                    videoRate,
                    bitrates.AudioKbps,
                    outputPath,
                    audioStreamCount),
                cancellationToken);

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

    internal static async Task<ProcessResult> RunAsync(
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
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cleanupDeadline.Token); } catch { }
            try
            {
                await Task.WhenAll(standardOutput, standardError)
                    .WaitAsync(cleanupDeadline.Token);
            }
            catch { }
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

    // Matches the input stream dump, for example "Stream #0:1[0x2](eng): Audio: aac ...".
    [GeneratedRegex(@"Stream #0:\d+[^\r\n]*?:\s*Audio:", RegexOptions.CultureInvariant)]
    private static partial Regex AudioStreamPattern();

    internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    internal sealed record MediaProbe(TimeSpan Duration, int AudioStreamCount);
}

internal sealed class CompressionTargetUnachievableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
