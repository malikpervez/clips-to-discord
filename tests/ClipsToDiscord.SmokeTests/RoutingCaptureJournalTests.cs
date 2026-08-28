using ClipsToDiscord;

internal static class RoutingCaptureJournalTests
{
    internal static void Run(string testRoot) => RunAsync(testRoot).GetAwaiter().GetResult();

    private static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "routing-capture-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await AssertOriginalWriteAheadRecoveryAsync(Path.Combine(root, "original-intent"));
            await AssertColdRestartOriginalIntentDiscoveryAsync(Path.Combine(root, "cold-restart"));
            await AssertHappyPathCanonicalIdentityAndPromotionAsync(Path.Combine(root, "happy"));
            await AssertFailureNoCameraMissingAndImmutableReplayAsync(Path.Combine(root, "edges"));
            await AssertStaleAttemptCannotPromoteAsync(Path.Combine(root, "stale-attempt"));
            await AssertPromotionIntentReparseAncestorsRejectedAsync(Path.Combine(root, "reparse"));
            await AssertBoundedResumableStartupReconciliationAsync(Path.Combine(root, "paging"));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static async Task AssertColdRestartOriginalIntentDiscoveryAsync(string root)
    {
        var now = new DateTimeOffset(2026, 8, 27, 11, 30, 0, TimeSpan.Zero);
        foreach (var crashAfterMove in new[] { false, true })
        {
            var caseRoot = Path.Combine(root, crashAfterMove ? "after-move" : "before-move");
            var staging = CaptureLibraryLayout.GetStagingDirectory(caseRoot);
            var game = Path.Combine(caseRoot, "Library", "Game", "Cold Game");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(game);
            var staged = Path.Combine(staging, "cold-stage.mp4");
            var destination = Path.Combine(game, "cold-final.mp4");
            await File.WriteAllBytesAsync(staged, crashAfterMove ? [81, 82] : [71, 72]);
            CaptureJournalOriginalPromotionIntent? returnedIntent =
                await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
                    caseRoot,
                    staged,
                    destination,
                    CaptureJournalSourceKind.InstantReplay,
                    "Cold Game",
                    now,
                    TimeSpan.FromSeconds(6),
                    1280,
                    720,
                    reactionCameraRequested: false,
                    [],
                    now: now);
            var expectedId = returnedIntent.ClipId;
            if (crashAfterMove)
            {
                Assert((await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
                           caseRoot,
                           expectedId)).Status == CaptureJournalPromotionStatus.DestinationReady,
                    "The after-move cold-start fixture must stop between promotion and journal commit.");
            }

