namespace ClipsToDiscord;

/// <summary>
/// Completes interrupted capture promotions and projects durable camera/rendition state into the
/// capture journal. Every operation is generation checked and safe to repeat after another crash.
/// </summary>
internal static class CaptureJournalStartupRecovery
{
    private const int PageSize = 128;

    internal static async Task RecoverAsync(
        string libraryRoot,
        DateTimeOffset existingJournalCutoffUtc,
        CancellationToken cancellationToken = default)
    {
        if (existingJournalCutoffUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The capture-journal recovery cutoff must be UTC.",
                nameof(existingJournalCutoffUtc));
        }
        var promotionHandler = new OriginalPromotionHandler(libraryRoot);
        string? cursor = null;
        do
        {
            var page = await CaptureJournalOriginalPromotionStartupReconciler.ReconcileAsync(
                    libraryRoot,
                    promotionHandler,
                    cancellationToken,
                    maximumEntries: PageSize,
                    maximumDuration: TimeSpan.FromSeconds(2),
                    afterClipId: cursor)
                .ConfigureAwait(false);
            cursor = page.EntryLimitReached ? page.NextCursor : null;
            if (!page.EntryLimitReached) break;
        } while (cursor is not null);

        var projectionHandler = new JournalProjectionHandler(
            libraryRoot,
            existingJournalCutoffUtc);
        cursor = null;
        do
        {
            var page = await CaptureJournalStartupReconciler.ReconcileAsync(
                    libraryRoot,
                    projectionHandler,
                    cancellationToken,
                    maximumEntries: PageSize,
                    maximumDuration: TimeSpan.FromSeconds(2),
                    afterClipId: cursor)
                .ConfigureAwait(false);
            cursor = page.EntryLimitReached ? page.NextCursor : null;
            if (!page.EntryLimitReached) break;
        } while (cursor is not null);
    }

    private sealed class OriginalPromotionHandler(
        string libraryRoot) : ICaptureJournalOriginalPromotionReconciliationHandler
    {
        public async ValueTask ReconcileAsync(
            CaptureJournalOriginalPromotionReconciliationItem item,
            CancellationToken cancellationToken)
        {
            try
            {
                var inspection = item.Inspection;
                if (inspection.Status == CaptureJournalPromotionStatus.MoveRequired)
                {
                    inspection = await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
                            libraryRoot,
                            item.ClipId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                if (inspection.Status == CaptureJournalPromotionStatus.DestinationReady)
                {
                    _ = await CaptureJournalPromotionIntentStore.CommitOriginalAsync(
                            libraryRoot,
                            item.ClipId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    inspection = await CaptureJournalPromotionIntentStore.InspectOriginalAsync(
                            libraryRoot,
                            item.ClipId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                if (inspection.Status == CaptureJournalPromotionStatus.AlreadyJournaled)
                {
                    await CaptureJournalPromotionIntentStore.CompleteOriginalAsync(
                            libraryRoot,
                            item.ClipId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or
                    CaptureJournalConcurrencyException)
            {
                Log.Error(
                    $"ClipCord could not recover capture promotion {item.ClipId}.",
                    exception);
            }
        }
    }

    private sealed class JournalProjectionHandler(
        string libraryRoot,
        DateTimeOffset existingJournalCutoffUtc) : ICaptureJournalReconciliationHandler
    {
        public async ValueTask ReconcileAsync(
            CaptureJournalReconciliationItem item,
            CancellationToken cancellationToken)
        {
            if (!item.MediaValidated || item.Document is null) return;
            try
            {
                var current = item.Document;
                if (current.State == CaptureJournalState.OriginalCommitted &&
                    current.Clip.ReactionCameraRequested)
                {
                    current = await CaptureJournalCaptureCommit.ReconcileCameraProjectAsync(
                            libraryRoot,
                            item.ClipId,
                            failIfProjectMissing:
                                current.CreatedUtc < existingJournalCutoffUtc,
                            cancellationToken)
                        .ConfigureAwait(false) ?? current;
                }
                if (current.State == CaptureJournalState.CameraPending)
                {
                    _ = await CaptureJournalCaptureCommit.ReconcileRenditionsAsync(
                            libraryRoot,
                            item.ClipId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or
                    CaptureJournalConcurrencyException)
            {
                Log.Error(
                    $"ClipCord could not reconcile capture journal {item.ClipId}.",
                    exception);
            }
        }
    }
}
