using System.Diagnostics;

namespace ClipsToDiscord;

internal static class SilhouetteWorkerProcess
{
    internal const int SuccessExitCode = 0;
    internal const int ProcessingFailedExitCode = 3;
    internal const int DependencyUnavailableExitCode = 4;
    internal const int InvalidArgumentsExitCode = 22;

    internal static async Task<int> RunAsync(
        SilhouetteWorkerLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            TryLowerPriority();
            var ffmpegPath = FfmpegCompressor.FindExecutable();
            if (ffmpegPath is null)
            {
                Log.Error(
                    "ClipCord could not start silhouette processing because ffmpeg.exe is unavailable.");
                return DependencyUnavailableExitCode;
            }

            var modelPath = OnnxSilhouetteFrameSegmenter.GetPackagedModelPath();
            if (!File.Exists(modelPath))
            {
                Log.Error(
                    "ClipCord could not start silhouette processing because its local model is unavailable.");
                return DependencyUnavailableExitCode;
            }

            var processor = new SilhouetteProjectProcessor(
                ffmpegPath,
                modelPath,
                preferDirectMl: !options.ForceCpu);
            var state = await processor.ProcessAsync(
                    options.LibraryRoot,
                    options.ProjectId,
                    cancellationToken)
                .ConfigureAwait(false);
            return state.AggregateState is
                SilhouetteRenditionAggregateState.Ready or
                SilhouetteRenditionAggregateState.Disabled
                ? SuccessExitCode
                : ProcessingFailedExitCode;
        }
        catch (OperationCanceledException)
        {
            return ProcessingFailedExitCode;
        }
        catch (Exception exception)
        {
            Log.Error(
                $"ClipCord silhouette processing failed for project {options.ProjectId}.",
                exception);
            return ProcessingFailedExitCode;
        }
    }

    private static void TryLowerPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
            // Best effort. The worker remains isolated from the capture process even if Windows
            // or a policy prevents adjusting its scheduling priority.
        }
    }
}
