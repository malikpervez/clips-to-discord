using System.Diagnostics;
using System.Globalization;

namespace ClipsToDiscord;

internal sealed record SilhouetteMatteAnalysis(
    int FrameCount,
    double FramesPerSecond,
    NormalizedSilhouetteBounds SubjectBounds)
{
    internal double SubjectAspectRatio => SubjectBounds.Width / SubjectBounds.Height;
}

internal static class SilhouetteMatteGenerator
{
    internal const int FramesPerSecond = 30;
    private const byte BoundsThreshold = 96;
    private const double BoundsPadding = 0.035;
    private const int MinimumForegroundPixels = 96;

    internal static async Task<SilhouetteMatteAnalysis> GenerateAsync(
        string ffmpegPath,
        string cameraPath,
        string temporaryMattePath,
        ISilhouetteFrameSegmenter segmenter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryMattePath);
        ArgumentNullException.ThrowIfNull(segmenter);
        if (segmenter.InputWidth != OnnxSilhouetteFrameSegmenter.ModelWidth ||
            segmenter.InputHeight != OnnxSilhouetteFrameSegmenter.ModelHeight)
        {
            throw new ArgumentException("The silhouette segmenter has an unexpected frame size.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var decoder = StartDecoder(ffmpegPath, cameraPath, segmenter.InputWidth, segmenter.InputHeight);
        var encoder = StartEncoder(
            ffmpegPath,
            temporaryMattePath,
            segmenter.InputWidth,
            segmenter.InputHeight);
        var decoderError = decoder.StandardError.ReadToEndAsync(cancellationToken);
        var encoderError = encoder.StandardError.ReadToEndAsync(cancellationToken);
        var rgb = new byte[checked(segmenter.InputWidth * segmenter.InputHeight * 3)];
        var alpha = new byte[checked(segmenter.InputWidth * segmenter.InputHeight)];
        var bounds = new BoundsAccumulator(segmenter.InputWidth, segmenter.InputHeight);
        var frameCount = 0;

        try
        {
            while (await ReadFrameAsync(
                       decoder.StandardOutput.BaseStream,
                       rgb,
                       cancellationToken).ConfigureAwait(false))
            {
                segmenter.Segment(rgb, alpha);
                bounds.Add(alpha);
                await encoder.StandardInput.BaseStream.WriteAsync(
                    alpha,
                    cancellationToken).ConfigureAwait(false);
                frameCount = checked(frameCount + 1);
            }
            await encoder.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            encoder.StandardInput.Close();
            await Task.WhenAll(
                    decoder.WaitForExitAsync(cancellationToken),
                    encoder.WaitForExitAsync(cancellationToken))
                .ConfigureAwait(false);
            var decodeDiagnostic = await decoderError.ConfigureAwait(false);
            var encodeDiagnostic = await encoderError.ConfigureAwait(false);
            if (decoder.ExitCode != 0 || encoder.ExitCode != 0 ||
                frameCount <= 0 || !File.Exists(temporaryMattePath) ||
                new FileInfo(temporaryMattePath).Length <= 0)
            {
                LogFfmpegFailure(
                    MediaToolOperation.SilhouetteMatte,
                    decoder.ExitCode,
                    decodeDiagnostic);
                LogFfmpegFailure(
                    MediaToolOperation.SilhouetteMatte,
                    encoder.ExitCode,
                    encodeDiagnostic);
                throw new InvalidOperationException(
                    "ClipCord could not create the local Reaction Camera silhouette.");
            }
            return new SilhouetteMatteAnalysis(
                frameCount,
                FramesPerSecond,
                bounds.Complete());
        }
        catch
        {
            Kill(decoder);
            Kill(encoder);
            try { encoder.StandardInput.Close(); } catch { }
            throw;
        }
        finally
        {
            decoder.Dispose();
            encoder.Dispose();
        }
    }

    internal static async Task<SilhouetteMatteAnalysis> AnalyzeAsync(
        string ffmpegPath,
        string mattePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mattePath);
        var process = StartMatteDecoder(ffmpegPath, mattePath);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var alpha = new byte[
            OnnxSilhouetteFrameSegmenter.ModelWidth *
            OnnxSilhouetteFrameSegmenter.ModelHeight];
        var bounds = new BoundsAccumulator(
            OnnxSilhouetteFrameSegmenter.ModelWidth,
            OnnxSilhouetteFrameSegmenter.ModelHeight);
        var frameCount = 0;
        try
        {
            while (await ReadFrameAsync(
                       process.StandardOutput.BaseStream,
                       alpha,
                       cancellationToken).ConfigureAwait(false))
            {
                bounds.Add(alpha);
                frameCount = checked(frameCount + 1);
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var diagnostic = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0 || frameCount <= 0)
            {
                LogFfmpegFailure(
                    MediaToolOperation.SilhouetteMatteInspection,
                    process.ExitCode,
                    diagnostic);
                throw new InvalidDataException("The saved silhouette matte could not be read.");
            }
            return new SilhouetteMatteAnalysis(
                frameCount,
                FramesPerSecond,
                bounds.Complete());
        }
        catch
        {
            Kill(process);
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    internal static IReadOnlyList<string> BuildDecoderArguments(
        string cameraPath,
        int width,
        int height) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-i", cameraPath,
        "-an",
        "-vf", string.Create(
            CultureInfo.InvariantCulture,
            $"fps={FramesPerSecond},scale={width}:{height}:flags=bicubic"),
        "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"
    ];

    internal static IReadOnlyList<string> BuildEncoderArguments(
        string outputPath,
        int width,
        int height) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "rawvideo", "-pix_fmt", "gray",
        "-video_size", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
        "-framerate", FramesPerSecond.ToString(CultureInfo.InvariantCulture),
        "-i", "pipe:0", "-an", "-c:v", "ffv1", "-level", "3", "-g", "1",
        "-f", "matroska", outputPath
    ];

