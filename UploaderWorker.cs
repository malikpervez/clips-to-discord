using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ClipsToDiscord;

internal sealed class UploaderWorker(AppSettings settings, Action<string> reportStatus)
{
    private const int UploadWorkerCount = 2;
    private readonly WatchStateStore _stateStore = new();
    private readonly FileReadinessTracker _readiness = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queuedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _activeMoves = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _hashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastReadinessLog = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var state = await _stateStore.LoadOrInitializeAsync(
            settings.ClipsFolder,
            reportStatus,
            cancellationToken);
        using var discord = new DiscordWebhookClient();
        var queue = Channel.CreateBounded<QueuedClip>(new BoundedChannelOptions(50)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var consumers = Enumerable.Range(0, UploadWorkerCount)
            .Select(_ => ConsumeQueueAsync(queue.Reader, state, discord, cancellationToken))
            .ToArray();

        Log.Info($"Clip watcher started for {settings.ClipsFolder} with {UploadWorkerCount} upload workers.");
        reportStatus("Discord open — watching for clips");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ProcessPendingMovesAsync(state, cancellationToken);
                _readiness.RemoveMissingFiles();

                foreach (var path in WatchStateStore.EnumerateClips(settings.ClipsFolder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await InspectAndQueueAsync(path, state, queue.Writer, cancellationToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            try
            {
                await Task.WhenAll(consumers);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected when Discord or the tray app closes.
            }
            _stateGate.Dispose();
            Log.Info("Clip watcher stopped.");
        }
    }

    private async Task InspectAndQueueAsync(
        string path,
        WatchState state,
        ChannelWriter<QueuedClip> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            if (IsBackedOff(path)) return;
            var clip = new FileInfo(path);
            clip.Refresh();
            var fileKey = WatchStateStore.FileKey(clip);

            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                if (state.IgnoredFileKeys.Contains(fileKey) || state.PendingMoves.Contains(clip.FullName))
                {
                    return;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            if (IsBackedOff(fileKey)) return;
            var readiness = _readiness.Observe(clip, DateTime.UtcNow);
            if (!readiness.IsReady)
            {
                MaybeLogReadinessBackoff(clip.Name, clip.FullName, readiness);
                return;
            }
            _lastReadinessLog.TryRemove(clip.FullName, out _);

            reportStatus($"Hashing {clip.Name}");
            var contentHash = _hashCache.TryGetValue(fileKey, out var cachedHash)
                ? cachedHash
                : await ContentIdentity.ComputeSha256Async(clip.FullName, cancellationToken);
            _hashCache[fileKey] = contentHash;

            if (IsBackedOff(contentHash)) return;
            if (!_queuedHashes.TryAdd(contentHash, 0)) return;

            try
            {
                await writer.WriteAsync(
                    new QueuedClip(clip.FullName, clip.Name, fileKey, contentHash, clip.Length),
                    cancellationToken);
                _readiness.Forget(clip.FullName);
            }
            catch
            {
                _queuedHashes.TryRemove(contentHash, out _);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _retryAfter[path] = DateTime.UtcNow.AddMinutes(1);
            Log.Error($"Could not inspect clip {Path.GetFileName(path)}; retrying later.", exception);
        }
    }

    private async Task ConsumeQueueAsync(
        ChannelReader<QueuedClip> reader,
        WatchState state,
        DiscordWebhookClient discord,
        CancellationToken cancellationToken)
    {
        await foreach (var clip in reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessQueuedClipAsync(clip, state, discord, cancellationToken);
            }
            finally
            {
                _queuedHashes.TryRemove(clip.ContentHash, out _);
                _hashCache.TryRemove(clip.FileKey, out _);
            }
        }
    }

    private async Task ProcessQueuedClipAsync(
        QueuedClip clip,
        WatchState state,
        DiscordWebhookClient discord,
        CancellationToken cancellationToken)
    {
        var durableSuccess = false;
        try
        {
            if (!File.Exists(clip.FilePath)) return;
            var currentFile = new FileInfo(clip.FilePath);
            currentFile.Refresh();
            if (!WatchStateStore.FileKey(currentFile).Equals(clip.FileKey, StringComparison.OrdinalIgnoreCase))
            {
                _hashCache.TryRemove(clip.FileKey, out _);
                return;
            }

            await _stateGate.WaitAsync(cancellationToken);
            bool alreadyKnown;
            bool previouslyUploaded;
            try
            {
                if (state.PendingMoves.Contains(clip.FilePath)) return;
                alreadyKnown = state.KnownContentHashes.Contains(clip.ContentHash);
                previouslyUploaded = state.UploadedContentHashes.Contains(clip.ContentHash);
            }
            finally
            {
                _stateGate.Release();
            }

            if (alreadyKnown && !previouslyUploaded)
            {
                await _stateGate.WaitAsync(cancellationToken);
                try
                {
                    state.IgnoredFileKeys.Add(clip.FileKey);
                    _stateStore.Save(state);
                }
                finally
                {
                    _stateGate.Release();
                }
                Log.Info($"Content matches the existing baseline; leaving it in place without uploading: {clip.FileName}.");
                return;
            }

            if (!alreadyKnown)
            {
                reportStatus($"Uploading {clip.FileName}");
                Log.Info($"Uploading new clip: {clip.FileName} ({clip.Length / 1024d / 1024d:F1} MB).");
                await discord.UploadWithCompressionAsync(
                    settings.WebhookUrl,
                    clip.FilePath,
                    settings.CompressionTargetMb,
                    cancellationToken);
            }
            else
            {
                Log.Info($"Content duplicate detected; archiving without another upload: {clip.FileName}.");
            }

            // This is intentionally the first operation after Discord confirms success.
            // The content hash and pending move are flushed to disk before any move attempt.
            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                state.KnownContentHashes.Add(clip.ContentHash);
                state.UploadedContentHashes.Add(clip.ContentHash);
                state.PendingMoves.Add(clip.FilePath);
                state.IgnoredFileKeys.Remove(clip.FileKey);
                _stateStore.Save(state);
                durableSuccess = true;
            }
            finally
            {
                _stateGate.Release();
            }

            _retryAfter.TryRemove(clip.ContentHash, out _);
            await TryMoveAndClearPendingAsync(clip.FilePath, state, cancellationToken);
            Log.Info($"Upload complete and clip archived: {clip.FileName}");
            reportStatus("Upload complete — watching for clips");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (durableSuccess)
            {
                Log.Error($"Clip was uploaded but could not be archived yet: {clip.FileName}. The move will be retried.", exception);
                reportStatus($"Uploaded {clip.FileName} — archive move pending");
            }
            else
            {
                _retryAfter[clip.ContentHash] = DateTime.UtcNow.AddMinutes(5);
                Log.Error($"Upload failed for {clip.FileName}; retrying in 5 minutes.", exception);
                reportStatus($"Upload failed — retrying {clip.FileName} later");
            }
        }
    }

    private async Task ProcessPendingMovesAsync(WatchState state, CancellationToken cancellationToken)
    {
        string[] pending;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            pending = state.PendingMoves.ToArray();
        }
        finally
        {
            _stateGate.Release();
        }

        foreach (var path in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await TryMoveAndClearPendingAsync(path, state, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not move uploaded clip {Path.GetFileName(path)}; it will be retried.", exception);
            }
        }
    }

