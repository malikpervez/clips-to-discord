using ClipsToDiscord;
using System.Text.Json;

internal static class RoutingMigrationAdmissionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 2, 16, 0, 0, TimeSpan.Zero);

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        AssertFreshProfilesStayFresh(root);
        AssertSettingsPresenceIsStatusBearing(root);
        AssertValidLegacyProfilesAreAdmitted(root);
        AssertReleasedLegacyStateVersionsAreAdmitted(root);
        AssertUnversionedLegacyStateRemainsEligible(root);
        AssertTransientEvidenceIsDeferred(root);
        AssertUnmountedLegacyFolderIsStillRecognized(root);
        AssertInvalidLegacyEvidenceFailsClosed(root);
        AssertConcurrentResolutionIsStable(root);
        AssertFreshPlannerCreatesNoRoutingArtifacts(root);
        AssertCorruptAdmissionFailsClosed(root);
    }

    private static void AssertFreshProfilesStayFresh(string root)
    {
        foreach (var (profileName, profile) in new[]
                 {
                     ("fresh-unpackaged", Path.Combine(root, "fresh-unpackaged")),
                     ("fresh-store-shaped", Path.Combine(
                         root,
                         "Packages",
                         "ClipCord_8wekyb3d8bbwe",
                         "LocalState"))
                 })
        {
            var store = AdmissionStore(profile);
            var stateStore = StateStore(profile);
            var first = store.Resolve(
                AppSettings.Empty,
                stateStore.ProbeForLegacyMigrationAdmission(),
                LegacyRoutingSettingsPresence.Missing,
                now: Now);
            Assert(first.Loaded && !first.AllowsLegacyImport &&
                   !first.LegacyRuntimeAllowed &&
                   first.Document!.Admission ==
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile,
                $"{profileName} must start as a fresh profile without an imported route.");

            var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
            stateStore.Save(State(clips));
            var afterSetup = store.Resolve(
                Settings(clips, upload: false),
                stateStore.ProbeForLegacyMigrationAdmission(),
                LegacyRoutingSettingsPresence.Present,
                now: Now.AddMinutes(1));
            Assert(afterSetup.Document == first.Document && !afterSetup.AllowsLegacyImport,
                $"{profileName} must not become a 1.x upgrade after 2.0 writes valid-looking settings and state.");
        }
    }

    private static void AssertSettingsPresenceIsStatusBearing(string root)
    {
        var profile = Path.Combine(root, "settings-presence");
        var settingsPath = Path.Combine(profile, "settings.json");
        Assert(SettingsStore.InspectLegacySettingsPresence(settingsPath) ==
               LegacyRoutingSettingsPresence.Missing,
            "An absent settings directory must be distinguished from unreadable evidence.");
        Directory.CreateDirectory(profile);
        Assert(SettingsStore.InspectLegacySettingsPresence(settingsPath) ==
               LegacyRoutingSettingsPresence.Missing,
            "An ordinary settings directory without a settings document must be reported missing.");
        File.WriteAllText(settingsPath, "{corrupt settings are still present evidence");
        Assert(SettingsStore.InspectLegacySettingsPresence(settingsPath) ==
               LegacyRoutingSettingsPresence.Present,
            "An ordinary readable settings document must remain present at the presence layer even when its contents need separate validation.");
        using (var locked = new FileStream(
                   settingsPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert(SettingsStore.InspectLegacySettingsPresence(settingsPath) ==
                   LegacyRoutingSettingsPresence.Unavailable,
                "An exclusively locked settings document must remain retryable instead of freezing a fresh decision after settings loading falls back.");
        }
        Assert(SettingsStore.InspectLegacySettingsPresence(settingsPath) ==
               LegacyRoutingSettingsPresence.Present,
            "Settings evidence must become readable again after a transient exclusive lock is released.");
    }

    private static void AssertValidLegacyProfilesAreAdmitted(string root)
    {
        foreach (var (profileName, upload) in new[]
                 {
                     ("legacy-discord", true),
                     ("legacy-local-only", false)
                 })
        {
            var profile = Path.Combine(root, profileName);
            var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
            var stateStore = StateStore(profile);
            stateStore.Save(State(clips));
            var store = AdmissionStore(profile);
            var first = store.Resolve(
                Settings(clips, upload),
                stateStore.ProbeForLegacyMigrationAdmission(),
                LegacyRoutingSettingsPresence.Present,
                now: Now);
            var restarted = AdmissionStore(profile).Resolve(
                Settings(clips, upload),
                stateStore.ProbeForLegacyMigrationAdmission(),
                LegacyRoutingSettingsPresence.Present,
                now: Now.AddMinutes(1));
            Assert(first.AllowsLegacyImport && first.LegacyRuntimeAllowed &&
                   restarted.Document == first.Document,
                $"{profileName} must retain one immutable positive 1.x-upgrade decision across restart.");
            Assert(first.ImportedRouteLabelVersion ==
                   LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion,
                $"{profileName} must persist the approved imported-route label for a new migration.");
        }

        var previewProfile = Path.Combine(root, "existing-preview-migration");
        var previewClips = Directory.CreateDirectory(
            Path.Combine(previewProfile, "clips")).FullName;
        var previewState = StateStore(previewProfile);
        previewState.Save(State(previewClips));
        var preview = AdmissionStore(previewProfile).Resolve(
            Settings(previewClips, upload: false),
            previewState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            preserveExistingMigrationLabel: true,
            now: Now);
        Assert(preview.AllowsLegacyImport && preview.ImportedRouteLabelVersion ==
               LegacyRoutingMigrationPlanner.LegacyImportedRouteLabelVersion,
            "An already committed 2.0 preview migration must retain its verifiable durable route payload.");
    }

    private static void AssertReleasedLegacyStateVersionsAreAdmitted(string root)
    {
        foreach (var version in new[] { 2, 3, 4 })
        {
            var profile = Path.Combine(root, $"released-state-v{version}");
            var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
            var stateStore = StateStore(profile);
            WriteHistoricalState(stateStore.StatePath, version, clips);
            var strictProbe = stateStore.ProbeForRoutingActivation();
            var admissionProbe = stateStore.ProbeForLegacyMigrationAdmission();
            Assert((version == WatchStateStore.CurrentVersion
                        ? strictProbe.Loaded
                        : strictProbe.Status == WatchStateRoutingProbeStatus.UnsupportedVersion) &&
                   admissionProbe.Loaded && admissionProbe.State?.Version == version,
                $"Released 1.x watcher-state version {version} must be readable only by the admission probe until upgraded.");

            var decision = AdmissionStore(profile).Resolve(
                Settings(clips, upload: version % 2 == 0),
                admissionProbe,
                LegacyRoutingSettingsPresence.Present,
                now: Now);
            Assert(decision.AllowsLegacyImport && decision.LegacyRuntimeAllowed &&
                   decision.Loaded,
                $"Released 1.x watcher-state version {version} must be admitted.");

            if (version < WatchStateStore.CurrentVersion)
            {
                _ = stateStore.LoadOrInitializeAsync(
                        clips,
                        _ => { },
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Assert(stateStore.ProbeForRoutingActivation() is
                        { Status: WatchStateRoutingProbeStatus.Loaded, State.Version: 4 },
                    $"The legacy runtime must upgrade admitted version {version} before Routing activation.");
            }
        }
    }

    private static void AssertTransientEvidenceIsDeferred(string root)
    {
        var corruptProfile = Path.Combine(root, "transient-corrupt-state");
        var corruptClips = Directory.CreateDirectory(
            Path.Combine(corruptProfile, "clips")).FullName;
        var corruptState = StateStore(corruptProfile);
        Directory.CreateDirectory(Path.GetDirectoryName(corruptState.StatePath)!);
        File.WriteAllText(corruptState.StatePath, "{not valid json");
        var corruptStore = AdmissionStore(corruptProfile);
        var corrupt = corruptStore.Resolve(
            Settings(corruptClips, upload: false),
            corruptState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        Assert(!corrupt.Loaded &&
               corrupt.EffectiveAdmission == LegacyRoutingMigrationAdmission.Deferred &&
               !corrupt.LegacyRuntimeAllowed &&
               !File.Exists(corruptStore.Path),
            "A corrupt watcher state must fail closed without freezing a permanent negative decision.");
        WriteHistoricalState(corruptState.StatePath, version: 3, corruptClips);
        var repaired = corruptStore.Resolve(
            Settings(corruptClips, upload: false),
            corruptState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now.AddMinutes(1));
        Assert(repaired.AllowsLegacyImport,
            "A repaired genuine v3 state must remain eligible on a later resolution.");

        var lockedProfile = Path.Combine(root, "transient-locked-state");
        var lockedClips = Directory.CreateDirectory(Path.Combine(lockedProfile, "clips")).FullName;
        var lockedState = StateStore(lockedProfile);
        WriteHistoricalState(lockedState.StatePath, version: 3, lockedClips);
        var lockedStore = AdmissionStore(lockedProfile);
        using (var stateLock = new FileStream(
                   lockedState.StatePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var probe = lockedState.ProbeForLegacyMigrationAdmission();
            var locked = lockedStore.Resolve(
                Settings(lockedClips, upload: false),
                probe,
                LegacyRoutingSettingsPresence.Present,
                now: Now);
            Assert(probe.Status == WatchStateRoutingProbeStatus.Unavailable &&
                   !locked.Loaded &&
                   locked.EffectiveAdmission == LegacyRoutingMigrationAdmission.Deferred &&
                   !locked.LegacyRuntimeAllowed &&
                   !File.Exists(lockedStore.Path),
                "A temporarily locked state must deny this launch without persisting a negative decision.");
        }
        var unlocked = lockedStore.Resolve(
            Settings(lockedClips, upload: false),
            lockedState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now.AddMinutes(1));
        Assert(unlocked.AllowsLegacyImport,
            "A genuine legacy state must be admitted after a transient lock is released.");

        var futureProfile = Path.Combine(root, "unsupported-future-state");
        var futureClips = Directory.CreateDirectory(Path.Combine(futureProfile, "clips")).FullName;
        var futureState = StateStore(futureProfile);
        WriteHistoricalState(futureState.StatePath, version: 99, futureClips);
        var futureStore = AdmissionStore(futureProfile);
        var future = futureStore.Resolve(
            Settings(futureClips, upload: false),
            futureState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        Assert(future.EffectiveAdmission == LegacyRoutingMigrationAdmission.Deferred &&
               !future.AllowsLegacyImport && !future.LegacyRuntimeAllowed &&
               !File.Exists(futureStore.Path),
            "A future watcher-state version must remain fail-closed and retryable, not be misclassified as fresh.");

        var futureBytes = File.ReadAllBytes(futureState.StatePath);
        var futureSafeBaselineMarker = Path.Combine(
            futureProfile,
            ".safe-baseline-required");
        File.WriteAllText(futureSafeBaselineMarker, Now.ToString("O"));
        var futureStatuses = new List<string>();
        AssertThrows<InvalidDataException>(() => futureState.LoadOrInitializeAsync(
                futureClips,
                futureStatuses.Add,
                CancellationToken.None)
            .GetAwaiter().GetResult());
        Assert(futureBytes.SequenceEqual(File.ReadAllBytes(futureState.StatePath)) &&
               !File.Exists(futureState.StatePath + ".tmp") &&
               futureState.ProbeForLegacyMigrationAdmission().Status ==
               WatchStateRoutingProbeStatus.UnsupportedVersion &&
               File.Exists(futureSafeBaselineMarker) &&
               futureStatuses.Count == 1 &&
               futureStatuses[0].Contains("newer ClipCord version", StringComparison.Ordinal) &&
               futureStatuses[0].Contains("processing is paused", StringComparison.Ordinal),
            "The mutating legacy loader must refuse and preserve a future watcher-state version.");
    }

    private static void AssertUnversionedLegacyStateRemainsEligible(string root)
    {
        var profile = Path.Combine(root, "released-unversioned-state");
        var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
        var stateStore = StateStore(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(stateStore.StatePath)!);
        File.WriteAllText(stateStore.StatePath, JsonSerializer.Serialize(new
        {
            ClipsFolder = clips,
            KnownSignatures = Array.Empty<string>(),
            PendingMoves = Array.Empty<string>()
        }));
        var admissionStore = AdmissionStore(profile);
        var first = admissionStore.Resolve(
            Settings(clips, upload: false),
            stateStore.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        Assert(first.Loaded && first.AllowsLegacyImport && first.LegacyRuntimeAllowed &&
               first.Document!.Admission ==
               LegacyRoutingMigrationAdmission.ValidLegacyUpgrade &&
               File.Exists(admissionStore.Path),
            "The exact released v1.0-v1.1 watcher schema must be admitted before its safe upgrade runs.");

        _ = stateStore.LoadOrInitializeAsync(
                clips,
                _ => { },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var afterSafeUpgrade = admissionStore.Resolve(
            Settings(clips, upload: false),
            stateStore.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now.AddMinutes(1));
        Assert(afterSafeUpgrade.AllowsLegacyImport &&
               afterSafeUpgrade.Document == first.Document,
            "A released unversioned state must retain its durable admission after the legacy runtime safely upgrades it.");

        var unknownProfile = Path.Combine(root, "unknown-unversioned-state");
        var unknownClips = Directory.CreateDirectory(
            Path.Combine(unknownProfile, "clips")).FullName;
        var unknownState = StateStore(unknownProfile);
        Directory.CreateDirectory(Path.GetDirectoryName(unknownState.StatePath)!);
        File.WriteAllText(unknownState.StatePath, JsonSerializer.Serialize(new
        {
            ClipsFolder = unknownClips,
            KnownSignatures = Array.Empty<string>(),
            PendingMoves = Array.Empty<string>(),
            UnexpectedField = true
        }));
        var unknownProbe = unknownState.ProbeForLegacyMigrationAdmission();
        var unknown = AdmissionStore(unknownProfile).Resolve(
            Settings(unknownClips, upload: false),
            unknownProbe,
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        Assert(unknownProbe.Status == WatchStateRoutingProbeStatus.UnsupportedVersion &&
               unknown.EffectiveAdmission == LegacyRoutingMigrationAdmission.Deferred &&
               !unknown.LegacyRuntimeAllowed,
            "Only the exact released unversioned watcher schema may authorize a legacy upgrade.");
    }

    private static void AssertUnmountedLegacyFolderIsStillRecognized(string root)
    {
        var profile = Path.Combine(root, "temporarily-unmounted-folder");
        Directory.CreateDirectory(profile);
        var clips = Path.Combine(profile, "offline-drive", "clips");
        var stateStore = StateStore(profile);
        WriteHistoricalState(stateStore.StatePath, version: 3, clips);
        var settings = Settings(clips, upload: false);
        Assert(!settings.IsValid && !Directory.Exists(clips),
            "The unmounted-folder fixture must exercise settings that are temporarily runtime-invalid.");
        var decision = AdmissionStore(profile).Resolve(
            settings,
            stateStore.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        Assert(decision.AllowsLegacyImport,
            "Matching, structurally valid legacy evidence must not be forfeited merely because its drive is not mounted yet.");
    }

    private static void AssertInvalidLegacyEvidenceFailsClosed(string root)
    {
        var unavailableSettingsProfile = Path.Combine(root, "unavailable-settings");
        var unavailableSettingsStore = AdmissionStore(unavailableSettingsProfile);
        var unavailableSettings = unavailableSettingsStore.Resolve(
            AppSettings.Empty,
            StateStore(unavailableSettingsProfile).ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Unavailable,
            now: Now);
        Assert(unavailableSettings.EffectiveAdmission == LegacyRoutingMigrationAdmission.Deferred &&
               !unavailableSettings.LegacyRuntimeAllowed &&
               !File.Exists(unavailableSettingsStore.Path),
            "Unavailable settings evidence must not be mistaken for a clearly fresh profile.");

        var missingProfile = Path.Combine(root, "missing-state-with-settings");
        var missingClips = Directory.CreateDirectory(Path.Combine(missingProfile, "clips")).FullName;
        var missingStore = AdmissionStore(missingProfile);
        var missing = missingStore.Resolve(
            Settings(missingClips, upload: false),
            StateStore(missingProfile).ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        var missingState = StateStore(missingProfile);
        missingState.Save(State(missingClips));
        var missingAfterSyntheticState = AdmissionStore(missingProfile).Resolve(
            Settings(missingClips, upload: false),
            missingState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now.AddMinutes(1));
        Assert(missing.Loaded && missing.Document!.Admission ==
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile &&
               !missing.AllowsLegacyImport && !missing.LegacyRuntimeAllowed &&
               missingAfterSyntheticState.Document == missing.Document,
            "Settings without watcher state must durably stay non-legacy after 2.0 writes matching state.");

        var incompleteProfile = Path.Combine(root, "incomplete-compatible-state");
        var incompleteClips = Directory.CreateDirectory(
            Path.Combine(incompleteProfile, "clips")).FullName;
        var incompleteState = StateStore(incompleteProfile);
        Directory.CreateDirectory(Path.GetDirectoryName(incompleteState.StatePath)!);
        File.WriteAllText(incompleteState.StatePath, JsonSerializer.Serialize(new
        {
            Version = 3,
            ClipsFolder = incompleteClips
        }));
        var incompleteProbe = incompleteState.ProbeForLegacyMigrationAdmission();
        Assert(incompleteProbe.Status == WatchStateRoutingProbeStatus.Invalid,
            "A compatible version number without its released schema must not become legacy evidence.");

        var wrongRootProfile = Path.Combine(root, "wrong-state-root");
        var expectedClips = Directory.CreateDirectory(
            Path.Combine(wrongRootProfile, "clips")).FullName;
        var otherClips = Directory.CreateDirectory(
            Path.Combine(wrongRootProfile, "other-clips")).FullName;
        var wrongRootState = StateStore(wrongRootProfile);
        wrongRootState.Save(State(otherClips));
        var wrongRootStore = AdmissionStore(wrongRootProfile);
        var wrongRoot = wrongRootStore.Resolve(
            Settings(expectedClips, upload: false),
            wrongRootState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        wrongRootState.Save(State(expectedClips));
        var wrongRootAfterSyntheticState = AdmissionStore(wrongRootProfile).Resolve(
            Settings(expectedClips, upload: false),
            wrongRootState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now.AddMinutes(1));
        Assert(wrongRoot.Loaded && wrongRoot.Document!.Admission ==
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile &&
               !wrongRoot.LegacyRuntimeAllowed &&
               wrongRootAfterSyntheticState.Document == wrongRoot.Document,
            "Mismatched watcher roots must durably stay non-legacy after 2.0 re-baselines the configured root.");

        var wrongSourceProfile = Path.Combine(root, "wrong-state-source");
        var wrongSourceClips = Directory.CreateDirectory(
            Path.Combine(wrongSourceProfile, "clips")).FullName;
        var wrongSourceState = StateStore(wrongSourceProfile);
        wrongSourceState.Save(State(wrongSourceClips));
        var nvidiaSettings = Settings(wrongSourceClips, upload: false) with
        {
            CaptureSource = ClipCaptureSource.Nvidia
        };
        var wrongSourceStore = AdmissionStore(wrongSourceProfile);
        var wrongSource = wrongSourceStore.Resolve(
            nvidiaSettings,
            wrongSourceState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        var syntheticNvidiaState = State(wrongSourceClips);
        syntheticNvidiaState.CaptureSource = ClipCaptureSource.Nvidia;
        wrongSourceState.Save(syntheticNvidiaState);
        var wrongSourceAfterSyntheticState = AdmissionStore(wrongSourceProfile).Resolve(
            nvidiaSettings,
            wrongSourceState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now.AddMinutes(1));
        Assert(wrongSource.Loaded && wrongSource.Document!.Admission ==
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile &&
               !wrongSource.LegacyRuntimeAllowed &&
               wrongSourceAfterSyntheticState.Document == wrongSource.Document,
            "Mismatched capture sources must durably stay non-legacy after 2.0 re-baselines the configured source.");

        var invalidDiscordProfile = Path.Combine(root, "invalid-discord");
        var invalidDiscordClips = Directory.CreateDirectory(
            Path.Combine(invalidDiscordProfile, "clips")).FullName;
        var invalidDiscordState = StateStore(invalidDiscordProfile);
        invalidDiscordState.Save(State(invalidDiscordClips));
        var invalidDiscordStore = AdmissionStore(invalidDiscordProfile);
        var invalidDiscord = invalidDiscordStore.Resolve(
            Settings(invalidDiscordClips, upload: true) with
            {
                WebhookUrl = "https://example.com/not-discord"
            },
            invalidDiscordState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        var repairedDiscord = invalidDiscordStore.Resolve(
            Settings(invalidDiscordClips, upload: true),
            invalidDiscordState.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now.AddMinutes(1));
        Assert(invalidDiscord.Loaded && invalidDiscord.Document!.Admission ==
                   LegacyRoutingMigrationAdmission.FreshOrInvalidProfile &&
               !invalidDiscord.LegacyRuntimeAllowed &&
               repairedDiscord.Document == invalidDiscord.Document,
            "Invalid Discord settings must durably stay non-legacy after 2.0 repairs the settings.");
    }

    private static void AssertConcurrentResolutionIsStable(string root)
    {
        var profile = Path.Combine(root, "concurrent-resolution");
        var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
        var stateStore = StateStore(profile);
        WriteHistoricalState(stateStore.StatePath, version: 3, clips);
        var settings = Settings(clips, upload: false);
        var probe = stateStore.ProbeForLegacyMigrationAdmission();
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => AdmissionStore(profile).Resolve(
                settings,
                probe,
                LegacyRoutingSettingsPresence.Present,
                now: Now)))
            .ToArray();
        Task.WaitAll(tasks);
        var documents = tasks.Select(task => task.Result.Document).ToArray();
        Assert(documents.All(document => document == documents[0]) &&
               documents[0]?.Admission == LegacyRoutingMigrationAdmission.ValidLegacyUpgrade,
            "Concurrent first resolution must produce one identical immutable positive decision.");
    }

    private static void AssertFreshPlannerCreatesNoRoutingArtifacts(string root)
    {
        var profile = Path.Combine(root, "fresh-planner");
        var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
        var capture = Directory.CreateDirectory(Path.Combine(profile, "capture")).FullName;
        var routes = new RoutingSnapshotStore(Path.Combine(
            profile,
            "routing",
            RoutingSnapshotStore.FileName));
        var markers = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            profile,
            "routing",
            LegacyRoutingMigrationMarkerStore.FileName));
        var result = new LegacyRoutingMigrationCoordinator(routes, markers)
            .ExecuteAsync(new LegacyRoutingMigrationInput(
                Settings(clips, upload: true),
                State(clips),
                LegacyWorkerQuiesced: true,
                ["discord.11111111111111111111111111111111"],
                RoutingCaptureLibraryBindingModel.Create(capture),
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile), Now)
            .GetAwaiter()
            .GetResult();
        Assert(result.Status == LegacyRoutingCutoverResultStatus.Blocked &&
               result.Readiness.Status ==
               LegacyRoutingCutoverReadinessStatus.NoLegacyUpgradeEvidence &&
               routes.Load().Status == RoutingDocumentLoadStatus.Missing &&
               markers.Load().Status == RoutingDocumentLoadStatus.Missing,
            "A fresh profile must leave both the route snapshot and legacy cutover marker absent.");
        Assert(LegacyRoutingMigrationPlanner.ImportedRouteLabel ==
               "Imported from ClipCord 1.x",
            "The one genuine-upgrade route must have the approved user-facing label.");
    }

    private static void AssertCorruptAdmissionFailsClosed(string root)
    {
        var profile = Path.Combine(root, "corrupt-admission");
        var clips = Directory.CreateDirectory(Path.Combine(profile, "clips")).FullName;
        var stateStore = StateStore(profile);
        stateStore.Save(State(clips));
        var store = AdmissionStore(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "{not valid json");
        var decision = store.Resolve(
            Settings(clips, upload: true),
            stateStore.ProbeForLegacyMigrationAdmission(),
            LegacyRoutingSettingsPresence.Present,
            now: Now);
        Assert(!decision.Loaded && !decision.AllowsLegacyImport &&
               !decision.LegacyRuntimeAllowed &&
               decision.Status == RoutingDocumentLoadStatus.Corrupt &&
               File.ReadAllText(store.Path) == "{not valid json",
            "A corrupt durable admission record must block import without being replaced.");
    }

    private static LegacyRoutingMigrationAdmissionStore AdmissionStore(string profile) => new(
        Path.Combine(profile, "routing", LegacyRoutingMigrationAdmissionStore.FileName));

    private static WatchStateStore StateStore(string profile) => new(
        Path.Combine(profile, "state.json"),
        Path.Combine(profile, ".safe-baseline-required"));

    private static WatchState State(string clips) => new()
    {
        Version = 4,
        ClipsFolder = clips,
        CaptureSource = ClipCaptureSource.SteelSeriesGg
    };

    private static void WriteHistoricalState(string path, int version, string clips)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object state = version switch
        {
            2 => new
            {
                Version = version,
                ClipsFolder = clips,
                KnownContentHashes = Array.Empty<string>(),
                UploadedContentHashes = Array.Empty<string>(),
                IgnoredFileKeys = Array.Empty<string>(),
                PendingMoves = Array.Empty<string>()
            },
            3 => new
            {
                Version = version,
                ClipsFolder = clips,
                KnownContentHashes = Array.Empty<string>(),
                UploadedContentHashes = Array.Empty<string>(),
                LocalOnlyContentHashes = Array.Empty<string>(),
                IgnoredFileKeys = Array.Empty<string>(),
                PendingMoves = Array.Empty<string>(),
                PendingLocalOnlyMoves = Array.Empty<string>()
            },
            _ => new
            {
                Version = version,
                ClipsFolder = clips,
                CaptureSource = ClipCaptureSource.SteelSeriesGg,
                KnownContentHashes = Array.Empty<string>(),
                UploadedContentHashes = Array.Empty<string>(),
                LocalOnlyContentHashes = Array.Empty<string>(),
                IgnoredFileKeys = Array.Empty<string>(),
                PendingMoves = Array.Empty<string>(),
                PendingLocalOnlyMoves = Array.Empty<string>(),
                PendingEditedUploads = Array.Empty<object>()
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(state));
    }

    private const string Webhook =
        "https://discord.com/api/webhooks/123456789012345678/legacy-secret-token";

    private static AppSettings Settings(string clips, bool upload) => new(
        clips,
        Webhook,
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Migration tester",
        upload,
        GlobalHotkeyBinding.DefaultDisplayText,
        ClipCaptureSource.SteelSeriesGg);

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
}
