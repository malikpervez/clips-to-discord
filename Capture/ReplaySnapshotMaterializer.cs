using System.Globalization;
using System.Diagnostics;

namespace ClipsToDiscord;

internal static class ReplaySnapshotMaterializer
{
    internal static async Task<ManualCaptureResult> SaveAsync(
        EncodedReplaySnapshot snapshot,
        ReplayAudioSession? audioSession,
        CaptureSettings settings,
        string gameName,
        CancellationToken cancellationToken = default,
        string? ffmpegOverride = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? concatRunner = null,
        EncodedReplaySnapshot? reactionCameraSnapshot = null,
        string? reactionCameraWarning = null,
        TimeSpan? reactionCameraPersistenceTimeout = null,
        Func<CancellationToken, Task>? reactionCameraPersistenceProbe = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var commandStopwatch = Stopwatch.StartNew();
        if (snapshot.Segments.Count == 0) throw new ArgumentException("The replay snapshot is empty.", nameof(snapshot));
        settings = CaptureSettings.Normalize(settings);
        gameName = string.IsNullOrWhiteSpace(gameName) ? "Unknown Game" : gameName.Trim();
        var ffmpeg = ffmpegOverride ?? FfmpegCompressor.FindExecutable() ??
            throw new InvalidOperationException("ClipCord's bundled FFmpeg tool is required to save an Instant Replay.");
        concatRunner ??= static async (executable, arguments, token) =>
        {
            await FfmpegCompressor.RunAsync(executable, arguments, token).ConfigureAwait(false);
        };
        var capturedAt = DateTimeOffset.Now;
        var outputDirectory = CaptureLibraryLayout.GetRecordingDirectory(settings.LibraryRoot, gameName);
        Directory.CreateDirectory(outputDirectory);
        var finalPath = CaptureOutputPolicy.CreateAvailablePath(outputDirectory, gameName, capturedAt);
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(settings.LibraryRoot);
        Directory.CreateDirectory(stagingDirectory);
        EnsureOrdinaryDirectory(stagingDirectory);

        var token = Guid.NewGuid().ToString("N");
        var segmentPaths = new List<string>();
        var audioPaths = new List<string>();
        var concatPath = Path.Combine(stagingDirectory, $"replay-save-{token}.concat.txt");
        var videoPath = Path.Combine(stagingDirectory, $"replay-save-{token}.video.mp4");
        var completedPath = Path.Combine(stagingDirectory, $"replay-save-{token}.mp4");
        try
        {
            for (var index = 0; index < snapshot.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(
                    stagingDirectory,
                    $"replay-save-{token}.{index.ToString("D4", CultureInfo.InvariantCulture)}.mp4");
                await File.WriteAllBytesAsync(
                    path,
                    snapshot.Segments[index].Mp4Bytes.ToArray(),
                    cancellationToken).ConfigureAwait(false);
                segmentPaths.Add(path);
            }
            await File.WriteAllLinesAsync(
                concatPath,
                segmentPaths.Select(path => $"file '{Path.GetFileName(path)}'"),
                cancellationToken).ConfigureAwait(false);
            await concatRunner(
                ffmpeg,
                BuildConcatArguments(concatPath, videoPath),
                cancellationToken).ConfigureAwait(false);
            EnsureNonEmptyFile(videoPath, "ClipCord could not assemble the buffered replay video.");

            var audioTracks = audioSession is null
                ? Array.Empty<CaptureAudioMuxTrack>()
                : await audioSession.WriteSnapshotAsync(
                    snapshot.StartTimestamp,
                    snapshot.EndTimestamp,
                    stagingDirectory,
                    token,
                    cancellationToken).ConfigureAwait(false);
            audioPaths.AddRange(audioTracks.Select(track => track.Path));
            await CaptureAudioMuxer.MuxAsync(
                videoPath,
                audioTracks,
                snapshot.StartTimestamp,
                completedPath,
                cancellationToken).ConfigureAwait(false);
            EnsureNonEmptyFile(completedPath, "ClipCord could not finalize the buffered replay.");

            var dimensions = CaptureProfileCatalog.GetDimensions(settings.Resolution);
            var journal = await CaptureJournalCaptureCommit.PromoteOriginalAsync(
                    settings.LibraryRoot,
                    completedPath,
                    finalPath,
                    CaptureJournalSourceKind.InstantReplay,
                    gameName,
                    capturedAt,
                    snapshot.ActualDuration,
                    dimensions.Width,
                    dimensions.Height,
                    settings,
                    CancellationToken.None)
                .ConfigureAwait(false);
            CaptureProjectSaveResult? cameraLayer = null;
            if (reactionCameraSnapshot is not null)
            {
                CancellationTokenSource? cameraCancellation = null;
                IDisposable? persistenceLease = null;
                if (!ReactionCameraPersistenceGate.TryAcquire(out persistenceLease))
                {
                    reactionCameraWarning =
                        "The replay was saved without a new Reaction Camera layer because a previous camera layer is still being stored.";
                }
                else
                {
                    try
                    {
                        cameraCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        var cameraTask = SaveReactionCameraLayerAsync(
                            reactionCameraSnapshot,
                            snapshot.StartTimestamp,
                            settings,
                            finalPath,
                            capturedAt,
                            stagingDirectory,
                            token,
                            ffmpeg,
                            concatRunner,
                            cameraCancellation.Token,
                            reactionCameraPersistenceProbe);
                        var timeout = reactionCameraPersistenceTimeout ??
                            ReactionCameraPersistencePolicy.GetRemainingBudget(
                                commandStopwatch.Elapsed);
                        if (await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                                cameraTask,
                                timeout,
                                CancellationToken.None).ConfigureAwait(false))
                        {
                            cameraLayer = await cameraTask.ConfigureAwait(false);
                            cameraCancellation.Dispose();
                            cameraCancellation = null;
                            persistenceLease!.Dispose();
                            persistenceLease = null;
                        }
                        else
                        {
                            cameraCancellation.Cancel();
                            var ownedCancellation = cameraCancellation;
                            var ownedLease = persistenceLease;
                            _ = cameraTask.ContinueWith(
                                completed =>
                                {
                                    _ = completed.Exception;
                                    ownedCancellation.Dispose();
                                    ownedLease?.Dispose();
                                },
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                            cameraCancellation = null;
                            persistenceLease = null;
                            reactionCameraWarning =
                                "The replay was saved, but storing its Reaction Camera layer timed out.";
                        }
                    }
                    catch (Exception exception)
                    {
                        cameraCancellation?.Cancel();
                        cameraCancellation?.Dispose();
                        persistenceLease?.Dispose();
                        Log.Error(
                            "ClipCord saved the replay but could not store its optional Reaction Camera layer.",
                            exception);
                        reactionCameraWarning =
                            "The replay was saved, but its Reaction Camera layer could not be stored.";
                    }
                }
            }
            else if (settings.IncludeReactionCamera && string.IsNullOrWhiteSpace(reactionCameraWarning))
            {
                reactionCameraWarning =
                    "The replay was saved without a Reaction Camera layer because no camera segment was available.";
            }
            try
            {
                _ = await CaptureJournalCaptureCommit.FinalizeCameraAsync(
                        settings.LibraryRoot,
                        journal.Clip.ClipId,
                        cameraLayer,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The replay and source journal are already durable. Optional camera projection
                // is recoverable and must not turn a successful hotkey save into a failure.
                Log.Error(
                    "ClipCord saved the replay but could not update its camera journal state.",
                    exception);
                reactionCameraWarning ??=
                    "The replay was saved, but Reaction Camera processing needs attention.";
            }
            return new ManualCaptureResult(
                finalPath,
                gameName,
                capturedAt,
                snapshot.ActualDuration,
                cameraLayer,
                reactionCameraWarning);
        }
        catch
        {
            if (!CaptureJournalPromotionIntentStore.IsOriginalStageProtected(
                    settings.LibraryRoot,
                    completedPath))
            {
                TryDelete(completedPath);
            }
            throw;
        }
        finally
        {
            foreach (var path in segmentPaths) TryDelete(path);
            foreach (var path in audioPaths) TryDelete(path);
            TryDelete(concatPath);
            TryDelete(videoPath);
        }
    }

    private static async Task<CaptureProjectSaveResult> SaveReactionCameraLayerAsync(
        EncodedReplaySnapshot reactionCameraSnapshot,
        TimeSpan gameplayStart,
        CaptureSettings settings,
        string finalGameplayPath,
        DateTimeOffset capturedAt,
        string stagingDirectory,
        string token,
        string ffmpeg,
        Func<string, IReadOnlyList<string>, CancellationToken, Task> concatRunner,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? persistenceProbe)
    {
        var segmentPaths = new List<string>();
        var concatPath = Path.Combine(
            stagingDirectory,
            $"replay-save-{token}.camera.concat.txt");
        var videoPath = Path.Combine(
            stagingDirectory,
            $"replay-save-{token}.camera.mp4");
        try
        {
            for (var index = 0; index < reactionCameraSnapshot.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(
                    stagingDirectory,
                    $"replay-save-{token}.camera.{index.ToString("D4", CultureInfo.InvariantCulture)}.mp4");
                await File.WriteAllBytesAsync(
                    path,
                    reactionCameraSnapshot.Segments[index].Mp4Bytes.ToArray(),
                    cancellationToken).ConfigureAwait(false);
                segmentPaths.Add(path);
            }
            await File.WriteAllLinesAsync(
                concatPath,
                segmentPaths.Select(path => $"file '{Path.GetFileName(path)}'"),
                cancellationToken).ConfigureAwait(false);
            await concatRunner(
                ffmpeg,
                BuildConcatArguments(concatPath, videoPath),
                cancellationToken).ConfigureAwait(false);
            EnsureNonEmptyFile(
                videoPath,
                "ClipCord could not assemble the buffered Reaction Camera layer.");
            if (persistenceProbe is not null)
            {
                await persistenceProbe(cancellationToken).ConfigureAwait(false);
            }
            // The UI process attached reusable layout preferences to settings when buffering
            // started. Commit that immutable start-time value rather than rereading mutable
            // defaults when the hotkey save completes.
            var compositionSnapshot = CaptureCompositionSnapshotFactory.Create(
                settings,
                finalGameplayPath,
                mirrorCamera: true,
                capturedAt);
            return await CaptureProjectStore.SaveReactionCameraLayerAsync(
                settings.LibraryRoot,
                finalGameplayPath,
                videoPath,
                gameplayStart,
                reactionCameraSnapshot.StartTimestamp,
                reactionCameraSnapshot.ActualDuration,
                mirrorCamera: true,
                compositionSnapshot: compositionSnapshot,
                createdUtc: capturedAt,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in segmentPaths) TryDelete(path);
            TryDelete(concatPath);
            TryDelete(videoPath);
        }
    }

    internal static IReadOnlyList<string> BuildConcatArguments(string concatPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(concatPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        return [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "concat",
            "-safe", "1",
            "-i", concatPath,
            "-map", "0:v:0",
            "-c:v", "copy",
            "-an",
            "-movflags", "+faststart",
            outputPath
        ];
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            throw new IOException("The replay staging folder cannot be a symbolic link or junction.");
        }
    }

    private static void EnsureNonEmptyFile(string path, string message)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new InvalidOperationException(message);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
