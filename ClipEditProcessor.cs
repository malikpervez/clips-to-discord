using System.Globalization;

namespace ClipsToDiscord;

internal sealed class ClipEditProcessor
{
    private const double MinimumSelectionSeconds = 0.25;
    private const string StagingFolderName = ".clipcord-editing";
    private const int MaximumOutputFileNameLength = 255;

    internal async Task<ClipMediaInfo> ProbeAsync(
        string clipsFolder,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var source = ValidateLocalOnlySource(clipsFolder, sourcePath);
        var ffmpeg = FfmpegCompressor.FindExecutable()
            ?? throw new InvalidOperationException("ClipCord's bundled FFmpeg tool is unavailable.");
        var duration = await FfmpegCompressor.ProbeDurationAsync(source.FullName, ffmpeg, cancellationToken);
        return new ClipMediaInfo(duration, source.Length);
    }

    internal long EstimateOutputBytes(
        long sourceBytes,
        TimeSpan sourceDuration,
        TimeSpan selectionDuration)
    {
        if (sourceBytes <= 0 || sourceDuration <= TimeSpan.Zero || selectionDuration <= TimeSpan.Zero) return 0;
        var ratio = Math.Clamp(
            selectionDuration.TotalSeconds / sourceDuration.TotalSeconds,
            0,
            1);
        // CRF-based re-encoding can produce larger files than the source segment
        // (especially on short trims), so the estimate is deliberately conservative.
        const double crfConservativeMultiplier = 3.0;
        var conservativeBytes = sourceBytes * ratio * crfConservativeMultiplier;
        var slack = sourceBytes <= 10_000_000 ? 1024 * 1024 : sourceBytes * 0.0025;
        var estimate = Math.Ceiling(conservativeBytes + slack);
        if (estimate >= long.MaxValue)
        {
            return long.MaxValue;
        }
        return Math.Max(1, (long)estimate);
    }

