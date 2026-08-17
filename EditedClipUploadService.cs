using Microsoft.VisualBasic.FileIO;

namespace ClipsToDiscord;

internal interface IOriginalClipRecycler
{
    void Recycle(string path);
}

internal sealed class WindowsOriginalClipRecycler : IOriginalClipRecycler
{
    public void Recycle(string path)
    {
        FileSystem.DeleteFile(
            path,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.DoNothing);
    }
}

internal interface IManualDiscordUploader : IDisposable
{
    Task UploadAsync(
        string webhookUrl,
        string physicalPath,
        DiscordUploadPresentation presentation,
        int compressionTargetMb,
        string uploaderName,
        CancellationToken cancellationToken,
        Action<CompressionProgress>? reportCompression);
}

internal sealed class ManualDiscordUploader : IManualDiscordUploader
{
    private readonly DiscordWebhookClient _client = new();

    public Task UploadAsync(
        string webhookUrl,
        string physicalPath,
        DiscordUploadPresentation presentation,
        int compressionTargetMb,
        string uploaderName,
        CancellationToken cancellationToken,
        Action<CompressionProgress>? reportCompression) =>
        _client.UploadWithCompressionAsync(
            webhookUrl,
            physicalPath,
            presentation,
            compressionTargetMb,
            uploaderName,
            cancellationToken,
            reportCompression,
            completeStartedPosts: true);

    public void Dispose() => _client.Dispose();
}

internal sealed record EditedClipDispositionResult(
    string ArchivedPath,
    bool OriginalKept,
    bool OriginalCleanupFailed);

