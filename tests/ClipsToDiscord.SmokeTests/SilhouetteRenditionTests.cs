using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipsToDiscord;

internal static class SilhouetteRenditionTests
{
    private const string ModelId = "clipcord-person-matte-v1";
    private static readonly string ModelHash = new('a', 64);
    private static readonly CaptureMediaFingerprint MatteArtifact =
        new(1_234_567, new string('b', 64));
    private static readonly CaptureMediaFingerprint LandscapeArtifact =
        new(8_765_432, new string('c', 64));
    private static readonly CaptureMediaFingerprint PortraitArtifact =
        new(4_321_987, new string('d', 64));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    internal static void Run(string testRoot)
    {
        AssertInitialSelectionAndAggregate(testRoot);
        AssertLegalAndIllegalTransitions(testRoot);
        AssertIndependentOutputsAndRetry(testRoot);
        AssertRecoveryContracts(testRoot);
        AssertPersistenceAcceptsLegalLifecycle(testRoot);
        AssertConcurrentWritersUseCas(testRoot);
        AssertAtomicCasAndHostileFiles(testRoot);
        AssertArtifactStoreContracts(testRoot);
        AssertProcessorFailureCleanupIntegration(testRoot);
        AssertProcessorOutputFailureCleanupIntegration(testRoot);
        AssertLegacyReadyOutputMigration(testRoot);
        AssertRendererAndWorkerSeams(testRoot);
        AssertPackagedModelIntegrity();
    }

