using ClipsToDiscord;

internal static class XboxDvrSourceAdapterTests
{
    private static readonly DateTimeOffset Activation =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    internal static void Run()
    {
        AssertCurrentFileNameConventionParsesFromTheRight();
        AssertLegacyAndMalformedNamesNeedAttention();
        AssertCloudReparseFamilyIsNarrowlyAccepted();
        AssertHistoryPoliciesFreezeAbsoluteCutoffs();
        AssertPreflightIsMetadataOnlyAndBounded();
        AssertStableIdentitiesUseSourceAndMetadata();
        AssertPreflightFailsClosedOnDuplicateLeaves();
    }

    private static void AssertCurrentFileNameConventionParsesFromTheRight()
    {
        var battlefield = XboxDvrFileNameParser.Parse(
            "Battlefield™ 6-2026_09_01-02-35-02.mp4");
        var hyphenated = XboxDvrFileNameParser.Parse(
            "Tom-Clancy's-Rainbow-Six®-2025_05_23-03-40-36.MP4");

        Assert(battlefield.IsParsed &&
               battlefield.GameName == "Battlefield™ 6" &&
               battlefield.CapturedUtc ==
                   new DateTimeOffset(2026, 9, 1, 2, 35, 2, TimeSpan.Zero) &&
               battlefield.CapturedUtc.Value.Offset == TimeSpan.Zero &&
               battlefield.NeedsAttentionReason == XboxDvrNeedsAttentionReason.None,
            "The modern Xbox leaf must parse its fixed right-hand timestamp as UTC without losing the game name.");
        Assert(hyphenated.IsParsed &&
               hyphenated.GameName == "Tom-Clancy's-Rainbow-Six®" &&
               hyphenated.CapturedUtc ==
                   new DateTimeOffset(2025, 5, 23, 3, 40, 36, TimeSpan.Zero),
            "Hyphens and Unicode marks in the game name must not steer timestamp parsing.");
    }

    private static void AssertLegacyAndMalformedNamesNeedAttention()
    {
        var legacyTime = XboxDvrFileNameParser.Parse(
            "Tom Clancy's Rainbow Six Siege-2025_05_22-04_27_39.mp4");
        var legacyNoGame = XboxDvrFileNameParser.Parse("11-28-2016_7-41-47_PM.mp4");
        var invalidCalendar = XboxDvrFileNameParser.Parse(
            "Game-2026_13_40-25-70-90.mp4");
        var nested = XboxDvrFileNameParser.Parse(
            "nested/Game-2026_09_01-02-35-02.mp4");

        Assert(!legacyTime.IsParsed &&
               legacyTime.NeedsAttentionReason ==
                   XboxDvrNeedsAttentionReason.UnsupportedFileName &&
               !legacyNoGame.IsParsed &&
               legacyNoGame.NeedsAttentionReason ==
                   XboxDvrNeedsAttentionReason.UnsupportedFileName,
            "Legacy Xbox filename shapes must be review-only rather than guessed.");
        Assert(!invalidCalendar.IsParsed &&
               invalidCalendar.NeedsAttentionReason ==
                   XboxDvrNeedsAttentionReason.InvalidTimestamp,
            "A modern-shaped but impossible timestamp must be identified precisely.");
        Assert(!nested.IsParsed &&
               nested.NeedsAttentionReason ==
                   XboxDvrNeedsAttentionReason.UnsupportedFileName,
            "The parser must accept only a leaf, never a path supplied as a filename.");
    }

    private static void AssertCloudReparseFamilyIsNarrowlyAccepted()
    {
        const uint reparse = XboxDvrCloudReparsePolicy.FileAttributeReparsePoint;
        Assert(XboxDvrCloudReparsePolicy.Classify(0x20, 0) ==
                   XboxDvrReparseClassification.Ordinary,
            "An ordinary non-directory file must remain valid.");
        Assert(XboxDvrCloudReparsePolicy.Classify(reparse | 0x20, 0x9000001a) ==
                   XboxDvrReparseClassification.CloudPlaceholder &&
               XboxDvrCloudReparsePolicy.Classify(reparse | 0x20, 0x9000401a) ==
                   XboxDvrReparseClassification.CloudPlaceholder &&
               XboxDvrCloudReparsePolicy.Classify(reparse | 0x20, 0x9000601a) ==
                   XboxDvrReparseClassification.CloudPlaceholder &&
               XboxDvrCloudReparsePolicy.Classify(reparse | 0x20, 0x9000f01a) ==
                   XboxDvrReparseClassification.CloudPlaceholder,
            "The Cloud Files base tag and CLOUD_1 through CLOUD_F state variants must be accepted.");

        Assert(XboxDvrCloudReparsePolicy.Classify(reparse, 0xa000000c) ==
                   XboxDvrReparseClassification.Rejected && // symlink
               XboxDvrCloudReparsePolicy.Classify(reparse, 0xa0000003) ==
                   XboxDvrReparseClassification.Rejected && // mount point / junction
               XboxDvrCloudReparsePolicy.Classify(reparse, 0x8000001b) ==
                   XboxDvrReparseClassification.Rejected &&
               XboxDvrCloudReparsePolicy.Classify(0x20, 0x9000401a) ==
                   XboxDvrReparseClassification.Rejected &&
               XboxDvrCloudReparsePolicy.Classify(
                   reparse | XboxDvrCloudReparsePolicy.FileAttributeDirectory,
                   0x9000401a) == XboxDvrReparseClassification.Rejected,
            "Symlinks, junctions, name-surrogate/unknown tags, inconsistent tags, and directories must fail closed.");
    }