    internal async Task<string> CreatePreviewFrameAsync(
        string clipsFolder,
        string sourcePath,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        var source = ValidateLocalOnlySource(clipsFolder, sourcePath);
        var ffmpeg = FfmpegCompressor.FindExecutable()
            ?? throw new InvalidOperationException("ClipCord's bundled FFmpeg tool is unavailable.");
        var previewFolder = Path.Combine(Path.GetTempPath(), "ClipsToDiscord", "previews");
        Directory.CreateDirectory(previewFolder);
        var previewPath = Path.Combine(previewFolder, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await FfmpegCompressor.RunAsync(
                ffmpeg,
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", FormatSeconds(Math.Max(0, position.TotalSeconds)),
                    "-i", source.FullName,
                    "-frames:v", "1",
                    "-vf", "scale='min(960,iw)':-2",
                    previewPath
                ],
                cancellationToken);
            if (!File.Exists(previewPath))
            {
                throw new InvalidOperationException("FFmpeg did not create a preview frame.");
            }
            return previewPath;
        }
        catch
        {
            TryDelete(previewPath);
            throw;
        }
    }

    internal async Task<PreparedClipEdit> PrepareAsync(
        string clipsFolder,
        ManualClipEditRequest request,
        IProgress<ManualClipEditProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report(new ManualClipEditProgress(
            ManualClipEditStage.Inspecting,
            "Inspecting the Local-only source…"));
        var source = ValidateLocalOnlySource(clipsFolder, request.Source.Path);
        ValidateOutputFileName(request.OutputFileName);
        var sanitizedGame = UploadedFolder.SanitizeGameFolderName(request.GameName);
        var media = await ProbeAsync(clipsFolder, source.FullName, cancellationToken);
        ValidateSelection(request.TrimStart, request.TrimEnd, media.Duration);
        var originalFileKey = WatchStateStore.FileKey(source);
        var originalHash = await ContentIdentity.ComputeSha256Async(source.FullName, cancellationToken);
        var operationId = Guid.NewGuid();
        var noMediaChanges = request.TrimStart <= TimeSpan.FromMilliseconds(50) &&
                             media.Duration - request.TrimEnd <= TimeSpan.FromMilliseconds(50) &&
                             !request.MuteAudio;
        if (noMediaChanges && !request.KeepOriginal)
        {
            return new PreparedClipEdit(
                operationId,
                Path.GetFullPath(clipsFolder),
                source.FullName,
                originalHash,
                source.FullName,
                originalHash,
                request.OutputFileName,
                sanitizedGame,
                DiscordClipMessage.NormalizeNote(request.DiscordNote),
                request.KeepOriginal,
                UsesOriginalAsArtifact: true,
                source.Length);
        }

        var operationFolder = CreateOperationFolder(clipsFolder, operationId);
        var stagedPath = Path.Combine(operationFolder, request.OutputFileName);
        try
        {
            if (noMediaChanges)
            {
                progress?.Report(new ManualClipEditProgress(
                    ManualClipEditStage.Rendering,
                    "Preparing a high-quality local copy…"));
                File.Copy(source.FullName, stagedPath, overwrite: false);
            }
            else
            {
                progress?.Report(new ManualClipEditProgress(
                    ManualClipEditStage.Rendering,
                    "Rendering the selected trim at high quality…"));
                await RenderAsync(source.FullName, stagedPath, request, cancellationToken);
            }

            source.Refresh();
            if (!source.Exists || !WatchStateStore.FileKey(source).Equals(originalFileKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The Local-only source changed while ClipCord was preparing the edit.");
            }
            var editedHash = await ContentIdentity.ComputeSha256Async(stagedPath, cancellationToken);
            var outputBytes = new FileInfo(stagedPath).Length;
            progress?.Report(new ManualClipEditProgress(
                ManualClipEditStage.Rendering,
                $"Edit ready ({GalleryView.FormatBytes(outputBytes)}).",
                outputBytes));
            return new PreparedClipEdit(
                operationId,
                Path.GetFullPath(clipsFolder),
                source.FullName,
                originalHash,
                stagedPath,
                editedHash,
                request.OutputFileName,
                sanitizedGame,
                DiscordClipMessage.NormalizeNote(request.DiscordNote),
                request.KeepOriginal,
                UsesOriginalAsArtifact: false,
                outputBytes);
        }
        catch
        {
            TryDelete(stagedPath);
            TryDeleteEmptyDirectory(operationFolder);
            throw;
        }
    }

    internal static void CleanupPreparedArtifact(PreparedClipEdit prepared)
    {
        if (prepared.UsesOriginalAsArtifact) return;
        TryDelete(prepared.EditedPath);
        var operationFolder = Path.GetDirectoryName(prepared.EditedPath);
        if (operationFolder is not null) TryDeleteEmptyDirectory(operationFolder);
    }

    internal static FileInfo ValidateLocalOnlySource(string clipsFolder, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var localOnlyRoot = UploadedFolder.FindExistingLocalOnly(Path.GetFullPath(clipsFolder))
            ?? throw new InvalidOperationException("The Local-only archive is unavailable.");
        EnsureNormalDirectory(localOnlyRoot);
        var fullSource = Path.GetFullPath(sourcePath);
        var relative = Path.GetRelativePath(localOnlyRoot, fullSource);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only clips inside this clips folder's Local-only archive can be edited.");
        }
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 or > 2)
        {
            throw new InvalidOperationException("The Local-only clip path is deeper than ClipCord's game archive layout.");
        }
        if (segments.Length == 2)
        {
            EnsureNormalDirectory(Path.Combine(localOnlyRoot, segments[0]));
        }
        var file = new FileInfo(fullSource);
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected Local-only clip is unavailable or unsafe to edit.");
        }
        return file;
    }

    private static async Task RenderAsync(
        string sourcePath,
        string stagedPath,
        ManualClipEditRequest request,
        CancellationToken cancellationToken)
    {
        var ffmpeg = FfmpegCompressor.FindExecutable()
            ?? throw new InvalidOperationException("ClipCord's bundled FFmpeg tool is unavailable.");
        var arguments = BuildRenderArguments(sourcePath, stagedPath, request);
        await FfmpegCompressor.RunAsync(ffmpeg, arguments, cancellationToken);
        if (!File.Exists(stagedPath) || new FileInfo(stagedPath).Length == 0)
        {
            throw new InvalidOperationException("FFmpeg did not create the edited clip.");
        }
    }

    internal static IReadOnlyList<string> BuildRenderArguments(
        string sourcePath,
        string stagedPath,
        ManualClipEditRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentNullException.ThrowIfNull(request);
        var selectionDuration = request.TrimEnd - request.TrimStart;
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", FormatSeconds(request.TrimStart.TotalSeconds),
            "-i", sourcePath,
            "-t", FormatSeconds(selectionDuration.TotalSeconds),
            "-map", "0:v:0"
        };
        if (request.MuteAudio)
        {
            arguments.AddRange(["-an"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:a:0?"]);
        }
        arguments.AddRange(["-vf", "setpts=PTS-STARTPTS"]);
        if (!request.MuteAudio)
        {
            arguments.AddRange(["-af", "asetpts=PTS-STARTPTS"]);
        }
        arguments.AddRange([
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18"
        ]);
        if (!request.MuteAudio)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }
        arguments.AddRange([
            "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn",
            "-f", "mp4", stagedPath
        ]);
        return arguments;
    }

    private static string CreateOperationFolder(string clipsFolder, Guid operationId)
    {
        var root = Path.Combine(Path.GetFullPath(clipsFolder), StagingFolderName);
        if (Directory.Exists(root)) EnsureNormalDirectory(root);
        else Directory.CreateDirectory(root);
        EnsureNormalDirectory(root);
        var operationFolder = Path.Combine(root, operationId.ToString("N"));
        Directory.CreateDirectory(operationFolder);
        EnsureNormalDirectory(operationFolder);
        return operationFolder;
    }

    private static void EnsureNormalDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("ClipCord will not use a symbolic link or junction for edit storage.");
        }
    }

    internal static void ValidateOutputFileName(string outputFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFileName);
        if (outputFileName.Length > MaximumOutputFileNameLength ||
            outputFileName.Trim() != outputFileName ||
            outputFileName.TrimEnd('.', ' ') != outputFileName ||
            !Path.GetFileName(outputFileName).Equals(outputFileName, StringComparison.Ordinal) ||
            outputFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !Path.GetExtension(outputFileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The edited clip name must be a valid MP4 file name.", nameof(outputFileName));
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(outputFileName);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension) || IsReservedDosDeviceName(nameWithoutExtension))
        {
            throw new ArgumentException(
                "The edited clip name must not be reserved or empty.",
                nameof(outputFileName));
        }
    }

    internal static bool IsReservedDosDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var trimmed = value.Trim('.', ' ');
        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length == 4 &&
            int.TryParse(trimmed.AsSpan(3).ToString(), out var comIndex) &&
            comIndex is >= 1 and <= 9)
        {
            return true;
        }

        return trimmed.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Length == 4 &&
               int.TryParse(trimmed.AsSpan(3).ToString(), out var lptIndex) &&
               lptIndex is >= 1 and <= 9;
    }

    private static void ValidateSelection(TimeSpan start, TimeSpan end, TimeSpan duration)
    {
        if (start < TimeSpan.Zero || end <= start ||
            (end - start).TotalSeconds < MinimumSelectionSeconds ||
            end > duration + TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Choose a valid trim range inside the clip.");
        }
    }

    private static string FormatSeconds(double value) =>
        value.ToString("F6", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try { Directory.Delete(path, recursive: false); } catch { }
    }
}
