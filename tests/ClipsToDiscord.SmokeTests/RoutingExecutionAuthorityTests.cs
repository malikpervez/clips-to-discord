using ClipsToDiscord;

internal static class RoutingExecutionAuthorityTests
{
    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        AssertMissingIsTheOnlyLegacyPermittedState(testRoot);
        await AssertCommitIsStickyPrivateAndIdempotentAsync(testRoot);
        await AssertCancellationCannotStrandAnAmbiguousCommitAsync(testRoot);
        await AssertConcurrentExactCommitsConvergeAsync(testRoot);
        await AssertDifferentAuthorityCannotReplaceCommitAsync(testRoot);
        AssertValidShapeTamperingFailsClosed(testRoot);
        AssertUnsupportedOversizedAndUnexpectedPathsFailClosed(testRoot);
        AssertOnlyCommittedMigrationCanCreateAuthority(testRoot);
        AssertSuccessorsAreForbidden(testRoot);
    }

    private static void AssertMissingIsTheOnlyLegacyPermittedState(string root)
    {
        var store = Store(root, "missing");
        var inspection = store.Inspect();
        Assert(inspection.LegacyPermitted && !inspection.RoutingRequired &&
               !inspection.Blocked && inspection.LoadStatus == RoutingDocumentLoadStatus.Missing &&
               inspection.Document is null,
            "Only a genuinely missing authority document may permit the legacy watcher.");
    }

    private static async Task AssertCommitIsStickyPrivateAndIdempotentAsync(string root)
    {
        var test = Path.Combine(root, "commit");
        var fixture = CreateAuthorityFixture(test, ClipCaptureSource.Nvidia);
        var store = Store(test, "state");
        var first = await store.CommitAsync(fixture.Authority);
        Assert(first.Status == RoutingExecutionAuthorityCommitStatus.Committed &&
               first.Document == fixture.Authority,
            "The first exact authority must commit once.");

        var restarted = new RoutingExecutionAuthorityStore(store.Path);
        var inspection = restarted.Inspect();
        Assert(inspection.RoutingRequired && !inspection.LegacyPermitted &&
               inspection.Document == fixture.Authority &&
               inspection.Document!.RequiredLegacySource == ClipCaptureSource.Nvidia,
            "A restart must recover Routing authority without allowing a legacy scan.");
        var repeated = await restarted.CommitAsync(fixture.Authority);
        Assert(repeated.Status == RoutingExecutionAuthorityCommitStatus.AlreadyCommitted &&
               repeated.Document == fixture.Authority,
            "Retrying the exact activation must be idempotent.");

        var text = File.ReadAllText(store.Path);
        Assert(!text.Contains(fixture.ClipsRoot, StringComparison.OrdinalIgnoreCase) &&
               !text.Contains(fixture.CaptureLibraryRoot, StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("authority-secret-token", StringComparison.Ordinal),
            "Durable execution authority must contain neither watched paths nor connection secrets.");
    }

    private static async Task AssertCancellationCannotStrandAnAmbiguousCommitAsync(string root)
    {
        var test = Path.Combine(root, "non-cancellable-commit");
        var fixture = CreateAuthorityFixture(test, ClipCaptureSource.SteelSeriesGg);
        var store = Store(test, "state");
        using var cancellation = new CancellationTokenSource();
        var result = await store.CommitAsync(
            fixture.Authority,
            cancellation.Token,
            beforeCommit: cancellation.Cancel);
        Assert(cancellation.IsCancellationRequested &&
               result.Status == RoutingExecutionAuthorityCommitStatus.Committed &&
               store.Inspect().RoutingRequired,
            "Cancellation after authority commit starts must not make legacy eligibility ambiguous.");

        var cancelledStore = Store(root, "cancelled-before-entry");
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => cancelledStore.CommitAsync(
            fixture.Authority,
            alreadyCancelled.Token));
        Assert(cancelledStore.Inspect().LegacyPermitted,
            "Cancellation before the commit boundary must leave authority genuinely missing.");
    }

    private static async Task AssertConcurrentExactCommitsConvergeAsync(string root)
    {
        var test = Path.Combine(root, "concurrent");
        var fixture = CreateAuthorityFixture(test, ClipCaptureSource.SteelSeriesGg);
        var path = Path.Combine(test, "state", RoutingExecutionAuthorityStore.FileName);
        using var start = new ManualResetEventSlim(false);
        Task<RoutingExecutionAuthorityCommitResult> CommitAsync() => Task.Run(async () =>
        {
            start.Wait();
            return await new RoutingExecutionAuthorityStore(path).CommitAsync(fixture.Authority);
        });
        var first = CommitAsync();
        var second = CommitAsync();
        start.Set();
        var results = await Task.WhenAll(first, second);
        Assert(results.Count(item =>
                   item.Status == RoutingExecutionAuthorityCommitStatus.Committed) == 1 &&
               results.Count(item =>
                   item.Status == RoutingExecutionAuthorityCommitStatus.AlreadyCommitted) == 1 &&
               new RoutingExecutionAuthorityStore(path).Load().Document == fixture.Authority,
            "Concurrent exact activation attempts must converge on one immutable authority.");
    }

    private static async Task AssertDifferentAuthorityCannotReplaceCommitAsync(string root)
    {
        var test = Path.Combine(root, "conflict");
        var first = CreateAuthorityFixture(Path.Combine(test, "first"),
            ClipCaptureSource.SteelSeriesGg);
        var second = CreateAuthorityFixture(Path.Combine(test, "second"),
            ClipCaptureSource.Nvidia);
        var store = Store(test, "state");
        _ = await store.CommitAsync(first.Authority);
        await AssertThrowsAsync<RoutingExecutionAuthorityConflictException>(() =>
            store.CommitAsync(second.Authority));
        Assert(store.Load().Document == first.Authority,
            "A different source or migration must never replace committed Routing authority.");
    }

    private static void AssertValidShapeTamperingFailsClosed(string root)
    {
        var test = Path.Combine(root, "tamper");
        var fixture = CreateAuthorityFixture(test, ClipCaptureSource.SteelSeriesGg);
        var store = Store(test, "state");
        _ = store.CommitAsync(fixture.Authority).GetAwaiter().GetResult();
        var text = File.ReadAllText(store.Path);
        var replacement = fixture.Authority.SourceFingerprint[0] == 'A' ? 'B' : 'A';
        var changedFingerprint = replacement + fixture.Authority.SourceFingerprint[1..];
        var mutated = text.Replace(
            fixture.Authority.SourceFingerprint,
            changedFingerprint,
            StringComparison.Ordinal);
        Assert(mutated != text, "The authority tamper fixture did not change durable evidence.");
        File.WriteAllText(store.Path, mutated);
        var inspection = store.Inspect();
        Assert(inspection.Blocked && !inspection.LegacyPermitted &&
               inspection.LoadStatus == RoutingDocumentLoadStatus.Invalid,
            "Valid-shape authority tampering must block both runtimes rather than permit Legacy.");
    }

    private static void AssertUnsupportedOversizedAndUnexpectedPathsFailClosed(string root)
    {
        AssertThrows<ArgumentException>(() => _ = new RoutingExecutionAuthorityStore(
                Path.Combine(root, "wrong-authority-name.json")),
            "Authority must use its canonical filename.");

        var unsupportedTest = Path.Combine(root, "unsupported");
        var unsupportedFixture = CreateAuthorityFixture(
            unsupportedTest, ClipCaptureSource.SteelSeriesGg);
        var unsupported = Store(unsupportedTest, "state");
        _ = unsupported.CommitAsync(unsupportedFixture.Authority).GetAwaiter().GetResult();
        var text = File.ReadAllText(unsupported.Path).Replace(
            $"\"schemaVersion\": {RoutingExecutionAuthorityStore.CurrentSchemaVersion}",
            $"\"schemaVersion\": {RoutingExecutionAuthorityStore.CurrentSchemaVersion + 1}",
            StringComparison.Ordinal);
        File.WriteAllText(unsupported.Path, text);
        Assert(unsupported.Inspect() is
               {
                   Blocked: true,
                   LoadStatus: RoutingDocumentLoadStatus.UnsupportedSchema
               },
            "A future authority schema must fail closed.");

        var oversized = Store(root, "oversized");
        Directory.CreateDirectory(Path.GetDirectoryName(oversized.Path)!);
        File.WriteAllBytes(
            oversized.Path,
            new byte[RoutingExecutionAuthorityStore.MaximumDocumentBytes + 1]);
        Assert(oversized.Inspect() is
               {
                   Blocked: true,
                   LoadStatus: RoutingDocumentLoadStatus.Invalid
               },
            "An oversized authority document must fail closed before JSON parsing.");

        var occupied = Store(root, "directory-at-file-path");
        Directory.CreateDirectory(occupied.Path);
        Assert(occupied.Inspect() is
               {
                   Blocked: true,
                   LoadStatus: RoutingDocumentLoadStatus.Invalid
               },
            "A directory occupying the authority path must not masquerade as Missing.");
    }

    private static void AssertOnlyCommittedMigrationCanCreateAuthority(string root)
    {
        var fixture = CreateAuthorityFixture(
            Path.Combine(root, "prepared-rejected"),
            ClipCaptureSource.SteelSeriesGg);
        var prepared = fixture.Marker with
        {
            Generation = 1,
            Phase = LegacyRoutingMigrationMarkerPhase.Prepared,
            UpdatedUtc = fixture.Marker.CreatedUtc
        };
        AssertThrows<InvalidDataException>(() => _ = RoutingExecutionAuthorityModel.Create(
                prepared,
                ClipCaptureSource.SteelSeriesGg),
            "Prepared migration evidence must not create execution authority.");
        AssertThrows<InvalidDataException>(() => _ = RoutingExecutionAuthorityModel.Create(
                fixture.Marker,
                (ClipCaptureSource)999),
            "Unknown watched-source coverage must not create execution authority.");
    }

    private static void AssertSuccessorsAreForbidden(string root)
    {
        var fixture = CreateAuthorityFixture(
            Path.Combine(root, "successor"),
            ClipCaptureSource.SteelSeriesGg);
        AssertThrows<InvalidDataException>(() =>
                RoutingExecutionAuthorityModel.ValidateSuccessor(
                    fixture.Authority,
                    fixture.Authority),
            "Even an identical successor write must be forbidden; exact retries use CommitAsync.");
    }

    private static AuthorityFixture CreateAuthorityFixture(
        string root,
        ClipCaptureSource source)
    {
        var clips = Path.Combine(root, "watched", "private-authority-clips");
        Directory.CreateDirectory(clips);
        var captureLibrary = Directory.CreateDirectory(
            Path.Combine(root, "capture-library", "private-capture-clips")).FullName;
        var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(captureLibrary);
        var settings = new AppSettings(
            clips,
            "https://discord.com/api/webhooks/123456789012345678/authority-secret-token",
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Authority tester",
            UploadToDiscord: false,
            GlobalHotkeyBinding.DefaultDisplayText,
            source);
        var state = new WatchState
        {
            Version = 4,
            ClipsFolder = clips,
            CaptureSource = source,
            KnownContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            UploadedContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            LocalOnlyContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            IgnoredFileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingLocalOnlyMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingEditedUploads = []
        };
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(
            new LegacyRoutingMigrationInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                DiscordConnectionIds: [],
                CaptureLibraryBinding: captureLibraryBinding),
            At(0));
        Assert(readiness.CanCommit && readiness.Plan is not null,
            "The authority fixture requires a valid drained migration.");
        var marker = LegacyRoutingMigrationMarkerModel.Commit(
            LegacyRoutingMigrationMarkerModel.CreatePrepared(readiness.Plan!, At(0)),
            At(1));
        var authority = RoutingExecutionAuthorityModel.Create(marker, source, At(2));
        return new AuthorityFixture(clips, captureLibrary, marker, authority);
    }

    private static RoutingExecutionAuthorityStore Store(string root, string name) => new(
        Path.Combine(root, name, RoutingExecutionAuthorityStore.FileName));

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
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
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}, but the operation completed.");
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

    private sealed record AuthorityFixture(
        string ClipsRoot,
        string CaptureLibraryRoot,
        LegacyRoutingMigrationMarker Marker,
        RoutingExecutionAuthorityDocument Authority);
}
