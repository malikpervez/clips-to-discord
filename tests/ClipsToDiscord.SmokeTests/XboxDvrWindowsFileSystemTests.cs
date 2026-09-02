using System.Security.Cryptography;
using System.Text;
using ClipsToDiscord;

internal static class XboxDvrWindowsFileSystemTests
{
    private static readonly DateTimeOffset Activation =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync()
    {
        AssertPreflightUsesOnlyZeroDataOperations();
        AssertEnumerationFailsClosedOnEscapesAndRootReplacement();
        AssertMetadataEnumerationStopsAtCallerCapacity();
        AssertNativeFindStopsAtMaximumPlusOne();
        await AssertImporterCopiesAndHashesWithoutMutatingSourceAsync();
        await AssertNativeImporterLeavesOriginalFileUnchangedAsync();
        await AssertImporterRejectsOverlappingRootsAsync();
        await AssertImporterCancellationCleansOnlyOwnedPartialAsync();
        await AssertImporterRejectsChangedRevisionAndLengthAsync();
    }

    private static void AssertPreflightUsesOnlyZeroDataOperations()
    {
        var root = CanonicalTempPath("metadata-source");
        var cloud = Metadata(
            "Battlefield 6-2026_09_01-11-30-00.mp4",
            Encoding.UTF8.GetByteCount("cloud"),
            1,
            0x9000401a);
        var ordinary = Metadata(
            "Duskfade-2026_09_01-11-00-00.mp4",
            Encoding.UTF8.GetByteCount("ordinary"),
            2,
            reparseTag: 0,
            fileAttributes: 0x20);
        var unsafeLink = Metadata(
            "Unsafe-2026_09_01-10-00-00.mp4",
            Encoding.UTF8.GetByteCount("unsafe"),
            3,
            0xa000000c);
        var operations = new RecordingWindowsOperations(
            root,
            [cloud, ordinary, unsafeLink])
        {
            RootSnapshots =
            [
                RootSnapshot(0x9000401a),
                RootSnapshot(0x9000601a)
            ],
            AdditionalFindEntries =
            [
                new XboxDvrWindowsFindEntry(
                    "readme.txt", 12, LastWriteTicks(10), 0x20, 0),
                new XboxDvrWindowsFindEntry(
                    "Folder.mp4", 0, LastWriteTicks(11),
                    XboxDvrCloudReparsePolicy.FileAttributeDirectory, 0)
            ]
        };
        var fileSystem = new WindowsXboxDvrMetadataFileSystem(operations);
        var adapter = new XboxDvrSourceAdapter(fileSystem);

        var preview = adapter.Preflight(
            "xbox-source-winfs",
            root,
            XboxDvrHistoryPolicy.Create(
                XboxDvrHistoryWindow.Last24Hours,
                Activation,
                maximumHistoricalClips: 10));

        Assert(operations.RootMetadataCalls == 2 &&
               operations.FindCalls == 1 &&
               operations.LeafMetadataCalls == 3 &&
               operations.SourceDataOpenCalls == 0 &&
               operations.PartialCreateCalls == 0 &&
               operations.SourceWriteCalls == 0 &&
               operations.SourceDeleteCalls == 0 &&
               operations.SourceMoveCalls == 0,
            "Xbox preflight must perform only root/list/leaf metadata operations and zero content or destination operations.");
        Assert(preview.TotalClipCount == 3 &&
               preview.EligibleHistoricalCount == 2 &&
               preview.NeedsAttentionCount == 1 &&
               preview.Items.Single(item => item.DisplayFileName.StartsWith("Unsafe"))
                   .NeedsAttentionReason == XboxDvrNeedsAttentionReason.UnsafeReparsePoint,
            "Metadata preflight must include ordinary and Cloud family clips while making a name-surrogate item review-only.");
    }