    private static void AssertConcurrentWritersUseCas(string testRoot)
    {
        var fixture = CreateFixture(
            testRoot,
            "concurrent-cas",
            landscape: true,
            portrait: false);
        var initial = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));
        _ = SilhouetteRenditionStore.SaveAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                initial,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();
        var firstCandidate = SilhouetteRenditionModel.StartMatte(initial, At(1));
        var secondCandidate = SilhouetteRenditionModel.StartMatte(initial, At(2));
        var firstSave = SilhouetteRenditionStore.SaveAsync(
            fixture.Root,
            fixture.Project.ProjectId,
            firstCandidate,
            expectedGeneration: 1);
        var secondSave = SilhouetteRenditionStore.SaveAsync(
            fixture.Root,
            fixture.Project.ProjectId,
            secondCandidate,
            expectedGeneration: 1);
        try
        {
            Task.WhenAll(firstSave, secondSave).GetAwaiter().GetResult();
        }
        catch (SilhouetteRenditionConcurrencyException)
        {
            // Exactly one sibling is expected to lose the generation-one CAS below.
        }
        var tasks = new Task[] { firstSave, secondSave };
        var loaded = SilhouetteRenditionStore.Load(
            fixture.Root,
            fixture.Project.ProjectId);
        Assert(
            tasks.Count(task => task.IsCompletedSuccessfully) == 1 &&
            tasks.Count(task => task.IsFaulted && task.Exception!.Flatten().InnerExceptions
                .Any(exception => exception is SilhouetteRenditionConcurrencyException)) == 1 &&
            loaded.LoadedFromDisk && loaded.Document is not null &&
            loaded.Document.Generation == 2 &&
            (loaded.Document.UpdatedUtc == At(1) || loaded.Document.UpdatedUtc == At(2)),
            "Two simultaneous writers for one expected generation must serialize through the named mutex, publish exactly one candidate, and reject the loser by CAS.");
    }

    private static void AssertPersistenceAcceptsLegalLifecycle(string testRoot)
    {
        var fixture = CreateFixture(
            testRoot,
            "durable-legal-lifecycle",
            landscape: true,
            portrait: true);
        var current = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));

        void Persist(SilhouetteRenditionDocument candidate)
        {
            var expectedGeneration = current.Generation;
            current = SilhouetteRenditionStore.SaveAsync(
                    fixture.Root,
                    fixture.Project.ProjectId,
                    candidate,
                    expectedGeneration: expectedGeneration)
                .GetAwaiter()
                .GetResult();
            var loaded = SilhouetteRenditionStore.Load(
                fixture.Root,
                fixture.Project.ProjectId);
            Assert(
                loaded.LoadedFromDisk && loaded.Document is not null &&
                loaded.Document.Generation == current.Generation &&
                loaded.Document.Matte == current.Matte &&
                loaded.Document.Outputs.SequenceEqual(current.Outputs),
                "Every reviewed legal transition must survive the durable CAS boundary exactly.");
        }

        // Generation one is the one canonical bootstrap document.
        current = SilhouetteRenditionStore.SaveAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                current,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();
        var matteProcessing = SilhouetteRenditionModel.StartMatte(current, At(1));
        Persist(matteProcessing);
        var matteCommitting = SilhouetteRenditionModel.BeginMatteCommit(
            current,
            current.Matte.AttemptGeneration,
            MatteArtifact,
            At(2));
        Persist(matteCommitting);
        var matteReady = SilhouetteRenditionModel.CompleteMatte(
            current,
            current.Matte.AttemptGeneration,
            MatteArtifact,
            At(3));
        Persist(matteReady);

        foreach (var (orientationId, artifact, startOffset) in new[]
                 {
                     (CompositionOrientationIds.Landscape, LandscapeArtifact, 4),
                     (CompositionOrientationIds.Portrait, PortraitArtifact, 7)
                 })
        {
            var rendering = SilhouetteRenditionModel.StartOutput(
                current,
                orientationId,
                At(startOffset));
            Persist(rendering);
            var output = Find(current, orientationId);
            var committing = SilhouetteRenditionModel.BeginOutputCommit(
                current,
                orientationId,
                output.AttemptGeneration,
                artifact,
                At(startOffset + 1));
            Persist(committing);
            output = Find(current, orientationId);
            var ready = SilhouetteRenditionModel.CompleteOutput(
                current,
                orientationId,
                output.AttemptGeneration,
                artifact,
                At(startOffset + 2));
            Persist(ready);
        }

        Assert(
            current.AggregateState == SilhouetteRenditionAggregateState.Ready &&
            current.Generation == 10,
            "The shared-matte plus two-output durable lifecycle must converge to Ready in the expected ten generations.");

        var readyLandscape = Find(current, CompositionOrientationIds.Landscape);
        var readyPortrait = Find(current, CompositionOrientationIds.Portrait);
        Assert(
            Throws<InvalidDataException>(() =>
                SilhouetteRenditionModel.InvalidateReadyOutput(
                    current,
                    CompositionOrientationIds.Portrait,
                    readyPortrait.FinalArtifact,
                    At(10))),
            "A ready output whose fixed artifact still matches its fingerprint must not be invalidated.");
        var invalidated = SilhouetteRenditionModel.InvalidateReadyOutput(
            current,
            CompositionOrientationIds.Portrait,
            readyPortrait.FinalArtifact! with { Sha256 = new string('e', 64) },
            At(10));
        Persist(invalidated);
        Assert(
            current.Generation == 11 &&
            current.AggregateState == SilhouetteRenditionAggregateState.PartiallyReady &&
            Find(current, CompositionOrientationIds.Landscape) == readyLandscape &&
            Find(current, CompositionOrientationIds.Portrait) is
            {
                State: SilhouetteOutputState.Failed,
                ErrorCode: "output-invalid",
                FinalArtifact: null,
                ExpectedTemporaryArtifact: null
            },
            "A durable invalidation must fail only the mismatched ready output and preserve its ready sibling exactly.");
    }

    private static void AssertArtifactStoreContracts(string testRoot)
    {
        var fixture = CreateFixture(
            testRoot,
            "artifact-store-contracts",
            landscape: true,
            portrait: true);
        var projectDirectory = fixture.Project.ProjectDirectory;
        var gameplayPath = CaptureProjectStore.ValidateCommittedMediaIdentity(
            fixture.Root,
            fixture.Project.ProjectId,
            fixture.Project.Manifest).GameplayPath;
        var gameplayDirectory = Path.GetDirectoryName(gameplayPath)!;
        var gameplayStem = Path.GetFileNameWithoutExtension(gameplayPath);
        var landscapeOutputPath = SilhouetteArtifactStore.GetOutputPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        var portraitOutputPath = SilhouetteArtifactStore.GetOutputPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Portrait);

        Assert(
            Path.GetFileName(SilhouetteArtifactStore.GetMattePath(
                fixture.Root,
                fixture.Project.ProjectId)) == SilhouetteArtifactStore.MatteFileName &&
            Path.GetDirectoryName(SilhouetteArtifactStore.GetMattePath(
                fixture.Root,
                fixture.Project.ProjectId)) == projectDirectory &&
            Path.GetDirectoryName(landscapeOutputPath) == gameplayDirectory &&
            Path.GetFileName(landscapeOutputPath) ==
                $"{gameplayStem}__Reaction-Camera__Landscape.mp4" &&
            Path.GetDirectoryName(portraitOutputPath) == gameplayDirectory &&
            Path.GetFileName(portraitOutputPath) ==
                $"{gameplayStem}__Reaction-Camera__Portrait.mp4" &&
            Path.GetDirectoryName(SilhouetteArtifactStore.GetLegacyOutputPath(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Landscape)) == projectDirectory &&
            Throws<ArgumentException>(() =>
                SilhouetteArtifactStore.GetOutputPath(
                    fixture.Root,
                    fixture.Project.ProjectId,
                    "../../portrait")),
            "Matte and legacy artifacts must stay private while completed videos use exact, source-derived visible sibling paths.");

        var current = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(40));
        current = SilhouetteRenditionStore.SaveAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                current,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();
        void Persist(SilhouetteRenditionDocument candidate)
        {
            current = SilhouetteRenditionStore.SaveAsync(
                    fixture.Root,
                    fixture.Project.ProjectId,
                    candidate,
                    expectedGeneration: current.Generation)
                .GetAwaiter()
                .GetResult();
        }

        Persist(SilhouetteRenditionModel.StartMatte(current, At(41)));
        var matteAttempt = current.Matte.AttemptGeneration;
        var matteTemporary = SilhouetteArtifactStore.CreateMatteTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            matteAttempt);
        File.WriteAllBytes(
            matteTemporary,
            Enumerable.Range(0, 97).Select(value => (byte)(value * 11)).ToArray());
        var matteFingerprint = SilhouetteArtifactStore.FingerprintTemporaryAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                matteTemporary)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.BeginMatteCommit(
            current,
            matteAttempt,
            matteFingerprint,
            At(42)));
        var promotedMatte = SilhouetteArtifactStore.PromoteMatteAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                matteAttempt,
                matteTemporary,
                matteFingerprint)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.CompleteMatte(
            current,
            matteAttempt,
            promotedMatte,
            At(43)));
        var validMatte = SilhouetteArtifactStore.ValidateMatteAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                matteFingerprint)
            .GetAwaiter()
            .GetResult();
        Assert(
            validMatte.IsValid &&
            Path.GetFileName(SilhouetteArtifactStore.GetMattePath(
                fixture.Root,
                fixture.Project.ProjectId)) == SilhouetteArtifactStore.MatteFileName,
            "The shared matte must follow the same committing-state, fingerprint, fixed-path publication contract as outputs.");

        Persist(SilhouetteRenditionModel.StartOutput(
            current,
            CompositionOrientationIds.Landscape,
            At(44)));
        var landscapeAttempt = Find(
            current,
            CompositionOrientationIds.Landscape).AttemptGeneration;
        var firstLandscapeBytes = Enumerable.Range(0, 257)
            .Select(value => (byte)(value * 17))
            .ToArray();
        var landscapeTemporary = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Landscape,
            landscapeAttempt);
        Assert(
            Path.GetDirectoryName(landscapeTemporary) == projectDirectory &&
            !Path.GetDirectoryName(landscapeTemporary)!.Equals(
                gameplayDirectory,
                StringComparison.OrdinalIgnoreCase),
            "A rendition render must stay in the private project directory until its fingerprinted publication commit.");
        File.WriteAllBytes(landscapeTemporary, firstLandscapeBytes);
        var firstLandscapeFingerprint = SilhouetteArtifactStore.FingerprintTemporaryAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                landscapeTemporary)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.BeginOutputCommit(
            current,
            CompositionOrientationIds.Landscape,
            landscapeAttempt,
            firstLandscapeFingerprint,
            At(45)));
        var promotedLandscape = SilhouetteArtifactStore.PromoteOutputAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Landscape,
                landscapeAttempt,
                landscapeTemporary,
                firstLandscapeFingerprint)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.CompleteOutput(
            current,
            CompositionOrientationIds.Landscape,
            landscapeAttempt,
            promotedLandscape,
            At(46)));
        var landscapePath = SilhouetteArtifactStore.GetOutputPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        var validLandscape = SilhouetteArtifactStore.ValidateOutputAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Landscape,
                firstLandscapeFingerprint)
            .GetAwaiter()
            .GetResult();
        Assert(
            promotedLandscape == firstLandscapeFingerprint &&
            validLandscape.Status == SilhouetteArtifactValidationStatus.Valid &&
            validLandscape.ObservedFingerprint == firstLandscapeFingerprint &&
            File.ReadAllBytes(landscapePath).SequenceEqual(firstLandscapeBytes) &&
            !File.Exists(landscapeTemporary),
            "Promotion must atomically rename an exact owned temporary output to its fixed path and retain the SHA-256 identity.");

        var staleAttemptTemporary = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Landscape,
            landscapeAttempt);
        File.WriteAllBytes(staleAttemptTemporary, firstLandscapeBytes);
        Assert(
            Throws<SilhouetteRenditionConcurrencyException>(() =>
                SilhouetteArtifactStore.PromoteOutputAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        CompositionOrientationIds.Landscape,
                        landscapeAttempt,
                        staleAttemptTemporary,
                        firstLandscapeFingerprint)
                    .GetAwaiter()
                    .GetResult()) &&
            File.Exists(staleAttemptTemporary) &&
            File.ReadAllBytes(landscapePath).SequenceEqual(firstLandscapeBytes),
            "A worker from an attempt that is no longer Committing must not overwrite a ready fixed output.");
        File.Delete(staleAttemptTemporary);

        var secondLandscapeBytes = firstLandscapeBytes
            .Select(value => (byte)(value ^ 0x5a))
            .ToArray();
        File.WriteAllBytes(landscapePath, secondLandscapeBytes);
        var staleValidation = SilhouetteArtifactStore.ValidateOutputAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Landscape,
                firstLandscapeFingerprint)
            .GetAwaiter()
            .GetResult();
        Assert(
            staleValidation.Status == SilhouetteArtifactValidationStatus.Mismatch &&
            staleValidation.IsDefinitiveFailure &&
            staleValidation.ObservedFingerprint is not null,
            "Ready validation must detect same-length replacement by SHA-256 before state is invalidated.");
        Persist(SilhouetteRenditionModel.InvalidateReadyOutput(
            current,
            CompositionOrientationIds.Landscape,
            staleValidation.ObservedFingerprint,
            At(47)));
        Persist(SilhouetteRenditionModel.RetryOutput(
            current,
            CompositionOrientationIds.Landscape,
            At(48)));
        Persist(SilhouetteRenditionModel.StartOutput(
            current,
            CompositionOrientationIds.Landscape,
            At(49)));
        landscapeAttempt = Find(
            current,
            CompositionOrientationIds.Landscape).AttemptGeneration;
        var secondLandscapeTemporary = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Landscape,
            landscapeAttempt);
        File.WriteAllBytes(secondLandscapeTemporary, secondLandscapeBytes);
        var secondLandscapeFingerprint = SilhouetteArtifactStore.FingerprintTemporaryAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                secondLandscapeTemporary)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.BeginOutputCommit(
            current,
            CompositionOrientationIds.Landscape,
            landscapeAttempt,
            secondLandscapeFingerprint,
            At(50)));
        _ = SilhouetteArtifactStore.PromoteOutputAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Landscape,
                landscapeAttempt,
                secondLandscapeTemporary,
                secondLandscapeFingerprint)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.CompleteOutput(
            current,
            CompositionOrientationIds.Landscape,
            landscapeAttempt,
            secondLandscapeFingerprint,
            At(51)));
        var currentValidation = SilhouetteArtifactStore.ValidateOutputAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Landscape,
                secondLandscapeFingerprint)
            .GetAwaiter()
            .GetResult();
        Assert(
            staleValidation.ObservedFingerprint == secondLandscapeFingerprint &&
            currentValidation.IsValid &&
            File.ReadAllBytes(landscapePath).SequenceEqual(secondLandscapeBytes),
            "Ready validation must use SHA-256, detect same-length replacement, and accept only the current promoted bytes.");

        var wrongTemporary = Path.Combine(projectDirectory, "silhouette-output.tmp");
        File.WriteAllBytes(wrongTemporary, [1, 2, 3, 4]);
        var expectedWrongBytes = File.ReadAllBytes(wrongTemporary);
        Assert(
            Throws<IOException>(() =>
                SilhouetteArtifactStore.PromoteOutputAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        CompositionOrientationIds.Portrait,
                        attemptGeneration: 9,
                        wrongTemporary,
                        secondLandscapeFingerprint)
                    .GetAwaiter()
                    .GetResult()) &&
            File.ReadAllBytes(wrongTemporary).SequenceEqual(expectedWrongBytes) &&
            !File.Exists(SilhouetteArtifactStore.GetOutputPath(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Portrait)),
            "Promotion must reject and preserve caller-supplied lookalike paths outside the strict owned temporary-name grammar.");

        Persist(SilhouetteRenditionModel.StartOutput(
            current,
            CompositionOrientationIds.Portrait,
            At(52)));
        var portraitAttempt = Find(
            current,
            CompositionOrientationIds.Portrait).AttemptGeneration;
        var portraitTemporary = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Portrait,
            portraitAttempt);
        File.WriteAllBytes(portraitTemporary, [9, 8, 7, 6, 5]);
        var portraitFingerprint = SilhouetteArtifactStore.FingerprintTemporaryAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                portraitTemporary)
            .GetAwaiter()
            .GetResult();
        Assert(
            Throws<InvalidDataException>(() =>
                SilhouetteArtifactStore.PromoteOutputAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        CompositionOrientationIds.Portrait,
                        portraitAttempt,
                        portraitTemporary,
                        secondLandscapeFingerprint)
                    .GetAwaiter()
                    .GetResult()) &&
            File.Exists(portraitTemporary),
            "A fingerprint mismatch must fail before the atomic commit boundary and retain the exact temporary file for recovery.");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert(
                Throws<OperationCanceledException>(() =>
                    SilhouetteArtifactStore.PromoteOutputAsync(
                            fixture.Root,
                            fixture.Project.ProjectId,
                            CompositionOrientationIds.Portrait,
                            portraitAttempt,
                            portraitTemporary,
                            portraitFingerprint,
                            cancelled.Token)
                        .GetAwaiter()
                        .GetResult()) &&
                File.Exists(portraitTemporary),
                "Cancellation before promotion must leave the owned temporary artifact available and publish no final file.");
        }

        Persist(SilhouetteRenditionModel.BeginOutputCommit(
            current,
            CompositionOrientationIds.Portrait,
            portraitAttempt,
            portraitFingerprint,
            At(53)));
        var occupiedPortraitBytes = new byte[] { 4, 3, 2, 1 };
        var portraitPath = SilhouetteArtifactStore.GetOutputPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Portrait);
        File.WriteAllBytes(portraitPath, occupiedPortraitBytes);
        Assert(
            Throws<IOException>(() =>
                SilhouetteArtifactStore.PromoteOutputAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        CompositionOrientationIds.Portrait,
                        portraitAttempt,
                        portraitTemporary,
                        portraitFingerprint)
                    .GetAwaiter()
                    .GetResult()) &&
            File.Exists(portraitTemporary) &&
            File.ReadAllBytes(portraitTemporary).SequenceEqual(new byte[] { 9, 8, 7, 6, 5 }) &&
            File.ReadAllBytes(portraitPath).SequenceEqual(occupiedPortraitBytes),
            "Publication must preserve both a fingerprinted private render and unrelated media occupying its visible sibling name.");
        File.Delete(portraitPath);
        _ = SilhouetteArtifactStore.PromoteOutputAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Portrait,
                portraitAttempt,
                portraitTemporary,
                portraitFingerprint)
            .GetAwaiter()
            .GetResult();
        Persist(SilhouetteRenditionModel.CompleteOutput(
            current,
            CompositionOrientationIds.Portrait,
            portraitAttempt,
            portraitFingerprint,
            At(54)));
        File.Delete(portraitPath);
        var missingPortrait = SilhouetteArtifactStore.ValidateOutputAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Portrait,
                portraitFingerprint)
            .GetAwaiter()
            .GetResult();
        Assert(
            missingPortrait.Status == SilhouetteArtifactValidationStatus.Missing &&
            missingPortrait.IsDefinitiveFailure &&
            missingPortrait.ObservedFingerprint is null,
            "A definitively absent fixed output must be reported as Missing for targeted state recovery.");

        var cleanupCutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        var oldOwnedTemporaryPaths = Enumerable.Range(20, 3)
            .Select(generation => SilhouetteArtifactStore.CreateMatteTemporaryPath(
                fixture.Root,
                fixture.Project.ProjectId,
                generation))
            .ToArray();
        foreach (var path in oldOwnedTemporaryPaths)
        {
            File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(path, cleanupCutoff.UtcDateTime.AddMinutes(-1));
        }
        var recentOwnedTemporary = SilhouetteArtifactStore.CreateMatteTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            attemptGeneration: 23);
        File.WriteAllBytes(recentOwnedTemporary, [2]);
        var failedMatteTemporary = SilhouetteArtifactStore.CreateMatteTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            attemptGeneration: 24);
        var failedOutputTemporary = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            fixture.Root,
            fixture.Project.ProjectId,
            CompositionOrientationIds.Landscape,
            attemptGeneration: 25);
        File.WriteAllBytes(failedMatteTemporary, [4, 5, 6]);
        File.WriteAllBytes(failedOutputTemporary, [7, 8, 9]);
        SilhouetteArtifactStore.DiscardOwnedTemporary(
            fixture.Root,
            fixture.Project.ProjectId,
            failedMatteTemporary);
        SilhouetteArtifactStore.DiscardOwnedTemporary(
            fixture.Root,
            fixture.Project.ProjectId,
            failedOutputTemporary);
        SilhouetteArtifactStore.DiscardOwnedTemporary(
            fixture.Root,
            fixture.Project.ProjectId,
            wrongTemporary);
        Assert(
            !File.Exists(failedMatteTemporary) &&
            !File.Exists(failedOutputTemporary) &&
            File.Exists(wrongTemporary) &&
            File.ReadAllBytes(wrongTemporary).SequenceEqual(expectedWrongBytes),
            "Handled matte and output failures must immediately discard exact owned temporaries while preserving every caller-supplied lookalike.");
        var lookalikeTemporary = Path.Combine(
            projectDirectory,
            Path.GetFileName(oldOwnedTemporaryPaths[0]) + ".extra");
        File.WriteAllBytes(lookalikeTemporary, [3]);
        File.SetLastWriteTimeUtc(
            lookalikeTemporary,
            cleanupCutoff.UtcDateTime.AddMinutes(-1));
        var cleaned = SilhouetteArtifactStore.CleanupTemporaryArtifacts(
            fixture.Root,
            fixture.Project.ProjectId,
            cleanupCutoff,
            maximumFiles: 2);
        Assert(
            cleaned == 2 &&
            oldOwnedTemporaryPaths.Count(File.Exists) == 1 &&
            File.Exists(recentOwnedTemporary) &&
            File.Exists(lookalikeTemporary) &&
            Throws<ArgumentOutOfRangeException>(() =>
                SilhouetteArtifactStore.CleanupTemporaryArtifacts(
                    fixture.Root,
                    fixture.Project.ProjectId,
                    cleanupCutoff,
                    SilhouetteArtifactStore.MaximumCleanupFiles + 1)),
            "Interrupted or crashed work must remain recoverable while recent, then bounded cleanup must delete only exact age-qualified names and preserve lookalikes.");

        AssertArtifactReparseRejectedWhenSupported(
            fixture,
            secondLandscapeFingerprint,
            landscapePath);
        AssertArtifactDirectoryJunctionRejected(
            fixture,
            secondLandscapeFingerprint,
            landscapePath);

        File.Delete(wrongTemporary);
        File.Delete(lookalikeTemporary);
        foreach (var path in oldOwnedTemporaryPaths) if (File.Exists(path)) File.Delete(path);
        if (File.Exists(recentOwnedTemporary)) File.Delete(recentOwnedTemporary);

        var orphanFixture = CreateFixture(
            testRoot,
            "artifact-orphan-cleanup",
            landscape: true,
            portrait: false);
        File.WriteAllBytes(
            SilhouetteArtifactStore.GetMattePath(
                orphanFixture.Root,
                orphanFixture.Project.ProjectId),
            [5, 4, 3, 2, 1]);
        var orphanOutputTemporary = SilhouetteArtifactStore.CreateOutputTemporaryPath(
            orphanFixture.Root,
            orphanFixture.Project.ProjectId,
            CompositionOrientationIds.Landscape,
            attemptGeneration: 2);
        File.WriteAllBytes(orphanOutputTemporary, [1]);
        File.Delete(orphanFixture.Project.Manifest.GameplayRelativePath.Contains('/')
            ? Path.Combine(
                orphanFixture.Root,
                orphanFixture.Project.Manifest.GameplayRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar))
            : Path.Combine(
                orphanFixture.Root,
                orphanFixture.Project.Manifest.GameplayRelativePath));
        Directory.SetLastWriteTimeUtc(
            orphanFixture.Project.ProjectDirectory,
            DateTime.UtcNow.AddDays(-1));
        var removed = CaptureProjectStore.RemoveOrphanedProjects(
            orphanFixture.Root,
            DateTimeOffset.UtcNow.AddMinutes(1),
            TimeSpan.Zero);
        Assert(
            removed == 1 &&
            !Directory.Exists(orphanFixture.Project.ProjectDirectory),
            "Orphan cleanup must recognize and remove only the exact fixed and temporary silhouette artifact names.");
    }

    private static void AssertLegacyReadyOutputMigration(string testRoot)
    {
        var duplicateFixture = CreateFixture(
            testRoot,
            "legacy-ready-matching-duplicate",
            landscape: true,
            portrait: false);
        var duplicateBytes = Enumerable.Range(0, 227)
            .Select(value => (byte)(value * 19))
            .ToArray();
        var duplicateFingerprint = Fingerprint(duplicateBytes);
        var readyWithDuplicate = PersistReadyLandscapeState(
            duplicateFixture,
            duplicateFingerprint);
        var duplicateLegacyPath = SilhouetteArtifactStore.GetLegacyOutputPath(
            duplicateFixture.Root,
            duplicateFixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        var duplicateVisiblePath = SilhouetteArtifactStore.GetOutputPath(
            duplicateFixture.Root,
            duplicateFixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        File.WriteAllBytes(duplicateLegacyPath, duplicateBytes);
        File.WriteAllBytes(duplicateVisiblePath, duplicateBytes);

        var duplicateProcessor = new SilhouetteProjectProcessor(
            Path.Combine(duplicateFixture.Root, "unused-ffmpeg.exe"),
            Path.Combine(duplicateFixture.Root, "unused-model.onnx"),
            preferDirectMl: false);
        var duplicateResult = duplicateProcessor.ProcessAsync(
                duplicateFixture.Root,
                duplicateFixture.Project.ProjectId)
            .GetAwaiter()
            .GetResult();
        Assert(
            duplicateResult.Generation == readyWithDuplicate.Generation &&
            duplicateResult.Matte == readyWithDuplicate.Matte &&
            Find(duplicateResult, CompositionOrientationIds.Landscape) ==
                Find(readyWithDuplicate, CompositionOrientationIds.Landscape) &&
            File.Exists(duplicateVisiblePath) &&
            File.ReadAllBytes(duplicateVisiblePath).SequenceEqual(duplicateBytes) &&
            !File.Exists(duplicateLegacyPath),
            "Validating an already-visible Ready rendition must remove only its fingerprint-identical private legacy duplicate while preserving the visible owner and durable state.");

        var migrationFixture = CreateFixture(
            testRoot,
            "legacy-ready-migration",
            landscape: true,
            portrait: false);
        var migrationBytes = Enumerable.Range(0, 311)
            .Select(value => (byte)(value * 29))
            .ToArray();
        var migrationFingerprint = Fingerprint(migrationBytes);
        var readyBeforeMigration = PersistReadyLandscapeState(
            migrationFixture,
            migrationFingerprint);
        var legacyPath = SilhouetteArtifactStore.GetLegacyOutputPath(
            migrationFixture.Root,
            migrationFixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        var visiblePath = SilhouetteArtifactStore.GetOutputPath(
            migrationFixture.Root,
            migrationFixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        File.WriteAllBytes(legacyPath, migrationBytes);

        var processor = new SilhouetteProjectProcessor(
            Path.Combine(migrationFixture.Root, "unused-ffmpeg.exe"),
            Path.Combine(migrationFixture.Root, "unused-model.onnx"),
            preferDirectMl: false);
        var migrated = processor.ProcessAsync(
                migrationFixture.Root,
                migrationFixture.Project.ProjectId)
            .GetAwaiter()
            .GetResult();
        var migratedOutput = Find(migrated, CompositionOrientationIds.Landscape);
        Assert(
            migrated.Generation == readyBeforeMigration.Generation &&
            migratedOutput.State == SilhouetteOutputState.Ready &&
            migratedOutput.FinalArtifact == migrationFingerprint &&
            File.Exists(visiblePath) &&
            File.ReadAllBytes(visiblePath).SequenceEqual(migrationBytes) &&
            !File.Exists(legacyPath),
            "Processing an existing Ready project must atomically migrate its fingerprinted private output beside the gameplay source without re-rendering or changing durable state.");

        var conflictFixture = CreateFixture(
            testRoot,
            "legacy-ready-conflict",
            landscape: true,
            portrait: false);
        var legacyBytes = Enumerable.Range(0, 173)
            .Select(value => (byte)(value * 13))
            .ToArray();
        var conflictBytes = Enumerable.Range(0, 181)
            .Select(value => (byte)(255 - value))
            .ToArray();
        var legacyFingerprint = Fingerprint(legacyBytes);
        _ = PersistReadyLandscapeState(conflictFixture, legacyFingerprint);
        var conflictLegacyPath = SilhouetteArtifactStore.GetLegacyOutputPath(
            conflictFixture.Root,
            conflictFixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        var conflictVisiblePath = SilhouetteArtifactStore.GetOutputPath(
            conflictFixture.Root,
            conflictFixture.Project.ProjectId,
            CompositionOrientationIds.Landscape);
        File.WriteAllBytes(conflictLegacyPath, legacyBytes);
        File.WriteAllBytes(conflictVisiblePath, conflictBytes);

        var conflictProcessor = new SilhouetteProjectProcessor(
            Path.Combine(conflictFixture.Root, "unused-ffmpeg.exe"),
            Path.Combine(conflictFixture.Root, "unused-model.onnx"),
            preferDirectMl: false);
        var conflicted = conflictProcessor.ProcessAsync(
                conflictFixture.Root,
                conflictFixture.Project.ProjectId)
            .GetAwaiter()
            .GetResult();
        var conflictedOutput = Find(conflicted, CompositionOrientationIds.Landscape);
        Assert(
            conflictedOutput.State == SilhouetteOutputState.Failed &&
            conflictedOutput.ErrorCode == "output-invalid" &&
            File.ReadAllBytes(conflictLegacyPath).SequenceEqual(legacyBytes) &&
            File.ReadAllBytes(conflictVisiblePath).SequenceEqual(conflictBytes),
            "Legacy Ready migration must never overwrite an occupied visible name or delete either file; only that conflicting rendition may become invalid.");
    }

    private static void AssertProcessorFailureCleanupIntegration(string testRoot)
    {
        var ffmpegPath = FfmpegCompressor.FindExecutable();
        if (ffmpegPath is null)
        {
            Console.WriteLine(
                "  (skipped the processor temporary-cleanup integration check: verified FFmpeg is not available on this local test host)");
            return;
        }
        var matteFixture = CreateFixture(
            testRoot,
            "processor-matte-failure-cleanup",
            landscape: true,
            portrait: false);
        var matteState = SilhouetteRenditionStore.CreateInitial(
            matteFixture.Root,
            matteFixture.Project.ProjectId,
            OnnxSilhouetteFrameSegmenter.ModelId,
            OnnxSilhouetteFrameSegmenter.ModelSha256,
            At(77));
        matteState = SilhouetteRenditionStore.SaveAsync(
                matteFixture.Root,
                matteFixture.Project.ProjectId,
                matteState,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();
        var expectedMatteAttempt = SilhouetteRenditionModel.StartMatte(
            matteState,
            At(78)).Matte.AttemptGeneration;
        var controlTemporary = SilhouetteArtifactStore.CreateMatteTemporaryPath(
            matteFixture.Root,
            matteFixture.Project.ProjectId,
            expectedMatteAttempt);
        var modelPath = OnnxSilhouetteFrameSegmenter.GetPackagedModelPath();
        using (var segmenter = new OnnxSilhouetteFrameSegmenter(
                   modelPath,
                   preferDirectMl: false))
        {
            Assert(
                Throws<Exception>(() =>
                    SilhouetteMatteGenerator.GenerateAsync(
                            ffmpegPath,
                            matteFixture.Project.CameraLayerPath,
                            controlTemporary,
                            segmenter,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()) &&
                File.Exists(controlTemporary),
                "The invalid-camera control must prove that the real matte generator materializes its exact temporary file before reporting failure.");
        }
        File.Delete(controlTemporary);
        var expectedTemporaryPrefix = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $".{SilhouetteArtifactStore.MatteFileName}.{expectedMatteAttempt:D20}.");
        var matteProcessor = new SilhouetteProjectProcessor(
            ffmpegPath,
            modelPath,
            preferDirectMl: false);
        var matteResult = matteProcessor.ProcessAsync(
                matteFixture.Root,
                matteFixture.Project.ProjectId)
            .GetAwaiter()
            .GetResult();
        var mattePersisted = SilhouetteRenditionStore.Load(
            matteFixture.Root,
            matteFixture.Project.ProjectId);
        Assert(
            matteResult.Matte.State == SilhouetteMatteState.Failed &&
            matteResult.Matte.ErrorCode == "matte-processing-failed" &&
            mattePersisted.LoadedFromDisk &&
            mattePersisted.Document is not null &&
            mattePersisted.Document.Generation == matteResult.Generation &&
            mattePersisted.Document.Matte == matteResult.Matte &&
            !Directory.EnumerateFiles(matteFixture.Project.ProjectDirectory)
                .Select(Path.GetFileName)
                .Any(name => name is not null &&
                    name.StartsWith(expectedTemporaryPrefix, StringComparison.Ordinal) &&
                    name.EndsWith(".tmp", StringComparison.Ordinal)),
            "The processor's handled matte-generation failure must durably fail the attempt and immediately discard its exact owned temporary file. " +
            $"result={matteResult.Matte.State}/{matteResult.Matte.ErrorCode}, " +
            $"loaded={mattePersisted.Status}, resultGeneration={matteResult.Generation}, " +
            $"loadedGeneration={mattePersisted.Document?.Generation}, " +
            $"sameMatte={mattePersisted.Document?.Matte == matteResult.Matte}");
    }

    private static void AssertProcessorOutputFailureCleanupIntegration(string testRoot)
    {
        var fixture = CreateFixture(
            testRoot,
            "processor-output-failure-cleanup",
            landscape: true,
            portrait: false);
        var matteBytes = Enumerable.Range(0, 193)
            .Select(value => (byte)(value * 31))
            .ToArray();
        var mattePath = SilhouetteArtifactStore.GetMattePath(
            fixture.Root,
            fixture.Project.ProjectId);
        File.WriteAllBytes(mattePath, matteBytes);
        var matteFingerprint = Fingerprint(matteBytes);
        var state = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            OnnxSilhouetteFrameSegmenter.ModelId,
            OnnxSilhouetteFrameSegmenter.ModelSha256,
            At(80));
        state = SilhouetteRenditionStore.SaveAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                state,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();

        void Persist(SilhouetteRenditionDocument candidate)
        {
            state = SilhouetteRenditionStore.SaveAsync(
                    fixture.Root,
                    fixture.Project.ProjectId,
                    candidate,
                    expectedGeneration: state.Generation)
                .GetAwaiter()
                .GetResult();
        }

        Persist(SilhouetteRenditionModel.StartMatte(state, At(81)));
        Persist(SilhouetteRenditionModel.BeginMatteCommit(
            state,
            state.Matte.AttemptGeneration,
            matteFingerprint,
            At(82)));
        Persist(SilhouetteRenditionModel.CompleteMatte(
            state,
            state.Matte.AttemptGeneration,
            matteFingerprint,
            At(83)));
        var expectedOutputAttempt = Find(
            SilhouetteRenditionModel.StartOutput(
                state,
                CompositionOrientationIds.Landscape,
                At(84)),
            CompositionOrientationIds.Landscape).AttemptGeneration;
        var analysisCalls = 0;
        var renderCalls = 0;
        string? observedMattePath = null;
        string? observedTemporaryPath = null;
        string? observedOrientationId = null;
        var analysis = new SilhouetteMatteAnalysis(
            FrameCount: 15,
            FramesPerSecond: SilhouetteMatteGenerator.FramesPerSecond,
            new NormalizedSilhouetteBounds(0.1, 0.1, 0.8, 0.8));
        var seams = new SilhouetteProjectProcessorSeams(
            (ffmpegPath, candidateMattePath, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                analysisCalls++;
                observedMattePath = candidateMattePath;
                return Task.FromResult(analysis);
            },
            (
                ffmpegPath,
                gameplayPath,
                cameraPath,
                candidateMattePath,
                outputPath,
                manifest,
                composition,
                candidateAnalysis,
                orientationId,
                cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                renderCalls++;
                observedTemporaryPath = outputPath;
                observedOrientationId = orientationId;
                File.WriteAllBytes(outputPath, [9, 7, 5, 3, 1]);
                return Task.FromException(
                    new InvalidOperationException(
                        "Synthetic post-write renderer failure for cleanup coverage."));
            });
        var processor = new SilhouetteProjectProcessor(
            Path.Combine(fixture.Root, "unused-ffmpeg.exe"),
            Path.Combine(fixture.Root, "unused-model.onnx"),
            preferDirectMl: false,
            seams);
        var result = processor.ProcessAsync(
                fixture.Root,
                fixture.Project.ProjectId)
            .GetAwaiter()
            .GetResult();
        var persisted = SilhouetteRenditionStore.Load(
            fixture.Root,
            fixture.Project.ProjectId);
        var failedOutput = Find(result, CompositionOrientationIds.Landscape);
        var expectedTemporaryPrefix = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $".{SilhouetteArtifactStore.LandscapeFileName}.{expectedOutputAttempt:D20}.");
        Assert(
            analysisCalls == 1 &&
            renderCalls == 1 &&
            observedMattePath == mattePath &&
            observedOrientationId == CompositionOrientationIds.Landscape &&
            observedTemporaryPath is not null &&
            Path.GetDirectoryName(observedTemporaryPath) == fixture.Project.ProjectDirectory &&
            Path.GetFileName(observedTemporaryPath)
                .StartsWith(expectedTemporaryPrefix, StringComparison.Ordinal) &&
            Path.GetFileName(observedTemporaryPath)
                .EndsWith(".tmp", StringComparison.Ordinal) &&
            !File.Exists(observedTemporaryPath) &&
            !Directory.EnumerateFiles(fixture.Project.ProjectDirectory)
                .Select(Path.GetFileName)
                .Any(name => name is not null &&
                    name.StartsWith(expectedTemporaryPrefix, StringComparison.Ordinal) &&
                    name.EndsWith(".tmp", StringComparison.Ordinal)) &&
            !File.Exists(SilhouetteArtifactStore.GetOutputPath(
                fixture.Root,
                fixture.Project.ProjectId,
                CompositionOrientationIds.Landscape)) &&
            failedOutput.State == SilhouetteOutputState.Failed &&
            failedOutput.ErrorCode == "render-failed" &&
            failedOutput.FinalArtifact is null &&
            failedOutput.ExpectedTemporaryArtifact is null &&
            persisted.LoadedFromDisk &&
            persisted.Document is not null &&
            persisted.Document.Generation == result.Generation &&
            Find(persisted.Document, CompositionOrientationIds.Landscape) == failedOutput,
            "A renderer failure after writing the processor-generated output temporary must delete that exact artifact, publish no visible clip, and durably fail only the selected rendition.");
    }

    private static SilhouetteRenditionDocument PersistReadyLandscapeState(
        Fixture fixture,
        CaptureMediaFingerprint outputFingerprint)
    {
        var current = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            OnnxSilhouetteFrameSegmenter.ModelId,
            OnnxSilhouetteFrameSegmenter.ModelSha256,
            At(70));
        current = SilhouetteRenditionStore.SaveAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                current,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();

        void Persist(SilhouetteRenditionDocument candidate)
        {
            current = SilhouetteRenditionStore.SaveAsync(
                    fixture.Root,
                    fixture.Project.ProjectId,
                    candidate,
                    expectedGeneration: current.Generation)
                .GetAwaiter()
                .GetResult();
        }

        Persist(SilhouetteRenditionModel.StartMatte(current, At(71)));
        Persist(SilhouetteRenditionModel.BeginMatteCommit(
            current,
            current.Matte.AttemptGeneration,
            MatteArtifact,
            At(72)));
        Persist(SilhouetteRenditionModel.CompleteMatte(
            current,
            current.Matte.AttemptGeneration,
            MatteArtifact,
            At(73)));
        Persist(SilhouetteRenditionModel.StartOutput(
            current,
            CompositionOrientationIds.Landscape,
            At(74)));
        var attemptGeneration = Find(
            current,
            CompositionOrientationIds.Landscape).AttemptGeneration;
        Persist(SilhouetteRenditionModel.BeginOutputCommit(
            current,
            CompositionOrientationIds.Landscape,
            attemptGeneration,
            outputFingerprint,
            At(75)));
        Persist(SilhouetteRenditionModel.CompleteOutput(
            current,
            CompositionOrientationIds.Landscape,
            attemptGeneration,
            outputFingerprint,
            At(76)));
        return current;
    }

    private static CaptureMediaFingerprint Fingerprint(byte[] bytes) => new(
        bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static void AssertRendererAndWorkerSeams(string testRoot)
    {
        var fixture = CreateFixture(
            testRoot,
            "renderer-worker-seams",
            landscape: true,
            portrait: true);
        var manifestDuration = TimeSpan.FromTicks(123_456_780);
        var gameplayDuration = TimeSpan.FromMilliseconds(9_280);
        var manifest = fixture.Project.Manifest with
        {
            CameraStartOffsetTicks = TimeSpan.FromMilliseconds(125).Ticks,
            DurationTicks = manifestDuration.Ticks
        };
        var gameplay = new FfmpegCompressor.MediaProbe(
            gameplayDuration,
            AudioStreamCount: 3,
            VideoWidth: 2561,
            VideoHeight: 1441,
            VideoFramesPerSecond: 60);
        var camera = new FfmpegCompressor.MediaProbe(
            TimeSpan.FromSeconds(10),
            AudioStreamCount: 1,
            VideoWidth: 1281,
            VideoHeight: 721,
            VideoFramesPerSecond: 30);
        var matte = new SilhouetteMatteAnalysis(
            FrameCount: 371,
            FramesPerSecond: 30,
            new NormalizedSilhouetteBounds(
                Left: 0.12,
                Top: 0.04,
                Width: 0.76,
                Height: 0.92));
        var extremeLayouts = fixture.Composition.SilhouetteLayouts
            .Select(layout => layout with
            {
                Enabled = true,
                Transform = layout.Transform with
                {
                    AnchorX = layout.ProfileId == CompositionOrientationIds.Landscape
                        ? -4
                        : 5,
                    AnchorY = 3,
                    HeightFraction = 4
                }
            })
            .ToArray();
        var composition = fixture.Composition with { SilhouetteLayouts = extremeLayouts };
        var landscapeOutput = Path.Combine(
            fixture.Project.ProjectDirectory,
            $".{SilhouetteArtifactStore.LandscapeFileName}.00000000000000000012.{Guid.NewGuid():N}.tmp");
        var portraitOutput = Path.Combine(
            fixture.Project.ProjectDirectory,
            $".{SilhouetteArtifactStore.PortraitFileName}.00000000000000000013.{Guid.NewGuid():N}.tmp");
        var landscapeArguments = SilhouetteCompositor.BuildArguments(
            fixture.Project.Manifest.GameplayRelativePath,
            fixture.Project.CameraLayerPath,
            SilhouetteArtifactStore.GetMattePath(fixture.Root, fixture.Project.ProjectId),
            landscapeOutput,
            manifest,
            composition,
            matte,
            gameplay,
            camera,
            CompositionOrientationIds.Landscape);
        var portraitArguments = SilhouetteCompositor.BuildArguments(
            fixture.Project.Manifest.GameplayRelativePath,
            fixture.Project.CameraLayerPath,
            SilhouetteArtifactStore.GetMattePath(fixture.Root, fixture.Project.ProjectId),
            portraitOutput,
            manifest,
            composition,
            matte,
            gameplay,
            camera,
            CompositionOrientationIds.Portrait);

        foreach (var (arguments, outputPath, orientation) in new[]
                 {
                     (landscapeArguments, landscapeOutput, "landscape"),
                     (portraitArguments, portraitOutput, "portrait")
                 })
        {
            Assert(
                outputPath.EndsWith(".tmp", StringComparison.Ordinal) &&
                arguments.Count >= 3 &&
                arguments[^3] == "-f" &&
                arguments[^2] == "mp4" &&
                arguments[^1] == outputPath,
                $"The {orientation} renderer must explicitly select MP4 immediately before its opaque .tmp output path.");
            Assert(
                CountPair(arguments, "-map", "0:a?") == 1 &&
                !HasPair(arguments, "-map", "0:a:0?") &&
                gameplay.AudioStreamCount == 3,
                $"The {orientation} renderer must optionally map every gameplay audio stream, not only stream zero.");
            Assert(
                CountPair(arguments, "-t", "9.28") == 1,
                $"The {orientation} renderer must preserve the finalized gameplay MP4 timeline even when the camera manifest observed a longer capture interval.");
        }
        Assert(
            SilhouetteCompositor.ResolveOutputDuration(manifest, gameplay) == gameplayDuration &&
            SilhouetteCompositor.ResolveOutputDuration(manifest, gameplay) != manifestDuration,
            "The finalized gameplay duration must be authoritative for rendition validation, not the camera encoder's longer wall-clock interval.");
        var reproducedManifest = manifest with { DurationTicks = 111_042_390 };
        var finalizedGameplay = gameplay with
        {
            Duration = TimeSpan.FromSeconds(9.283333)
        };
        var renderedOutput = finalizedGameplay with
        {
            VideoWidth = 2560,
            VideoHeight = 1440
        };
        Assert(
            DoesNotThrow(() =>
                SilhouetteCompositor.ValidateRenderedOutput(
                    reproducedManifest,
                    finalizedGameplay,
                    renderedOutput)),
            "Post-render validation must accept the real 9.283333-second finalized gameplay/output timeline even when the camera wall-clock manifest reports 11.104239 seconds.");
        Assert(
            Throws<InvalidDataException>(() =>
                SilhouetteCompositor.ValidateRenderedOutput(
                    reproducedManifest,
                    finalizedGameplay,
                    renderedOutput with
                    {
                        Duration = TimeSpan.FromTicks(111_042_390)
                    })),
            "Post-render validation must compare the real 9.283333-second finalized gameplay/output timeline against itself, not the 11.104239-second camera wall-clock manifest.");
        var shorterCameraManifest = manifest with
        {
            DurationTicks = TimeSpan.FromSeconds(8).Ticks
        };
        Assert(
            SilhouetteCompositor.ResolveOutputDuration(shorterCameraManifest, gameplay) ==
                gameplayDuration,
            "A shorter camera interval must not truncate the finalized gameplay clip's rendition timeline.");
        var shorterCameraArguments = SilhouetteCompositor.BuildArguments(
            fixture.Project.Manifest.GameplayRelativePath,
            fixture.Project.CameraLayerPath,
            SilhouetteArtifactStore.GetMattePath(fixture.Root, fixture.Project.ProjectId),
            landscapeOutput,
            shorterCameraManifest,
            composition,
            matte,
            gameplay,
            camera,
            CompositionOrientationIds.Landscape);
        Assert(
            CountPair(shorterCameraArguments, "-t", "9.28") == 1,
            "The FFmpeg render command must retain the complete gameplay timeline when the camera interval is shorter.");

        var landscapePlan = SilhouetteCompositor.CreatePlan(
            composition,
            matte,
            gameplay,
            camera,
            CompositionOrientationIds.Landscape);
        var portraitPlan = SilhouetteCompositor.CreatePlan(
            composition,
            matte,
            gameplay,
            camera,
            CompositionOrientationIds.Portrait);
        Assert(
            landscapePlan.CanvasWidth == 2560 &&
            landscapePlan.CanvasHeight == 1440 &&
            portraitPlan.CanvasWidth == SilhouetteCompositor.PortraitWidth &&
            portraitPlan.CanvasHeight == SilhouetteCompositor.PortraitHeight &&
            PlanIsContained(landscapePlan) &&
            PlanIsContained(portraitPlan),
            "Landscape must retain the even gameplay dimensions, portrait must be 1080x1920, and extreme saved placements must remain fully inside either canvas.");

        var absoluteRoot = Path.GetFullPath(Path.Combine(
            testRoot,
            "Library with spaces & shell metacharacters"));
        var projectId = new string('a', 32);
        var validArguments = new[]
        {
            SilhouetteWorkerLaunchOptions.WorkerArgument,
            SilhouetteWorkerLaunchOptions.LibraryRootArgument,
            absoluteRoot,
            SilhouetteWorkerLaunchOptions.ProjectIdArgument,
            projectId,
            SilhouetteWorkerLaunchOptions.CpuArgument
        };
        Assert(
            SilhouetteWorkerLaunchOptions.TryParse(validArguments, out var parsed) &&
            parsed == new SilhouetteWorkerLaunchOptions(absoluteRoot, projectId, ForceCpu: true),
            "The isolated silhouette worker must accept a fully-qualified root and exact lowercase 32-hex project id.");

        foreach (var hostile in new[]
                 {
                     ReplaceArgument(validArguments, 2, Path.Combine("relative", "Library")),
                     ReplaceArgument(
                         validArguments,
                         2,
                         Path.DirectorySeparatorChar + "rooted-but-not-fully-qualified"),
                     ReplaceArgument(validArguments, 4, new string('A', 32)),
                     ReplaceArgument(validArguments, 4, new string('a', 31)),
                     ReplaceArgument(validArguments, 4, new string('a', 31) + "g")
                 })
        {
            Assert(
                !SilhouetteWorkerLaunchOptions.TryParse(hostile, out _),
                "Worker parsing must reject relative/root-relative paths and every non-canonical project id.");
        }

        var executablePath = Path.GetFullPath(Path.Combine(
            testRoot,
            "worker folder",
            "ClipCord worker.exe"));
        var startInfo = SilhouetteWorkerLaunch.CreateStartInfo(
            absoluteRoot,
            projectId,
            forceCpu: true,
            executablePath);
        Assert(
            startInfo.FileName == executablePath &&
            !startInfo.UseShellExecute &&
            startInfo.CreateNoWindow &&
            string.IsNullOrEmpty(startInfo.Arguments) &&
            startInfo.ArgumentList.SequenceEqual(validArguments) &&
            SilhouetteWorkerLaunchOptions.TryParse(startInfo.ArgumentList.ToArray(), out var roundTrip) &&
            roundTrip == parsed &&
            Throws<ArgumentException>(() =>
                SilhouetteWorkerLaunch.CreateStartInfo(
                    Path.Combine("relative", "Library"),
                    projectId,
                    executablePath: executablePath)) &&
            Throws<ArgumentException>(() =>
                SilhouetteWorkerLaunch.CreateStartInfo(
                    absoluteRoot,
                    new string('A', 32),
                    executablePath: executablePath)),
            "Worker launch must use ArgumentList with no shell or flattened Arguments string, preserving even metacharacters as one inert root argument.");
    }

    private static void AssertPackagedModelIntegrity()
    {
        const long expectedModelBytes = 25_888_640;
        var modelPath = OnnxSilhouetteFrameSegmenter.GetPackagedModelPath();
        Assert(File.Exists(modelPath),
            "The packaged MODNet model must be copied next to the application output.");
        using var stream = new FileStream(
            modelPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var byteLength = stream.Length;
        var actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        Assert(
            byteLength == expectedModelBytes &&
            actualSha256 == OnnxSilhouetteFrameSegmenter.ModelSha256 &&
            OnnxSilhouetteFrameSegmenter.SourceModelSha256 ==
                OnnxSilhouetteFrameSegmenter.ModelSha256 &&
            Path.GetFileName(modelPath) == "modnet-photographic.onnx",
            "The packaged MODNet asset must retain the reviewed 25,888,640-byte SHA-256 identity and canonical filename.");
    }

    private static bool PlanIsContained(SilhouetteRenderPlan plan) =>
        plan.CanvasWidth > 0 &&
        plan.CanvasHeight > 0 &&
        plan.CameraCropX >= 0 &&
        plan.CameraCropY >= 0 &&
        plan.CameraCropWidth > 0 &&
        plan.CameraCropHeight > 0 &&
        plan.CameraCropX + plan.CameraCropWidth <= plan.CameraWidth &&
        plan.CameraCropY + plan.CameraCropHeight <= plan.CameraHeight &&
        plan.SubjectX >= 0 &&
        plan.SubjectY >= 0 &&
        plan.SubjectWidth >= 2 &&
        plan.SubjectHeight >= 2 &&
        plan.SubjectWidth % 2 == 0 &&
        plan.SubjectHeight % 2 == 0 &&
        plan.SubjectX + plan.SubjectWidth <= plan.CanvasWidth &&
        plan.SubjectY + plan.SubjectHeight <= plan.CanvasHeight;

    private static bool HasPair(
        IReadOnlyList<string> arguments,
        string option,
        string value) =>
        CountPair(arguments, option, value) > 0;

    private static int CountPair(
        IReadOnlyList<string> arguments,
        string option,
        string value) =>
        Enumerable.Range(0, Math.Max(0, arguments.Count - 1))
            .Count(index =>
                arguments[index].Equals(option, StringComparison.Ordinal) &&
                arguments[index + 1].Equals(value, StringComparison.Ordinal));

    private static string[] ReplaceArgument(
        IReadOnlyList<string> arguments,
        int index,
        string value)
    {
        var replaced = arguments.ToArray();
        replaced[index] = value;
        return replaced;
    }

    private static void AssertInitialSelectionAndAggregate(string testRoot)
    {
        var both = CreateFixture(testRoot, "initial-both", landscape: true, portrait: true);
        var initial = SilhouetteRenditionStore.CreateInitial(
            both.Root,
            both.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));
        var landscape = Find(initial, CompositionOrientationIds.Landscape);
        var portrait = Find(initial, CompositionOrientationIds.Portrait);
        var disabledAggregate = SilhouetteRenditionModel.GetAggregateState(initial with
        {
            Outputs = initial.Outputs.Select(output => output with
            {
                State = SilhouetteOutputState.Disabled
            }).ToArray()
        });
        Assert(
            initial.SchemaVersion == SilhouetteRenditionStore.CurrentSchemaVersion &&
            initial.ProjectId == both.Project.ProjectId &&
            initial.Generation == 1 &&
            initial.CompositionRevision == both.Composition.ModifiedUtc.UtcDateTime.Ticks &&
            initial.CompositionSha256.Length == 64 &&
            initial.ModelId == ModelId &&
            initial.ModelSha256 == ModelHash &&
            initial.GameplaySource == both.Project.Manifest.GameplayFingerprint &&
            initial.CameraSource == both.Project.Manifest.CameraLayerFingerprint &&
            initial.Matte.State == SilhouetteMatteState.Pending &&
            landscape.State == SilhouetteOutputState.Pending &&
            portrait.State == SilhouetteOutputState.Pending &&
            initial.AggregateState == SilhouetteRenditionAggregateState.Pending &&
            disabledAggregate == SilhouetteRenditionAggregateState.Disabled,
            "Rendition initialization must bind immutable sources, composition and model identity while selecting both requested outputs.");

        var landscapeOnly = CreateFixture(
            testRoot,
            "initial-landscape",
            landscape: true,
            portrait: false);
        var oneOutput = SilhouetteRenditionStore.CreateInitial(
            landscapeOnly.Root,
            landscapeOnly.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));
        var oneOutputWithMatte = MakeMatteReady(oneOutput);
        Assert(
            Find(oneOutput, CompositionOrientationIds.Landscape).State ==
                SilhouetteOutputState.Pending &&
            Find(oneOutput, CompositionOrientationIds.Portrait).State ==
                SilhouetteOutputState.Disabled &&
            Throws<InvalidDataException>(() => SilhouetteRenditionModel.StartOutput(
                oneOutputWithMatte,
                CompositionOrientationIds.Portrait,
                At(4))),
            "The persisted output set must be normalized from the immutable composition snapshot; a disabled output cannot be started.");
    }

    private static void AssertLegalAndIllegalTransitions(string testRoot)
    {
        var fixture = CreateFixture(testRoot, "transitions", landscape: true, portrait: true);
        var initial = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));
        Assert(
            Throws<InvalidDataException>(() => SilhouetteRenditionModel.BeginMatteCommit(
                initial,
                attemptGeneration: 1,
                MatteArtifact,
                At(1))) &&
            Throws<InvalidDataException>(() => SilhouetteRenditionModel.StartOutput(
                initial,
                CompositionOrientationIds.Landscape,
                At(1))),
            "A matte cannot skip Processing and an output cannot render before the shared matte is ready.");

        var processing = SilhouetteRenditionModel.StartMatte(initial, At(1));
        var matteAttempt = processing.Matte.AttemptGeneration;
        Assert(
            processing.Generation == 2 &&
            matteAttempt == processing.Generation &&
            processing.Matte.Attempts == 1 &&
            processing.Matte.State == SilhouetteMatteState.Processing &&
            processing.AggregateState == SilhouetteRenditionAggregateState.Processing &&
            Throws<InvalidDataException>(() => SilhouetteRenditionModel.CompleteMatte(
                processing,
                matteAttempt,
                MatteArtifact,
                At(2))) &&
            Throws<InvalidDataException>(() => SilhouetteRenditionModel.BeginMatteCommit(
                processing,
                matteAttempt + 1,
                MatteArtifact,
                At(2))),
            "Matte processing must advance generation and reject state-skipping or stale attempt completions.");

        var committing = SilhouetteRenditionModel.BeginMatteCommit(
            processing,
            matteAttempt,
            MatteArtifact,
            At(2));
        Assert(
            committing.Matte.State == SilhouetteMatteState.Committing &&
            committing.Matte.ExpectedTemporaryArtifact == MatteArtifact &&
            Throws<InvalidDataException>(() => SilhouetteRenditionModel.CompleteMatte(
                committing,
                matteAttempt,
                MatteArtifact with { ByteLength = MatteArtifact.ByteLength + 1 },
                At(3))),
            "A commit may publish only the exact length and hash declared at its temporary-artifact boundary.");

        var ready = SilhouetteRenditionModel.CompleteMatte(
            committing,
            matteAttempt,
            MatteArtifact,
            At(3));
        var landscapeRendering = SilhouetteRenditionModel.StartOutput(
            ready,
            CompositionOrientationIds.Landscape,
            At(4));
        var outputAttempt = Find(
            landscapeRendering,
            CompositionOrientationIds.Landscape).AttemptGeneration;
        var landscapeCommitting = SilhouetteRenditionModel.BeginOutputCommit(
            landscapeRendering,
            CompositionOrientationIds.Landscape,
            outputAttempt,
            LandscapeArtifact,
            At(5));
        var landscapeReady = SilhouetteRenditionModel.CompleteOutput(
            landscapeCommitting,
            CompositionOrientationIds.Landscape,
            outputAttempt,
            LandscapeArtifact,
            At(6));
        Assert(
            ready.Matte.AttemptGeneration == matteAttempt &&
            Find(landscapeRendering, CompositionOrientationIds.Landscape).MatteGeneration ==
                matteAttempt &&
            Find(landscapeReady, CompositionOrientationIds.Landscape).State ==
                SilhouetteOutputState.Ready &&
            Find(landscapeReady, CompositionOrientationIds.Landscape).FinalArtifact ==
                LandscapeArtifact &&
            Find(landscapeReady, CompositionOrientationIds.Portrait).State ==
                SilhouetteOutputState.Pending &&
            landscapeReady.AggregateState == SilhouetteRenditionAggregateState.PartiallyReady,
            "An output must bind the one shared matte attempt and publish independently of its sibling.");
    }

    private static void AssertIndependentOutputsAndRetry(string testRoot)
    {
        var fixture = CreateFixture(testRoot, "independence", landscape: true, portrait: true);
        var document = MakeMatteReady(SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0)));
        document = MakeOutputReady(
            document,
            CompositionOrientationIds.Landscape,
            LandscapeArtifact,
            10);
        var portraitRendering = SilhouetteRenditionModel.StartOutput(
            document,
            CompositionOrientationIds.Portrait,
            At(20));
        var failedAttempt = Find(
            portraitRendering,
            CompositionOrientationIds.Portrait).AttemptGeneration;
        var failed = SilhouetteRenditionModel.FailOutput(
            portraitRendering,
            CompositionOrientationIds.Portrait,
            failedAttempt,
            @"C:\Users\private-user\model-crash.txt",
            At(21));
        Assert(
            Find(failed, CompositionOrientationIds.Landscape).State ==
                SilhouetteOutputState.Ready &&
            Find(failed, CompositionOrientationIds.Landscape).FinalArtifact ==
                LandscapeArtifact &&
            Find(failed, CompositionOrientationIds.Portrait).State ==
                SilhouetteOutputState.Failed &&
            Find(failed, CompositionOrientationIds.Portrait).ErrorCode ==
                SilhouetteRenditionModel.DefaultErrorCode &&
            !JsonSerializer.Serialize(failed, JsonOptions).Contains(
                "private-user",
                StringComparison.OrdinalIgnoreCase) &&
            failed.AggregateState == SilhouetteRenditionAggregateState.PartiallyReady,
            "A portrait failure must leave a ready landscape intact and must never persist an arbitrary diagnostic message or path.");

        var retryPending = SilhouetteRenditionModel.RetryOutput(
            failed,
            CompositionOrientationIds.Portrait,
            At(22));
        var retryRendering = SilhouetteRenditionModel.StartOutput(
            retryPending,
            CompositionOrientationIds.Portrait,
            At(23));
        var retryAttempt = Find(
            retryRendering,
            CompositionOrientationIds.Portrait).AttemptGeneration;
        var retryCommitting = SilhouetteRenditionModel.BeginOutputCommit(
            retryRendering,
            CompositionOrientationIds.Portrait,
            retryAttempt,
            PortraitArtifact,
            At(24));
        var complete = SilhouetteRenditionModel.CompleteOutput(
            retryCommitting,
            CompositionOrientationIds.Portrait,
            retryAttempt,
            PortraitArtifact,
            At(25));
        Assert(
            Find(complete, CompositionOrientationIds.Landscape).State ==
                SilhouetteOutputState.Ready &&
            Find(complete, CompositionOrientationIds.Portrait).State ==
                SilhouetteOutputState.Ready &&
            Find(complete, CompositionOrientationIds.Portrait).Attempts == 2 &&
            Find(complete, CompositionOrientationIds.Landscape).MatteGeneration ==
                Find(complete, CompositionOrientationIds.Portrait).MatteGeneration &&
            Find(complete, CompositionOrientationIds.Portrait).MatteGeneration ==
                complete.Matte.AttemptGeneration &&
            complete.AggregateState == SilhouetteRenditionAggregateState.Ready,
            "Retrying one failed output must preserve its ready sibling, use the same shared matte identity, and converge to aggregate Ready.");

        var matteFailureFixture = CreateFixture(
            testRoot,
            "matte-failure",
            landscape: true,
            portrait: false);
        var mattePending = SilhouetteRenditionStore.CreateInitial(
            matteFailureFixture.Root,
            matteFailureFixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));
        var matteProcessing = SilhouetteRenditionModel.StartMatte(mattePending, At(1));
        var matteFailed = SilhouetteRenditionModel.FailMatte(
            matteProcessing,
            matteProcessing.Matte.AttemptGeneration,
            "model-unavailable",
            At(2));
        var matteRetry = SilhouetteRenditionModel.RetryMatte(matteFailed, At(3));
        Assert(
            matteFailed.AggregateState == SilhouetteRenditionAggregateState.Failed &&
            matteFailed.Matte.ErrorCode == "model-unavailable" &&
            matteRetry.Matte.State == SilhouetteMatteState.Pending &&
            matteRetry.Matte.Attempts == 1 &&
            matteRetry.AggregateState == SilhouetteRenditionAggregateState.Pending,
            "A stable matte failure code must remain retryable without erasing attempt history.");
    }

    private static void AssertRecoveryContracts(string testRoot)
    {
        var fixture = CreateFixture(testRoot, "recovery", landscape: true, portrait: true);
        var initial = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));
        var abandonedMatte = SilhouetteRenditionModel.StartMatte(initial, At(1));
        var matteRecovered = SilhouetteRenditionModel.RecoverAbandonedWork(
            abandonedMatte,
            At(2));
        Assert(
            matteRecovered.Matte.State == SilhouetteMatteState.Pending &&
            matteRecovered.Matte.Attempts == 1 &&
            matteRecovered.Matte.AttemptGeneration == 0 &&
            matteRecovered.Generation == abandonedMatte.Generation + 1,
            "Startup recovery must return an abandoned Processing matte to Pending while preserving attempts.");

        var processingAgain = SilhouetteRenditionModel.StartMatte(matteRecovered, At(3));
        var attempt = processingAgain.Matte.AttemptGeneration;
        var committing = SilhouetteRenditionModel.BeginMatteCommit(
            processingAgain,
            attempt,
            MatteArtifact,
            At(4));
        var untouched = SilhouetteRenditionModel.RecoverAbandonedWork(committing, At(5));
        var retry = SilhouetteRenditionModel.RecoverMatteCommit(
            committing,
            attempt,
            verifiedFinalArtifact: null,
            At(5));
        Assert(
            ReferenceEquals(untouched, committing) &&
            retry.Matte.State == SilhouetteMatteState.Pending &&
            retry.Matte.Attempts == 2,
            "Generic recovery must not guess at Committing state; an explicit no-adoption result may return it to Pending.");

        var commitForAdoptionProcessing = SilhouetteRenditionModel.StartMatte(retry, At(6));
        var adoptionAttempt = commitForAdoptionProcessing.Matte.AttemptGeneration;
        var commitForAdoption = SilhouetteRenditionModel.BeginMatteCommit(
            commitForAdoptionProcessing,
            adoptionAttempt,
            MatteArtifact,
            At(7));
        var wrongVerification = SilhouetteRenditionModel.RecoverMatteCommit(
            commitForAdoption,
            adoptionAttempt,
            MatteArtifact with { Sha256 = new string('e', 64) },
            At(8));
        var recommitProcessing = SilhouetteRenditionModel.StartMatte(
            wrongVerification,
            At(9));
        var verifiedAttempt = recommitProcessing.Matte.AttemptGeneration;
        var verifiedCommit = SilhouetteRenditionModel.BeginMatteCommit(
            recommitProcessing,
            verifiedAttempt,
            MatteArtifact,
            At(10));
        var adopted = SilhouetteRenditionModel.RecoverMatteCommit(
            verifiedCommit,
            verifiedAttempt,
            MatteArtifact,
            At(11));
        Assert(
            wrongVerification.Matte.State == SilhouetteMatteState.Pending &&
            adopted.Matte.State == SilhouetteMatteState.Ready &&
            adopted.Matte.FinalArtifact == MatteArtifact,
            "Commit recovery may adopt only an explicitly verified exact artifact; a mismatch must retry instead.");

        var rendering = SilhouetteRenditionModel.StartOutput(
            adopted,
            CompositionOrientationIds.Landscape,
            At(12));
        var renderingRecovered = SilhouetteRenditionModel.RecoverAbandonedWork(
            rendering,
            At(13));
        var renderingAgain = SilhouetteRenditionModel.StartOutput(
            renderingRecovered,
            CompositionOrientationIds.Landscape,
            At(14));
        var outputAttempt = Find(
            renderingAgain,
            CompositionOrientationIds.Landscape).AttemptGeneration;
        var outputCommitting = SilhouetteRenditionModel.BeginOutputCommit(
            renderingAgain,
            CompositionOrientationIds.Landscape,
            outputAttempt,
            LandscapeArtifact,
            At(15));
        var outputUntouched = SilhouetteRenditionModel.RecoverAbandonedWork(
            outputCommitting,
            At(16));
        var outputRetry = SilhouetteRenditionModel.RecoverOutputCommit(
            outputCommitting,
            CompositionOrientationIds.Landscape,
            outputAttempt,
            verifiedFinalArtifact: null,
            At(16));
        var outputRenderingForAdoption = SilhouetteRenditionModel.StartOutput(
            outputRetry,
            CompositionOrientationIds.Landscape,
            At(17));
        var outputAdoptionAttempt = Find(
            outputRenderingForAdoption,
            CompositionOrientationIds.Landscape).AttemptGeneration;
        var outputCommitForAdoption = SilhouetteRenditionModel.BeginOutputCommit(
            outputRenderingForAdoption,
            CompositionOrientationIds.Landscape,
            outputAdoptionAttempt,
            LandscapeArtifact,
            At(18));
        var outputAdopted = SilhouetteRenditionModel.RecoverOutputCommit(
            outputCommitForAdoption,
            CompositionOrientationIds.Landscape,
            outputAdoptionAttempt,
            LandscapeArtifact,
            At(19));
        Assert(
            Find(renderingRecovered, CompositionOrientationIds.Landscape).State ==
                SilhouetteOutputState.Pending &&
            ReferenceEquals(outputUntouched, outputCommitting) &&
            Find(outputRetry, CompositionOrientationIds.Landscape).State ==
                SilhouetteOutputState.Pending &&
            Find(outputAdopted, CompositionOrientationIds.Landscape).State ==
                SilhouetteOutputState.Ready &&
            Find(outputAdopted, CompositionOrientationIds.Portrait).State ==
                SilhouetteOutputState.Pending,
            "Output recovery must retry abandoned Rendering and adopt a Committing artifact without changing its sibling.");
    }

    private static void AssertAtomicCasAndHostileFiles(string testRoot)
    {
        var fixture = CreateFixture(testRoot, "store", landscape: true, portrait: true);
        var initial = SilhouetteRenditionStore.CreateInitial(
            fixture.Root,
            fixture.Project.ProjectId,
            ModelId,
            ModelHash,
            At(0));
        var path = SilhouetteRenditionStore.GetPath(
            fixture.Root,
            fixture.Project.ProjectId);
        var forgedInitial = initial with
        {
            Matte = initial.Matte with
            {
                State = SilhouetteMatteState.Ready,
                Attempts = 1,
                AttemptGeneration = 1,
                StartedUtc = initial.CreatedUtc,
                FinalArtifact = MatteArtifact
            }
        };
        Assert(
            Throws<InvalidDataException>(() =>
                SilhouetteRenditionStore.SaveAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        forgedInitial,
                        expectedGeneration: 0)
                    .GetAwaiter()
                    .GetResult()) &&
            !File.Exists(path),
            "The first durable state must be canonical Pending; a shape-valid forged Ready document must not bootstrap a project.");
        var saved = SilhouetteRenditionStore.SaveAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                initial,
                expectedGeneration: 0)
            .GetAwaiter()
            .GetResult();
        var originalBytes = File.ReadAllBytes(path);
        var loaded = SilhouetteRenditionStore.Load(
            fixture.Root,
            fixture.Project.ProjectId);
        Assert(
            loaded.LoadedFromDisk && loaded.Document is not null &&
            loaded.Document.ProjectId == saved.ProjectId &&
            loaded.Document.CompositionSha256 == saved.CompositionSha256 &&
            loaded.Document.ModelSha256 == saved.ModelSha256 &&
            loaded.Document.Outputs.SequenceEqual(saved.Outputs) &&
            loaded.Document!.Generation == 1 &&
            !Encoding.UTF8.GetString(originalBytes).Contains(
                Path.GetFullPath(fixture.Root),
                StringComparison.OrdinalIgnoreCase),
            "The bounded rendition document must round-trip without storing absolute project paths.");

        var processing = SilhouetteRenditionModel.StartMatte(initial, At(1));
        var forgedReady = initial with
        {
            Generation = 2,
            UpdatedUtc = At(1),
            Matte = initial.Matte with
            {
                State = SilhouetteMatteState.Ready,
                Attempts = 1,
                AttemptGeneration = 2,
                StartedUtc = At(1),
                UpdatedUtc = At(1),
                FinalArtifact = MatteArtifact
            }
        };
        var swappedModel = processing with
        {
            ModelId = "clipcord-person-matte-swapped",
            ModelSha256 = new string('9', 64)
        };
        Assert(
            Throws<InvalidDataException>(() =>
                SilhouetteRenditionStore.SaveAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        forgedReady,
                        expectedGeneration: 1)
                    .GetAwaiter()
                    .GetResult()) &&
            Throws<InvalidDataException>(() =>
                SilhouetteRenditionStore.SaveAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        swappedModel,
                        expectedGeneration: 1)
                    .GetAwaiter()
                    .GetResult()) &&
            File.ReadAllBytes(path).SequenceEqual(originalBytes),
            "Durable persistence must reject a Pending-to-Ready skip and immutable model substitution even when both candidates are shape-valid and advance one generation.");
        SilhouetteRenditionStore.SaveAsync(
                fixture.Root,
                fixture.Project.ProjectId,
                processing,
                expectedGeneration: 1)
            .GetAwaiter()
            .GetResult();
        var resetAttemptHistory = SilhouetteRenditionModel.RecoverAbandonedWork(
            processing,
            At(2)) with
        {
            Matte = SilhouetteRenditionModel.RecoverAbandonedWork(processing, At(2)).Matte with
            {
                Attempts = 0
            }
        };
        var processingBytes = File.ReadAllBytes(path);
        Assert(
            Throws<InvalidDataException>(() =>
                SilhouetteRenditionStore.SaveAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        resetAttemptHistory,
                        expectedGeneration: processing.Generation)
                    .GetAwaiter()
                    .GetResult()) &&
            File.ReadAllBytes(path).SequenceEqual(processingBytes),
            "A shape-valid recovery candidate must not reset persisted attempt history.");
        var staleCandidate = SilhouetteRenditionModel.StartMatte(initial, At(2));
        Assert(
            Throws<SilhouetteRenditionConcurrencyException>(() =>
                SilhouetteRenditionStore.SaveAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        staleCandidate,
                        expectedGeneration: 1)
                    .GetAwaiter()
                    .GetResult()),
            "A stale generation must never publish over a newer rendition state.");

        var beforeCancelledSave = File.ReadAllBytes(path);
        var committing = SilhouetteRenditionModel.BeginMatteCommit(
            processing,
            processing.Matte.AttemptGeneration,
            MatteArtifact,
            At(3));
        using (var cancellation = new CancellationTokenSource())
        {
            Assert(
                Throws<OperationCanceledException>(() =>
                    SilhouetteRenditionStore.SaveAsync(
                            fixture.Root,
                            fixture.Project.ProjectId,
                            committing,
                            expectedGeneration: processing.Generation,
                            cancellation.Token,
                            beforeCommit: cancellation.Cancel)
                        .GetAwaiter()
                        .GetResult()) &&
                File.ReadAllBytes(path).SequenceEqual(beforeCancelledSave) &&
                !Directory.EnumerateFiles(
                        fixture.Project.ProjectDirectory,
                        $".{SilhouetteRenditionStore.FileName}.*.tmp",
                        SearchOption.TopDirectoryOnly)
                    .Any(),
                "Cancellation after the durable temporary write must preserve the previous generation and clean only the exact owned temp file.");
        }

        var validBytes = File.ReadAllBytes(path);
        AssertRejectedWithoutRewrite(
            path,
            Encoding.UTF8.GetBytes("{"),
            () => SilhouetteRenditionStore.Load(fixture.Root, fixture.Project.ProjectId),
            SilhouetteRenditionLoadStatus.Corrupt,
            "Corrupt rendition JSON must be rejected without rewriting it.");
        Assert(
            Throws<InvalidDataException>(() =>
                SilhouetteRenditionStore.SaveAsync(
                        fixture.Root,
                        fixture.Project.ProjectId,
                        committing,
                        expectedGeneration: processing.Generation)
                    .GetAwaiter()
                    .GetResult()) &&
            File.ReadAllBytes(path).SequenceEqual(Encoding.UTF8.GetBytes("{")),
            "A save must not use a corrupt file as replace authorization.");
        File.WriteAllBytes(path, validBytes);

        var oversized = Enumerable.Repeat(
                (byte)'x',
                SilhouetteRenditionStore.MaximumDocumentBytes + 1)
            .ToArray();
        AssertRejectedWithoutRewrite(
            path,
            oversized,
            () => SilhouetteRenditionStore.Load(fixture.Root, fixture.Project.ProjectId),
            SilhouetteRenditionLoadStatus.Invalid,
            "Oversized rendition state must be rejected without an unbounded read or rewrite.");
        File.WriteAllBytes(path, validBytes);

        var newer = Encoding.UTF8.GetBytes("{\"schemaVersion\":99}");
        AssertRejectedWithoutRewrite(
            path,
            newer,
            () => SilhouetteRenditionStore.Load(fixture.Root, fixture.Project.ProjectId),
            SilhouetteRenditionLoadStatus.UnsupportedSchema,
            "A newer rendition schema must be preserved and rejected rather than downgraded.");
        File.WriteAllBytes(path, validBytes);

        var validJson = Encoding.UTF8.GetString(validBytes);
        foreach (var (name, hostileJson) in new[]
                 {
                     (
                         "composition hash",
                         validJson.Replace(
                             $"\"compositionSha256\": \"{processing.CompositionSha256}\"",
                             "\"compositionSha256\": null",
                             StringComparison.Ordinal)),
                     (
                         "fingerprint hash",
                         validJson.Replace(
                             $"\"sha256\": \"{processing.GameplaySource.Sha256}\"",
                             "\"sha256\": null",
                             StringComparison.Ordinal)),
                     (
                         "orientation id",
                         validJson.Replace(
                             $"\"orientationId\": \"{CompositionOrientationIds.Landscape}\"",
                             "\"orientationId\": null",
                             StringComparison.Ordinal))
                 })
        {
            Assert(hostileJson != validJson,
                $"The hostile {name} fixture must mutate the serialized rendition document.");
            AssertRejectedWithoutRewrite(
                path,
                Encoding.UTF8.GetBytes(hostileJson),
                () => SilhouetteRenditionStore.Load(fixture.Root, fixture.Project.ProjectId),
                SilhouetteRenditionLoadStatus.Invalid,
                $"A typed-null {name} must return Invalid without throwing or rewriting the hostile file.");
        }
        File.WriteAllBytes(path, validBytes);

        var otherProjectId = new string('f', 32);
        var mismatchedBytes = JsonSerializer.SerializeToUtf8Bytes(
            processing with { ProjectId = otherProjectId },
            JsonOptions);
        AssertRejectedWithoutRewrite(
            path,
            mismatchedBytes,
            () => SilhouetteRenditionStore.Load(fixture.Root, fixture.Project.ProjectId),
            SilhouetteRenditionLoadStatus.ProjectMismatch,
            "Rendition state from another project must be rejected without rewriting it.");
        File.WriteAllBytes(path, validBytes);

        AssertReparseRejectedWhenSupported(fixture, path, validBytes);
        var restored = SilhouetteRenditionStore.Load(fixture.Root, fixture.Project.ProjectId);
        Assert(
            restored.LoadedFromDisk && restored.Document!.Generation == processing.Generation,
            "Hostile-file probes must leave the original valid generation recoverable.");
    }

    private static SilhouetteRenditionDocument MakeMatteReady(
        SilhouetteRenditionDocument document)
    {
        var processing = SilhouetteRenditionModel.StartMatte(document, At(1));
        var attempt = processing.Matte.AttemptGeneration;
        var committing = SilhouetteRenditionModel.BeginMatteCommit(
            processing,
            attempt,
            MatteArtifact,
            At(2));
        return SilhouetteRenditionModel.CompleteMatte(
            committing,
            attempt,
            MatteArtifact,
            At(3));
    }

    private static SilhouetteRenditionDocument MakeOutputReady(
        SilhouetteRenditionDocument document,
        string orientationId,
        CaptureMediaFingerprint artifact,
        int timeOffset)
    {
        var rendering = SilhouetteRenditionModel.StartOutput(
            document,
            orientationId,
            At(timeOffset));
        var attempt = Find(rendering, orientationId).AttemptGeneration;
        var committing = SilhouetteRenditionModel.BeginOutputCommit(
            rendering,
            orientationId,
            attempt,
            artifact,
            At(timeOffset + 1));
        return SilhouetteRenditionModel.CompleteOutput(
            committing,
            orientationId,
            attempt,
            artifact,
            At(timeOffset + 2));
    }

    private static Fixture CreateFixture(
        string testRoot,
        string name,
        bool landscape,
        bool portrait)
    {
        var root = Path.Combine(testRoot, "silhouette-renditions", name);
        var gameDirectory = Path.Combine(root, "Library", "Game", "Duskfade");
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(root);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var gameplayPath = Path.Combine(
            gameDirectory,
            $"Duskfade__2026-08-25__12-00-{name.Length:00}.mp4");
        var cameraStage = Path.Combine(stagingDirectory, $"{name}-camera.mp4");
        File.WriteAllBytes(
            gameplayPath,
            Enumerable.Range(1, 96).Select(value => (byte)value).ToArray());
        File.WriteAllBytes(
            cameraStage,
            Enumerable.Range(1, 128).Select(value => (byte)(255 - value)).ToArray());
        var createdUtc = At(name.Length);
        var settings = CaptureSettings.Default with
        {
            LibraryRoot = root,
            SilhouetteLandscapeEnabled = landscape,
            SilhouettePortraitEnabled = portrait
        };
        var composition = CaptureCompositionSnapshotFactory.Create(
            settings,
            gameplayPath,
            mirrorCamera: true,
            createdUtc);
        var project = CaptureProjectStore.SaveReactionCameraLayerAsync(
                root,
                gameplayPath,
                cameraStage,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10.1),
                TimeSpan.FromSeconds(15),
                mirrorCamera: true,
                composition,
                createdUtc)
            .GetAwaiter()
            .GetResult();
        var committedComposition = CaptureCompositionStore.LoadOrDefault(
            root,
            project.ProjectId,
            createdUtc);
        Assert(committedComposition.LoadedFromDisk,
            "The rendition fixture must contain a committed composition snapshot.");
        return new Fixture(root, project, committedComposition.Document);
    }

    private static void AssertRejectedWithoutRewrite(
        string path,
        byte[] hostileBytes,
        Func<SilhouetteRenditionLoadResult> load,
        SilhouetteRenditionLoadStatus expectedStatus,
        string message)
    {
        File.WriteAllBytes(path, hostileBytes);
        var before = File.ReadAllBytes(path);
        var result = load();
        Assert(
            result.Status == expectedStatus && result.Document is null &&
            File.ReadAllBytes(path).SequenceEqual(before),
            message);
    }

    private static void AssertReparseRejectedWhenSupported(
        Fixture fixture,
        string path,
        byte[] validBytes)
    {
        var externalTarget = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            $"{fixture.Project.ProjectId}-external-renditions.json");
        File.WriteAllBytes(externalTarget, validBytes);
        var linkCreated = false;
        try
        {
            File.Delete(path);
            File.CreateSymbolicLink(path, externalTarget);
            linkCreated = true;
            var targetBefore = File.ReadAllBytes(externalTarget);
            var result = SilhouetteRenditionStore.Load(
                fixture.Root,
                fixture.Project.ProjectId);
            Assert(
                result.Status == SilhouetteRenditionLoadStatus.Unavailable &&
                result.Document is null &&
                File.ReadAllBytes(externalTarget).SequenceEqual(targetBefore),
                "A reparse-point rendition file must be rejected without touching its target.");
        }
        catch (Exception exception) when (
            !linkCreated && exception is UnauthorizedAccessException or IOException or
                PlatformNotSupportedException)
        {
            // Creating a symlink may require Developer Mode or elevated permission. All
            // platforms still compile the probe; supported test hosts exercise the gate.
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
            File.WriteAllBytes(path, validBytes);
            try { File.Delete(externalTarget); }
            catch { }
        }
    }

    private static void AssertArtifactReparseRejectedWhenSupported(
        Fixture fixture,
        CaptureMediaFingerprint expectedFingerprint,
        string finalPath)
    {
        var originalBytes = File.ReadAllBytes(finalPath);
        var externalTarget = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            $"{fixture.Project.ProjectId}-external-silhouette.mp4");
        File.WriteAllBytes(externalTarget, originalBytes);
        var finalLinkCreated = false;
        try
        {
            File.Delete(finalPath);
            File.CreateSymbolicLink(finalPath, externalTarget);
            finalLinkCreated = true;
            var targetBefore = File.ReadAllBytes(externalTarget);
            Assert(
                Throws<IOException>(() =>
                    SilhouetteArtifactStore.ValidateOutputAsync(
                            fixture.Root,
                            fixture.Project.ProjectId,
                            CompositionOrientationIds.Landscape,
                            expectedFingerprint)
                        .GetAwaiter()
                        .GetResult()) &&
                File.ReadAllBytes(externalTarget).SequenceEqual(targetBefore),
                "A fixed silhouette artifact reparse point must be rejected without reading or modifying its target.");
        }
        catch (Exception exception) when (
            !finalLinkCreated && exception is UnauthorizedAccessException or IOException or
                PlatformNotSupportedException)
        {
            // Creating a symlink may require Developer Mode or elevation. Supported hosts
            // exercise the same component-by-component reparse gate used in production.
        }
        finally
        {
            try { if (File.Exists(finalPath)) File.Delete(finalPath); }
            catch { }
            File.WriteAllBytes(finalPath, originalBytes);
            try { File.Delete(externalTarget); }
            catch { }
        }
    }

    private static void AssertArtifactDirectoryJunctionRejected(
        Fixture fixture,
        CaptureMediaFingerprint expectedFingerprint,
        string finalPath)
    {
        var projectDirectory = fixture.Project.ProjectDirectory;
        var junctionTarget = Path.Combine(
            Path.GetDirectoryName(fixture.Root)!,
            $"{fixture.Project.ProjectId}-junction-target");
        Assert(!Directory.Exists(junctionTarget) && !File.Exists(junctionTarget),
            "The mandatory artifact junction target must begin absent.");
        var finalBytes = File.ReadAllBytes(finalPath);
        var manifestName = Path.GetFileName(fixture.Project.ManifestPath);
        Directory.Move(projectDirectory, junctionTarget);
        var junctionCreated = false;
        try
        {
            CreateDirectoryJunction(projectDirectory, junctionTarget);
            junctionCreated = true;
            Assert(
                File.GetAttributes(projectDirectory).HasFlag(FileAttributes.ReparsePoint),
                "The mandatory artifact fixture must be a real directory reparse point.");
            var targetManifest = Path.Combine(junctionTarget, manifestName);
            var targetManifestBefore = File.ReadAllBytes(targetManifest);
            Assert(
                Throws<IOException>(() =>
                    SilhouetteArtifactStore.ValidateOutputAsync(
                            fixture.Root,
                            fixture.Project.ProjectId,
                            CompositionOrientationIds.Landscape,
                            expectedFingerprint)
                        .GetAwaiter()
                        .GetResult()) &&
                File.ReadAllBytes(targetManifest).SequenceEqual(targetManifestBefore) &&
                File.ReadAllBytes(finalPath).SequenceEqual(finalBytes),
                "A project-directory junction must be rejected deterministically without reading, rewriting, or deleting its target or the visible rendition.");
        }
        finally
        {
            if (junctionCreated && Directory.Exists(projectDirectory))
            {
                Directory.Delete(projectDirectory);
            }
            if (!Directory.Exists(projectDirectory) && Directory.Exists(junctionTarget))
            {
                Directory.Move(junctionTarget, projectDirectory);
            }
        }
        Assert(
            Directory.Exists(projectDirectory) &&
            !Directory.Exists(junctionTarget) &&
            File.ReadAllBytes(finalPath).SequenceEqual(finalBytes),
            "Removing the mandatory test junction must restore the real project directory without touching its target contents or visible rendition.");
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "Windows did not start the mandatory artifact junction helper.");
        Assert(
            process.WaitForExit(5000),
            "The mandatory artifact junction helper exceeded its five-second deadline.");
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "The mandatory artifact junction fixture could not be prepared: " +
                process.StandardError.ReadToEnd());
        }
    }

    private static SilhouetteOutputRendition Find(
        SilhouetteRenditionDocument document,
        string orientationId) => document.Outputs.Single(output =>
        output.OrientationId.Equals(orientationId, StringComparison.Ordinal));

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 8, 25, 16, 0, 0, TimeSpan.Zero)
            .AddSeconds(seconds);

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static bool DoesNotThrow(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Fixture(
        string Root,
        CaptureProjectSaveResult Project,
        CaptureCompositionDocument Composition);
}
