using System.Globalization;
using ClipsToDiscord;

internal static class RoutingLocalOnlyOverrideTests
{
    private const string XboxSourceId = "source.11111111222233334444555555555555";
    private const string OccurrenceId =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RevisionId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset StartedUtc =
        new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertLegacyMigrationAndDurabilityAsync(Path.Combine(root, "migration"));
        await AssertCorruptionFallsBackToLocalOnlyAsync(Path.Combine(root, "corrupt"));
        await AssertAdmissionFreezeAcrossEverySourceAsync(Path.Combine(root, "admission"));
        await AssertCapturePromotionFreezesBeforeJournalAsync(
            Path.Combine(root, "capture-promotion"));
        await AssertWatchedJournalFreezesAdmissionAsync(Path.Combine(root, "watched-journal"));
        await AssertXboxAdmissionSurvivesImportAndToggleAsync(
            Path.Combine(root, "xbox-admission"));
        await RoutingLocalOnlyShellTests.RunAsync(Path.Combine(root, "shell"));
    }

    private static async Task AssertLegacyMigrationAndDurabilityAsync(string root)
    {
        Directory.CreateDirectory(root);
        var now = StartedUtc;
        var path = Path.Combine(root, RoutingLocalOnlyOverrideStore.FileName);
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path),
            () => now);

        var migrated = await state.EnsureMigratedAsync(
            legacyUploadToDiscord: false,
            legacyHotkeyBinding: "Control + Alt + K");
        Assert(migrated.LoadedFromDisk && migrated.Document is
               {
                   Revision: 1,
                   Enabled: true,
                   HotkeyBinding: "Ctrl + Alt + K",
                   FirstRunNoticeDismissed: false
               } &&
               migrated.AdmissionSnapshot == new RoutingLocalOnlyAdmissionSnapshot(
                   Enabled: true, StateRevision: 1, FailSafe: false),
            "One-time migration must preserve legacy Local-only state and normalize its shortcut in Routing-owned storage.");

        var restarted = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path),
            () => now.AddMinutes(1));
        var loaded = restarted.Inspect();
        Assert(loaded == migrated,
            "The Routing-owned Local-only override must survive a fresh state-service instance.");

        var ignoredLegacyChange = await restarted.EnsureMigratedAsync(
            legacyUploadToDiscord: true,
            legacyHotkeyBinding: GlobalHotkeyBinding.DefaultDisplayText);
        Assert(ignoredLegacyChange == migrated,
            "Legacy settings must never overwrite an already-migrated Routing-owned override.");

        now = now.AddMinutes(2);
        var dismissed = await state.DismissFirstRunNoticeAsync();
        now = now.AddMinutes(1);
        var rebound = await state.SetHotkeyAsync("Ctrl + Shift + F9");
        now = now.AddMinutes(1);
        var disabled = await state.SetEnabledAsync(false);
        Assert(dismissed.Document is { Revision: 2, FirstRunNoticeDismissed: true } &&
               rebound.Document is { Revision: 3, HotkeyBinding: "Ctrl + Shift + F9" } &&
               disabled.Document is { Revision: 4, Enabled: false } &&
               disabled.AdmissionSnapshot == new RoutingLocalOnlyAdmissionSnapshot(
                   Enabled: false, StateRevision: 4, FailSafe: false),
            "Every Local-only mutation must return its exact durable successor for immediate shell refresh.");
        Assert(new RoutingLocalOnlyOverrideState(
                       new RoutingLocalOnlyOverrideStore(path)).Inspect().Document == disabled.Document,
            "The latest Local-only state must reload exactly after restart.");
        Assert(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Length == 0,
            "Atomic Local-only writes must not leave sibling temporary files behind.");

        var normalPath = Path.Combine(root, "legacy-discord", RoutingLocalOnlyOverrideStore.FileName);
        var normal = await new RoutingLocalOnlyOverrideState(
                new RoutingLocalOnlyOverrideStore(normalPath),
                () => StartedUtc)
            .EnsureMigratedAsync(
                legacyUploadToDiscord: true,
                legacyHotkeyBinding: string.Empty);
        Assert(normal.Document is { Enabled: false, HotkeyBinding: "" },
            "Legacy Discord mode and a disabled shortcut must migrate without enabling Local-only mode.");
    }

    private static async Task AssertCorruptionFallsBackToLocalOnlyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, RoutingLocalOnlyOverrideStore.FileName);
        await File.WriteAllTextAsync(path, "{ this is not valid json");
        var before = await File.ReadAllBytesAsync(path);
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(path),
            () => StartedUtc);

        var inspection = state.Inspect();
        Assert(inspection.Status == RoutingDocumentLoadStatus.Corrupt &&
               !inspection.LoadedFromDisk && inspection.EffectiveEnabled &&
               inspection.AdmissionSnapshot == new RoutingLocalOnlyAdmissionSnapshot(
                   Enabled: true, StateRevision: 0, FailSafe: true),
            "Corrupt Local-only state must fail safe by suppressing external delivery.");
        await AssertThrowsAsync<InvalidDataException>(
            () => state.SetEnabledAsync(false),
            "A corrupt safety override must not be silently overwritten to re-enable delivery.");
        Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(before),
            "Corruption fallback must preserve the untrusted document for explicit recovery.");

        var proposal = RoutingEvaluator.CreatePlan(
            Snapshot(),
            CaptureFacts("clip-corrupt-failsafe"),
            Guid.NewGuid(),
            deliberateDuplicateAuthorizations: [],
            StartedUtc,
            inspection.AdmissionSnapshot);
        Assert(proposal.Deliveries.Count == 0 &&
               proposal.FileDisposition?.LibraryArea == RoutingLibraryArea.LocalOnly &&
               proposal.LocalOnlyOverride?.FailSafe == true,
            "Fail-safe admission must durably choose Local-only filing, never external work.");
    }

    private static async Task AssertAdmissionFreezeAcrossEverySourceAsync(string root)
    {
        Directory.CreateDirectory(root);
        var now = StartedUtc;
        var overridePath = Path.Combine(root, RoutingLocalOnlyOverrideStore.FileName);
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(overridePath),
            () => now);
        var initial = await state.EnsureMigratedAsync(
            legacyUploadToDiscord: true,
            legacyHotkeyBinding: GlobalHotkeyBinding.DefaultDisplayText);
        var snapshot = Snapshot();
        var frozenRoutes = snapshot with { Routes = snapshot.Routes.ToArray() };
        var snapshotPath = Path.Combine(root, RoutingSnapshotStore.FileName);
        var snapshotStore = new RoutingSnapshotStore(snapshotPath);
        _ = await snapshotStore.SaveAsync(frozenRoutes, expectedGeneration: 0);

        var sources = new[]
        {
            CaptureFacts("clip-capture-off"),
            WatchedFacts("clip-watched-off"),
            XboxFacts("clip-xbox-off")
        };
        foreach (var facts in sources)
        {
            var normal = Plan(snapshot, facts, initial.AdmissionSnapshot);
            Assert(normal.Deliveries.Count == 1 &&
                   normal.FileDisposition?.LibraryArea == RoutingLibraryArea.Uploaded &&
                   normal.LocalOnlyOverride == initial.AdmissionSnapshot,
                "With the override off, each source family must retain its normal frozen route actions.");
        }

        now = now.AddMinutes(1);
        var enabled = await state.SetEnabledAsync(true);
        var captureOutputsBefore = sources[0].Outputs.ToArray();
        var admitted = new[]
        {
            Plan(snapshot, CaptureFacts("clip-capture-on"), enabled.AdmissionSnapshot),
            Plan(snapshot, WatchedFacts("clip-watched-on"), enabled.AdmissionSnapshot),
            Plan(snapshot, XboxFacts("clip-xbox-on"), enabled.AdmissionSnapshot)
        };
        Assert(admitted.All(plan =>
                   plan.Deliveries.Count == 0 &&
                   plan.FileDisposition is { LibraryArea: RoutingLibraryArea.LocalOnly } disposition &&
                   RoutingLocalOnlyPlanPolicy.IsSyntheticDisposition(disposition) &&
                   plan.LocalOnlyOverride == enabled.AdmissionSnapshot) &&
               sources[0].Outputs.SequenceEqual(captureOutputsBefore),
            "Override-on admission must suppress every external destination, force Local-only filing, and leave produced artifacts untouched for watched, Capture, and Xbox sources.");

        var noMatch = Plan(
            snapshot,
            ManualImportFacts("clip-no-route-on"),
            enabled.AdmissionSnapshot);
        Assert(noMatch.MatchedRouteIds.Count == 0 && noMatch.Deliveries.Count == 0 &&
               noMatch.FileDisposition?.LibraryArea == RoutingLibraryArea.LocalOnly,
            "Local-only safety mode must file even a future clip with no matching route instead of dropping or externally routing it.");

        var capturePlan = admitted[0];
        var outboxStore = new RoutingOutboxStore(
            Path.Combine(root, RoutingOutboxStore.FileName));
        var empty = await outboxStore.LoadOrCreateAsync(now);
        var persistedCandidate = RoutingOutboxModel.AppendEvaluatedPlan(
            empty,
            capturePlan,
            now);
        _ = await outboxStore.SaveAsync(persistedCandidate, empty.Generation);

        now = now.AddMinutes(1);
        var disabled = await state.SetEnabledAsync(false);
        var reloaded = outboxStore.Load().Document ??
                       throw new InvalidOperationException("The frozen Local-only plan did not reload.");
        var frozenPlan = reloaded.Plans.Single(plan => plan.PlanId == capturePlan.PlanId);
        Assert(frozenPlan.LocalOnlyOverride == enabled.AdmissionSnapshot &&
               reloaded.Deliveries.All(item => item.PlanId != capturePlan.PlanId) &&
               reloaded.FileDispositions.Single(item => item.PlanId == capturePlan.PlanId)
                   .LibraryArea == RoutingLibraryArea.LocalOnly,
            "Turning the override off after admission must not create retroactive uploads or alter in-flight Local-only work.");

        var future = Plan(snapshot, CaptureFacts("clip-capture-after-off"),
            disabled.AdmissionSnapshot);
        Assert(future.Deliveries.Count == 1 &&
               future.FileDisposition?.LibraryArea == RoutingLibraryArea.Uploaded &&
               future.LocalOnlyOverride == disabled.AdmissionSnapshot,
            "Turning the override off must affect only future admissions, which resume normal routing.");
        Assert(snapshotStore.Load().Document == frozenRoutes,
            "Toggling Local-only mode must never rewrite or disable the user's route definitions.");
    }

    private static async Task AssertWatchedJournalFreezesAdmissionAsync(string root)
    {
        var clipsRoot = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var captureLibrary = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        var state = new WatchState
        {
            Version = 4,
            ClipsFolder = clipsRoot,
            CaptureSource = ClipCaptureSource.SteelSeriesGg
        };
        var settings = new AppSettings(
            clipsRoot,
            WebhookUrl: string.Empty,
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Watched override test",
            UploadToDiscord: false,
            ModeToggleHotkey: string.Empty,
            ClipCaptureSource.SteelSeriesGg);
        var migration = LegacyRoutingMigrationPlanner.Evaluate(
            new LegacyRoutingMigrationInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                DiscordConnectionIds: [],
                RoutingCaptureLibraryBindingModel.Create(captureLibrary),
                LegacyRoutingMigrationAdmission.ValidLegacyUpgrade),
            StartedUtc).Plan ?? throw new InvalidOperationException(
                "The watched Local-only fixture could not create its migration route.");
        var marker = LegacyRoutingMigrationMarkerModel.Commit(
            LegacyRoutingMigrationMarkerModel.CreatePrepared(migration, StartedUtc),
            StartedUtc.AddSeconds(1));
        var externalRoute = Snapshot().Routes.Single(route =>
            route.Name.Equals("Watched route", StringComparison.Ordinal));
        var routingSnapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [externalRoute, migration.Route],
            CreatedUtc: StartedUtc,
            UpdatedUtc: StartedUtc);
        RoutingSnapshotModel.Validate(routingSnapshot);

        var source = new RoutingWatchedSourceFile(
            ClipCaptureSource.SteelSeriesGg,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(clipsRoot)),
            "Battlefield 6 2026-09-02 12-00-00.mp4",
            "Battlefield 6",
            "Battlefield 6 2026-09-02 12-00-00.mp4",
            new string('d', 64),
            new RoutingWatchedNativeFileIdentity(
                VolumeSerialNumber: 0x12345678,
                FileIdHex: "0000000000000001",
                ByteLength: 4096,
                CreationUtcTicks: StartedUtc.UtcTicks,
                LastWriteUtcTicks: StartedUtc.UtcTicks),
            RevisionId);
        var admission = new RoutingLocalOnlyAdmissionSnapshot(
            Enabled: true,
            StateRevision: 8,
            FailSafe: false);
        var accessorCalls = 0;
        var factory = new RoutingWatchedJournalFactory(
            new FixedWatchedProbe(),
            createPlanId: Guid.NewGuid,
            utcNow: () => StartedUtc.AddMinutes(1),
            captureLocalOnlyOverride: () =>
            {
                Interlocked.Increment(ref accessorCalls);
                return admission;
            });
        var journal = await factory.CreateAsync(
            source,
            new RevalidatingWatchedAdapter(source.Source),
            marker,
            routingSnapshot,
            new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.Enabled,
                0,
                0,
                0,
                0,
                routingSnapshot.Generation,
                marker.PayloadFingerprint,
                OwnershipEpoch: 1,
                source.Source,
                new HashSet<ClipCaptureSource> { source.Source },
                Guid.NewGuid()));
        admission = new RoutingLocalOnlyAdmissionSnapshot(
            Enabled: false,
            StateRevision: 9,
            FailSafe: false);

        var frozen = journal.FrozenPlan ?? throw new InvalidOperationException(
            "The watched Local-only admission did not freeze a plan.");
        Assert(accessorCalls == 1 &&
               frozen.LocalOnlyOverride == new RoutingLocalOnlyAdmissionSnapshot(
                   Enabled: true, StateRevision: 8, FailSafe: false) &&
               frozen.Deliveries.Count == 0 &&
               frozen.FileDisposition is { LibraryArea: RoutingLibraryArea.LocalOnly } disposition &&
               RoutingLocalOnlyPlanPolicy.IsSyntheticDisposition(disposition),
            "Ordinary watched clips must sample Local-only mode exactly once and freeze that admission decision in their write-ahead journal.");
    }

    private static async Task AssertCapturePromotionFreezesBeforeJournalAsync(string root)
    {
        var libraryRoot = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(Path.Combine(
                root, "routing", RoutingLocalOnlyOverrideStore.FileName)),
            () => StartedUtc);
        var admitted = await state.EnsureMigratedAsync(
            legacyUploadToDiscord: false,
            GlobalHotkeyBinding.DefaultDisplayText);
        var staging = CaptureLibraryLayout.GetStagingDirectory(libraryRoot);
        Directory.CreateDirectory(staging);
        var stagedPath = Path.Combine(staging, "capture-freeze.mp4");
        await File.WriteAllBytesAsync(stagedPath, [9, 8, 7, 6]);
        var gameDirectory = CaptureLibraryLayout.GetRecordingDirectory(
            libraryRoot, "Capture Freeze Game");
        Directory.CreateDirectory(gameDirectory);
        var finalPath = Path.Combine(
            gameDirectory, "Capture Freeze Game 2026-09-02 12-00-00.mp4");
        var intent = await CaptureJournalPromotionIntentStore.PrepareOriginalAsync(
            libraryRoot,
            stagedPath,
            finalPath,
            CaptureJournalSourceKind.InstantReplay,
            "Capture Freeze Game",
            StartedUtc,
            TimeSpan.FromSeconds(30),
            1920,
            1080,
            reactionCameraRequested: false,
            requestedRenditions: [],
            now: StartedUtc,
            localOnlyOverride: admitted.AdmissionSnapshot);
        _ = await state.SetEnabledAsync(false);
        var promoted = await CaptureJournalPromotionIntentStore.PromoteOriginalAsync(
            libraryRoot, intent.ClipId);
        Assert(promoted.Status == CaptureJournalPromotionStatus.DestinationReady,
            "The Capture Local-only promotion fixture must reach its durable destination.");
        var document = await CaptureJournalPromotionIntentStore.CommitOriginalAsync(
            libraryRoot, intent.ClipId, now: StartedUtc.AddMinutes(1));

        Assert(!state.Inspect().EffectiveEnabled &&
               intent.LocalOnlyOverride == admitted.AdmissionSnapshot &&
               document.Clip.LocalOnlyOverride == admitted.AdmissionSnapshot,
            "Capture must freeze Local-only mode in its promotion intent before publication and carry it into the later Capture Journal despite a toggle.");
    }

    private static async Task AssertXboxAdmissionSurvivesImportAndToggleAsync(string root)
    {
        var libraryRoot = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        var routingRoot = Directory.CreateDirectory(Path.Combine(root, "routing")).FullName;
        var state = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(Path.Combine(
                routingRoot, RoutingLocalOnlyOverrideStore.FileName)),
            () => StartedUtc);
        var admitted = await state.EnsureMigratedAsync(
            legacyUploadToDiscord: false,
            GlobalHotkeyBinding.DefaultDisplayText);
        var occurrenceJournal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(routingRoot, XboxSourceId),
            XboxSourceId,
            new string('d', 64),
            () => StartedUtc);
        var entry = await occurrenceJournal.SelectAsync(
            OccurrenceId,
            RevisionId,
            "Battlefield 6 2026-09-02 12-00-01.mp4",
            "Battlefield 6",
            StartedUtc.AddSeconds(1),
            logicalBytes: 6,
            lastWriteUtcTicks: StartedUtc.UtcTicks,
            localOnlyOverride: admitted.AdmissionSnapshot);
        _ = await state.SetEnabledAsync(false);

        entry = await occurrenceJournal.BeginImportAsync(entry.OccurrenceId, entry.RevisionId);
        var stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(libraryRoot);
        Directory.CreateDirectory(stagingDirectory);
        var stagedPath = Path.Combine(stagingDirectory, "xbox-admission.mp4");
        await File.WriteAllBytesAsync(stagedPath, [1, 2, 3, 4, 5, 6]);
        var recordingDirectory = CaptureLibraryLayout.GetRecordingDirectory(
            libraryRoot, entry.GameName);
        Directory.CreateDirectory(recordingDirectory);
        var destinationPath = Path.Combine(
            recordingDirectory, "Battlefield 6 2026-09-02 12-00-01.mp4");
        var identity = new XboxDvrLibraryPromotionIdentity(
            XboxSourceId,
            entry.OccurrenceId,
            entry.RevisionId,
            entry.GameName,
            entry.CapturedUtc,
            stagedPath,
            destinationPath,
            RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                entry.LocalOnlyOverride));
        var document = await new XboxDvrCaptureLibraryPromoter(
                libraryRoot,
                () => StartedUtc.AddMinutes(1))
            .PromoteAsync(
                new XboxDvrLibraryPromotionRequest(
                    identity,
                    new RoutingWatchedFolderMediaInfo(
                        TimeSpan.FromSeconds(30), 1920, 1080)),
                CancellationToken.None);
        entry = await occurrenceJournal.CompleteAsync(
            entry.OccurrenceId,
            entry.RevisionId,
            document.Clip.ClipId,
            document.Clip.Original.Fingerprint.Sha256);

        var original = new RoutingRuntimeOutput(
            RoutingEvaluator.CreateLogicalOutputReference(
                document.Clip.ClipId,
                document.Clip.Original.Fingerprint.Sha256,
                RoutingOutputKind.Original),
            RoutingRuntimeArtifactReadiness.Ready,
            document.Clip.Original);
        var outputs = new Dictionary<RoutingOutputKind, RoutingRuntimeOutput>
        {
            [RoutingOutputKind.Original] = original,
            [RoutingOutputKind.Landscape] = new(
                RoutingEvaluator.CreateLogicalOutputReference(
                    document.Clip.ClipId,
                    document.Clip.Original.Fingerprint.Sha256,
                    RoutingOutputKind.Landscape),
                RoutingRuntimeArtifactReadiness.Unavailable,
                CommittedArtifact: null),
            [RoutingOutputKind.Portrait] = new(
                RoutingEvaluator.CreateLogicalOutputReference(
                    document.Clip.ClipId,
                    document.Clip.Original.Fingerprint.Sha256,
                    RoutingOutputKind.Portrait),
                RoutingRuntimeArtifactReadiness.Unavailable,
                CommittedArtifact: null)
        };
        var plan = new RoutingEvaluatorRuntimePlanner().BuildPlan(
            new RoutingRuntimePlanningContext(
                Snapshot(),
                document,
                outputs,
                StartedUtc.AddMinutes(2),
                RoutingLocalOnlyAdmissionSnapshot.PersistedOrFailSafe(
                    document.Clip.LocalOnlyOverride)));
        var reloaded = occurrenceJournal.Load().Document?.Occurrences.Single() ??
                       throw new InvalidOperationException(
                           "The Xbox admission did not survive restart.");
        Assert(!state.Inspect().EffectiveEnabled &&
               entry.State == XboxDvrOccurrenceState.Imported &&
               reloaded.LocalOnlyOverride == admitted.AdmissionSnapshot &&
               document.Clip.LocalOnlyOverride == admitted.AdmissionSnapshot &&
               plan.LocalOnlyOverride == admitted.AdmissionSnapshot &&
               plan.Deliveries.Count == 0 &&
               plan.FileDisposition is { LibraryArea: RoutingLibraryArea.LocalOnly },
            "Xbox selection must freeze Local-only mode before import and carry it through occurrence restart, promotion recovery, Capture journaling, and later planning despite a toggle.");
    }

    private static RoutingPlanProposal Plan(
        RoutingSnapshotDocument snapshot,
        RoutingClipFacts facts,
        RoutingLocalOnlyAdmissionSnapshot localOnly) => RoutingEvaluator.CreatePlan(
        snapshot,
        facts,
        Guid.NewGuid(),
        deliberateDuplicateAuthorizations: [],
        StartedUtc.AddMinutes(10),
        localOnly);

    private static RoutingSnapshotDocument Snapshot()
    {
        var routes = new[]
        {
            Route(
                priority: 0,
                "Capture route",
                RoutingTriggerKind.InstantReplay,
                [Condition(RoutingConditionField.ClipSource,
                    RoutingConditionOperator.Equals, RoutingClipSource.ClipCordCapture.ToString())]),
            Route(
                priority: 1,
                "Watched route",
                RoutingTriggerKind.WatchedFolder,
                [Condition(RoutingConditionField.ClipSource,
                    RoutingConditionOperator.Equals, RoutingClipSource.WatchedFolder.ToString())]),
            Route(
                priority: 2,
                "Xbox route",
                RoutingTriggerKind.WatchedFolder,
                [
                    Condition(RoutingConditionField.SourceConnection,
                        RoutingConditionOperator.Equals, XboxSourceId),
                    Condition(RoutingConditionField.CapturedAt,
                        RoutingConditionOperator.GreaterThanOrEqual,
                        StartedUtc.ToString("O", CultureInfo.InvariantCulture))
                ],
                new RoutingXboxHistorySelection(StartedUtc, []))
        };
        var snapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            routes,
            StartedUtc,
            StartedUtc);
        RoutingSnapshotModel.Validate(snapshot);
        return snapshot;
    }

    private static RoutingRoute Route(
        int priority,
        string name,
        RoutingTriggerKind trigger,
        IReadOnlyList<RoutingCondition> conditions,
        RoutingXboxHistorySelection? xboxHistory = null) => new(
        Guid.NewGuid(),
        name,
        Enabled: true,
        priority,
        Revision: 1,
        RoutingRouteSource.User,
        RoutingRouteKind.Specific,
        trigger,
        new RoutingPrepareSettings(
            Landscape: false,
            Portrait: false,
            RoutingMissingOutputBehavior.UseOriginal),
        conditions,
        [Delivery("discord." + priority), FileUploaded()],
        StartedUtc,
        StartedUtc,
        xboxHistory);

    private static RoutingCondition Condition(
        RoutingConditionField field,
        RoutingConditionOperator comparison,
        string value) => new(Guid.NewGuid(), field, comparison, value);

    private static RoutingAction Delivery(string connectionId) => new(
        Guid.NewGuid(),
        Enabled: true,
        RoutingActionKind.Deliver,
        RoutingDestinationKind.Discord,
        connectionId,
        RoutingOutputKind.Original,
        RoutingMissingOutputBehavior.UseOriginal,
        RoutingDeliveryMode.Automatic,
        LibraryArea: null,
        new RoutingDeliverySettings(
            Message: null,
            Title: null,
            Caption: null,
            RoutingVisibility.Unspecified,
            NotifyFollowers: false));

    private static RoutingAction FileUploaded() => new(
        Guid.NewGuid(),
        Enabled: true,
        RoutingActionKind.FileIntoLibrary,
        Destination: null,
        ConnectionId: null,
        OutputRef: null,
        OnMissingOutput: null,
        RoutingDeliveryMode.Automatic,
        RoutingLibraryArea.Uploaded,
        DeliverySettings: null);

    private static RoutingClipFacts CaptureFacts(string clipId) => Facts(
        clipId,
        RoutingClipSource.ClipCordCapture,
        RoutingTriggerKind.InstantReplay,
        RoutingCaptureType.InstantReplay);

    private static RoutingClipFacts WatchedFacts(string clipId) => Facts(
        clipId,
        RoutingClipSource.WatchedFolder,
        RoutingTriggerKind.WatchedFolder,
        CaptureType: null);

    private static RoutingClipFacts XboxFacts(string clipId) => Facts(
        clipId,
        RoutingClipSource.XboxOneDrive,
        RoutingTriggerKind.WatchedFolder,
        CaptureType: null,
        XboxSourceId,
        StartedUtc.AddSeconds(1),
        OccurrenceId,
        RevisionId);

    private static RoutingClipFacts ManualImportFacts(string clipId) => Facts(
        clipId,
        RoutingClipSource.ManualImport,
        RoutingTriggerKind.AnyNewSourceClip,
        CaptureType: null);

    private static RoutingClipFacts Facts(
        string clipId,
        RoutingClipSource source,
        RoutingTriggerKind trigger,
        RoutingCaptureType? CaptureType,
        string? sourceConnectionId = null,
        DateTimeOffset? capturedUtc = null,
        string? sourceOccurrenceId = null,
        string? sourceRevisionId = null) => new(
        clipId,
        RoutingEvaluationEventKind.SourceArrival,
        source,
        trigger,
        CaptureType,
        "Battlefield 6",
        ReactionCamera: true,
        DurationMilliseconds: 30_000,
        RevisionId,
        [new RoutingClipOutputRevision(
            new RoutingOutputReference(clipId, RoutingOutputKind.Original, RevisionId),
            RoutingOutputAvailability.Ready,
            FailureCode: null)],
        sourceConnectionId,
        capturedUtc,
        sourceOccurrenceId,
        sourceRevisionId);

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

    private sealed class FixedWatchedProbe : IRoutingWatchedFolderMediaProbe
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

    private sealed class RevalidatingWatchedAdapter(ClipCaptureSource source) :
        IRoutingWatchedSourceAdapter
    {
        public ClipCaptureSource Source { get; } = source;

        public string InspectRootIdentity(string clipsRoot) => new string('d', 64);

        public IReadOnlyList<string> EnumerateCandidates(
            string clipsRoot,
            CancellationToken cancellationToken = default) => [];

        public Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RoutingWatchedSourceFile> RevalidateAsync(
            RoutingWatchedSourceFile prior,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(prior);
        }
    }
}
