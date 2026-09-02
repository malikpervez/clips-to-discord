using ClipsToDiscord;

internal static class XboxDvrOccurrenceJournalTests
{
    private const string SourceId = "source.0123456789abcdef0123456789abcdef";
    private const string RootIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OccurrenceId =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RevisionId =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string ReplacementRevisionId =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ContentSha256 =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string OtherContentSha256 =
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string ClipId = "0123456789abcdef0123456789abcdef";
    private const string OtherClipId = "fedcba9876543210fedcba9876543210";
    private const string LeafName = "Battlefield 6-2026_08_31-20-00-00.mp4";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CapturedUtc =
        new(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertSelectionIsDurableBeforeImportAsync(Path.Combine(root, "select-first"));
        await AssertRestartResetsInterruptedImportAsync(Path.Combine(root, "restart"));
        await AssertCompletionIsImmutableAndIdempotentAsync(Path.Combine(root, "completion"));
        await AssertSkippedOccurrenceIsTerminalAndImmutableAsync(
            Path.Combine(root, "skipped"));
        await AssertBlockedRetryReentersImportingIdempotentlyAsync(
            Path.Combine(root, "retry-blocked"));
        await AssertRevisionReplacementNeedsAttentionWithoutContentAsync(
            Path.Combine(root, "replacement"));
        await AssertCompareAndSwapRejectsStaleWritersAsync(Path.Combine(root, "cas"));
        AssertCaptureJournalXboxProvenanceContract();
    }

    private static async Task AssertSelectionIsDurableBeforeImportAsync(string root)
    {
        var fixture = Fixture(root);
        var selected = await SelectAsync(fixture.Journal);
        var loaded = fixture.Store.Load();

        Assert(selected is
               {
                   State: XboxDvrOccurrenceState.Selected,
                   ImportAttempts: 0,
                   ClipId: null,
                   ContentSha256: null,
                   ErrorCode: null
               } &&
               loaded.LoadedFromDisk && loaded.Document is
               {
                   Generation: 2,
                   Occurrences.Count: 1
               } &&
               loaded.Document.Occurrences.Single() == selected,
            "Xbox admission must durably select exact metadata before an import can begin.");
        Assert(!Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories).Any(),
            "Selecting Xbox metadata must not create, open, hydrate, or copy clip content.");

