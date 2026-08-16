namespace ClipsToDiscord;

internal sealed class ManualClipEditCoordinator(
    AppSettings settings,
    Func<PreparedClipEdit, IProgress<ManualClipEditProgress>?, CancellationToken, Task<ManualClipEditResult>> uploadPreparedAsync,
    ClipEditProcessor? processor = null) : IManualClipEditService
{
    private readonly ClipEditProcessor _processor = processor ?? new ClipEditProcessor();

    public Task<ClipMediaInfo> ProbeAsync(
        GalleryClipEntry source,
        CancellationToken cancellationToken)
    {
        ValidateSourceEntry(source);
        return _processor.ProbeAsync(settings.ClipsFolder, source.Path, cancellationToken);
    }

    public Task<string> CreatePreviewFrameAsync(
        GalleryClipEntry source,
        TimeSpan position,
        CancellationToken cancellationToken,
        int maxDimension = 960)
    {
        ValidateSourceEntry(source);
        return _processor.CreatePreviewFrameAsync(
            settings.ClipsFolder,
            source.Path,
            position,
            cancellationToken,
            maxDimension);
    }

    public Task<string> CreateTrimmedPlaybackAsync(
        GalleryClipEntry source,
        TimeSpan start,
        TimeSpan end,
        bool muteAudio,
        CancellationToken cancellationToken)
    {
        ValidateSourceEntry(source);
        return _processor.CreateTrimmedPlaybackAsync(
            settings.ClipsFolder,
            source.Path,
            start,
            end,
            muteAudio,
            cancellationToken);
    }

    public long EstimateOutputBytes(
        long sourceBytes,
        TimeSpan sourceDuration,
        TimeSpan selectionDuration) =>
        _processor.EstimateOutputBytes(sourceBytes, sourceDuration, selectionDuration);

    public async Task<ManualClipEditResult> EditAndUploadAsync(
        ManualClipEditRequest request,
        IProgress<ManualClipEditProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSourceEntry(request.Source);
        if (!WebhookValidation.IsDiscordWebhook(settings.WebhookUrl))
        {
            throw new InvalidOperationException("Add a valid Discord webhook in Settings before uploading a Local-only clip.");
        }
        PreparedClipEdit? prepared = null;
        try
        {
            prepared = await _processor.PrepareAsync(
                settings.ClipsFolder,
                request,
                progress,
                cancellationToken);
            return await uploadPreparedAsync(prepared, progress, cancellationToken);
        }
        catch (ManualClipCommitPendingException)
        {
            // The staged artifact is referenced by durable state and must remain for recovery.
            throw;
        }
        catch
        {
            if (prepared is not null) ClipEditProcessor.CleanupPreparedArtifact(prepared);
            throw;
        }
    }

    private static void ValidateSourceEntry(GalleryClipEntry source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Route != GalleryClipRoute.LocalOnly)
        {
            throw new InvalidOperationException("Only Local-only clips can enter the manual editor.");
        }
    }
}
