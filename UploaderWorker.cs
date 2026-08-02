namespace MomentsToDiscord;

internal sealed class UploaderWorker(AppSettings settings, Action<string> reportStatus)
{
    private readonly WatchStateStore _stateStore = new();
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var state = _stateStore.LoadOrInitialize(settings.ClipsFolder);
        using var discord = new DiscordWebhookClient();
        Log.Info($"Clip watcher started for {settings.ClipsFolder}.");
        reportStatus("Discord open — watching for clips");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ProcessPendingMovesAsync(state, cancellationToken);

                foreach (var path in WatchStateStore.EnumerateClips(settings.ClipsFolder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var clip = new FileInfo(path);
                    var signature = WatchStateStore.Signature(clip);
                    if (state.KnownSignatures.Contains(signature)) continue;
                    if (_retryAfter.TryGetValue(signature, out var retryAt) && retryAt > DateTime.UtcNow) continue;
                    if (!IsReady(clip)) continue;

                    try
                    {
                        reportStatus($"Uploading {clip.Name}");
                        Log.Info($"Uploading new clip: {clip.Name} ({clip.Length / 1024d / 1024d:F1} MB).");
                        await discord.UploadWithCompressionAsync(settings.WebhookUrl, clip.FullName, cancellationToken);

                        state.KnownSignatures.Add(signature);
                        state.PendingMoves.Add(clip.FullName);
                        _retryAfter.Remove(signature);
                        _stateStore.Save(state);

                        await MovePendingClipAsync(clip.FullName, cancellationToken);
                        state.PendingMoves.Remove(clip.FullName);
                        _stateStore.Save(state);
                        Log.Info($"Upload complete and clip moved: {clip.Name}");
                        reportStatus("Upload complete — watching for clips");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _retryAfter[signature] = DateTime.UtcNow.AddMinutes(5);
                        Log.Error($"Upload failed for {clip.Name}; retrying in 5 minutes.", exception);
                        reportStatus($"Upload failed — retrying {clip.Name} later");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        finally
        {
            Log.Info("Clip watcher stopped.");
        }
    }

    private async Task ProcessPendingMovesAsync(WatchState state, CancellationToken cancellationToken)
    {
        foreach (var path in state.PendingMoves.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                state.PendingMoves.Remove(path);
                _stateStore.Save(state);
                continue;
            }

            try
            {
                await MovePendingClipAsync(path, cancellationToken);
                state.PendingMoves.Remove(path);
                _stateStore.Save(state);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not move uploaded clip {Path.GetFileName(path)}; it will be retried.", exception);
            }
        }
    }

    private static async Task MovePendingClipAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var clipsFolder = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The clip folder could not be determined.");
        var uploadedFolder = Path.Combine(clipsFolder, "uploaded");
        Directory.CreateDirectory(uploadedFolder);
        var destinationPath = UniqueDestination(uploadedFolder, Path.GetFileName(sourcePath));
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(sourcePath, destinationPath);
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                if (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }

        throw new IOException($"Could not move the uploaded clip to {uploadedFolder}.", lastError);
    }

    private static string UniqueDestination(string folder, string fileName)
    {
        var destination = Path.Combine(folder, fileName);
        if (!File.Exists(destination)) return destination;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        do
        {
            var suffix = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}"[..25];
            destination = Path.Combine(folder, $"{baseName}-{suffix}{extension}");
        } while (File.Exists(destination));

        return destination;
    }

    private static bool IsReady(FileInfo file)
    {
        if (DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(20)) return false;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
