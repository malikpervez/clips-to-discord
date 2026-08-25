using System.Text.Json;

namespace ClipsToDiscord;

internal delegate Task<SilhouetteMatteAnalysis> SilhouetteMatteAnalyzer(
    string ffmpegPath,
    string mattePath,
    CancellationToken cancellationToken);

internal delegate Task SilhouetteOutputRenderer(
    string ffmpegPath,
    string gameplayPath,
    string cameraPath,
    string mattePath,
    string outputPath,
    CaptureProjectManifest manifest,
    CaptureCompositionDocument composition,
    SilhouetteMatteAnalysis analysis,
    string orientationId,
    CancellationToken cancellationToken);

internal sealed record SilhouetteProjectProcessorSeams(
    SilhouetteMatteAnalyzer AnalyzeMatteAsync,
    SilhouetteOutputRenderer RenderOutputAsync)
{
    internal static SilhouetteProjectProcessorSeams Production { get; } = new(
        SilhouetteMatteGenerator.AnalyzeAsync,
        SilhouetteCompositor.RenderAsync);
}

internal sealed class SilhouetteProjectProcessor
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _ffmpegPath;
    private readonly string _modelPath;
    private readonly bool _preferDirectMl;
    private readonly SilhouetteProjectProcessorSeams _seams;

    internal SilhouetteProjectProcessor(
        string ffmpegPath,
        string modelPath,
        bool preferDirectMl = true,
        SilhouetteProjectProcessorSeams? seams = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        _ffmpegPath = Path.GetFullPath(ffmpegPath);
        _modelPath = Path.GetFullPath(modelPath);
        _preferDirectMl = preferDirectMl;
        _seams = seams ?? SilhouetteProjectProcessorSeams.Production;
        ArgumentNullException.ThrowIfNull(_seams.AnalyzeMatteAsync);
        ArgumentNullException.ThrowIfNull(_seams.RenderOutputAsync);
    }

    internal async Task<SilhouetteRenditionDocument> ProcessAsync(
        string libraryRoot,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = LoadProject(libraryRoot, projectId, cancellationToken);
        var compositionLoad = CaptureCompositionStore.LoadOrDefault(libraryRoot, projectId);
        if (!compositionLoad.LoadedFromDisk)
        {
            throw new InvalidDataException(
                "The capture project does not have a valid silhouette layout snapshot.");
        }
        var composition = compositionLoad.Document;
        _ = SilhouetteArtifactStore.CleanupTemporaryArtifacts(
            libraryRoot,
            projectId,
            DateTimeOffset.UtcNow.AddHours(-24));

        var state = await LoadOrCreateStateAsync(
            libraryRoot,
            projectId,
            cancellationToken).ConfigureAwait(false);
        state = await RecoverInterruptedStateAsync(
            libraryRoot,
            projectId,
            state,
            cancellationToken).ConfigureAwait(false);
        state = await ValidateReadyOutputsAsync(
            libraryRoot,
            projectId,
            state,
            cancellationToken).ConfigureAwait(false);

        SilhouetteMatteAnalysis? analysis = null;
        if (state.Matte.State == SilhouetteMatteState.Pending)
        {
            (state, analysis) = await GenerateMatteAsync(
                libraryRoot,
                project,
                state,
                cancellationToken).ConfigureAwait(false);
        }
        if (state.Matte.State != SilhouetteMatteState.Ready ||
            state.Matte.FinalArtifact is null)
        {
            return state;
        }
        if (!state.Outputs.Any(output => output.State == SilhouetteOutputState.Pending))
        {
            return state;
        }

        var matteValidation = await SilhouetteArtifactStore.ValidateMatteAsync(
            libraryRoot,
            projectId,
            state.Matte.FinalArtifact,
            cancellationToken).ConfigureAwait(false);
        if (!matteValidation.IsValid)
        {
            throw new InvalidDataException(
                "The shared silhouette matte is missing or no longer matches its saved identity.");
        }
        analysis ??= await _seams.AnalyzeMatteAsync(
            _ffmpegPath,
            SilhouetteArtifactStore.GetMattePath(libraryRoot, projectId),
            cancellationToken).ConfigureAwait(false);

        foreach (var orientationId in CompositionOrientationIds.All)
        {
            var output = state.Outputs.Single(candidate =>
                candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
            if (output.State != SilhouetteOutputState.Pending) continue;
            state = await RenderOutputAsync(
                libraryRoot,
                project,
                composition,
                analysis,
                state,
                orientationId,
                cancellationToken).ConfigureAwait(false);
        }
        return state;
    }

    private async Task<SilhouetteRenditionDocument> LoadOrCreateStateAsync(
        string libraryRoot,
        string projectId,
        CancellationToken cancellationToken)
    {
        var load = SilhouetteRenditionStore.Load(
            libraryRoot,
            projectId,
            cancellationToken);
        if (load.Status == SilhouetteRenditionLoadStatus.Missing)
        {
            var initial = SilhouetteRenditionStore.CreateInitial(
                libraryRoot,
                projectId,
                OnnxSilhouetteFrameSegmenter.ModelId,
                OnnxSilhouetteFrameSegmenter.ModelSha256,
                cancellationToken: cancellationToken);
            return await SilhouetteRenditionStore.SaveAsync(
                libraryRoot,
                projectId,
                initial,
                expectedGeneration: 0,
                cancellationToken).ConfigureAwait(false);
        }
        if (!load.LoadedFromDisk || load.Document is null)
        {
            throw new InvalidDataException(
                $"The silhouette processing state is not usable ({load.Status}).");
        }
        if (!load.Document.ModelId.Equals(
                OnnxSilhouetteFrameSegmenter.ModelId,
                StringComparison.Ordinal) ||
            !load.Document.ModelSha256.Equals(
                OnnxSilhouetteFrameSegmenter.ModelSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The capture project belongs to a different silhouette model revision.");
        }
        return load.Document;
    }

    private static async Task<SilhouetteRenditionDocument> RecoverInterruptedStateAsync(
        string libraryRoot,
        string projectId,
        SilhouetteRenditionDocument state,
        CancellationToken cancellationToken)
    {
        if (state.Matte.State == SilhouetteMatteState.Committing)
        {
            var expected = state.Matte.ExpectedTemporaryArtifact ??
                throw new InvalidDataException("The committing matte identity is missing.");
            var validation = await SilhouetteArtifactStore.ValidateMatteAsync(
                libraryRoot,
                projectId,
                expected,
                cancellationToken).ConfigureAwait(false);
            var candidate = SilhouetteRenditionModel.RecoverMatteCommit(
                state,
                state.Matte.AttemptGeneration,
                validation.IsValid ? validation.ObservedFingerprint : null,
                DateTimeOffset.UtcNow);
            state = await SaveAsync(
                libraryRoot,
                projectId,
                state,
                candidate,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var orientationId in CompositionOrientationIds.All)
        {
            var output = state.Outputs.Single(candidate =>
                candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
            if (output.State != SilhouetteOutputState.Committing) continue;
            var expected = output.ExpectedTemporaryArtifact ??
                throw new InvalidDataException("The committing rendition identity is missing.");
            var validation = await SilhouetteArtifactStore.ValidateOutputAsync(
                libraryRoot,
                projectId,
                orientationId,
                expected,
                cancellationToken).ConfigureAwait(false);
            var candidate = SilhouetteRenditionModel.RecoverOutputCommit(
                state,
                orientationId,
                output.AttemptGeneration,
                validation.IsValid ? validation.ObservedFingerprint : null,
                DateTimeOffset.UtcNow);
            state = await SaveAsync(
                libraryRoot,
                projectId,
                state,
                candidate,
                cancellationToken).ConfigureAwait(false);
        }

        var recovered = SilhouetteRenditionModel.RecoverAbandonedWork(
            state,
            DateTimeOffset.UtcNow);
        if (!ReferenceEquals(recovered, state))
        {
            state = await SaveAsync(
                libraryRoot,
                projectId,
                state,
                recovered,
                cancellationToken).ConfigureAwait(false);
        }
        return state;
    }

    private static async Task<SilhouetteRenditionDocument> ValidateReadyOutputsAsync(
        string libraryRoot,
        string projectId,
        SilhouetteRenditionDocument state,
        CancellationToken cancellationToken)
    {
        foreach (var orientationId in CompositionOrientationIds.All)
        {
            var output = state.Outputs.Single(candidate =>
                candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
            if (output.State != SilhouetteOutputState.Ready ||
                output.FinalArtifact is null)
            {
                continue;
            }
            var validation = await SilhouetteArtifactStore.ValidateOutputAsync(
                libraryRoot,
                projectId,
                orientationId,
                output.FinalArtifact,
                cancellationToken).ConfigureAwait(false);
            if (!validation.IsDefinitiveFailure) continue;
            var invalid = SilhouetteRenditionModel.InvalidateReadyOutput(
                state,
                orientationId,
                validation.ObservedFingerprint,
                DateTimeOffset.UtcNow);
            state = await SaveAsync(
                libraryRoot,
                projectId,
                state,
                invalid,
                cancellationToken).ConfigureAwait(false);
        }
        return state;
    }

    private async Task<(SilhouetteRenditionDocument State, SilhouetteMatteAnalysis? Analysis)>
        GenerateMatteAsync(
            string libraryRoot,
            LoadedProject project,
            SilhouetteRenditionDocument state,
            CancellationToken cancellationToken)
    {
        var processing = SilhouetteRenditionModel.StartMatte(
            state,
            DateTimeOffset.UtcNow);
        state = await SaveAsync(
            libraryRoot,
            project.Manifest.ProjectId,
            state,
            processing,
            cancellationToken).ConfigureAwait(false);
        var attemptGeneration = state.Matte.AttemptGeneration;
        var temporaryPath = SilhouetteArtifactStore.CreateMatteTemporaryPath(
            libraryRoot,
            project.Manifest.ProjectId,
            attemptGeneration);
        try
        {
            using var segmenter = new OnnxSilhouetteFrameSegmenter(
                _modelPath,
                _preferDirectMl);
            var analysis = await SilhouetteMatteGenerator.GenerateAsync(
                _ffmpegPath,
                project.CameraPath,
                temporaryPath,
                segmenter,
                cancellationToken).ConfigureAwait(false);
            var fingerprint = await SilhouetteArtifactStore.FingerprintTemporaryAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            var committing = SilhouetteRenditionModel.BeginMatteCommit(
                state,
                attemptGeneration,
                fingerprint,
                DateTimeOffset.UtcNow);
            state = await SaveAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                state,
                committing,
                cancellationToken).ConfigureAwait(false);
            var finalFingerprint = await SilhouetteArtifactStore.PromoteMatteAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                attemptGeneration,
                temporaryPath,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            var ready = SilhouetteRenditionModel.CompleteMatte(
                state,
                attemptGeneration,
                finalFingerprint,
                DateTimeOffset.UtcNow);
            state = await SaveAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                state,
                ready,
                cancellationToken).ConfigureAwait(false);
            return (state, analysis);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not create the shared silhouette matte.", exception);
            SilhouetteArtifactStore.DiscardOwnedTemporary(
                libraryRoot,
                project.Manifest.ProjectId,
                temporaryPath);
            state = await TryFailMatteAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                state,
                attemptGeneration,
                cancellationToken).ConfigureAwait(false);
            return (state, null);
        }
    }

    private async Task<SilhouetteRenditionDocument> RenderOutputAsync(
        string libraryRoot,
        LoadedProject project,
        CaptureCompositionDocument composition,
        SilhouetteMatteAnalysis analysis,
        SilhouetteRenditionDocument state,
        string orientationId,
        CancellationToken cancellationToken)
    {
        var rendering = SilhouetteRenditionModel.StartOutput(
            state,
            orientationId,
            DateTimeOffset.UtcNow);
        state = await SaveAsync(
            libraryRoot,
            project.Manifest.ProjectId,
            state,
            rendering,
            cancellationToken).ConfigureAwait(false);
        var output = state.Outputs.Single(candidate =>
            candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
        var attemptGeneration = output.AttemptGeneration;
        var temporaryPath = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            libraryRoot,
            project.Manifest.ProjectId,
            orientationId,
            attemptGeneration);
        try
        {
            await _seams.RenderOutputAsync(
                _ffmpegPath,
                project.GameplayPath,
                project.CameraPath,
                SilhouetteArtifactStore.GetMattePath(
                    libraryRoot,
                    project.Manifest.ProjectId),
                temporaryPath,
                project.Manifest,
                composition,
                analysis,
                orientationId,
                cancellationToken).ConfigureAwait(false);
            var fingerprint = await SilhouetteArtifactStore.FingerprintTemporaryAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            var committing = SilhouetteRenditionModel.BeginOutputCommit(
                state,
                orientationId,
                attemptGeneration,
                fingerprint,
                DateTimeOffset.UtcNow);
            state = await SaveAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                state,
                committing,
                cancellationToken).ConfigureAwait(false);
            var finalFingerprint = await SilhouetteArtifactStore.PromoteOutputAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                orientationId,
                attemptGeneration,
                temporaryPath,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            var ready = SilhouetteRenditionModel.CompleteOutput(
                state,
                orientationId,
                attemptGeneration,
                finalFingerprint,
                DateTimeOffset.UtcNow);
            return await SaveAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                state,
                ready,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Error($"ClipCord could not render the {orientationId} silhouette output.", exception);
            SilhouetteArtifactStore.DiscardOwnedTemporary(
                libraryRoot,
                project.Manifest.ProjectId,
                temporaryPath);
            return await TryFailOutputAsync(
                libraryRoot,
                project.Manifest.ProjectId,
                state,
                orientationId,
                attemptGeneration,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<SilhouetteRenditionDocument> TryFailMatteAsync(
        string libraryRoot,
        string projectId,
        SilhouetteRenditionDocument state,
        long attemptGeneration,
        CancellationToken cancellationToken)
    {
        if (state.Matte.State is not (SilhouetteMatteState.Processing or
            SilhouetteMatteState.Committing) ||
            state.Matte.AttemptGeneration != attemptGeneration)
        {
            return state;
        }
        var failed = SilhouetteRenditionModel.FailMatte(
            state,
            attemptGeneration,
            "matte-processing-failed",
            DateTimeOffset.UtcNow);
        return await SaveAsync(
            libraryRoot,
            projectId,
            state,
            failed,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SilhouetteRenditionDocument> TryFailOutputAsync(
        string libraryRoot,
        string projectId,
        SilhouetteRenditionDocument state,
        string orientationId,
        long attemptGeneration,
        CancellationToken cancellationToken)
    {
        var output = state.Outputs.Single(candidate =>
            candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
        if (output.State is not (SilhouetteOutputState.Rendering or
            SilhouetteOutputState.Committing) ||
            output.AttemptGeneration != attemptGeneration)
        {
            return state;
        }
        var failed = SilhouetteRenditionModel.FailOutput(
            state,
            orientationId,
            attemptGeneration,
            "render-failed",
            DateTimeOffset.UtcNow);
        return await SaveAsync(
            libraryRoot,
            projectId,
            state,
            failed,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<SilhouetteRenditionDocument> SaveAsync(
        string libraryRoot,
        string projectId,
        SilhouetteRenditionDocument current,
        SilhouetteRenditionDocument candidate,
        CancellationToken cancellationToken) =>
        SilhouetteRenditionStore.SaveAsync(
            libraryRoot,
            projectId,
            candidate,
            current.Generation,
            cancellationToken);

    private static LoadedProject LoadProject(
        string libraryRoot,
        string projectId,
        CancellationToken cancellationToken)
    {
        var summary = CaptureProjectStore.LoadProjectIndex(
                libraryRoot,
                cancellationToken)
            .Values.SingleOrDefault(candidate =>
                candidate.ProjectId.Equals(projectId, StringComparison.Ordinal)) ??
            throw new InvalidDataException(
                "The committed Reaction Camera project could not be validated.");
        var manifest = JsonSerializer.Deserialize<CaptureProjectManifest>(
                           CaptureProjectStore.ReadBoundedManifestFile(summary.ManifestPath),
                           ManifestJsonOptions) ??
                       throw new InvalidDataException(
                           "The capture project manifest could not be read.");
        var media = CaptureProjectStore.ValidateCommittedMediaIdentity(
            libraryRoot,
            projectId,
            manifest,
            cancellationToken);
        return new LoadedProject(
            manifest,
            media.GameplayPath,
            media.CameraLayerPath);
    }

    private sealed record LoadedProject(
        CaptureProjectManifest Manifest,
        string GameplayPath,
        string CameraPath);
}