internal sealed class EditedClipDispositionProcessor(
    IOriginalClipRecycler? recycler = null)
{
    private readonly IOriginalClipRecycler _recycler = recycler ?? new WindowsOriginalClipRecycler();

    internal async Task<EditedClipDispositionResult> CompleteAsync(
        PendingEditedClipDisposition pending,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ValidatePendingPaths(pending);
        cancellationToken.ThrowIfCancellationRequested();

        var editedExists = File.Exists(pending.EditedPath);
        var destinationExists = File.Exists(pending.DestinationPath);
        if (destinationExists)
        {
            var destinationHash = await ContentIdentity.ComputeSha256Async(
                pending.DestinationPath,
                cancellationToken);
            if (!destinationHash.Equals(pending.EditedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The reserved edited-clip destination is occupied by different content.");
            }

            if (editedExists && !PathsEqual(pending.EditedPath, pending.OriginalLocalOnlyPath))
            {
                var editedHash = await ContentIdentity.ComputeSha256Async(pending.EditedPath, cancellationToken);
                if (!editedHash.Equals(pending.EditedContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The staged edited clip changed before archive recovery completed.");
                }
                DeleteStagedArtifact(pending);
            }
        }
        else
        {
            if (!editedExists)
            {
                throw new FileNotFoundException(
                    "The confirmed edited clip is missing from both staging and its archive destination.",
                    pending.EditedPath);
            }
            var editedHash = await ContentIdentity.ComputeSha256Async(pending.EditedPath, cancellationToken);
            if (!editedHash.Equals(pending.EditedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The staged edited clip changed before it could be archived.");
            }
            File.Move(pending.EditedPath, pending.DestinationPath, overwrite: false);
            destinationExists = true;
        }

        if (!destinationExists)
        {
            throw new IOException("The edited clip archive move did not complete.");
        }

        var originalKept = pending.KeepOriginal;
        var cleanupFailed = false;
        if (!pending.KeepOriginal && File.Exists(pending.OriginalLocalOnlyPath))
        {
            try
            {
                var original = ClipEditProcessor.ValidateLocalOnlySource(
                    pending.ClipsFolder,
                    pending.OriginalLocalOnlyPath);
                var currentHash = await ContentIdentity.ComputeSha256Async(
                    original.FullName,
                    cancellationToken);
                if (!currentHash.Equals(pending.OriginalContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    cleanupFailed = true;
                    originalKept = true;
                }
                else
                {
                    _recycler.Recycle(original.FullName);
                    originalKept = File.Exists(original.FullName);
                    cleanupFailed = originalKept;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                cleanupFailed = true;
                originalKept = true;
                Log.Error("The edited clip was uploaded, but its Local-only original could not be sent to the Recycle Bin.", exception);
            }
        }
        else if (!pending.KeepOriginal)
        {
            // The original itself was already moved into uploaded, or cleanup completed
            // before a restart resumed the durable disposition.
            originalKept = false;
        }

        TryDeleteOperationDirectory(pending.EditedPath);
        return new EditedClipDispositionResult(
            pending.DestinationPath,
            originalKept,
            cleanupFailed);
    }

    internal static void ValidatePendingPaths(PendingEditedClipDisposition pending)
    {
        if (pending.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(pending.ClipsFolder) ||
            string.IsNullOrWhiteSpace(pending.EditedPath) ||
            string.IsNullOrWhiteSpace(pending.DestinationPath) ||
            string.IsNullOrWhiteSpace(pending.OriginalLocalOnlyPath) ||
            string.IsNullOrWhiteSpace(pending.EditedContentHash) ||
            string.IsNullOrWhiteSpace(pending.OriginalContentHash))
        {
            throw new InvalidOperationException("The pending edited-clip disposition is incomplete.");
        }

        var clipsFolder = Path.GetFullPath(pending.ClipsFolder);
        var uploadedRoot = UploadedFolder.FindExistingUploaded(clipsFolder)
            ?? throw new DirectoryNotFoundException("The uploaded archive is unavailable.");
        EnsureNormalDirectory(uploadedRoot);
        EnsureContained(uploadedRoot, pending.DestinationPath, maximumSegments: 2);
        EnsureNormalParent(pending.DestinationPath);
        if (File.Exists(pending.DestinationPath)) EnsureNormalFile(pending.DestinationPath);

        if (PathsEqual(pending.EditedPath, pending.OriginalLocalOnlyPath))
        {
            var localOnlyRoot = UploadedFolder.FindExistingLocalOnly(clipsFolder)
                ?? throw new DirectoryNotFoundException("The Local-only archive is unavailable.");
            EnsureNormalDirectory(localOnlyRoot);
            EnsureContained(localOnlyRoot, pending.OriginalLocalOnlyPath, maximumSegments: 2);
            EnsureNormalParent(pending.OriginalLocalOnlyPath);
            if (!Path.GetExtension(pending.OriginalLocalOnlyPath)
                    .Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The edited-clip identity artifact is not an MP4 file.");
            }

            var identityFile = new FileInfo(pending.OriginalLocalOnlyPath);
            identityFile.Refresh();
            if (identityFile.LinkTarget is not null ||
                (identityFile.Exists && (identityFile.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new IOException("The edited-clip identity artifact is a linked file.");
            }
            if (identityFile.Exists)
            {
                var original = ClipEditProcessor.ValidateLocalOnlySource(
                    clipsFolder,
                    pending.OriginalLocalOnlyPath);
                if (!PathsEqual(original.FullName, pending.EditedPath))
                {
                    throw new IOException("The edited-clip identity artifact is outside Local only.");
                }
            }
            else if (!File.Exists(pending.DestinationPath))
            {
                throw new FileNotFoundException(
                    "The edited-clip identity artifact is missing from Local only and its archive destination.",
                    pending.OriginalLocalOnlyPath);
            }
        }
        else
        {
            var stagingRoot = Path.Combine(clipsFolder, ".clipcord-editing");
            EnsureContained(stagingRoot, pending.EditedPath, maximumSegments: 2);
            if (File.Exists(pending.EditedPath))
            {
                EnsureNormalDirectory(stagingRoot);
                EnsureNormalParent(pending.EditedPath);
                EnsureNormalFile(pending.EditedPath);
            }
            else if (Directory.Exists(stagingRoot))
            {
                EnsureNormalDirectory(stagingRoot);
            }
        }
    }

    private static void EnsureContained(string root, string path, int maximumSegments)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException("An edited-clip disposition path escaped its managed archive.");
        }
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 || segments.Length > maximumSegments)
        {
            throw new IOException("An edited-clip disposition path has an unexpected archive depth.");
        }
    }

    private static void EnsureNormalParent(string path)
    {
        var parent = Directory.GetParent(Path.GetFullPath(path))
            ?? throw new IOException("The edited-clip destination has no parent directory.");
        if (!parent.Exists || parent.LinkTarget is not null ||
            (parent.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("ClipCord will not archive an edited clip through a symbolic link or junction.");
        }
    }

    private static void EnsureNormalDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("ClipCord will not recover an edited clip through a symbolic link or junction.");
        }
    }

    private static void EnsureNormalFile(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("ClipCord will not recover an edited clip through a linked file.");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void DeleteStagedArtifact(PendingEditedClipDisposition pending)
    {
        File.Delete(pending.EditedPath);
        if (File.Exists(pending.EditedPath))
        {
            throw new IOException("The redundant staged edit could not be removed safely.");
        }
    }

    private static void TryDeleteOperationDirectory(string editedPath)
    {
        try
        {
            var operationDirectory = Path.GetDirectoryName(editedPath);
            var stagingDirectory = operationDirectory is null
                ? null
                : Path.GetDirectoryName(operationDirectory);
            if (operationDirectory is null || stagingDirectory is null ||
                !Path.GetFileName(stagingDirectory)
                    .Equals(".clipcord-editing", StringComparison.OrdinalIgnoreCase)) return;
            Directory.Delete(operationDirectory, recursive: false);
        }
        catch { }
    }
}

internal sealed class EditedClipUploadService(
    WatchStateStore? stateStore = null,
    Func<IManualDiscordUploader>? uploaderFactory = null,
    EditedClipDispositionProcessor? dispositionProcessor = null)
{
    private readonly WatchStateStore _stateStore = stateStore ?? new WatchStateStore();
    private readonly Func<IManualDiscordUploader> _uploaderFactory = uploaderFactory ?? (() => new ManualDiscordUploader());
    private readonly EditedClipDispositionProcessor _dispositionProcessor = dispositionProcessor ?? new EditedClipDispositionProcessor();

    internal async Task<ManualClipEditResult> UploadAsync(
        AppSettings settings,
        PreparedClipEdit prepared,
        ActivityHistoryStore? activityHistory,
        IProgress<ManualClipEditProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!WebhookValidation.IsDiscordWebhook(settings.WebhookUrl))
        {
            ClipEditProcessor.CleanupPreparedArtifact(prepared);
            throw new InvalidOperationException(
                "Add a valid Discord webhook in Settings before uploading a Local-only clip.");
        }
        if (!Path.GetFullPath(settings.ClipsFolder)
                .Equals(Path.GetFullPath(prepared.ClipsFolder), StringComparison.OrdinalIgnoreCase))
        {
            ClipEditProcessor.CleanupPreparedArtifact(prepared);
            throw new InvalidOperationException("The configured clips folder changed while the edit was open.");
        }

        var state = await _stateStore.LoadOrInitializeAsync(
            settings.ClipsFolder,
            _ => { },
            cancellationToken,
            settings.CaptureSource);
        if (state.UploadedContentHashes.Contains(prepared.EditedContentHash))
        {
            ClipEditProcessor.CleanupPreparedArtifact(prepared);
            activityHistory?.Transition(new ClipActivityUpdate(
                prepared.OriginalPath,
                ClipActivityState.Archived,
                GameName: prepared.GameName,
                OriginalBytes: prepared.OutputBytes,
                Route: ClipActivityRoute.Duplicate,
                Detail: "This edited content was already uploaded; Discord was not posted again.",
                ClearError: true));
            return new ManualClipEditResult(
                string.Empty,
                OriginalKept: true,
                OriginalCleanupFailed: false,
                AlreadyUploaded: true);
        }

        var currentEditedHash = await ContentIdentity.ComputeSha256Async(
            prepared.EditedPath,
            cancellationToken);
        if (!currentEditedHash.Equals(prepared.EditedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            ClipEditProcessor.CleanupPreparedArtifact(prepared);
            throw new IOException("The prepared edit changed before upload.");
        }

        PendingEditedClipDisposition pending;
        try
        {
            var archiveFolder = UploadedFolder.GetOrCreateForGame(settings.ClipsFolder, prepared.GameName);
            var destinationPath = UploadedFolder.GetUniqueDestination(archiveFolder, prepared.OutputFileName);
            var original = ClipEditProcessor.ValidateLocalOnlySource(
                settings.ClipsFolder,
                prepared.OriginalPath);
            var currentOriginalHash = await ContentIdentity.ComputeSha256Async(
                original.FullName,
                cancellationToken);
            if (!currentOriginalHash.Equals(prepared.OriginalContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The Local-only original changed before upload.");
            }
            pending = new PendingEditedClipDisposition
            {
                Id = prepared.OperationId,
                ClipsFolder = prepared.ClipsFolder,
                EditedPath = prepared.EditedPath,
                DestinationPath = destinationPath,
                OriginalLocalOnlyPath = prepared.OriginalPath,
                EditedContentHash = prepared.EditedContentHash,
                OriginalContentHash = prepared.OriginalContentHash,
                KeepOriginal = prepared.KeepOriginal,
                OutputBytes = prepared.OutputBytes
            };
            EditedClipDispositionProcessor.ValidatePendingPaths(pending);
        }
        catch
        {
            ClipEditProcessor.CleanupPreparedArtifact(prepared);
            throw;
        }
        var uploadConfirmed = false;
        activityHistory?.Transition(new ClipActivityUpdate(
            prepared.OriginalPath,
            ClipActivityState.Discovered,
            GameName: prepared.GameName,
            OriginalBytes: prepared.OutputBytes,
            Detail: "Manual Local-only edit selected for Discord."));
        try
        {
            progress?.Report(new ManualClipEditProgress(
                ManualClipEditStage.Uploading,
                "Uploading the edited clip to Discord…",
                prepared.OutputBytes));
            activityHistory?.Transition(new ClipActivityUpdate(
                prepared.OriginalPath,
                ClipActivityState.Uploading,
                GameName: prepared.GameName,
                OriginalBytes: prepared.OutputBytes,
                IncrementAttempt: true,
                Detail: "Uploading a user-edited Local-only clip."));
            cancellationToken.ThrowIfCancellationRequested();
            using var uploader = _uploaderFactory();
            // Each individual bounded webhook POST is allowed to finish once it starts,
            // while this token still cancels probing, compression, and later attempts.
            // That avoids an ambiguous accepted-but-locally-cancelled result without
            // making FFmpeg work non-cancellable.
            await uploader.UploadAsync(
                settings.WebhookUrl,
                prepared.EditedPath,
                new DiscordUploadPresentation(
                    prepared.OutputFileName,
                    prepared.GameName,
                    prepared.DiscordNote),
                settings.CompressionTargetMb,
                settings.UploaderName,
                cancellationToken,
                compression =>
                {
                    progress?.Report(new ManualClipEditProgress(
                        compression.CompressedBytes is null
                            ? ManualClipEditStage.Compressing
                            : ManualClipEditStage.Uploading,
                        compression.CompressedBytes is null
                            ? $"Compressing toward the {compression.TargetMb} MB Discord ceiling…"
                            : "Compression complete; resuming the Discord upload…",
                        compression.CompressedBytes,
                        compression.TargetMb));
                    activityHistory?.Transition(new ClipActivityUpdate(
                        prepared.OriginalPath,
                        ClipActivityState.Compressing,
                        GameName: prepared.GameName,
                        OriginalBytes: compression.OriginalBytes,
                        CompressedBytes: compression.CompressedBytes,
                        CompressionTargetMb: compression.TargetMb,
                        VideoKbps: compression.VideoKbps,
                        AudioKbps: compression.AudioKbps,
                        Detail: compression.CompressedBytes is null
                            ? $"Encoding toward the {compression.TargetMb} MB ceiling."
                            : "Compression complete; preparing the Discord upload.",
                        ResetCompression: compression.CompressedBytes is null));
                });
            uploadConfirmed = true;

            // Once Discord confirms success, this short commit is deliberately non-cancellable.
            // The recovery record is flushed before any archive move or original cleanup.
            state.KnownContentHashes.Add(prepared.EditedContentHash);
            state.UploadedContentHashes.Add(prepared.EditedContentHash);
            state.PendingEditedUploads.RemoveAll(item => item.Id == pending.Id);
            state.PendingEditedUploads.Add(pending);
            _stateStore.Save(state);

            activityHistory?.Transition(new ClipActivityUpdate(
                prepared.OriginalPath,
                ClipActivityState.Completed,
                GameName: prepared.GameName,
                OriginalBytes: prepared.OutputBytes,
                Route: ClipActivityRoute.Uploaded,
                Detail: "Discord accepted the edited clip; completing the local archive.",
                ClearError: true));
            progress?.Report(new ManualClipEditProgress(
                ManualClipEditStage.Archiving,
                "Discord accepted the clip. Finishing the local archive…",
                prepared.OutputBytes));

            var disposition = await _dispositionProcessor.CompleteAsync(pending, CancellationToken.None);
            state.PendingEditedUploads.RemoveAll(item => item.Id == pending.Id);
            _stateStore.Save(state);
            activityHistory?.Transition(new ClipActivityUpdate(
                prepared.OriginalPath,
                ClipActivityState.Archived,
                GameName: prepared.GameName,
                OriginalBytes: prepared.OutputBytes,
                CurrentPath: disposition.ArchivedPath,
                Route: ClipActivityRoute.Uploaded,
                Detail: disposition.OriginalCleanupFailed
                    ? "Edited clip uploaded and archived; the Local-only original was kept because Recycle Bin cleanup failed."
                    : prepared.KeepOriginal
                        ? "Edited clip uploaded and archived; the Local-only original was kept by choice."
                        : "Edited clip uploaded and archived; the Local-only original was removed safely.",
                ClearError: true));
            progress?.Report(new ManualClipEditProgress(
                ManualClipEditStage.Completed,
                disposition.OriginalCleanupFailed
                    ? "Uploaded. The original could not be moved to the Recycle Bin."
                    : "Edited clip uploaded and archived.",
                prepared.OutputBytes));
            return new ManualClipEditResult(
                disposition.ArchivedPath,
                disposition.OriginalKept,
                disposition.OriginalCleanupFailed);
        }
        catch (Exception exception) when (!uploadConfirmed)
        {
            ClipEditProcessor.CleanupPreparedArtifact(prepared);
            activityHistory?.Transition(new ClipActivityUpdate(
                prepared.OriginalPath,
                ClipActivityState.Failed,
                GameName: prepared.GameName,
                OriginalBytes: prepared.OutputBytes,
                Detail: exception is TimeoutException
                    ? "Discord's response was uncertain. Check the channel before retrying."
                    : "Manual edited-clip upload failed; the Local-only original was not changed.",
                Error: exception.Message));
            throw;
        }
        catch (Exception exception)
        {
            activityHistory?.Transition(new ClipActivityUpdate(
                prepared.OriginalPath,
                ClipActivityState.Retrying,
                GameName: prepared.GameName,
                OriginalBytes: prepared.OutputBytes,
                Route: ClipActivityRoute.Uploaded,
                Detail: "Discord accepted the edit; archive recovery will finish without another upload.",
                Error: exception.Message));
            throw new ManualClipCommitPendingException(
                "Discord accepted the edited clip, but local archiving is still pending. ClipCord will recover it without uploading again.",
                exception);
        }
    }
}

internal sealed class ManualClipCommitPendingException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