    private static void AssertEnumerationFailsClosedOnEscapesAndRootReplacement()
    {
        var root = CanonicalTempPath("metadata-containment");
        var clip = Metadata("Game-2026_09_01-11-00-00.mp4", 4, 10);
        var escaped = new RecordingWindowsOperations(root, [clip])
        {
            FindEntriesOverride =
            [
                FindEntry(clip) with
                {
                    FileName = "..\\Game-2026_09_01-11-00-00.mp4"
                }
            ]
        };
        AssertThrows<InvalidDataException>(() =>
            new WindowsXboxDvrMetadataFileSystem(escaped)
                .EnumerateMetadata(root, CancellationToken.None));
        Assert(escaped.LeafMetadataCalls == 0 && escaped.SourceDataOpenCalls == 0,
            "A non-leaf enumeration result must be rejected before any handle is opened for it.");

        var replaced = new RecordingWindowsOperations(root, [clip])
        {
            RootSnapshots =
            [
                RootSnapshot(0x9000401a),
                RootSnapshot(0x9000401a) with
                {
                    FileId128Hex = "000000000000000000000000000000ff"
                }
            ]
        };
        AssertThrows<IOException>(() =>
            new WindowsXboxDvrMetadataFileSystem(replaced)
                .EnumerateMetadata(root, CancellationToken.None));
        Assert(replaced.SourceDataOpenCalls == 0,
            "Replacing the configured root during enumeration must fail without opening content.");
    }

    private static void AssertMetadataEnumerationStopsAtCallerCapacity()
    {
        var root = CanonicalTempPath("metadata-capacity");
        var clips = new[]
        {
            Metadata("A-2026_09_01-11-00-00.mp4", 1, 61),
            Metadata("B-2026_09_01-11-01-00.mp4", 1, 62),
            Metadata("C-2026_09_01-11-02-00.mp4", 1, 63)
        };
        var operations = new RecordingWindowsOperations(root, clips);

        var exception = AssertThrows<XboxDvrMetadataCapacityExceededException>(() =>
            new WindowsXboxDvrMetadataFileSystem(operations)
                .EnumerateMetadata(root, maximumEntries: 2, CancellationToken.None));

        Assert(exception.MaximumEntries == 2 &&
               exception.Message.Contains("allowed 2 direct entries", StringComparison.Ordinal) &&
               operations.LastFindMaximumEntries == 2 &&
               operations.FindEntriesVisited == 3 &&
               operations.RootMetadataCalls == 1 &&
               operations.LeafMetadataCalls == 0 &&
               operations.SourceDataOpenCalls == 0,
            "A caller-provided metadata ceiling must stop at max+1 inside the listing boundary, return a typed capacity failure, and never inspect or open clip content.");
        AssertThrows<ArgumentOutOfRangeException>(() =>
            new WindowsXboxDvrMetadataFileSystem(operations)
                .EnumerateMetadata(root, maximumEntries: 0, CancellationToken.None));
    }

    private static void AssertNativeFindStopsAtMaximumPlusOne()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "clipcord-xbox-native-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "one.txt"), "one");
            File.WriteAllText(Path.Combine(root, "two.txt"), "two");
            var operations = new XboxDvrWindowsFileOperations();
            var exact = operations.FindTopLevelNoData(
                root,
                maximumEntries: 2,
                CancellationToken.None);
            Assert(exact.Count == 2,
                "Native metadata enumeration must allow exactly the caller's ceiling.");

