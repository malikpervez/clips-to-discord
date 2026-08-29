using System.Security.Cryptography;
using ClipsToDiscord;

internal static class RoutingWatchedFolderShadowTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 16, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertDisabledAndMissingGateAreInertAsync(Path.Combine(root, "disabled"));
        await AssertLocalOnlyShadowPlanIsIsolatedAsync(Path.Combine(root, "local-only"));
        await AssertDiscordPlanRestartAndDuplicatesAsync(Path.Combine(root, "discord"));
        await AssertLegacyExclusionsNeverPlanAsync(Path.Combine(root, "exclusions"));
        await AssertSourceSwitchFailsClosedAsync(Path.Combine(root, "source-switch"));
        await AssertModeAndConnectionSwitchFailClosedAsync(
            Path.Combine(root, "mode-switch"));
        await AssertCatalogConnectionAuthorityAsync(
            Path.Combine(root, "catalog-authority"));
        await AssertLayoutAndPostProbeIdentityFailClosedAsync(
            Path.Combine(root, "identity-validation"));
        AssertCorruptShadowStoreFailsClosed(Path.Combine(root, "corrupt"));
    }

    private static async Task AssertDisabledAndMissingGateAreInertAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var path = await WriteClipAsync(
            clips,
            "Battlefield 6 2026.08.28 - 12.00.00.00.DVR.mp4",
            [1, 2, 3, 4]);
        var settings = Settings(clips, upload: false, ClipCaptureSource.SteelSeriesGg);
        var candidate = await CandidateAsync(settings, path);
        var disabled = await DisabledRoutingWatchedFolderObserver.Instance.ObserveAsync(
            candidate,
            CancellationToken.None);
        Assert(disabled.Status == RoutingWatchedFolderObservationStatus.Disabled,
            "Watched-folder ingestion must be disabled by default.");

        var routing = Directory.CreateDirectory(Path.Combine(root, "routing")).FullName;
        var shadowPath = Path.Combine(routing, RoutingWatchedShadowStore.FileName);
        var probe = new RecordingMediaProbe();
        var observer = new RoutingWatchedFolderShadowObserver(
            RoutingWatchedFolderIngestionOptions.Shadow,
            new RoutingSnapshotStore(Path.Combine(routing, RoutingSnapshotStore.FileName)),
            new LegacyRoutingMigrationMarkerStore(Path.Combine(
                routing,
                LegacyRoutingMigrationMarkerStore.FileName)),
            new RoutingWatchedShadowStore(shadowPath),
            probe,
            createPlanId: () => Guid.Parse("11111111-1111-1111-1111-111111111111"),
            utcNow: () => Now);
        var missingGate = await observer.ObserveAsync(candidate, CancellationToken.None);
        Assert(missingGate.Status == RoutingWatchedFolderObservationStatus.GateUnavailable &&
               probe.Calls == 0 && !File.Exists(shadowPath) && File.Exists(path),
            "A missing routing snapshot/marker must perform no probe, persistence, or file effect.");
        AssertThrows<NotSupportedException>(() => _ = new RoutingWatchedFolderShadowObserver(
                new RoutingWatchedFolderIngestionOptions(
                    RoutingWatchedFolderIngestionMode.Active),
                new RoutingSnapshotStore(Path.Combine(routing, "active-routes.json")),
                new LegacyRoutingMigrationMarkerStore(Path.Combine(
                    routing,
                    LegacyRoutingMigrationMarkerStore.FileName)),
                new RoutingWatchedShadowStore(Path.Combine(routing, "active-shadow.json")),
                probe),
            "The shadow observer must refuse to masquerade as a live active runtime.");
    }

    private static async Task AssertLocalOnlyShadowPlanIsIsolatedAsync(string root)
    {
        var fixture = CreateFixture(
            root,
            upload: false,
            ClipCaptureSource.SteelSeriesGg);
        var fileName = "Battlefield 6 2026.08.28 - 12.01.02.03.DVR.mp4";
        var path = await WriteClipAsync(fixture.ClipsRoot, fileName, [5, 6, 7, 8]);
        var before = await File.ReadAllBytesAsync(path);
        var result = await fixture.Observer.ObserveAsync(
            await CandidateAsync(fixture.Settings, path),
            CancellationToken.None);
        var load = fixture.Shadow.Load();
        var observation = load.Document!.Observations.Single();
        Assert(result.Status == RoutingWatchedFolderObservationStatus.Recorded &&
               result.Kind == RoutingWatchedShadowObservationKind.Planned &&
               observation.CaptureSource == ClipCaptureSource.SteelSeriesGg &&
               observation.GameName == "Battlefield 6" &&
               observation.DurationMilliseconds == 42_000 &&
               observation.Width == 2560 && observation.Height == 1440 &&
               observation.Deliveries.Count == 0 &&
                observation.LibraryArea == RoutingLibraryArea.LocalOnly &&
                observation.MatchedRouteIds.SequenceEqual(
                    [fixture.Marker.Route.RouteId]) &&
               fixture.Probe.Calls == 1,
            "A Local-only watched clip must freeze one file-only shadow plan with exact media facts.");
        Assert(File.Exists(path) && (await File.ReadAllBytesAsync(path)).SequenceEqual(before) &&
               !Directory.Exists(Path.Combine(fixture.ClipsRoot, "local-only")) &&
               !Directory.Exists(Path.Combine(fixture.ClipsRoot, "uploaded")) &&
               !File.Exists(Path.Combine(
                   Path.GetDirectoryName(fixture.Shadow.Path)!,
                   RoutingOutboxStore.FileName)),
            "Shadow planning must not move a file, create an archive, or touch the production outbox.");
        var persisted = await File.ReadAllTextAsync(fixture.Shadow.Path);
        Assert(!persisted.Contains(fileName, StringComparison.OrdinalIgnoreCase) &&
               !persisted.Contains(fixture.ClipsRoot, StringComparison.OrdinalIgnoreCase) &&
               !persisted.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase) &&
               !persisted.Contains("shadow-secret", StringComparison.Ordinal),
            "The shadow report must persist no path, file name, webhook, or credential material.");
    }

    private static async Task AssertDiscordPlanRestartAndDuplicatesAsync(string root)
    {
        var fixture = CreateFixture(
            root,
            upload: true,
            ClipCaptureSource.Nvidia);
        var gameFolder = Directory.CreateDirectory(
            Path.Combine(fixture.ClipsRoot, "Duskfade")).FullName;
        var first = await WriteClipAsync(
            gameFolder,
            "Duskfade 2026.08.28 - 12.02.03.04.DVR.mp4",
            [9, 10, 11, 12]);
        var firstCandidate = await CandidateAsync(fixture.Settings, first);
        var planned = await fixture.Observer.ObserveAsync(
            firstCandidate,
            CancellationToken.None);
        var firstObservation = fixture.Shadow.Load().Document!.Observations.Single();
        var delivery = firstObservation.Deliveries.Single();
        Assert(planned.Kind == RoutingWatchedShadowObservationKind.Planned &&
               firstObservation.CaptureSource == ClipCaptureSource.Nvidia &&
               firstObservation.GameName == "Duskfade" &&
               delivery.Destination == RoutingDestinationKind.Discord &&
               delivery.ConnectionId == fixture.ConnectionId &&
               delivery.RequestedOutput == RoutingOutputKind.Original &&
               delivery.EffectiveOutput == RoutingOutputKind.Original &&
               delivery.State == PlannedDeliveryState.Ready &&
               firstObservation.LibraryArea == RoutingLibraryArea.Uploaded,
            "A Discord-mode NVIDIA clip must freeze its exact delivery and Uploaded disposition.");

        var restartProbe = new RecordingMediaProbe();
        var restarted = fixture.CreateObserver(restartProbe);
        var replay = await restarted.ObserveAsync(firstCandidate, CancellationToken.None);
        Assert(replay.Status == RoutingWatchedFolderObservationStatus.AlreadyObserved &&
               replay.SourceClipId == planned.SourceClipId &&
               restartProbe.Calls == 0 &&
               fixture.Shadow.Load().Document!.Observations.Count == 1,
            "Restart/replay must reuse the durable occurrence identity without probing or replanning.");

        var duplicate = await WriteClipAsync(
            gameFolder,
            "Duskfade 2026.08.28 - 12.02.04.05.DVR.mp4",
            [9, 10, 11, 12]);
        var duplicateResult = await restarted.ObserveAsync(
            await CandidateAsync(fixture.Settings, duplicate),
            CancellationToken.None);
        var observations = fixture.Shadow.Load().Document!.Observations;
        var duplicateObservation = observations.Single(item =>
            item.SourceClipId == duplicateResult.SourceClipId);
        Assert(duplicateResult.Kind == RoutingWatchedShadowObservationKind.ContentDuplicate &&
               duplicateObservation.DuplicateOfSourceClipId == firstObservation.SourceClipId &&
               duplicateObservation.Deliveries.SequenceEqual(firstObservation.Deliveries) &&
               duplicateObservation.LibraryArea == RoutingLibraryArea.Uploaded &&
               duplicateObservation.PlanFingerprint is not null &&
               restartProbe.Calls == 1 && observations.Count == 2 &&
               File.Exists(first) && File.Exists(duplicate),
            "Equal bytes at another occurrence must retain that occurrence's route/file plan while recording duplicate evidence.");

        var durable = fixture.Shadow.Load().Document!;
        var mismatchedId = firstObservation with
        {
            SourceClipId = new string(
                firstObservation.SourceClipId[0] == 'f' ? 'e' : 'f',
                32)
        };
        AssertThrows<InvalidDataException>(
            () => RoutingWatchedShadowModel.Validate(durable with
            {
                Observations = [mismatchedId, duplicateObservation]
            }),
            "Shadow validation must bind the short source id to its full occurrence identity.");
        AssertThrows<InvalidDataException>(
            () => RoutingWatchedShadowModel.Validate(durable with
            {
                Observations =
                [
                    firstObservation,
                    duplicateObservation with
                    {
                        DuplicateOfSourceClipId = mismatchedId.SourceClipId
                    }
                ]
            }),
            "Shadow validation must reject duplicate evidence that does not name earlier equal content.");
    }

    private static async Task AssertLegacyExclusionsNeverPlanAsync(string root)
    {
        var bytes = new byte[] { 20, 21, 22, 23 };
        var hash = Hash(bytes).ToUpperInvariant();
        var fixture = CreateFixture(
            root,
            upload: true,
            ClipCaptureSource.SteelSeriesGg,
            state =>
            {
                state.KnownContentHashes.Add(hash);
                state.UploadedContentHashes.Add(hash);
            });
        var path = await WriteClipAsync(
            fixture.ClipsRoot,
            "Halo Infinite 2026.08.28 - 12.03.04.05.DVR.mp4",
            bytes);
        var result = await fixture.Observer.ObserveAsync(
            await CandidateAsync(fixture.Settings, path),
            CancellationToken.None);
        var observation = fixture.Shadow.Load().Document!.Observations.Single();
        Assert(result.Kind == RoutingWatchedShadowObservationKind.LegacyUploadedExcluded &&
               observation.Deliveries.Count == 0 && observation.LibraryArea is null &&
               fixture.Probe.Calls == 0 && File.Exists(path),
            "A hash imported from the legacy Uploaded set must be recorded as excluded without planning or probing it.");
    }

    private static async Task AssertSourceSwitchFailsClosedAsync(string root)
    {
        var fixture = CreateFixture(
            root,
            upload: false,
            ClipCaptureSource.SteelSeriesGg);
        var game = Directory.CreateDirectory(Path.Combine(fixture.ClipsRoot, "Duskfade")).FullName;
        var path = await WriteClipAsync(
            game,
            "Duskfade 2026.08.28 - 12.04.05.06.DVR.mp4",
            [30, 31, 32, 33]);
        var switchedSettings = fixture.Settings with
        {
            CaptureSource = ClipCaptureSource.Nvidia
        };
        var result = await fixture.Observer.ObserveAsync(
            await CandidateAsync(switchedSettings, path),
            CancellationToken.None);
        Assert(result.Status == RoutingWatchedFolderObservationStatus.GateUnavailable &&
               fixture.Probe.Calls == 0 &&
               fixture.Shadow.Load().Status == RoutingDocumentLoadStatus.Missing &&
               File.Exists(path),
            "Changing SteelSeries/NVIDIA after the frozen migration source must fail closed with no shadow side effect.");
    }

    private static async Task AssertModeAndConnectionSwitchFailClosedAsync(string root)
    {
        var localFixture = CreateFixture(
            Path.Combine(root, "local-to-discord"),
            upload: false,
            ClipCaptureSource.SteelSeriesGg);
        var localPath = await WriteClipAsync(
            localFixture.ClipsRoot,
            "Halo Infinite 2026.08.28 - 12.04.05.07.DVR.mp4",
            [70, 71, 72, 73]);
        var switchedToDiscord = localFixture.Settings with
        {
            UploadToDiscord = true,
            WebhookUrl = "https://discord.com/api/webhooks/123456789012345678/new-secret"
        };
        var modeResult = await localFixture.Observer.ObserveAsync(
            await CandidateAsync(switchedToDiscord, localPath),
            CancellationToken.None);
        Assert(modeResult.Status == RoutingWatchedFolderObservationStatus.GateUnavailable &&
               localFixture.Probe.Calls == 0 &&
               localFixture.Shadow.Load().Status == RoutingDocumentLoadStatus.Missing,
            "Changing Local-only to Discord after migration must invalidate shadow planning.");

        var discordFixture = CreateFixture(
            Path.Combine(root, "discord-connection"),
            upload: true,
            ClipCaptureSource.SteelSeriesGg);
        var discordPath = await WriteClipAsync(
            discordFixture.ClipsRoot,
            "Valorant 2026.08.28 - 12.04.05.08.DVR.mp4",
            [74, 75, 76, 77]);
        var changedWebhook = discordFixture.Settings with
        {
            WebhookUrl = "https://discord.com/api/webhooks/987654321098765432/other-secret"
        };
        var connectionResult = await discordFixture.Observer.ObserveAsync(
            await CandidateAsync(changedWebhook, discordPath),
            CancellationToken.None);
        Assert(connectionResult.Status == RoutingWatchedFolderObservationStatus.GateUnavailable &&
               discordFixture.Probe.Calls == 0 &&
               discordFixture.Shadow.Load().Status == RoutingDocumentLoadStatus.Missing,
            "Changing the Discord connection after migration must invalidate shadow planning.");
    }

    private static async Task AssertLayoutAndPostProbeIdentityFailClosedAsync(string root)
    {
        var layoutFixture = CreateFixture(
            Path.Combine(root, "layout"),
            upload: false,
            ClipCaptureSource.SteelSeriesGg);
        var nested = Directory.CreateDirectory(
            Path.Combine(layoutFixture.ClipsRoot, "Not-A-SteelSeries-Layout")).FullName;
        var nestedPath = await WriteClipAsync(
            nested,
            "Duskfade 2026.08.28 - 12.04.06.07.DVR.mp4",
            [34, 35, 36, 37]);
        await AssertThrowsAsync<InvalidDataException>(
            () => layoutFixture.Observer.ObserveAsync(
                    CandidateAsync(layoutFixture.Settings, nestedPath).GetAwaiter().GetResult(),
                    CancellationToken.None)
                .AsTask(),
            "A SteelSeries shadow candidate below the configured root must be rejected.");
        Assert(layoutFixture.Probe.Calls == 0 &&
               layoutFixture.Shadow.Load().Status == RoutingDocumentLoadStatus.Missing,
            "An invalid source layout must fail before media probing or shadow persistence.");

        var mutationFixture = CreateFixture(
            Path.Combine(root, "mutation"),
            upload: false,
            ClipCaptureSource.SteelSeriesGg);
        var mutationPath = await WriteClipAsync(
            mutationFixture.ClipsRoot,
            "Valorant 2026.08.28 - 12.04.07.08.DVR.mp4",
            [50, 51, 52, 53]);
        var mutatingProbe = new MutatingMediaProbe([60, 61, 62, 63]);
        await AssertThrowsAsync<InvalidDataException>(
            () => mutationFixture.CreateObserver(mutatingProbe).ObserveAsync(
                    CandidateAsync(mutationFixture.Settings, mutationPath).GetAwaiter().GetResult(),
                    CancellationToken.None)
                .AsTask(),
            "A clip whose bytes change during the media probe must not produce shadow evidence.");
        Assert(mutatingProbe.Calls == 1 &&
               mutationFixture.Shadow.Load().Status == RoutingDocumentLoadStatus.Missing,
            "Post-probe identity failure must leave the shadow store untouched.");
    }

    private static async Task AssertCatalogConnectionAuthorityAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var routing = Directory.CreateDirectory(Path.Combine(root, "routing")).FullName;
        var settings = Settings(clips, upload: true, ClipCaptureSource.SteelSeriesGg);
        var catalog = new DiscordConnectionCatalog(
            new DiscordConnectionCatalogStore(Path.Combine(
                routing,
                DiscordConnectionCatalogStore.FileName)),
            new TestWebhookProtector(),
            new DiscordConnectionReferenceProbe(
                new RoutingSnapshotStore(Path.Combine(routing, "authority-routes.json")),
                new RoutingOutboxStore(Path.Combine(routing, "authority-outbox.json"))),
            () => Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var imported = await catalog.EnsureLegacyConnectionAsync(settings, Now);
        Assert(imported.Succeeded && imported.Connection is not null,
            "The concrete watched-folder connection authority fixture could not import Discord.");
        Assert(DiscordRoutingConnectionIdentity.TryCreate(
                settings.WebhookUrl,
                out var legacyIdentity),
            "The concrete watched-folder connection authority fixture has no webhook identity.");
        var authority = new DiscordCatalogRoutingWatchedFolderLegacyConnectionAuthority(
            catalog,
            settings);
        var resolved = authority.ResolveCurrentConnectionId(
            legacyIdentity,
            CancellationToken.None);
        Assert(resolved == imported.Connection!.ConnectionId && resolved != legacyIdentity,
            "The connection authority must translate the webhook digest to the catalog's random route id.");
        Assert(DiscordRoutingConnectionIdentity.TryCreate(
                   "https://discord.com/api/webhooks/987654321098765432/changed-secret",
                   out var changedIdentity) &&
               authority.ResolveCurrentConnectionId(
                   changedIdentity,
                   CancellationToken.None) is null,
            "The connection authority must reject a webhook identity other than its current setting.");
    }

    private static void AssertCorruptShadowStoreFailsClosed(string root)
    {
        Directory.CreateDirectory(root);
        var fixture = CreateFixture(
            root,
            upload: false,
            ClipCaptureSource.SteelSeriesGg);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Shadow.Path)!);
        File.WriteAllText(fixture.Shadow.Path, "{ definitely-not-json");
        var path = WriteClipAsync(
                fixture.ClipsRoot,
                "Valorant 2026.08.28 - 12.05.06.07.DVR.mp4",
                [40, 41, 42, 43])
            .GetAwaiter().GetResult();
        AssertThrows<InvalidDataException>(() => fixture.Observer.ObserveAsync(
                CandidateAsync(fixture.Settings, path).GetAwaiter().GetResult(),
                CancellationToken.None).AsTask().GetAwaiter().GetResult(),
            "A corrupt shadow store must be rejected instead of overwritten.");
        Assert(File.Exists(path),
            "Rejecting corrupt shadow state must never modify the source clip.");
    }

    private static Fixture CreateFixture(
        string root,
        bool upload,
        ClipCaptureSource source,
        Action<WatchState>? configureState = null)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var routing = Directory.CreateDirectory(Path.Combine(root, "routing")).FullName;
        var settings = Settings(clips, upload, source);
        var state = State(clips, source);
        configureState?.Invoke(state);
        var snapshot = new RoutingSnapshotStore(Path.Combine(
            routing,
            RoutingSnapshotStore.FileName));
        var markers = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            routing,
            LegacyRoutingMigrationMarkerStore.FileName));
        var coordinator = new LegacyRoutingMigrationCoordinator(snapshot, markers);
        string? legacyWebhookIdentity = null;
        string? connectionId = null;
        if (upload)
        {
            Assert(DiscordRoutingConnectionIdentity.TryCreate(
                    settings.WebhookUrl,
                    out var resolvedLegacyIdentity),
                "The watched shadow fixture Discord webhook could not be identified.");
            legacyWebhookIdentity = resolvedLegacyIdentity;
            connectionId = Fixture.DiscordConnectionId;
        }
        IReadOnlyList<string> connectionIds = connectionId is null
            ? []
            : [connectionId];
        var cutover = coordinator.ExecuteAsync(
                new LegacyRoutingMigrationInput(
                    settings,
                    state,
                    LegacyWorkerQuiesced: true,
                    connectionIds),
                Now)
            .GetAwaiter().GetResult();
        Assert(cutover.IsCommitted,
            $"The watched shadow fixture could not commit its migration route: {cutover.Status}.");
        var shadow = new RoutingWatchedShadowStore(Path.Combine(
            routing,
            RoutingWatchedShadowStore.FileName));
        var probe = new RecordingMediaProbe();
        return new Fixture(
            clips,
            settings,
            snapshot,
            markers,
            shadow,
            probe,
            new TestLegacyConnectionAuthority(legacyWebhookIdentity, connectionId),
            connectionId,
            cutover.Marker!);
    }

    private static AppSettings Settings(
        string clips,
        bool upload,
        ClipCaptureSource source) => new(
        clips,
        upload
            ? "https://discord.com/api/webhooks/123456789012345678/shadow-secret"
            : string.Empty,
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Shadow tester",
        upload,
        GlobalHotkeyBinding.DefaultDisplayText,
        source);

    private static WatchState State(string clips, ClipCaptureSource source) => new()
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

    private static async Task<RoutingWatchedFolderCandidate> CandidateAsync(
        AppSettings settings,
        string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        var hash = await ContentIdentity.ComputeSha256Async(path, CancellationToken.None);
        return RoutingWatchedFolderCandidate.Create(
            settings,
            info,
            WatchStateStore.FileKey(info),
            hash);
    }

    private static async Task<string> WriteClipAsync(
        string folder,
        string fileName,
        byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 28, 16, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private sealed class RecordingMediaProbe : IRoutingWatchedFolderMediaProbe
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(File.Exists(filePath), "The media probe received a missing source file.");
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new RoutingWatchedFolderMediaInfo(
                TimeSpan.FromSeconds(42),
                2560,
                1440));
        }
    }

    private sealed class MutatingMediaProbe(byte[] replacement) : IRoutingWatchedFolderMediaProbe
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public async Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            await File.WriteAllBytesAsync(filePath, replacement, cancellationToken);
            File.SetLastWriteTimeUtc(filePath, lastWrite);
            return new RoutingWatchedFolderMediaInfo(
                TimeSpan.FromSeconds(42),
                2560,
                1440);
        }
    }

    private sealed record Fixture(
        string ClipsRoot,
        AppSettings Settings,
        RoutingSnapshotStore Snapshots,
        LegacyRoutingMigrationMarkerStore Markers,
        RoutingWatchedShadowStore Shadow,
        RecordingMediaProbe Probe,
        IRoutingWatchedFolderLegacyConnectionAuthority ConnectionAuthority,
        string? ConnectionId,
        LegacyRoutingMigrationMarker Marker)
    {
        internal const string DiscordConnectionId =
            "discord.11111111111111111111111111111111";

        internal RoutingWatchedFolderShadowObserver Observer => CreateObserver(Probe);

        internal RoutingWatchedFolderShadowObserver CreateObserver(
            IRoutingWatchedFolderMediaProbe mediaProbe) => new(
            RoutingWatchedFolderIngestionOptions.Shadow,
            Snapshots,
            Markers,
            Shadow,
            mediaProbe,
            ConnectionAuthority,
            () => Guid.Parse("22222222-2222-2222-2222-222222222222"),
            () => Now.AddSeconds(1));
    }

    private sealed class TestLegacyConnectionAuthority(
        string? expectedLegacyIdentity,
        string? connectionId) : IRoutingWatchedFolderLegacyConnectionAuthority
    {
        public string? ResolveCurrentConnectionId(
            string legacyWebhookIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return legacyWebhookIdentity.Equals(
                expectedLegacyIdentity,
                StringComparison.Ordinal)
                ? connectionId
                : null;
        }
    }

    private sealed class TestWebhookProtector : IDiscordWebhookProtector
    {
        public string Protect(string webhookUrl, string connectionId)
        {
            RoutingValidation.RequireOpaqueId(connectionId, 128, "test Discord connection id");
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(webhookUrl));
        }

        public bool TryUnprotect(
            string protectedWebhook,
            string connectionId,
            out string webhookUrl)
        {
            webhookUrl = string.Empty;
            try
            {
                RoutingValidation.RequireOpaqueId(connectionId, 128, "test Discord connection id");
                webhookUrl = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(protectedWebhook));
                return WebhookValidation.IsDiscordWebhook(webhookUrl);
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidDataException or ArgumentException)
            {
                webhookUrl = string.Empty;
                return false;
            }
        }
    }
}

internal sealed class BlockingRoutingWatchedFolderObserver : IRoutingWatchedFolderObserver
{
    private int _calls;
    private int _cancellationObserved;
    internal int Calls => Volatile.Read(ref _calls);
    internal bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;

    public async ValueTask<RoutingWatchedFolderObservationResult> ObserveAsync(
        RoutingWatchedFolderCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _cancellationObserved, 1);
            throw;
        }
        throw new InvalidOperationException("The blocking shadow observer unexpectedly resumed.");
    }
}
