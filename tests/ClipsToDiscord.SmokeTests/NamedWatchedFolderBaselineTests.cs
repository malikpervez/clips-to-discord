namespace ClipsToDiscord;

internal static class NamedWatchedFolderBaselineTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertFromNowBoundaryAsync(
            Path.Combine(root, "steelseries-arbitrary-drive"),
            RoutingInputSourceKind.SteelSeriesGg,
            "source.81000000000000000000000000000001");
        await AssertFromNowBoundaryAsync(
            Path.Combine(root, "nvidia-arbitrary-drive"),
            RoutingInputSourceKind.Nvidia,
            "source.82000000000000000000000000000002");
        await AssertMissingCorruptAndMismatchedStateFailsClosedAsync(
            Path.Combine(root, "untrusted-state"));
        await AssertEmptyArbitraryRootWorksAsync(
            Path.Combine(root, "empty-arbitrary-drive"));
    }

    private static async Task AssertFromNowBoundaryAsync(
        string root,
        RoutingInputSourceKind kind,
        string sourceId)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(
            root,
            "Drive-X",
            "Recorder clips can live anywhere"));
        Directory.CreateDirectory(sourceRoot);
        var adapter = Adapter(kind);
        var existingPath = CreateCandidate(
            sourceRoot,
            kind,
            "Existing Game",
            "Existing clip.mp4",
            [1, 2, 3, 4]);
        var source = Source(sourceId, kind, sourceRoot);
        var store = new RoutingNamedWatchedBaselineStore(
            Path.Combine(root, "baselines"));

        var baseline = await store.EnsureAsync(source, adapter);
        var existing = await adapter.InspectOccurrenceAsync(sourceRoot, existingPath);
        Assert(baseline is { Generation: 1, IgnoredOccurrenceIds.Count: 1 } &&
               store.IsIgnored(baseline, source, existing),
            $"Every {kind} clip present at registration must be excluded by the immutable from-now baseline.");

        var newPath = CreateCandidate(
            sourceRoot,
            kind,
            "New Game",
            "New clip.mp4",
            [5, 6, 7, 8]);
        var afterRegistration = await adapter.InspectOccurrenceAsync(sourceRoot, newPath);
        var loadedAgain = await store.EnsureAsync(source, adapter);
        Assert(loadedAgain.SourceId == baseline.SourceId &&
               loadedAgain.RootIdentitySha256 == baseline.RootIdentitySha256 &&
               loadedAgain.IgnoredOccurrenceIds.SequenceEqual(
                   baseline.IgnoredOccurrenceIds, StringComparer.Ordinal) &&
               loadedAgain.IgnoredOccurrenceIds.Count == 1 &&
               !store.IsIgnored(loadedAgain, source, afterRegistration),
            $"A {kind} clip created after registration must remain eligible and must not expand the immutable baseline.");
    }

    private static async Task AssertMissingCorruptAndMismatchedStateFailsClosedAsync(
        string root)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Recorder"));
        Directory.CreateDirectory(sourceRoot);
        var source = Source(
            "source.83000000000000000000000000000003",
            RoutingInputSourceKind.SteelSeriesGg,
            sourceRoot);

        var missingStore = new RoutingNamedWatchedBaselineStore(
            Path.Combine(root, "missing"));
        var missing = missingStore.Load(source.SourceId);
        Assert(missing.Status == RoutingDocumentLoadStatus.Missing &&
               !missing.LoadedFromDisk && missing.Document is null,
            "A missing named-source baseline must never be represented as trusted state.");

        var corruptRoot = Path.Combine(root, "corrupt");
        var corruptPath = BaselinePath(corruptRoot, source.SourceId);
        Directory.CreateDirectory(corruptRoot);
        const string corruptPayload = "{ definitely not a trusted baseline";
        await File.WriteAllTextAsync(corruptPath, corruptPayload);
        var corruptStore = new RoutingNamedWatchedBaselineStore(corruptRoot);
        var corrupt = corruptStore.Load(source.SourceId);
        Assert(corrupt.Status == RoutingDocumentLoadStatus.Corrupt &&
               !corrupt.LoadedFromDisk && corrupt.Document is null,
            "A corrupt named-source baseline must fail closed during load.");
        await AssertThrowsAsync<InvalidDataException>(
            () => corruptStore.EnsureAsync(
                source,
                Adapter(RoutingInputSourceKind.SteelSeriesGg)),
            "Ensure must not silently replace a corrupt named-source baseline.");
        Assert(await File.ReadAllTextAsync(corruptPath) == corruptPayload,
            "A rejected corrupt baseline must remain untouched for recovery and diagnosis.");

        var changedAuthority = source with
        {
            Revision = source.Revision + 1,
            RootIdentitySha256 =
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            UpdatedUtc = Now.AddMinutes(1)
        };
        var initialMismatchStore = new RoutingNamedWatchedBaselineStore(
            Path.Combine(root, "initial-mismatch"));
        await AssertThrowsAsync<InvalidDataException>(
            () => initialMismatchStore.EnsureAsync(
                changedAuthority,
                Adapter(RoutingInputSourceKind.SteelSeriesGg)),
            "Even an empty folder must not establish a baseline under a mismatched native root authority.");
        Assert(initialMismatchStore.Load(source.SourceId).Status ==
               RoutingDocumentLoadStatus.Missing,
            "Rejecting a mismatched empty root must not leave a trusted baseline behind.");

        var mismatchStore = new RoutingNamedWatchedBaselineStore(
            Path.Combine(root, "mismatch"));
        var adapter = Adapter(RoutingInputSourceKind.SteelSeriesGg);
        var trusted = await mismatchStore.EnsureAsync(source, adapter);
        await AssertThrowsAsync<InvalidDataException>(
            () => mismatchStore.EnsureAsync(changedAuthority, adapter),
            "An existing baseline must not be inherited by a changed native root authority.");
        var stillTrusted = mismatchStore.Load(source.SourceId).Document;
        Assert(stillTrusted is not null &&
               stillTrusted.SourceId == trusted.SourceId &&
               stillTrusted.RootIdentitySha256 == trusted.RootIdentitySha256 &&
               stillTrusted.IgnoredOccurrenceIds.SequenceEqual(
                   trusted.IgnoredOccurrenceIds, StringComparer.Ordinal),
            "Authority mismatch rejection must not mutate the trusted immutable baseline.");
    }

    private static async Task AssertEmptyArbitraryRootWorksAsync(string root)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(
            root,
            "Drive-Q",
            "Users may choose a different disk",
            "NVIDIA"));
        Directory.CreateDirectory(sourceRoot);
        var source = Source(
            "source.84000000000000000000000000000004",
            RoutingInputSourceKind.Nvidia,
            sourceRoot);
        var store = new RoutingNamedWatchedBaselineStore(
            Path.Combine(root, "baselines"));

        var baseline = await store.EnsureAsync(
            source,
            Adapter(RoutingInputSourceKind.Nvidia));
        Assert(baseline.IgnoredOccurrenceIds.Count == 0 &&
               baseline.RootIdentitySha256 == source.RootIdentitySha256 &&
               baseline.Kind == RoutingInputSourceKind.Nvidia,
            "An empty recorder folder on an arbitrary absolute path must establish a valid zero-item baseline.");
    }

    private static RoutingInputSourceRecord Source(
        string sourceId,
        RoutingInputSourceKind kind,
        string root) => new(
            sourceId,
            Revision: 1,
            kind == RoutingInputSourceKind.SteelSeriesGg
                ? "SteelSeries GG"
                : "NVIDIA",
            kind,
            Path.GetFullPath(root),
            Enabled: false,
            Retired: false,
            RoutingInputSourceHealth.Ready,
            RoutingInputSourceAttentionReason.None,
            RoutingWatchedSourceRootIdentity.Create(kind, root),
            TimeZoneInfo.Utc.Id,
            Now,
            Now);

    private static IRoutingWatchedSourceAdapter Adapter(RoutingInputSourceKind kind) =>
        RoutingWatchedSourceAdapters.Get(kind switch
        {
            RoutingInputSourceKind.SteelSeriesGg => ClipCaptureSource.SteelSeriesGg,
            RoutingInputSourceKind.Nvidia => ClipCaptureSource.Nvidia,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

    private static string CreateCandidate(
        string root,
        RoutingInputSourceKind kind,
        string game,
        string fileName,
        byte[] content)
    {
        var folder = kind == RoutingInputSourceKind.Nvidia
            ? Path.Combine(root, game)
            : root;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string BaselinePath(string root, string sourceId) => Path.Combine(
        Path.GetFullPath(root),
        $"{sourceId[RoutingInputSourceCatalogModel.SourceIdPrefix.Length..]}.json");

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
}