    private static void AssertHistoryPoliciesFreezeAbsoluteCutoffs()
    {
        var newOnly = XboxDvrHistoryPolicy.Create(
            XboxDvrHistoryWindow.NewOnly, Activation, 10);
        var day = XboxDvrHistoryPolicy.Create(
            XboxDvrHistoryWindow.Last24Hours, Activation, 10);
        var week = XboxDvrHistoryPolicy.Create(
            XboxDvrHistoryWindow.Last7Days, Activation, 10);
        var customCutoff = Activation.AddDays(-3);
        var custom = XboxDvrHistoryPolicy.Create(
            XboxDvrHistoryWindow.Custom, Activation, 10, customCutoff);

        Assert(newOnly.HistoricalCutoffUtc == Activation &&
               day.HistoricalCutoffUtc == Activation.AddHours(-24) &&
               week.HistoricalCutoffUtc == Activation.AddDays(-7) &&
               custom.HistoricalCutoffUtc == customCutoff,
            "Every Xbox history label must freeze to one absolute activation-time cutoff.");
        AssertThrows<ArgumentException>(() => XboxDvrHistoryPolicy.Create(
            XboxDvrHistoryWindow.Last24Hours,
            Activation.ToOffset(TimeSpan.FromHours(-4)),
            10));
        AssertThrows<ArgumentException>(() => XboxDvrHistoryPolicy.Create(
            XboxDvrHistoryWindow.Custom, Activation, 10));
        AssertThrows<ArgumentOutOfRangeException>(() => XboxDvrHistoryPolicy.Create(
            XboxDvrHistoryWindow.Custom, Activation, 10, Activation.AddSeconds(1)));
        AssertThrows<InvalidDataException>(() =>
            (day with { HistoricalCutoffUtc = Activation.AddHours(-23) }).Validate());
    }

    private static void AssertPreflightIsMetadataOnlyAndBounded()
    {
        var metadata = new[]
        {
            Candidate("Newest-2026_09_01-11-30-00.mp4", 100, 1),
            Candidate("Second-2026_09_01-11-00-00.mp4", 200, 2, 0x9000401a),
            Candidate("Yesterday-2026_08_31-13-00-00.mp4", 300, 3),
            Candidate("Week-2026_08_27-12-00-00.mp4", 400, 4),
            Candidate("Old-2026_08_20-12-00-00.mp4", 500, 5),
            Candidate("Future-2026_09_01-12-00-01.mp4", 600, 6),
            Candidate("Legacy-2026_08_31-11_00_00.mp4", 700, 7),
            Candidate("Unsafe-2026_09_01-10-00-00.mp4", 800, 8, 0xa000000c)
        };
        var fileSystem = new RecordingMetadataFileSystem(metadata);
        var adapter = new XboxDvrSourceAdapter(fileSystem);
        var root = Path.Combine(Path.GetTempPath(), "clipcord-xbox-preflight");

        var day = adapter.Preflight(
            "xbox-source-1",
            root,
            XboxDvrHistoryPolicy.Create(
                XboxDvrHistoryWindow.Last24Hours,
                Activation,
                maximumHistoricalClips: 2));

        Assert(fileSystem.EnumerationCalls == 1 &&
               fileSystem.ContentOpenCalls == 0 &&
               fileSystem.HydrationCalls == 0,
            "Xbox preflight must use only the metadata seam and perform zero content opens or hydrations.");
        Assert(day.TotalClipCount == 8 && day.TotalLogicalBytes == 3_600 &&
               day.ParsedClipCount == 7 && day.WindowMatchCount == 3 &&
               day.EligibleHistoricalCount == 2 &&
               day.EligibleHistoricalBytes == 300 &&
               day.BaselineOnlyCount == 3 && day.NeedsAttentionCount == 3 &&
               day.EligibleHistorical.Select(item => item.GameName)
                   .SequenceEqual(new[] { "Second", "Newest" }),
            "The 24-hour preview must select only the newest bounded history and report exact counts/bytes.");
        Assert(day.Items.Single(item => item.DisplayFileName.StartsWith("Future"))
                       .NeedsAttentionReason ==
                   XboxDvrNeedsAttentionReason.CaptureTimeAfterActivation &&
               day.Items.Single(item => item.DisplayFileName.StartsWith("Legacy"))
                       .NeedsAttentionReason ==
                   XboxDvrNeedsAttentionReason.UnsupportedFileName &&
               day.Items.Single(item => item.DisplayFileName.StartsWith("Unsafe"))
                       .NeedsAttentionReason ==
                   XboxDvrNeedsAttentionReason.UnsafeReparsePoint,
            "Future, legacy, and unsafe-reparse candidates must never enter the automatic history set.");

        var newOnly = adapter.Preflight(
            "xbox-source-1",
            root,
            XboxDvrHistoryPolicy.Create(XboxDvrHistoryWindow.NewOnly, Activation, 10));
        var week = adapter.Preflight(
            "xbox-source-1",
            root,
            XboxDvrHistoryPolicy.Create(XboxDvrHistoryWindow.Last7Days, Activation, 10));
        var custom = adapter.Preflight(
            "xbox-source-1",
            root,
            XboxDvrHistoryPolicy.Create(
                XboxDvrHistoryWindow.Custom,
                Activation,
                10,
                Activation.AddDays(-2)));
        Assert(newOnly.WindowMatchCount == 0 && newOnly.EligibleHistoricalCount == 0 &&
               week.WindowMatchCount == 4 && week.EligibleHistoricalCount == 4 &&
               custom.WindowMatchCount == 3 && custom.EligibleHistoricalCount == 3,
            "New-only, seven-day, and custom policies must use their frozen cutoff semantics.");
    }

