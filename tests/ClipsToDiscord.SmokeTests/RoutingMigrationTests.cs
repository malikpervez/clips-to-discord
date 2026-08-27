using System.Text.Json;
using ClipsToDiscord;

internal static class RoutingMigrationTests
{
    internal static void Run(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        AssertDiscordAndLocalOnlyPlans(testRoot);
        AssertReadinessBlocksUnsafeCutover(testRoot);
        AssertCommittedCutoverIsDurableAndIdempotent(testRoot);
        AssertPreparedCutoverResumesAfterRestart(testRoot);
        AssertRouteWrittenBeforeCommitResumesAfterRestart(testRoot);
        AssertOrphanedEquivalentRouteIsAdopted(testRoot);
        AssertCorruptMarkerFailsClosed(testRoot);
        AssertValidShapeMarkerMutationFailsClosed(testRoot);
        AssertConcurrentCutoverCommitsExactlyOnce(testRoot);
        AssertMarkerPathAndSizeAreBounded(testRoot);
    }

    private static void AssertDiscordAndLocalOnlyPlans(string root)
    {
        var clips = NewClipsRoot(root, "plan");
        var state = State(clips);
        state.KnownContentHashes.Add(Hash('a'));
        state.UploadedContentHashes.Add(Hash('b'));
        state.LocalOnlyContentHashes.Add(Hash('c'));
        var discord = LegacyRoutingMigrationPlanner.Evaluate(
            Input(Settings(clips, upload: true), state, ["discord.friends"]), At(0));
        Assert(discord.CanCommit && discord.Plan is not null,
            "A drained Discord-on legacy state should produce a cutover plan.");
        var route = discord.Plan!.Route;
        Assert(discord.Plan.Scope == LegacyRoutingCutoverScope.FutureClipsOnly &&
               route.Source == RoutingRouteSource.Migration &&
               route.Kind == RoutingRouteKind.Fallback &&
               route.Trigger == RoutingTriggerKind.AnyNewSourceClip &&
               route.Conditions.Count == 0 && route.Actions.Count == 2,
            "Discord migration must be one unconditional future-clips fallback route.");
        Assert(route.Actions[0] is
               {
                   Kind: RoutingActionKind.Deliver,
                   Destination: RoutingDestinationKind.Discord,
                   ConnectionId: "discord.friends",
                   OutputRef: RoutingOutputKind.Original,
                   Mode: RoutingDeliveryMode.Automatic
               } &&
               route.Actions[1] is
               {
                   Kind: RoutingActionKind.FileIntoLibrary,
                   LibraryArea: RoutingLibraryArea.Uploaded
               },
            "Discord-on migration must deliver the original then file it as Uploaded.");
        Assert(discord.Plan.ContentHashExclusions.Known.SequenceEqual(
                   new[] { Hash('a'), Hash('b'), Hash('c') }) &&
               discord.Plan.ContentHashExclusions.Uploaded.SequenceEqual([Hash('b')]) &&
               discord.Plan.ContentHashExclusions.LocalOnly.SequenceEqual([Hash('c')]),
            "All prior content hashes, including Local-only clips, must remain exclusions.");
        var serializedPlan = JsonSerializer.Serialize(discord.Plan);
        Assert(!serializedPlan.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase) &&
               !serializedPlan.Contains("legacy-secret-token", StringComparison.Ordinal),
            "Migration plans must not persist a webhook URL or token.");

        var rerun = LegacyRoutingMigrationPlanner.Evaluate(
            Input(Settings(clips, upload: true), state, ["discord.friends"]), At(10));
        Assert(rerun.Plan!.MigrationId == discord.Plan.MigrationId &&
               rerun.Plan.Route.RouteId == route.RouteId &&
               rerun.Plan.Route.Actions.Select(action => action.ActionId)
                   .SequenceEqual(route.Actions.Select(action => action.ActionId)),
            "Equivalent legacy inputs must produce stable migration, route, and action ids.");

