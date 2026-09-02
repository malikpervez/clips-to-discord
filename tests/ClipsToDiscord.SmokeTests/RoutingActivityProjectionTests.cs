using ClipsToDiscord;

internal static class RoutingActivityProjectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var libraryRoot = Path.Combine(root, "capture-library");
        var watchedRoot = Path.Combine(root, "watched");
        var routingRoot = Path.Combine(root, "routing");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(watchedRoot);
        Directory.CreateDirectory(routingRoot);

        var gameRoot = Path.Combine(libraryRoot, "Library", "Game", "NBA 2K27");
        Directory.CreateDirectory(gameRoot);
        var originalPath = Path.Combine(
            gameRoot,
            "Xbox__2026-09-01__02-35-02Z__activity-test.mp4");
        var originalBytes = Enumerable.Range(0, 8192)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await File.WriteAllBytesAsync(originalPath, originalBytes);

        var journal = await CaptureJournalStore.CommitOriginalAsync(
            libraryRoot,
            originalPath,
            CaptureJournalSourceKind.XboxGameDvr,
            "NBA 2K27",
            Now.AddDays(-1),
            TimeSpan.FromSeconds(30),
            1920,
            1080,
            reactionCameraRequested: false,
            requestedRenditions: [],
            now: Now.AddDays(-1),
            sourceConnectionId: new string('a', 64),
            sourceOccurrenceId: new string('b', 64),
            sourceRevisionId: new string('c', 64),
            localOnlyOverride: new RoutingLocalOnlyAdmissionSnapshot(
                Enabled: false,
                StateRevision: 1,
                FailSafe: false));

        var outbox = new RoutingOutboxStore(
            Path.Combine(routingRoot, RoutingOutboxStore.FileName));
        var current = await outbox.LoadOrCreateAsync(Now);
        var planId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var dispositionId = Guid.NewGuid();
        var route = new RoutingRouteSnapshotReference(
            1,
            Guid.NewGuid(),
            1,
            "2K Clips",
            Guid.NewGuid(),
            0,
            0);
        var output = new RoutingOutputReference(
            journal.Clip.ClipId,
            RoutingOutputKind.Original,
            journal.Clip.Original.Fingerprint.Sha256);
        var delivery = RoutingOutboxModel.CreateDelivery(
            deliveryId,
            planId,
            journal.Clip.ClipId,
            route,
            RoutingDestinationKind.Discord,
            "discord.activity-test",
            output,
            output,
            RoutingMissingOutputBehavior.NeedsAttention,
            RoutingDeliveryMode.Automatic,
            new RoutingDeliverySettings(
                null,
                null,
                null,
                RoutingVisibility.Unspecified,
                false),
            artifactReady: true,
            intentionalDuplicate: null,
            Now);
        var disposition = RoutingOutboxModel.CreateFileDisposition(
            dispositionId,
            planId,
            journal.Clip.ClipId,
            journal.Clip.Original.Fingerprint.Sha256,
            route with { ActionId = Guid.NewGuid(), Order = 1 },
            RoutingLibraryArea.Uploaded,
            [deliveryId],
            Now);
        current = await SaveNextAsync(outbox, current, value =>
            RoutingOutboxModel.AppendPlan(
                value,
                planId,
                [delivery],
                [disposition],
                Now.AddSeconds(1)));
        var deliveryAttempt = Guid.NewGuid();
        current = await SaveNextAsync(outbox, current, value =>
            RoutingOutboxModel.StartDelivery(
                value,
                deliveryId,
                deliveryAttempt,
                Now.AddSeconds(2)));
        current = await SaveNextAsync(outbox, current, value =>
            RoutingOutboxModel.CompleteDelivery(
                value,
                deliveryId,
                deliveryAttempt,
                "discord:activity-test",
                Now.AddSeconds(3)));
        current = await SaveNextAsync(outbox, current, value =>
            RoutingOutboxModel.RefreshFileDisposition(
                value,
                dispositionId,
                Now.AddSeconds(4)));
        var fileAttempt = Guid.NewGuid();
        current = await SaveNextAsync(outbox, current, value =>
            RoutingOutboxModel.StartFileDisposition(
                value,
                dispositionId,
                fileAttempt,
                Now.AddSeconds(5)));
        current = await SaveNextAsync(outbox, current, value =>
            RoutingOutboxModel.CompleteFileDisposition(
                value,
                dispositionId,
                fileAttempt,
                $"capture:{journal.Clip.ClipId}:original",
                Now.AddSeconds(6)));

        var watchedJournals = new RoutingWatchedSourceJournalStore(
            Path.Combine(routingRoot, "watched-journal", "v1"));
        var projection = new RoutingActivityProjection(
            new RoutingDeliveryHistoryReader(outbox),
            libraryRoot,
            watchedRoot,
            watchedJournals);
        AssertXboxEntry(projection.Read(), planId, originalPath, originalBytes.LongLength);

        using (var activity = new ActivityHistoryStore(Path.Combine(root, "activity.json")))
        {
            activity.SetExternalEntriesProvider(() => projection.Read());
            AssertXboxEntry(activity.GetSnapshot().Entries, planId, originalPath,
                originalBytes.LongLength);
            Assert(!File.Exists(Path.Combine(root, "activity.json")),
                "Routing projection must remain read-only and must not mirror authority into activity.json.");
        }

        var archive = RoutingArchiveModel.Create(current, planId);
        await outbox.ArchiveStore.PersistExactAsync(archive);
        AssertXboxEntry(projection.Read(), planId, originalPath, originalBytes.LongLength);

        var compacted = RoutingArchiveModel.RemoveArchivedPlans(
            current,
            [archive],
            Now.AddSeconds(7));
        _ = await outbox.SaveCompactedAsync(
            compacted,
            current.Generation,
            [archive]);
        var restartedProjection = new RoutingActivityProjection(
            new RoutingDeliveryHistoryReader(new RoutingOutboxStore(
                Path.Combine(routingRoot, RoutingOutboxStore.FileName))),
            libraryRoot,
            watchedRoot,
            new RoutingWatchedSourceJournalStore(
                Path.Combine(routingRoot, "watched-journal", "v1")));
        AssertXboxEntry(
            restartedProjection.Read(),
            planId,
            originalPath,
            originalBytes.LongLength);

        await AssertNamedHistorySurvivesUnavailableRootAsync(
            Path.Combine(root, "named-unavailable"));
    }

    private static async Task AssertNamedHistorySurvivesUnavailableRootAsync(string root)
    {
        Directory.CreateDirectory(root);
        var libraryRoot = Directory.CreateDirectory(
            Path.Combine(root, "capture-library")).FullName;
        var legacyRoot = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
        var namedRoot = Directory.CreateDirectory(
            Path.Combine(root, "SteelSeries on games drive")).FullName;
        var routingRoot = Directory.CreateDirectory(Path.Combine(root, "routing")).FullName;
        var clipPath = Path.Combine(namedRoot, "Battlefield 6 2026-09-02.mp4");
        await File.WriteAllBytesAsync(clipPath, Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251))
            .ToArray());

        const string sourceId = "source.a1000000000000000000000000000001";
        var rootIdentity = RoutingWatchedSourceRootIdentity.Create(
            RoutingInputSourceKind.SteelSeriesGg,
            namedRoot);
        var sourceRecord = new RoutingInputSourceRecord(
            sourceId,
            Revision: 1,
            "SteelSeries on games drive",
            RoutingInputSourceKind.SteelSeriesGg,
            namedRoot,
            Enabled: true,
            Retired: false,
            RoutingInputSourceHealth.Ready,
            RoutingInputSourceAttentionReason.None,
            rootIdentity,
            TimeZoneInfo.Utc.Id,
            Now.AddSeconds(1),
            Now.AddSeconds(1));
        var sourceStore = new RoutingInputSourceCatalogStore(Path.Combine(
            routingRoot,
            RoutingInputSourceCatalogStore.FileName));
        var emptySources = await sourceStore.LoadOrCreateAsync(Now);
        var sources = RoutingInputSourceCatalogModel.ReplaceSources(
            emptySources,
            [sourceRecord],
            Now.AddSeconds(1));
        _ = await sourceStore.SaveAsync(sources, emptySources.Generation);
        var sourceCatalog = new RoutingInputSourceCatalog(
            sourceStore,
            NoReferences.Instance);
        var rootResolver = new RoutingWatchedSourceRootResolver(legacyRoot, sourceCatalog);

        var source = await RoutingWatchedSourceAdapters
            .Get(ClipCaptureSource.SteelSeriesGg)
            .OpenAndFingerprintAsync(namedRoot, clipPath);
        var authorityFingerprint =
            RoutingWatchedJournalFactory.CreateNamedAuthorityFingerprint(sourceRecord);
        var occurrence = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            source,
            authorityFingerprint);
        var sourceClipId = RoutingWatchedJournalModel.SourcePrefix + occurrence;
        var fileAction = new RoutingAction(
            Guid.Parse("a1000000-0000-0000-0000-000000000002"),
            Enabled: true,
            RoutingActionKind.FileIntoLibrary,
            Destination: null,
            ConnectionId: null,
            OutputRef: null,
            OnMissingOutput: null,
            RoutingDeliveryMode.Automatic,
            RoutingLibraryArea.LocalOnly,
            DeliverySettings: null);
        var route = new RoutingRoute(
            Guid.Parse("a1000000-0000-0000-0000-000000000001"),
            "Named source Activity fixture",
            Enabled: true,
            Priority: 0,
            Revision: 1,
            RoutingRouteSource.User,
            RoutingRouteKind.Fallback,
            RoutingTriggerKind.AnyNewSourceClip,
            new RoutingPrepareSettings(
                Landscape: false,
                Portrait: false,
                RoutingMissingOutputBehavior.UseOriginal),
            Conditions: [],
            Actions: [fileAction],
            CreatedUtc: Now.AddSeconds(2),
            ModifiedUtc: Now.AddSeconds(2));
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [route],
            CreatedUtc: Now.AddSeconds(2),
            UpdatedUtc: Now.AddSeconds(2));
        var original = RoutingEvaluator.CreateLogicalOutputReference(
            sourceClipId,
            source.ContentSha256,
            RoutingOutputKind.Original);
        var facts = new RoutingClipFacts(
            sourceClipId,
            RoutingEvaluationEventKind.SourceArrival,
            RoutingClipSource.WatchedFolder,
            RoutingTriggerKind.WatchedFolder,
            CaptureType: null,
            source.GameName,
            ReactionCamera: false,
            DurationMilliseconds: 30_000,
            source.ContentSha256,
            [
                new RoutingClipOutputRevision(
                    original,
                    RoutingOutputAvailability.Ready,
                    FailureCode: null),
                MissingOutput(sourceClipId, source.ContentSha256,
                    RoutingOutputKind.Landscape),
                MissingOutput(sourceClipId, source.ContentSha256,
                    RoutingOutputKind.Portrait)
            ],
            SourceConnectionId: sourceId);
        var proposal = RoutingEvaluator.CreatePlan(
            snapshot,
            facts,
            Guid.Parse("a1000000-0000-0000-0000-000000000003"),
            deliberateDuplicateAuthorizations: [],
            Now.AddSeconds(2));
        var watched = RoutingWatchedJournalModel.Create(
            RoutingWatchedJournalAdmissionKind.PreparedPlan,
            occurrence,
            source.RootIdentitySha256,
            authorityFingerprint,
            new string('d', 64),
            source.Source,
            source.PortableRelativePath,
            source.DisplayFileName,
            source.GameName,
            source.NativeFileIdentity,
            source.ContentSha256,
            durationMilliseconds: 30_000,
            width: 1920,
            height: 1080,
            proposal,
            duplicateOfSourceClipId: null,
            Now.AddSeconds(2),
            sourceConnectionId: sourceId);
        var watchedJournals = new RoutingWatchedSourceJournalStore(Path.Combine(
            routingRoot,
            "watched-journal",
            "v1"));
        _ = await watchedJournals.PersistExactAsync(watched);
        var outbox = new RoutingOutboxStore(Path.Combine(
            routingRoot,
            RoutingOutboxStore.FileName));
        var emptyOutbox = await outbox.LoadOrCreateAsync(Now.AddSeconds(2));
        _ = await outbox.SaveAsync(
            RoutingOutboxModel.AppendEvaluatedPlan(
                emptyOutbox,
                proposal,
                Now.AddSeconds(2)),
            emptyOutbox.Generation);

        var projection = new RoutingActivityProjection(
            new RoutingDeliveryHistoryReader(outbox),
            libraryRoot,
            legacyRoot,
            watchedJournals,
            rootResolver.ResolveConfigured,
            rootResolver.IsCurrent);
        var available = projection.Read().Single();
        Assert(rootResolver.Resolve(watched).Equals(
                   namedRoot, StringComparison.OrdinalIgnoreCase) &&
               rootResolver.ResolveConfigured(watched).Equals(
                   namedRoot, StringComparison.OrdinalIgnoreCase) &&
               rootResolver.IsCurrent(watched) &&
               available.SourcePath.Equals(clipPath, StringComparison.OrdinalIgnoreCase) &&
               available.CurrentPath!.Equals(clipPath, StringComparison.OrdinalIgnoreCase),
            "A live named source must resolve through its exact catalog and native-root authority.");

        Directory.Delete(namedRoot, recursive: true);
        var offline = projection.Read().Single();
        Assert(rootResolver.ResolveConfigured(watched).Equals(
                   namedRoot, StringComparison.OrdinalIgnoreCase) &&
               !rootResolver.IsCurrent(watched) &&
               offline.SourcePath.Equals(clipPath, StringComparison.OrdinalIgnoreCase) &&
               offline.CurrentPath is null,
            "An offline named source must retain its durable Activity row while disabling filesystem actions.");
        AssertThrows<IOException>(
            () => rootResolver.Resolve(watched),
            "Strict artifact resolution must still reject an offline named source.");

        Directory.CreateDirectory(namedRoot);
        var replaced = projection.Read().Single();
        Assert(!rootResolver.IsCurrent(watched) &&
               replaced.SourcePath.Equals(clipPath, StringComparison.OrdinalIgnoreCase) &&
               replaced.CurrentPath is null,
            "A different folder recreated at the configured path must retain history but never regain Show in folder authority.");
        AssertThrows<InvalidDataException>(
            () => rootResolver.Resolve(watched),
            "Strict artifact resolution must reject a replacement folder at the same path.");

        var legacyJournal = watched with { SourceConnectionId = null };
        RoutingWatchedJournalModel.Validate(legacyJournal);
        Assert(rootResolver.ResolveConfigured(legacyJournal).Equals(
                   legacyRoot, StringComparison.OrdinalIgnoreCase) &&
               rootResolver.Resolve(legacyJournal).Equals(
                   legacyRoot, StringComparison.OrdinalIgnoreCase) &&
               rootResolver.IsCurrent(legacyJournal),
            "Legacy watched Activity behavior must remain rooted in the configured 1.x folder.");
    }

    private static RoutingClipOutputRevision MissingOutput(
        string clipId,
        string contentHash,
        RoutingOutputKind kind) => new(
        RoutingEvaluator.CreateLogicalOutputReference(clipId, contentHash, kind),
        RoutingOutputAvailability.PermanentlyMissing,
        "watched-output-not-produced");

    private static void AssertXboxEntry(
        IReadOnlyList<ClipActivityEntry> entries,
        Guid planId,
        string originalPath,
        long originalBytes)
    {
        Assert(entries.Count == 1, "One Routing plan must produce exactly one Activity row.");
        var entry = entries[0];
        Assert(entry.Id == planId &&
               entry.State == ClipActivityState.Completed &&
               entry.Route == ClipActivityRoute.Uploaded &&
               entry.GameName == "NBA 2K27" &&
               entry.SourcePath.Equals(originalPath, StringComparison.OrdinalIgnoreCase) &&
               entry.CurrentPath!.Equals(originalPath, StringComparison.OrdinalIgnoreCase) &&
               entry.OriginalBytes == originalBytes &&
               entry.AttemptCount == 1 &&
               entry.CompressedBytes is null &&
               entry.Detail == "Delivered to Discord",
            "A confirmed Xbox route must project its stable plan identity and managed artifact metadata into Activity.");
    }

    private static Task<RoutingOutboxDocument> SaveNextAsync(
        RoutingOutboxStore store,
        RoutingOutboxDocument current,
        Func<RoutingOutboxDocument, RoutingOutboxDocument> transition) =>
        store.SaveAsync(transition(current), current.Generation);

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class NoReferences : IRoutingInputSourceReferenceProbe
    {
        internal static NoReferences Instance { get; } = new();

        public RoutingInputSourceReferenceStatus Inspect(
            string sourceId,
            CancellationToken cancellationToken = default) =>
            RoutingInputSourceReferenceStatus.NotReferenced;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