            File.WriteAllText(Path.Combine(root, "three.txt"), "three");
            var exception = AssertThrows<XboxDvrMetadataCapacityExceededException>(() =>
                operations.FindTopLevelNoData(
                    root,
                    maximumEntries: 2,
                    CancellationToken.None));
            Assert(exception.MaximumEntries == 2,
                "Native metadata enumeration must stop and report the exact ceiling on entry max+1.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertImporterCopiesAndHashesWithoutMutatingSourceAsync()
    {
        var root = CanonicalTempPath("import-source");
        var staging = CanonicalTempPath("import-staging");
        var payload = Encoding.UTF8.GetBytes("one admitted xbox clip");
        var expected = Metadata(
            "Battlefield 6-2026_09_01-11-30-00.mp4",
            payload.Length,
            20,
            0x9000401a);
        var operations = new RecordingWindowsOperations(root, [expected])
        {
            SourceBytes = payload,
            OpenedMetadataBefore = expected with { ReparseTag = 0x9000601a },
            OpenedMetadataAfter = expected with { ReparseTag = 0x9000601a }
        };
        var request = Request(root, staging, expected);
        var importer = new WindowsXboxDvrContentImporter(
            operations,
            () => "testtoken");

        var result = await importer.ImportAsync(request);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        Assert(result.CopiedBytes == payload.Length &&
               result.ContentSha256 == expectedHash &&
               result.OccurrenceIdentitySha256 == request.ExpectedOccurrenceIdentitySha256 &&
               result.RevisionIdentitySha256 == request.ExpectedRevisionIdentitySha256 &&
               result.StagedPath == operations.PublishedFinalPath &&
               operations.PublishedBytes!.SequenceEqual(payload),
            "A successful import must publish the exact admitted bytes and their in-flight SHA-256.");
        Assert(operations.LeafMetadataCalls == 1 &&
               operations.SourceDataOpenCalls == 1 &&
               operations.SourceMetadataReads == 2 &&
               operations.PartialCreateCalls == 1 &&
               operations.PublishCalls == 1 &&
               operations.PartialDeleteCalls == 0 &&
               operations.SourceWriteCalls == 0 &&
               operations.SourceDeleteCalls == 0 &&
               operations.SourceMoveCalls == 0,
            "The importer must read one admitted source, publish one owned copy, and never mutate the Xbox source.");
        Assert(operations.CreatedPartialPath is not null &&
               Path.GetDirectoryName(operations.CreatedPartialPath) == staging &&
               Path.GetFileName(operations.CreatedPartialPath).Contains(
                   request.ExpectedOccurrenceIdentitySha256,
                   StringComparison.Ordinal) &&
               operations.PublishedFinalPath == Path.Combine(
                   staging,
                   request.ExpectedOccurrenceIdentitySha256 + ".mp4"),
            "The importer must create and publish only direct children of its owned staging root.");
    }

    private static async Task AssertNativeImporterLeavesOriginalFileUnchangedAsync()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "clipcord-xbox-native-import-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(fixtureRoot, "source");
        var stagingRoot = Path.Combine(fixtureRoot, "staging");
        var sourceLeaf = "Battlefield 6-2026_09_01-11-30-00.mp4";
        var sourcePath = Path.Combine(sourceRoot, sourceLeaf);
        var payload = Enumerable.Range(0, 4_097)
            .Select(index => (byte)((index * 31) % 251))
            .ToArray();

        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(stagingRoot);
        try
        {
            await File.WriteAllBytesAsync(sourcePath, payload);
            File.SetLastWriteTimeUtc(
                sourcePath,
                new DateTime(2026, 9, 1, 11, 30, 0, DateTimeKind.Utc));

            var operations = new XboxDvrWindowsFileOperations();
            var expected = operations.InspectLeafNoData(
                sourceRoot,
                sourceLeaf,
                CancellationToken.None);
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            var originalLength = new FileInfo(sourcePath).Length;
            var originalLastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);
            var originalFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly);

            var request = Request(sourceRoot, stagingRoot, expected);
            var result = await new WindowsXboxDvrContentImporter(
                    operations,
                    () => "nativetesttoken")
                .ImportAsync(request);

