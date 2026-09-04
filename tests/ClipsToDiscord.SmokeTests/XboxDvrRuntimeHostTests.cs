using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClipsToDiscord;

internal static class XboxDvrRuntimeHostTests
{
    private const string SourceId = "source.11111111222233334444555555555555";
    private const string OtherSourceId = "source.aaaaaaaa222233334444555555555555";
    private const string RootIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PromotionRevision =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now.AddDays(-7);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertMetadataAdmissionPrecedesSerializedContentAsync(
            Path.Combine(root, "admission"));
        await AssertOwnedStageLifecycleRemainsSourceGatedAsync(
            Path.Combine(root, "owned-stage-gate"));
        await AssertImportingAndCommittedRecoveryConvergeAsync(
            Path.Combine(root, "recovery"));
        await AssertInterruptedPromotionResumesWithoutRecopyAsync(
            Path.Combine(root, "promotion-resume"));
        await AssertCommittedPromotionBeatsRouteRevocationAsync(
            Path.Combine(root, "committed-route-revocation"));
        await AssertMissingMetadataResetsWithoutAttemptInflationAsync(
            Path.Combine(root, "missing-metadata"));
        await AssertTransientRetryAndSourceIsolationAsync(
            Path.Combine(root, "retry-isolation"));
        await AssertOptionalCatalogFailureDoesNotOwnAggregateLifetimeAsync(
            Path.Combine(root, "catalog-isolation"));
        await AssertReplacementAndWrongProvenanceFailClosedAsync(
            Path.Combine(root, "replacement"));
        await AssertRouteEditBeforeContentLeavesDurableSelectionAsync(
            Path.Combine(root, "route-edit"));
        await AssertSourceAuthorityChangesBeforeContentNeverHydrateAsync(
            Path.Combine(root, "source-authority"));
        await AssertProductionImportBoundarySerializesSourceMutationAsync(
            Path.Combine(root, "production-import-gate"));
        await AssertExplicitSkipResumesLaterClipsAcrossRestartAsync(
            Path.Combine(root, "skip-blocked"));
        await AssertPromotionIntentRecoveryUsesXboxProvenanceAsync(
            Path.Combine(root, "promotion"));
        AssertOwnedDirectoryCreationRejectsRedirectedAncestors(
            Path.Combine(root, "directory-authority"));
        AssertStaleOwnedPartialsAreCleanedConservatively(
            Path.Combine(root, "partial-cleanup"));
        AssertXboxRootsCannotOverlapOtherRuntimeAuthorities(
            Path.Combine(root, "root-overlap"));
        await AssertLifecycleSurfaceMatchesAggregateSessionAsync(
            Path.Combine(root, "lifecycle"));
        await AssertGalleryUsesCompletedXboxRoutingEvidenceAsync(
            Path.Combine(root, "gallery-presentation"));
        await AssertGalleryDoesNotInferXboxProvenanceFromFileNameAsync(
            Path.Combine(root, "gallery-spoof-resistance"));
    }

    private static async Task AssertGalleryUsesCompletedXboxRoutingEvidenceAsync(string root)
    {
        var externalRoot = Path.Combine(root, "external");
        var captureRoot = Path.Combine(root, "capture");
        Directory.CreateDirectory(externalRoot);
        Directory.CreateDirectory(captureRoot);
        const string gameName = "NBA 2K27";
        const string occurrenceId =
            "f0db12e73cfb1792f0db12e73cfb1792f0db12e73cfb1792f0db12e73cfb1792";
        var capturedUtc = new DateTimeOffset(2026, 9, 1, 2, 35, 2, TimeSpan.Zero);
        XboxDvrRuntimeLayout.EnsureOwnedDirectories(captureRoot, SourceId, gameName);
        var importedPath = XboxDvrRuntimeLayout.GetDestinationPath(
            captureRoot,
            gameName,
            capturedUtc,
            occurrenceId);
        await File.WriteAllBytesAsync(importedPath, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);
        var journal = await CaptureJournalStore.CommitOriginalAsync(
            captureRoot,
            importedPath,
            CaptureJournalSourceKind.XboxGameDvr,
            gameName,
            capturedUtc,
            TimeSpan.FromSeconds(30),
            1920,
            1080,
            reactionCameraRequested: false,
            requestedRenditions: [],
            sourceConnectionId: SourceId,
            sourceOccurrenceId: occurrenceId,
            sourceRevisionId: PromotionRevision);
        var planId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var routeId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var routeReference = new RoutingRouteSnapshotReference(
            1,
            routeId,
            1,
            "Xbox to Discord",
            Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
            100,
            0);
        var history = new RoutingDeliveryHistoryItem(
            new RoutingPlanDecision(
                planId,
                journal.Clip.ClipId,
                1,
                [routeId],
                [],
                [],
                Now),
            [],
            [new PlannedFileDisposition(
                Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
                planId,
                journal.Clip.ClipId,
                journal.Clip.Original.Fingerprint.Sha256,
                routeReference,
                RoutingLibraryArea.Uploaded,
                [],
                PlannedFileDispositionState.Completed,
                1,
                null,
                $"capture:{journal.Clip.ClipId}:original",
                null,
                Now,
                Now,
                Now,
                Now)],
            IsArchived: false,
            ArchivedUtc: null);

        var snapshot = GalleryCatalog.Scan(
            externalRoot,
            CancellationToken.None,
            captureLibraryRoot: captureRoot,
            captureRoutingHistory: _ => [history]);
        var clip = snapshot.Games.Single().Clips.Single();

        Assert(clip.Route == GalleryClipRoute.Uploaded &&
               clip.Source == GalleryClipSource.Xbox &&
               clip.SourceLabel == "Xbox" &&
               clip.GameName == gameName &&
               clip.Title.StartsWith(gameName + "__2026-09-01", StringComparison.Ordinal) &&
               clip.FileName.StartsWith("Xbox__2026-09-01", StringComparison.Ordinal) &&
               clip.Path.Equals(importedPath, StringComparison.OrdinalIgnoreCase),
            "Gallery must label a completed Xbox-to-Discord disposition as uploaded and derive its visible title from validated Xbox game metadata without renaming the immutable library file.");
    }

    private static async Task AssertGalleryDoesNotInferXboxProvenanceFromFileNameAsync(
        string root)
    {
        var externalRoot = Path.Combine(root, "external");
        var captureRoot = Path.Combine(root, "capture");
        const string gameName = "Spoof Defense";
        const string fileName = "Xbox__2026-09-02__12-34-56Z__not-xbox.mp4";
        Directory.CreateDirectory(externalRoot);
        var gameDirectory = CaptureLibraryLayout.GetRecordingDirectory(captureRoot, gameName);
        Directory.CreateDirectory(gameDirectory);
        var capturedPath = Path.Combine(gameDirectory, fileName);
        await File.WriteAllBytesAsync(
            capturedPath,
            [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]);
        _ = await CaptureJournalStore.CommitOriginalAsync(
            captureRoot,
            capturedPath,
            CaptureJournalSourceKind.ManualCapture,
            gameName,
            new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.Zero),
            TimeSpan.FromSeconds(30),
            1920,
            1080,
            reactionCameraRequested: false,
            requestedRenditions: []);

        var snapshot = GalleryCatalog.Scan(
            externalRoot,
            CancellationToken.None,
            captureLibraryRoot: captureRoot);
        var clip = snapshot.Games.Single().Clips.Single();

        Assert(clip.Source == GalleryClipSource.ClipCord &&
               clip.SourceLabel == "ClipCord" &&
               clip.Title == fileName &&
               clip.FileName == fileName &&
               clip.Path.Equals(capturedPath, StringComparison.OrdinalIgnoreCase),
            "Gallery must derive Xbox provenance only from validated Capture Journal metadata; an Xbox-prefixed non-Xbox recording must remain ClipCord-owned and keep its literal title.");
    }

    private static async Task AssertMetadataAdmissionPrecedesSerializedContentAsync(string root)
    {
        var first = Candidate("Battlefield 6-2026_08_31-12-00-00.mp4", fileByte: 0x11);
        var second = Candidate("Battlefield 6-2026_09_01-12-00-00.mp4", fileByte: 0x22);
        var unconfirmedHistory = Candidate(
            "Battlefield 6-2026_08_30-12-00-00.mp4", fileByte: 0x23);
        var beforeCutoff = Candidate("Battlefield 6-2026_08_20-12-00-00.mp4", fileByte: 0x33);
        var fallbackOnly = Candidate("Duskfade-2026_09_01-13-00-00.mp4", fileByte: 0x44);
        var fixture = Fixture(
            root,
            [first, second, unconfirmedHistory, beforeCutoff, fallbackOnly],
            [first, second]);
        fixture.Backend.BeforeImport = request =>
        {
            if (fixture.Backend.ImportRequests.Count != 1) return;
            var selected = fixture.Backend.LoadJournal(SourceId, RootIdentity);
            Assert(selected.Occurrences.Count == 2 &&
                   selected.Occurrences.All(item => item.State is
                       XboxDvrOccurrenceState.Selected or XboxDvrOccurrenceState.Importing),
                "The complete metadata-matched batch must be durable before the first source is opened.");
        };

        await fixture.Host.PollOnceAsync();

        Assert(fixture.Backend.ImportRequests.Count == 2 &&
               fixture.Backend.ImportRequests.Select(request =>
                       request.ExpectedMetadata.PortableRelativePath)
                   .ToHashSet(StringComparer.Ordinal)
                   .SetEquals([
                       first.PortableRelativePath,
                       second.PortableRelativePath
                   ]),
            "Only exact source-bound, frozen-history metadata may reach the hydration boundary.");
        Assert(fixture.Backend.MaximumConcurrentImports == 1,
            "Xbox content import must remain globally serialized.");
        Assert(fixture.Backend.ProbePaths.Count == 2 &&
               fixture.Backend.Promotions.Count == 2,
            "Every admitted owned copy must be probed and promoted exactly once.");
        var journal = fixture.Backend.LoadJournal(SourceId, RootIdentity);
        Assert(journal.Occurrences.Count == 2 &&
               journal.Occurrences.All(item =>
                   item.State == XboxDvrOccurrenceState.Imported &&
                   item.ClipId is not null && item.ContentSha256 is not null),
            "Every selected Xbox occurrence must converge on immutable Capture Library evidence.");
        Assert(fixture.Backend.Committed.Values.All(document =>
                   document.Clip.SourceKind == CaptureJournalSourceKind.XboxGameDvr &&
                   document.Clip.SourceConnectionId == SourceId &&
                   document.Clip.SourceOccurrenceId is not null),
            "Capture Journal commits must retain opaque Xbox source provenance.");
        Assert(fixture.Backend.Promotions.All(request =>
                   Path.GetFileName(request.Identity.DestinationPath)
                       .StartsWith("Xbox__2026-", StringComparison.Ordinal) &&
                   Path.GetFileName(request.Identity.DestinationPath)
                       .Contains("Z__", StringComparison.Ordinal) &&
                   request.Identity.DestinationPath.Contains(
                       Path.Combine("Library", "Game", "Battlefield 6"),
                       StringComparison.OrdinalIgnoreCase)),
            "Xbox imports must use deterministic timestamped paths under Library/Game/<Game>.");
    }

    private static async Task AssertOwnedStageLifecycleRemainsSourceGatedAsync(string root)
    {
        var candidate = Candidate(
            "Battlefield 6-2026_09_01-12-15-00.mp4", fileByte: 0x2a);
        var fixture = Fixture(root, [candidate]);
        fixture.Backend.BlockProbe = true;

        var poll = fixture.Host.PollOnceAsync();
        await fixture.Backend.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duringProbe = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        var stagedPath = XboxDvrRuntimeLayout.GetStagedPath(
            fixture.LibraryRoot, SourceId, duringProbe.OccurrenceId);
        Assert(duringProbe.State == XboxDvrOccurrenceState.Importing &&
               File.Exists(stagedPath) &&
               fixture.Backend.SourceExecutionGateCurrentCount == 0,
            "The source lease must remain held after stage publication while probe and promotion are pending.");

        var contenderStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var contenderEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var contender = Task.Run(async () =>
        {
            contenderStarted.TrySetResult();
            using var lease = await fixture.Backend.EnterSourceExecutionGateAsync();
            contenderEntered.TrySetResult();
        });
        await contenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (fixture.Backend.SourceExecutionGateWaiters == 0)
            await Task.Delay(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!contenderEntered.Task.IsCompleted && File.Exists(stagedPath),
            "Source recovery/replacement must wait instead of mutating a published stage during probe.");

        fixture.Backend.ReleaseProbe();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));
        await contender.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(completed.State == XboxDvrOccurrenceState.Imported &&
               contenderEntered.Task.IsCompleted,
            "The source lease must release only after promotion and immutable journal completion.");
    }

    private static async Task AssertImportingAndCommittedRecoveryConvergeAsync(string root)
    {
        var committedCandidate = Candidate(
            "Battlefield 6-2026_08_31-14-00-00.mp4", fileByte: 0x51);
        var stagedCandidate = Candidate(
            "Battlefield 6-2026_08_31-15-00-00.mp4", fileByte: 0x52);
        var fixture = Fixture(root, [committedCandidate, stagedCandidate]);
        var committedIds = Identities(SourceId, committedCandidate);
        var stagedIds = Identities(SourceId, stagedCandidate);
        var journal = fixture.Backend.OpenJournal(
            fixture.Backend.Sources.Sources.Single(), RootIdentity);
        var committedEntry = await SelectAndBeginAsync(
            journal, committedCandidate, committedIds);
        var stagedEntry = await SelectAndBeginAsync(journal, stagedCandidate, stagedIds);
        var priorDocument = fixture.Backend.CreateCommittedDocument(
            committedEntry,
            SourceId,
            Path.Combine(
                fixture.LibraryRoot,
                "Library", "Game", "Battlefield 6", "already-committed.mp4"));
        fixture.Backend.Committed[committedEntry.OccurrenceId] = priorDocument;

        XboxDvrRuntimeLayout.EnsureOwnedDirectories(
            fixture.LibraryRoot, SourceId, stagedEntry.GameName);
        var stagedPath = XboxDvrRuntimeLayout.GetStagedPath(
            fixture.LibraryRoot, SourceId, stagedEntry.OccurrenceId);
        await File.WriteAllBytesAsync(
            stagedPath,
            Enumerable.Repeat((byte)0xee, (int)stagedCandidate.LogicalBytes).ToArray());
        fixture.Backend.BeforeImport = request =>
        {
            if (request.ExpectedOccurrenceIdentitySha256 == stagedEntry.OccurrenceId)
            {
                Assert(!File.Exists(stagedPath),
                    "An orphan stage without a promotion intent must be removed before the admitted source is recopied.");
            }
        };

        await fixture.Host.PollOnceAsync();

        Assert(fixture.Backend.ImportRequests.Count == 1 &&
               fixture.Backend.ImportRequests[0].ExpectedOccurrenceIdentitySha256 ==
               stagedEntry.OccurrenceId,
            "Committed provenance must not rehydrate, while an unauthenticated orphan stage must be safely recopied once.");
        Assert(fixture.Backend.ProbePaths.SequenceEqual([stagedPath],
                   StringComparer.OrdinalIgnoreCase),
            "A fully published deterministic owned stage must be adopted and probed once.");
        var recovered = fixture.Backend.LoadJournal(SourceId, RootIdentity);
        Assert(recovered.Occurrences.Count == 2 &&
               recovered.Occurrences.All(item => item.State == XboxDvrOccurrenceState.Imported) &&
               recovered.Occurrences.Single(item =>
                       item.OccurrenceId == committedEntry.OccurrenceId)
                   .ClipId == priorDocument.Clip.ClipId,
            "Both already-committed provenance and an interrupted owned stage must converge without duplication.");
    }

    private static async Task AssertInterruptedPromotionResumesWithoutRecopyAsync(
        string root)
    {
        var candidate = Candidate(
            "Battlefield 6-2026_08_31-15-30-00.mp4", fileByte: 0x53);
        var fixture = Fixture(root, [candidate]);
        var identities = Identities(SourceId, candidate);
        var journal = fixture.Backend.OpenJournal(
            fixture.Backend.Sources.Sources.Single(), RootIdentity);
        var importing = await SelectAndBeginAsync(journal, candidate, identities);
        XboxDvrRuntimeLayout.EnsureOwnedDirectories(
            fixture.LibraryRoot, SourceId, importing.GameName);
        var stagedPath = XboxDvrRuntimeLayout.GetStagedPath(
            fixture.LibraryRoot, SourceId, importing.OccurrenceId);
        var destinationPath = XboxDvrRuntimeLayout.GetDestinationPath(
            fixture.LibraryRoot,
            importing.GameName,
            importing.CapturedUtc,
            importing.OccurrenceId);
        await File.WriteAllBytesAsync(
            stagedPath,
            Enumerable.Repeat((byte)0x7f, checked((int)candidate.LogicalBytes)).ToArray());
        var resumedDocument = fixture.Backend.CreateCommittedDocument(
            importing, SourceId, destinationPath);
        fixture.Backend.ResumablePromotions[importing.OccurrenceId] = resumedDocument;

        await fixture.Host.PollOnceAsync();

        var recovered = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(recovered.State == XboxDvrOccurrenceState.Imported &&
               recovered.ClipId == resumedDocument.Clip.ClipId &&
               fixture.Backend.ResumeRequests.Count == 1 &&
               fixture.Backend.ResumeRequests[0].OccurrenceId == importing.OccurrenceId &&
               fixture.Backend.ImportRequests.Count == 0 &&
               fixture.Backend.ProbePaths.Count == 0 &&
               fixture.Backend.Promotions.Count == 0 &&
               fixture.Backend.Committed.Count == 1 &&
               File.Exists(destinationPath) &&
               !File.Exists(stagedPath),
            "A durable interrupted promotion must resume exactly once and complete the occurrence without deleting the authenticated stage, reopening OneDrive, reprobe, or duplicate promotion.");
    }

    private static async Task AssertCommittedPromotionBeatsRouteRevocationAsync(
        string root)
    {
        var candidate = Candidate(
            "Battlefield 6-2026_09_01-10-00-00.mp4", fileByte: 0x64);
        var fixture = Fixture(root, [candidate]);
        var identities = Identities(SourceId, candidate);
        var journal = fixture.Backend.OpenJournal(
            fixture.Backend.Sources.Sources.Single(), RootIdentity);
        var importing = await SelectAndBeginAsync(journal, candidate, identities);
        await AssertThrowsAsync<InvalidOperationException>(
            () => journal.DismissRouteRevokedAsync(
                importing.OccurrenceId, importing.RevisionId),
            "An Importing occurrence must never bypass promotion recovery through route revocation.");
        var destination = Path.Combine(
            fixture.LibraryRoot,
            "Library", "Game", "Battlefield 6", "committed-before-delete.mp4");
        fixture.Backend.Committed[importing.OccurrenceId] =
            fixture.Backend.CreateCommittedDocument(
                importing, SourceId, destination);
        var previous = fixture.Backend.Routes;
        fixture.Backend.SetRoutes(previous with
        {
            Generation = previous.Generation + 1,
            Routes = [],
            UpdatedUtc = previous.UpdatedUtc.AddSeconds(1)
        });

        await fixture.Host.PollOnceAsync();

        var recovered = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(recovered.State == XboxDvrOccurrenceState.Imported &&
               fixture.Backend.ImportRequests.Count == 0 &&
               fixture.Backend.Promotions.Count == 0,
            "A deleted route must not discard an Importing occurrence that already has committed Capture Library provenance.");
        await fixture.Host.DisposeAsync();
    }

    private static async Task AssertMissingMetadataResetsWithoutAttemptInflationAsync(string root)
    {
        Directory.CreateDirectory(root);
        var sourceRoot = Path.Combine(root, "Xbox Game DVR");
        var libraryRoot = Path.Combine(root, "library");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(libraryRoot);
        var candidate = Candidate(
            "Battlefield 6-2026_09_01-08-00-00.mp4", fileByte: 0x58);
        var source = Source(SourceId, sourceRoot);
        var backend = new FakeBackend(
            Path.Combine(root, "routing"),
            libraryRoot,
            [source],
            Routes([XboxRoute(SourceId, [candidate])]),
            new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>(StringComparer.Ordinal)
            {
                [SourceId] = new(RootIdentity, [])
            });
        var host = new XboxDvrRuntimeHost(
            libraryRoot,
            backend,
            TestOptions(),
            () => Now,
            static (_, _) => Task.CompletedTask);
        var journal = backend.OpenJournal(source, RootIdentity);
        var ids = Identities(SourceId, candidate);
        _ = await SelectAndBeginAsync(journal, candidate, ids);

        await host.PollOnceAsync();
        var absent = backend.LoadJournal(SourceId, RootIdentity).Occurrences.Single();
        Assert(absent.State == XboxDvrOccurrenceState.Selected &&
               absent.ImportAttempts == 1 && backend.ImportRequests.Count == 0,
            "A missing current item with no stage or intent must reset to Selected without another import attempt.");
        await host.PollOnceAsync();
        var stillAbsent = backend.LoadJournal(SourceId, RootIdentity).Occurrences.Single();
        Assert(stillAbsent.State == XboxDvrOccurrenceState.Selected &&
               stillAbsent.ImportAttempts == 1,
            "Repeated metadata absence must not leave Importing or inflate attempt history.");

        backend.SetMetadata(SourceId, new XboxDvrRuntimeMetadataSnapshot(
            RootIdentity, [candidate]));
        await host.PollOnceAsync();
        var recovered = backend.LoadJournal(SourceId, RootIdentity).Occurrences.Single();
        Assert(recovered.State == XboxDvrOccurrenceState.Imported &&
               recovered.ImportAttempts == 2 && backend.ImportRequests.Count == 1,
            "The exact frozen revision must resume once OneDrive metadata reappears.");
        await host.DisposeAsync();
    }

    private static async Task AssertTransientRetryAndSourceIsolationAsync(string root)
    {
        var retryCandidate = Candidate(
            "Battlefield 6-2026_09_01-10-00-00.mp4", fileByte: 0x61);
        var fixture = Fixture(root, [retryCandidate]);
        fixture.Backend.ImportFailuresRemaining = 1;

        await fixture.Host.PollOnceAsync();
        var interrupted = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(interrupted.State == XboxDvrOccurrenceState.Importing &&
               interrupted.ImportAttempts == 1,
            "A transient content error must leave one recoverable Importing record.");

        await fixture.Host.PollOnceAsync();
        var retried = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(retried.State == XboxDvrOccurrenceState.Imported &&
               retried.ImportAttempts == 2 &&
               fixture.Backend.ImportRequests.Count == 2,
            "A transient import must reset and retry the exact frozen revision once safe.");

        var badSourceRoot = Path.Combine(root, "bad-source");
        Directory.CreateDirectory(badSourceRoot);
        var goodSource = Source(SourceId, Path.Combine(badSourceRoot, "Xbox Good"));
        var badSource = Source(OtherSourceId, Path.Combine(badSourceRoot, "Xbox Missing"));
        Directory.CreateDirectory(goodSource.CanonicalRoot);
        Directory.CreateDirectory(badSource.CanonicalRoot);
        var goodCandidate = Candidate(
            "Battlefield 6-2026_09_01-11-00-00.mp4", fileByte: 0x62);
        var backend = new FakeBackend(
            badSourceRoot,
            Path.Combine(badSourceRoot, "library"),
            [badSource, goodSource],
            Routes([XboxRoute(SourceId, [goodCandidate])]),
            new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>(StringComparer.Ordinal)
            {
                [SourceId] = new(RootIdentity, [goodCandidate]),
                [OtherSourceId] = new(RootIdentity, [])
            });
        backend.MetadataFailuresRemaining[OtherSourceId] = 2;
        var host = new XboxDvrRuntimeHost(
            backend.LibraryRoot,
            backend,
            TestOptions(maximumFailures: 2),
            () => Now,
            static (_, _) => Task.CompletedTask);

        await host.PollOnceAsync();
        await host.PollOnceAsync();

        Assert(!backend.MarkedNeedsAttention.Contains(OtherSourceId) &&
               backend.Committed.Count == 1 && host.Failure is null,
            "A temporary OneDrive outage must not permanently disable an additive source, stop healthy sources, or fail the host.");
        await host.PollOnceAsync();
        Assert(backend.Sources.Sources.Single(source =>
                   source.SourceId == OtherSourceId).Health == RoutingInputSourceHealth.Ready,
            "A source must remain retryable after its transient I/O budget is exhausted.");
        await host.DisposeAsync();
    }

    private static async Task AssertOptionalCatalogFailureDoesNotOwnAggregateLifetimeAsync(
        string root)
    {
        var backend = new FakeBackend(
            Path.Combine(root, "routing"),
            Path.Combine(root, "library"),
            [],
            RoutingSnapshotModel.CreateEmpty(Now),
            new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>())
        {
            SourceCatalogStatus = RoutingDocumentLoadStatus.Unavailable
        };
        var host = new XboxDvrRuntimeHost(
            backend.LibraryRoot,
            backend,
            TestOptions(maximumFailures: 1),
            () => Now,
            static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        await host.StartAsync();
        Assert(host.Failure is null && !host.Completion.IsCompleted,
            "An unavailable optional input-source catalog must not fail or complete the aggregate-compatible host.");
        await host.StopAsync();
        await host.Completion;
        Assert(host.Failure is null,
            "Stopping after an optional catalog outage must remain a clean lifecycle completion.");
        await host.DisposeAsync();

        var corruptBackend = new FakeBackend(
            Path.Combine(root, "corrupt-routing"),
            Path.Combine(root, "corrupt-library"),
            [],
            RoutingSnapshotModel.CreateEmpty(Now),
            new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>())
        {
            RouteStatus = RoutingDocumentLoadStatus.Invalid
        };
        var corruptHost = new XboxDvrRuntimeHost(
            corruptBackend.LibraryRoot,
            corruptBackend,
            TestOptions(),
            () => Now,
            static (_, _) => Task.CompletedTask);
        var rejected = false;
        try
        {
            await corruptHost.PollOnceAsync();
        }
        catch (XboxDvrRuntimeAuthorityException)
        {
            rejected = true;
        }
        Assert(rejected,
            "Corrupt shared Routing authority must remain terminal even though optional Xbox sources are isolated.");
        await corruptHost.DisposeAsync();

        var invalidSource = Source(
            SourceId, Path.Combine(root, "invalid-source", "Xbox Game DVR"));
        Directory.CreateDirectory(invalidSource.CanonicalRoot);
        var invalidBackend = new FakeBackend(
            Path.Combine(root, "invalid-routing"),
            Path.Combine(root, "invalid-library"),
            [invalidSource],
            Routes([XboxRoute(SourceId)]),
            new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>(StringComparer.Ordinal)
            {
                [SourceId] = new(RootIdentity, [])
            })
        {
            MarkNeedsAttentionFailuresRemaining = 1
        };
        invalidBackend.MetadataInvalidSources.Add(SourceId);
        var invalidHost = new XboxDvrRuntimeHost(
            invalidBackend.LibraryRoot,
            invalidBackend,
            TestOptions(maximumFailures: 1),
            () => Now,
            static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await invalidHost.StartAsync();
        Assert(invalidHost.Failure is null && !invalidHost.Completion.IsCompleted,
            "An invalid optional source plus a temporarily unavailable source catalog must not fault aggregate Completion.");
        await invalidHost.StopAsync();
        await invalidHost.Completion;
        await invalidHost.DisposeAsync();
    }

    private static async Task AssertReplacementAndWrongProvenanceFailClosedAsync(string root)
    {
        var original = Candidate(
            "Battlefield 6-2026_09_01-09-00-00.mp4", fileByte: 0x71);
        var originalIds = Identities(SourceId, original);
        var replaced = original with { LastWriteUtcTicks = original.LastWriteUtcTicks + 1 };
        var replacedRootFixture = Fixture(root + "-root-before-first-poll", [original]);
        replacedRootFixture.Backend.SetMetadata(
            SourceId,
            new XboxDvrRuntimeMetadataSnapshot(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                [original]));

        await replacedRootFixture.Host.PollOnceAsync();

        Assert(replacedRootFixture.Backend.ImportRequests.Count == 0 &&
               replacedRootFixture.Backend.MarkedNeedsAttention.Contains(SourceId) &&
               replacedRootFixture.Backend.MarkedAttentionReasons[SourceId] ==
               RoutingInputSourceAttentionReason.RootAuthorityChanged &&
               replacedRootFixture.Backend.OpenJournal(
                       replacedRootFixture.Backend.Sources.Sources.Single(), RootIdentity)
                   .Load().Status == RoutingDocumentLoadStatus.Missing,
            "A root replaced before the first runtime poll must be rejected by catalog authority before any journal creation or hydration.");
        await replacedRootFixture.Host.DisposeAsync();

        var approvalFixture = Fixture(
            root + "-frozen-revision", [replaced], [original]);
        await approvalFixture.Host.PollOnceAsync();
        Assert(approvalFixture.Backend.ImportRequests.Count == 0 &&
               approvalFixture.Backend.OpenJournal(
                       approvalFixture.Backend.Sources.Sources.Single(), RootIdentity)
                   .Load().Status == RoutingDocumentLoadStatus.Missing,
            "A historical occurrence whose revision changed after confirmation must never be selected or hydrated.");
        await approvalFixture.Host.DisposeAsync();

        var replacementFixture = Fixture(root + "-leaf", [replaced]);
        var replacementJournal = replacementFixture.Backend.OpenJournal(
            replacementFixture.Backend.Sources.Sources.Single(), RootIdentity);
        _ = await replacementJournal.SelectAsync(
            originalIds.OccurrenceId,
            originalIds.RevisionId,
            original.PortableRelativePath,
            "Battlefield 6",
            ParsedTime(original),
            original.LogicalBytes,
            original.LastWriteUtcTicks);

        await replacementFixture.Host.PollOnceAsync();

        var replacementState = replacementFixture.Backend
            .LoadJournal(SourceId, RootIdentity).Occurrences.Single();
        Assert(replacementState.State == XboxDvrOccurrenceState.NeedsAttention &&
               replacementState.ErrorCode == "source-replaced" &&
               replacementFixture.Backend.MarkedNeedsAttention.Contains(SourceId) &&
               replacementFixture.Backend.ImportRequests.Count == 0,
            "A changed revision under one native occurrence must stop before content and mark the source.");

        var wrongFixture = Fixture(root + "-wrong-provenance", [original]);
        var wrongJournal = wrongFixture.Backend.OpenJournal(
            wrongFixture.Backend.Sources.Sources.Single(), RootIdentity);
        var wrongEntry = await SelectAndBeginAsync(wrongJournal, original, originalIds);
        wrongFixture.Backend.Committed[wrongEntry.OccurrenceId] =
            wrongFixture.Backend.CreateCommittedDocument(
                wrongEntry,
                OtherSourceId,
                Path.Combine(
                    wrongFixture.LibraryRoot,
                    "Library", "Game", "Battlefield 6", "wrong-source.mp4"));

        await wrongFixture.Host.PollOnceAsync();

        var stillPending = wrongFixture.Backend
            .LoadJournal(SourceId, RootIdentity).Occurrences.Single();
        Assert(stillPending.State == XboxDvrOccurrenceState.Importing &&
               stillPending.ClipId is null &&
               wrongFixture.Backend.MarkedNeedsAttention.Contains(SourceId) &&
               wrongFixture.Backend.ImportRequests.Count == 0,
            "Committed provenance from another source id must never complete this occurrence or trigger another import.");
    }

    private static async Task AssertPromotionIntentRecoveryUsesXboxProvenanceAsync(string root)
    {
        var library = Path.Combine(root, "library");
        Directory.CreateDirectory(library);
        var occurrence =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var game = "Battlefield 6";
        XboxDvrRuntimeLayout.EnsureOwnedDirectories(library, SourceId, game);
        var staged = XboxDvrRuntimeLayout.GetStagedPath(library, SourceId, occurrence);
        var destination = XboxDvrRuntimeLayout.GetDestinationPath(
            library, game, Now.AddHours(-1), occurrence);
        var bytes = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(staged, bytes);
        var prepared = await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
            library,
            staged,
            destination,
            CaptureJournalSourceKind.XboxGameDvr,
            game,
            Now.AddHours(-1),
            TimeSpan.FromSeconds(20),
            1920,
            1080,
            reactionCameraRequested: false,
            requestedRenditions: [],
            sourceConnectionId: SourceId,
            sourceOccurrenceId: occurrence,
            sourceRevisionId: PromotionRevision);
        var promoter = new XboxDvrCaptureLibraryPromoter(library, () => Now);
        var identity = new XboxDvrLibraryPromotionIdentity(
            SourceId, occurrence, PromotionRevision, game,
            Now.AddHours(-1), staged, destination);

        var recovered = await promoter.TryResumeAsync(identity, CancellationToken.None);
        var found = await promoter.FindCommittedAsync(
            SourceId, occurrence, PromotionRevision, CancellationToken.None);

        Assert(recovered is not null && found is not null &&
               recovered.Clip.ClipId == prepared.ClipId &&
               found.Clip.ClipId == prepared.ClipId &&
               found.Clip.SourceConnectionId == SourceId &&
               found.Clip.SourceOccurrenceId == occurrence &&
               found.Clip.SourceRevisionId == PromotionRevision &&
               !File.Exists(staged) && File.Exists(destination),
            "A durable original-promotion intent must resume to one provenance-bound Capture Journal clip.");
    }

    private static async Task AssertRouteEditBeforeContentLeavesDurableSelectionAsync(
        string root)
    {
        var candidate = Candidate(
            "Battlefield 6-2026_09_01-07-00-00.mp4", fileByte: 0x79);
        var fixture = Fixture(root, [candidate]);
        var initial = fixture.Backend.Routes;
        fixture.Backend.BeforeJournalMutation = count =>
        {
            if (count != 2) return;
            fixture.Backend.BeforeJournalMutation = null;
            var removed = new RoutingSnapshotDocument(
                initial.SchemaVersion,
                initial.Generation + 1,
                [],
                initial.CreatedUtc,
                initial.UpdatedUtc.AddSeconds(1));
            RoutingSnapshotModel.Validate(removed);
            fixture.Backend.SetRoutes(removed);
        };

        await fixture.Host.PollOnceAsync();
        var selected = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(selected.State == XboxDvrOccurrenceState.Selected &&
               selected.ImportAttempts == 0 &&
               fixture.Backend.ImportRequests.Count == 0,
            "A route-generation change after durable selection but before content open must leave Selected and never hydrate.");
        await fixture.Host.DisposeAsync();

        var restarted = new XboxDvrRuntimeHost(
            fixture.LibraryRoot,
            fixture.Backend,
            TestOptions(),
            () => Now,
            static (_, _) => Task.CompletedTask);
        var routeLoadsBeforeRestart = fixture.Backend.RouteLoadCount;
        fixture.Backend.BeforeRouteLoad = count =>
        {
            if (count != routeLoadsBeforeRestart + 2) return;
            fixture.Backend.BeforeRouteLoad = null;
            fixture.Backend.SetRoutes(initial with
            {
                Generation = initial.Generation + 2,
                UpdatedUtc = initial.UpdatedUtc.AddSeconds(2)
            });
        };
        await restarted.PollOnceAsync();
        var addWon = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(addWon.State == XboxDvrOccurrenceState.Selected &&
               addWon.ImportAttempts == 0 &&
               fixture.Backend.ImportRequests.Count == 0,
            "A matching route Add that wins the fresh dismissal gate must preserve the selected occurrence.");

        fixture.Backend.SetRoutes(initial with
        {
            Generation = initial.Generation + 3,
            Routes = [],
            UpdatedUtc = initial.UpdatedUtc.AddSeconds(3)
        });
        fixture.Backend.SetMetadata(
            SourceId, new XboxDvrRuntimeMetadataSnapshot(RootIdentity, []));
        await restarted.PollOnceAsync();
        var revoked = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(revoked.State == XboxDvrOccurrenceState.RouteRevoked &&
               revoked.ErrorCode == "route-revoked" &&
               revoked.ImportAttempts == 0 &&
               fixture.Backend.ImportRequests.Count == 0,
            "Deleting the sole source route must durably revoke selected work without hydration.");

        fixture.Backend.SetRoutes(initial with
        {
            Generation = initial.Generation + 4,
            UpdatedUtc = initial.UpdatedUtc.AddSeconds(4)
        });
        fixture.Backend.SetMetadata(
            SourceId, new XboxDvrRuntimeMetadataSnapshot(RootIdentity, [candidate]));
        await restarted.PollOnceAsync();
        var readmitted = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(readmitted.State == XboxDvrOccurrenceState.Imported &&
               fixture.Backend.ImportRequests.Count == 1,
            "A newly confirmed matching route may re-admit the exact route-revoked occurrence revision once.");
        await restarted.DisposeAsync();
    }

    private static async Task AssertSourceAuthorityChangesBeforeContentNeverHydrateAsync(
        string root)
    {
        await AssertSourceMutationNeverHydratesAsync(
            Path.Combine(root, "disabled-before-begin"),
            mutationCount: 1,
            source => [source with
            {
                Revision = source.Revision + 1,
                Enabled = false,
                UpdatedUtc = source.UpdatedUtc.AddSeconds(1)
            }],
            "Disabling an Xbox source after selection must stop content access.");
        await AssertSourceMutationNeverHydratesAsync(
            Path.Combine(root, "retired-after-begin"),
            mutationCount: 2,
            source => [source with
            {
                Revision = source.Revision + 1,
                Enabled = false,
                Retired = true,
                Health = RoutingInputSourceHealth.NeedsAttention,
                AttentionReason = RoutingInputSourceAttentionReason.RootAuthorityChanged,
                UpdatedUtc = source.UpdatedUtc.AddSeconds(1)
            }],
            "Retiring an Xbox source during BeginImport must stop content access.");
        await AssertSourceMutationNeverHydratesAsync(
            Path.Combine(root, "removed-after-begin"),
            mutationCount: 2,
            _ => [],
            "Removing an Xbox source during BeginImport must stop content access.");
        await AssertSourceMutationNeverHydratesAsync(
            Path.Combine(root, "display-revision-after-begin"),
            mutationCount: 2,
            source => [source with
            {
                Revision = source.Revision + 1,
                DisplayName = "Renamed Xbox captures",
                UpdatedUtc = source.UpdatedUtc.AddSeconds(1)
            }],
            "Any source revision change must force safe re-evaluation instead of using stale authority.");
    }

    private static async Task AssertSourceMutationNeverHydratesAsync(
        string root,
        int mutationCount,
        Func<RoutingInputSourceRecord, IReadOnlyList<RoutingInputSourceRecord>> mutation,
        string message)
    {
        var candidate = Candidate(
            "Battlefield 6-2026_09_01-08-00-00.mp4", fileByte: 0x7a);
        var fixture = Fixture(root, [candidate]);
        var initialSource = fixture.Backend.Sources.Sources.Single();
        fixture.Backend.BeforeJournalMutation = count =>
        {
            if (count != mutationCount) return;
            fixture.Backend.BeforeJournalMutation = null;
            fixture.Backend.SetSources(mutation(initialSource));
        };

        await fixture.Host.PollOnceAsync();

        var entry = fixture.Backend.LoadJournal(SourceId, RootIdentity)
            .Occurrences.Single();
        Assert(fixture.Backend.ImportRequests.Count == 0 &&
               entry.State == XboxDvrOccurrenceState.Selected,
            message);
        await fixture.Host.DisposeAsync();
    }

    private static async Task AssertExplicitSkipResumesLaterClipsAcrossRestartAsync(
        string root)
    {
        var bad = Candidate(
            "Battlefield 6-2026_09_01-08-30-00.mp4", fileByte: 0x7b);
        var later = Candidate(
            "Battlefield 6-2026_09_01-08-31-00.mp4", fileByte: 0x7c);
        var fixture = Fixture(root, [bad, later], [bad, later]);
        fixture.Backend.ProbeFailuresRemaining = 1;
        fixture.Backend.MarkNeedsAttentionFailuresRemaining = 1;

        await fixture.Host.PollOnceAsync();
        var blocked = fixture.Backend.LoadJournal(SourceId, RootIdentity);
        var blockedEntry = blocked.Occurrences.Single(item =>
            item.State == XboxDvrOccurrenceState.NeedsAttention);
        var pendingEntry = blocked.Occurrences.Single(item =>
            item.State == XboxDvrOccurrenceState.Selected);
        Assert(fixture.Backend.ImportRequests.Count == 1 &&
               blockedEntry.ErrorCode == "invalid-media" &&
               !fixture.Backend.MarkedAttentionReasons.ContainsKey(SourceId),
            "A catalog write outage may delay source health, but must preserve the durable per-clip blocker.");
        await fixture.Host.DisposeAsync();

        var restarted = new XboxDvrRuntimeHost(
            fixture.LibraryRoot,
            fixture.Backend,
            TestOptions(),
            () => Now,
            static (_, _) => Task.CompletedTask);
        await restarted.PollOnceAsync();
        Assert(fixture.Backend.ImportRequests.Count == 1 &&
               fixture.Backend.MarkedAttentionReasons[SourceId] ==
               RoutingInputSourceAttentionReason.BlockedOccurrence,
            "Restart must replay a durable media blocker as BlockedOccurrence without hydrating a later clip.");

        var source = fixture.Backend.Sources.Sources.Single(item =>
            item.SourceId == SourceId);
        var journal = fixture.Backend.OpenJournal(source, RootIdentity);
        _ = await journal.SkipBlockedAsync(
            blockedEntry.OccurrenceId, blockedEntry.RevisionId);
        fixture.Backend.SetSources(
        [
            source with
            {
                Revision = source.Revision + 1,
                Health = RoutingInputSourceHealth.Ready,
                AttentionReason = RoutingInputSourceAttentionReason.None,
                UpdatedUtc = source.UpdatedUtc.AddSeconds(1)
            }
        ]);

        await restarted.PollOnceAsync();
        var resumed = fixture.Backend.LoadJournal(SourceId, RootIdentity);
        Assert(resumed.Occurrences.Single(item =>
                   item.OccurrenceId == blockedEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.Skipped &&
               resumed.Occurrences.Single(item =>
                   item.OccurrenceId == pendingEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.Imported &&
               fixture.Backend.ImportRequests.Count == 2,
            "After explicit skip, the blocked clip must stay terminal while the later clip imports once.");
        await restarted.DisposeAsync();

        var secondRestart = new XboxDvrRuntimeHost(
            fixture.LibraryRoot,
            fixture.Backend,
            TestOptions(),
            () => Now,
            static (_, _) => Task.CompletedTask);
        await secondRestart.PollOnceAsync();
        Assert(fixture.Backend.ImportRequests.Count == 2 &&
               fixture.Backend.LoadJournal(SourceId, RootIdentity).Occurrences
                   .Single(item => item.OccurrenceId == blockedEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.Skipped,
            "Restart must preserve the skip and never retry or route the dismissed clip.");
        await secondRestart.DisposeAsync();
    }

    private static async Task AssertProductionImportBoundarySerializesSourceMutationAsync(
        string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        var libraryRoot = Path.GetFullPath(Path.Combine(root, "library"));
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(libraryRoot);
        var routeStore = new RoutingSnapshotStore(
            Path.Combine(routingRoot, RoutingSnapshotStore.FileName));
        _ = await routeStore.LoadOrCreateAsync(Now);
        var catalog = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(
                Path.Combine(routingRoot, RoutingInputSourceCatalogStore.FileName)),
            new RoutingInputSourceReferenceProbe(routeStore, routingRoot),
            () => new Guid("12121212-3434-5656-7878-909090909090"),
            () => Now);
        var added = await catalog.AddVerifiedAsync(
            "Xbox captures",
            sourceRoot,
            RootIdentity,
            TimeZoneInfo.Utc.Id,
            enabled: true,
            Now);
        var source = added.Source ??
            throw new InvalidOperationException("The test Xbox source was not registered.");
        var candidate = Candidate(
            "Battlefield 6-2026_09_01-09-00-00.mp4", fileByte: 0x7d);
        var identities = Identities(source.SourceId, candidate);
        var importer = new BlockingContentImporter();
        var backend = new XboxDvrProductionRuntimeBackend(
            routingRoot,
            libraryRoot,
            catalog,
            routeStore,
            static () => true,
            contentImporter: importer);

        var disabled = await catalog.SetEnabledAsync(
            source.SourceId, source.Revision, enabled: false, Now.AddSeconds(1));
        var rejectedRequest = ImportRequest(
            disabled.Source!, candidate, identities, libraryRoot);
        await AssertThrowsAsync<IOException>(
            () => backend.ImportAsync(rejectedRequest, CancellationToken.None),
            "A source mutation committed before the import gate must prevent content open.");
        Assert(importer.CallCount == 0,
            "Rejected source authority must not reach the native content importer.");

        var enabled = await catalog.SetEnabledAsync(
            disabled.Source!.SourceId,
            disabled.Source.Revision,
            enabled: true,
            Now.AddSeconds(2));
        var enabledSource = enabled.Source ??
            throw new InvalidOperationException("The test Xbox source was not enabled.");
        var admittedRequest = ImportRequest(
            enabledSource, candidate, identities, libraryRoot);
        var importTask = backend.ImportAsync(admittedRequest, CancellationToken.None);
        await importer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disableTask = catalog.SetEnabledAsync(
            enabledSource.SourceId,
            enabledSource.Revision,
            enabled: false,
            Now.AddSeconds(3));
        await Task.Delay(50);
        Assert(!disableTask.IsCompleted,
            "A source mutation must wait while the validated native-open boundary owns the source execution gate.");
        importer.Release();
        _ = await importTask;
        var committedDisable = await disableTask;
        Assert(committedDisable.Status == RoutingInputSourceMutationStatus.Disabled &&
               importer.CallCount == 1,
            "Import-first ordering may finish the admitted open, then the source mutation must commit.");

        await AssertThrowsAsync<IOException>(
            () => backend.ImportAsync(admittedRequest, CancellationToken.None),
            "After Disable commits, a stale import must fail before native content open.");
        Assert(importer.CallCount == 1,
            "No native source open may occur after a committed Disable.");

        var readyAgain = await catalog.SetEnabledAsync(
            committedDisable.Source!.SourceId,
            committedDisable.Source.Revision,
            enabled: true,
            Now.AddSeconds(4));
        var routeSource = readyAgain.Source ??
            throw new InvalidOperationException("The test Xbox source was not re-enabled.");
        var routeManager = new RoutingRouteManager(
            routeStore,
            () => Now.AddSeconds(5),
            TestRouteMutationAuthority.Allowed,
            inputSourceMembership: catalog);
        var route = await routeManager.AddAsync(new RoutingRouteDraft(
            "Xbox Battlefield highlights",
            RoutingTriggerKind.WatchedFolder,
            "Battlefield 6",
            Destination: null,
            ConnectionId: null,
            RoutingOutputKind.Original,
            RoutingDeliveryMode.Automatic,
            RoutingMissingOutputBehavior.UseOriginal,
            FileIntoLibrary: true,
            WatchedSourceId: routeSource.SourceId,
            EarliestCapturedUtc: Cutoff,
            XboxHistorySelection: new RoutingXboxHistorySelection(Now, [])));
        var admittedGeneration = routeStore.Load().Document!.Generation;
        var routeStoppedImporter = new BlockingContentImporter();
        routeStoppedImporter.Release();
        var routeBackend = new XboxDvrProductionRuntimeBackend(
            routingRoot,
            libraryRoot,
            catalog,
            routeStore,
            static () => true,
            contentImporter: routeStoppedImporter);
        var routeRequest = ImportRequest(
            routeSource,
            candidate,
            identities,
            libraryRoot,
            admittedGeneration);
        await routeManager.SetEnabledAsync(route.RouteId, enabled: false);
        await AssertThrowsAsync<XboxDvrRuntimeAdmissionChangedException>(
            () => routeBackend.ImportAsync(routeRequest, CancellationToken.None),
            "A route Disable that wins the gate must invalidate the admitted generation before native open.");
        Assert(routeStoppedImporter.CallCount == 0,
            "A completed route Disable must suppress native Xbox content open.");

        await routeManager.SetEnabledAsync(route.RouteId, enabled: true);
        var deleteGeneration = routeStore.Load().Document!.Generation;
        var deleteImporter = new BlockingContentImporter();
        var deleteBackend = new XboxDvrProductionRuntimeBackend(
            routingRoot,
            libraryRoot,
            catalog,
            routeStore,
            static () => true,
            contentImporter: deleteImporter);
        var deleteRequest = ImportRequest(
            routeSource,
            candidate,
            identities,
            libraryRoot,
            deleteGeneration);
        var admittedImport = deleteBackend.ImportAsync(
            deleteRequest, CancellationToken.None);
        await deleteImporter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deleteTask = routeManager.DeleteAsync(route.RouteId);
        await Task.Delay(50);
        Assert(!deleteTask.IsCompleted,
            "Route Delete must wait while the validated native-open boundary owns the source execution gate.");
        deleteImporter.Release();
        _ = await admittedImport;
        await deleteTask;
        Assert(deleteImporter.CallCount == 1,
            "Import-first ordering may finish exactly one admitted source open before Delete publishes.");
        await AssertThrowsAsync<XboxDvrRuntimeAdmissionChangedException>(
            () => deleteBackend.ImportAsync(deleteRequest, CancellationToken.None),
            "A completed route Delete must invalidate stale admission before native open.");
        Assert(deleteImporter.CallCount == 1,
            "No native source open may occur after a completed route Delete.");
    }

    private static async Task AssertLifecycleSurfaceMatchesAggregateSessionAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var backend = new FakeBackend(
            root,
            library,
            [],
            RoutingSnapshotModel.CreateEmpty(Now),
            new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>());
        var host = new XboxDvrRuntimeHost(
            library,
            backend,
            TestOptions(),
            () => Now,
            static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        await host.StartAsync();
        await host.StopAsync();
        await host.Completion;

        Assert(host.Failure is null,
            "Start, Stop, Completion, and Failure must compose with the aggregate active-work session.");
        await host.DisposeAsync();
    }

    private static void AssertOwnedDirectoryCreationRejectsRedirectedAncestors(string root)
    {
        var stagingRoot = Path.Combine(root, "staging-root");
        var stagingTarget = Path.Combine(root, "staging-target");
        var privateRoot = Path.Combine(
            stagingRoot, CaptureLibraryLayout.PrivateDataFolderName);
        Directory.CreateDirectory(privateRoot);
        Directory.CreateDirectory(stagingTarget);
        var stagingJunction = Path.Combine(
            privateRoot, CaptureLibraryLayout.StagingFolderName);
        CreateDirectoryJunction(stagingJunction, stagingTarget);
        var stagingRejected = false;
        try
        {
            XboxDvrRuntimeLayout.EnsureOwnedDirectories(
                stagingRoot, SourceId, "Battlefield 6");
        }
        catch (IOException)
        {
            stagingRejected = true;
        }
        Assert(stagingRejected && !Directory.EnumerateFileSystemEntries(stagingTarget).Any(),
            "Xbox staging must reject an existing junction before creating anything through it.");
        Directory.Delete(stagingJunction);

        var destinationRoot = Path.Combine(root, "destination-root");
        var destinationTarget = Path.Combine(root, "destination-target");
        var gameParent = Path.Combine(
            destinationRoot,
            CaptureLibraryLayout.LibraryFolderName,
            CaptureLibraryLayout.GameFolderName);
        Directory.CreateDirectory(Path.GetDirectoryName(gameParent)!);
        Directory.CreateDirectory(destinationTarget);
        CreateDirectoryJunction(gameParent, destinationTarget);
        var destinationRejected = false;
        try
        {
            XboxDvrRuntimeLayout.EnsureOwnedDirectories(
                destinationRoot, SourceId, "Battlefield 6");
        }
        catch (IOException)
        {
            destinationRejected = true;
        }
        Assert(destinationRejected &&
               !Directory.EnumerateFileSystemEntries(destinationTarget).Any(),
            "Xbox Library filing must reject an existing junction before creating a game folder through it.");
        Directory.Delete(gameParent);
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
                                "Could not start the Xbox runtime junction helper.");
        Assert(process.WaitForExit(5000) && process.ExitCode == 0,
            "The mandatory Xbox runtime junction fixture could not be prepared.");
    }

    private static void AssertXboxRootsCannotOverlapOtherRuntimeAuthorities(string root)
    {
        var library = Path.Combine(root, "library");
        var watched = Path.Combine(root, "watched");
        var disjoint = Source(SourceId, Path.Combine(root, "xbox"));
        XboxDvrRuntimeAuthorityValidator.RequireDisjointOwnedAndWatchedRoots(
            library, [disjoint], [watched]);

        foreach (var overlappingRoot in new[]
                 {
                     library,
                     Path.Combine(library, "Xbox"),
                     Path.GetDirectoryName(library)!,
                     watched,
                     Path.Combine(watched, "Xbox"),
                     Path.GetDirectoryName(watched)!
                 })
        {
            var rejected = false;
            try
            {
                XboxDvrRuntimeAuthorityValidator.RequireDisjointOwnedAndWatchedRoots(
                    library, [Source(SourceId, overlappingRoot)], [watched]);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Assert(rejected,
                "Same, ancestor, and descendant Xbox roots must not overlap Capture Library or legacy watched authority.");
        }
    }

    private static void AssertStaleOwnedPartialsAreCleanedConservatively(string root)
    {
        Directory.CreateDirectory(root);
        XboxDvrRuntimeLayout.EnsureOwnedDirectories(root, SourceId, "Battlefield 6");
        var staging = XboxDvrRuntimeLayout.GetSourceStagingRoot(root, SourceId);
        var occurrence =
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        var stale = Path.Combine(staging, occurrence + ".oldtoken.partial");
        var recent = Path.Combine(staging, occurrence + ".recenttoken.partial");
        var unknown = Path.Combine(staging, "do-not-own.partial");
        File.WriteAllBytes(stale, [1, 2, 3]);
        File.WriteAllBytes(recent, [4, 5, 6]);
        File.WriteAllBytes(unknown, [7, 8, 9]);
        File.SetLastWriteTimeUtc(stale, Now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(recent, Now.AddMinutes(-5).UtcDateTime);
        File.SetLastWriteTimeUtc(unknown, Now.AddDays(-2).UtcDateTime);

        var removed = XboxDvrRuntimeLayout.CleanupStaleOwnedPartials(
            root, SourceId, Now);

        Assert(removed == 1 && !File.Exists(stale) && File.Exists(recent) &&
               File.Exists(unknown),
            "Startup cleanup must remove only old exact-owned Xbox partials while preserving recent and unknown entries.");
    }

    private static FixtureState Fixture(
        string root,
        IReadOnlyList<XboxDvrCandidateMetadata> candidates,
        IReadOnlyList<XboxDvrCandidateMetadata>? frozenHistorical = null)
    {
        Directory.CreateDirectory(root);
        var sourceRoot = Path.Combine(root, "Xbox Game DVR");
        var libraryRoot = Path.Combine(root, "library");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(libraryRoot);
        var source = Source(SourceId, sourceRoot);
        var backend = new FakeBackend(
            Path.Combine(root, "routing"),
            libraryRoot,
            [source],
            Routes([XboxRoute(SourceId, frozenHistorical ?? candidates)]),
            new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>(StringComparer.Ordinal)
            {
                [SourceId] = new(RootIdentity, candidates)
            });
        var host = new XboxDvrRuntimeHost(
            libraryRoot,
            backend,
            TestOptions(),
            () => Now,
            static (_, _) => Task.CompletedTask);
        return new FixtureState(libraryRoot, backend, host);
    }

    private static RoutingInputSourceRecord Source(string sourceId, string root) => new(
        sourceId,
        Revision: 1,
        "Xbox captures",
        RoutingInputSourceKind.XboxGameDvrOneDrive,
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
        Enabled: true,
        Retired: false,
        RoutingInputSourceHealth.Ready,
        RoutingInputSourceAttentionReason.None,
        RootIdentity,
        TimeZoneInfo.Utc.Id,
        Now,
        Now);

    private static XboxDvrCandidateMetadata Candidate(string leaf, byte fileByte)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leaf)))
            .ToLowerInvariant()[..32];
        return new XboxDvrCandidateMetadata(
            leaf,
            LogicalBytes: 32,
            LastWriteUtcTicks: Now.UtcTicks + fileByte,
            VolumeSerialNumber: 0x1234,
            FileId128Hex: id,
            ProviderIdentitySha256: null,
            FileAttributes: 0x20,
            ReparseTag: 0);
    }

    private static DateTimeOffset ParsedTime(XboxDvrCandidateMetadata candidate) =>
        XboxDvrFileNameParser.Parse(candidate.PortableRelativePath).CapturedUtc ??
        throw new InvalidOperationException("The test Xbox filename must parse.");

    private static (string OccurrenceId, string RevisionId) Identities(
        string sourceId,
        XboxDvrCandidateMetadata candidate)
    {
        var occurrence = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            sourceId, candidate.PortableRelativePath, candidate);
        return (
            occurrence,
            XboxDvrSourceAdapter.CreateRevisionIdentity(
                occurrence, ParsedTime(candidate), candidate));
    }

    private static XboxDvrContentImportRequest ImportRequest(
        RoutingInputSourceRecord source,
        XboxDvrCandidateMetadata candidate,
        (string OccurrenceId, string RevisionId) identities,
        string libraryRoot,
        long expectedRoutingGeneration = 1) => new(
        source.SourceId,
        source.Revision,
        expectedRoutingGeneration,
        source.RootIdentitySha256,
        source.CanonicalRoot,
        candidate,
        identities.OccurrenceId,
        identities.RevisionId,
        XboxDvrRuntimeLayout.GetSourceStagingRoot(libraryRoot, source.SourceId));

    private static Task<XboxDvrOccurrenceEntry> SelectAndBeginAsync(
        XboxDvrOccurrenceJournal journal,
        XboxDvrCandidateMetadata candidate,
        (string OccurrenceId, string RevisionId) identities) =>
        SelectAndBeginCoreAsync(journal, candidate, identities);

    private static async Task<XboxDvrOccurrenceEntry> SelectAndBeginCoreAsync(
        XboxDvrOccurrenceJournal journal,
        XboxDvrCandidateMetadata candidate,
        (string OccurrenceId, string RevisionId) identities)
    {
        var parsed = XboxDvrFileNameParser.Parse(candidate.PortableRelativePath);
        _ = await journal.SelectAsync(
            identities.OccurrenceId,
            identities.RevisionId,
            candidate.PortableRelativePath,
            parsed.GameName!,
            parsed.CapturedUtc!.Value,
            candidate.LogicalBytes,
            candidate.LastWriteUtcTicks);
        return await journal.BeginImportAsync(
            identities.OccurrenceId, identities.RevisionId);
    }

    private static RoutingSnapshotDocument Routes(IReadOnlyList<RoutingRoute> routes)
    {
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            routes,
            Now,
            Now);
        RoutingSnapshotModel.Validate(snapshot);
        return snapshot;
    }

    private static RoutingRoute XboxRoute(
        string sourceId,
        IReadOnlyList<XboxDvrCandidateMetadata>? historical = null) => new(
        Guid.NewGuid(),
        "Xbox Battlefield route",
        Enabled: true,
        Priority: 0,
        Revision: 1,
        RoutingRouteSource.User,
        RoutingRouteKind.Specific,
        RoutingTriggerKind.WatchedFolder,
        new RoutingPrepareSettings(
            Landscape: false,
            Portrait: false,
            RoutingMissingOutputBehavior.UseOriginal),
        [
            new RoutingCondition(
                Guid.NewGuid(),
                RoutingConditionField.Game,
                RoutingConditionOperator.Equals,
                "Battlefield 6"),
            new RoutingCondition(
                Guid.NewGuid(),
                RoutingConditionField.SourceConnection,
                RoutingConditionOperator.Equals,
                sourceId),
            new RoutingCondition(
                Guid.NewGuid(),
                RoutingConditionField.CapturedAt,
                RoutingConditionOperator.GreaterThanOrEqual,
                Cutoff.ToString("O", CultureInfo.InvariantCulture))
        ],
        [
            new RoutingAction(
                Guid.NewGuid(),
                Enabled: true,
                RoutingActionKind.FileIntoLibrary,
                Destination: null,
                ConnectionId: null,
                OutputRef: null,
                OnMissingOutput: null,
                RoutingDeliveryMode.Automatic,
                RoutingLibraryArea.LocalOnly,
                DeliverySettings: null)
        ],
        Now,
        Now,
        new RoutingXboxHistorySelection(
            Now,
            (historical ?? [])
                .Select(candidate => Identities(sourceId, candidate))
                .Select(identity => new RoutingXboxHistoricalOccurrence(
                    identity.OccurrenceId,
                    identity.RevisionId))
                .Distinct()
                .OrderBy(identity => identity.OccurrenceId, StringComparer.Ordinal)
                .ThenBy(identity => identity.RevisionId, StringComparer.Ordinal)
                .ToArray()));

    private static XboxDvrRuntimeHostOptions TestOptions(int maximumFailures = 3) => new(
        PollInterval: TimeSpan.Zero,
        ErrorRetryInterval: TimeSpan.Zero,
        MaximumErrorRetryInterval: TimeSpan.Zero,
        MaximumConsecutiveLoopFailures: maximumFailures,
        MaximumCandidatesPerSource: 100);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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

    private sealed record FixtureState(
        string LibraryRoot,
        FakeBackend Backend,
        XboxDvrRuntimeHost Host);

    private sealed class BlockingContentImporter : IXboxDvrContentImporter
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallCount { get; private set; }

        internal void Release() => _release.TrySetResult();

        public async Task<XboxDvrContentImportResult> ImportAsync(
            XboxDvrContentImportRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new XboxDvrContentImportResult(
                Path.Combine(
                    request.OwnedStagingRoot,
                    request.ExpectedOccurrenceIdentitySha256 + ".mp4"),
                request.ExpectedMetadata.LogicalBytes,
                RootIdentity,
                request.ExpectedOccurrenceIdentitySha256,
                request.ExpectedRevisionIdentitySha256);
        }
    }

    private sealed class FakeBackend : IXboxDvrRuntimeBackend
    {
        private readonly string _routingRoot;
        private readonly Dictionary<string, XboxDvrRuntimeMetadataSnapshot> _metadata;
        private readonly SemaphoreSlim _sourceExecutionGate = new(1, 1);
        private int _activeImports;
        private int _sourceExecutionGateWaiters;

        internal FakeBackend(
            string routingRoot,
            string libraryRoot,
            IReadOnlyList<RoutingInputSourceRecord> sources,
            RoutingSnapshotDocument routes,
            Dictionary<string, XboxDvrRuntimeMetadataSnapshot> metadata)
        {
            _routingRoot = Path.GetFullPath(routingRoot);
            LibraryRoot = Path.GetFullPath(libraryRoot);
            Directory.CreateDirectory(_routingRoot);
            Directory.CreateDirectory(LibraryRoot);
            Sources = new RoutingInputSourceCatalogSnapshot(
                RoutingDocumentLoadStatus.Loaded,
                Generation: 2,
                sources.ToArray());
            Routes = routes;
            _metadata = metadata;
        }

        internal string LibraryRoot { get; }
        internal RoutingInputSourceCatalogSnapshot Sources { get; private set; }
        internal RoutingSnapshotDocument Routes { get; private set; }
        internal Dictionary<string, int> MetadataFailuresRemaining { get; } =
            new(StringComparer.Ordinal);
        internal HashSet<string> MetadataInvalidSources { get; } =
            new(StringComparer.Ordinal);
        internal List<XboxDvrContentImportRequest> ImportRequests { get; } = [];
        internal List<string> ProbePaths { get; } = [];
        internal List<XboxDvrLibraryPromotionRequest> Promotions { get; } = [];
        internal List<XboxDvrLibraryPromotionIdentity> ResumeRequests { get; } = [];
        internal Dictionary<string, CaptureJournalDocument> ResumablePromotions { get; } =
            new(StringComparer.Ordinal);
        internal Dictionary<string, CaptureJournalDocument> Committed { get; } =
            new(StringComparer.Ordinal);
        internal HashSet<string> MarkedNeedsAttention { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, RoutingInputSourceAttentionReason> MarkedAttentionReasons
            { get; } = new(StringComparer.Ordinal);
        internal Action<XboxDvrContentImportRequest>? BeforeImport { get; set; }
        internal int ImportFailuresRemaining { get; set; }
        internal int ProbeFailuresRemaining { get; set; }
        internal int MarkNeedsAttentionFailuresRemaining { get; set; }
        internal int MaximumConcurrentImports { get; private set; }
        internal RoutingDocumentLoadStatus SourceCatalogStatus { get; set; } =
            RoutingDocumentLoadStatus.Loaded;
        internal RoutingDocumentLoadStatus RouteStatus { get; set; } =
            RoutingDocumentLoadStatus.Loaded;
        internal Action<int>? BeforeRouteLoad { get; set; }
        internal int RouteLoadCount { get; private set; }
        internal Action<int>? BeforeJournalMutation { get; set; }
        internal int JournalMutationCount { get; private set; }
        internal bool BlockProbe { get; set; }
        internal TaskCompletionSource ProbeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource ProbeRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int SourceExecutionGateCurrentCount => _sourceExecutionGate.CurrentCount;
        internal int SourceExecutionGateWaiters => Volatile.Read(ref _sourceExecutionGateWaiters);
        public bool HasCurrentAuthority { get; set; } = true;

        internal void ReleaseProbe() => ProbeRelease.TrySetResult();

        internal void SetMetadata(string sourceId, XboxDvrRuntimeMetadataSnapshot snapshot) =>
            _metadata[sourceId] = snapshot;

        internal void SetRoutes(RoutingSnapshotDocument routes) => Routes = routes;

        internal void SetSources(IReadOnlyList<RoutingInputSourceRecord> sources) =>
            Sources = Sources with
            {
                Generation = Sources.Generation + 1,
                Sources = sources.ToArray()
            };

        public RoutingInputSourceCatalogSnapshot InspectSources(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Sources with { StoreStatus = SourceCatalogStatus };
        }

        public async ValueTask<IDisposable> EnterSourceExecutionGateAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sourceExecutionGateWaiters);
            try
            {
                await _sourceExecutionGate.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _sourceExecutionGateWaiters);
            }
            return new SemaphoreLease(_sourceExecutionGate);
        }

        public RoutingDocumentLoadResult<RoutingSnapshotDocument> LoadRoutes(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RouteLoadCount++;
            BeforeRouteLoad?.Invoke(RouteLoadCount);
            return new RoutingDocumentLoadResult<RoutingSnapshotDocument>(
                RouteStatus == RoutingDocumentLoadStatus.Loaded ? Routes : null,
                RouteStatus);
        }

        public XboxDvrRuntimeMetadataSnapshot ReadMetadata(
            RoutingInputSourceRecord source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MetadataInvalidSources.Contains(source.SourceId))
            {
                throw new InvalidDataException("Injected invalid Xbox root authority.");
            }
            if (MetadataFailuresRemaining.TryGetValue(source.SourceId, out var failures) &&
                failures > 0)
            {
                MetadataFailuresRemaining[source.SourceId] = failures - 1;
                throw new IOException("Injected OneDrive metadata failure.");
            }
            return _metadata[source.SourceId];
        }

        public XboxDvrOccurrenceJournal OpenJournal(
            RoutingInputSourceRecord source,
            string rootIdentitySha256) => new(
            new XboxDvrOccurrenceStore(_routingRoot, source.SourceId),
            source.SourceId,
            rootIdentitySha256,
            () =>
            {
                JournalMutationCount++;
                BeforeJournalMutation?.Invoke(JournalMutationCount);
                return Now;
            });

        internal XboxDvrOccurrenceDocument LoadJournal(
            string sourceId,
            string rootIdentitySha256)
        {
            return new XboxDvrOccurrenceStore(_routingRoot, sourceId).Load().Document ??
                   throw new InvalidOperationException("The test occurrence journal is missing.");
        }

        public async Task<XboxDvrContentImportResult> ImportAsync(
            XboxDvrContentImportRequest request,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeImports);
            MaximumConcurrentImports = Math.Max(MaximumConcurrentImports, active);
            try
            {
                ImportRequests.Add(request);
                BeforeImport?.Invoke(request);
                if (ImportFailuresRemaining > 0)
                {
                    ImportFailuresRemaining--;
                    throw new IOException("Injected OneDrive hydration failure.");
                }
                var byteValue = (byte)(request.ExpectedMetadata.LastWriteUtcTicks & 0xff);
                var bytes = Enumerable.Repeat(
                        byteValue, checked((int)request.ExpectedMetadata.LogicalBytes))
                    .ToArray();
                var staged = Path.Combine(
                    request.OwnedStagingRoot,
                    request.ExpectedOccurrenceIdentitySha256 + ".mp4");
                await File.WriteAllBytesAsync(staged, bytes, cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return new XboxDvrContentImportResult(
                    staged,
                    bytes.LongLength,
                    hash,
                    request.ExpectedOccurrenceIdentitySha256,
                    request.ExpectedRevisionIdentitySha256);
            }
            finally
            {
                Interlocked.Decrement(ref _activeImports);
            }
        }

        public async Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
            string stagedPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(stagedPath)) throw new FileNotFoundException();
            if (BlockProbe)
            {
                ProbeEntered.TrySetResult();
                await ProbeRelease.Task.WaitAsync(cancellationToken);
            }
            if (ProbeFailuresRemaining > 0)
            {
                ProbeFailuresRemaining--;
                throw new InvalidDataException("Injected invalid media probe.");
            }
            ProbePaths.Add(stagedPath);
            return new RoutingWatchedFolderMediaInfo(
                TimeSpan.FromSeconds(30), 1920, 1080);
        }

        private sealed class SemaphoreLease(SemaphoreSlim gate) : IDisposable
        {
            private SemaphoreSlim? _gate = gate;

            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }

        public Task<CaptureJournalDocument?> FindCommittedAsync(
            string sourceId,
            string occurrenceId,
            string revisionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Committed.TryGetValue(occurrenceId, out var document);
            return Task.FromResult(document);
        }

        public Task<CaptureJournalDocument?> TryResumePromotionAsync(
            XboxDvrLibraryPromotionIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeRequests.Add(identity);
            if (!ResumablePromotions.TryGetValue(identity.OccurrenceId, out var document))
                return Task.FromResult<CaptureJournalDocument?>(null);
            Directory.CreateDirectory(Path.GetDirectoryName(identity.DestinationPath)!);
            File.Move(identity.StagedPath, identity.DestinationPath, overwrite: false);
            Committed[identity.OccurrenceId] = document;
            return Task.FromResult<CaptureJournalDocument?>(document);
        }

        public Task<CaptureJournalDocument> PromoteAsync(
            XboxDvrLibraryPromotionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Promotions.Add(request);
            Directory.CreateDirectory(Path.GetDirectoryName(request.Identity.DestinationPath)!);
            File.Move(
                request.Identity.StagedPath,
                request.Identity.DestinationPath,
                overwrite: false);
            var entry = LoadJournal(
                    request.Identity.SourceId,
                    _metadata[request.Identity.SourceId].RootIdentitySha256)
                .Occurrences.Single(item =>
                    item.OccurrenceId == request.Identity.OccurrenceId);
            var document = CreateCommittedDocument(
                entry, request.Identity.SourceId, request.Identity.DestinationPath);
            Committed[request.Identity.OccurrenceId] = document;
            return Task.FromResult(document);
        }

        internal CaptureJournalDocument CreateCommittedDocument(
            XboxDvrOccurrenceEntry entry,
            string sourceId,
            string destinationPath)
        {
            var relative = Path.GetRelativePath(LibraryRoot, destinationPath).Replace('\\', '/');
            var clipId = CaptureProjectStore.CreateProjectIdFromRelativeGameplayPath(relative);
            var bytes = File.Exists(destinationPath)
                ? File.ReadAllBytes(destinationPath)
                : Enumerable.Repeat((byte)0x7f, checked((int)entry.LogicalBytes)).ToArray();
            var fingerprint = new CaptureJournalFingerprint(
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            return CaptureJournalModel.CreateOriginalCommitted(
                new CaptureJournalClipMetadata(
                    clipId,
                    CaptureJournalSourceKind.XboxGameDvr,
                    entry.GameName,
                    entry.CapturedUtc,
                    TimeSpan.FromSeconds(30).Ticks,
                    1920,
                    1080,
                    ReactionCameraRequested: false,
                    RequestedRenditions: [],
                    new CaptureJournalArtifact(
                        $"{clipId}:original", "original", relative, fingerprint),
                    sourceId,
                    entry.OccurrenceId,
                    entry.RevisionId),
                Now);
        }

        public Task MarkSourceNeedsAttentionAsync(
            string sourceId,
            RoutingInputSourceAttentionReason attentionReason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MarkNeedsAttentionFailuresRemaining > 0)
            {
                MarkNeedsAttentionFailuresRemaining--;
                throw new IOException("Injected optional source-catalog write outage.");
            }
            MarkedNeedsAttention.Add(sourceId);
            MarkedAttentionReasons[sourceId] = attentionReason;
            Sources = Sources with
            {
                Sources = Sources.Sources.Select(source =>
                        source.SourceId == sourceId
                            ? source with
                            {
                                Revision = source.Revision + 1,
                                Health = RoutingInputSourceHealth.NeedsAttention,
                                AttentionReason = attentionReason,
                                UpdatedUtc = Now
                            }
                            : source)
                    .ToArray()
            };
            return Task.CompletedTask;
        }
    }
}