    private static Process StartDecoder(string ffmpeg, string input, int width, int height) =>
        StartProcess(
            ffmpeg,
            BuildDecoderArguments(input, width, height),
            redirectInput: false,
            redirectOutput: true);

    private static Process StartEncoder(string ffmpeg, string output, int width, int height) =>
        StartProcess(
            ffmpeg,
            BuildEncoderArguments(output, width, height),
            redirectInput: true,
            redirectOutput: false);

    private static Process StartMatteDecoder(string ffmpeg, string mattePath) =>
        StartProcess(
            ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", mattePath, "-an", "-pix_fmt", "gray",
                "-f", "rawvideo", "pipe:1"
            ],
            redirectInput: false,
            redirectOutput: true);

    private static Process StartProcess(
        string executable,
        IEnumerable<string> arguments,
        bool redirectInput,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("ClipCord could not start its local media processor.");
        }
        return process;
    }

    private static async Task<bool> ReadFrameAsync(
        Stream stream,
        Memory<byte> frame,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = await stream.ReadAsync(frame[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new InvalidDataException("A local silhouette frame ended unexpectedly.");
            }
            offset += read;
        }
        return true;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static void LogFfmpegFailure(
        MediaToolOperation operation,
        int exitCode,
        string diagnostic)
    {
        if (exitCode == 0) return;
        Log.Error(
            MediaToolDiagnosticSanitizer.BuildFfmpegFailureMessage(
                operation,
                exitCode,
                diagnostic));
    }

    private sealed class BoundsAccumulator(int width, int height)
    {
        private readonly List<int> _left = [];
        private readonly List<int> _top = [];
        private readonly List<int> _right = [];
        private readonly List<int> _bottom = [];

        internal void Add(ReadOnlySpan<byte> alpha)
        {
            if (alpha.Length != width * height)
            {
                throw new ArgumentException("The matte frame has an unexpected size.");
            }
            var left = width;
            var top = height;
            var right = -1;
            var bottom = -1;
            var foreground = 0;
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    if (alpha[row + x] < BoundsThreshold) continue;
                    foreground++;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (foreground < MinimumForegroundPixels) return;
            _left.Add(left);
            _top.Add(top);
            _right.Add(right);
            _bottom.Add(bottom);
        }

        internal NormalizedSilhouetteBounds Complete()
        {
            if (_left.Count == 0)
            {
                throw new InvalidDataException(
                    "ClipCord could not find a person in the Reaction Camera layer.");
            }
            var left = Percentile(_left, 0.05) / width;
            var top = Percentile(_top, 0.05) / height;
            var right = (Percentile(_right, 0.95) + 1) / width;
            var bottom = (Percentile(_bottom, 0.95) + 1) / height;
            left = Math.Clamp(left - BoundsPadding, 0, 1);
            top = Math.Clamp(top - BoundsPadding, 0, 1);
            right = Math.Clamp(right + BoundsPadding, 0, 1);
            bottom = Math.Clamp(bottom + BoundsPadding, 0, 1);
            if (right - left < 0.05 || bottom - top < 0.05)
            {
                throw new InvalidDataException(
                    "ClipCord found an invalid person region in the Reaction Camera layer.");
            }
            return new NormalizedSilhouetteBounds(left, top, right - left, bottom - top);
        }

        private static double Percentile(List<int> values, double percentile)
        {
            var ordered = values.Order().ToArray();
            var index = (int)Math.Round(
                (ordered.Length - 1) * percentile,
                MidpointRounding.AwayFromZero);
            return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
        }
    }
}
