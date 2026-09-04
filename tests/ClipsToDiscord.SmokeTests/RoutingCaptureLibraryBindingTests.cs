using System.Text.Json;
using System.Reflection;
using ClipsToDiscord;

internal static class RoutingCaptureLibraryBindingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 18, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        AssertPrivacySafeStableNativeBinding(testRoot);
        AssertRetainedRootHandlePreventsReplacement(testRoot);
        AssertCaptureLibrarySwitchPolicyIsFailClosed();
        AssertCaptureLibraryEventAuthorityRequiresPinnedRoot(testRoot);
        AssertExactRootRepairIsPrevalidatedAndVerified(testRoot);
        AssertExactRootRepairRollsBackAndPreservesFailure(testRoot);
        AssertLiveCaptureLibraryPermitFailsClosed(testRoot);
        AssertCaptureSettingsInspectionIsStrict(testRoot);
        AssertTrayUsesStrictCaptureSettingsEvidence();
        AssertTrayContinuouslyMonitorsCaptureLibraryAuthority();
        await AssertRoutedCaptureLibrarySwitchIsCrashSafeAsync(
            Path.Combine(testRoot, "routed-library-switch"));
        await CaptureLibraryShutdownCoordinationTests.RunAsync(
            Path.Combine(testRoot, "capture-library-shutdown-coordination"));
        await CaptureRecoveryAuthorityTests.RunAsync(
            Path.Combine(testRoot, "capture-recovery-authority"));
        await AssertDurableEvidenceBindsFingerprintsAsync(
            Path.Combine(testRoot, "durable-binding-integrity"));
        await AssertColdStartFailsClosedUntilExactLibraryReturnsAsync(testRoot);
    }

    private static void AssertPrivacySafeStableNativeBinding(string testRoot)
    {
        var parent = Path.Combine(testRoot, "binding-model");
        var library = Path.Combine(parent, "capture-library-private-sentinel");
        Directory.CreateDirectory(library);

        var original = RoutingCaptureLibraryBindingModel.Create(library);
        AssertCanonicalSha256(original.CanonicalPathFingerprint,
            "The Capture library path fingerprint must be canonical uppercase SHA-256.");
        AssertCanonicalSha256(original.NativeDirectoryIdentityFingerprint,
            "The Capture library native-identity fingerprint must be canonical uppercase SHA-256.");

        var serialized = JsonSerializer.Serialize(original);
        var canonicalLibrary = Path.GetFullPath(library);
        using var serializedDocument = JsonDocument.Parse(serialized);
        var serializedProperties = serializedDocument.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert(serialized.Contains(original.CanonicalPathFingerprint, StringComparison.Ordinal) &&
               serialized.Contains(original.NativeDirectoryIdentityFingerprint, StringComparison.Ordinal),
            "The serialized Capture library binding must retain both opaque fingerprints.");
        Assert(!serialized.Contains(canonicalLibrary, StringComparison.OrdinalIgnoreCase) &&
               !serialized.Contains("capture-library-private-sentinel", StringComparison.OrdinalIgnoreCase) &&
               serializedProperties.SequenceEqual(
                   [
                       nameof(RoutingCaptureLibraryBinding.CanonicalPathFingerprint),
                       nameof(RoutingCaptureLibraryBinding.NativeDirectoryIdentityFingerprint)
                   ],
                   StringComparer.Ordinal),
            "The serialized Capture library binding must expose only opaque fingerprints, never path or user metadata.");

        var child = Path.Combine(library, "Game", "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        File.WriteAllBytes(child, [1, 2, 3]);
        File.WriteAllBytes(child, [4, 5, 6, 7]);
        var afterChildChanges = RoutingCaptureLibraryBindingModel.Create(
            library + Path.DirectorySeparatorChar);
        Assert(afterChildChanges == original,
            "Child file changes and a trailing separator must not change the Capture library binding.");

        var renamed = Path.Combine(parent, "capture-library-renamed");
        Directory.Move(library, renamed);
        var afterRename = RoutingCaptureLibraryBindingModel.Create(renamed);
        Assert(afterRename.CanonicalPathFingerprint != original.CanonicalPathFingerprint &&
               afterRename.NativeDirectoryIdentityFingerprint ==
               original.NativeDirectoryIdentityFingerprint,
            "Renaming the same directory must change only its canonical-path fingerprint.");
        Directory.Move(renamed, library);
        Assert(RoutingCaptureLibraryBindingModel.Create(library) == original,
            "Moving the original directory back to its canonical path must restore its exact binding.");

        var parkedOriginal = Path.Combine(parent, "capture-library-original-parked");
        Directory.Move(library, parkedOriginal);
        Directory.CreateDirectory(library);
        var replacement = RoutingCaptureLibraryBindingModel.Create(library);
        Assert(replacement.CanonicalPathFingerprint == original.CanonicalPathFingerprint &&
               replacement.NativeDirectoryIdentityFingerprint !=
               original.NativeDirectoryIdentityFingerprint,
            "Replacing a directory at the same path must change only its native-identity fingerprint.");

        Directory.Delete(library, recursive: true);
        Directory.Move(parkedOriginal, library);
        var restored = RoutingCaptureLibraryBindingModel.Create(library);
        Assert(restored == original,
            "Restoring the original directory object at the original path must restore its exact binding.");

        AssertThrows<InvalidDataException>(() => RoutingCaptureLibraryBindingModel.Validate(
                original with
                {
                    CanonicalPathFingerprint =
                        original.CanonicalPathFingerprint.ToLowerInvariant()
                }),
            "Lowercase SHA-256 text must not be accepted as a canonical Capture library binding.");
    }

    private static async Task AssertRoutedCaptureLibrarySwitchIsCrashSafeAsync(string root)
    {
        var oldRoot = Directory.CreateDirectory(Path.Combine(root, "old-library")).FullName;
        var newRoot = Directory.CreateDirectory(Path.Combine(root, "new-library")).FullName;
        var thirdRoot = Directory.CreateDirectory(Path.Combine(root, "third-library")).FullName;
        var oldBinding = RoutingCaptureLibraryBindingModel.Create(oldRoot);
        var newBinding = RoutingCaptureLibraryBindingModel.Create(newRoot);
        var thirdBinding = RoutingCaptureLibraryBindingModel.Create(thirdRoot);
        var evidence = await CreateCommittedEvidenceAsync(
            Path.Combine(root, "authority"),
            oldBinding,
            withExecutionAuthority: true);
        var authority = evidence.Authority.Load().Document ??
            throw new InvalidOperationException(
                "The routed Capture-library switch fixture is missing authority.");
        var switchPath = Path.Combine(
            root,
            "switch-state",
            RoutingCaptureLibrarySwitchStore.FileName);
        var store = new RoutingCaptureLibrarySwitchStore(switchPath, () => Now.AddMinutes(1));

        Assert(store.Resolve(authority, oldBinding) == oldBinding,
            "Missing switch state must retain the Capture library from original Routing authority.");
        var abandonedBeforeSettings = store.Prepare(authority, oldBinding, newBinding);
        Assert(store.Resolve(authority, oldBinding) == oldBinding &&
               store.Load().Document is
               {
                   Phase: RoutingCaptureLibrarySwitchPhase.Stable,
                   PendingBinding: null
               } aborted &&
               aborted.EffectiveBinding == oldBinding,
            "A crash before Capture settings change must durably abort the prepared switch.");

        var abandonedAfterSettings = store.Prepare(authority, oldBinding, newBinding);
        Assert(store.Resolve(authority, newBinding) == newBinding &&
               store.Load().Document is
               {
                   Phase: RoutingCaptureLibrarySwitchPhase.Stable,
                   PendingBinding: null
               } recovered &&
               recovered.EffectiveBinding == newBinding,
            "A crash after Capture settings change must durably finish the prepared switch.");

        var committedThenFailed = store.Prepare(authority, newBinding, thirdBinding);
        Assert(store.Commit(committedThenFailed) == thirdBinding &&
               store.RollBack(authority, committedThenFailed) == newBinding &&
               store.Resolve(authority, newBinding) == newBinding,
            "A post-commit failure must be reversible through a new durable switch generation.");
        AssertThrows<InvalidDataException>(() => store.Resolve(authority, thirdBinding),
            "A settings root outside the stable switch lineage must fail closed.");

        var serialized = File.ReadAllText(switchPath);
        Assert(!serialized.Contains(oldRoot, StringComparison.OrdinalIgnoreCase) &&
               !serialized.Contains(newRoot, StringComparison.OrdinalIgnoreCase) &&
               !serialized.Contains(thirdRoot, StringComparison.OrdinalIgnoreCase),
            "Capture-library switch authority must persist opaque bindings, never local paths.");
    }

    private static void AssertCaptureSettingsInspectionIsStrict(string testRoot)
    {
        var parent = Path.Combine(testRoot, "settings-inspection");
        Directory.CreateDirectory(parent);
        var settingsPath = Path.Combine(parent, "capture-settings.json");
        var library = Path.Combine(parent, "Capture Library");
        Directory.CreateDirectory(library);

        var missing = CaptureSettingsStore.Inspect(settingsPath);
        Assert(missing is
               {
                   Status: CaptureSettingsDocumentStatus.Missing,
                   Settings: null,
                   Error: null
               },
            "An absent injected Capture settings path must report Missing without synthesizing defaults.");

        var expected = CaptureSettings.Default with { LibraryRoot = Path.GetFullPath(library) };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(expected));
        var loaded = CaptureSettingsStore.Inspect(settingsPath);
        Assert(loaded is
               {
                   Status: CaptureSettingsDocumentStatus.Loaded,
                   Settings: not null,
                   Error: null
               } &&
               loaded.Settings.LibraryRoot.Equals(
                   expected.LibraryRoot,
                   StringComparison.OrdinalIgnoreCase),
            "A valid injected Capture settings document must report Loaded with its explicit library root.");

        File.WriteAllText(settingsPath, "{ definitely-not-json }");
        AssertInvalid(settingsPath,
            "Malformed Capture settings must report Invalid without default substitution.");

        File.WriteAllText(settingsPath, "{}");
        AssertInvalid(settingsPath,
            "Capture settings without an explicit library root must report Invalid.");

        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(expected with { LibraryRoot = "relative-capture-library" }));
        AssertInvalid(settingsPath,
            "A relative Capture library root must report Invalid.");

        File.WriteAllBytes(settingsPath, []);
        AssertInvalid(settingsPath,
            "An empty Capture settings document must report Invalid.");

        File.WriteAllBytes(settingsPath, new byte[CaptureSettingsStore.MaximumDocumentBytes + 1]);
        AssertInvalid(settingsPath,
            "An oversized Capture settings document must report Invalid before deserialization.");

        File.Delete(settingsPath);
        Directory.CreateDirectory(settingsPath);
        AssertInvalid(settingsPath,
            "A directory occupying the Capture settings path must report Invalid rather than Missing.");
    }

    private static void AssertRetainedRootHandlePreventsReplacement(string testRoot)
    {
        var root = Directory.CreateDirectory(
            Path.Combine(testRoot, "retained-root-handle", "capture-library")).FullName;
        var moved = root + "-moved";
        var pin = RoutingWatchedFileSystem.OpenOrdinaryRoot(root);
        var renameBlocked = false;
        var deleteBlocked = false;
        try
        {
            try
            {
                Directory.Delete(root);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                deleteBlocked = true;
            }

            try
            {
                Directory.Move(root, moved);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                renameBlocked = true;
            }
        }
        finally
        {
            pin.Handle.Dispose();
        }

        var rootExistedWhilePinned = Directory.Exists(root);
        var movedExistedWhilePinned = Directory.Exists(moved);
        if (rootExistedWhilePinned)
        {
            Directory.Move(root, moved);
        }
        if (Directory.Exists(moved))
        {
            Directory.Delete(moved);
        }
        Assert(renameBlocked && deleteBlocked && rootExistedWhilePinned &&
               !movedExistedWhilePinned && !Directory.Exists(root) &&
               !Directory.Exists(moved),
            "The retained native Capture-root handle must deny rename and deletion until disposal, then release both operations. " +
            $"RenameBlocked={renameBlocked}; DeleteBlocked={deleteBlocked}; " +
            $"RootExistedWhilePinned={rootExistedWhilePinned}; " +
            $"MovedExistedWhilePinned={movedExistedWhilePinned}.");
    }

    private static void AssertCaptureLibrarySwitchPolicyIsFailClosed()
    {
        var idleCamera = new ReactionCameraRuntimeStatus(false, false);
        foreach (var manualState in Enum.GetValues<ManualCaptureState>().Where(state =>
                     state is not (ManualCaptureState.NoTarget or ManualCaptureState.Ready)))
        {
            AssertThrows<InvalidOperationException>(
                () => TrayCaptureLibrarySwitchPolicy.RequireIdle(
                    manualState,
                    ReplayCaptureState.Off,
                    idleCamera,
                    silhouetteProcessingIdle: true),
                $"Capture root switching must reject manual state {manualState}.");
        }

        foreach (var replayState in Enum.GetValues<ReplayCaptureState>().Where(state =>
                     state != ReplayCaptureState.Off))
        {
            AssertThrows<InvalidOperationException>(
                () => TrayCaptureLibrarySwitchPolicy.RequireIdle(
                    ManualCaptureState.Ready,
                    replayState,
                    idleCamera,
                    silhouetteProcessingIdle: true),
                $"Capture root switching must reject replay state {replayState}.");
        }

        ReactionCameraRuntimeStatus[] nonIdleCameraStates =
        [
            new(ManualCaptureActive: true, InstantReplayActive: false),
            new(ManualCaptureActive: false, InstantReplayActive: true),
            new(
                ManualCaptureActive: false,
                InstantReplayActive: false,
                ManualCaptureStarting: true),
            new(
                ManualCaptureActive: false,
                InstantReplayActive: false,
                InstantReplayStarting: true),
            new(
                ManualCaptureActive: false,
                InstantReplayActive: false,
                ReleaseNeedsAttention: true)
        ];
        foreach (var cameraStatus in nonIdleCameraStates)
        {
            AssertThrows<InvalidOperationException>(
                () => TrayCaptureLibrarySwitchPolicy.RequireIdle(
                    ManualCaptureState.Ready,
                    ReplayCaptureState.Off,
                    cameraStatus,
                    silhouetteProcessingIdle: true),
                $"Capture root switching must reject Reaction Camera state {cameraStatus.State}.");
        }

        AssertThrows<InvalidOperationException>(
            () => TrayCaptureLibrarySwitchPolicy.RequireIdle(
                ManualCaptureState.Ready,
                ReplayCaptureState.Off,
                idleCamera,
                silhouetteProcessingIdle: false),
            "Capture root switching must reject queued or active silhouette processing.");

        TrayCaptureLibrarySwitchPolicy.RequireIdle(
            ManualCaptureState.NoTarget,
            ReplayCaptureState.Off,
            idleCamera,
            silhouetteProcessingIdle: true);
        TrayCaptureLibrarySwitchPolicy.RequireIdle(
            ManualCaptureState.Ready,
            ReplayCaptureState.Off,
            idleCamera,
            silhouetteProcessingIdle: true);
    }

    private static void AssertCaptureLibraryEventAuthorityRequiresPinnedRoot(string testRoot)
    {
        var parent = Path.Combine(testRoot, "event-root-authority");
        var authorizedRoot = Directory.CreateDirectory(
            Path.Combine(parent, "authorized-capture-library")).FullName;
        var staleRoot = Directory.CreateDirectory(
            Path.Combine(parent, "stale-capture-library")).FullName;
        var expected = RoutingCaptureLibraryBindingModel.Create(authorizedRoot);
        var pin = RoutingWatchedFileSystem.OpenOrdinaryRoot(authorizedRoot);
        try
        {
            var permit = new RoutingCaptureLibraryPermit(
                expected,
                () => RoutingCaptureLibraryBindingModel.Create(authorizedRoot));
            TrayCaptureLibraryEventAuthority.RequireCurrentRoot(
                authorizedRoot + Path.DirectorySeparatorChar,
                permit,
                "accepting an exact pinned-root event");
            AssertThrows<InvalidDataException>(
                () => TrayCaptureLibraryEventAuthority.RequireCurrentRoot(
                    staleRoot,
                    permit,
                    "rejecting a stale-root event"),
                "Capture completion and retry events must reject a root other than the exact pinned root.");
            Assert(!pin.Handle.IsInvalid && !pin.Handle.IsClosed &&
                   Directory.Exists(authorizedRoot) && Directory.Exists(staleRoot),
                "Capture event-root validation must leave the retained pin and both roots unchanged.");
        }
        finally
        {
            pin.Handle.Dispose();
        }
    }

    private static void AssertExactRootRepairIsPrevalidatedAndVerified(string testRoot)
    {
        var parent = Path.Combine(testRoot, "exact-root-repair");
        var original = Directory.CreateDirectory(
            Path.Combine(parent, "capture-library")).FullName;
        var expected = RoutingCaptureLibraryBindingModel.Create(original);
        var currentSettings = CaptureSettings.Default with
        {
            LibraryRoot = Path.Combine(parent, "broken-setting")
        };
        var persistCalls = 0;
        var verifyCalls = 0;
        CaptureSettings? persisted = null;
        void Persist(CaptureSettings settings)
        {
            persistCalls++;
            persisted = settings;
        }
        RoutingCaptureLibraryBinding Verify(string candidate)
        {
            verifyCalls++;
            Assert(persisted is not null &&
                   CaptureJournalStore.NormalizeLibraryRoot(persisted.LibraryRoot).Equals(
                       CaptureJournalStore.NormalizeLibraryRoot(candidate),
                       StringComparison.OrdinalIgnoreCase),
                "Capture repair verification must run after the canonical root is persisted.");
            return RoutingCaptureLibraryBindingModel.Create(candidate);
        }

        var renamedOriginal = Path.Combine(parent, "capture-library-renamed");
        Directory.Move(original, renamedOriginal);
        AssertThrows<InvalidDataException>(() =>
                RoutingCaptureLibraryRepair.RestoreExactRoot(
                    currentSettings,
                    renamedOriginal,
                    expected,
                    Persist,
                    Verify),
            "Repair must reject the same native directory at a different canonical path before persistence.");
        Assert(persistCalls == 0 && verifyCalls == 0 && persisted is null,
            "A canonical-path repair mismatch must perform zero persistence or verification calls.");
        Directory.Move(renamedOriginal, original);

        var parkedOriginal = Path.Combine(parent, "capture-library-parked");
        Directory.Move(original, parkedOriginal);
        Directory.CreateDirectory(original);
        AssertThrows<InvalidDataException>(() =>
                RoutingCaptureLibraryRepair.RestoreExactRoot(
                    currentSettings,
                    original,
                    expected,
                    Persist,
                    Verify),
            "Repair must reject a replacement directory at the exact authorized path before persistence.");
        Assert(persistCalls == 0 && verifyCalls == 0 && persisted is null,
            "A native-directory repair mismatch must perform zero persistence or verification calls.");
        Directory.Delete(original);
        Directory.Move(parkedOriginal, original);

        var repaired = RoutingCaptureLibraryRepair.RestoreExactRoot(
            currentSettings,
            original + Path.DirectorySeparatorChar,
            expected,
            Persist,
            Verify);
        var canonicalOriginal = CaptureJournalStore.NormalizeLibraryRoot(original);
        Assert(persistCalls == 1 && verifyCalls == 1 && persisted == repaired &&
               repaired.LibraryRoot.Equals(
                   canonicalOriginal,
                   StringComparison.OrdinalIgnoreCase) &&
               RoutingCaptureLibraryBindingModel.Create(repaired.LibraryRoot) == expected,
            "Repairing the exact original directory must persist once, verify once, and return its canonical authorized root.");
    }

    private static void AssertExactRootRepairRollsBackAndPreservesFailure(string testRoot)
    {
        var parent = Path.Combine(testRoot, "exact-root-repair-rollback");
        var authorizedRoot = Directory.CreateDirectory(
            Path.Combine(parent, "capture-library")).FullName;
        var expected = RoutingCaptureLibraryBindingModel.Create(authorizedRoot);
        var currentSettings = CaptureSettings.Default with
        {
            LibraryRoot = Path.Combine(parent, "broken-setting")
        };
        var verificationFailure = new InvalidDataException(
            "post-persist Capture root verification failed");
        var events = new List<string>();
        Exception? observed = null;
        try
        {
            _ = RoutingCaptureLibraryRepair.RestoreExactRoot(
                currentSettings,
                authorizedRoot,
                expected,
                settings =>
                {
                    Assert(CaptureJournalStore.NormalizeLibraryRoot(settings.LibraryRoot).Equals(
                            CaptureJournalStore.NormalizeLibraryRoot(authorizedRoot),
                            StringComparison.OrdinalIgnoreCase),
                        "The rollback fixture must persist the repaired canonical root first.");
                    events.Add("persist");
                },
                _ =>
                {
                    events.Add("verify");
                    throw verificationFailure;
                },
                () => events.Add("rollback"));
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        Assert(ReferenceEquals(observed, verificationFailure) &&
               events.SequenceEqual(["persist", "verify", "rollback"]),
            "A post-persist Capture repair verification failure must invoke rollback exactly " +
            "after verification and rethrow the original exception unchanged.");
    }

    private static void AssertLiveCaptureLibraryPermitFailsClosed(string testRoot)
    {
        var library = Directory.CreateDirectory(
            Path.Combine(testRoot, "live-permit", "capture-library")).FullName;
        var expected = RoutingCaptureLibraryBindingModel.Create(library);
        RoutingCaptureLibraryBinding current = expected;
        Exception? currentError = null;
        var probeCount = 0;
        var permit = new RoutingCaptureLibraryPermit(
            expected,
            () =>
            {
                probeCount++;
                return currentError is null ? current : throw currentError;
            });
        Assert(permit.Inspect() is
               {
                   State: RoutingCaptureLibraryPermitState.Allowed,
                   Allowed: true,
                   Error: null
               },
            "An exact live Capture-library identity must be permitted.");

        current = expected with
        {
            CanonicalPathFingerprint = DifferentFingerprint(
                expected.CanonicalPathFingerprint)
        };
        var mismatchRevocation = permit.Inspect();
        Assert(mismatchRevocation is
               {
                   State: RoutingCaptureLibraryPermitState.Mismatch,
                   Allowed: false,
                   CurrentBinding: not null,
                   Error: null
               },
            "A live canonical-path mismatch must revoke the Capture-library permit.");

        var probesAtMismatch = probeCount;
        current = expected;
        var restoredAfterMismatch = permit.Inspect();
        Assert(ReferenceEquals(restoredAfterMismatch, mismatchRevocation) &&
               !restoredAfterMismatch.Allowed &&
               restoredAfterMismatch.State == RoutingCaptureLibraryPermitState.Mismatch &&
               probeCount == probesAtMismatch,
            "A Capture-library permit must remain permanently revoked after one mismatch, " +
            "even if the exact identity is later restored; a revoked permit must not probe again.");
        AssertThrows<RoutingCaptureLibraryPermitException>(
            () => permit.RequireCurrent("test admission after restored mismatch"),
            "Restoring exact identity must not re-admit work through a mismatch-revoked permit.");

        var nativeMismatchCurrent = expected with
        {
            NativeDirectoryIdentityFingerprint = DifferentFingerprint(
                expected.NativeDirectoryIdentityFingerprint)
        };
        var nativeMismatchPermit = new RoutingCaptureLibraryPermit(
            expected,
            () => nativeMismatchCurrent);
        Assert(nativeMismatchPermit.Inspect() is
               {
                   State: RoutingCaptureLibraryPermitState.Mismatch,
                   Allowed: false,
                   CurrentBinding: not null,
                   Error: null
               },
            "A live native-directory mismatch must revoke the Capture-library permit.");

        RoutingCaptureLibraryBinding unavailableCurrent = expected;
        Exception? unavailableError = null;
        var unavailableProbeCount = 0;
        var unavailablePermit = new RoutingCaptureLibraryPermit(
            expected,
            () =>
            {
                unavailableProbeCount++;
                return unavailableError is null
                    ? unavailableCurrent
                    : throw unavailableError;
            });
        Assert(unavailablePermit.Inspect().Allowed,
            "The unavailable-evidence fixture must begin with an allowed exact identity.");
        unavailableError = new IOException("strict Capture identity unavailable");
        var unavailableRevocation = unavailablePermit.Inspect();
        Assert(unavailableRevocation is
               {
                   State: RoutingCaptureLibraryPermitState.Unavailable,
                   Allowed: false,
                   CurrentBinding: null,
                   Error: IOException
               },
            "Unavailable strict Capture identity evidence must revoke the live permit.");
        var probesAtUnavailable = unavailableProbeCount;
        unavailableError = null;
        unavailableCurrent = expected;
        var restoredAfterUnavailable = unavailablePermit.Inspect();
        Assert(ReferenceEquals(restoredAfterUnavailable, unavailableRevocation) &&
               !restoredAfterUnavailable.Allowed &&
               restoredAfterUnavailable.State == RoutingCaptureLibraryPermitState.Unavailable &&
               unavailableProbeCount == probesAtUnavailable,
            "A Capture-library permit must remain permanently revoked after one unavailable " +
            "observation, even if exact evidence is later restored; a revoked permit must not probe again.");
        AssertThrows<RoutingCaptureLibraryPermitException>(
            () => unavailablePermit.RequireCurrent(
                "test admission after restored unavailable evidence"),
            "Restoring evidence must not re-admit work through an unavailable-revoked permit.");
    }

    private static void AssertTrayUsesStrictCaptureSettingsEvidence()
    {
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags staticInternal = BindingFlags.Static | BindingFlags.NonPublic;
        var provider = typeof(TrayApplicationContext).GetMethod(
            "RequireCurrentCaptureLibraryBinding",
            instancePrivate) ?? throw new InvalidOperationException(
            "The Tray Capture-library authority provider is unavailable.");
        var inspect = typeof(CaptureSettingsStore).GetMethod(
            nameof(CaptureSettingsStore.Inspect),
            staticInternal) ?? throw new InvalidOperationException(
            "The strict Capture settings inspection seam is unavailable.");
        var fallbackLoad = typeof(CaptureSettingsStore).GetMethod(
            nameof(CaptureSettingsStore.Load),
            staticInternal) ?? throw new InvalidOperationException(
            "The fallback Capture settings load seam is unavailable.");
        var compareDurableRoot = typeof(TrayApplicationContext).GetMethod(
            "RequireSameCaptureLibraryRoot",
            staticInternal) ?? throw new InvalidOperationException(
            "The durable/in-memory Capture root comparison seam is unavailable.");
        var createBinding = typeof(RoutingCaptureLibraryBindingModel).GetMethod(
            nameof(RoutingCaptureLibraryBindingModel.Create),
            staticInternal,
            [typeof(string)]) ?? throw new InvalidOperationException(
            "The native Capture-library binding seam is unavailable.");

        Assert(CallsDirectly(provider, inspect) &&
               !CallsDirectly(provider, fallbackLoad) &&
               CallsDirectly(provider, compareDurableRoot) &&
               CallsDirectly(provider, createBinding),
            "Tray authority bootstrap must use strict status-bearing Capture settings, compare them with in-memory settings, and derive the native binding without a default-substituting load.");
    }

    private static void AssertTrayContinuouslyMonitorsCaptureLibraryAuthority()
    {
        const BindingFlags instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags staticInternal = BindingFlags.Static | BindingFlags.NonPublic;
        var trayType = typeof(TrayApplicationContext);
        var monitorField = trayType.GetField(
            "_captureLibraryPermitMonitor",
            instancePrivate) ?? throw new InvalidOperationException(
            "The Tray Capture-library monitor field is unavailable.");
        var revocationTaskField = trayType.GetField(
            "_captureLibraryRevocationTask",
            instancePrivate) ?? throw new InvalidOperationException(
            "The Tray Capture-library revocation task field is unavailable.");
        var constructor = trayType.GetConstructors(instance).Single(constructor =>
            constructor.GetParameters().Length == 0);
        var startMonitor = RequireMethod(trayType, "StartCaptureLibraryPermitMonitor");
        var monitor = RequireMethod(trayType, "MonitorCaptureLibraryPermit");
        var stopMonitor = RequireMethod(trayType, "StopCaptureLibraryPermitMonitor");
        var accessAllowed = RequireMethod(trayType, "CaptureLibraryAccessAllowed");
        var scheduleRevocation = RequireMethod(
            trayType,
            "ScheduleCaptureLibraryRevocation");
        var currentRevocation = RequireMethod(
            trayType,
            "CurrentCaptureLibraryRevocationTask");
        var shutdown = RequireMethod(trayType, "ExitThreadCore");
        var shutdownFallback = typeof(TrayShutdownFallbacks).GetMethod(
            nameof(TrayShutdownFallbacks.Run),
            staticInternal) ?? throw new InvalidOperationException(
            "The Tray shutdown convergence seam is unavailable.");
        var revocationJoin = trayType.GetMethods(instancePrivate).SingleOrDefault(method =>
            method.Name.Contains("<ExitThreadCore>", StringComparison.Ordinal) &&
            CallsDirectly(method, currentRevocation) &&
            CallsNamedMethod(method, "GetResult"));
        var stopMonitorOffset = FirstDirectCallOffset(shutdown, stopMonitor);
        var fallbackOffset = FirstDirectCallOffset(shutdown, shutdownFallback);
        var recorderField = trayType.GetField(
            "_manualCaptureRecorder",
            instancePrivate) ?? throw new InvalidOperationException(
            "The Tray manual-recorder field is unavailable.");
        var hostField = trayType.GetField(
            "_captureHostClient",
            instancePrivate) ?? throw new InvalidOperationException(
            "The Tray Capture-host field is unavailable.");
        var recorderDisposeOffset = FirstFieldAccessOffset(shutdown, recorderField);
        var hostDisposeOffset = FirstFieldAccessOffset(shutdown, hostField);

        Assert(monitorField.FieldType == typeof(System.Threading.Timer) &&
               revocationTaskField.FieldType == typeof(Task) &&
               CallsDirectly(constructor, startMonitor) &&
               CallsDirectly(monitor, accessAllowed) &&
               FirstFieldAccessOffset(scheduleRevocation, revocationTaskField) >= 0 &&
               CallsNamedMethod(scheduleRevocation, nameof(Task.Run)) &&
               CallsNamedMethod(stopMonitor, nameof(System.Threading.Timer.DisposeAsync)) &&
               CallsNamedMethod(stopMonitor, "GetResult") &&
               revocationJoin is not null &&
               fallbackOffset >= 0 && stopMonitorOffset > fallbackOffset &&
               stopMonitorOffset < hostDisposeOffset &&
               recorderDisposeOffset > fallbackOffset &&
               hostDisposeOffset > fallbackOffset,
            "Tray must own and bootstrap a continuous System.Threading.Timer permit monitor, " +
            "keep it alive through shutdown convergence, then drain it before the final " +
            "authority decision and Capture-host disposal while joining revocation work.");
    }

    private static async Task AssertDurableEvidenceBindsFingerprintsAsync(string root)
    {
        var captureLibrary = Directory.CreateDirectory(
            Path.Combine(root, "custom-capture-a")).FullName;
        var binding = RoutingCaptureLibraryBindingModel.Create(captureLibrary);
        var (authorityStore, markerStore) = await CreateCommittedEvidenceAsync(
            root,
            binding,
            withExecutionAuthority: true);

        var markerText = File.ReadAllText(markerStore.Path);
        AssertFingerprintTamperRejected(
            markerStore.Path,
            markerText,
            binding.CanonicalPathFingerprint,
            () => markerStore.Load().Status == RoutingDocumentLoadStatus.Invalid,
            "The migration marker payload must integrity-bind the Capture canonical-path fingerprint.");
        AssertFingerprintTamperRejected(
            markerStore.Path,
            markerText,
            binding.NativeDirectoryIdentityFingerprint,
            () => markerStore.Load().Status == RoutingDocumentLoadStatus.Invalid,
            "The migration marker payload must integrity-bind the Capture native-directory fingerprint.");
        File.WriteAllText(markerStore.Path, markerText);

        var authorityText = File.ReadAllText(authorityStore.Path);
        AssertFingerprintTamperRejected(
            authorityStore.Path,
            authorityText,
            binding.CanonicalPathFingerprint,
            () => authorityStore.Inspect().Blocked &&
                  authorityStore.Inspect().LoadStatus == RoutingDocumentLoadStatus.Invalid,
            "Routing execution authority must integrity-bind the Capture canonical-path fingerprint.");
        AssertFingerprintTamperRejected(
            authorityStore.Path,
            authorityText,
            binding.NativeDirectoryIdentityFingerprint,
            () => authorityStore.Inspect().Blocked &&
                  authorityStore.Inspect().LoadStatus == RoutingDocumentLoadStatus.Invalid,
            "Routing execution authority must integrity-bind the Capture native-directory fingerprint.");
        File.WriteAllText(authorityStore.Path, authorityText);
    }

    private static void AssertFingerprintTamperRejected(
        string path,
        string originalDocument,
        string fingerprint,
        Func<bool> rejected,
        string message)
    {
        var replacement = DifferentFingerprint(fingerprint);
        var mutated = originalDocument.Replace(
            fingerprint,
            replacement,
            StringComparison.Ordinal);
        Assert(mutated != originalDocument,
            "The Capture-library fingerprint mutation fixture did not change its document.");
        File.WriteAllText(path, mutated);
        Assert(rejected(), message);
        File.WriteAllText(path, originalDocument);
    }

    private static string DifferentFingerprint(string fingerprint) =>
        (fingerprint[0] == 'A' ? 'B' : 'A') + fingerprint[1..];

    private static MethodInfo RequireMethod(Type type, string name) =>
        type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"The Tray method {name} is unavailable.");

    private static bool CallsDirectly(MethodBase caller, MethodBase callee) =>
        FirstDirectCallOffset(caller, callee) >= 0;

    private static int FirstDirectCallOffset(MethodBase caller, MethodBase callee)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x28 or 0x6F)) continue;
            if (TryResolveMethod(caller, BitConverter.ToInt32(il, offset + 1)) is
                    { } called &&
                called.Module == callee.Module &&
                called.MetadataToken == callee.MetadataToken)
            {
                return offset;
            }
        }
        return -1;
    }

    private static bool CallsNamedMethod(MethodBase caller, string methodName)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x28 or 0x6F)) continue;
            if (TryResolveMethod(caller, BitConverter.ToInt32(il, offset + 1)) is
                    { } called &&
                called.Name.Equals(methodName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static int FirstFieldAccessOffset(MethodBase caller, FieldInfo field)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x7B or 0x7C or 0x7D)) continue;
            try
            {
                var accessed = caller.Module.ResolveField(
                    BitConverter.ToInt32(il, offset + 1),
                    caller.DeclaringType?.GetGenericArguments(),
                    caller is MethodInfo method ? method.GetGenericArguments() : null);
                if (accessed is not null && accessed.Module == field.Module &&
                    accessed.MetadataToken == field.MetadataToken)
                {
                    return offset;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or BadImageFormatException)
            {
            }
        }
        return -1;
    }

    private static MethodBase? TryResolveMethod(MethodBase caller, int metadataToken)
    {
        try
        {
            return caller.Module.ResolveMethod(
                metadataToken,
                caller.DeclaringType?.GetGenericArguments(),
                caller is MethodInfo method ? method.GetGenericArguments() : null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }

    private static async Task AssertColdStartFailsClosedUntilExactLibraryReturnsAsync(
        string testRoot)
    {
        foreach (var withExecutionAuthority in new[] { false, true })
        {
            var kind = withExecutionAuthority ? "authority" : "marker-only";
            var root = Path.Combine(testRoot, "cold-start", kind);
            var captureA = Path.Combine(root, "custom-capture-a");
            var captureB = Path.Combine(root, "alternate-capture-b");
            Directory.CreateDirectory(captureA);
            Directory.CreateDirectory(captureB);
            var aSentinel = WriteSentinel(
                Path.Combine(captureA, "pending-journal-a.sentinel"),
                [0x41, 0x2D, 0x4A, 0x4F, 0x55, 0x52, 0x4E, 0x41, 0x4C]);
            var bSentinel = WriteSentinel(
                Path.Combine(captureB, "pending-journal-b.sentinel"),
                [0x42, 0x2D, 0x4A, 0x4F, 0x55, 0x52, 0x4E, 0x41, 0x4C]);
            var outboxSentinel = WriteSentinel(
                Path.Combine(root, "routing-state", RoutingOutboxStore.FileName),
                [0x4F, 0x55, 0x54, 0x42, 0x4F, 0x58, 0x2D, 0x50, 0x45, 0x4E, 0x44]);

            var settingsPath = Path.Combine(root, "capture-settings.json");
            var settingsA = CaptureSettings.Default with
            {
                LibraryRoot = Path.GetFullPath(captureA)
            };
            var settingsB = settingsA with { LibraryRoot = Path.GetFullPath(captureB) };
            WriteSettings(settingsPath, settingsA);

            var bindingA = RoutingCaptureLibraryBindingModel.Create(captureA);
            var (authorityStore, markerStore) = await CreateCommittedEvidenceAsync(
                root,
                bindingA,
                withExecutionAuthority);
            RoutingCaptureLibraryBinding StrictSettingsBinding()
            {
                var inspection = CaptureSettingsStore.Inspect(settingsPath);
                if (!inspection.Loaded)
                {
                    throw new InvalidDataException(
                        $"Strict Capture settings evidence is {inspection.Status}.",
                        inspection.Error);
                }
                return RoutingCaptureLibraryBindingModel.Create(
                    inspection.Settings!.LibraryRoot);
            }

            File.Delete(settingsPath);
            await AssertColdStartRejectedAsync(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: missing Capture settings",
                aSentinel,
                bSentinel,
                outboxSentinel);
            WriteSettings(settingsPath, settingsA);
            AssertColdStartAllowed(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: restored settings after Missing");

            File.WriteAllText(settingsPath, "{ malformed-capture-settings }");
            await AssertColdStartRejectedAsync(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: malformed Capture settings",
                aSentinel,
                bSentinel,
                outboxSentinel);
            WriteSettings(settingsPath, settingsA);
            AssertColdStartAllowed(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: restored settings after Invalid");

            WriteSettings(settingsPath, settingsB);
            await AssertColdStartRejectedAsync(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: valid settings redirected to B",
                aSentinel,
                bSentinel,
                outboxSentinel);
            WriteSettings(settingsPath, settingsA);
            AssertColdStartAllowed(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: restored settings after B redirect");

            var renamedA = Path.Combine(root, "custom-capture-a-renamed");
            Directory.Move(captureA, renamedA);
            var renamedOriginalSentinel = aSentinel with
            {
                Path = Path.Combine(renamedA, Path.GetFileName(aSentinel.Path))
            };
            WriteSettings(
                settingsPath,
                settingsA with { LibraryRoot = Path.GetFullPath(renamedA) });
            await AssertColdStartRejectedAsync(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: same directory moved to a different canonical path",
                renamedOriginalSentinel,
                bSentinel,
                outboxSentinel);
            Directory.Move(renamedA, captureA);
            WriteSettings(settingsPath, settingsA);
            AssertColdStartAllowed(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: original canonical path restored");

            var parkedA = Path.Combine(root, "custom-capture-a-parked");
            Directory.Move(captureA, parkedA);
            Directory.CreateDirectory(captureA);
            var replacementSentinel = WriteSentinel(
                Path.Combine(captureA, "replacement-directory.sentinel"),
                [0x52, 0x45, 0x50, 0x4C, 0x41, 0x43, 0x45, 0x4D, 0x45, 0x4E, 0x54]);
            var parkedOriginalSentinel = aSentinel with
            {
                Path = Path.Combine(parkedA, Path.GetFileName(aSentinel.Path))
            };
            WriteSettings(settingsPath, settingsA);
            await AssertColdStartRejectedAsync(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: same-path directory replacement",
                parkedOriginalSentinel,
                replacementSentinel,
                bSentinel,
                outboxSentinel);

            Directory.Delete(captureA, recursive: true);
            Directory.Move(parkedA, captureA);
            WriteSettings(settingsPath, settingsA);
            AssertColdStartAllowed(
                authorityStore,
                markerStore,
                StrictSettingsBinding,
                $"{kind}: exact original A restored");
            AssertSentinelsUnchanged(aSentinel, bSentinel, outboxSentinel);
        }
    }

    private static async Task<(RoutingExecutionAuthorityStore Authority,
        LegacyRoutingMigrationMarkerStore Marker)> CreateCommittedEvidenceAsync(
        string root,
        RoutingCaptureLibraryBinding captureLibraryBinding,
        bool withExecutionAuthority)
    {
        var clips = Path.Combine(root, "watched-clips");
        Directory.CreateDirectory(clips);
        var settings = new AppSettings(
            clips,
            string.Empty,
            StartWithWindows: false,
            AppSettings.DefaultCompressionTargetMb,
            "Capture binding tester",
            UploadToDiscord: false,
            GlobalHotkeyBinding.DefaultDisplayText,
            ClipCaptureSource.SteelSeriesGg);
        var state = new WatchState
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
        var readiness = LegacyRoutingMigrationPlanner.Evaluate(
            new LegacyRoutingMigrationInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                DiscordConnectionIds: [],
                captureLibraryBinding,
                LegacyRoutingMigrationAdmission.ValidLegacyUpgrade),
            Now);
        Assert(readiness.CanCommit && readiness.Plan is not null,
            "The Capture library cold-start fixture must produce a valid migration plan.");

        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(
            readiness.Plan!,
            Now);
        var committed = LegacyRoutingMigrationMarkerModel.Commit(
            prepared,
            Now.AddSeconds(1));
        var markerStore = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            root,
            "routing-state",
            LegacyRoutingMigrationMarkerStore.FileName));
        _ = await markerStore.SaveAsync(prepared, expectedGeneration: 0);
        _ = await markerStore.SaveAsync(committed, prepared.Generation);

        var authorityStore = new RoutingExecutionAuthorityStore(Path.Combine(
            root,
            "routing-state",
            RoutingExecutionAuthorityStore.FileName));
        if (withExecutionAuthority)
        {
            _ = await authorityStore.CommitAsync(RoutingExecutionAuthorityModel.Create(
                committed,
                ClipCaptureSource.SteelSeriesGg,
                Now.AddSeconds(2)));
        }
        var captureRoot = Path.GetFullPath(Path.Combine(root, "custom-capture-a"));
        Assert(!File.ReadAllText(markerStore.Path).Contains(
                   captureRoot,
                   StringComparison.OrdinalIgnoreCase) &&
               (!withExecutionAuthority ||
                !File.ReadAllText(authorityStore.Path).Contains(
                    captureRoot,
                    StringComparison.OrdinalIgnoreCase)),
            "Migration and authority evidence must never serialize the Capture library path.");
        return (authorityStore, markerStore);
    }

    private static async Task AssertColdStartRejectedAsync(
        RoutingExecutionAuthorityStore authorityStore,
        LegacyRoutingMigrationMarkerStore markerStore,
        Func<RoutingCaptureLibraryBinding> currentBinding,
        string caseName,
        params FileSentinel[] sentinels)
    {
        var fixture = new RuntimeFixture();
        var lifecycle = RoutingApplicationLifecycle.Create(
            authorityStore,
            fixture.Ownership,
            fixture.Legacy,
            fixture.Routing,
            migrationMarkers: markerStore,
            currentCaptureLibraryBinding: currentBinding);
        var started = await lifecycle.StartAsync();

        Assert(lifecycle.State == RoutingApplicationLifecycleState.NeedsAttention &&
               started.Status == RoutingApplicationLifecycleStatus.NeedsAttention &&
               fixture.Ownership.Owner is null &&
               !TrayCaptureLibraryStartupGate.IsAllowed(lifecycle) &&
               fixture.CallbackCount == 0,
            $"{caseName} must fail closed before ownership, Capture startup, or runtime callbacks.");
        AssertSentinelsUnchanged(sentinels);
    }

    private static void AssertColdStartAllowed(
        RoutingExecutionAuthorityStore authorityStore,
        LegacyRoutingMigrationMarkerStore markerStore,
        Func<RoutingCaptureLibraryBinding> currentBinding,
        string caseName)
    {
        var fixture = new RuntimeFixture();
        var lifecycle = RoutingApplicationLifecycle.Create(
            authorityStore,
            fixture.Ownership,
            fixture.Legacy,
            fixture.Routing,
            migrationMarkers: markerStore,
            currentCaptureLibraryBinding: currentBinding);
        Assert(lifecycle.State == RoutingApplicationLifecycleState.RoutingReady &&
               fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
               TrayCaptureLibraryStartupGate.IsAllowed(lifecycle) &&
               fixture.CallbackCount == 0,
            $"{caseName} must synchronously restore Routing readiness without running callbacks.");
    }

    private static void WriteSettings(string path, CaptureSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings));
    }

    private static FileSentinel WriteSentinel(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new FileSentinel(path, bytes.ToArray());
    }

    private static void AssertSentinelsUnchanged(params FileSentinel[] sentinels)
    {
        foreach (var sentinel in sentinels)
        {
            Assert(File.Exists(sentinel.Path) &&
                   File.ReadAllBytes(sentinel.Path).SequenceEqual(sentinel.Bytes),
                $"Cold-start validation must not mutate sentinel '{Path.GetFileName(sentinel.Path)}'.");
        }
    }

    private sealed class RuntimeFixture
    {
        internal FakeLegacyRuntime Legacy { get; } = new();
        internal FakeRoutingRuntime Routing { get; } = new();
        internal ClipProcessingOwnershipCoordinator Ownership { get; } = new();
        internal int CallbackCount => Legacy.CallbackCount + Routing.CallbackCount;
    }

    private sealed class FakeLegacyRuntime : ILegacyClipProcessingRuntime
    {
        internal int CallbackCount { get; private set; }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            CallbackCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            CallbackCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRoutingRuntime : IRoutingClipProcessingRuntime
    {
        internal int CallbackCount { get; private set; }

        public ValueTask PrepareActivationAsync(CancellationToken cancellationToken)
        {
            CallbackCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitExecutionAuthorityAsync(
            ClipProcessingOwnershipLease ownership)
        {
            CallbackCount++;
            return ValueTask.CompletedTask;
        }

        public RoutingRuntimeGateInspection InspectActivation(
            ClipProcessingOwnershipLease ownership)
        {
            CallbackCount++;
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.Enabled,
                0,
                0,
                0,
                0,
                RoutingGeneration: 1,
                MarkerPayloadFingerprint: new string('A', 64),
                OwnershipEpoch: ownership.Epoch,
                RequiredLegacySource: ClipCaptureSource.SteelSeriesGg,
                CoveredLegacySources: new HashSet<ClipCaptureSource>
                {
                    ClipCaptureSource.SteelSeriesGg,
                    ClipCaptureSource.Nvidia
                },
                ExecutionAuthorityActivationId:
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            CallbackCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            CallbackCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record FileSentinel(string Path, byte[] Bytes);

    private static void AssertInvalid(string path, string message)
    {
        var inspection = CaptureSettingsStore.Inspect(path);
        Assert(inspection.Status == CaptureSettingsDocumentStatus.Invalid &&
               inspection.Settings is null &&
               inspection.Error is not null,
            message);
    }

    private static void AssertCanonicalSha256(string value, string message)
    {
        Assert(value.Length == 64 && value.All(character =>
                character is >= '0' and <= '9' or >= 'A' and <= 'F'),
            message);
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
}
