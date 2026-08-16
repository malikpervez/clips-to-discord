namespace ClipsToDiscord;

internal enum ManualClipEditStage
{
    Inspecting,
    Rendering,
    Uploading,
    Compressing,
    Archiving,
    RecyclingOriginal,
    Completed
}

internal sealed record ClipMediaInfo(TimeSpan Duration, long SourceBytes);

internal sealed record ManualClipEditRequest(
    GalleryClipEntry Source,
    TimeSpan TrimStart,
    TimeSpan TrimEnd,
    bool MuteAudio,
    string OutputFileName,
    string GameName,
    string? DiscordNote,
    bool KeepOriginal);

internal sealed record ManualClipEditProgress(
    ManualClipEditStage Stage,
    string Message,
    long? OutputBytes = null,
    int? CompressionTargetMb = null);

internal sealed record PreparedClipEdit(
    Guid OperationId,
    string ClipsFolder,
    string OriginalPath,
    string OriginalContentHash,
    string EditedPath,
    string EditedContentHash,
    string OutputFileName,
    string GameName,
    string? DiscordNote,
    bool KeepOriginal,
    bool UsesOriginalAsArtifact,
    long OutputBytes);

internal sealed record ManualClipEditResult(
    string ArchivedPath,
    bool OriginalKept,
    bool OriginalCleanupFailed,
    bool AlreadyUploaded = false);

internal interface IManualClipEditService
{
    Task<ClipMediaInfo> ProbeAsync(
        GalleryClipEntry source,
        CancellationToken cancellationToken);

    Task<string> CreatePreviewFrameAsync(
        GalleryClipEntry source,
        TimeSpan position,
        CancellationToken cancellationToken,
        int maxDimension = 960);

    Task<string> CreateTrimmedPlaybackAsync(
        GalleryClipEntry source,
        TimeSpan start,
        TimeSpan end,
        bool muteAudio,
        CancellationToken cancellationToken);

    long EstimateOutputBytes(
        long sourceBytes,
        TimeSpan sourceDuration,
        TimeSpan selectionDuration);

    Task<ManualClipEditResult> EditAndUploadAsync(
        ManualClipEditRequest request,
        IProgress<ManualClipEditProgress>? progress,
        CancellationToken cancellationToken);
}