            // Simulate a new process: discard every returned record and recover only from disk.
            returnedIntent = null;
            var page = CaptureJournalPromotionIntentStore.ReadOriginalClipIdPage(
                caseRoot,
                maximumEntries: 1);
            Assert(page.ClipIds.Count == 1 && page.ClipIds[0] == expectedId && !page.HasMore,
                "Cold startup must discover the persisted original intent without an in-memory clip id.");
            var handler = new RecordingOriginalPromotionHandler();
            var summary = await CaptureJournalOriginalPromotionStartupReconciler.ReconcileAsync(
                caseRoot,
                handler,
                maximumEntries: 1,
                maximumDuration: TimeSpan.FromSeconds(5));
            var discoveredId = handler.Items.Single().ClipId;
            Assert(summary.Inspected == 1 && summary.Recoverable == 1 &&
                   summary.NeedsAttention == 0 && discoveredId == expectedId &&
                   handler.Items[0].Inspection.Status ==
                   (crashAfterMove
                       ? CaptureJournalPromotionStatus.DestinationReady
                       : CaptureJournalPromotionStatus.MoveRequired),
                "Cold reconciliation must identify the exact resumable crash boundary.");
            if (!crashAfterMove)
            {
                Assert((await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
                           caseRoot,
                           discoveredId)).Status == CaptureJournalPromotionStatus.DestinationReady,
                    "A pre-move crash must resume using only its enumerated clip id.");
            }
            _ = await CaptureJournalPromotionIntentStore.CommitOriginalAsync(
                caseRoot,
                discoveredId,
                now: now.AddSeconds(1));
            await CaptureJournalPromotionIntentStore.CompleteOriginalAsync(caseRoot, discoveredId);
            Assert(CaptureJournalPromotionIntentStore.ReadOriginalClipIdPage(
                       caseRoot,
                       maximumEntries: 1).ClipIds.Count == 0,
                "Cold recovery must remove the discovered intent only after journal commit.");
        }

        var pagingRoot = Path.Combine(root, "paging");
        var pagingStage = CaptureLibraryLayout.GetStagingDirectory(pagingRoot);
        var pagingGame = Path.Combine(pagingRoot, "Library", "Game", "Paging Cold Game");
        Directory.CreateDirectory(pagingStage);
        Directory.CreateDirectory(pagingGame);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 3; index++)
        {
            var staged = Path.Combine(pagingStage, $"stage-{index}.mp4");
            await File.WriteAllBytesAsync(staged, [(byte)(91 + index)]);
            var prepared = await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
                pagingRoot,
                staged,
                Path.Combine(pagingGame, $"final-{index}.mp4"),
                CaptureJournalSourceKind.ManualCapture,
                "Paging Cold Game",
                now.AddSeconds(index),
                TimeSpan.FromSeconds(4),
                1280,
                720,
                false,
                [],
                now: now.AddSeconds(index));
            expected.Add(prepared.ClipId);
        }
        await File.WriteAllTextAsync(
            Path.Combine(
                CaptureJournalPromotionIntentStore.GetOriginalDirectory(pagingRoot),
                "not-a-clip.original.json"),
            "ignored");
        var firstPage = CaptureJournalPromotionIntentStore.ReadOriginalClipIdPage(
            pagingRoot,
            maximumEntries: 2);
        var secondPage = CaptureJournalPromotionIntentStore.ReadOriginalClipIdPage(
            pagingRoot,
            maximumEntries: 2,
            afterClipId: firstPage.NextCursor);
        Assert(firstPage.ClipIds.Count == 2 && firstPage.HasMore &&
               firstPage.NextCursor is not null &&
               secondPage.ClipIds.Count == 1 && !secondPage.HasMore &&
               firstPage.ClipIds.Concat(secondPage.ClipIds).ToHashSet(StringComparer.Ordinal)
                   .SetEquals(expected),
            "Original intent discovery must enforce the exact bound and resume without repeats or starvation.");
    }

    private static async Task AssertOriginalWriteAheadRecoveryAsync(string root)
    {
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 8, 27, 11, 0, 0, TimeSpan.Zero);
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(root);
        var gameDirectory = Path.Combine(root, "Library", "Game", "Intent Game");
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(gameDirectory);
        var staged = Path.Combine(stagingDirectory, "gameplay-stage.mp4");
        var destination = Path.Combine(gameDirectory, "intent-clip.mp4");
        await File.WriteAllBytesAsync(staged, [31, 32, 33, 34]);
        var intent = await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
            root,
            staged,
            destination,
            CaptureJournalSourceKind.InstantReplay,
            "Intent Game",
            now,
            TimeSpan.FromSeconds(15),
            1920,
            1080,
            reactionCameraRequested: false,
            [],
            now: now);
        Assert(File.Exists(CaptureJournalPromotionIntentStore.GetOriginalPath(root, intent.ClipId)) &&
               !File.Exists(destination),
            "The original write-ahead record must reach disk before gameplay promotion begins.");
        Assert((await CaptureJournalPromotionIntentStore.InspectOriginalAsync(root, intent.ClipId)).Status ==
               CaptureJournalPromotionStatus.MoveRequired,
            "A restart before the gameplay move must recover the exact staged original.");
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalPromotionIntentStore.CommitOriginalAsync(root, intent.ClipId),
            "The capture journal cannot be created before the intended gameplay bytes are promoted.");

        var promoted = await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
            root,
            intent.ClipId);
        Assert(promoted.Status == CaptureJournalPromotionStatus.DestinationReady &&
               File.Exists(destination) && !File.Exists(staged),
            "A restart after intent flush must finish the exact staged-to-final gameplay rename.");
        Assert((await CaptureJournalPromotionIntentStore.InspectOriginalAsync(root, intent.ClipId)).Status ==
               CaptureJournalPromotionStatus.DestinationReady,
            "A restart after the gameplay move but before journal commit must remain recoverable.");

        var journal = await CaptureJournalPromotionIntentStore.CommitOriginalAsync(
            root,
            intent.ClipId,
            now: now.AddSeconds(1));
        Assert(journal.State == CaptureJournalState.OriginalCommitted &&
               journal.Clip.Original.Fingerprint == intent.Fingerprint,
            "Original recovery must bind the durable journal to the write-ahead fingerprint.");
        Assert((await CaptureJournalPromotionIntentStore.InspectOriginalAsync(root, intent.ClipId)).Status ==
               CaptureJournalPromotionStatus.AlreadyJournaled,
            "A restart after journal commit must recognize the exact durable original.");

        await File.AppendAllTextAsync(destination, "changed");
        Assert((await CaptureJournalPromotionIntentStore.InspectOriginalAsync(root, intent.ClipId)).Status ==
               CaptureJournalPromotionStatus.FingerprintMismatch,
            "Original intent cleanup must validate the destination bytes, not just journal metadata.");
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalPromotionIntentStore.CompleteOriginalAsync(root, intent.ClipId),
            "A changed original must keep its recovery intent for diagnosis.");
        await File.WriteAllBytesAsync(destination, [31, 32, 33, 34]);
        await CaptureJournalPromotionIntentStore.CompleteOriginalAsync(root, intent.ClipId);
        Assert((await CaptureJournalPromotionIntentStore.InspectOriginalAsync(root, intent.ClipId)).Status ==
               CaptureJournalPromotionStatus.Missing,
            "Original intent cleanup is terminal only after exact journal and disk validation.");
    }

    private static async Task AssertHappyPathCanonicalIdentityAndPromotionAsync(string root)
    {
        var now = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var fixture = await CreateCameraFixtureAsync(
            root,
            "clip.mp4",
            [CaptureJournalArtifactKinds.Landscape],
            now);
        var original = fixture.Journal;
        Assert(original.State == CaptureJournalState.OriginalCommitted &&
               original.Generation == 1 &&
               !original.CameraPresent &&
               original.Artifacts.Count == 0,
            "The initial journal entry must contain exactly one immutable original snapshot.");
        Assert(original.Clip.ClipId == fixture.Project.ProjectId &&
               original.Clip.Original.ArtifactId == $"{original.Clip.ClipId}:original" &&
               original.Clip.Original.Fingerprint.ByteLength == 5 &&
               original.Clip.Original.Fingerprint.Sha256.All(character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "The journal must bind the project id and lower-case source fingerprint exactly.");

        var pending = await CaptureJournalStore.BeginCameraAsync(
            root,
            original.Clip.ClipId,
            original.Generation,
            now: now.AddSeconds(1));
        var camera = await CaptureJournalStore.CreateArtifactAsync(
            root,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.ReactionCamera,
            fixture.Project.CameraLayerPath);
        await File.AppendAllTextAsync(fixture.Project.CameraLayerPath, "changed");
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalStore.AttachCameraAsync(
                root,
                original.Clip.ClipId,
                pending.Generation,
                camera,
                now: now.AddSeconds(2)),
            "Camera readiness must revalidate disk bytes while the journal transition is locked.");
        await File.WriteAllBytesAsync(fixture.Project.CameraLayerPath, [6, 7, 8]);
        var cameraAttached = await CaptureJournalStore.AttachCameraAsync(
            root,
            original.Clip.ClipId,
            pending.Generation,
            camera,
            now: now.AddSeconds(2));
        Assert(cameraAttached.State == CaptureJournalState.CameraPending &&
               cameraAttached.Generation == 3 &&
               cameraAttached.ProcessingAttemptEpoch == 1 &&
               CaptureJournalModel.IsClipId(cameraAttached.ProcessingAttemptId) &&
               cameraAttached.CameraPresent &&
               cameraAttached.Artifacts.Count == 1,
            "Camera attachment must preserve pending state until the requested output is ready.");

        var decoyCameraPath = Path.Combine(root, "Library", "Game", "Test Game", "decoy-camera.mp4");
        await File.WriteAllBytesAsync(decoyCameraPath, [91, 92]);
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalStore.CreateArtifactAsync(
                root,
                original.Clip.ClipId,
                CaptureJournalArtifactKinds.ReactionCamera,
                decoyCameraPath),
            "A same-library MP4 must not be adopted as another project's camera layer.");

        var directDecoy = new CaptureJournalArtifact(
            CaptureJournalModel.CreateArtifactId(
                original.Clip.ClipId,
                CaptureJournalArtifactKinds.Landscape),
            CaptureJournalArtifactKinds.Landscape,
            Path.GetRelativePath(root, decoyCameraPath).Replace('\\', '/'),
            new CaptureJournalFingerprint(
                2,
                await ContentIdentity.ComputeSha256Async(
                    decoyCameraPath,
                    CancellationToken.None)));
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalStore.AttachRenditionAsync(
                root,
                original.Clip.ClipId,
                cameraAttached.Generation,
                directDecoy,
                now: now.AddSeconds(3)),
            "A transition must re-check canonical paths even when an artifact record is constructed directly.");

        var portraitPath = CaptureJournalStore.GetCanonicalArtifactPath(
            root,
            original.Clip,
            CaptureJournalArtifactKinds.Portrait);
        await File.WriteAllBytesAsync(portraitPath, [41, 42, 43]);
        var unrequestedPortrait = await CaptureJournalStore.CreateArtifactAsync(
            root,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.Portrait,
            portraitPath);
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalStore.CompleteRenditionsAsync(
                root,
                original.Clip.ClipId,
                cameraAttached.Generation,
                [unrequestedPortrait],
                now: now.AddSeconds(3)),
            "CompleteRenditions must reject every unrequested output kind, even at its canonical path.");

        var staged = Path.Combine(
            fixture.Project.ProjectDirectory,
            ".silhouette-landscape.mp4.00000000000000000001.test.tmp");
        await File.WriteAllBytesAsync(staged, [9, 10, 11]);
        var intent = await CaptureJournalPromotionIntentStore.PrepareAsync(
            root,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape,
            cameraAttached.Generation,
            staged,
            now: now.AddSeconds(3));
        Assert(new FileInfo(CaptureJournalPromotionIntentStore.GetPath(
                   root,
                   original.Clip.ClipId,
                   CaptureJournalArtifactKinds.Landscape)).Length is > 0 and
               <= CaptureJournalPromotionIntentStore.MaximumDocumentBytes,
            "The write-ahead promotion intent must be persisted within its strict size bound.");
        var beforeMove = await CaptureJournalPromotionIntentStore.InspectAsync(
            root,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape);
        Assert(beforeMove.Status == CaptureJournalPromotionStatus.MoveRequired &&
               beforeMove.Intent == intent,
            "A prepared promotion with intact staged bytes must be resumable as MoveRequired.");
        var promoted = await CaptureJournalPromotionIntentStore.PromoteAsync(
            root,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape);
        Assert(promoted.Status == CaptureJournalPromotionStatus.DestinationReady &&
               promoted.Artifact is not null &&
               !File.Exists(staged) &&
               File.Exists(CaptureJournalStore.GetCanonicalArtifactPath(
                   root,
                   original.Clip,
                   CaptureJournalArtifactKinds.Landscape)),
            "Promotion must atomically rename only to the canonical destination and expose recovery state.");
        var landscapePath = CaptureJournalStore.GetCanonicalArtifactPath(
            root,
            original.Clip,
            CaptureJournalArtifactKinds.Landscape);
        await File.AppendAllTextAsync(landscapePath, "changed-before-attach");
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalStore.AttachRenditionAsync(
                root,
                original.Clip.ClipId,
                cameraAttached.Generation,
                promoted.Artifact!,
                now: now.AddSeconds(4)),
            "Rendition readiness must revalidate disk bytes while the transition is locked.");
        await File.WriteAllBytesAsync(landscapePath, [9, 10, 11]);
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalPromotionIntentStore.CompleteAsync(
                root,
                original.Clip.ClipId,
                CaptureJournalArtifactKinds.Landscape),
            "The write-ahead intent must survive until its exact artifact is durably journaled.");

        var ready = await CaptureJournalStore.AttachRenditionAsync(
            root,
            original.Clip.ClipId,
            cameraAttached.Generation,
            promoted.Artifact!,
            now: now.AddSeconds(4));
        Assert(ready.State == CaptureJournalState.RenditionsReady &&
               ready.Generation == 4 &&
               ready.Artifacts.Count == 2,
            "The journal must become ready after its one requested canonical rendition is attached.");
        var afterAttach = await CaptureJournalPromotionIntentStore.InspectAsync(
            root,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape);
        Assert(afterAttach.Status == CaptureJournalPromotionStatus.AlreadyJournaled,
            "Startup inspection must recognize the crash point after attachment but before intent cleanup.");
        await File.AppendAllTextAsync(landscapePath, "changed");
        Assert((await CaptureJournalPromotionIntentStore.InspectAsync(
                   root,
                   original.Clip.ClipId,
                   CaptureJournalArtifactKinds.Landscape)).Status ==
               CaptureJournalPromotionStatus.FingerprintMismatch,
            "AlreadyJournaled must not be reported after the destination bytes change.");
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalPromotionIntentStore.CompleteAsync(
                root,
                original.Clip.ClipId,
                CaptureJournalArtifactKinds.Landscape),
            "Promotion cleanup must retain an intent whose journaled bytes no longer validate.");

        var invalidMediaHandler = new RecordingReconciliationHandler();
        _ = await CaptureJournalStartupReconciler.ReconcileAsync(
            root,
            invalidMediaHandler,
            maximumEntries: 10,
            maximumDuration: TimeSpan.FromSeconds(5));
        var invalidReady = invalidMediaHandler.Items.Single(item =>
            item.ClipId == original.Clip.ClipId);
        Assert(!invalidReady.MediaValidated && !invalidReady.CanPlanDeliveries &&
               invalidReady.ArtifactStatuses[CaptureJournalArtifactKinds.Landscape] ==
               CaptureJournalArtifactValidationStatus.Mismatch,
            "Startup must expose validated artifact statuses and refuse delivery planning for changed ready media.");
        await File.WriteAllBytesAsync(landscapePath, [9, 10, 11]);
        await CaptureJournalPromotionIntentStore.CompleteAsync(
            root,
            original.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape);
        Assert((await CaptureJournalPromotionIntentStore.InspectAsync(
                   root,
                   original.Clip.ClipId,
                   CaptureJournalArtifactKinds.Landscape)).Status ==
               CaptureJournalPromotionStatus.Missing,
            "Intent cleanup must occur only after the exact destination fingerprint is journaled.");

        var idempotent = await CaptureJournalStore.CommitOriginalAsync(
            root,
            fixture.GameplayPath,
            CaptureJournalSourceKind.InstantReplay,
            "Test Game",
            now,
            TimeSpan.FromSeconds(30),
            1920,
            1080,
            reactionCameraRequested: true,
            [CaptureJournalArtifactKinds.Landscape],
            now: now.AddMinutes(1));
        Assert(idempotent.Generation == ready.Generation &&
               idempotent.State == CaptureJournalState.RenditionsReady,
            "Replaying an identical original commit must preserve the newest journal generation.");

        var journalPath = CaptureJournalStore.GetPath(root, original.Clip.ClipId);
        var originalJson = await File.ReadAllTextAsync(journalPath);
        var tamperedRelative = Path.GetRelativePath(root, decoyCameraPath).Replace('\\', '/');
        var tamperedJson = originalJson.Replace(
            camera.RelativePath,
            tamperedRelative,
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(journalPath, tamperedJson);
        Assert(CaptureJournalStore.Load(root, original.Clip.ClipId).Status ==
               CaptureJournalLoadStatus.Invalid,
            "Loading must reject a fingerprinted MP4 redirected away from its canonical project path.");
        await File.WriteAllTextAsync(journalPath, originalJson);
    }

    private static async Task AssertFailureNoCameraMissingAndImmutableReplayAsync(string root)
    {
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 8, 27, 13, 0, 0, TimeSpan.Zero);
        var gameDirectory = Path.Combine(root, "Library", "Game", "Edge Game");
        Directory.CreateDirectory(gameDirectory);

        var noCameraPath = Path.Combine(gameDirectory, "plain.mp4");
        await File.WriteAllBytesAsync(noCameraPath, [1, 2, 3]);
        var noCamera = await CaptureJournalStore.CommitOriginalAsync(
            root,
            noCameraPath,
            CaptureJournalSourceKind.ManualCapture,
            "Edge Game",
            now,
            TimeSpan.FromSeconds(8),
            1280,
            720,
            reactionCameraRequested: false,
            [],
            now: now);
        Assert(noCamera.State == CaptureJournalState.OriginalCommitted &&
               !noCamera.Clip.ReactionCameraRequested &&
               noCamera.Clip.RequestedRenditions.Count == 0,
            "A no-camera clip must remain a complete original-only journal record.");
        await AssertThrowsAsync<InvalidDataException>(
            () => CaptureJournalStore.BeginCameraAsync(
                root,
                noCamera.Clip.ClipId,
                noCamera.Generation,
                now: now.AddSeconds(1)),
            "A no-camera capture snapshot must never enter CameraPending.");

        var missingOriginal = noCamera.Clip.Original;
        File.Delete(noCameraPath);
        var missing = await CaptureJournalStore.ValidateArtifactAsync(root, missingOriginal);
        Assert(missing.Status == CaptureJournalArtifactValidationStatus.Missing &&
               missing.ObservedFingerprint is null,
            "A canonical artifact removed after commit must be reported as Missing, not adopted or invalidated.");

        var failedPath = Path.Combine(gameDirectory, "failed.mp4");
        await File.WriteAllBytesAsync(failedPath, [20, 21, 22]);
        var original = await CaptureJournalStore.CommitOriginalAsync(
            root,
            failedPath,
            CaptureJournalSourceKind.ManualCapture,
            "Edge Game",
            now,
            TimeSpan.FromSeconds(10),
            1280,
            720,
            reactionCameraRequested: true,
            [CaptureJournalArtifactKinds.Landscape],
            now: now);
        await AssertThrowsAsync<CaptureJournalConcurrencyException>(
            () => CaptureJournalStore.CommitOriginalAsync(
                root,
                failedPath,
                CaptureJournalSourceKind.InstantReplay,
                "Changed Game",
                now.AddMinutes(1),
                TimeSpan.FromSeconds(99),
                3840,
                2160,
                reactionCameraRequested: true,
                [CaptureJournalArtifactKinds.Portrait],
                now: now.AddMinutes(1)),
            "Replaying the same clip id with changed immutable metadata must be rejected.");

        var pending = await CaptureJournalStore.BeginCameraAsync(
            root,
            original.Clip.ClipId,
            original.Generation,
            now: now.AddSeconds(1));
        var failed = await CaptureJournalStore.FailRenditionsAsync(
            root,
            original.Clip.ClipId,
            pending.Generation,
            "camera-layer-unavailable",
            now: now.AddSeconds(2));
        var reloadedFailure = CaptureJournalStore.Load(root, original.Clip.ClipId);
        Assert(reloadedFailure.LoadedFromDisk &&
               reloadedFailure.Document?.State == CaptureJournalState.RenditionsFailed &&
               reloadedFailure.Document.FailureCode == "camera-layer-unavailable" &&
               reloadedFailure.Document.Generation == failed.Generation,
            "The exact stable failure code and generation must survive disk replay.");
        var retry = await CaptureJournalStore.BeginCameraAsync(
            root,
            original.Clip.ClipId,
            failed.Generation,
            now: now.AddSeconds(3));
        Assert(retry.State == CaptureJournalState.CameraPending && retry.FailureCode is null,
            "Retry must explicitly advance the persisted failure generation back to pending.");

        await AssertThrowsAsync<CaptureJournalConcurrencyException>(
            () => CaptureJournalStore.FailRenditionsAsync(
                root,
                original.Clip.ClipId,
                failed.Generation,
                "stale-attempt",
                now: now.AddSeconds(4)),
            "A stale recovery worker must not overwrite a newer journal generation.");
    }

    private static async Task AssertBoundedResumableStartupReconciliationAsync(string root)
    {
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 8, 27, 14, 0, 0, TimeSpan.Zero);
        var gameDirectory = Path.Combine(root, "Library", "Game", "Paging Game");
        Directory.CreateDirectory(gameDirectory);
        var expectedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 5; index++)
        {
            var path = Path.Combine(gameDirectory, $"page-{index}.mp4");
            await File.WriteAllBytesAsync(path, [(byte)(index + 1)]);
            var document = await CaptureJournalStore.CommitOriginalAsync(
                root,
                path,
                CaptureJournalSourceKind.InstantReplay,
                "Paging Game",
                now.AddSeconds(index),
                TimeSpan.FromSeconds(5),
                1280,
                720,
                reactionCameraRequested: false,
                [],
                now: now.AddSeconds(index));
            expectedIds.Add(document.Clip.ClipId);
        }

        var journalDirectory = CaptureJournalStore.GetJournalDirectory(root);
        var corruptId = new string('a', 32);
        var futureId = new string('b', 32);
        await File.WriteAllTextAsync(Path.Combine(journalDirectory, $"{corruptId}.json"), "{broken");
        await File.WriteAllTextAsync(
            Path.Combine(journalDirectory, $"{futureId}.json"),
            "{\"schemaVersion\":999}");
        await File.WriteAllTextAsync(
            Path.Combine(journalDirectory, $".{new string('c', 32)}.tmp"),
            "ignored");
        expectedIds.Add(corruptId);
        expectedIds.Add(futureId);

        var tinyDeadlineHandler = new RecordingReconciliationHandler();
        var tinyDeadline = await CaptureJournalStartupReconciler.ReconcileAsync(
            root,
            tinyDeadlineHandler,
            maximumEntries: 2,
            maximumDuration: TimeSpan.FromTicks(1));
        Assert(tinyDeadline.Inspected == 1 &&
               tinyDeadline.NextCursor is not null &&
               tinyDeadline.EntryLimitReached,
            "An already-expired startup budget must still advance one resumable journal id.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var handler = new RecordingReconciliationHandler();
            var summary = await CaptureJournalStartupReconciler.ReconcileAsync(
                root,
                handler,
                maximumEntries: 2,
                maximumDuration: TimeSpan.FromSeconds(5),
                afterClipId: cursor);
            pages++;
            Assert(summary.Inspected is > 0 and <= 2 &&
                   summary.Inspected == handler.Statuses.Count,
                "Each startup page must honor the exact requested entry bound.");
            Assert(handler.Statuses.Keys.All(seen.Add),
                "Resumable startup pages must not repeat or permanently starve a clip id.");
            if (!summary.EntryLimitReached)
            {
                Assert(summary.NextCursor is not null,
                    "A non-empty terminal page must still report its processed cursor.");
                break;
            }
            Assert(summary.Inspected == 2 && summary.NextCursor is not null,
                "A bounded non-terminal page must fill the exact bound and provide a resume cursor.");
            cursor = summary.NextCursor;
        }
        Assert(pages == 4 && seen.SetEquals(expectedIds),
            "Two-entry deterministic paging must visit all seven journal ids in exactly four pages.");

        var finalHandler = new RecordingReconciliationHandler();
        var exhausted = await CaptureJournalStartupReconciler.ReconcileAsync(
            root,
            finalHandler,
            maximumEntries: 2,
            maximumDuration: TimeSpan.FromSeconds(5),
            afterClipId: seen.Max(StringComparer.Ordinal));
        Assert(exhausted.Inspected == 0 &&
               exhausted.Loaded == 0 &&
               exhausted.Unreadable == 0 &&
               !exhausted.EntryLimitReached &&
               exhausted.NextCursor is null,
            "An exhausted cursor must return an exact empty terminal page.");

        var auditHandler = new RecordingReconciliationHandler();
        var audit = await CaptureJournalStartupReconciler.ReconcileAsync(
            root,
            auditHandler,
            maximumEntries: 20,
            maximumDuration: TimeSpan.FromSeconds(5));
        Assert(audit.Inspected == 7 && audit.Loaded == 5 && audit.Unreadable == 2 &&
               !audit.EntryLimitReached,
            "A full page must count loaded and unreadable records exactly.");
        Assert(auditHandler.Statuses[corruptId] == CaptureJournalLoadStatus.Corrupt &&
               auditHandler.Statuses[futureId] == CaptureJournalLoadStatus.UnsupportedSchema,
            "Startup reconciliation must surface corrupt and future-schema state distinctly.");

        var orderedIds = expectedIds.Order(StringComparer.Ordinal).ToArray();
        var failingHandler = new FailingReconciliationHandler(
            orderedIds[0],
            new InvalidDataException("synthetic-routing-item-failure"));
        var observedLogLines = new List<string>();
        CaptureJournalReconciliationSummary isolatedFailure;
        using (Log.ObserveForTests(observedLogLines.Add))
        {
            isolatedFailure = await CaptureJournalStartupReconciler.ReconcileAsync(
                root,
                failingHandler,
                maximumEntries: 20,
                maximumDuration: TimeSpan.FromSeconds(5));
        }
        Assert(isolatedFailure.Inspected == expectedIds.Count &&
               failingHandler.Visited.SequenceEqual(orderedIds) &&
               failingHandler.Visited.Skip(1).Any(),
            "One invalid routing handler item must be counted and isolated without starving later clips in the startup page.");
        Assert(observedLogLines.Count(line =>
                   line.Contains(
                       $"could not reconcile capture journal {orderedIds[0]}",
                       StringComparison.OrdinalIgnoreCase) &&
                   line.Contains("synthetic-routing-item-failure", StringComparison.Ordinal)) == 1,
            "An isolated per-clip reconciliation failure must leave one actionable, clip-scoped diagnostic.");

        var concurrencyHandler = new FailingReconciliationHandler(
            orderedIds[0],
            new RoutingConcurrencyException("synthetic-routing-concurrency-failure"));
        observedLogLines.Clear();
        using (Log.ObserveForTests(observedLogLines.Add))
        {
            isolatedFailure = await CaptureJournalStartupReconciler.ReconcileAsync(
                root,
                concurrencyHandler,
                maximumEntries: 20,
                maximumDuration: TimeSpan.FromSeconds(5));
        }
        Assert(isolatedFailure.Inspected == expectedIds.Count &&
               concurrencyHandler.Visited.SequenceEqual(orderedIds) &&
               observedLogLines.Count(line => line.Contains(
                   "synthetic-routing-concurrency-failure", StringComparison.Ordinal)) == 1,
            "A routing CAS conflict must be isolated and logged without aborting the remaining startup page.");

        using var cancelled = new CancellationTokenSource();
        var cancellingHandler = new FailingReconciliationHandler(
            orderedIds[0],
            new InvalidDataException("synthetic-failure-after-cancellation"),
            cancelled.Cancel);
        await AssertThrowsAsync<OperationCanceledException>(
            () => CaptureJournalStartupReconciler.ReconcileAsync(
                root,
                cancellingHandler,
                cancelled.Token,
                maximumEntries: 20,
                maximumDuration: TimeSpan.FromSeconds(5)),
            "Cancellation observed by a failing handler must be rethrown instead of returning a successful page.");
        Assert(cancellingHandler.Visited.SequenceEqual([orderedIds[0]]),
            "Cancellation after a catchable handler failure must stop before a later clip is inspected.");
    }

    private static async Task AssertStaleAttemptCannotPromoteAsync(string root)
    {
        var now = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
        var fixture = await CreateCameraFixtureAsync(
            root,
            "stale.mp4",
            [CaptureJournalArtifactKinds.Landscape],
            now);
        var pending = await CaptureJournalStore.BeginCameraAsync(
            root,
            fixture.Journal.Clip.ClipId,
            fixture.Journal.Generation,
            now: now.AddSeconds(1));
        var camera = await CaptureJournalStore.CreateArtifactAsync(
            root,
            fixture.Journal.Clip.ClipId,
            CaptureJournalArtifactKinds.ReactionCamera,
            fixture.Project.CameraLayerPath);
        var cameraAttached = await CaptureJournalStore.AttachCameraAsync(
            root,
            fixture.Journal.Clip.ClipId,
            pending.Generation,
            camera,
            now: now.AddSeconds(2));
        var staleStage = Path.Combine(fixture.Project.ProjectDirectory, ".stale-output.tmp");
        await File.WriteAllBytesAsync(staleStage, [61, 62, 63]);
        var staleIntent = await CaptureJournalPromotionIntentStore.PrepareAsync(
            root,
            fixture.Journal.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape,
            cameraAttached.Generation,
            staleStage,
            now: now.AddSeconds(3));
        var failed = await CaptureJournalStore.FailRenditionsAsync(
            root,
            fixture.Journal.Clip.ClipId,
            cameraAttached.Generation,
            "render-failed",
            now: now.AddSeconds(4));
        Assert((await CaptureJournalPromotionIntentStore.InspectAsync(
                   root,
                   fixture.Journal.Clip.ClipId,
                   CaptureJournalArtifactKinds.Landscape)).Status ==
               CaptureJournalPromotionStatus.StaleAttempt,
            "A failed attempt's prepared output must become non-promotable immediately.");
        var retry = await CaptureJournalStore.BeginCameraAsync(
            root,
            fixture.Journal.Clip.ClipId,
            failed.Generation,
            now: now.AddSeconds(5));
        Assert(retry.ProcessingAttemptEpoch == staleIntent.ProcessingAttemptEpoch + 1 &&
               retry.ProcessingAttemptId != staleIntent.ProcessingAttemptId &&
               !retry.CameraPresent && retry.Artifacts.Count == 0,
            "Retry must start a new durable epoch and discard failed-attempt readiness.");
        var stalePromotion = await CaptureJournalPromotionIntentStore.PromoteAsync(
            root,
            fixture.Journal.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape);
        Assert(stalePromotion.Status == CaptureJournalPromotionStatus.StaleAttempt &&
               !File.Exists(CaptureJournalStore.GetCanonicalArtifactPath(
                   root,
                   fixture.Journal.Clip,
                   CaptureJournalArtifactKinds.Landscape)),
            "A stale pre-failure intent must not publish bytes after retry starts.");

        var retryCamera = await CaptureJournalStore.AttachCameraAsync(
            root,
            fixture.Journal.Clip.ClipId,
            retry.Generation,
            camera,
            now: now.AddSeconds(6));
        var retryStage = Path.Combine(fixture.Project.ProjectDirectory, ".retry-output.tmp");
        await File.WriteAllBytesAsync(retryStage, [71, 72, 73]);
        var retryIntent = await CaptureJournalPromotionIntentStore.PrepareAsync(
            root,
            fixture.Journal.Clip.ClipId,
            CaptureJournalArtifactKinds.Landscape,
            retryCamera.Generation,
            retryStage,
            now: now.AddSeconds(7));
        Assert(retryIntent.ProcessingAttemptEpoch == retry.ProcessingAttemptEpoch &&
               retryIntent.ProcessingAttemptId == retry.ProcessingAttemptId,
            "A retry must replace the stale intent with one bound to its new durable attempt.");
    }

    private static async Task AssertPromotionIntentReparseAncestorsRejectedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 8, 27, 13, 45, 0, TimeSpan.Zero);

        var storageRoot = Path.Combine(root, "storage");
        var storageTarget = Path.Combine(root, "storage-target");
        var storagePrivate = Path.Combine(storageRoot, CaptureLibraryLayout.PrivateDataFolderName);
        var storageJunction = Path.Combine(storagePrivate, CaptureJournalStore.RoutingFolderName);
        var storageGame = Path.Combine(storageRoot, "Library", "Game", "Reparse Game");
        var storageStageDirectory = CaptureLibraryLayout.GetStagingDirectory(storageRoot);
        Directory.CreateDirectory(storagePrivate);
        Directory.CreateDirectory(storageTarget);
        Directory.CreateDirectory(storageGame);
        Directory.CreateDirectory(storageStageDirectory);
        CreateDirectoryJunction(storageJunction, storageTarget);
        var storageStage = Path.Combine(storageStageDirectory, "stage.mp4");
        await File.WriteAllBytesAsync(storageStage, [1, 2, 3]);
        await AssertThrowsAsync<IOException>(
            () => CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
                storageRoot,
                storageStage,
                Path.Combine(storageGame, "final.mp4"),
                CaptureJournalSourceKind.ManualCapture,
                "Reparse Game",
                now,
                TimeSpan.FromSeconds(5),
                1280,
                720,
                false,
                [],
                now: now),
            "Intent persistence must reject a redirected ancestor before writing outside the library.");
        Directory.Delete(storageJunction);
        Assert(!Directory.EnumerateFileSystemEntries(storageTarget).Any(),
            "Rejecting a junctioned intent folder must not touch its target.");

        var stageRoot = Path.Combine(root, "stage");
        var stageTarget = Path.Combine(root, "stage-target");
        var stageJunction = Path.Combine(stageRoot, "linked-stage");
        var stageGame = Path.Combine(stageRoot, "Library", "Game", "Reparse Game");
        Directory.CreateDirectory(stageRoot);
        Directory.CreateDirectory(stageTarget);
        Directory.CreateDirectory(stageGame);
        await File.WriteAllBytesAsync(Path.Combine(stageTarget, "stage.mp4"), [4, 5, 6]);
        CreateDirectoryJunction(stageJunction, stageTarget);
        await AssertThrowsAsync<IOException>(
            () => CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
                stageRoot,
                Path.Combine(stageJunction, "stage.mp4"),
                Path.Combine(stageGame, "final.mp4"),
                CaptureJournalSourceKind.ManualCapture,
                "Reparse Game",
                now,
                TimeSpan.FromSeconds(5),
                1280,
                720,
                false,
                [],
                now: now),
            "Original promotion must reject a staged file reached through a redirected ancestor.");
        Directory.Delete(stageJunction);

        var destinationRoot = Path.Combine(root, "destination");
        var destinationTarget = Path.Combine(root, "destination-target");
        var destinationGameParent = Path.Combine(destinationRoot, "Library", "Game");
        var destinationJunction = Path.Combine(destinationGameParent, "Linked Game");
        var destinationStageDirectory = CaptureLibraryLayout.GetStagingDirectory(destinationRoot);
        Directory.CreateDirectory(destinationGameParent);
        Directory.CreateDirectory(destinationTarget);
        Directory.CreateDirectory(destinationStageDirectory);
        var destinationStage = Path.Combine(destinationStageDirectory, "stage.mp4");
        await File.WriteAllBytesAsync(destinationStage, [7, 8, 9]);
        CreateDirectoryJunction(destinationJunction, destinationTarget);
        await AssertThrowsAsync<IOException>(
            () => CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
                destinationRoot,
                destinationStage,
                Path.Combine(destinationJunction, "final.mp4"),
                CaptureJournalSourceKind.ManualCapture,
                "Reparse Game",
                now,
                TimeSpan.FromSeconds(5),
                1280,
                720,
                false,
                [],
                now: now),
            "Original promotion must reject a canonical destination beneath a redirected ancestor.");
        Directory.Delete(destinationJunction);
        Assert(!Directory.EnumerateFileSystemEntries(destinationTarget).Any(),
            "Rejecting a junctioned destination must not create its final gameplay file.");
    }

    private static void CreateDirectoryJunction(string junction, string target)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(startInfo) ??
                            throw new InvalidOperationException(
                                "Could not start the capture-journal junction helper.");
        Assert(process.WaitForExit(5000) && process.ExitCode == 0,
            "The mandatory capture-journal junction fixture could not be prepared.");
    }

    private static async Task<CameraFixture> CreateCameraFixtureAsync(
        string root,
        string fileName,
        IReadOnlyList<string> requestedRenditions,
        DateTimeOffset now)
    {
        Directory.CreateDirectory(root);
        var gameDirectory = Path.Combine(root, "Library", "Game", "Test Game");
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(root);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var gameplayPath = Path.Combine(gameDirectory, fileName);
        var cameraStage = Path.Combine(stagingDirectory, "camera-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(gameplayPath, [1, 2, 3, 4, 5]);
        await File.WriteAllBytesAsync(cameraStage, [6, 7, 8]);
        var projectId = CaptureProjectStore.CreateProjectId(root, gameplayPath);
        var composition = SilhouettePreferencesModel.CreateProjectSnapshot(
            projectId,
            SilhouettePreferencesModel.CreateDefault(mirrorCamera: true, modifiedUtc: now),
            landscapeEnabled: requestedRenditions.Contains(
                CaptureJournalArtifactKinds.Landscape,
                StringComparer.Ordinal),
            portraitEnabled: requestedRenditions.Contains(
                CaptureJournalArtifactKinds.Portrait,
                StringComparer.Ordinal),
            mirrorCamera: true,
            modifiedUtc: now);
        var project = await CaptureProjectStore.SaveReactionCameraLayerAsync(
            root,
            gameplayPath,
            cameraStage,
            gameplayStart: TimeSpan.Zero,
            cameraStart: TimeSpan.Zero,
            duration: TimeSpan.FromSeconds(30),
            mirrorCamera: true,
            composition,
            createdUtc: now);
        var journal = await CaptureJournalStore.CommitOriginalAsync(
            root,
            gameplayPath,
            CaptureJournalSourceKind.InstantReplay,
            "Test Game",
            now,
            TimeSpan.FromSeconds(30),
            1920,
            1080,
            reactionCameraRequested: true,
            requestedRenditions,
            now: now);
        return new CameraFixture(gameplayPath, project, journal);
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record CameraFixture(
        string GameplayPath,
        CaptureProjectSaveResult Project,
        CaptureJournalDocument Journal);

    private sealed class RecordingReconciliationHandler : ICaptureJournalReconciliationHandler
    {
        internal Dictionary<string, CaptureJournalLoadStatus> Statuses { get; } =
            new(StringComparer.Ordinal);
        internal List<CaptureJournalReconciliationItem> Items { get; } = [];

        public ValueTask ReconcileAsync(
            CaptureJournalReconciliationItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Statuses.Add(item.ClipId, item.Status);
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingReconciliationHandler(
        string failingClipId,
        Exception failure,
        Action? beforeThrow = null) :
        ICaptureJournalReconciliationHandler
    {
        internal List<string> Visited { get; } = [];

        public ValueTask ReconcileAsync(
            CaptureJournalReconciliationItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Visited.Add(item.ClipId);
            if (item.ClipId.Equals(failingClipId, StringComparison.Ordinal))
            {
                beforeThrow?.Invoke();
                throw failure;
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOriginalPromotionHandler :
        ICaptureJournalOriginalPromotionReconciliationHandler
    {
        internal List<CaptureJournalOriginalPromotionReconciliationItem> Items { get; } = [];

        public ValueTask ReconcileAsync(
            CaptureJournalOriginalPromotionReconciliationItem item,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }
}
