using ClipsToDiscord;

internal static class FreshRoutingBaselinePreparerTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        AssertMissingStateIsBaselined(root);
        AssertRetryMergesNewClipsAndPreservesClassifications(root);
        AssertFingerprintFailurePreservesStateBytes(root);
        AssertOversizedSavePreservesStateBytes(root);
        AssertFreshAdmissionRebaselinesInvalidOrUnrelatedState(root);
        AssertFailedResetLeavesPriorBytesQuarantined(root);
        AssertAdmissionAndQuiescenceAreRequired(root);
        AssertRootIdentityChangeFailsBeforeSave(root);
        AssertCandidateRaceFailsBeforeSave(root);
    }

    private static void AssertMissingStateIsBaselined(string root)
    {
        var fixture = CreateFixture(root, "missing");
        fixture.Adapter.Files.Add(SourceFile(fixture.ClipsRoot, "first.mp4", Hash('a'), 1));

        var result = fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                legacyWorkerQuiesced: true)
            .GetAwaiter().GetResult();

        Assert(result.Loaded && result.State is
               {
                   Version: WatchStateStore.CurrentVersion,
                   PendingMoves.Count: 0,
                   PendingLocalOnlyMoves.Count: 0,
                   PendingEditedUploads.Count: 0,
                   IgnoredFileKeys.Count: 0
               } && result.State.KnownContentHashes.SetEquals([Hash('a')]) &&
               result.WatchedRootIdentitySha256 == Hash('9'),
            "A missing fresh state must be created once with every current clip hash excluded and return the validated native source-root identity.");
    }

    private static void AssertRetryMergesNewClipsAndPreservesClassifications(string root)
    {
        var fixture = CreateFixture(root, "retry");
        fixture.Adapter.Files.Add(SourceFile(fixture.ClipsRoot, "first.mp4", Hash('b'), 2));
        _ = fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                legacyWorkerQuiesced: true)
            .GetAwaiter().GetResult();

        var classified = fixture.Store.ProbeForRoutingActivation().State!;
        classified.UploadedContentHashes.Add(Hash('b'));
        fixture.Store.Save(classified);
        fixture.Adapter.Files.Add(SourceFile(fixture.ClipsRoot, "arrived.mp4", Hash('c'), 3));

        var retried = fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                legacyWorkerQuiesced: true)
            .GetAwaiter().GetResult();

        Assert(retried.State!.KnownContentHashes.SetEquals([Hash('b'), Hash('c')]) &&
               retried.State.UploadedContentHashes.SetEquals([Hash('b')]) &&
               retried.State.LocalOnlyContentHashes.Count == 0,
            "A retried fresh setup must rebaseline newly arrived clips without changing prior delivery classifications.");
    }

    private static void AssertFingerprintFailurePreservesStateBytes(string root)
    {
        var fixture = CreateFixture(root, "fingerprint-failure");
        var state = DrainedState(fixture.ClipsRoot);
        state.KnownContentHashes.Add(Hash('d'));
        state.LocalOnlyContentHashes.Add(Hash('d'));
        fixture.Store.Save(state);
        var before = File.ReadAllBytes(fixture.Store.StatePath);
        fixture.Adapter.Files.Add(SourceFile(fixture.ClipsRoot, "failure.mp4", Hash('e'), 4));
        fixture.Adapter.FailFingerprint = true;

        AssertThrows<IOException>(() => fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                legacyWorkerQuiesced: true)
            .GetAwaiter().GetResult());
        Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Store.StatePath)),
            "A fingerprint failure must leave the prior watch-state document byte-for-byte intact.");
    }

    private static void AssertOversizedSavePreservesStateBytes(string root)
    {
        var fixture = CreateFixture(root, "oversized-save");
        var prior = DrainedState(fixture.ClipsRoot);
        prior.KnownContentHashes.Add(Hash('7'));
        prior.LocalOnlyContentHashes.Add(Hash('7'));
        fixture.Store.Save(prior);
        var before = File.ReadAllBytes(fixture.Store.StatePath);

        var oversized = DrainedState(fixture.ClipsRoot);
        oversized.IgnoredFileKeys.Add(new string('x', WatchStateStore.MaximumStateBytes));
        AssertThrows<InvalidDataException>(() => fixture.Store.Save(oversized));

        var after = fixture.Store.ProbeForRoutingActivation();
        Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Store.StatePath)) &&
               !File.Exists(fixture.Store.StatePath + ".tmp") &&
               after.Loaded && after.State is not null &&
               after.State.KnownContentHashes.SetEquals([Hash('7')]) &&
               after.State.LocalOnlyContentHashes.SetEquals([Hash('7')]),
            "An oversized fresh baseline must be rejected before it can replace the prior valid state.");
    }

    private static void AssertFreshAdmissionRebaselinesInvalidOrUnrelatedState(string root)
    {
        foreach (var scenario in Enum.GetValues<ResetScenario>())
        {
            var fixture = CreateFixture(root, "reset-" + scenario);
            var oldRoot = Directory.CreateDirectory(
                Path.Combine(fixture.ProfileRoot, "old-clips")).FullName;
            var state = DrainedState(fixture.ClipsRoot);
            state.KnownContentHashes.Add(Hash('1'));
            state.UploadedContentHashes.Add(Hash('1'));
            state.IgnoredFileKeys.Add("legacy-file-key");
            state.PendingMoves.Add(Path.Combine(fixture.ProfileRoot, "pending-upload.mp4"));
            state.PendingLocalOnlyMoves.Add(
                Path.Combine(fixture.ProfileRoot, "pending-local-only.mp4"));
            state.PendingEditedUploads.Add(new PendingEditedClipDisposition
            {
                Id = Guid.NewGuid()
            });
            switch (scenario)
            {
                case ResetScenario.WrongRoot:
                    state.ClipsFolder = oldRoot;
                    break;
                case ResetScenario.WrongSource:
                    state.CaptureSource = ClipCaptureSource.Nvidia;
                    break;
                case ResetScenario.StructurallyInvalid:
                    state.KnownContentHashes.Add("not-a-sha256");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            fixture.Store.Save(state);
            var priorBytes = File.ReadAllBytes(fixture.Store.StatePath);
            var priorProbe = fixture.Store.ProbeForRoutingActivation();
            var expectedProbe = scenario == ResetScenario.StructurallyInvalid
                ? WatchStateRoutingProbeStatus.Invalid
                : WatchStateRoutingProbeStatus.Loaded;
            Assert(priorProbe.Status == expectedProbe &&
                   LegacyRoutingMigrationAdmissionStore.Classify(
                       Settings(fixture.ClipsRoot),
                       priorProbe,
                       LegacyRoutingSettingsPresence.Present) ==
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                $"{scenario} must be a terminal fresh-profile case before rebaselining.");

            var currentHash = scenario switch
            {
                ResetScenario.WrongRoot => Hash('2'),
                ResetScenario.WrongSource => Hash('3'),
                ResetScenario.StructurallyInvalid => Hash('4'),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            fixture.Adapter.Files.Add(SourceFile(
                fixture.ClipsRoot,
                "already-there.mp4",
                currentHash,
                (uint)scenario + 20));

            var result = fixture.Preparer.PrepareAsync(
                    Settings(fixture.ClipsRoot),
                    LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                    legacyWorkerQuiesced: true)
                .GetAwaiter().GetResult();

            var quarantines = Directory.GetFiles(
                fixture.ProfileRoot,
                "state.json.fresh-rebaseline-*.quarantine",
                SearchOption.TopDirectoryOnly);
            Assert(result.State is
                   {
                       Version: WatchStateStore.CurrentVersion,
                       CaptureSource: ClipCaptureSource.SteelSeriesGg,
                       PendingMoves.Count: 0,
                       PendingLocalOnlyMoves.Count: 0,
                       PendingEditedUploads.Count: 0,
                       IgnoredFileKeys.Count: 0,
                       UploadedContentHashes.Count: 0,
                       LocalOnlyContentHashes.Count: 0
                   } &&
                   result.State.ClipsFolder.Equals(
                       fixture.ClipsRoot, StringComparison.OrdinalIgnoreCase) &&
                   result.State.KnownContentHashes.SetEquals([currentHash]) &&
                   quarantines.Length == 1 &&
                   priorBytes.SequenceEqual(File.ReadAllBytes(quarantines[0])),
                $"{scenario} must become a clean current-source baseline, exclude every current clip, discard all executable legacy queues, and preserve the prior bytes only in quarantine.");
        }
    }

    private static void AssertFailedResetLeavesPriorBytesQuarantined(string root)
    {
        var fixture = CreateFixture(root, "reset-save-failure");
        var invalid = DrainedState(fixture.ClipsRoot);
        invalid.KnownContentHashes.Add("invalid-hash");
        invalid.PendingMoves.Add(Path.Combine(fixture.ProfileRoot, "pending-upload.mp4"));
        fixture.Store.Save(invalid);
        var priorBytes = File.ReadAllBytes(fixture.Store.StatePath);
        fixture.Adapter.Files.Add(SourceFile(
            fixture.ClipsRoot, "already-there.mp4", Hash('5'), 30));

        // WatchStateStore writes this path before its atomic replace. A directory at the path
        // injects a post-quarantine persistence failure without altering the production store.
        Directory.CreateDirectory(fixture.Store.StatePath + ".tmp");
        Exception? failure = null;
        try
        {
            _ = fixture.Preparer.PrepareAsync(
                    Settings(fixture.ClipsRoot),
                    LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                    legacyWorkerQuiesced: true)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var quarantines = Directory.GetFiles(
            fixture.ProfileRoot,
            "state.json.fresh-rebaseline-*.quarantine",
            SearchOption.TopDirectoryOnly);
        Assert(failure is IOException or UnauthorizedAccessException &&
               !File.Exists(fixture.Store.StatePath) &&
               quarantines.Length == 1 &&
               priorBytes.SequenceEqual(File.ReadAllBytes(quarantines[0])),
            "A failed reset save must leave the prior invalid document intact only in quarantine, never at the executable watcher-state path.");
    }

    private static void AssertAdmissionAndQuiescenceAreRequired(string root)
    {
        var fixture = CreateFixture(root, "guards");
        AssertThrows<InvalidOperationException>(() => fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.ValidLegacyUpgrade,
                legacyWorkerQuiesced: true)
            .GetAwaiter().GetResult());
        AssertThrows<InvalidOperationException>(() => fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                legacyWorkerQuiesced: false)
            .GetAwaiter().GetResult());

        Assert(fixture.Adapter.EnumerationCalls == 0 && !File.Exists(fixture.Store.StatePath),
            "Rejected admission or live-worker calls must neither inspect clips nor create state.");
    }

    private static void AssertCandidateRaceFailsBeforeSave(string root)
    {
        var fixture = CreateFixture(root, "race");
        fixture.Adapter.Files.Add(SourceFile(fixture.ClipsRoot, "racing.mp4", Hash('f'), 5));
        fixture.Adapter.RemoveCandidatesAfterFirstEnumeration = true;

        AssertThrows<InvalidDataException>(() => fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                legacyWorkerQuiesced: true)
            .GetAwaiter().GetResult());
        Assert(!File.Exists(fixture.Store.StatePath),
            "A changing candidate set must fail before a missing fresh state is persisted.");
    }

    private static void AssertRootIdentityChangeFailsBeforeSave(string root)
    {
        var fixture = CreateFixture(root, "root-identity-race");
        fixture.Adapter.RootIdentityAfterFirstInspection = Hash('8');

        AssertThrows<InvalidDataException>(() => fixture.Preparer.PrepareAsync(
                Settings(fixture.ClipsRoot),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                legacyWorkerQuiesced: true)
            .GetAwaiter().GetResult());
        Assert(!File.Exists(fixture.Store.StatePath),
            "A native source-root replacement during fresh baselining must fail before watcher state is saved.");
    }

    private static Fixture CreateFixture(string root, string name)
    {
        var profile = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
        var store = new WatchStateStore(
            Path.Combine(profile, "state.json"),
            Path.Combine(profile, ".safe-baseline-required"));
        var adapter = new BaselineAdapter(clips, ClipCaptureSource.SteelSeriesGg);
        return new Fixture(
            profile,
            clips,
            store,
            adapter,
            new FreshRoutingBaselinePreparer(store, _ => adapter));
    }

    private static AppSettings Settings(string clipsRoot) => new(
        clipsRoot,
        string.Empty,
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Fresh baseline test",
        UploadToDiscord: false,
        CaptureSource: ClipCaptureSource.SteelSeriesGg);

    private static WatchState DrainedState(string clipsRoot) => new()
    {
        Version = WatchStateStore.CurrentVersion,
        ClipsFolder = clipsRoot,
        CaptureSource = ClipCaptureSource.SteelSeriesGg
    };

    private static RoutingWatchedSourceFile SourceFile(
        string root,
        string leaf,
        string hash,
        uint fileId) => new(
        ClipCaptureSource.SteelSeriesGg,
        root,
        leaf,
        "Game",
        leaf,
        Hash('9'),
        new RoutingWatchedNativeFileIdentity(
            VolumeSerialNumber: 17,
            FileIdHex: fileId.ToString("x16"),
            ByteLength: 4,
            CreationUtcTicks: 638900000000000000 + fileId,
            LastWriteUtcTicks: 638900000100000000 + fileId),
        hash);

    private static string Hash(char value) => new(value, 64);

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException(
                $"Expected {typeof(TException).Name}, but the operation succeeded.");
        }
        catch (TException)
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Fixture(
        string ProfileRoot,
        string ClipsRoot,
        WatchStateStore Store,
        BaselineAdapter Adapter,
        FreshRoutingBaselinePreparer Preparer);

    private enum ResetScenario
    {
        WrongRoot,
        WrongSource,
        StructurallyInvalid
    }

    private sealed class BaselineAdapter(
        string canonicalRoot,
        ClipCaptureSource source) : IRoutingWatchedSourceAdapter
    {
        private int _enumerationCalls;
        private int _rootIdentityInspectionCalls;

        internal List<RoutingWatchedSourceFile> Files { get; } = [];
        internal bool FailFingerprint { get; set; }
        internal bool RemoveCandidatesAfterFirstEnumeration { get; set; }
        internal string? RootIdentityAfterFirstInspection { get; set; }
        internal int EnumerationCalls => Volatile.Read(ref _enumerationCalls);
        public ClipCaptureSource Source { get; } = source;

        public string InspectRootIdentity(string clipsRoot) =>
            Interlocked.Increment(ref _rootIdentityInspectionCalls) > 1 &&
            RootIdentityAfterFirstInspection is not null
                ? RootIdentityAfterFirstInspection
                : Hash('9');

        public IReadOnlyList<string> EnumerateCandidates(
            string clipsRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _enumerationCalls);
            if (RemoveCandidatesAfterFirstEnumeration && call > 1) return [];
            return Files.Select(item => Path.Combine(
                    canonicalRoot,
                    item.PortableRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .ToArray();
        }

        public Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Find(candidatePath);
            return Task.FromResult(new RoutingWatchedSourceOccurrence(
                file.Source,
                file.CanonicalRoot,
                file.PortableRelativePath,
                file.GameName,
                file.DisplayFileName,
                file.RootIdentitySha256,
                file.NativeFileIdentity));
        }

        public Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailFingerprint) throw new IOException("Injected fingerprint failure.");
            return Task.FromResult(Find(candidatePath));
        }

        public Task<RoutingWatchedSourceFile> RevalidateAsync(
            RoutingWatchedSourceFile prior,
            CancellationToken cancellationToken = default) => Task.FromResult(prior);

        private RoutingWatchedSourceFile Find(string candidatePath)
        {
            var relative = Path.GetRelativePath(canonicalRoot, candidatePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return Files.Single(item => item.PortableRelativePath.Equals(
                relative, StringComparison.OrdinalIgnoreCase));
        }
    }
}
