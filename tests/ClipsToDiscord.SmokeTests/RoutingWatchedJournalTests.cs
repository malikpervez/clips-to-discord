using ClipsToDiscord;

internal static class RoutingWatchedJournalTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 15, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        AssertOccurrenceIdentityBindsRootAndNativeFile();
        await AssertImmutablePerSourceStoreAsync(testRoot);
        AssertMalformedJournalsFailClosed(testRoot);
        AssertFrozenPlanIsStructurallyPinned();
    }

    private static void AssertOccurrenceIdentityBindsRootAndNativeFile()
    {
        var first = CreateSource("c:\\clips", new string('1', 64));
        var same = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            first,
            new string('A', 64));
        var repeated = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            first,
            new string('A', 64));
        var otherRoot = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            first with { RootIdentitySha256 = new string('2', 64) },
            new string('A', 64));
        var otherNativeFile = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            first with
            {
                NativeFileIdentity = first.NativeFileIdentity with
                {
                    FileIdHex = "0000000000000002"
                }
            },
            new string('A', 64));
        Assert(same == repeated && same != otherRoot && same != otherNativeFile,
            "A watched occurrence must be stable on restart and change with root or native-file authority.");
    }

    private static async Task AssertImmutablePerSourceStoreAsync(string root)
    {
        var test = Path.Combine(root, "immutable");
        var store = new RoutingWatchedSourceJournalStore(test);
        var source = CreateSource(test, new string('3', 64));
        var document = CreatePreparedDocument(source);
        var saved = await store.PersistExactAsync(document);
        var repeated = await store.PersistExactAsync(document);
        var loaded = store.Load(document.SourceClipId);
        Assert(saved.SourceClipId == document.SourceClipId && repeated.SourceClipId == document.SourceClipId &&
               loaded.LoadedFromDisk && loaded.Document is not null,
            "An exact watched journal retry must converge on one immutable source document.");
        RoutingWatchedJournalModel.RequireExact(loaded.Document!, document);

        var payload = File.ReadAllText(store.PathForSource(document.SourceClipId));
        Assert(!payload.Contains(source.CanonicalRoot, StringComparison.OrdinalIgnoreCase),
            "A watched journal must not persist the user's absolute capture-folder path.");
        Assert(store.EnumerateSourceClipIds().SequenceEqual([document.SourceClipId]),
            "Watched journal enumeration must recover the exact source identity.");

        await AssertThrowsAsync<InvalidDataException>(async () =>
            await store.PersistExactAsync(document with { GameName = "Different game" }),
            "A conflicting occurrence must never replace an existing watched journal.");
        Assert(store.Load("capture-id").Status ==
               RoutingWatchedJournalLoadStatus.IdentityMismatch,
            "The watched journal store must structurally reject non-watched source ids.");
    }

    private static void AssertMalformedJournalsFailClosed(string root)
    {
        var test = Path.Combine(root, "malformed");
        var store = new RoutingWatchedSourceJournalStore(test);
        var document = CreatePreparedDocument(CreateSource(test, new string('4', 64)));
        _ = store.PersistExactAsync(document).GetAwaiter().GetResult();
        var path = store.PathForSource(document.SourceClipId);
        File.WriteAllText(path, "not-json");
        Assert(store.Load(document.SourceClipId).Status == RoutingWatchedJournalLoadStatus.Corrupt,
            "A corrupt watched journal must fail closed instead of becoming missing.");

        var unsafePath = document with { SourceRelativePath = "../escape.mp4" };
        AssertThrows<InvalidDataException>(() => RoutingWatchedJournalModel.Validate(unsafePath),
            "A watched journal path escape must be rejected.");
        var wrongIdentity = document with
        {
            OccurrenceIdentitySha256 = new string('5', 64)
        };
        AssertThrows<InvalidDataException>(() => RoutingWatchedJournalModel.Validate(wrongIdentity),
            "A watched source id must remain bound to its full occurrence identity.");
    }

    private static void AssertFrozenPlanIsStructurallyPinned()
    {
        var document = CreatePreparedDocument(
            CreateSource("c:\\clips", new string('6', 64)));
        var changedPlan = document with
        {
            FrozenPlan = document.FrozenPlan! with { PlanId = Guid.NewGuid() }
        };
        AssertThrows<InvalidDataException>(
            () => RoutingWatchedJournalModel.Validate(changedPlan),
            "A watched journal must reject a plan whose members no longer share its plan id.");

        var excluded = document with
        {
            AdmissionKind = RoutingWatchedJournalAdmissionKind.LegacyKnownExcluded,
            DurationMilliseconds = 0,
            Width = 0,
            Height = 0,
            FrozenPlan = null
        };
        RoutingWatchedJournalModel.Validate(excluded);
        AssertThrows<InvalidDataException>(
            () => RoutingWatchedJournalModel.Validate(excluded with
            {
                FrozenPlan = document.FrozenPlan
            }),
            "A legacy exclusion must never retain provider or file work.");
    }

    private static RoutingWatchedSourceJournalDocument CreatePreparedDocument(
        RoutingWatchedSourceFile source)
    {
        var occurrence = RoutingWatchedJournalFactory.CreateOccurrenceIdentity(
            source,
            new string('A', 64));
        var sourceClipId = RoutingWatchedJournalModel.SourcePrefix + occurrence;
        var fileAction = new RoutingAction(
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
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
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            "Journal fixture fallback",
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
            CreatedUtc: Now,
            ModifiedUtc: Now);
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [route],
            CreatedUtc: Now,
            UpdatedUtc: Now);
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
            DurationMilliseconds: 10_000,
            source.ContentSha256,
            [
                new RoutingClipOutputRevision(
                    original,
                    RoutingOutputAvailability.Ready,
                    FailureCode: null),
                new RoutingClipOutputRevision(
                    RoutingEvaluator.CreateLogicalOutputReference(
                        sourceClipId, source.ContentSha256, RoutingOutputKind.Landscape),
                    RoutingOutputAvailability.PermanentlyMissing,
                    "watched-output-not-produced"),
                new RoutingClipOutputRevision(
                    RoutingEvaluator.CreateLogicalOutputReference(
                        sourceClipId, source.ContentSha256, RoutingOutputKind.Portrait),
                    RoutingOutputAvailability.PermanentlyMissing,
                    "watched-output-not-produced")
            ]);
        var plan = RoutingEvaluator.CreatePlan(
            snapshot,
            facts,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            [],
            Now);
        return RoutingWatchedJournalModel.Create(
            RoutingWatchedJournalAdmissionKind.PreparedPlan,
            occurrence,
            source.RootIdentitySha256,
            new string('A', 64),
            new string('B', 64),
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
    }

    private static RoutingWatchedSourceFile CreateSource(
        string root,
        string rootIdentity) => new(
        ClipCaptureSource.SteelSeriesGg,
        Path.GetFullPath(root),
        "Battlefield 6 2026-08-29 15-00-00.mp4",
        "Battlefield 6",
        "Battlefield 6 2026-08-29 15-00-00.mp4",
        rootIdentity,
        new RoutingWatchedNativeFileIdentity(
            VolumeSerialNumber: 1,
            FileIdHex: "0000000000000001",
            ByteLength: 4096,
            CreationUtcTicks: Now.UtcTicks,
            LastWriteUtcTicks: Now.UtcTicks),
        new string('c', 64));

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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
