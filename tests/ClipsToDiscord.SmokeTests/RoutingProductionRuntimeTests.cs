using System.Text;
using ClipsToDiscord;

internal static class RoutingProductionRuntimeTests
{
    private const string Webhook =
        "https://discord.com/api/webhooks/123456789012345678/runtime-private-token";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertLegacyStateCompatibilityRepairAsync(Path.Combine(root, "state-repair"));
        await AssertIgnoredBaselineReconciliationAsync(Path.Combine(root, "baseline"));
        await AssertActualLegacyPreparationAsync(Path.Combine(root, "preparation"));
        await AssertPreparationAuthorityStatesAsync(Path.Combine(root, "authority-states"));
        await AssertProductionLayoutPreflightRestoresLegacyAsync(
            Path.Combine(root, "layout-preflight"));
        await AssertStickyBoundaryAndWorkHostAsync(Path.Combine(root, "sticky"));
        await AssertFreshAuthorityAndMutableSnapshotAsync(Path.Combine(root, "fresh"));
        await AssertFreshSamePathRootReplacementFailsClosedAsync(
            Path.Combine(root, "fresh-root-replacement"));
        await AssertFreshDependencyRevocationFailsClosedAsync(
            Path.Combine(root, "fresh-dependencies"));
        await AssertCommitPreflightFailsClosedAsync(Path.Combine(root, "preflight"));
        await AssertDeterministicColdRepeatAsync(Path.Combine(root, "repeat"));
    }

    private static async Task AssertLegacyStateCompatibilityRepairAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "current-clips")).FullName;
        var oldClips = Directory.CreateDirectory(Path.Combine(root, "old-clips")).FullName;
        var currentClip = Path.Combine(clips, "current.mp4");
        var oldClip = Path.Combine(oldClips, "old.mp4");
        await File.WriteAllBytesAsync(currentClip, [1, 3, 3, 7]);
        await File.WriteAllBytesAsync(oldClip, [2, 4, 6, 8]);
        File.SetLastWriteTimeUtc(currentClip, Now.UtcDateTime);
        File.SetLastWriteTimeUtc(oldClip, Now.AddMinutes(-1).UtcDateTime);

        var store = Store(root);
        var uploadedHash = new string('a', 64);
        var localOnlyHash = new string('b', 64);
        var state = State(clips);
        state.KnownContentHashes.UnionWith([uploadedHash, localOnlyHash]);
        state.UploadedContentHashes.Add(uploadedHash);
        state.LocalOnlyContentHashes.UnionWith([uploadedHash, localOnlyHash]);
        state.IgnoredFileKeys.UnionWith([
            WatchStateStore.FileKey(new FileInfo(currentClip)),
            WatchStateStore.FileKey(new FileInfo(oldClip))
        ]);
        store.Save(state);
        Assert(store.ProbeForRoutingActivation() is
               { Status: WatchStateRoutingProbeStatus.Loaded, IgnoredFileKeys: 2 },
            "The strict probe must accept legitimate overlapping legacy history so the quiesced migration can canonicalize it.");

        var reconciled = await new LegacyIgnoredBaselineReconciler(store).ReconcileAsync(
            Settings(clips, upload: false),
            legacyWorkerQuiesced: true);
        var currentHash = await ContentIdentity.ComputeSha256Async(
            currentClip,
            CancellationToken.None);
        Assert(reconciled.Loaded && reconciled.IgnoredFileKeys == 0,
            "Compatibility repair must leave a strictly loaded, drained watcher baseline.");
        Assert(reconciled.State!.KnownContentHashes.Contains(
                currentHash, StringComparer.OrdinalIgnoreCase),
            "Compatibility repair must retain the exact current baseline as a known-content exclusion.");
        Assert(reconciled.State.UploadedContentHashes.Contains(uploadedHash),
            "Compatibility repair must retain the stronger Uploaded duplicate exclusion.");
        Assert(!reconciled.State.LocalOnlyContentHashes.Contains(uploadedHash),
            "Compatibility repair must remove the weaker overlapping Local-only classification.");
        Assert(reconciled.State.LocalOnlyContentHashes.Contains(localOnlyHash) &&
               File.Exists(oldClip),
            "Compatibility repair must preserve unambiguous Local-only history and never access or remove an out-of-scope clip.");
        var stableBytes = await File.ReadAllBytesAsync(store.StatePath);
        _ = await store.LoadOrInitializeAsync(
            clips,
            _ => { },
            CancellationToken.None,
            ClipCaptureSource.SteelSeriesGg);
        Assert(stableBytes.SequenceEqual(await File.ReadAllBytesAsync(store.StatePath)),
            "A repaired legacy state must be byte-idempotent on the next startup.");

        var sameFolderRoot = Path.Combine(root, "same-folder-precedence");
        var sameFolderClips = Directory.CreateDirectory(
            Path.Combine(sameFolderRoot, "clips")).FullName;
        var sameFolderStore = Store(sameFolderRoot);
        var overlappingHash = new string('c', 64);
        var sameFolderState = State(sameFolderClips);
        sameFolderState.KnownContentHashes.Add(overlappingHash);
        sameFolderState.UploadedContentHashes.Add(overlappingHash);
        sameFolderState.LocalOnlyContentHashes.Add(overlappingHash);
        sameFolderStore.Save(sameFolderState);
        var sameFolderLoaded = await sameFolderStore.LoadOrInitializeAsync(
            sameFolderClips,
            _ => { },
            CancellationToken.None,
            ClipCaptureSource.SteelSeriesGg);
        var sameFolderRaw = sameFolderStore.ProbeForRoutingActivation();
        Assert(sameFolderLoaded.UploadedContentHashes.Contains(overlappingHash) &&
               !sameFolderLoaded.LocalOnlyContentHashes.Contains(overlappingHash) &&
               sameFolderRaw is
               {
                   Status: WatchStateRoutingProbeStatus.Loaded,
                   State: not null
               } &&
               sameFolderRaw.State.UploadedContentHashes.Contains(overlappingHash) &&
               !sameFolderRaw.State.LocalOnlyContentHashes.Contains(overlappingHash),
            "A same-folder/source watcher load must persist Uploaded precedence without relying on the later Routing reconciler.");

        var folderSwitchRoot = Path.Combine(root, "folder-only-switch");
        var oldFolder = Directory.CreateDirectory(
            Path.Combine(folderSwitchRoot, "old-clips")).FullName;
        var newFolder = Directory.CreateDirectory(
            Path.Combine(folderSwitchRoot, "new-clips")).FullName;
        var oldFolderClip = Path.Combine(oldFolder, "old.mp4");
        var newFolderClip = Path.Combine(newFolder, "new.mp4");
        await File.WriteAllBytesAsync(oldFolderClip, [9, 8, 7]);
        await File.WriteAllBytesAsync(newFolderClip, [6, 5, 4]);
        var folderSwitchStore = Store(folderSwitchRoot);
        var folderSwitchState = State(oldFolder);
        folderSwitchState.IgnoredFileKeys.Add(
            WatchStateStore.FileKey(new FileInfo(oldFolderClip)));
        folderSwitchStore.Save(folderSwitchState);
        var folderSwitched = await folderSwitchStore.LoadOrInitializeAsync(
            newFolder,
            _ => { },
            CancellationToken.None,
            ClipCaptureSource.SteelSeriesGg);
        Assert(folderSwitched.ClipsFolder.Equals(newFolder, StringComparison.OrdinalIgnoreCase) &&
               folderSwitched.CaptureSource == ClipCaptureSource.SteelSeriesGg &&
               folderSwitched.IgnoredFileKeys.SetEquals([
                   WatchStateStore.FileKey(new FileInfo(newFolderClip))
               ]),
            "Changing only the clips folder must replace exact ignored keys with the new folder baseline instead of retaining stale paths.");

        var sourceSwitchRoot = Path.Combine(root, "source-only-switch");
        var sharedSourceFolder = Directory.CreateDirectory(
            Path.Combine(sourceSwitchRoot, "clips")).FullName;
        var gameFolder = Directory.CreateDirectory(
            Path.Combine(sharedSourceFolder, "Battlefield 6")).FullName;
        var steelSeriesClip = Path.Combine(sharedSourceFolder, "steelseries.mp4");
        var nvidiaClip = Path.Combine(gameFolder, "nvidia.mp4");
        await File.WriteAllBytesAsync(steelSeriesClip, [5, 4, 3]);
        await File.WriteAllBytesAsync(nvidiaClip, [2, 1, 0]);
        var sourceSwitchStore = Store(sourceSwitchRoot);
        var sourceSwitchState = State(sharedSourceFolder);
        sourceSwitchState.IgnoredFileKeys.Add(
            WatchStateStore.FileKey(new FileInfo(steelSeriesClip)));
        sourceSwitchStore.Save(sourceSwitchState);
        var sourceSwitched = await sourceSwitchStore.LoadOrInitializeAsync(
            sharedSourceFolder,
            _ => { },
            CancellationToken.None,
            ClipCaptureSource.Nvidia);
        Assert(sourceSwitched.ClipsFolder.Equals(sharedSourceFolder, StringComparison.OrdinalIgnoreCase) &&
               sourceSwitched.CaptureSource == ClipCaptureSource.Nvidia &&
               sourceSwitched.IgnoredFileKeys.SetEquals([
                   WatchStateStore.FileKey(new FileInfo(nvidiaClip))
               ]),
            "Changing only the capture source must replace exact ignored keys with the new source geometry instead of retaining stale paths.");
    }

    private static async Task AssertIgnoredBaselineReconciliationAsync(string root)
    {
        var exactRoot = Path.Combine(root, "exact");
        var clips = Path.Combine(exactRoot, "clips");
        Directory.CreateDirectory(clips);
        var clip = Path.Combine(clips, "Battlefield__2026-08-29.mp4");
        await File.WriteAllBytesAsync(clip, Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251)).ToArray());
        File.SetLastWriteTimeUtc(clip, Now.UtcDateTime);
        var store = Store(exactRoot);
        var state = State(clips);
        state.IgnoredFileKeys.Add(WatchStateStore.FileKey(new FileInfo(clip)));
        store.Save(state);
        var before = await File.ReadAllBytesAsync(store.StatePath);
        var reconciler = new LegacyIgnoredBaselineReconciler(store);
        var result = await reconciler.ReconcileAsync(
            Settings(clips, upload: false), legacyWorkerQuiesced: true);
        var hash = await ContentIdentity.ComputeSha256Async(clip, CancellationToken.None);
        Assert(result.Loaded && result.IgnoredFileKeys == 0 &&
               result.State!.KnownContentHashes.Contains(hash, StringComparer.OrdinalIgnoreCase),
            "An exact legacy ignored occurrence must become a content-hash exclusion.");
        var after = await File.ReadAllBytesAsync(store.StatePath);
        _ = await reconciler.ReconcileAsync(
            Settings(clips, upload: false), legacyWorkerQuiesced: true);
        Assert(after.SequenceEqual(await File.ReadAllBytesAsync(store.StatePath)) &&
               !before.SequenceEqual(after),
            "Ignored-baseline reconciliation must persist once and then be byte-idempotent.");

        var modifiedRoot = Path.Combine(root, "modified");
        clips = Path.Combine(modifiedRoot, "clips");
        Directory.CreateDirectory(clips);
        clip = Path.Combine(clips, "changed.mp4");
        await File.WriteAllBytesAsync(clip, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(clip, Now.UtcDateTime);
        store = Store(modifiedRoot);
        state = State(clips);
        state.IgnoredFileKeys.Add(WatchStateStore.FileKey(new FileInfo(clip)));
        store.Save(state);
        await File.AppendAllTextAsync(clip, "changed");
        File.SetLastWriteTimeUtc(clip, Now.AddMinutes(1).UtcDateTime);
        result = await new LegacyIgnoredBaselineReconciler(store).ReconcileAsync(
            Settings(clips, upload: false), legacyWorkerQuiesced: true);
        Assert(result.IgnoredFileKeys == 0 && result.State!.KnownContentHashes.Count == 0,
            "A modified occurrence must lose stale file-key protection without being grandfathered by hash.");

        var missingRoot = Path.Combine(root, "missing");
        clips = Path.Combine(missingRoot, "clips");
        Directory.CreateDirectory(clips);
        clip = Path.Combine(clips, "missing.mp4");
        await File.WriteAllBytesAsync(clip, [9, 8, 7]);
        store = Store(missingRoot);
        state = State(clips);
        state.IgnoredFileKeys.Add(WatchStateStore.FileKey(new FileInfo(clip)));
        File.Delete(clip);
        store.Save(state);
        result = await new LegacyIgnoredBaselineReconciler(store).ReconcileAsync(
            Settings(clips, upload: false), legacyWorkerQuiesced: true);
        Assert(result.IgnoredFileKeys == 0 && result.State!.KnownContentHashes.Count == 0,
            "A missing occurrence must remove only its stale file key.");

        var unsafeRoot = Path.Combine(root, "unsafe");
        clips = Path.Combine(unsafeRoot, "clips");
        var outside = Path.Combine(unsafeRoot, "outside.mp4");
        Directory.CreateDirectory(clips);
        await File.WriteAllBytesAsync(outside, [5, 4, 3, 2, 1]);
        store = Store(unsafeRoot);
        state = State(clips);
        state.IgnoredFileKeys.Add(WatchStateStore.FileKey(new FileInfo(outside)));
        store.Save(state);
        var outsideBytes = await File.ReadAllBytesAsync(outside);
        var unusedAdapter = new FailIfUsedSourceAdapter();
        result = await new LegacyIgnoredBaselineReconciler(
                store,
                _ => unusedAdapter)
            .ReconcileAsync(Settings(clips, upload: false), legacyWorkerQuiesced: true);
        Assert(result.Loaded && result.IgnoredFileKeys == 0 &&
               result.State!.KnownContentHashes.Count == 0 &&
               unusedAdapter.OpenCalls == 0 &&
               outsideBytes.SequenceEqual(await File.ReadAllBytesAsync(outside)),
            "A well-formed ignored key outside the active source must be drained without opening or changing the external file.");

        var malformedRoot = Path.Combine(root, "malformed");
        clips = Path.Combine(malformedRoot, "clips");
        Directory.CreateDirectory(clips);
        store = Store(malformedRoot);
        state = State(clips);
        state.IgnoredFileKeys.Add("not-an-exact-file-key");
        store.Save(state);
        var malformedBytes = await File.ReadAllBytesAsync(store.StatePath);
        await ExpectAsync<InvalidDataException>(() =>
            new LegacyIgnoredBaselineReconciler(store).ReconcileAsync(
                Settings(clips, upload: false), legacyWorkerQuiesced: true));
        Assert(malformedBytes.SequenceEqual(await File.ReadAllBytesAsync(store.StatePath)),
            "Malformed ignored evidence must fail closed without a partial save.");

        var cancellationRoot = Path.Combine(root, "cancellation");
        clips = Path.Combine(cancellationRoot, "clips");
        Directory.CreateDirectory(clips);
        clip = Path.Combine(clips, "cancel.mp4");
        await File.WriteAllBytesAsync(clip, Enumerable.Repeat((byte)42, 1024).ToArray());
        store = Store(cancellationRoot);
        state = State(clips);
        state.IgnoredFileKeys.Add(WatchStateStore.FileKey(new FileInfo(clip)));
        store.Save(state);
        var cancellationBytes = await File.ReadAllBytesAsync(store.StatePath);
        using var cancellation = new CancellationTokenSource();
        var cancellingAdapter = new CancellingSourceAdapter(cancellation);
        await ExpectAsync<OperationCanceledException>(() =>
            new LegacyIgnoredBaselineReconciler(store, _ => cancellingAdapter).ReconcileAsync(
                Settings(clips, upload: false),
                legacyWorkerQuiesced: true,
                cancellation.Token));
        Assert(cancellationBytes.SequenceEqual(await File.ReadAllBytesAsync(store.StatePath)),
            "Cancellation during fingerprinting must leave the durable legacy baseline untouched.");

        await AssertReparseEvidenceFailsClosedIfSupportedAsync(Path.Combine(root, "reparse"));
    }

    private static async Task AssertReparseEvidenceFailsClosedIfSupportedAsync(string root)
    {
        var clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(clips);
        var target = Path.Combine(root, "target.mp4");
        var link = Path.Combine(clips, "linked.mp4");
        await File.WriteAllBytesAsync(target, [1, 3, 3, 7]);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }
        var store = Store(root);
        var state = State(clips);
        state.IgnoredFileKeys.Add(WatchStateStore.FileKey(new FileInfo(link)));
        store.Save(state);
        var bytes = await File.ReadAllBytesAsync(store.StatePath);
        await ExpectAsync<InvalidDataException>(() =>
            new LegacyIgnoredBaselineReconciler(store).ReconcileAsync(
                Settings(clips, upload: false), legacyWorkerQuiesced: true));
        Assert(bytes.SequenceEqual(await File.ReadAllBytesAsync(store.StatePath)),
            "Reparse-point ignored evidence must fail closed without changing state.");
    }

    private static async Task AssertActualLegacyPreparationAsync(string root)
    {
        var local = Fixture(Path.Combine(root, "local"), upload: false);
        var localMarker = await local.Preparer.PrepareAsync(CancellationToken.None);
        var localRoute = local.Routes.Load().Document!.Routes.Single();
        Assert(localMarker.Phase == LegacyRoutingMigrationMarkerPhase.Committed &&
               localMarker.Mode == LegacyRoutingMode.LocalOnly &&
               localRoute.Actions is [
                   { Kind: RoutingActionKind.FileIntoLibrary,
                       LibraryArea: RoutingLibraryArea.LocalOnly, ConnectionId: null }] &&
               local.Catalog.Inspect().Connections.Count == 0,
            "Actual Local-only preparation must commit the connectionless library route.");

        var discord = Fixture(Path.Combine(root, "discord"), upload: true);
        var discordMarker = await discord.Preparer.PrepareAsync(CancellationToken.None);
        var discordRoute = discord.Routes.Load().Document!.Routes.Single();
        Assert(discordMarker.Phase == LegacyRoutingMigrationMarkerPhase.Committed &&
               discordMarker.Mode == LegacyRoutingMode.DiscordUpload &&
               discordRoute.Actions.Count == 2 &&
               discordRoute.Actions[0] is
               {
                   Kind: RoutingActionKind.Deliver,
                   Destination: RoutingDestinationKind.Discord
               } &&
               discordRoute.Actions[1] is
               {
                   Kind: RoutingActionKind.FileIntoLibrary,
                   LibraryArea: RoutingLibraryArea.Uploaded
               } && discord.Catalog.Inspect().Connections.Count == 1,
            "Actual Discord preparation must import one opaque connection before exact route cutover.");
        var durableRoutingText = string.Join('\n', new[]
        {
            discord.Markers.Path,
            discord.Routes.Path,
            discord.ConnectionStore.Path
        }.Where(File.Exists).Select(File.ReadAllText));
        Assert(!durableRoutingText.Contains(Webhook, StringComparison.Ordinal) &&
               !durableRoutingText.Contains("runtime-private-token", StringComparison.Ordinal) &&
               !durableRoutingText.Contains(discord.Clips, StringComparison.OrdinalIgnoreCase) &&
               !durableRoutingText.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase),
            "Migration persistence must contain neither webhook material nor watched/user paths.");

        var liveShape = Fixture(Path.Combine(root, "live-shaped-history"), upload: true);
        var currentClip = Path.Combine(liveShape.Clips, "current.mp4");
        var staleRoot = Directory.CreateDirectory(
            Path.Combine(liveShape.Root, "previous-clips-root")).FullName;
        var staleClip = Path.Combine(staleRoot, "stale.mp4");
        await File.WriteAllBytesAsync(currentClip, [4, 2, 4, 2]);
        await File.WriteAllBytesAsync(staleClip, [8, 6, 8, 6]);
        var overlap = new string('d', 64);
        var liveState = State(liveShape.Clips);
        liveState.KnownContentHashes.Add(overlap);
        liveState.UploadedContentHashes.Add(overlap);
        liveState.LocalOnlyContentHashes.Add(overlap);
        liveState.IgnoredFileKeys.UnionWith([
            WatchStateStore.FileKey(new FileInfo(currentClip)),
            WatchStateStore.FileKey(new FileInfo(staleClip))
        ]);
        liveShape.WatchState.Save(liveState);
        var liveMarker = await liveShape.Preparer.PrepareAsync(CancellationToken.None);
        var repairedState = liveShape.WatchState.ProbeForRoutingActivation();
        Assert(liveMarker.Phase == LegacyRoutingMigrationMarkerPhase.Committed &&
               liveMarker.ContentHashExclusions.Uploaded.Contains(
                   overlap, StringComparer.OrdinalIgnoreCase) &&
               !liveMarker.ContentHashExclusions.LocalOnly.Contains(
                   overlap, StringComparer.OrdinalIgnoreCase) &&
               repairedState is
               {
                   Status: WatchStateRoutingProbeStatus.Loaded,
                   IgnoredFileKeys: 0,
                   State: not null
               } &&
               !repairedState.State.UploadedContentHashes.Intersect(
                   repairedState.State.LocalOnlyContentHashes,
                   StringComparer.OrdinalIgnoreCase).Any() &&
               File.Exists(staleClip),
            "Production activation must canonicalize live-shaped legacy history even when the background watcher never loaded it.");
    }

    private static async Task AssertPreparationAuthorityStatesAsync(string root)
    {
        var fixture = Fixture(Path.Combine(root, "prepared"), upload: false);
        var marker = await fixture.Preparer.PrepareAsync(CancellationToken.None);
        var authority = AuthorityStore(fixture.Root, "missing-authority");
        var fake = new RecordingPreparer(marker);
        var runtime = Runtime(fixture, authority, fake);
        await runtime.PrepareActivationAsync(CancellationToken.None);
        Assert(fake.Calls == 1,
            "Missing authority must run reversible activation preparation exactly once.");

        var corrupt = AuthorityStore(fixture.Root, "corrupt-authority");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt.Path)!);
        await File.WriteAllTextAsync(corrupt.Path, "{ corrupt authority");
        fake = new RecordingPreparer(marker);
        await ExpectAsync<InvalidDataException>(() =>
            Runtime(fixture, corrupt, fake).PrepareActivationAsync(CancellationToken.None)
                .AsTask());
        Assert(fake.Calls == 0,
            "Corrupt sticky authority must block before reversible preparation runs.");

        var missingMarkerFixture = Fixture(Path.Combine(root, "missing-marker"), upload: false);
        fake = new RecordingPreparer(marker);
        await ExpectAsync<InvalidDataException>(() =>
            Runtime(missingMarkerFixture,
                    AuthorityStore(missingMarkerFixture.Root, "authority"), fake)
                .PrepareActivationAsync(CancellationToken.None).AsTask());
        Assert(fake.Calls == 1,
            "A preparer result that is not the exact durable marker must be rejected.");
    }

    private static async Task AssertStickyBoundaryAndWorkHostAsync(string root)
    {
        var fixture = Fixture(root, upload: false);
        var authority = AuthorityStore(root, "authority");
        var starts = 0;
        var stops = 0;
        RoutingProductionRuntime? runtime = null;
        runtime = Runtime(
            fixture,
            authority,
            fixture.Preparer,
            start: (lease, gate, _) =>
            {
                Assert(authority.Inspect().RoutingRequired && gate.Inspect().Enabled &&
                       runtime!.InspectActivation(lease).Enabled,
                    "The work-host callback must observe exact sticky authority and an enabled gate.");
                starts++;
                return ValueTask.CompletedTask;
            },
            stop: _ =>
            {
                stops++;
                return ValueTask.CompletedTask;
            });
        await runtime.PrepareActivationAsync(CancellationToken.None);
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease) &&
               lease is not null, "The test could not acquire Routing ownership.");
        using (var routingLease = lease!)
        {
            Assert(runtime.InspectActivation(routingLease).State ==
                   RoutingRuntimeGateState.ExecutionAuthorityUnavailable,
                "Routing must remain disabled before the sticky authority write.");
            await ExpectAsync<InvalidOperationException>(() =>
                runtime.StartAsync(routingLease, CancellationToken.None).AsTask());
            Assert(starts == 0, "The work host must not start before authority exists.");
            await runtime.CommitExecutionAuthorityAsync(routingLease);
            var document = authority.Inspect().Document!;
            var marker = fixture.Markers.Load().Document!;
            Assert(document.MigrationId == marker.MigrationId &&
                   document.MigrationPayloadFingerprint == marker.PayloadFingerprint &&
                   document.SourceFingerprint == marker.SourceFingerprint &&
                   document.RequiredLegacySource == fixture.Settings.CaptureSource &&
                   document.ActivatedUtc == marker.UpdatedUtc,
                "Sticky authority must bind the exact committed marker, source, and deterministic timestamp.");
            await runtime.StartAsync(routingLease, CancellationToken.None);
            Assert(starts == 1, "The work host must start once after exact authority is enabled.");
            await runtime.StopAsync(CancellationToken.None);
            Assert(stops == 1, "Stopping the production runtime must stop its work host exactly once.");
        }

        var rejectedFixture = Fixture(Path.Combine(root, "rejected"), upload: false);
        await rejectedFixture.Preparer.PrepareAsync(CancellationToken.None);
        var rejectedAuthority = AuthorityStore(rejectedFixture.Root, "authority");
        var rejectedRuntime = Runtime(rejectedFixture, rejectedAuthority, rejectedFixture.Preparer);
        var legacyOwner = new ClipProcessingOwnershipCoordinator();
        Assert(legacyOwner.TryAcquire(ClipProcessingRuntimeOwner.Legacy, out var legacyLease) &&
               legacyLease is not null, "The test could not acquire Legacy ownership.");
        using (var currentLegacyLease = legacyLease!)
        {
            await ExpectAsync<InvalidOperationException>(() =>
                rejectedRuntime.CommitExecutionAuthorityAsync(currentLegacyLease).AsTask());
        }
        Assert(rejectedAuthority.Inspect().LegacyPermitted,
            "Rejected ownership must not create sticky authority.");
        var staleOwner = new ClipProcessingOwnershipCoordinator();
        Assert(staleOwner.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var staleLease) &&
               staleLease is not null, "The test could not acquire stale Routing ownership.");
        var stale = staleLease!;
        stale.Dispose();
        await ExpectAsync<InvalidOperationException>(() =>
            rejectedRuntime.CommitExecutionAuthorityAsync(stale).AsTask());
        Assert(rejectedAuthority.Inspect().LegacyPermitted,
            "A stale Routing lease must not create sticky authority.");
    }

    private static async Task AssertProductionLayoutPreflightRestoresLegacyAsync(string root)
    {
        var fixture = Fixture(root, upload: false);
        var authority = AuthorityStore(root, "authority");
        var preflightCalls = 0;
        var runtime = Runtime(
            fixture,
            authority,
            fixture.Preparer,
            activationPreflight: cancellationToken =>
            {
                preflightCalls++;
                cancellationToken.ThrowIfCancellationRequested();
                _ = RoutingActiveWorkSession.RequireProductionConfiguration(
                    fixture.Settings,
                    CaptureSettings.Default with { LibraryRoot = fixture.Clips });
                return ValueTask.CompletedTask;
            });
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(
                   ClipProcessingRuntimeOwner.Legacy,
                   out var initialLegacyOwnership) && initialLegacyOwnership is not null,
            "The layout-preflight test could not acquire initial Legacy ownership.");
        using var legacy = new RestorableLegacyRuntime(initialLegacyOwnership!);
        var coordinator = new RoutingRuntimeCoordinator(
            ownership,
            legacy,
            runtime,
            new RoutingRuntimeCoordinatorOptions(
                RequestedEnabled: true,
                LegacyRuntimeAllowed: true));

        var result = await coordinator.StartAsync(CancellationToken.None);

        Assert(result.Status ==
                   RoutingRuntimeTransitionStatus.PreparationFailedLegacyRestored &&
               result.State == RoutingRuntimeCoordinatorState.LegacyActive &&
               ownership.Owner == ClipProcessingRuntimeOwner.Legacy &&
               legacy.StopCalls == 1 && legacy.StartCalls == 1 &&
               preflightCalls == 1 && authority.Inspect().LegacyPermitted &&
               !File.Exists(authority.Path) && !File.Exists(fixture.Markers.Path),
            "An invalid production folder layout must fail before migration or sticky authority and restore the existing Legacy watcher.");
    }

    private static async Task AssertFreshAuthorityAndMutableSnapshotAsync(string root)
    {
        Directory.CreateDirectory(root);
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var library = Directory.CreateDirectory(Path.Combine(root, "capture-library")).FullName;
        var settings = Settings(clips, upload: false);
        var stateStore = Store(root);
        stateStore.Save(State(clips));
        var state = stateStore.ProbeForRoutingActivation().State ??
                    throw new InvalidOperationException("Fresh state did not reload.");
        var binding = RoutingCaptureLibraryBindingModel.Create(library);
        var watchedRootIdentity = RoutingWatchedSourceAdapters
            .Get(settings.CaptureSource)
            .InspectRootIdentity(clips);
        var readiness = FreshRoutingSetupPlanner.Evaluate(
            new FreshRoutingSetupInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                new RoutingRouteDraft(
                    "My first route",
                    RoutingTriggerKind.AnyNewSourceClip,
                    Game: null,
                    Destination: null,
                    ConnectionId: null,
                    RoutingOutputKind.Original,
                    RoutingDeliveryMode.Automatic,
                    RoutingMissingOutputBehavior.UseOriginal,
                    FileIntoLibrary: true),
                binding,
                watchedRootIdentity,
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile),
            Now);
        var plan = readiness.Plan ?? throw new InvalidOperationException(
            $"Fresh setup plan did not become ready ({readiness.Status}).");
        var routingRoot = Path.Combine(root, "routing");
        var snapshots = new RoutingSnapshotStore(Path.Combine(
            routingRoot, RoutingSnapshotStore.FileName));
        var initialSnapshot = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion,
            Generation: 1,
            Routes: [plan.Route],
            plan.Route.CreatedUtc,
            plan.Route.ModifiedUtc);
        await snapshots.SaveAsync(initialSnapshot, expectedGeneration: 0);
        var initialSnapshotBytes = await File.ReadAllBytesAsync(snapshots.Path);
        var markers = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            routingRoot, LegacyRoutingMigrationMarkerStore.FileName));
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(plan, Now);
        await markers.SaveAsync(prepared, expectedGeneration: 0);

        var authority = AuthorityStore(root, "authority");
        var legacyEvidence = new LegacyRoutingActivationEvidenceSource(
            stateStore,
            () => settings,
            () => [],
            () => binding,
            () => LegacyRoutingMigrationAdmission.ValidLegacyUpgrade);
        var freshEvidence = new FreshRoutingActivationEvidenceSource(
            stateStore,
            () => settings,
            () => binding);
        var runtime = new RoutingProductionRuntime(
            new RecordingPreparer(prepared, throwIfCalled: true),
            markers,
            snapshots,
            legacyEvidence,
            authority,
            RoutingWatchedSourceAdapters.CoveredSources,
            (_, _, _) => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            freshActivationEvidence: freshEvidence);
        Assert(runtime.RequiresRoutingFenceAfterPreparation(),
            "Every durable fresh marker phase must immediately fence Legacy, before runtime authority commits.");
        var committedMarker = LegacyRoutingMigrationMarkerModel.Commit(
            prepared, Now.AddSeconds(1));
        await markers.SaveAsync(committedMarker, prepared.Generation);
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease) &&
               lease is not null, "The fresh test could not acquire Routing ownership.");
        using var routingLease = lease!;

        var preAuthorityEdit = plan.Route with
        {
            Name = "Edited before authority",
            Revision = plan.Route.Revision + 1,
            ModifiedUtc = Now.AddSeconds(2)
        };
        var preAuthoritySnapshot = RoutingSnapshotModel.ReplaceRoutes(
            initialSnapshot,
            [preAuthorityEdit],
            Now.AddSeconds(2));
        await snapshots.SaveAsync(preAuthoritySnapshot, initialSnapshot.Generation);
        await ExpectAsync<InvalidDataException>(() =>
            runtime.CommitExecutionAuthorityAsync(routingLease).AsTask());
        Assert(authority.Inspect().LegacyPermitted,
            "Editing the frozen fresh first route before authority commits must fail before the irreversible write.");

        // Reset this deliberately corrupted negative-case fixture to the byte-exact clean
        // starting document. Production correctly forbids decreasing a route revision, so an
        // ordinary successor cannot represent a test-only rollback.
        await File.WriteAllBytesAsync(snapshots.Path, initialSnapshotBytes);
        var restoredSnapshot = snapshots.Load().Document ??
                               throw new InvalidOperationException(
                                   "The clean fresh snapshot could not be restored for activation.");
        await runtime.CommitExecutionAuthorityAsync(routingLease);
        Assert(authority.Inspect().RoutingRequired &&
               runtime.InspectActivation(routingLease).Enabled,
            "A strict fresh marker, frozen first route, source coverage, and current evidence must commit authority and enable Routing.");

        var editedRoute = plan.Route with
        {
            Name = "Renamed after activation",
            Revision = plan.Route.Revision + 1,
            ModifiedUtc = Now.AddMinutes(1)
        };
        var editedSnapshot = RoutingSnapshotModel.ReplaceRoutes(
            restoredSnapshot,
            [editedRoute],
            Now.AddMinutes(1));
        await snapshots.SaveAsync(editedSnapshot, restoredSnapshot.Generation);
        Assert(runtime.InspectActivation(routingLease).Enabled,
            "A fresh first route may be edited after authority commits without revoking Routing.");
        await AssertFreshColdRestartAsync(
            "edited",
            markers,
            snapshots,
            stateStore,
            authority,
            settings,
            binding,
            committedMarker);

        var emptySnapshot = RoutingSnapshotModel.ReplaceRoutes(
            editedSnapshot,
            [],
            Now.AddMinutes(2));
        await snapshots.SaveAsync(emptySnapshot, editedSnapshot.Generation);
        Assert(runtime.InspectActivation(routingLease).Enabled,
            "A fresh first route may be deleted after authority commits while a valid live snapshot remains.");
        await AssertFreshColdRestartAsync(
            "empty",
            markers,
            snapshots,
            stateStore,
            authority,
            settings,
            binding,
            committedMarker);

        var changedState = stateStore.ProbeForRoutingActivation().State!;
        changedState.KnownContentHashes.Add(new string('E', 64));
        stateStore.Save(changedState);
        Assert(runtime.InspectActivation(routingLease).State ==
               RoutingRuntimeGateState.MigrationEvidenceMismatch,
            "Changing fresh settings/state evidence after authority commits must fail closed.");
        changedState.KnownContentHashes.Clear();
        stateStore.Save(changedState);
        Assert(runtime.InspectActivation(routingLease).Enabled,
            "Restoring fresh evidence must restore the same authority permit.");

        File.Delete(snapshots.Path);
        Assert(runtime.InspectActivation(routingLease).State ==
               RoutingRuntimeGateState.RoutingSnapshotUnavailable,
            "Fresh authority must still require a valid loaded live snapshot.");
    }

    private static async Task AssertFreshColdRestartAsync(
        string scenario,
        LegacyRoutingMigrationMarkerStore markers,
        RoutingSnapshotStore snapshots,
        WatchStateStore stateStore,
        RoutingExecutionAuthorityStore authority,
        AppSettings settings,
        RoutingCaptureLibraryBinding binding,
        LegacyRoutingMigrationMarker committedMarker)
    {
        var legacyEvidence = new LegacyRoutingActivationEvidenceSource(
            stateStore,
            () => settings,
            () => [],
            () => binding,
            () => LegacyRoutingMigrationAdmission.ValidLegacyUpgrade);
        var freshEvidence = new FreshRoutingActivationEvidenceSource(
            stateStore,
            () => settings,
            () => binding);
        var started = 0;
        var restartedRuntime = new RoutingProductionRuntime(
            new RecordingPreparer(committedMarker, throwIfCalled: true),
            markers,
            snapshots,
            legacyEvidence,
            authority,
            RoutingWatchedSourceAdapters.CoveredSources,
            (_, _, _) =>
            {
                started++;
                return ValueTask.CompletedTask;
            },
            _ => ValueTask.CompletedTask,
            freshActivationEvidence: freshEvidence);
        var lifecycle = RoutingApplicationLifecycle.Create(
            authority,
            new ClipProcessingOwnershipCoordinator(),
            new InertLegacyRuntime(),
            restartedRuntime,
            migrationMarkers: markers,
            currentCaptureLibraryBinding: () => binding,
            legacyRuntimeAllowed: false);
        var result = await lifecycle.StartAsync();
        Assert(result is
                   {
                       Status: RoutingApplicationLifecycleStatus.RoutingStarted,
                       State: RoutingApplicationLifecycleState.RoutingRunning
                   } && started == 1,
            $"A cold restart must activate fresh Routing from the valid {scenario} live snapshot without restoring Legacy or requiring the frozen first route.");
        _ = await lifecycle.StopAsync();
    }

    private static async Task AssertFreshSamePathRootReplacementFailsClosedAsync(
        string root)
    {
        Directory.CreateDirectory(root);
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var displacedClips = Path.Combine(root, "clips-before-replacement");
        var library = Directory.CreateDirectory(Path.Combine(root, "capture-library")).FullName;
        var settings = Settings(clips, upload: false);
        var stateStore = Store(root);
        stateStore.Save(State(clips));
        var state = stateStore.ProbeForRoutingActivation().State ??
                    throw new InvalidOperationException(
                        "The root-replacement fresh state did not reload.");
        var binding = RoutingCaptureLibraryBindingModel.Create(library);
        var adapter = RoutingWatchedSourceAdapters.Get(settings.CaptureSource);
        var originalRootIdentity = adapter.InspectRootIdentity(clips);
        var readiness = FreshRoutingSetupPlanner.Evaluate(
            new FreshRoutingSetupInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                new RoutingRouteDraft(
                    "Root-bound first route",
                    RoutingTriggerKind.AnyNewSourceClip,
                    Game: null,
                    Destination: null,
                    ConnectionId: null,
                    RoutingOutputKind.Original,
                    RoutingDeliveryMode.Automatic,
                    RoutingMissingOutputBehavior.UseOriginal,
                    FileIntoLibrary: true),
                binding,
                originalRootIdentity,
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile),
            Now);
        var plan = readiness.Plan ?? throw new InvalidOperationException(
            $"The root-replacement fresh plan was blocked ({readiness.Status}).");
        var routingRoot = Path.Combine(root, "routing");
        var snapshots = new RoutingSnapshotStore(Path.Combine(
            routingRoot, RoutingSnapshotStore.FileName));
        await snapshots.SaveAsync(
            new RoutingSnapshotDocument(
                RoutingSnapshotStore.CurrentSchemaVersion,
                Generation: 1,
                Routes: [plan.Route],
                plan.Route.CreatedUtc,
                plan.Route.ModifiedUtc),
            expectedGeneration: 0);
        var markers = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            routingRoot, LegacyRoutingMigrationMarkerStore.FileName));
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(plan, Now);
        await markers.SaveAsync(prepared, expectedGeneration: 0);
        var committed = LegacyRoutingMigrationMarkerModel.Commit(
            prepared, Now.AddSeconds(1));
        await markers.SaveAsync(committed, prepared.Generation);

        Directory.Move(clips, displacedClips);
        _ = Directory.CreateDirectory(clips);
        var replacementRootIdentity = adapter.InspectRootIdentity(clips);
        Assert(!replacementRootIdentity.Equals(
                originalRootIdentity, StringComparison.Ordinal),
            "Replacing a directory at the same canonical path must change its native root identity.");

        var authority = AuthorityStore(root, "authority");
        var legacyEvidence = new LegacyRoutingActivationEvidenceSource(
            stateStore,
            () => settings,
            () => [],
            () => binding,
            () => LegacyRoutingMigrationAdmission.ValidLegacyUpgrade);
        var runtime = new RoutingProductionRuntime(
            new RecordingPreparer(committed, throwIfCalled: true),
            markers,
            snapshots,
            legacyEvidence,
            authority,
            RoutingWatchedSourceAdapters.CoveredSources,
            (_, _, _) => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            freshActivationEvidence: new FreshRoutingActivationEvidenceSource(
                stateStore,
                () => settings,
                () => binding));
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease) &&
               lease is not null,
            "The root-replacement test could not acquire Routing ownership.");
        using (var routingLease = lease!)
        {
            await ExpectAsync<InvalidDataException>(() =>
                runtime.CommitExecutionAuthorityAsync(routingLease).AsTask());
        }
        Assert(authority.Inspect().LegacyPermitted && !File.Exists(authority.Path),
            "A same-path watched-root replacement must fail closed before sticky authority is written.");
    }

    private static async Task AssertFreshDependencyRevocationFailsClosedAsync(string root)
    {
        const string discordConnectionId =
            "discord.11111111222233334444555555555555";
        const string namedSourceId =
            "source.11111111222233334444555555555555";
        const string xboxSourceId =
            "source.aaaaaaaa222233334444555555555555";

        var missingDiscord = await RecoverPreparedFreshSetupAsync(
            Path.Combine(root, "discord-missing"),
            new RoutingRouteDraft(
                "Discord first route",
                RoutingTriggerKind.AnyNewSourceClip,
                Game: null,
                RoutingDestinationKind.Discord,
                discordConnectionId,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true));
        await AssertFreshDependencyBlocksAuthorityAsync(
            missingDiscord,
            new SequencedConnectionMembership(discordConnectionId, false),
            inputSources: null,
            "A deleted or unready Discord connection");

        var revokingDiscord = await RecoverPreparedFreshSetupAsync(
            Path.Combine(root, "discord-before-commit"),
            new RoutingRouteDraft(
                "Discord callback route",
                RoutingTriggerKind.AnyNewSourceClip,
                Game: null,
                RoutingDestinationKind.Discord,
                discordConnectionId,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true));
        var connection = new SequencedConnectionMembership(
            discordConnectionId,
            true,
            false);
        await AssertFreshDependencyBlocksAuthorityAsync(
            revokingDiscord,
            connection,
            inputSources: null,
            "A Discord connection revoked at the atomic authority boundary");
        Assert(connection.Checks == 2 && connection.GateEntries == 1 &&
               connection.ChecksOutsideGate == 0 && connection.ActiveGates == 0,
            "Discord readiness must be checked after fresh evidence and again in the authority store's before-commit callback while its mutation gate remains held.");

        var missingNamedSource = await RecoverPreparedFreshSetupAsync(
            Path.Combine(root, "named-source-missing"),
            new RoutingRouteDraft(
                "NVIDIA first route",
                RoutingTriggerKind.WatchedFolder,
                Game: null,
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true,
                WatchedSourceId: namedSourceId,
                WatchedSourceKind: RoutingInputSourceKind.Nvidia));
        var namedSource = new GatedInputSourceMembership(
            namedSourceId,
            RoutingInputSourceKind.Nvidia,
            false);
        await AssertFreshDependencyBlocksAuthorityAsync(
            missingNamedSource,
            connections: null,
            namedSource,
            "A retired, replaced, disabled, or unready named source");
        Assert(namedSource.GateEntries == 1 && namedSource.ChecksOutsideGate == 0 &&
               namedSource.ActiveGates == 0 &&
               !namedSource.ObservedKinds.Contains(RoutingInputSourceKind.XboxGameDvrOneDrive),
            "Named-source readiness must be inspected under its execution gate and must not be reinterpreted as Xbox authority.");

        var revokingXboxSource = await RecoverPreparedFreshSetupAsync(
            Path.Combine(root, "xbox-before-commit"),
            new RoutingRouteDraft(
                "Xbox first route",
                RoutingTriggerKind.WatchedFolder,
                Game: null,
                Destination: null,
                ConnectionId: null,
                RoutingOutputKind.Original,
                RoutingDeliveryMode.Automatic,
                RoutingMissingOutputBehavior.UseOriginal,
                FileIntoLibrary: true,
                WatchedSourceId: xboxSourceId,
                EarliestCapturedUtc: Now.AddMinutes(-1),
                XboxHistorySelection: new RoutingXboxHistorySelection(Now, []),
                WatchedSourceKind: RoutingInputSourceKind.XboxGameDvrOneDrive));
        var xboxSource = new GatedInputSourceMembership(
            xboxSourceId,
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            true,
            false);
        await AssertFreshDependencyBlocksAuthorityAsync(
            revokingXboxSource,
            connections: null,
            xboxSource,
            "An Xbox source revoked at the atomic authority boundary");
        Assert(xboxSource.ExpectedKindChecks == 2 && xboxSource.GateEntries == 1 &&
               xboxSource.ChecksOutsideGate == 0 && xboxSource.ActiveGates == 0 &&
               xboxSource.ObservedKinds.All(kind =>
                   kind == RoutingInputSourceKind.XboxGameDvrOneDrive),
            "Xbox readiness must be rechecked in the atomic callback while the same source execution gate remains held.");
    }

    private static async Task<RecoveredFreshSetup> RecoverPreparedFreshSetupAsync(
        string root,
        RoutingRouteDraft draft)
    {
        Directory.CreateDirectory(root);
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var library = Directory.CreateDirectory(Path.Combine(root, "capture-library")).FullName;
        var settings = Settings(clips, upload: false);
        var stateStore = Store(root);
        stateStore.Save(State(clips));
        var state = stateStore.ProbeForRoutingActivation().State ??
                    throw new InvalidOperationException(
                        "The fresh dependency test state did not reload.");
        var binding = RoutingCaptureLibraryBindingModel.Create(library);
        var watchedRootIdentity = RoutingWatchedSourceAdapters
            .Get(settings.CaptureSource)
            .InspectRootIdentity(clips);
        var readiness = FreshRoutingSetupPlanner.Evaluate(
            new FreshRoutingSetupInput(
                settings,
                state,
                LegacyWorkerQuiesced: true,
                draft,
                binding,
                watchedRootIdentity,
                LegacyRoutingMigrationAdmission.FreshOrInvalidProfile),
            Now);
        var plan = readiness.Plan ?? throw new InvalidOperationException(
            $"The fresh dependency plan was blocked ({readiness.Status}).");
        var routingRoot = Path.Combine(root, "routing");
        var snapshots = new RoutingSnapshotStore(Path.Combine(
            routingRoot,
            RoutingSnapshotStore.FileName));
        var markers = new LegacyRoutingMigrationMarkerStore(Path.Combine(
            routingRoot,
            LegacyRoutingMigrationMarkerStore.FileName));
        var prepared = LegacyRoutingMigrationMarkerModel.CreatePrepared(plan, Now);
        await markers.SaveAsync(prepared, expectedGeneration: 0);

        // Reconstruct every store/coordinator to exercise the real cold recovery path from a
        // durable Prepared marker. Dependency revocation happens only after that recovery has
        // made the route and marker durable, but before sticky runtime authority is allowed.
        snapshots = new RoutingSnapshotStore(snapshots.Path);
        markers = new LegacyRoutingMigrationMarkerStore(markers.Path);
        var recovered = await new LegacyRoutingMigrationCoordinator(snapshots, markers)
            .ResumeFreshAsync(
                settings,
                state,
                binding,
                watchedRootIdentity,
                Now.AddSeconds(1));
        Assert(recovered.IsCommitted && recovered.Marker is
               { Phase: LegacyRoutingMigrationMarkerPhase.Committed },
            "Cold recovery from Prepared must establish the frozen fresh route before dependency revocation is tested.");
        return new RecoveredFreshSetup(
            root,
            settings,
            stateStore,
            binding,
            snapshots,
            markers,
            recovered.Marker!);
    }

    private static async Task AssertFreshDependencyBlocksAuthorityAsync(
        RecoveredFreshSetup setup,
        IRoutingConnectionMembership? connections,
        IRoutingInputSourceMembership? inputSources,
        string scenario)
    {
        var authority = AuthorityStore(setup.Root, "authority");
        var legacyEvidence = new LegacyRoutingActivationEvidenceSource(
            setup.WatchState,
            () => setup.Settings,
            () => [],
            () => setup.Binding,
            () => LegacyRoutingMigrationAdmission.ValidLegacyUpgrade);
        var runtime = new RoutingProductionRuntime(
            new RecordingPreparer(setup.Marker, throwIfCalled: true),
            setup.Markers,
            setup.Snapshots,
            legacyEvidence,
            authority,
            RoutingWatchedSourceAdapters.CoveredSources,
            (_, _, _) => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            freshActivationEvidence: new FreshRoutingActivationEvidenceSource(
                setup.WatchState,
                () => setup.Settings,
                () => setup.Binding),
            connectionMembership: connections,
            inputSourceMembership: inputSources);
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease) &&
               lease is not null,
            "The fresh dependency test could not acquire Routing ownership.");
        using (var routingLease = lease!)
        {
            await ExpectAsync<InvalidDataException>(() =>
                runtime.CommitExecutionAuthorityAsync(routingLease).AsTask());
        }
        Assert(authority.Inspect().LegacyPermitted && !File.Exists(authority.Path),
            $"{scenario} must fail closed without writing sticky execution authority.");
    }

    private static async Task AssertCommitPreflightFailsClosedAsync(string root)
    {
        var fixture = Fixture(Path.Combine(root, "queues"), upload: false);
        await fixture.Preparer.PrepareAsync(CancellationToken.None);
        var changed = fixture.WatchState.ProbeForRoutingActivation().State!;
        changed.PendingMoves.Add(Path.Combine(fixture.Clips, "appeared-after-preparation.mp4"));
        fixture.WatchState.Save(changed);
        var authority = AuthorityStore(fixture.Root, "authority");
        var runtime = Runtime(fixture, authority, fixture.Preparer);
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease) &&
               lease is not null, "The test could not acquire preflight ownership.");
        using (var queueLease = lease!)
        {
            await ExpectAsync<InvalidDataException>(() =>
                runtime.CommitExecutionAuthorityAsync(queueLease).AsTask());
        }
        Assert(authority.Inspect().LegacyPermitted,
            "A changed legacy queue must fail before the irreversible authority write.");

        var coverage = Fixture(Path.Combine(root, "coverage"), upload: false);
        await coverage.Preparer.PrepareAsync(CancellationToken.None);
        authority = AuthorityStore(coverage.Root, "authority");
        runtime = Runtime(
            coverage,
            authority,
            coverage.Preparer,
            covered: new HashSet<ClipCaptureSource> { ClipCaptureSource.Nvidia });
        ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out lease) &&
               lease is not null, "The test could not acquire coverage ownership.");
        using (var coverageLease = lease!)
        {
            await ExpectAsync<InvalidDataException>(() =>
                runtime.CommitExecutionAuthorityAsync(coverageLease).AsTask());
        }
        Assert(authority.Inspect().LegacyPermitted,
            "Missing source coverage must fail before sticky authority is written.");
    }

    private static async Task AssertDeterministicColdRepeatAsync(string root)
    {
        var fixture = Fixture(root, upload: false);
        await fixture.Preparer.PrepareAsync(CancellationToken.None);
        var authorityA = AuthorityStore(root, "authority-a");
        var authorityB = AuthorityStore(root, "authority-b");
        var clockA = new Func<DateTimeOffset>(() => Now.AddYears(-10));
        var clockB = new Func<DateTimeOffset>(() => Now.AddYears(10));
        var runtimeA = Runtime(fixture, authorityA, fixture.Preparer, clock: clockA);
        var runtimeB = Runtime(fixture, authorityB, fixture.Preparer, clock: clockB);
        var ownership = new ClipProcessingOwnershipCoordinator();
        Assert(ownership.TryAcquire(ClipProcessingRuntimeOwner.Routing, out var lease) &&
               lease is not null, "The test could not acquire repeat ownership.");
        using (var repeatLease = lease!)
        {
            await runtimeA.CommitExecutionAuthorityAsync(repeatLease);
            await runtimeB.CommitExecutionAuthorityAsync(repeatLease);
            Assert(authorityA.Inspect().Document == authorityB.Inspect().Document,
                "Two runtime clocks must construct byte-exact authority from one committed marker.");

            var bytes = await File.ReadAllBytesAsync(authorityA.Path);
            var coldPreparer = new RecordingPreparer(
                fixture.Markers.Load().Document!, throwIfCalled: true);
            var cold = Runtime(fixture, authorityA, coldPreparer,
                clock: () => Now.AddYears(100));
            await cold.PrepareActivationAsync(CancellationToken.None);
            await cold.CommitExecutionAuthorityAsync(repeatLease);
            Assert(coldPreparer.Calls == 0 &&
                   bytes.SequenceEqual(await File.ReadAllBytesAsync(authorityA.Path)),
                "Cold preparation and recommit must be a verification-only byte-stable no-op.");
        }

        var persisted = File.ReadAllText(authorityA.Path);
        Assert(!persisted.Contains(Webhook, StringComparison.Ordinal) &&
               !persisted.Contains(fixture.Clips, StringComparison.OrdinalIgnoreCase) &&
               !persisted.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase),
            "Sticky execution authority must not persist secrets, user names, or watched paths.");
    }

    private static RuntimeFixture Fixture(string root, bool upload)
    {
        Directory.CreateDirectory(root);
        var clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(clips);
        var captureLibraryRoot = Path.Combine(root, "capture-library");
        Directory.CreateDirectory(captureLibraryRoot);
        var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(
            captureLibraryRoot);
        var settings = Settings(clips, upload);
        var watchState = Store(root);
        watchState.Save(State(clips));
        var routingRoot = Path.Combine(root, "routing");
        var routes = new RoutingSnapshotStore(
            Path.Combine(routingRoot, RoutingSnapshotStore.FileName));
        var markers = new LegacyRoutingMigrationMarkerStore(
            Path.Combine(routingRoot, LegacyRoutingMigrationMarkerStore.FileName));
        var connectionStore = new DiscordConnectionCatalogStore(
            Path.Combine(routingRoot, DiscordConnectionCatalogStore.FileName));
        var outbox = new RoutingOutboxStore(
            Path.Combine(routingRoot, RoutingOutboxStore.FileName));
        var catalog = new DiscordConnectionCatalog(
            connectionStore,
            new TestWebhookProtector(),
            new DiscordConnectionReferenceProbe(routes, outbox),
            () => new Guid("11111111-2222-3333-4444-555555555555"));
        var cutover = new LegacyDiscordConnectionCutoverAdapter(
            catalog,
            new LegacyRoutingMigrationCoordinator(routes, markers));
        var preparer = new LegacyRoutingActivationPreparer(
            () => settings,
            watchState,
            cutover,
            () => captureLibraryBinding,
            () => LegacyRoutingMigrationAdmission.ValidLegacyUpgrade,
            () => Now);
        var evidence = new LegacyRoutingActivationEvidenceSource(
            watchState,
            () => settings,
            () => catalog.Inspect().Connections.Select(connection => connection.ConnectionId)
                .ToArray(),
            () => captureLibraryBinding,
            () => LegacyRoutingMigrationAdmission.ValidLegacyUpgrade);
        return new RuntimeFixture(
            root, clips, captureLibraryRoot, captureLibraryBinding,
            settings, watchState, routes, markers,
            connectionStore, catalog, preparer, evidence);
    }

    private static RoutingProductionRuntime Runtime(
        RuntimeFixture fixture,
        RoutingExecutionAuthorityStore authority,
        IRoutingActivationPreparer preparer,
        IReadOnlySet<ClipCaptureSource>? covered = null,
        Func<ClipProcessingOwnershipLease, RoutingRuntimeFeatureGate,
            CancellationToken, ValueTask>? start = null,
        Func<CancellationToken, ValueTask>? stop = null,
        Func<CancellationToken, ValueTask>? activationPreflight = null,
        Func<DateTimeOffset>? clock = null) => new(
        preparer,
        fixture.Markers,
        fixture.Routes,
        fixture.Evidence,
        authority,
        covered ?? RoutingWatchedSourceAdapters.CoveredSources,
        start ?? ((_, _, _) => ValueTask.CompletedTask),
        stop ?? (_ => ValueTask.CompletedTask),
        activationPreflight,
        clock);

    private static RoutingExecutionAuthorityStore AuthorityStore(string root, string folder) =>
        new(Path.Combine(root, folder, RoutingExecutionAuthorityStore.FileName));

    private static WatchStateStore Store(string root) => new(
        Path.Combine(root, "state.json"),
        Path.Combine(root, ".safe-baseline-required"));

    private static AppSettings Settings(string clips, bool upload) => new(
        clips,
        upload ? Webhook : string.Empty,
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Runtime tester",
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

    private static async Task ExpectAsync<TException>(Func<Task> action)
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
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record RuntimeFixture(
        string Root,
        string Clips,
        string CaptureLibraryRoot,
        RoutingCaptureLibraryBinding CaptureLibraryBinding,
        AppSettings Settings,
        WatchStateStore WatchState,
        RoutingSnapshotStore Routes,
        LegacyRoutingMigrationMarkerStore Markers,
        DiscordConnectionCatalogStore ConnectionStore,
        DiscordConnectionCatalog Catalog,
        LegacyRoutingActivationPreparer Preparer,
        LegacyRoutingActivationEvidenceSource Evidence);

    private sealed record RecoveredFreshSetup(
        string Root,
        AppSettings Settings,
        WatchStateStore WatchState,
        RoutingCaptureLibraryBinding Binding,
        RoutingSnapshotStore Snapshots,
        LegacyRoutingMigrationMarkerStore Markers,
        LegacyRoutingMigrationMarker Marker);

    private sealed class SequencedConnectionMembership : IRoutingConnectionMembership
    {
        private readonly string _connectionId;
        private readonly bool[] _readiness;
        private int _activeGates;

        internal SequencedConnectionMembership(string connectionId, params bool[] readiness)
        {
            _connectionId = connectionId;
            _readiness = readiness.Length == 0 ? [false] : readiness;
        }

        internal int Checks { get; private set; }
        internal int GateEntries { get; private set; }
        internal int ChecksOutsideGate { get; private set; }
        internal int ActiveGates => Volatile.Read(ref _activeGates);

        public bool IsReady(
            RoutingDestinationKind destination,
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _activeGates) == 0) ChecksOutsideGate++;
            var index = Math.Min(Checks, _readiness.Length - 1);
            Checks++;
            return destination == RoutingDestinationKind.Discord &&
                   connectionId.Equals(_connectionId, StringComparison.Ordinal) &&
                   _readiness[index];
        }

        public ValueTask<IDisposable> EnterExecutionGateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GateEntries++;
            Interlocked.Increment(ref _activeGates);
            return ValueTask.FromResult<IDisposable>(new CallbackDisposable(() =>
                Interlocked.Decrement(ref _activeGates)));
        }
    }

    private sealed class GatedInputSourceMembership : IRoutingInputSourceMembership
    {
        private readonly string _sourceId;
        private readonly RoutingInputSourceKind _expectedKind;
        private readonly bool[] _readiness;
        private int _activeGates;

        internal GatedInputSourceMembership(
            string sourceId,
            RoutingInputSourceKind expectedKind,
            params bool[] readiness)
        {
            _sourceId = sourceId;
            _expectedKind = expectedKind;
            _readiness = readiness.Length == 0 ? [false] : readiness;
        }

        internal int GateEntries { get; private set; }
        internal int ChecksOutsideGate { get; private set; }
        internal int ExpectedKindChecks { get; private set; }
        internal int ActiveGates => Volatile.Read(ref _activeGates);
        internal List<RoutingInputSourceKind> ObservedKinds { get; } = [];

        public bool IsReady(
            string sourceId,
            RoutingInputSourceKind kind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _activeGates) == 0) ChecksOutsideGate++;
            ObservedKinds.Add(kind);
            if (!sourceId.Equals(_sourceId, StringComparison.Ordinal) ||
                kind != _expectedKind)
            {
                return false;
            }
            var index = Math.Min(ExpectedKindChecks, _readiness.Length - 1);
            ExpectedKindChecks++;
            return _readiness[index];
        }

        public ValueTask<IDisposable> EnterExecutionGateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GateEntries++;
            Interlocked.Increment(ref _activeGates);
            return ValueTask.FromResult<IDisposable>(new CallbackDisposable(() =>
                Interlocked.Decrement(ref _activeGates)));
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }

    private sealed class InertLegacyRuntime : ILegacyClipProcessingRuntime
    {
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Fresh Routing cold restart must never start the Legacy runtime.");
    }

    private sealed class RestorableLegacyRuntime(
        ClipProcessingOwnershipLease initialOwnership) :
        ILegacyClipProcessingRuntime,
        IDisposable
    {
        private ClipProcessingOwnershipLease? _owned = Adopt(initialOwnership);

        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            _owned?.Dispose();
            _owned = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            _owned = Adopt(ownership);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            _owned?.Dispose();
            _owned = null;
        }

        private static ClipProcessingOwnershipLease Adopt(
            ClipProcessingOwnershipLease ownership)
        {
            if (ownership.Owner != ClipProcessingRuntimeOwner.Legacy ||
                !ownership.IsCurrent ||
                !ownership.TryReissue(out var adopted) || adopted is null)
            {
                throw new InvalidOperationException(
                    "The test Legacy runtime could not adopt its ownership lease.");
            }
            return adopted;
        }
    }

    private sealed class RecordingPreparer(
        LegacyRoutingMigrationMarker marker,
        bool throwIfCalled = false) : IRoutingActivationPreparer
    {
        internal int Calls { get; private set; }

        public ValueTask<LegacyRoutingMigrationMarker> PrepareAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (throwIfCalled)
                throw new InvalidOperationException("Preparation was not expected.");
            return ValueTask.FromResult(marker);
        }
    }

    private sealed class CancellingSourceAdapter(CancellationTokenSource cancellation)
        : IRoutingWatchedSourceAdapter
    {
        public ClipCaptureSource Source => ClipCaptureSource.SteelSeriesGg;

        public string InspectRootIdentity(string clipsRoot) => new string('d', 64);

        public IReadOnlyList<string> EnumerateCandidates(
            string clipsRoot,
            CancellationToken cancellationToken = default) => [];

        public Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not observed.");
        }

        public Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not observed.");
        }

        public Task<RoutingWatchedSourceFile> RevalidateAsync(
            RoutingWatchedSourceFile prior,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailIfUsedSourceAdapter : IRoutingWatchedSourceAdapter
    {
        internal int OpenCalls { get; private set; }
        public ClipCaptureSource Source => ClipCaptureSource.SteelSeriesGg;

        public string InspectRootIdentity(string clipsRoot) =>
            throw new InvalidOperationException(
                "Out-of-scope evidence must not inspect the source root.");

        public IReadOnlyList<string> EnumerateCandidates(
            string clipsRoot,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Out-of-scope evidence must not enumerate files.");

        public Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Out-of-scope evidence must not inspect files.");

        public Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
            string clipsRoot,
            string candidatePath,
            CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            throw new InvalidOperationException("Out-of-scope evidence must not open files.");
        }

        public Task<RoutingWatchedSourceFile> RevalidateAsync(
            RoutingWatchedSourceFile prior,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Out-of-scope evidence must not revalidate files.");
    }

    private sealed class TestWebhookProtector : IDiscordWebhookProtector
    {
        public string Protect(string webhookUrl, string connectionId)
        {
            var bytes = Encoding.UTF8.GetBytes(
                $"runtime-test\0{connectionId}\0{webhookUrl.Trim()}");
            for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0xa5;
            return Convert.ToBase64String(bytes);
        }

        public bool TryUnprotect(
            string protectedWebhook,
            string connectionId,
            out string webhookUrl)
        {
            webhookUrl = string.Empty;
            try
            {
                var bytes = Convert.FromBase64String(protectedWebhook);
                for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0xa5;
                var value = Encoding.UTF8.GetString(bytes);
                var prefix = $"runtime-test\0{connectionId}\0";
                if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
                webhookUrl = value[prefix.Length..];
                return WebhookValidation.IsDiscordWebhook(webhookUrl);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