        var local = LegacyRoutingMigrationPlanner.Evaluate(
            Input(Settings(clips, upload: false), state,
                ["an.unused.connection", "another.unused.connection"]), At(0));
        Assert(local.CanCommit && local.Plan!.Mode == LegacyRoutingMode.LocalOnly &&
               local.Plan.Route.Actions.Count == 1 &&
               local.Plan.Route.Actions[0] is
               {
                   Kind: RoutingActionKind.FileIntoLibrary,
                   LibraryArea: RoutingLibraryArea.LocalOnly,
                   ConnectionId: null
            },
            "Discord-off migration must be connectionless and file only into Local only.");
        var localPlan = local.Plan!;
        Assert(!JsonSerializer.Serialize(localPlan.Route).Contains(Hash('c'),
                StringComparison.Ordinal),
            "A migrated Local-only route must never reach back into pre-existing Local-only clips.");
    }

    private static void AssertReadinessBlocksUnsafeCutover(string root)
    {
        var clips = NewClipsRoot(root, "readiness");
        AssertStatus(Input(Settings(clips, true), State(clips), ["discord.friends"], quiesced: false),
            LegacyRoutingCutoverReadinessStatus.LegacyWorkerActive);

        var pendingUpload = State(clips);
        pendingUpload.PendingMoves.Add(Path.Combine(clips, "pending-upload.mp4"));
        AssertStatus(Input(Settings(clips, true), pendingUpload, ["discord.friends"]),
            LegacyRoutingCutoverReadinessStatus.PendingLegacyWork);
        var pendingLocal = State(clips);
        pendingLocal.PendingLocalOnlyMoves.Add(Path.Combine(clips, "pending-local.mp4"));
        AssertStatus(Input(Settings(clips, false), pendingLocal, []),
            LegacyRoutingCutoverReadinessStatus.PendingLegacyWork);
        var pendingEdit = State(clips);
        pendingEdit.PendingEditedUploads.Add(new PendingEditedClipDisposition
        {
            Id = Guid.NewGuid(),
            ClipsFolder = clips,
            EditedContentHash = Hash('d'),
            OriginalContentHash = Hash('e')
        });
        AssertStatus(Input(Settings(clips, true), pendingEdit, ["discord.friends"]),
            LegacyRoutingCutoverReadinessStatus.PendingLegacyWork);

        var ignoredBaseline = State(clips);
        ignoredBaseline.IgnoredFileKeys.Add("unhashable-baseline-file-key");
        AssertStatus(Input(Settings(clips, false), ignoredBaseline, []),
            LegacyRoutingCutoverReadinessStatus.UnreconciledIgnoredFiles);

        AssertStatus(Input(Settings(clips, true), State(clips), []),
            LegacyRoutingCutoverReadinessStatus.MissingDiscordConnection);
        AssertStatus(Input(Settings(clips, true), State(clips), ["discord.one", "discord.two"]),
            LegacyRoutingCutoverReadinessStatus.AmbiguousDiscordConnection);
        AssertStatus(Input(Settings(clips, true), State(clips), ["https://not-opaque"]),
            LegacyRoutingCutoverReadinessStatus.InvalidDiscordConnection);
        AssertStatus(Input(Settings(clips, false) with { CompressionTargetMb = 0 }, State(clips), []),
            LegacyRoutingCutoverReadinessStatus.InvalidLegacySettings);

        var invalidHash = State(clips);
        invalidHash.KnownContentHashes.Add("not-a-sha256");
        AssertStatus(Input(Settings(clips, false), invalidHash, []),
            LegacyRoutingCutoverReadinessStatus.InvalidWatchState);
        var conflictingHash = State(clips);
        conflictingHash.UploadedContentHashes.Add(Hash('f'));
        conflictingHash.LocalOnlyContentHashes.Add(Hash('f'));
        AssertStatus(Input(Settings(clips, false), conflictingHash, []),
            LegacyRoutingCutoverReadinessStatus.InvalidWatchState);

        var oversizedExclusions = State(clips);
        for (var index = 0;
             index <= LegacyRoutingMigrationPlanner.MaximumPersistedHashOccurrences / 2;
             index++)
        {
            oversizedExclusions.UploadedContentHashes.Add(
                index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));
        }
        AssertStatus(Input(Settings(clips, false), oversizedExclusions, []),
            LegacyRoutingCutoverReadinessStatus.InvalidWatchState);
    }

    private static void AssertCommittedCutoverIsDurableAndIdempotent(string root)
    {
        var test = Path.Combine(root, "committed");
        var clips = NewClipsRoot(test, "clips");
        var state = State(clips);
        state.LocalOnlyContentHashes.Add(Hash('c'));
        var input = Input(Settings(clips, true), state, ["discord.friends"]);
        var (coordinator, routeStore, markerStore) = Stores(test);
        var first = coordinator.ExecuteAsync(input, At(0)).GetAwaiter().GetResult();
        Assert(first.Status == LegacyRoutingCutoverResultStatus.Committed && first.IsCommitted,
            "The first safe cutover should commit.");
        var routes = routeStore.Load();
        var marker = markerStore.Load();
        Assert(routes.LoadedFromDisk && routes.Document!.Routes.Count == 1 &&
               marker.LoadedFromDisk &&
               marker.Document!.Phase == LegacyRoutingMigrationMarkerPhase.Committed &&
               marker.Document.Generation == 2,
            "A committed cutover needs one durable fallback route and a committed marker.");
        var markerDocument = marker.Document!;
        Assert(markerDocument.ContentHashExclusions.Known.Contains(Hash('c')) &&
               markerDocument.ContentHashExclusions.LocalOnly.Contains(Hash('c')),
            "The committed marker must retain the Local-only exclusion without routing it.");
        var markerText = File.ReadAllText(markerStore.Path);
        Assert(!markerText.Contains("api/webhooks", StringComparison.OrdinalIgnoreCase) &&
               !markerText.Contains("legacy-secret-token", StringComparison.Ordinal),
            "The durable marker must never contain the legacy Discord credential.");

        var restart = new LegacyRoutingMigrationCoordinator(
            new RoutingSnapshotStore(routeStore.Path),
            new LegacyRoutingMigrationMarkerStore(markerStore.Path));
        var second = restart.ExecuteAsync(input, At(5)).GetAwaiter().GetResult();
        Assert(second.Status == LegacyRoutingCutoverResultStatus.AlreadyCommitted &&
               second.Marker!.MigrationId == first.Marker!.MigrationId &&
               new RoutingSnapshotStore(routeStore.Path).Load().Document!.Routes.Count == 1,
            "A restarted equivalent migration must return the existing cutover, not duplicate it.");

        var currentRoutes = routeStore.Load().Document!;
        var migrationRoute = currentRoutes.Routes.Single();
        var userRoute = migrationRoute with
        {
            RouteId = Guid.NewGuid(),
            Name = "Battlefield highlights",
            Priority = 1,
            Source = RoutingRouteSource.User,
            Kind = RoutingRouteKind.Specific,
            Conditions =
            [
                new RoutingCondition(
                    Guid.NewGuid(),
                    RoutingConditionField.Game,
                    RoutingConditionOperator.Equals,
                    "Battlefield 6")
            ],
            Actions =
            [
                migrationRoute.Actions[^1] with { ActionId = Guid.NewGuid() }
            ]
        };
        var expandedRoutes = RoutingSnapshotModel.ReplaceRoutes(
            currentRoutes,
            [migrationRoute, userRoute],
            At(6));
        _ = routeStore.SaveAsync(expandedRoutes, currentRoutes.Generation)
            .GetAwaiter().GetResult();
        var afterUserRoute = restart.ExecuteAsync(input, At(7)).GetAwaiter().GetResult();
        Assert(afterUserRoute.Status == LegacyRoutingCutoverResultStatus.AlreadyCommitted &&
               routeStore.Load().Document!.Routes.Count == 2,
            "A committed cutover must remain valid after legitimate user routes are added.");
    }

    private static void AssertPreparedCutoverResumesAfterRestart(string root)
    {
        var test = Path.Combine(root, "prepared-only");
        var clips = NewClipsRoot(test, "clips");
        var input = Input(Settings(clips, false), State(clips), []);
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(input, At(0));
        var (_, routeStore, markerStore) = Stores(test);
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(readiness.Plan!, At(0));
        _ = markerStore.SaveAsync(prepared, 0).GetAwaiter().GetResult();

        var restarted = new LegacyRoutingMigrationCoordinator(
            new RoutingSnapshotStore(routeStore.Path),
            new LegacyRoutingMigrationMarkerStore(markerStore.Path));
        var result = restarted.ExecuteAsync(input, At(1)).GetAwaiter().GetResult();
        Assert(result.Status == LegacyRoutingCutoverResultStatus.Committed &&
               routeStore.Load().Document!.Routes.Single().Actions.Single().LibraryArea ==
               RoutingLibraryArea.LocalOnly,
            "Restart must resume a prepared marker without inventing a different route.");
    }

    private static void AssertRouteWrittenBeforeCommitResumesAfterRestart(string root)
    {
        var test = Path.Combine(root, "route-written");
        var clips = NewClipsRoot(test, "clips");
        var input = Input(Settings(clips, true), State(clips), ["discord.friends"]);
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(input, At(0));
        var (_, routeStore, markerStore) = Stores(test);
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(readiness.Plan!, At(0));
        _ = markerStore.SaveAsync(prepared, 0).GetAwaiter().GetResult();
        _ = routeStore.SaveAsync(new RoutingSnapshotDocument(
                RoutingSnapshotStore.CurrentSchemaVersion,
                1,
                [prepared.Route],
                prepared.Route.CreatedUtc,
                prepared.Route.ModifiedUtc), 0)
            .GetAwaiter().GetResult();

        var result = new LegacyRoutingMigrationCoordinator(routeStore, markerStore)
            .ExecuteAsync(input, At(2)).GetAwaiter().GetResult();
        Assert(result.Status == LegacyRoutingCutoverResultStatus.Committed &&
               routeStore.Load().Document!.Routes.Count == 1,
            "A crash after route persistence must finish the same marker without a second route.");

        var changedState = State(clips);
        changedState.KnownContentHashes.Add(Hash('9'));
        var conflictTest = Path.Combine(root, "prepared-conflict");
        var (_, conflictRoutes, conflictMarkers) = Stores(conflictTest);
        _ = conflictMarkers.SaveAsync(
            LegacyRoutingMigrationMarkerModel.CreatePrepared(readiness.Plan!, At(0)), 0)
            .GetAwaiter().GetResult();
        var conflict = new LegacyRoutingMigrationCoordinator(conflictRoutes, conflictMarkers)
            .ExecuteAsync(Input(Settings(clips, true), changedState, ["discord.friends"]), At(2))
            .GetAwaiter().GetResult();
        Assert(conflict.Status == LegacyRoutingCutoverResultStatus.StateConflict &&
               conflictRoutes.Load().Status == RoutingDocumentLoadStatus.Missing,
            "A prepared transaction must not commit against changed legacy exclusion state.");
    }

    private static void AssertOrphanedEquivalentRouteIsAdopted(string root)
    {
        var test = Path.Combine(root, "route-only");
        var clips = NewClipsRoot(test, "clips");
        var input = Input(Settings(clips, false), State(clips), []);
        var plan = LegacyRoutingMigrationPlanner.Evaluate(input, At(0)).Plan!;
        var (_, routeStore, markerStore) = Stores(test);
        _ = routeStore.SaveAsync(new RoutingSnapshotDocument(
                RoutingSnapshotStore.CurrentSchemaVersion,
                1,
                [plan.Route],
                plan.Route.CreatedUtc,
                plan.Route.ModifiedUtc), 0)
            .GetAwaiter().GetResult();

        var result = new LegacyRoutingMigrationCoordinator(routeStore, markerStore)
            .ExecuteAsync(input, At(3)).GetAwaiter().GetResult();
        Assert(result.Status == LegacyRoutingCutoverResultStatus.Committed &&
               markerStore.Load().Document!.Phase == LegacyRoutingMigrationMarkerPhase.Committed &&
               routeStore.Load().Document!.Routes.Count == 1,
            "A lost pre-commit marker must adopt an exact deterministic route, not duplicate it.");
    }

    private static void AssertCorruptMarkerFailsClosed(string root)
    {
        var test = Path.Combine(root, "corrupt-marker");
        var clips = NewClipsRoot(test, "clips");
        var (_, routeStore, markerStore) = Stores(test);
        Directory.CreateDirectory(Path.GetDirectoryName(markerStore.Path)!);
        File.WriteAllText(markerStore.Path, "{ definitely not json");
        var result = new LegacyRoutingMigrationCoordinator(routeStore, markerStore)
            .ExecuteAsync(Input(Settings(clips, false), State(clips), []), At(0))
            .GetAwaiter().GetResult();
        Assert(result.Status == LegacyRoutingCutoverResultStatus.MarkerUnavailable &&
               markerStore.Load().Status == RoutingDocumentLoadStatus.Corrupt &&
               routeStore.Load().Status == RoutingDocumentLoadStatus.Missing,
            "A corrupt marker must block migration without being overwritten or creating a route.");
    }

    private static void AssertValidShapeMarkerMutationFailsClosed(string root)
    {
        var test = Path.Combine(root, "valid-shape-marker-mutation");
        var clips = NewClipsRoot(test, "clips");
        var state = State(clips);
        state.KnownContentHashes.Add(Hash('a'));
        var input = Input(Settings(clips, false), state, []);
        var (coordinator, _, markerStore) = Stores(test);
        var committed = coordinator.ExecuteAsync(input, At(0)).GetAwaiter().GetResult();
        Assert(committed.IsCommitted,
            "The marker mutation fixture must begin from a committed cutover.");
        var text = File.ReadAllText(markerStore.Path);
        var mutated = text.Replace(Hash('a'), Hash('b'), StringComparison.Ordinal);
        Assert(mutated != text, "The marker mutation fixture did not alter an exclusion.");
        File.WriteAllText(markerStore.Path, mutated);
        Assert(markerStore.Load().Status == RoutingDocumentLoadStatus.Invalid,
            "A valid-shape exclusion mutation must fail the durable payload fingerprint.");
    }

    private static void AssertConcurrentCutoverCommitsExactlyOnce(string root)
    {
        var test = Path.Combine(root, "concurrent");
        var clips = NewClipsRoot(test, "clips");
        var input = Input(Settings(clips, true), State(clips), ["discord.friends"]);
        var routePath = Path.Combine(test, "routing", RoutingSnapshotStore.FileName);
        var markerPath = Path.Combine(test, "routing", LegacyRoutingMigrationMarkerStore.FileName);
        LegacyRoutingMigrationCoordinator NewCoordinator() => new(
            new RoutingSnapshotStore(routePath),
            new LegacyRoutingMigrationMarkerStore(markerPath));
        var starts = new ManualResetEventSlim(false);
        var firstTask = Task.Run(async () =>
        {
            starts.Wait();
            return await NewCoordinator().ExecuteAsync(input, At(0));
        });
        var secondTask = Task.Run(async () =>
        {
            starts.Wait();
            return await NewCoordinator().ExecuteAsync(input, At(0));
        });
        starts.Set();
        Task.WaitAll(firstTask, secondTask);
        Assert(firstTask.Result.IsCommitted && secondTask.Result.IsCommitted &&
               new RoutingSnapshotStore(routePath).Load().Document!.Routes.Count == 1 &&
               new LegacyRoutingMigrationMarkerStore(markerPath).Load().Document!.Phase ==
               LegacyRoutingMigrationMarkerPhase.Committed,
            "Concurrent cutover attempts must converge on one route and one committed marker.");
    }

    private static void AssertMarkerPathAndSizeAreBounded(string root)
    {
        AssertThrows<ArgumentException>(() =>
                _ = new LegacyRoutingMigrationMarkerStore(
                    Path.Combine(root, "wrong-marker-name.json")),
            "The marker store must reject a non-canonical marker filename.");
        var path = Path.Combine(root, "oversized", LegacyRoutingMigrationMarkerStore.FileName);
        var store = new LegacyRoutingMigrationMarkerStore(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[LegacyRoutingMigrationMarkerStore.MaximumDocumentBytes + 1]);
        Assert(store.Load().Status == RoutingDocumentLoadStatus.Invalid,
            "An oversized cutover marker must be rejected before JSON parsing.");
    }

    private static void AssertStatus(
        LegacyRoutingMigrationInput input,
        LegacyRoutingCutoverReadinessStatus expected)
    {
        var actual = LegacyRoutingMigrationPlanner.Evaluate(input, At(0));
        Assert(actual.Status == expected && !actual.CanCommit && actual.Plan is null,
            $"Expected cutover readiness {expected}, received {actual.Status}.");
    }

    private static (LegacyRoutingMigrationCoordinator Coordinator,
        RoutingSnapshotStore Routes, LegacyRoutingMigrationMarkerStore Markers) Stores(string root)
    {
        var routing = Path.Combine(root, "routing");
        var routes = new RoutingSnapshotStore(Path.Combine(routing, RoutingSnapshotStore.FileName));
        var markers = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(routing, LegacyRoutingMigrationMarkerStore.FileName));
        return (new LegacyRoutingMigrationCoordinator(routes, markers), routes, markers);
    }

    private static LegacyRoutingMigrationInput Input(
        AppSettings settings,
        WatchState state,
        IReadOnlyList<string> connectionIds,
        bool quiesced = true) => new(settings, state, quiesced, connectionIds);

    private static AppSettings Settings(string clips, bool upload) => new(
        clips,
        "https://discord.com/api/webhooks/123456789012345678/legacy-secret-token",
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Migration tester",
        upload,
        GlobalHotkeyBinding.DefaultDisplayText,
        ClipCaptureSource.SteelSeriesGg);

    private static WatchState State(string clips) => new()
    {
        Version = 4,
        ClipsFolder = clips,
        CaptureSource = ClipCaptureSource.SteelSeriesGg,
        KnownContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        UploadedContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        LocalOnlyContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        IgnoredFileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PendingMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PendingLocalOnlyMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        PendingEditedUploads = []
    };

    private static string NewClipsRoot(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private static string Hash(char value) => new string(value, 64).ToUpperInvariant();

    private static DateTimeOffset At(int minutes) =>
        new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

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