    private static void AssertStableIdentitiesUseSourceAndMetadata()
    {
        var original = Candidate(
            "Game-2026_09_01-10-00-00.mp4", 100, 10, 0x9000401a);
        var parsed = XboxDvrFileNameParser.Parse(original.PortableRelativePath);
        var occurrence = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            "source-a", original.PortableRelativePath, original);
        var revision = XboxDvrSourceAdapter.CreateRevisionIdentity(
            occurrence, parsed.CapturedUtc, original);
        var hydratedState = original with
        {
            FileAttributes = 0x00000420,
            ReparseTag = 0x9000601a
        };
        var changedWrite = original with { LastWriteUtcTicks = original.LastWriteUtcTicks + 1 };
        var changedFile = original with
        {
            FileId128Hex = "000000000000000000000000000000ff"
        };

        Assert(IsSha256(occurrence) && IsSha256(revision) &&
               XboxDvrSourceAdapter.CreateOccurrenceIdentity(
                   "source-a", hydratedState.PortableRelativePath, hydratedState) == occurrence &&
               XboxDvrSourceAdapter.CreateRevisionIdentity(
                   occurrence, parsed.CapturedUtc, hydratedState) == revision,
            "Hydration-only Cloud Files attribute/tag changes must not change an Xbox occurrence or revision identity.");
        Assert(XboxDvrSourceAdapter.CreateOccurrenceIdentity(
                   "source-a", changedWrite.PortableRelativePath, changedWrite) == occurrence &&
               XboxDvrSourceAdapter.CreateRevisionIdentity(
                   occurrence, parsed.CapturedUtc, changedWrite) != revision,
            "A last-write change must advance only the revision identity.");
        Assert(XboxDvrSourceAdapter.CreateOccurrenceIdentity(
                   "source-b", original.PortableRelativePath, original) != occurrence &&
               XboxDvrSourceAdapter.CreateOccurrenceIdentity(
                   "source-a", changedFile.PortableRelativePath, changedFile) != occurrence,
            "Changing the configured source or native file identity must create another occurrence identity.");
    }

    private static void AssertPreflightFailsClosedOnDuplicateLeaves()
    {
        var candidate = Candidate("Game-2026_09_01-10-00-00.mp4", 100, 20);
        var adapter = new XboxDvrSourceAdapter(
            new RecordingMetadataFileSystem([candidate, candidate]));
        AssertThrows<InvalidDataException>(() => adapter.Preflight(
            "xbox-source-1",
            Path.Combine(Path.GetTempPath(), "clipcord-xbox-duplicates"),
            XboxDvrHistoryPolicy.Create(XboxDvrHistoryWindow.NewOnly, Activation, 10)));
    }

    private static XboxDvrCandidateMetadata Candidate(
        string fileName,
        long bytes,
        byte identity,
        uint reparseTag = 0x9000601a) => new(
        fileName,
        bytes,
        new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc).Ticks + identity,
        0x0123456789abcdef,
        identity.ToString("x2").PadLeft(32, '0'),
        identity.ToString("x2").PadLeft(64, '0'),
        XboxDvrCloudReparsePolicy.FileAttributeReparsePoint | 0x20,
        reparseTag);

    private sealed class RecordingMetadataFileSystem(
        IReadOnlyList<XboxDvrCandidateMetadata> metadata) : IXboxDvrMetadataFileSystem
    {
        internal int EnumerationCalls { get; private set; }
        internal int ContentOpenCalls { get; private set; }
        internal int HydrationCalls { get; private set; }

        public IReadOnlyList<XboxDvrCandidateMetadata> EnumerateMetadata(
            string canonicalRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(Path.IsPathFullyQualified(canonicalRoot),
                "The adapter must canonicalize the root before metadata enumeration.");
            EnumerationCalls++;
            return metadata;
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void AssertThrows<TException>(Action action)
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
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
