using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ClipsToDiscord;

internal sealed class UploaderWorker(
    AppSettings settings,
    Action<string> reportStatus,
    WatchStateStore? stateStore = null,
    Func<DiscordWebhookClient>? discordClientFactory = null)
{
    private const int UploadWorkerCount = 2;
    private readonly WatchStateStore _stateStore = stateStore ?? new WatchStateStore();
    private readonly FileReadinessTracker _readiness = new();
    // After initialization, every read and write of WatchState's mutable collections
    // is performed while this gate is held by the scanner or an upload worker.
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
        using var discord = settings.UploadToDiscord
            ? (discordClientFactory ?? (() => new DiscordWebhookClient()))()
            : null;
        var queue = Channel.CreateBounded<QueuedClip>(new BoundedChannelOptions(50)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var consumers = Enumerable.Range(0, UploadWorkerCount)
            .Select(_ => ConsumeQueueAsync(queue.Reader, state, discord, cancellationToken))
            .ToArray();

        Log.Info($"Clip watcher started for {settings.ClipsFolder} with {UploadWorkerCount} clip workers in {(settings.UploadToDiscord ? "Discord upload" : "local-only")} mode.");
        reportStatus(settings.UploadToDiscord
            ? "Discord open — watching for clips"
            : "Discord open — local-only mode");

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
                if (state.IgnoredFileKeys.Contains(fileKey) ||
                    state.PendingMoves.Contains(clip.FullName) ||
                    state.PendingLocalOnlyMoves.Contains(clip.FullName))
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
        DiscordWebhookClient? discord,
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
        DiscordWebhookClient? discord,
        CancellationToken cancellationToken)
    {
        var destination = settings.UploadToDiscord
            ? ArchiveDestination.Uploaded
            : ArchiveDestination.LocalOnly;
        var durableDisposition = false;
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
                if (state.PendingMoves.Contains(clip.FilePath) ||
                    state.PendingLocalOnlyMoves.Contains(clip.FilePath)) return;
                alreadyKnown = state.KnownContentHashes.Contains(clip.ContentHash);
                previouslyUploaded = state.UploadedContentHashes.Contains(clip.ContentHash);
            }
            finally
            {
                _stateGate.Release();
            }

            // Initial top-level baseline files are filtered by IgnoredFileKeys before they
            // reach the queue. In local-only mode, any path that does reach this point is a
            // newly observed file and is deliberately organized locally even if its bytes
            // duplicate a known clip. Upload mode remains conservative and leaves known,
            // never-uploaded content in place.
            if (settings.UploadToDiscord && alreadyKnown && !previouslyUploaded)
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

            if (settings.UploadToDiscord && !alreadyKnown)
            {
                reportStatus($"Uploading {clip.FileName}");
                Log.Info($"Uploading new clip: {clip.FileName} ({clip.Length / 1024d / 1024d:F1} MB).");
                await (discord ?? throw new InvalidOperationException("The Discord client is unavailable in upload mode."))
                    .UploadWithCompressionAsync(
                    settings.WebhookUrl,
                    clip.FilePath,
                    settings.CompressionTargetMb,
                    settings.UploaderName,
                    cancellationToken);
            }
            else if (settings.UploadToDiscord)
            {
                Log.Info($"Content duplicate detected; archiving without another upload: {clip.FileName}.");
            }
            else
            {
                reportStatus($"Saving {clip.FileName} locally");
                Log.Info($"Local-only mode selected; archiving without a Discord request: {clip.FileName}.");
            }

            // For uploads, this is intentionally the first operation after Discord confirms
            // success. For both destinations, the content hash and intended move are flushed
            // to disk before any move attempt so a restart cannot change the routing decision.
            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                state.KnownContentHashes.Add(clip.ContentHash);
                if (destination == ArchiveDestination.Uploaded)
                {
                    state.UploadedContentHashes.Add(clip.ContentHash);
                    state.PendingMoves.Add(clip.FilePath);
                    state.PendingLocalOnlyMoves.Remove(clip.FilePath);
                }
                else
                {
                    state.LocalOnlyContentHashes.Add(clip.ContentHash);
                    state.PendingLocalOnlyMoves.Add(clip.FilePath);
                    state.PendingMoves.Remove(clip.FilePath);
                }
                state.IgnoredFileKeys.Remove(clip.FileKey);
                _stateStore.Save(state);
                durableDisposition = true;
            }
            finally
            {
                _stateGate.Release();
            }

            _retryAfter.TryRemove(clip.ContentHash, out _);
            await TryMoveAndClearPendingAsync(clip.FilePath, state, destination, cancellationToken);
            if (destination == ArchiveDestination.Uploaded)
            {
                Log.Info($"Upload complete and clip archived: {clip.FileName}");
                reportStatus("Upload complete — watching for clips");
            }
            else
            {
                Log.Info($"Clip saved to the local-only archive: {clip.FileName}");
                reportStatus("Saved locally — local-only mode");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CompressionTargetUnachievableException exception)
        {
            _retryAfter[clip.ContentHash] = DateTime.MaxValue;
            Log.Error(
                $"Upload needs attention for {clip.FileName}; ClipCord will not retry it automatically unless settings change or the app restarts.",
                exception);
            reportStatus($"Upload needs attention — {clip.FileName}");
        }
        catch (Exception exception)
        {
            if (durableDisposition)
            {
                if (destination == ArchiveDestination.Uploaded)
                {
                    Log.Error($"Clip was uploaded but could not be archived yet: {clip.FileName}. The move will be retried.", exception);
                    reportStatus($"Uploaded {clip.FileName} — archive move pending");
                }
                else
                {
                    Log.Error($"Clip was marked local-only but could not be archived yet: {clip.FileName}. The move will be retried.", exception);
                    reportStatus($"Local-only move pending — {clip.FileName}");
                }
            }
            else
            {
                var retryDelay = settings.UploadToDiscord ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1);
                _retryAfter[clip.ContentHash] = DateTime.UtcNow.Add(retryDelay);
                if (settings.UploadToDiscord)
                {
                    Log.Error($"Upload failed for {clip.FileName}; retrying in 5 minutes.", exception);
                    reportStatus($"Upload failed — retrying {clip.FileName} later");
                }
                else
                {
                    Log.Error($"Could not prepare local-only clip {clip.FileName}; retrying in 1 minute.", exception);
                    reportStatus($"Local-only save failed — retrying {clip.FileName}");
                }
            }
        }
    }

    private async Task ProcessPendingMovesAsync(WatchState state, CancellationToken cancellationToken)
    {
        string[] pendingUploads;
        string[] pendingLocalOnly;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            pendingUploads = state.PendingMoves.ToArray();
            pendingLocalOnly = state.PendingLocalOnlyMoves.ToArray();
        }
        finally
        {
            _stateGate.Release();
        }

        foreach (var path in pendingUploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await TryMoveAndClearPendingAsync(
                    path,
                    state,
                    ArchiveDestination.Uploaded,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not move uploaded clip {Path.GetFileName(path)}; it will be retried.", exception);
            }
        }

        foreach (var path in pendingLocalOnly)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await TryMoveAndClearPendingAsync(
                    path,
                    state,
                    ArchiveDestination.LocalOnly,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not move local-only clip {Path.GetFileName(path)}; it will be retried.", exception);
            }
        }
    }

    private async Task TryMoveAndClearPendingAsync(
        string sourcePath,
        WatchState state,
        ArchiveDestination destination,
        CancellationToken cancellationToken)
    {
        if (!_activeMoves.TryAdd(sourcePath, 0)) return;
        try
        {
            if (File.Exists(sourcePath))
            {
                await MovePendingClipAsync(sourcePath, destination, cancellationToken);
            }

            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                if (destination == ArchiveDestination.Uploaded)
                {
                    state.PendingMoves.Remove(sourcePath);
                }
                else
                {
                    state.PendingLocalOnlyMoves.Remove(sourcePath);
                }
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

    private static async Task MovePendingClipAsync(
        string sourcePath,
        ArchiveDestination destination,
        CancellationToken cancellationToken)
    {
        var clipsFolder = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The clip folder could not be determined.");
        var archiveFolder = destination == ArchiveDestination.Uploaded
            ? UploadedFolder.GetOrCreateForClip(clipsFolder, Path.GetFileName(sourcePath))
            : UploadedFolder.GetOrCreateLocalOnlyForClip(clipsFolder, Path.GetFileName(sourcePath));
        var destinationPath = UniqueDestination(archiveFolder, Path.GetFileName(sourcePath));
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

        throw new IOException($"Could not move the clip to {archiveFolder}.", lastError);
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

    private enum ArchiveDestination
    {
        Uploaded,
        LocalOnly
    }
}