        var importing = await fixture.Journal.BeginImportAsync(OccurrenceId, RevisionId);
        Assert(importing.State == XboxDvrOccurrenceState.Importing &&
               importing.ImportAttempts == 1 &&
               fixture.Store.Load().Document!.Generation == 3,
            "Importing may begin only after the Selected state is durable.");
    }

    private static async Task AssertRestartResetsInterruptedImportAsync(string root)
    {
        var fixture = Fixture(root);
        await SelectAsync(fixture.Journal);
        var firstAttempt = await fixture.Journal.BeginImportAsync(OccurrenceId, RevisionId);

        var restartedStore = new XboxDvrOccurrenceStore(root, SourceId);
        var restarted = new XboxDvrOccurrenceJournal(
            restartedStore,
            SourceId,
            RootIdentity,
            () => Now.AddMinutes(1));
        var cold = restarted.Load();
        Assert(cold.LoadedFromDisk &&
               cold.Document!.Occurrences.Single().State == XboxDvrOccurrenceState.Importing &&
               firstAttempt.ImportAttempts == 1,
            "A cold restart must observe an interrupted Importing state exactly as persisted.");

        var reset = await restarted.ResetInterruptedAsync(OccurrenceId, RevisionId);
        Assert(reset.State == XboxDvrOccurrenceState.Selected &&
               reset.ImportAttempts == 1 &&
               restarted.Load().Document!.Generation == 4,
            "Startup recovery must return Importing to Selected without erasing attempt history.");
        var secondAttempt = await restarted.BeginImportAsync(OccurrenceId, RevisionId);
        Assert(secondAttempt.State == XboxDvrOccurrenceState.Importing &&
               secondAttempt.ImportAttempts == 2,
            "A recovered Xbox occurrence must start a distinguishable second import attempt.");
    }

    private static async Task AssertCompletionIsImmutableAndIdempotentAsync(string root)
    {
        var fixture = Fixture(root);
        await SelectAsync(fixture.Journal);
        await fixture.Journal.BeginImportAsync(OccurrenceId, RevisionId);
        var imported = await fixture.Journal.CompleteAsync(
            OccurrenceId,
            RevisionId,
            ClipId,
            ContentSha256.ToUpperInvariant());
        var completedGeneration = fixture.Store.Load().Document!.Generation;

        Assert(imported is
               {
                   State: XboxDvrOccurrenceState.Imported,
                   ClipId: ClipId,
                   ContentSha256: ContentSha256,
                   ErrorCode: null
               },
            "Completion must bind one Xbox revision to canonical immutable Library evidence.");

        var repeated = await fixture.Journal.CompleteAsync(
            OccurrenceId,
            RevisionId,
            ClipId,
            ContentSha256);
        Assert(repeated == imported &&
               fixture.Store.Load().Document!.Generation == completedGeneration,
            "Repeating the exact completion must be idempotent and must not write a generation.");
        await AssertThrowsAsync<InvalidDataException>(() => fixture.Journal.CompleteAsync(
                OccurrenceId,
                RevisionId,
                OtherClipId,
                ContentSha256),
            "An imported occurrence must not be rebound to another Library clip.");
        await AssertThrowsAsync<InvalidDataException>(() => fixture.Journal.CompleteAsync(
                OccurrenceId,
                RevisionId,
                ClipId,
                OtherContentSha256),
            "An imported occurrence must not accept different content evidence.");
        await AssertThrowsAsync<InvalidOperationException>(() =>
                fixture.Journal.MarkNeedsAttentionAsync(
                    OccurrenceId, RevisionId, "late-failure"),
            "An imported occurrence must never transition back to an uncommitted state.");

        var current = fixture.Store.Load().Document!;
        var changedImported = current.Occurrences.Single() with
        {
            ContentSha256 = OtherContentSha256,
            UpdatedUtc = Now.AddMinutes(2)
        };
        var invalidSuccessor = current with
        {
            Generation = current.Generation + 1,
            Occurrences = [changedImported],
            UpdatedUtc = Now.AddMinutes(2)
        };
        AssertThrows<InvalidDataException>(() =>
                XboxDvrOccurrenceModel.ValidateSuccessor(current, invalidSuccessor),
            "The successor model must independently pin imported completion evidence.");
    }

    private static async Task AssertRevisionReplacementNeedsAttentionWithoutContentAsync(
        string root)
    {
        var fixture = Fixture(root);
        var selected = await SelectAsync(fixture.Journal);
        var replaced = await fixture.Journal.SelectAsync(
            OccurrenceId,
            ReplacementRevisionId,
            "Changed-2026_09_01-01-00-00.mp4",
            "Changed game",
            CapturedUtc.AddHours(5),
            logicalBytes: 999_999,
            lastWriteUtcTicks: Now.UtcDateTime.Ticks + 99);

        Assert(replaced.State == XboxDvrOccurrenceState.NeedsAttention &&
               replaced.ErrorCode == "source-replaced" &&
               replaced.RevisionId == selected.RevisionId &&
               replaced.PortableRelativePath == selected.PortableRelativePath &&
               replaced.GameName == selected.GameName &&
               replaced.CapturedUtc == selected.CapturedUtc &&
               replaced.LogicalBytes == selected.LogicalBytes &&
               replaced.LastWriteUtcTicks == selected.LastWriteUtcTicks &&
               replaced.ClipId is null && replaced.ContentSha256 is null,
            "A replacement revision must preserve admitted metadata and move to NeedsAttention.");
        Assert(!Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories).Any(),
            "Revision replacement detection must use durable metadata only and never open content.");
        await AssertThrowsAsync<InvalidDataException>(() =>
                fixture.Journal.BeginImportAsync(OccurrenceId, ReplacementRevisionId),
            "A changed revision must not be imported under stale admission evidence.");
    }

    private static async Task AssertSkippedOccurrenceIsTerminalAndImmutableAsync(string root)
    {
        var fixture = Fixture(root);
        _ = await SelectAsync(fixture.Journal);
        _ = await fixture.Journal.MarkNeedsAttentionAsync(
            OccurrenceId, RevisionId, "invalid-media");
        var skipped = await fixture.Journal.SkipBlockedAsync(
            OccurrenceId, RevisionId);
        Assert(skipped is
               {
                   State: XboxDvrOccurrenceState.Skipped,
                   ErrorCode: "skipped-invalid-media",
                   ClipId: null,
                   ContentSha256: null
               },
            "Explicitly skipped media must retain terminal audit evidence without Library ownership.");

        var current = fixture.Store.Load().Document!;
        var mutations = new (string Description, Func<XboxDvrOccurrenceEntry,
            XboxDvrOccurrenceEntry> Mutate)[]
        {
            ("error", entry => entry with { ErrorCode = "skipped-library-conflict" }),
            ("attempt", entry => entry with { ImportAttempts = entry.ImportAttempts + 1 }),
            ("timestamp", entry => entry with { UpdatedUtc = entry.UpdatedUtc.AddTicks(1) }),
            ("revision", entry => entry with { RevisionId = ReplacementRevisionId }),
            ("path", entry => entry with
            {
                PortableRelativePath = "Changed-2026_09_01-01-00-00.mp4"
            })
        };
        foreach (var mutation in mutations)
        {
            var changed = mutation.Mutate(current.Occurrences.Single());
            var invalid = current with
            {
                Generation = current.Generation + 1,
                Occurrences = [changed],
                UpdatedUtc = current.UpdatedUtc.AddTicks(1)
            };
            AssertThrows<InvalidDataException>(() =>
                    XboxDvrOccurrenceModel.ValidateSuccessor(current, invalid),
                $"Skipped occurrence {mutation.Description} evidence must be immutable at the store boundary.");
        }
    }

    private static async Task AssertBlockedRetryReentersImportingIdempotentlyAsync(
        string root)
    {
        var fixture = Fixture(root);
        _ = await SelectAsync(fixture.Journal);
        var firstAttempt = await fixture.Journal.BeginImportAsync(
            OccurrenceId, RevisionId);
        _ = await fixture.Journal.MarkNeedsAttentionAsync(
            OccurrenceId, RevisionId, "library-conflict");

        var retried = await fixture.Journal.RetryBlockedAsync(
            OccurrenceId, RevisionId);
        var retryGeneration = fixture.Store.Load().Document!.Generation;
        Assert(retried is
               {
                   State: XboxDvrOccurrenceState.Importing,
                   ImportAttempts: 1,
                   ErrorCode: null,
                   ClipId: null,
                   ContentSha256: null
               } &&
               retried.ImportAttempts == firstAttempt.ImportAttempts,
            "Retrying a blocked Xbox clip must re-enter Importing without pretending that a second content import has started.");

        var repeated = await fixture.Journal.RetryBlockedAsync(
            OccurrenceId, RevisionId);
        Assert(repeated == retried &&
               fixture.Store.Load().Document!.Generation == retryGeneration,
            "Repeating a durable blocked-clip retry must be idempotent and must not write another generation.");

        _ = await fixture.Journal.MarkNeedsAttentionAsync(
            OccurrenceId, RevisionId, "source-replaced");
        var unsupportedGeneration = fixture.Store.Load().Document!.Generation;
        await AssertThrowsAsync<InvalidOperationException>(() =>
                fixture.Journal.RetryBlockedAsync(OccurrenceId, RevisionId),
            "Retry must not weaken a source-revision blocker into content work.");
        Assert(fixture.Store.Load().Document is { } unchanged &&
               unchanged.Generation == unsupportedGeneration &&
               unchanged.Occurrences.Single() is
               {
                   State: XboxDvrOccurrenceState.NeedsAttention,
                   ErrorCode: "source-replaced",
                   ImportAttempts: 1
               },
            "An unsupported retry request must leave the occurrence journal byte-for-state unchanged.");
        var current = fixture.Store.Load().Document!;
        var forgedRetry = current with
        {
            Generation = current.Generation + 1,
            Occurrences =
            [
                current.Occurrences.Single() with
                {
                    State = XboxDvrOccurrenceState.Importing,
                    ErrorCode = null,
                    UpdatedUtc = Now.AddMinutes(2)
                }
            ],
            UpdatedUtc = Now.AddMinutes(2)
        };
        AssertThrows<InvalidDataException>(() =>
                XboxDvrOccurrenceModel.ValidateSuccessor(current, forgedRetry),
            "The store boundary must independently reject retrying a source-revision blocker.");
        await AssertThrowsAsync<InvalidDataException>(() =>
                fixture.Journal.RetryBlockedAsync(
                    OccurrenceId, ReplacementRevisionId),
            "Retry must remain bound to the exact frozen Xbox source revision.");
        Assert(!Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories).Any(),
            "Blocked-clip retry state transitions must never create, open, copy, or change source content.");
    }

    private static async Task AssertCompareAndSwapRejectsStaleWritersAsync(string root)
    {
        var store = new XboxDvrOccurrenceStore(root, SourceId);
        var initial = await store.LoadOrCreateAsync(SourceId, RootIdentity, Now);
        var first = Entry(
            OccurrenceId,
            RevisionId,
            LeafName,
            "Battlefield 6",
            Now.AddSeconds(1));
        var second = Entry(
            OtherContentSha256,
            ReplacementRevisionId,
            "Another game-2026_08_31-21-00-00.mp4",
            "Another game",
            Now.AddSeconds(1));
        var firstCandidate = initial with
        {
            Generation = initial.Generation + 1,
            Occurrences = [first],
            UpdatedUtc = Now.AddSeconds(1)
        };
        var staleCandidate = initial with
        {
            Generation = initial.Generation + 1,
            Occurrences = [second],
            UpdatedUtc = Now.AddSeconds(1)
        };
        XboxDvrOccurrenceModel.ValidateSuccessor(initial, firstCandidate);
        XboxDvrOccurrenceModel.ValidateSuccessor(initial, staleCandidate);

        await store.SaveAsync(firstCandidate, initial.Generation);
        await AssertThrowsAsync<RoutingConcurrencyException>(() =>
                store.SaveAsync(staleCandidate, initial.Generation),
            "A stale Xbox occurrence writer must lose the generation CAS.");
        var loaded = store.Load();
        Assert(loaded.LoadedFromDisk &&
               loaded.Document!.Occurrences.Select(item => item.OccurrenceId)
                   .SequenceEqual([OccurrenceId]) &&
               !File.Exists(store.Path + ".tmp"),
            "A stale CAS failure must preserve the winning document without temporary residue.");

        var concurrentRoot = Path.Combine(root, "journal-retry");
        var firstJournal = Fixture(concurrentRoot);
        var secondJournal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(concurrentRoot, SourceId),
            SourceId,
            RootIdentity,
            () => Now.AddMinutes(1));
        await Task.WhenAll(
            SelectAsync(firstJournal.Journal),
            secondJournal.SelectAsync(
                OtherContentSha256,
                ReplacementRevisionId,
                "Another game-2026_08_31-21-00-00.mp4",
                "Another game",
                CapturedUtc.AddHours(1),
                logicalBytes: 2_000,
                lastWriteUtcTicks: Now.UtcDateTime.Ticks + 1));
        var converged = firstJournal.Store.Load();
        Assert(converged.LoadedFromDisk &&
               converged.Document!.Occurrences.Count == 2 &&
               converged.Document.Occurrences.Select(item => item.OccurrenceId)
                   .ToHashSet(StringComparer.Ordinal)
                   .SetEquals([OccurrenceId, OtherContentSha256]),
            "Journal-level CAS retries must retain two concurrently selected occurrences.");
    }

    private static void AssertCaptureJournalXboxProvenanceContract()
    {
        var uppercaseOccurrence = OccurrenceId.ToUpperInvariant();
        var xbox = CaptureJournalModel.CreateOriginalCommitted(
            Clip(
                CaptureJournalSourceKind.XboxGameDvr,
                SourceId,
                uppercaseOccurrence),
            Now);
        Assert(xbox.State == CaptureJournalState.OriginalCommitted &&
               xbox.Clip.SourceKind == CaptureJournalSourceKind.XboxGameDvr &&
               xbox.Clip.SourceConnectionId == SourceId &&
               xbox.Clip.SourceOccurrenceId == OccurrenceId &&
               xbox.Clip.SourceRevisionId == RevisionId &&
               !xbox.Clip.ReactionCameraRequested &&
               xbox.Clip.RequestedRenditions.Count == 0,
            "CaptureJournalModel must accept and normalize complete Xbox source provenance.");

        AssertThrows<InvalidDataException>(() => CaptureJournalModel.CreateOriginalCommitted(
                Clip(CaptureJournalSourceKind.XboxGameDvr, null, OccurrenceId), Now),
            "Xbox capture metadata without a source connection must fail closed.");
        AssertThrows<InvalidDataException>(() => CaptureJournalModel.CreateOriginalCommitted(
                Clip(CaptureJournalSourceKind.XboxGameDvr, SourceId, null), Now),
            "Xbox capture metadata without an occurrence id must fail closed.");
        AssertThrows<InvalidDataException>(() => CaptureJournalModel.CreateOriginalCommitted(
                Clip(CaptureJournalSourceKind.XboxGameDvr, SourceId, OccurrenceId) with
                {
                    SourceRevisionId = null
                }, Now),
            "Xbox capture metadata without a revision id must fail closed.");
        AssertThrows<InvalidDataException>(() => CaptureJournalModel.CreateOriginalCommitted(
                Clip(CaptureJournalSourceKind.XboxGameDvr, "C:\\Xbox Game DVR", OccurrenceId), Now),
            "A path must never be accepted as Xbox source provenance.");
        AssertThrows<InvalidDataException>(() => CaptureJournalModel.CreateOriginalCommitted(
                Clip(CaptureJournalSourceKind.XboxGameDvr, SourceId, "not-a-sha256"), Now),
            "Malformed Xbox occurrence provenance must fail closed.");

        foreach (var sourceKind in new[]
                 {
                     CaptureJournalSourceKind.ManualCapture,
                     CaptureJournalSourceKind.InstantReplay
                 })
        {
            var ordinary = CaptureJournalModel.CreateOriginalCommitted(
                Clip(sourceKind, null, null), Now);
            Assert(ordinary.Clip.SourceConnectionId is null &&
                   ordinary.Clip.SourceOccurrenceId is null &&
                   ordinary.Clip.SourceRevisionId is null,
                "Existing ClipCord capture metadata must remain valid without provenance.");
            AssertThrows<InvalidDataException>(() =>
                    CaptureJournalModel.CreateOriginalCommitted(
                        Clip(sourceKind, SourceId, OccurrenceId), Now),
                "Manual and Replay captures must reject external Xbox provenance.");
        }

        Assert(!CaptureJournalModel.ClipsEqual(
                xbox.Clip,
                xbox.Clip with { SourceOccurrenceId = OtherContentSha256 }),
            "Xbox provenance must participate in immutable capture-journal identity.");
    }

    private static FixtureState Fixture(string root)
    {
        var store = new XboxDvrOccurrenceStore(root, SourceId);
        return new FixtureState(
            store,
            new XboxDvrOccurrenceJournal(
                store,
                SourceId,
                RootIdentity,
                () => Now));
    }

    private static Task<XboxDvrOccurrenceEntry> SelectAsync(
        XboxDvrOccurrenceJournal journal) => journal.SelectAsync(
        OccurrenceId,
        RevisionId,
        LeafName,
        "Battlefield 6",
        CapturedUtc,
        logicalBytes: 50_000_000,
        lastWriteUtcTicks: Now.UtcDateTime.Ticks);

    private static XboxDvrOccurrenceEntry Entry(
        string occurrenceId,
        string revisionId,
        string leaf,
        string game,
        DateTimeOffset timestamp) => new(
        occurrenceId,
        revisionId,
        leaf,
        game,
        CapturedUtc,
        LogicalBytes: 1_000,
        LastWriteUtcTicks: Now.UtcDateTime.Ticks,
        XboxDvrOccurrenceState.Selected,
        ImportAttempts: 0,
        ClipId: null,
        ContentSha256: null,
        ErrorCode: null,
        timestamp,
        timestamp);

    private static CaptureJournalClipMetadata Clip(
        CaptureJournalSourceKind sourceKind,
        string? sourceConnectionId,
        string? sourceOccurrenceId) => new(
        ClipId,
        sourceKind,
        "Battlefield 6",
        CapturedUtc,
        DurationTicks: TimeSpan.FromSeconds(30).Ticks,
        Width: 1920,
        Height: 1080,
        ReactionCameraRequested: false,
        RequestedRenditions: [],
        new CaptureJournalArtifact(
            $"{ClipId}:original",
            "original",
            "Library/Game/Battlefield 6/clip.mp4",
            new CaptureJournalFingerprint(1_000, ContentSha256)),
        sourceConnectionId,
        sourceOccurrenceId,
        sourceKind == CaptureJournalSourceKind.XboxGameDvr ? RevisionId : null);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
        XboxDvrOccurrenceStore Store,
        XboxDvrOccurrenceJournal Journal);
}
