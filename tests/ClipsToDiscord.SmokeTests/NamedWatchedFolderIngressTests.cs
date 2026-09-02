using ClipsToDiscord;

internal static class NamedWatchedFolderIngressTests
{
    private const string LegacyConnectionId = "discord.friends";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 2, 18, 0, 0, TimeSpan.Zero);
    private static readonly RoutingWatchedFolderRuntimeHostOptions FastRuntimeOptions = new(
        PollInterval: TimeSpan.FromMilliseconds(5),
        ErrorRetryInterval: TimeSpan.FromMilliseconds(5),
        MaximumErrorRetryInterval: TimeSpan.FromMilliseconds(5),
        MaximumConsecutiveLoopFailures: 2,
        MaximumCandidatesPerScan: 100);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        AssertRuntimeRootConflictsAreIsolated(Path.Combine(root, "root-isolation"));
        await AssertRuntimePersistsMissingRootAttentionAsync(
            Path.Combine(root, "missing-root"));
        await AssertRuntimePersistsReplacedRootAttentionAsync(
            Path.Combine(root, "replaced-root"));
        await AssertRuntimeQuarantinesOverlapWithoutSuppressingPeersAsync(
            Path.Combine(root, "overlap-health"));
        await AssertIngressRestartAndRootResolutionAsync(
            Path.Combine(root, "ingress-and-resolution"));
    }

    private static async Task AssertRuntimePersistsMissingRootAttentionAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var missing = fixture.Sources[0];
        Directory.Delete(missing.Record.CanonicalRoot, recursive: true);

        await RunRuntimeUntilAsync(
            fixture,
            () => CurrentSource(fixture, missing.Record.SourceId) is
            {
                Health: RoutingInputSourceHealth.NeedsAttention,
                AttentionReason: RoutingInputSourceAttentionReason.MetadataInvalid
            });

        var persisted = CurrentSource(fixture, missing.Record.SourceId);
        var healthyPeer = CurrentSource(fixture, fixture.Sources[1].Record.SourceId);
        Assert(persisted.Health == RoutingInputSourceHealth.NeedsAttention &&
               persisted.AttentionReason == RoutingInputSourceAttentionReason.MetadataInvalid &&
               healthyPeer.Health == RoutingInputSourceHealth.Ready,
            "A missing named recorder folder must durably enter NeedsAttention/MetadataInvalid without changing a healthy peer.");
    }

    private static async Task AssertRuntimePersistsReplacedRootAttentionAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var replaced = fixture.Sources[0];
        Directory.Delete(replaced.Record.CanonicalRoot, recursive: true);
        Directory.CreateDirectory(replaced.Record.CanonicalRoot);

        await RunRuntimeUntilAsync(
            fixture,
            () => CurrentSource(fixture, replaced.Record.SourceId) is
            {
                Health: RoutingInputSourceHealth.NeedsAttention,
                AttentionReason: RoutingInputSourceAttentionReason.RootAuthorityChanged
            });

        var persisted = CurrentSource(fixture, replaced.Record.SourceId);
        Assert(persisted.Health == RoutingInputSourceHealth.NeedsAttention &&
               persisted.AttentionReason ==
               RoutingInputSourceAttentionReason.RootAuthorityChanged,
            "Recreating a named recorder at the same path must durably enter NeedsAttention/RootAuthorityChanged.");

        AssertGalleryIncludesOnlyTrustedNamedRoots(fixture, replaced.Record.SourceId);
    }

    private static async Task AssertRuntimeQuarantinesOverlapWithoutSuppressingPeersAsync(
        string root)
    {
        using var fixture = await Fixture.CreateAsync(root);
        var overlappingRoot = Directory.CreateDirectory(Path.Combine(
            fixture.LegacyRoot,
            "nested-named-source")).FullName;
        var added = await fixture.Catalog.AddVerifiedWatchedFolderAsync(
            "Overlapping SteelSeries source",
            RoutingInputSourceKind.SteelSeriesGg,
            overlappingRoot,
            RoutingWatchedSourceRootIdentity.Create(
                RoutingInputSourceKind.SteelSeriesGg,
                overlappingRoot),
            enabled: true,
            now: Now.AddMinutes(10));
        var overlapping = added.Source ?? throw new InvalidOperationException(
            "The runtime overlap fixture could not register its source.");

        await RunRuntimeUntilAsync(
            fixture,
            () => CurrentSource(fixture, overlapping.SourceId) is
            {
                Health: RoutingInputSourceHealth.NeedsAttention,
                AttentionReason: RoutingInputSourceAttentionReason.MetadataInvalid
            });

        var snapshot = fixture.Catalog.Inspect();
        Assert(snapshot.IsUsable &&
               snapshot.Sources.Single(source => source.SourceId == overlapping.SourceId) is
               {
                   Enabled: true,
                   Health: RoutingInputSourceHealth.NeedsAttention,
                   AttentionReason: RoutingInputSourceAttentionReason.MetadataInvalid
               } &&
               fixture.Sources.All(expected => snapshot.Sources.Single(source =>
                       source.SourceId == expected.Record.SourceId).Health ==
                   RoutingInputSourceHealth.Ready),
            "An overlapping named root must be quarantined durably while every unrelated recorder source remains ready.");
    }

    private static async Task RunRuntimeUntilAsync(
        Fixture fixture,
        Func<bool> condition)
    {
        var captureBinding = RoutingCaptureLibraryBindingModel.Create(fixture.CaptureRoot);
        using var executor = new RoutingOutboxExecutor(
            fixture.Outbox,
            RejectingProvider.Instance,
            RejectingResolver.Instance,
            RejectingFiler.Instance,
            () => fixture.Gate.Enabled,
            maximumSideEffectsPerRun: 4,
            maximumRecoveryInspectionsPerRun: 4,
            captureLibraryPermit: new RoutingCaptureLibraryPermit(
                captureBinding,
                () => captureBinding));
        await using var host = new RoutingNamedWatchedFolderRuntimeHost(
            fixture.LegacyRoot,
            fixture.CaptureRoot,
            fixture.RoutingLease,
            fixture.Gate,
            fixture.Catalog,
            fixture.Ingress,
            executor,
            FastRuntimeOptions);
        await host.StartAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert(condition(),
                "The named watched-folder runtime did not persist the expected source health transition.");
        }
        finally
        {
            await host.StopAsync();
        }
        Assert(host.Failure is null,
            "A per-source named watched-folder failure must not stop the shared runtime host.");
    }

    private static RoutingInputSourceRecord CurrentSource(Fixture fixture, string sourceId)
    {
        var snapshot = fixture.Catalog.Inspect();
        Assert(snapshot.IsUsable,
            "The named watched-source catalog became unavailable during runtime health verification.");
        return snapshot.Sources.Single(source => source.SourceId == sourceId);
    }

    private static void AssertGalleryIncludesOnlyTrustedNamedRoots(
        Fixture fixture,
        string replacedSourceId)
    {
        var healthy = fixture.Sources.Single(source =>
            source.Record.SourceId != replacedSourceId);
        var healthyGame = Directory.CreateDirectory(Path.Combine(
            healthy.Record.CanonicalRoot,
            "uploaded",
            "Healthy Game")).FullName;
        File.WriteAllBytes(Path.Combine(healthyGame, "healthy.mp4"), [31, 32, 33, 34]);
        var replaced = CurrentSource(fixture, replacedSourceId);
        var replacedGame = Directory.CreateDirectory(Path.Combine(
            replaced.CanonicalRoot,
            "uploaded",
            "Replaced Game")).FullName;
        File.WriteAllBytes(Path.Combine(replacedGame, "untrusted.mp4"), [41, 42, 43, 44]);

        var trustProbe = typeof(SettingsForm).GetMethod(
            "IsTrustedNamedGallerySource",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static) ??
            throw new InvalidOperationException(
                "The named Gallery trust probe could not be located.");
        bool IsTrusted(RoutingInputSourceRecord source) =>
            trustProbe.Invoke(null, [source]) is true;
        var catalog = fixture.Catalog.Inspect();
        var trustedRoots = catalog.Sources
            .Where(source => source.Kind is RoutingInputSourceKind.SteelSeriesGg or
                RoutingInputSourceKind.Nvidia)
            .Where(IsTrusted)
            .Select(source => new GalleryExternalSourceRoot(
                source.CanonicalRoot,
                source.Kind == RoutingInputSourceKind.Nvidia
                    ? GalleryClipSource.Nvidia
                    : GalleryClipSource.SteelSeriesGg))
            .ToArray();
        var gallery = GalleryCatalog.Scan(
            fixture.LegacyRoot,
            CancellationToken.None,
            additionalExternalRoots: trustedRoots);

        Assert(trustedRoots.Any(source => source.CanonicalRoot.Equals(
                   healthy.Record.CanonicalRoot,
                   StringComparison.OrdinalIgnoreCase)) &&
               trustedRoots.All(source => !source.CanonicalRoot.Equals(
                   replaced.CanonicalRoot,
                   StringComparison.OrdinalIgnoreCase)) &&
               gallery.Games.SelectMany(game => game.Clips)
                   .Any(clip => clip.FileName == "healthy.mp4") &&
               gallery.Games.SelectMany(game => game.Clips)
                   .All(clip => clip.FileName != "untrusted.mp4"),
            "Gallery must include archives from a native-identity-verified named source and exclude a replaced root even when that path now contains plausible archives.");
    }

    private static void AssertRuntimeRootConflictsAreIsolated(string root)
    {
        var legacy = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
        var capture = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        var xbox = Directory.CreateDirectory(Path.Combine(root, "xbox")).FullName;
        var healthySteel = Directory.CreateDirectory(Path.Combine(root, "steel-ok")).FullName;
        var healthyNvidia = Directory.CreateDirectory(Path.Combine(root, "nvidia-ok")).FullName;
        var legacyConflict = Directory.CreateDirectory(
            Path.Combine(legacy, "nested-recorder")).FullName;
        var pairRoot = Directory.CreateDirectory(Path.Combine(root, "pair")).FullName;
        var pairChild = Directory.CreateDirectory(Path.Combine(pairRoot, "nested")).FullName;
        var disabledXbox = Directory.CreateDirectory(Path.Combine(root, "xbox-disabled")).FullName;
        var disabledXboxChild = Directory.CreateDirectory(
            Path.Combine(disabledXbox, "allowed-while-disabled")).FullName;

        var active = new[]
        {
            Source("source.91000000000000000000000000000001", RoutingInputSourceKind.SteelSeriesGg, healthySteel),
            Source("source.92000000000000000000000000000002", RoutingInputSourceKind.Nvidia, healthyNvidia),
            Source("source.93000000000000000000000000000003", RoutingInputSourceKind.SteelSeriesGg, legacyConflict),
            Source("source.94000000000000000000000000000004", RoutingInputSourceKind.SteelSeriesGg, pairRoot),
            Source("source.95000000000000000000000000000005", RoutingInputSourceKind.Nvidia, pairChild),
            Source("source.96000000000000000000000000000006", RoutingInputSourceKind.SteelSeriesGg, disabledXboxChild)
        };
        var enabledXbox = Source(
            "source.97000000000000000000000000000007",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            xbox);
        var disabledXboxSource = Source(
            "source.98000000000000000000000000000008",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            disabledXbox) with { Enabled = false };

        var conflicts = RoutingNamedWatchedFolderRuntimeHost.FindConflictedSourceIds(
            legacy,
            capture,
            active,
            [.. active, enabledXbox, disabledXboxSource]);

        Assert(conflicts.SetEquals([
                   active[2].SourceId,
                   active[3].SourceId,
                   active[4].SourceId
               ]) &&
               !conflicts.Contains(active[0].SourceId) &&
               !conflicts.Contains(active[1].SourceId) &&
               !conflicts.Contains(active[5].SourceId),
            "A legacy/capture/Xbox or pairwise named-root conflict must quarantine only the exact conflicted source ids; unrelated sources and a disabled Xbox root must not be suppressed.");
    }

    private static async Task AssertIngressRestartAndRootResolutionAsync(string root)
    {
        using var fixture = await Fixture.CreateAsync(root);

        foreach (var source in fixture.Sources)
        {
            var excluded = await fixture.Ingress.AdmitPathAsync(
                source.Record,
                source.Adapter,
                source.ExistingPath);
            Assert(excluded.Status == RoutingWatchedIngressStatus.Excluded &&
                   excluded.SourceClipId is null && excluded.PlanId is null,
                $"The immutable from-now baseline must exclude the {source.Kind} clip that existed when the source was registered.");
        }

        var planned = new List<(NamedSource Source, RoutingWatchedIngressResult Result)>();
        foreach (var source in fixture.Sources)
        {
            var newPath = source.CreateNewClip();
            var result = await fixture.Ingress.AdmitPathAsync(
                source.Record,
                source.Adapter,
                newPath);
            Assert(result.Status == RoutingWatchedIngressStatus.Planned &&
                   result.SourceClipId is not null && result.PlanId is not null &&
                   File.Exists(newPath),
                $"A new {source.Kind} clip under an arbitrary configured root must be planned without moving or deleting the original during admission.");
            planned.Add((source, result));
        }

        var rootResolver = new RoutingWatchedSourceRootResolver(
            fixture.LegacyRoot,
            fixture.Catalog);
        foreach (var item in planned)
        {
            var loaded = fixture.Journals.Load(item.Result.SourceClipId!);
            var journal = loaded.Document ?? throw new InvalidOperationException(
                "The named watched-source journal was not persisted.");
            var disposition = journal.FrozenPlan?.FileDisposition ??
                              throw new InvalidOperationException(
                                  "The named watched-source route did not freeze its Library action.");
            var resolvedRoot = rootResolver.Resolve(journal);
            var destination = WatchedFolderLibraryLayout.GetDestinationPath(
                resolvedRoot,
                journal,
                disposition,
                createDirectories: false);
            Assert(journal.SourceConnectionId == item.Source.Record.SourceId &&
                   journal.CaptureSource == item.Source.CaptureSource &&
                   journal.FrozenPlan!.MatchedRouteIds.SequenceEqual(
                       [item.Source.RouteId]) &&
                   resolvedRoot.Equals(
                       item.Source.Record.CanonicalRoot,
                       StringComparison.OrdinalIgnoreCase) &&
                   IsWithinRoot(
                       item.Source.Record.CanonicalRoot,
                       destination) &&
                   !IsWithinRoot(fixture.LegacyRoot, destination),
                $"A {item.Source.Kind} occurrence must retain its exact source id/route and resolve any archive operation only inside that source's own root.");

            var wrongAuthority = journal with
            {
                SourceConnectionId = fixture.Sources.Single(other =>
                    other.Record.SourceId != item.Source.Record.SourceId).Record.SourceId
            };
            AssertThrows<InvalidDataException>(() => rootResolver.Resolve(wrongAuthority),
                "Changing a journal's source id must not redirect its artifact/archive root to another configured recorder.");
        }

        var restarted = fixture.CreateIngress();
        var recovered = await restarted.ReconcileAllAsync();
        var outboxBeforeRepeat = RequireOutbox(fixture.Outbox);
        foreach (var item in planned)
        {
            var repeated = await restarted.AdmitPathAsync(
                item.Source.Record,
                item.Source.Adapter,
                item.Source.NewPath!);
            Assert(repeated.Status == RoutingWatchedIngressStatus.AlreadyPlanned &&
                   repeated.SourceClipId == item.Result.SourceClipId &&
                   repeated.PlanId == item.Result.PlanId,
                "Restart admission must reconcile the immutable named-source journal instead of creating another plan.");
        }
        var outboxAfterRepeat = RequireOutbox(fixture.Outbox);
        Assert(recovered.Count(result =>
                   result.Status == RoutingWatchedIngressStatus.AlreadyPlanned) == planned.Count &&
               outboxAfterRepeat.Plans.Count == outboxBeforeRepeat.Plans.Count &&
               outboxAfterRepeat.FileDispositions.Count ==
               outboxBeforeRepeat.FileDispositions.Count,
            "Named-source restart reconciliation must be idempotent and must not duplicate plans or file dispositions.");

        var disabledSource = fixture.Sources[1];
        var disabled = await fixture.Catalog.SetEnabledAsync(
            disabledSource.Record.SourceId,
            enabled: false,
            now: Now.AddMinutes(5));
        Assert(disabled.Status == RoutingInputSourceMutationStatus.Disabled,
            "The isolation test could not disable its NVIDIA source.");
        await AssertThrowsAsync<InvalidDataException>(() => restarted.AdmitPathAsync(
                disabledSource.Record,
                disabledSource.Adapter,
                disabledSource.NewPath!),
            "A disabled named source must fail closed at ingress.");

        var healthySource = fixture.Sources[0];
        var nextHealthyPath = healthySource.CreateNewClip("second-new.mp4");
        var healthyAfterPeerFailure = await restarted.AdmitPathAsync(
            healthySource.Record,
            healthySource.Adapter,
            nextHealthyPath);
        Assert(healthyAfterPeerFailure.Status == RoutingWatchedIngressStatus.Planned &&
               File.Exists(nextHealthyPath),
            "Disabling or breaking one named source must not prevent another configured source from admitting new clips.");
    }

    private static RoutingOutboxDocument RequireOutbox(RoutingOutboxStore store)
    {
        var loaded = store.Load();
        return loaded.Document ?? throw new InvalidOperationException(
            $"The named watched-source outbox was unavailable ({loaded.Status}).");
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static RoutingInputSourceRecord Source(
        string sourceId,
        RoutingInputSourceKind kind,
        string root) => new(
        sourceId,
        Revision: 1,
        kind.ToString(),
        kind,
        Path.GetFullPath(root),
        Enabled: true,
        Retired: false,
        RoutingInputSourceHealth.Ready,
        RoutingInputSourceAttentionReason.None,
        new string(kind == RoutingInputSourceKind.Nvidia ? 'b' : 'a', 64),
        TimeZoneInfo.Utc.Id,
        Now,
        Now);

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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ClipProcessingOwnershipLease _routingLease;

        private Fixture(
            string legacyRoot,
            string captureRoot,
            RoutingInputSourceCatalog catalog,
            IReadOnlyList<NamedSource> sources,
            RoutingSnapshotStore snapshots,
            RoutingWatchedSourceJournalStore journals,
            RoutingOutboxStore outbox,
            RoutingNamedWatchedBaselineStore baselines,
            RoutingRuntimeFeatureGate gate,
            ClipProcessingOwnershipLease routingLease)
        {
            LegacyRoot = legacyRoot;
            CaptureRoot = captureRoot;
            Catalog = catalog;
            Sources = sources;
            Snapshots = snapshots;
            Journals = journals;
            Outbox = outbox;
            Baselines = baselines;
            Gate = gate;
            _routingLease = routingLease;
            Ingress = CreateIngress();
        }

        internal string LegacyRoot { get; }
        internal string CaptureRoot { get; }
        internal ClipProcessingOwnershipLease RoutingLease => _routingLease;
        internal RoutingInputSourceCatalog Catalog { get; }
        internal IReadOnlyList<NamedSource> Sources { get; }
        internal RoutingSnapshotStore Snapshots { get; }
        internal RoutingWatchedSourceJournalStore Journals { get; }
        internal RoutingOutboxStore Outbox { get; }
        internal RoutingNamedWatchedBaselineStore Baselines { get; }
        internal RoutingRuntimeFeatureGate Gate { get; }
        internal RoutingNamedWatchedFolderIngress Ingress { get; }

        internal RoutingNamedWatchedFolderIngress CreateIngress() => new(
            Snapshots,
            Journals,
            new RoutingWatchedJournalFactory(
                new FixedMediaProbe(),
                createPlanId: Guid.NewGuid,
                utcNow: () => Now.AddMinutes(2)),
            Baselines,
            Catalog,
            new RoutingPlanCommitter(Outbox, Gate),
            Gate);

        internal static async Task<Fixture> CreateAsync(string root)
        {
            Directory.CreateDirectory(root);
            var legacyRoot = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
            var captureRoot = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
            var stateRoot = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
            var steelRoot = Directory.CreateDirectory(
                Path.Combine(root, "Drive-D", "custom-steelseries-root")).FullName;
            var nvidiaRoot = Directory.CreateDirectory(
                Path.Combine(root, "Drive-E", "custom-nvidia-root")).FullName;
            var steelExisting = WriteClip(
                steelRoot,
                RoutingInputSourceKind.SteelSeriesGg,
                "Baseline Game",
                "existing-steel.mp4",
                [1, 2, 3, 4]);
            var nvidiaExisting = WriteClip(
                nvidiaRoot,
                RoutingInputSourceKind.Nvidia,
                "Baseline Game",
                "existing-nvidia.mp4",
                [5, 6, 7, 8]);

            var catalog = new RoutingInputSourceCatalog(
                new RoutingInputSourceCatalogStore(Path.Combine(
                    stateRoot,
                    RoutingInputSourceCatalogStore.FileName)),
                NoInputSourceReferences.Instance,
                new QueueGuidFactory([
                    Guid.Parse("91000000-0000-0000-0000-000000000001"),
                    Guid.Parse("92000000-0000-0000-0000-000000000002"),
                    Guid.Parse("93000000-0000-0000-0000-000000000003")
                ]).Next,
                () => Now);
            var steelAdded = await catalog.AddVerifiedWatchedFolderAsync(
                "SteelSeries arbitrary root",
                RoutingInputSourceKind.SteelSeriesGg,
                steelRoot,
                RoutingWatchedSourceRootIdentity.Create(
                    RoutingInputSourceKind.SteelSeriesGg,
                    steelRoot),
                enabled: false,
                now: Now);
            var nvidiaAdded = await catalog.AddVerifiedWatchedFolderAsync(
                "NVIDIA arbitrary root",
                RoutingInputSourceKind.Nvidia,
                nvidiaRoot,
                RoutingWatchedSourceRootIdentity.Create(
                    RoutingInputSourceKind.Nvidia,
                    nvidiaRoot),
                enabled: false,
                now: Now);
            Assert(steelAdded.Source is not null && nvidiaAdded.Source is not null,
                "The named watched-source fixture could not register both arbitrary roots.");

            var baselines = new RoutingNamedWatchedBaselineStore(Path.Combine(
                stateRoot,
                "named-watched-baselines",
                "v1"));
            _ = await baselines.EnsureAsync(
                steelAdded.Source!,
                RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg));
            _ = await baselines.EnsureAsync(
                nvidiaAdded.Source!,
                RoutingWatchedSourceAdapters.Get(ClipCaptureSource.Nvidia));

            var state = new WatchState
            {
                Version = 4,
                ClipsFolder = legacyRoot,
                CaptureSource = ClipCaptureSource.SteelSeriesGg,
                KnownContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                UploadedContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                LocalOnlyContentHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
            var stateStore = new WatchStateStore(
                Path.Combine(stateRoot, "legacy-state", "state.json"),
                Path.Combine(stateRoot, "legacy-state", ".safe-baseline-required"));
            stateStore.Save(state);
            var settings = new AppSettings(
                legacyRoot,
                "https://discord.com/api/webhooks/123456789012345678/named-source-token",
                StartWithWindows: false,
                AppSettings.DefaultCompressionTargetMb,
                "Named source test",
                UploadToDiscord: true,
                ModeToggleHotkey: string.Empty,
                ClipCaptureSource.SteelSeriesGg);
            var captureBinding = RoutingCaptureLibraryBindingModel.Create(captureRoot);
            IReadOnlyList<string> connectionIds = [LegacyConnectionId];
            var migration = LegacyRoutingMigrationPlanner.Evaluate(
                    new LegacyRoutingMigrationInput(
                        settings,
                        state,
                        LegacyWorkerQuiesced: true,
                        connectionIds,
                        captureBinding),
                    Now)
                .Plan ?? throw new InvalidOperationException(
                    "The named watched-source fixture could not create its legacy migration route.");
            var snapshots = new RoutingSnapshotStore(Path.Combine(
                stateRoot,
                RoutingSnapshotStore.FileName));
            _ = await snapshots.SaveAsync(
                new RoutingSnapshotDocument(
                    RoutingSnapshotStore.CurrentSchemaVersion,
                    Generation: 1,
                    Routes: [migration.Route],
                    CreatedUtc: Now,
                    UpdatedUtc: Now),
                expectedGeneration: 0);
            var routeManager = new RoutingRouteManager(
                snapshots,
                () => Now.AddMinutes(1),
                AlwaysAllowedMutationAuthority.Instance,
                inputSourceMembership: catalog);
            var steelRoute = await routeManager.AddAsync(NamedDraft(
                "SteelSeries route",
                steelAdded.Source!.SourceId,
                RoutingInputSourceKind.SteelSeriesGg));
            var nvidiaRoute = await routeManager.AddAsync(NamedDraft(
                "NVIDIA route",
                nvidiaAdded.Source!.SourceId,
                RoutingInputSourceKind.Nvidia));

            var steelEnabled = await catalog.SetEnabledAsync(
                steelAdded.Source.SourceId,
                expectedRevision: steelAdded.Source.Revision,
                enabled: true,
                now: Now.AddMinutes(1));
            var nvidiaEnabled = await catalog.SetEnabledAsync(
                nvidiaAdded.Source.SourceId,
                expectedRevision: nvidiaAdded.Source.Revision,
                enabled: true,
                now: Now.AddMinutes(1));
            Assert(steelEnabled.Source is { Enabled: true } &&
                   nvidiaEnabled.Source is { Enabled: true },
                "The named watched-source fixture could not enable both baselined sources.");

            var markers = new LegacyRoutingMigrationMarkerStore(Path.Combine(
                stateRoot,
                LegacyRoutingMigrationMarkerStore.FileName));
            var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(migration, Now);
            _ = await markers.SaveAsync(prepared, expectedGeneration: 0);
            var marker = await markers.SaveAsync(
                LegacyRoutingMigrationMarkerModel.Commit(prepared, Now.AddSeconds(1)),
                prepared.Generation);
            var authorityStore = new RoutingExecutionAuthorityStore(Path.Combine(
                stateRoot,
                RoutingExecutionAuthorityStore.FileName));
            _ = await authorityStore.CommitAsync(RoutingExecutionAuthorityModel.Create(
                marker,
                ClipCaptureSource.SteelSeriesGg,
                Now.AddSeconds(2)));
            var evidence = new LegacyRoutingActivationEvidenceSource(
                stateStore,
                () => settings,
                () => connectionIds,
                () => captureBinding);
            var ownership = new ClipProcessingOwnershipCoordinator();
            if (!ownership.TryAcquire(
                    ClipProcessingRuntimeOwner.Routing,
                    out var routingLease) || routingLease is null)
            {
                throw new InvalidOperationException(
                    "The named watched-source fixture could not acquire Routing ownership.");
            }
            var gate = RoutingRuntimeFeatureGate.Evaluate(
                requestedEnabled: true,
                markers,
                snapshots,
                evidence,
                routingLease,
                RoutingWatchedSourceAdapters.CoveredSources,
                authorityStore);
            Assert(gate.Enabled,
                "The named watched-source fixture must begin with complete Routing authority.");

            var journals = new RoutingWatchedSourceJournalStore(Path.Combine(
                stateRoot,
                "watched-journal",
                "v1"));
            var outbox = new RoutingOutboxStore(Path.Combine(
                stateRoot,
                RoutingOutboxStore.FileName));
            var sources = new[]
            {
                new NamedSource(
                    RoutingInputSourceKind.SteelSeriesGg,
                    ClipCaptureSource.SteelSeriesGg,
                    steelEnabled.Source!,
                    RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg),
                    steelExisting,
                    steelRoute.RouteId),
                new NamedSource(
                    RoutingInputSourceKind.Nvidia,
                    ClipCaptureSource.Nvidia,
                    nvidiaEnabled.Source!,
                    RoutingWatchedSourceAdapters.Get(ClipCaptureSource.Nvidia),
                    nvidiaExisting,
                    nvidiaRoute.RouteId)
            };
            return new Fixture(
                legacyRoot,
                captureRoot,
                catalog,
                sources,
                snapshots,
                journals,
                outbox,
                baselines,
                gate,
                routingLease);
        }

        public void Dispose() => _routingLease.Dispose();
    }

    private sealed class NamedSource(
        RoutingInputSourceKind kind,
        ClipCaptureSource captureSource,
        RoutingInputSourceRecord record,
        IRoutingWatchedSourceAdapter adapter,
        string existingPath,
        Guid routeId)
    {
        internal RoutingInputSourceKind Kind { get; } = kind;
        internal ClipCaptureSource CaptureSource { get; } = captureSource;
        internal RoutingInputSourceRecord Record { get; } = record;
        internal IRoutingWatchedSourceAdapter Adapter { get; } = adapter;
        internal string ExistingPath { get; } = existingPath;
        internal Guid RouteId { get; } = routeId;
        internal string? NewPath { get; private set; }

        internal string CreateNewClip(string? fileName = null)
        {
            var path = WriteClip(
                Record.CanonicalRoot,
                Kind,
                "New Game",
                fileName ?? $"new-{Kind}.mp4",
                Kind == RoutingInputSourceKind.SteelSeriesGg
                    ? [11, 12, 13, 14]
                    : [21, 22, 23, 24]);
            NewPath = path;
            return path;
        }
    }

    private static RoutingRouteDraft NamedDraft(
        string name,
        string sourceId,
        RoutingInputSourceKind kind) => new(
        name,
        RoutingTriggerKind.WatchedFolder,
        Game: null,
        Destination: null,
        ConnectionId: null,
        RoutingOutputKind.Original,
        RoutingDeliveryMode.Automatic,
        RoutingMissingOutputBehavior.UseOriginal,
        FileIntoLibrary: true,
        WatchedSourceId: sourceId,
        WatchedSourceKind: kind);

    private static string WriteClip(
        string root,
        RoutingInputSourceKind kind,
        string game,
        string fileName,
        byte[] bytes)
    {
        var folder = kind == RoutingInputSourceKind.Nvidia
            ? Path.Combine(root, game)
            : root;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class FixedMediaProbe : IRoutingWatchedFolderMediaProbe
    {
        public Task<RoutingWatchedFolderMediaInfo> ProbeAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RoutingWatchedFolderMediaInfo(
                TimeSpan.FromSeconds(30),
                1920,
                1080));
        }
    }

    private sealed class RejectingProvider : IRoutingDeliveryProvider
    {
        internal static readonly RejectingProvider Instance = new();

        public bool Supports(RoutingDestinationKind destination) => true;

        public Task<RoutingDeliveryAttemptResult> SendAsync(
            PlannedDelivery delivery,
            RoutingResolvedArtifact artifact,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The runtime health fixture must not perform a provider send.");
    }

    private sealed class RejectingResolver : IRoutingArtifactResolver
    {
        internal static readonly RejectingResolver Instance = new();

        public Task<RoutingResolvedArtifact> ResolveAsync(
            PlannedDelivery delivery,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The runtime health fixture must not resolve an artifact.");
    }

    private sealed class RejectingFiler : IRoutingLibraryFiler
    {
        internal static readonly RejectingFiler Instance = new();

        public Task<RoutingFileAttemptResult> FileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The runtime health fixture must not file a clip.");

        public Task<RoutingFileRecoveryResult> ReconcileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The runtime health fixture must not reconcile a clip filing.");
    }

    private sealed class QueueGuidFactory(IEnumerable<Guid> values)
    {
        private readonly Queue<Guid> _values = new(values);

        internal Guid Next() => _values.Count > 0
            ? _values.Dequeue()
            : throw new InvalidOperationException("The deterministic source-id queue is empty.");
    }

    private sealed class NoInputSourceReferences : IRoutingInputSourceReferenceProbe
    {
        internal static readonly NoInputSourceReferences Instance = new();

        public RoutingInputSourceReferenceStatus Inspect(
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RoutingInputSourceReferenceStatus.NotReferenced;
        }
    }

    private sealed class AlwaysAllowedMutationAuthority : IRoutingRouteMutationAuthority
    {
        internal static readonly AlwaysAllowedMutationAuthority Instance = new();

        public bool CanMutate(
            RoutingSnapshotDocument snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }
}
