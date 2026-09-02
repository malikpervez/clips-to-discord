using ClipsToDiscord;

internal static class RoutingWatchedFolderExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 18, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertResolverReturnsExactLeasedOriginalAsync(Path.Combine(testRoot, "resolver"));
        await AssertDeterministicMoveIsIdempotentAsync(Path.Combine(testRoot, "move"));
        await AssertPostMoveInspectionFailuresAreAmbiguousAsync(
            Path.Combine(testRoot, "post-move-inspection"));
        await AssertStartupRecoveryConvergesWithoutProviderResendAsync(
            Path.Combine(testRoot, "startup-recovery"));
        await AssertRecoveryTruthTableAsync(Path.Combine(testRoot, "recovery"));
    }

    private static async Task AssertResolverReturnsExactLeasedOriginalAsync(string root)
    {
        var fixture = await CreateFixtureAsync(
            root, includeDelivery: true, RoutingLibraryArea.Uploaded);
        var resolver = new WatchedFolderRoutingArtifactResolver(
            fixture.ClipsRoot, fixture.Journals);
        await using var artifact = await resolver.ResolveAsync(
            fixture.Plan.Deliveries.Single(), CancellationToken.None);
        Assert(artifact.Path == Path.GetFullPath(fixture.SourcePath) &&
               artifact.DisplayFileName == fixture.Journal.DisplayFileName &&
               artifact.GameName == fixture.Journal.GameName &&
               artifact.Sha256 == fixture.Journal.ContentSha256 &&
               artifact.ByteLength == fixture.Journal.FileIdentity.ByteLength,
            "The watched resolver must return exactly the journal-backed original artifact.");
        AssertThrows<IOException>(
            () => File.Move(fixture.SourcePath, fixture.SourcePath + ".moved", overwrite: false),
            "The resolved artifact must retain a read lease that blocks a concurrent move.");
    }

    private static async Task AssertDeterministicMoveIsIdempotentAsync(string root)
    {
        var fixture = await CreateFixtureAsync(root);
        var disposition = fixture.Plan.FileDisposition!;
        var filer = new WatchedFolderRoutingLibraryFiler(fixture.ClipsRoot, fixture.Journals);
        var expected = WatchedFolderLibraryLayout.GetDestinationPath(
            fixture.ClipsRoot, fixture.Journal, disposition, createDirectories: false);
        var expectedSuffix = "__cc-" + disposition.DispositionId.ToString("N") + ".mp4";

        var result = await filer.FileAsync(disposition, CancellationToken.None);
        Assert(result.Outcome == RoutingFileAttemptOutcome.Confirmed &&
               result.FinalLibraryItemReference ==
               WatchedFolderLibraryLayout.LibraryReference(disposition.DispositionId) &&
               File.Exists(expected) && !File.Exists(fixture.SourcePath) &&
               Path.GetFileName(expected).EndsWith(expectedSuffix, StringComparison.Ordinal) &&
               expected.Contains(Path.Combine("local-only", "Battlefield 6"),
                   StringComparison.OrdinalIgnoreCase),
            "A watched file must move once to its deterministic area/game/disposition path.");

        var repeated = await filer.FileAsync(disposition, CancellationToken.None);
        Assert(repeated.Outcome == RoutingFileAttemptOutcome.Confirmed && File.Exists(expected),
            "Repeating a confirmed watched move must converge on the same destination.");
    }

    private static async Task AssertPostMoveInspectionFailuresAreAmbiguousAsync(string root)
    {
        var failures = new (string Name, Func<Exception> Create)[]
        {
            ("io", () => new IOException("post-move-inspection-io")),
            ("unauthorized", () => new UnauthorizedAccessException(
                "post-move-inspection-unauthorized")),
            ("invalid-data", () => new InvalidDataException(
                "post-move-inspection-invalid-data"))
        };

        foreach (var failure in failures)
        {
            var fixture = await CreateFixtureAsync(Path.Combine(root, failure.Name));
            var disposition = fixture.Plan.FileDisposition!;
            var destination = WatchedFolderLibraryLayout.GetDestinationPath(
                fixture.ClipsRoot,
                fixture.Journal,
                disposition,
                createDirectories: false);
            var filer = new WatchedFolderRoutingLibraryFiler(
                fixture.ClipsRoot,
                fixture.Journals,
                point =>
                {
                    if (point == WatchedFolderRoutingInspectionPoint.AfterMoveAttempt)
                    {
                        throw failure.Create();
                    }
                });

            var result = await filer.FileAsync(disposition, CancellationToken.None);
            Assert(result.Outcome == RoutingFileAttemptOutcome.Unknown &&
                   result.ErrorCode == "watched-file-move-unknown" &&
                   !File.Exists(fixture.SourcePath) &&
                   File.Exists(destination),
                $"A {failure.Name} inspection failure after the move attempt must remain ambiguous for startup recovery.");
        }

        var beforeMove = await CreateFixtureAsync(Path.Combine(root, "before-move"));
        var definite = await new WatchedFolderRoutingLibraryFiler(
                beforeMove.ClipsRoot,
                beforeMove.Journals,
                point =>
                {
                    if (point == WatchedFolderRoutingInspectionPoint.BeforeMove)
                    {
                        throw new InvalidDataException("pre-move-evidence-invalid");
                    }
                })
            .FileAsync(beforeMove.Plan.FileDisposition!, CancellationToken.None);
        Assert(definite.Outcome == RoutingFileAttemptOutcome.Failed &&
               File.Exists(beforeMove.SourcePath),
            "A proven pre-move evidence failure must remain a definite failure without moving the source.");
    }

    private static async Task AssertStartupRecoveryConvergesWithoutProviderResendAsync(
        string root)
    {
        var fixture = await CreateFixtureAsync(
            root,
            includeDelivery: true,
            RoutingLibraryArea.Uploaded);
        var disposition = fixture.Plan.FileDisposition!;
        var delivery = fixture.Plan.Deliveries.Single();
        var outbox = new RoutingOutboxStore(Path.Combine(
            root,
            "outbox",
            RoutingOutboxStore.FileName));
        var current = await outbox.LoadOrCreateAsync(Now);
        current = await outbox.SaveAsync(
            RoutingOutboxModel.AppendPlan(
                current,
                fixture.Plan.PlanId,
                fixture.Plan.Deliveries,
                [disposition],
                Now.AddSeconds(1)),
            current.Generation);
        var deliveryAttemptId = Guid.Parse("41000000-0000-0000-0000-000000000001");
        current = await outbox.SaveAsync(
            RoutingOutboxModel.StartDelivery(
                current,
                delivery.DeliveryId,
                deliveryAttemptId,
                Now.AddSeconds(2)),
            current.Generation);
        const string providerReceipt = "discord-receipt-before-file-move";
        current = await outbox.SaveAsync(
            RoutingOutboxModel.CompleteDelivery(
                current,
                delivery.DeliveryId,
                deliveryAttemptId,
                providerReceipt,
                Now.AddSeconds(3)),
            current.Generation);
        current = await outbox.SaveAsync(
            RoutingOutboxModel.RefreshFileDisposition(
                current,
                disposition.DispositionId,
                Now.AddSeconds(4)),
            current.Generation);

        var provider = new RejectingProvider();
        var resolver = new RejectingResolver();
        var ambiguousFiler = new WatchedFolderRoutingLibraryFiler(
            fixture.ClipsRoot,
            fixture.Journals,
            point =>
            {
                if (point is WatchedFolderRoutingInspectionPoint.AfterMoveAttempt or
                    WatchedFolderRoutingInspectionPoint.Recovery)
                {
                    throw new IOException("inspection-unavailable-until-restart");
                }
            });
        using (var firstExecutor = new RoutingOutboxExecutor(
                   outbox,
                   provider,
                   resolver,
                   ambiguousFiler,
                   () => true,
                   maximumSideEffectsPerRun: 4,
                   createAttemptId: () =>
                       Guid.Parse("42000000-0000-0000-0000-000000000001"),
                   utcNow: () => Now.AddSeconds(5),
                   maximumRecoveryInspectionsPerRun: 4,
                   captureLibraryPermit: CurrentCaptureLibraryPermit()))
        {
            var first = await firstExecutor.RunOnceAsync();
            var pending = outbox.Load().Document!.FileDispositions.Single();
            Assert(first.ProviderAttempts == 0 && first.FileAttempts == 1 &&
                   pending.State == PlannedFileDispositionState.RecoveryPending &&
                   pending.ErrorCode == "watched-file-move-unknown",
                "An ambiguous post-move inspection must persist RecoveryPending rather than a definite filing failure.");
        }

        var destination = WatchedFolderLibraryLayout.GetDestinationPath(
            fixture.ClipsRoot,
            fixture.Journal,
            disposition,
            createDirectories: false);
        Assert(!File.Exists(fixture.SourcePath) && File.Exists(destination),
            "The ambiguous filing fixture must represent a completed physical move awaiting durable reconciliation.");

        using (var restartedExecutor = new RoutingOutboxExecutor(
                   outbox,
                   provider,
                   resolver,
                   new WatchedFolderRoutingLibraryFiler(
                       fixture.ClipsRoot,
                       fixture.Journals),
                   () => true,
                   maximumSideEffectsPerRun: 4,
                   utcNow: () => Now.AddSeconds(6),
                   maximumRecoveryInspectionsPerRun: 4,
                   captureLibraryPermit: CurrentCaptureLibraryPermit()))
        {
            var restarted = await restartedExecutor.RunOnceAsync();
            var recovered = outbox.Load().Document!;
            var recoveredDelivery = recovered.Deliveries.Single();
            var recoveredDisposition = recovered.FileDispositions.Single();
            Assert(restarted.ProviderAttempts == 0 &&
                   restarted.FileAttempts == 0 &&
                   restarted.RecoveryInspections == 1 &&
                   provider.SendCalls == 0 && resolver.ResolveCalls == 0 &&
                   recoveredDelivery.State == PlannedDeliveryState.Delivered &&
                   recoveredDelivery.Attempts == 1 &&
                   recoveredDelivery.RemoteReceiptReference == providerReceipt &&
                   recoveredDisposition.State == PlannedFileDispositionState.Completed &&
                   recoveredDisposition.FinalLibraryItemReference ==
                   WatchedFolderLibraryLayout.LibraryReference(disposition.DispositionId) &&
                   !File.Exists(fixture.SourcePath) && File.Exists(destination),
                "Startup reconciliation must complete the exact moved destination without repeating the provider upload or file move.");
        }
    }

    private static async Task AssertRecoveryTruthTableAsync(string root)
    {
        var retry = await CreateFixtureAsync(Path.Combine(root, "source-exact"));
        Assert((await Filer(retry).ReconcileAsync(retry.Plan.FileDisposition!, CancellationToken.None))
                   .Outcome == RoutingFileRecoveryOutcome.RetrySafe,
            "Exact source plus missing destination must be retry-safe.");

        var complete = await CreateFixtureAsync(Path.Combine(root, "destination-exact"));
        MoveToDestination(complete);
        Assert((await Filer(complete).ReconcileAsync(
                   complete.Plan.FileDisposition!, CancellationToken.None)).Outcome ==
               RoutingFileRecoveryOutcome.Completed,
            "Missing source plus exact destination must recover as completed.");

        var bothMissing = await CreateFixtureAsync(Path.Combine(root, "both-missing"));
        File.Delete(bothMissing.SourcePath);
        Assert((await Filer(bothMissing).ReconcileAsync(
                   bothMissing.Plan.FileDisposition!, CancellationToken.None)).Outcome ==
               RoutingFileRecoveryOutcome.Unresolved,
            "Missing source plus missing destination must stay unresolved.");

        var bothExact = await CreateFixtureAsync(Path.Combine(root, "both-exact"));
        CopyToDestination(bothExact, exact: true);
        Assert((await Filer(bothExact).ReconcileAsync(
                   bothExact.Plan.FileDisposition!, CancellationToken.None)).Outcome ==
               RoutingFileRecoveryOutcome.Unresolved,
            "Two exact copies are ambiguous and must stay unresolved.");

        var wrongDestination = await CreateFixtureAsync(Path.Combine(root, "wrong-destination"));
        CopyToDestination(wrongDestination, exact: false);
        Assert((await Filer(wrongDestination).ReconcileAsync(
                   wrongDestination.Plan.FileDisposition!, CancellationToken.None)).Outcome ==
               RoutingFileRecoveryOutcome.Unresolved,
            "A conflicting deterministic destination must stay unresolved.");

        var wrongSource = await CreateFixtureAsync(Path.Combine(root, "wrong-source"));
        await File.AppendAllTextAsync(wrongSource.SourcePath, "changed");
        Assert((await Filer(wrongSource).ReconcileAsync(
                   wrongSource.Plan.FileDisposition!, CancellationToken.None)).Outcome ==
               RoutingFileRecoveryOutcome.Unresolved,
            "Changed source content plus missing destination must stay unresolved.");
    }

    private static WatchedFolderRoutingLibraryFiler Filer(Fixture fixture) =>
        new(fixture.ClipsRoot, fixture.Journals);

    private static void MoveToDestination(Fixture fixture)
    {
        var destination = WatchedFolderLibraryLayout.GetDestinationPath(
            fixture.ClipsRoot, fixture.Journal, fixture.Plan.FileDisposition!, createDirectories: true);
        File.Move(fixture.SourcePath, destination, overwrite: false);
    }

    private static void CopyToDestination(Fixture fixture, bool exact)
    {
        var destination = WatchedFolderLibraryLayout.GetDestinationPath(
            fixture.ClipsRoot, fixture.Journal, fixture.Plan.FileDisposition!, createDirectories: true);
        if (exact) File.Copy(fixture.SourcePath, destination, overwrite: false);
        else File.WriteAllBytes(destination, [91, 92, 93, 94]);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string root,
        bool includeDelivery = false,
        RoutingLibraryArea libraryArea = RoutingLibraryArea.LocalOnly)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var sourcePath = Path.Combine(
            clips, "Battlefield 6 2026.08.29 - 18.00.00.01.DVR.mp4");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(1, 128)
            .Select(value => (byte)value).ToArray());
        var source = await RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg)
            .OpenAndFingerprintAsync(clips, sourcePath);
        var occurrence = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            source, new string('a', 64));
        var sourceClipId = RoutingWatchedJournalModel.SourcePrefix + occurrence;
        var output = RoutingEvaluator.CreateLogicalOutputReference(
            sourceClipId, source.ContentSha256, RoutingOutputKind.Original);
        var actions = new List<RoutingAction>();
        if (includeDelivery)
        {
            actions.Add(new RoutingAction(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                true,
                RoutingActionKind.Deliver,
                RoutingDestinationKind.Discord,
                "discord.fixture",
                RoutingOutputKind.Original,
                RoutingMissingOutputBehavior.UseOriginal,
                RoutingDeliveryMode.Automatic,
                null,
                new RoutingDeliverySettings(null, null, null,
                    RoutingVisibility.Unspecified, false)));
        }
        actions.Add(new RoutingAction(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            true,
            RoutingActionKind.FileIntoLibrary,
            null, null, null, null,
            RoutingDeliveryMode.Automatic,
            libraryArea,
            null));
        var route = new RoutingRoute(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            "Watched clips",
            Enabled: true,
            Priority: 0,
            Revision: 1,
            RoutingRouteSource.User,
            RoutingRouteKind.Specific,
            RoutingTriggerKind.WatchedFolder,
            new RoutingPrepareSettings(false, false, RoutingMissingOutputBehavior.UseOriginal),
            [],
            actions,
            Now,
            Now);
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            1,
            [route],
            Now,
            Now);
        RoutingSnapshotModel.Validate(snapshot);
        var facts = new RoutingClipFacts(
            sourceClipId,
            RoutingEvaluationEventKind.SourceArrival,
            RoutingClipSource.WatchedFolder,
            RoutingTriggerKind.WatchedFolder,
            CaptureType: null,
            source.GameName,
            ReactionCamera: false,
            DurationMilliseconds: 10_000,
            source.ContentSha256,
            [new RoutingClipOutputRevision(output, RoutingOutputAvailability.Ready, null)]);
        var plan = RoutingEvaluator.CreatePlan(
            snapshot,
            facts,
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            [],
            Now);
        Assert(plan.Deliveries.Count == (includeDelivery ? 1 : 0) &&
               plan.FileDisposition is not null &&
               plan.FileDisposition.LibraryArea == libraryArea,
            "The watched executor fixture must create the requested frozen work.");
        var journal = RoutingWatchedJournalModel.Create(
            RoutingWatchedJournalAdmissionKind.PreparedPlan,
            occurrence,
            source.RootIdentitySha256,
            new string('a', 64),
            new string('b', 64),
            source.Source,
            source.PortableRelativePath,
            source.DisplayFileName,
            source.GameName,
            source.NativeFileIdentity,
            source.ContentSha256,
            10_000,
            1920,
            1080,
            plan,
            duplicateOfSourceClipId: null,
            Now);
        var journals = new RoutingWatchedSourceJournalStore(Path.Combine(root, "journals"));
        await journals.PersistExactAsync(journal);
        return new Fixture(clips, sourcePath, journals, journal, plan);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static RoutingCaptureLibraryPermit CurrentCaptureLibraryPermit()
    {
        var binding = new RoutingCaptureLibraryBinding(
            new string('A', 64),
            new string('B', 64));
        return new RoutingCaptureLibraryPermit(binding, () => binding);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RejectingProvider : IRoutingDeliveryProvider
    {
        internal int SendCalls { get; private set; }

        public bool Supports(RoutingDestinationKind destination) => true;

        public Task<RoutingDeliveryAttemptResult> SendAsync(
            PlannedDelivery delivery,
            RoutingResolvedArtifact artifact,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            throw new InvalidOperationException(
                "Startup filing recovery must not repeat a completed provider upload.");
        }
    }

    private sealed class RejectingResolver : IRoutingArtifactResolver
    {
        internal int ResolveCalls { get; private set; }

        public Task<RoutingResolvedArtifact> ResolveAsync(
            PlannedDelivery delivery,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            throw new InvalidOperationException(
                "Startup filing recovery must not resolve an already-delivered upload again.");
        }
    }

    private sealed record Fixture(
        string ClipsRoot,
        string SourcePath,
        RoutingWatchedSourceJournalStore Journals,
        RoutingWatchedSourceJournalDocument Journal,
        RoutingPlanProposal Plan);
}
