namespace ClipsToDiscord;

/// <summary>
/// Connects the capture pipeline to the durable routing journal. The promotion intent is written
/// before the completed MP4 leaves private staging, so startup reconciliation can finish the
/// exact move/journal transaction after any process interruption.
/// </summary>
internal static class CaptureJournalCaptureCommit
{
    internal static async Task<CaptureJournalDocument> PromoteOriginalAsync(
        string libraryRoot,
        string stagedPath,
        string finalPath,
        CaptureJournalSourceKind sourceKind,
        string gameName,
        DateTimeOffset capturedUtc,
        TimeSpan duration,
        int width,
        int height,
        CaptureSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = CaptureSettings.Normalize(settings);
        var cameraRequested = normalized.IncludeReactionCamera &&
                              !string.IsNullOrWhiteSpace(normalized.CameraDeviceId);
        var requestedRenditions = cameraRequested
            ? GetRequestedRenditions(normalized)
            : [];
        var intent = await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
                libraryRoot,
                stagedPath,
                finalPath,
                sourceKind,
                gameName,
                capturedUtc,
                duration,
                width,
                height,
                cameraRequested,
                requestedRenditions,
                cancellationToken)
            .ConfigureAwait(false);
        var promoted = await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
                libraryRoot,
                intent.ClipId,
                cancellationToken)
            .ConfigureAwait(false);
        if (promoted.Status is not CaptureJournalPromotionStatus.DestinationReady and
            not CaptureJournalPromotionStatus.AlreadyJournaled)
        {
            throw new InvalidDataException(
                $"The completed capture could not be promoted safely ({promoted.Status}).");
        }

        var journal = await CaptureJournalPromotionIntentStore.CommitOriginalAsync(
                libraryRoot,
                intent.ClipId,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await CaptureJournalPromotionIntentStore.CompleteOriginalAsync(
                    libraryRoot,
                    intent.ClipId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The committed journal is authoritative. A leftover intent is safe and is removed by
            // bounded startup reconciliation after it proves the same fingerprint again.
            Log.Error("ClipCord could not clean a completed capture promotion intent.", exception);
        }
        return journal;
    }

    /// <summary>
    /// Projects the optional camera transaction into the source journal after gameplay is already
    /// durable. Missing optional camera media becomes an explicit stable failure instead of an
    /// indefinitely pending rendition.
    /// </summary>
    internal static async Task<CaptureJournalDocument> FinalizeCameraAsync(
        string libraryRoot,
        string clipId,
        CaptureProjectSaveResult? cameraProject,
        CancellationToken cancellationToken = default)
    {
        var load = CaptureJournalStore.Load(libraryRoot, clipId, cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null)
        {
            throw new InvalidDataException("The completed capture journal is unavailable.");
        }
        var current = load.Document;
        if (!current.Clip.ReactionCameraRequested) return current;
        if (current.State is CaptureJournalState.RenditionsReady or
            CaptureJournalState.RenditionsFailed)
        {
            return current;
        }
        if (current.State == CaptureJournalState.OriginalCommitted)
        {
            current = await CaptureJournalStore.BeginCameraAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (cameraProject is null)
        {
            return await CaptureJournalStore.FailRenditionsAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    "camera-layer-unavailable",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (!cameraProject.ProjectId.Equals(clipId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Reaction Camera project does not match its gameplay journal.");
        }
        return await FinalizeCameraPathAsync(
                libraryRoot,
                current,
                cameraProject.CameraLayerPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<CaptureJournalDocument?> ReconcileCameraProjectAsync(
        string libraryRoot,
        string clipId,
        bool failIfProjectMissing,
        CancellationToken cancellationToken = default,
        Action? beforeMutation = null)
    {
        var load = CaptureJournalStore.Load(libraryRoot, clipId, cancellationToken);
        if (!load.LoadedFromDisk || load.Document is null) return null;
        var current = load.Document;
        if (!current.Clip.ReactionCameraRequested ||
            current.State is CaptureJournalState.RenditionsReady or
                CaptureJournalState.RenditionsFailed)
        {
            return current;
        }
        var project = CaptureProjectStore.LoadProjectIndex(libraryRoot, cancellationToken)
            .Values.SingleOrDefault(candidate =>
                candidate.ProjectId.Equals(clipId, StringComparison.Ordinal));
        // Recovery runs concurrently with the capture host. A journal created by this app
        // process may be visible before its optional camera project is atomically promoted.
        // Only records that predate the process recovery boundary can treat absence as an
        // abandoned camera transaction.
        if (project is null && !failIfProjectMissing) return current;
        return await FinalizeCameraPathAsync(
                libraryRoot,
                current,
                project?.CameraLayerPath,
                cancellationToken,
                beforeMutation)
            .ConfigureAwait(false);
    }

    private static async Task<CaptureJournalDocument> FinalizeCameraPathAsync(
        string libraryRoot,
        CaptureJournalDocument current,
        string? cameraLayerPath,
        CancellationToken cancellationToken,
        Action? beforeMutation = null)
    {
        var clipId = current.Clip.ClipId;
        if (current.State == CaptureJournalState.OriginalCommitted)
        {
            RequireMutationAllowed(cancellationToken, beforeMutation);
            current = await CaptureJournalStore.BeginCameraAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(cameraLayerPath))
        {
            RequireMutationAllowed(cancellationToken, beforeMutation);
            return await CaptureJournalStore.FailRenditionsAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    "camera-layer-unavailable",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        if (!current.CameraPresent)
        {
            var camera = await CaptureJournalStore.CreateArtifactAsync(
                    libraryRoot,
                    clipId,
                    CaptureJournalArtifactKinds.ReactionCamera,
                    cameraLayerPath,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireMutationAllowed(cancellationToken, beforeMutation);
            current = await CaptureJournalStore.AttachCameraAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    camera,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return current;
    }

    internal static async Task<CaptureJournalDocument?> ReconcileRenditionsAsync(
        string libraryRoot,
        string clipId,
        CancellationToken cancellationToken = default,
        bool processingSettled = false,
        Action? beforeMutation = null)
    {
        var journalLoad = CaptureJournalStore.Load(libraryRoot, clipId, cancellationToken);
        if (!journalLoad.LoadedFromDisk || journalLoad.Document is null) return null;
        var current = journalLoad.Document;
        if (current.State != CaptureJournalState.CameraPending || !current.CameraPresent)
        {
            return current;
        }

        var renditionLoad = SilhouetteRenditionStore.Load(
            libraryRoot,
            clipId,
            cancellationToken);
        if (!renditionLoad.LoadedFromDisk || renditionLoad.Document is null)
        {
            if (!processingSettled) return current;
            RequireMutationAllowed(cancellationToken, beforeMutation);
            return await CaptureJournalStore.FailRenditionsAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    "rendition-processing-failed",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        var renditionState = renditionLoad.Document;
        foreach (var output in renditionState.Outputs.Where(candidate =>
                     candidate.State == SilhouetteOutputState.Ready &&
                     candidate.FinalArtifact is not null))
        {
            var kind = output.OrientationId switch
            {
                CompositionOrientationIds.Landscape => CaptureJournalArtifactKinds.Landscape,
                CompositionOrientationIds.Portrait => CaptureJournalArtifactKinds.Portrait,
                _ => throw new InvalidDataException("The silhouette output orientation is unsupported.")
            };
            if (!current.Clip.RequestedRenditions.Contains(kind, StringComparer.Ordinal) ||
                current.Artifacts.Any(candidate => candidate.Kind.Equals(kind, StringComparison.Ordinal)))
            {
                continue;
            }
            var path = SilhouetteArtifactStore.GetOutputPath(
                libraryRoot,
                clipId,
                output.OrientationId);
            var artifact = await CaptureJournalStore.CreateArtifactAsync(
                    libraryRoot,
                    clipId,
                    kind,
                    path,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireMutationAllowed(cancellationToken, beforeMutation);
            current = await CaptureJournalStore.AttachRenditionAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    artifact,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var enabledOutputs = renditionState.Outputs
                .Where(output => output.State != SilhouetteOutputState.Disabled)
                .ToArray();
        var terminalFailure = renditionState.Matte.State == SilhouetteMatteState.Failed ||
                              enabledOutputs.All(output =>
                                  output.State is SilhouetteOutputState.Ready or
                                      SilhouetteOutputState.Failed) &&
                              enabledOutputs.Any(output =>
                                  output.State == SilhouetteOutputState.Failed) ||
                              processingSettled &&
                              renditionState.AggregateState !=
                              SilhouetteRenditionAggregateState.Ready;
        if (current.State == CaptureJournalState.CameraPending && terminalFailure)
        {
            RequireMutationAllowed(cancellationToken, beforeMutation);
            current = await CaptureJournalStore.FailRenditionsAsync(
                    libraryRoot,
                    clipId,
                    current.Generation,
                    "rendition-processing-failed",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return current;
    }

    private static void RequireMutationAllowed(
        CancellationToken cancellationToken,
        Action? beforeMutation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        beforeMutation?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<string> GetRequestedRenditions(CaptureSettings settings)
    {
        var requested = new List<string>(2);
        if (settings.SilhouetteLandscapeEnabled)
        {
            requested.Add(CaptureJournalArtifactKinds.Landscape);
        }
        if (settings.SilhouettePortraitEnabled)
        {
            requested.Add(CaptureJournalArtifactKinds.Portrait);
        }
        return requested;
    }
}