            Assert(File.Exists(sourcePath) && Directory.Exists(sourceRoot),
                "A native Xbox import must leave the original source path in place.");
            Assert((await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(originalBytes) &&
                   new FileInfo(sourcePath).Length == originalLength &&
                   File.GetLastWriteTimeUtc(sourcePath) == originalLastWriteUtc,
                "A native Xbox import must not change the original bytes, length, or last-write timestamp.");
            Assert(Directory.GetFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly)
                       .SequenceEqual(originalFiles, StringComparer.OrdinalIgnoreCase),
                "A native Xbox import must not move, rename, add, or remove anything in the source folder.");
            Assert(File.Exists(result.StagedPath) &&
                   (await File.ReadAllBytesAsync(result.StagedPath)).SequenceEqual(payload) &&
                   result.CopiedBytes == payload.Length &&
                   result.ContentSha256 == Convert.ToHexString(SHA256.HashData(payload))
                       .ToLowerInvariant(),
                "The native importer must publish an exact owned copy while preserving the original.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    private static async Task AssertImporterRejectsOverlappingRootsAsync()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "clipcord-xbox-overlap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            for (var caseIndex = 0; caseIndex < 3; caseIndex++)
            {
                var caseRoot = Path.Combine(fixtureRoot, "case-" + caseIndex);
                var sourceRoot = caseIndex switch
                {
                    0 or 1 => Path.Combine(caseRoot, "source"),
                    _ => Path.Combine(caseRoot, "staging", "source")
                };
                var stagingRoot = caseIndex switch
                {
                    0 => sourceRoot,
                    1 => Path.Combine(sourceRoot, "owned-staging"),
                    _ => Path.Combine(caseRoot, "staging")
                };
                Directory.CreateDirectory(sourceRoot);
                Directory.CreateDirectory(stagingRoot);
                var sourceLeaf = "Game-2026_09_01-11-00-00.mp4";
                var sourcePath = Path.Combine(sourceRoot, sourceLeaf);
                var payload = new byte[] { 0x2a, 0x51, (byte)caseIndex };
                await File.WriteAllBytesAsync(sourcePath, payload);
                var operations = new XboxDvrWindowsFileOperations();
                var expected = operations.InspectLeafNoData(
                    sourceRoot,
                    sourceLeaf,
                    CancellationToken.None);

                await AssertThrowsAsync<IOException>(() =>
                    new WindowsXboxDvrContentImporter(
                            operations,
                            () => "overlaptesttoken")
                        .ImportAsync(Request(sourceRoot, stagingRoot, expected)));

                Assert(File.Exists(sourcePath) &&
                       (await File.ReadAllBytesAsync(sourcePath)).SequenceEqual(payload),
                    "Rejecting overlapping Xbox source and staging roots must leave the source untouched.");
            }
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    private static async Task AssertImporterCancellationCleansOnlyOwnedPartialAsync()
    {
        var root = CanonicalTempPath("cancel-source");
        var staging = CanonicalTempPath("cancel-staging");
        var payload = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        var expected = Metadata(
            "Duskfade-2026_09_01-11-15-00.mp4",
            payload.Length,
            30);
        using var cancellation = new CancellationTokenSource();
        var operations = new RecordingWindowsOperations(root, [expected])
        {
            SourceBytes = payload,
            SourceStreamFactory = () =>
                new CancelAfterFirstReadStream(payload, cancellation)
        };
        var importer = new WindowsXboxDvrContentImporter(
            operations,
            () => "canceltoken");

        await AssertThrowsAsync<OperationCanceledException>(() => importer.ImportAsync(
            Request(root, staging, expected),
            cancellation.Token));

        Assert(operations.PartialCreateCalls == 1 &&
               operations.PublishCalls == 0 &&
               operations.PartialDeleteCalls == 1 &&
               operations.DeletedPartialPath == operations.CreatedPartialPath &&
               operations.SourceWriteCalls == 0 &&
               operations.SourceDeleteCalls == 0 &&
               operations.SourceMoveCalls == 0,
            "Cancelling a hydrated read must remove only that operation's exact owned partial and never touch the source.");
    }

    private static async Task AssertImporterRejectsChangedRevisionAndLengthAsync()
    {
        var root = CanonicalTempPath("changed-source");
        var staging = CanonicalTempPath("changed-staging");
        var payload = Encoding.UTF8.GetBytes("stable bytes");
        var expected = Metadata(
            "Game-2026_09_01-11-00-00.mp4",
            payload.Length,
            40);
        var changed = new RecordingWindowsOperations(root, [expected])
        {
            SourceBytes = payload,
            OpenedMetadataAfter = expected with
            {
                LastWriteUtcTicks = expected.LastWriteUtcTicks + 1
            }
        };
        await AssertThrowsAsync<InvalidDataException>(() =>
            new WindowsXboxDvrContentImporter(changed, () => "changedtoken")
                .ImportAsync(Request(root, staging, expected)));
        Assert(changed.PublishCalls == 0 && changed.PartialDeleteCalls == 1,
            "A post-copy source revision change must prevent publication and clean the exact partial.");

        var shortRead = new RecordingWindowsOperations(root, [expected])
        {
            SourceBytes = payload[..^1]
        };
        await AssertThrowsAsync<InvalidDataException>(() =>
            new WindowsXboxDvrContentImporter(shortRead, () => "shorttoken")
                .ImportAsync(Request(root, staging, expected)));
        Assert(shortRead.PublishCalls == 0 && shortRead.PartialDeleteCalls == 1,
            "A short source read must not publish an incomplete staging file.");
    }

    private static XboxDvrContentImportRequest Request(
        string root,
        string staging,
        XboxDvrCandidateMetadata expected)
    {
        var parsed = XboxDvrFileNameParser.Parse(expected.PortableRelativePath);
        var occurrence = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            "xbox-source-winfs",
            expected.PortableRelativePath,
            expected);
        var revision = XboxDvrSourceAdapter.CreateRevisionIdentity(
            occurrence,
            parsed.CapturedUtc,
            expected);
        return new XboxDvrContentImportRequest(
            "xbox-source-winfs",
            ExpectedSourceRevision: 1,
            ExpectedRoutingGeneration: 1,
            ExpectedRootIdentitySha256:
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            root,
            expected,
            occurrence,
            revision,
            staging);
    }