    private async Task TryMoveAndClearPendingAsync(
        string sourcePath,
        WatchState state,
        CancellationToken cancellationToken)
    {
        if (!_activeMoves.TryAdd(sourcePath, 0)) return;
        try
        {
            if (File.Exists(sourcePath))
            {
                await MovePendingClipAsync(sourcePath, cancellationToken);
            }

            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                state.PendingMoves.Remove(sourcePath);
                _stateStore.Save(state);
            }
            finally
            {
                _stateGate.Release();
            }
        }
        finally
        {
            _activeMoves.TryRemove(sourcePath, out _);
        }
    }

    private static async Task MovePendingClipAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var clipsFolder = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The clip folder could not be determined.");
        var uploadedFolder = UploadedFolder.GetOrCreate(clipsFolder);
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

    private bool IsBackedOff(string key) =>
        _retryAfter.TryGetValue(key, out var retryAt) && retryAt > DateTime.UtcNow;

    private void MaybeLogReadinessBackoff(
        string fileName,
        string filePath,
        FileReadinessResult readiness)
    {
        if (readiness.ConsecutiveOpenFailures < FileReadinessTracker.StuckLogThreshold) return;
        if (_lastReadinessLog.TryGetValue(filePath, out var lastLog) &&
            DateTime.UtcNow - lastLog < TimeSpan.FromMinutes(5)) return;

        _lastReadinessLog[filePath] = DateTime.UtcNow;
        Log.Info($"Clip has failed {readiness.ConsecutiveOpenFailures} readiness checks; next check at {readiness.NextCheckUtc:u}: {fileName} ({readiness.Reason}).");
    }

    private sealed record QueuedClip(
        string FilePath,
        string FileName,
        string FileKey,
        string ContentHash,
        long Length);
}