    private static XboxDvrCandidateMetadata Metadata(
        string leaf,
        long bytes,
        byte identity,
        uint reparseTag = 0x9000601a,
        uint? fileAttributes = null) => new(
        leaf,
        bytes,
        LastWriteTicks(identity),
        0x0123456789abcdef,
        identity.ToString("x2").PadLeft(32, '0'),
        ProviderIdentitySha256: null,
        fileAttributes ??
            (XboxDvrCloudReparsePolicy.FileAttributeReparsePoint | 0x20),
        reparseTag);

    private static XboxDvrWindowsFindEntry FindEntry(
        XboxDvrCandidateMetadata metadata) => new(
        metadata.PortableRelativePath,
        metadata.LogicalBytes,
        metadata.LastWriteUtcTicks,
        metadata.FileAttributes,
        metadata.ReparseTag);

    private static XboxDvrWindowsRootSnapshot RootSnapshot(uint tag) => new(
        0x0123456789abcdef,
        "00000000000000000000000000000001",
        XboxDvrCloudReparsePolicy.FileAttributeDirectory |
        XboxDvrCloudReparsePolicy.FileAttributeReparsePoint,
        tag);

    private static long LastWriteTicks(byte discriminator) =>
        new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc).Ticks + discriminator;

    private static string CanonicalTempPath(string leaf) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clipcord-xbox-winfs", leaf)));

    private sealed class RecordingWindowsOperations : IXboxDvrWindowsFileOperations
    {
        private readonly string _root;
        private readonly Dictionary<string, XboxDvrCandidateMetadata> _metadata;
        private int _rootSnapshotIndex;
        private RetainedMemoryStream? _ownedPartial;

        internal RecordingWindowsOperations(
            string root,
            IReadOnlyList<XboxDvrCandidateMetadata> metadata)
        {
            _root = root;
            _metadata = metadata.ToDictionary(
                item => item.PortableRelativePath,
                StringComparer.Ordinal);
            FindEntriesOverride = metadata.Select(FindEntry).ToArray();
            RootSnapshots = [RootSnapshot(0x9000601a), RootSnapshot(0x9000601a)];
            SourceBytes = [];
        }

        internal IReadOnlyList<XboxDvrWindowsRootSnapshot> RootSnapshots { get; init; }
        internal IReadOnlyList<XboxDvrWindowsFindEntry> FindEntriesOverride { get; init; }
        internal IReadOnlyList<XboxDvrWindowsFindEntry> AdditionalFindEntries { get; init; } = [];
        internal byte[] SourceBytes { get; init; }
        internal Func<Stream>? SourceStreamFactory { get; init; }
        internal XboxDvrCandidateMetadata? OpenedMetadataBefore { get; init; }
        internal XboxDvrCandidateMetadata? OpenedMetadataAfter { get; init; }
        internal int RootMetadataCalls { get; private set; }
        internal int FindCalls { get; private set; }
        internal int LastFindMaximumEntries { get; private set; }
        internal int FindEntriesVisited { get; private set; }
        internal int LeafMetadataCalls { get; private set; }
        internal int SourceDataOpenCalls { get; private set; }
        internal int SourceMetadataReads { get; private set; }
        internal int PartialCreateCalls { get; private set; }
        internal int PublishCalls { get; private set; }
        internal int PartialDeleteCalls { get; private set; }
        internal int SourceWriteCalls { get; private set; }
        internal int SourceDeleteCalls { get; private set; }
        internal int SourceMoveCalls { get; private set; }
        internal string? CreatedPartialPath { get; private set; }
        internal string? DeletedPartialPath { get; private set; }
        internal string? PublishedFinalPath { get; private set; }
        internal byte[]? PublishedBytes { get; private set; }

        public XboxDvrWindowsRootSnapshot InspectRootNoData(
            string canonicalRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(canonicalRoot == _root, "The production adapter must preserve the canonical root.");
            RootMetadataCalls++;
            var index = Math.Min(_rootSnapshotIndex++, RootSnapshots.Count - 1);
            return RootSnapshots[index];
        }

        public IReadOnlyList<XboxDvrWindowsFindEntry> FindTopLevelNoData(
            string canonicalRoot,
            int maximumEntries,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(canonicalRoot == _root, "Enumeration must stay within the canonical root.");
            FindCalls++;
            LastFindMaximumEntries = maximumEntries;
            var entries = new List<XboxDvrWindowsFindEntry>(maximumEntries + 1);
            foreach (var entry in FindEntriesOverride.Concat(AdditionalFindEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FindEntriesVisited++;
                entries.Add(entry);
                if (entries.Count > maximumEntries)
                {
                    throw new XboxDvrMetadataCapacityExceededException(maximumEntries);
                }
            }
            return entries;
        }

        public XboxDvrCandidateMetadata InspectLeafNoData(
            string canonicalRoot,
            string portableLeaf,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(canonicalRoot == _root, "Leaf inspection must stay within the canonical root.");
            LeafMetadataCalls++;
            return _metadata[portableLeaf];
        }

        public IXboxDvrSourceReadSession OpenSourceRead(
            string canonicalRoot,
            string portableLeaf,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(canonicalRoot == _root && _metadata.ContainsKey(portableLeaf),
                "The importer must open only the admitted direct leaf.");
            SourceDataOpenCalls++;
            var expected = _metadata[portableLeaf];
            return new RecordingReadSession(
                SourceStreamFactory?.Invoke() ?? new MemoryStream(SourceBytes, writable: false),
                OpenedMetadataBefore ?? expected,
                OpenedMetadataAfter ?? expected,
                () => SourceMetadataReads++);
        }

        public Stream CreateOwnedPartial(
            string canonicalStagingRoot,
            string exactPartialPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(Path.GetDirectoryName(exactPartialPath) == canonicalStagingRoot,
                "A partial must be one direct staging child.");
            PartialCreateCalls++;
            CreatedPartialPath = exactPartialPath;
            _ownedPartial = new RetainedMemoryStream();
            return _ownedPartial;
        }

        public void PublishOwnedPartial(
            string canonicalStagingRoot,
            string exactPartialPath,
            string exactFinalPath)
        {
            Assert(exactPartialPath == CreatedPartialPath &&
                   Path.GetDirectoryName(exactFinalPath) == canonicalStagingRoot,
                "Only the current owned partial may be published.");
            PublishCalls++;
            PublishedFinalPath = exactFinalPath;
            PublishedBytes = _ownedPartial!.ToArray();
        }

        public void TryDeleteOwnedPartial(
            string canonicalStagingRoot,
            string exactPartialPath)
        {
            Assert(exactPartialPath == CreatedPartialPath &&
                   Path.GetDirectoryName(exactPartialPath) == canonicalStagingRoot,
                "Cleanup must target only the current owned partial.");
            PartialDeleteCalls++;
            DeletedPartialPath = exactPartialPath;
        }
    }

    private sealed class RecordingReadSession(
        Stream content,
        XboxDvrCandidateMetadata before,
        XboxDvrCandidateMetadata after,
        Action onMetadataRead) : IXboxDvrSourceReadSession
    {
        private int _metadataReads;
        public Stream Content => content;

        public XboxDvrCandidateMetadata ReadCurrentMetadata(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onMetadataRead();
            return _metadataReads++ == 0 ? before : after;
        }

        public ValueTask DisposeAsync() => content.DisposeAsync();
    }

    private sealed class RetainedMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            // Retain bytes so the fake platform can inspect them after importer disposal.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelAfterFirstReadStream(
        byte[] payload,
        CancellationTokenSource cancellation) : MemoryStream(payload, writable: false)
    {
        private bool _first = true;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Read(buffer.Span);
            if (_first && read > 0)
            {
                _first = false;
                cancellation.Cancel();
            }
            return ValueTask.FromResult(read);
        }
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

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
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
