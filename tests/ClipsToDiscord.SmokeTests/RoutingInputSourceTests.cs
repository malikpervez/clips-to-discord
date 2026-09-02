namespace ClipsToDiscord;

internal static class RoutingInputSourceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string TimeZoneId = TimeZoneInfo.Utc.Id;
    private const string RootIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>Standalone entry point for registration in the smoke runner.</summary>
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertLifecycleAndStableIdentityAsync(Path.Combine(root, "lifecycle"));
        await AssertReferenceGuardFailsClosedAsync(Path.Combine(root, "references"));
        await AssertConcurrentCatalogWritersConvergeAsync(Path.Combine(root, "concurrency"));
        await AssertInvalidAndUntrustedStateIsNeverReplacedAsync(Path.Combine(root, "invalid"));
        await AssertOrdinaryWatchedFolderRegistrationAsync(
            Path.Combine(root, "ordinary-watched"));
        await AssertWatchedRootIdentityMatchesAdapterAsync(
            Path.Combine(root, "ordinary-root-identity"));
        await AssertOrdinaryReplacementMessagesAsync(
            Path.Combine(root, "ordinary-replacement"));
        await AssertProductionReferenceProbeSeparatesSourceFamiliesAsync(
            Path.Combine(root, "production-references"));
        AssertModelPinsSchemaGenerationAndRevisionShape(Path.Combine(root, "model"));
        await XboxDvrInputSourceRecoveryTests.RunAsync(
            Path.Combine(root, "xbox-recovery"));
    }

    private static async Task AssertLifecycleAndStableIdentityAsync(string root)
    {
        var probe = new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced);
        var fixture = Fixture(
            root,
            new Guid("11111111-2222-3333-4444-555555555555"),
            probe);
        var xboxRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(xboxRoot);

        var added = await fixture.Catalog.AddVerifiedAsync(
            "Xbox captures · OneDrive",
            xboxRoot,
            RootIdentity,
            TimeZoneId,
            enabled: true,
            Now);
        Assert(added.Status == RoutingInputSourceMutationStatus.Added &&
               added.Source is
               {
                   SourceId: "source.11111111222233334444555555555555",
                   Revision: 1,
                   Kind: RoutingInputSourceKind.XboxGameDvrOneDrive,
                   Enabled: true,
                   Health: RoutingInputSourceHealth.Ready,
                   AttentionReason: RoutingInputSourceAttentionReason.None,
                   RootIdentitySha256: RootIdentity
               } &&
               added.Source.CanonicalRoot == xboxRoot &&
               added.Source.WindowsTimeZoneId == TimeZoneId,
            "An Xbox source must receive one stable opaque id and canonical durable settings.");
        Assert(!added.Source!.ToString().Contains(xboxRoot, StringComparison.OrdinalIgnoreCase) &&
               !added.Source.ToString().Contains(RootIdentity, StringComparison.Ordinal) &&
               added.Source.ToString().Contains("root and authority redacted", StringComparison.Ordinal),
            "Source diagnostics must not reveal the user's OneDrive path.");

        var persisted = fixture.Store.Load();
        Assert(persisted.LoadedFromDisk && persisted.Document is
               {
                   SchemaVersion: RoutingInputSourceCatalogStore.CurrentSchemaVersion,
                   Generation: 2,
                   Sources.Count: 1
               },
            "The source catalog must persist its schema, generation, and one source.");
        var duplicate = await fixture.Catalog.AddVerifiedAsync(
            "Same root, different name",
            xboxRoot.ToUpperInvariant(),
            RootIdentity,
            TimeZoneId,
            enabled: false,
            Now.AddMinutes(1));
        Assert(duplicate.Status == RoutingInputSourceMutationStatus.AlreadyExists &&
               duplicate.Source!.SourceId == added.Source.SourceId &&
               fixture.Catalog.Inspect().Sources.Count == 1,
            "A canonical Xbox root must converge on its existing stable source id.");

        var renamed = await fixture.Catalog.UpdateAsync(
            added.Source.SourceId,
            "Console captures",
            xboxRoot,
            TimeZoneId,
            Now.AddMinutes(2));
        Assert(renamed.Status == RoutingInputSourceMutationStatus.Updated &&
               renamed.Source is { Revision: 2, DisplayName: "Console captures" } &&
               renamed.Source.SourceId == added.Source.SourceId,
            "A source metadata update must retain its opaque id and advance one revision.");
        var disabled = await fixture.Catalog.DisableAsync(
            added.Source.SourceId, Now.AddMinutes(3));
        Assert(disabled.Status == RoutingInputSourceMutationStatus.Disabled &&
               disabled.Source is { Revision: 3, Enabled: false },
            "Disabling a source must be a durable revision rather than deletion.");
        var attention = await fixture.Catalog.SetNeedsAttentionAsync(
            added.Source.SourceId,
            RoutingInputSourceAttentionReason.MetadataInvalid,
            Now.AddMinutes(4));
        var enabled = await fixture.Catalog.SetEnabledAsync(
            added.Source.SourceId, enabled: true, Now.AddMinutes(5));
        var membership = (IRoutingInputSourceMembership)fixture.Catalog;
        Assert(attention.Source is
               {
                   Revision: 4,
                   Health: RoutingInputSourceHealth.NeedsAttention
               } &&
               enabled.Source is { Revision: 5, Enabled: true } &&
               !membership.IsReady(
                   added.Source.SourceId,
                   RoutingInputSourceKind.XboxGameDvrOneDrive),
            "An enabled source that needs attention must not be ready for route activation.");
        var ready = await fixture.Catalog.TryRestoreReadyAsync(
            added.Source.SourceId,
            enabled.Source!.Revision,
            RootIdentity,
            Now.AddMinutes(6));
        Assert(ready.Source is { Revision: 6, Health: RoutingInputSourceHealth.Ready } &&
               membership.IsReady(
                   added.Source.SourceId,
                   RoutingInputSourceKind.XboxGameDvrOneDrive),
            "Clearing attention must preserve identity and restore membership readiness.");
    }

    private static async Task AssertReferenceGuardFailsClosedAsync(string root)
    {
        var probe = new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced);
        var fixture = Fixture(
            root,
            new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            probe);
        var originalRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        var replacementRoot = Path.GetFullPath(Path.Combine(root, "Different Xbox DVR"));
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(replacementRoot);
        var added = await fixture.Catalog.AddVerifiedAsync(
            "Xbox captures", originalRoot, RootIdentity, TimeZoneId, enabled: true, Now);
        var sourceId = added.Source!.SourceId;

        probe.Set(RoutingInputSourceReferenceStatus.Referenced);
        var redirected = await fixture.Catalog.UpdateAsync(
            sourceId,
            "Redirected",
            replacementRoot,
            TimeZoneId,
            Now.AddMinutes(1));
        Assert(redirected.Status == RoutingInputSourceMutationStatus.ReplacementRequired &&
               fixture.Catalog.Inspect().Sources.Single().CanonicalRoot == originalRoot,
            "A source id must never be silently redirected to another folder, regardless of reference state.");
        var renamed = await fixture.Catalog.UpdateAsync(
            sourceId,
            "Safe display rename",
            originalRoot,
            TimeZoneId,
            Now.AddMinutes(2));
        Assert(renamed.Status == RoutingInputSourceMutationStatus.Updated &&
               renamed.Source!.DisplayName == "Safe display rename",
            "A display-only rename must remain safe while routes reference the source.");
        var disabled = await fixture.Catalog.DisableAsync(sourceId, Now.AddMinutes(3));
        Assert(disabled.Status == RoutingInputSourceMutationStatus.Disabled,
            "A user must be able to disable a referenced source without deleting its identity.");
        var removed = await fixture.Catalog.RemoveAsync(sourceId, Now.AddMinutes(4));
        Assert(removed.Status == RoutingInputSourceMutationStatus.InUse,
            "A durable route or work reference must block source removal.");

        probe.Set(RoutingInputSourceReferenceStatus.StateUnavailable);
        removed = await fixture.Catalog.RemoveAsync(sourceId, Now.AddMinutes(5));
        Assert(removed.Status == RoutingInputSourceMutationStatus.StateUnavailable,
            "Unreadable reference state must fail source removal closed.");

        // The first inspection permits the mutation; the before-commit inspection detects a
        // concurrently-created reference and must still veto the atomic rename.
        probe.SetSequence(
            RoutingInputSourceReferenceStatus.NotReferenced,
            RoutingInputSourceReferenceStatus.Referenced);
        removed = await fixture.Catalog.RemoveAsync(sourceId, Now.AddMinutes(6));
        Assert(removed.Status == RoutingInputSourceMutationStatus.InUse &&
               fixture.Catalog.Inspect().Sources.Any(source => source.SourceId == sourceId),
            "The reference guard must be rechecked immediately before the catalog commit.");

        probe.Set(RoutingInputSourceReferenceStatus.NotReferenced);
        removed = await fixture.Catalog.RemoveAsync(sourceId, Now.AddMinutes(7));
        Assert(removed.Status == RoutingInputSourceMutationStatus.Removed &&
               fixture.Catalog.Inspect().Sources.Count == 0,
            "An unreferenced source may be removed without leaving catalog residue.");
    }

    private static async Task AssertConcurrentCatalogWritersConvergeAsync(string root)
    {
        var store = Store(root);
        var first = new RoutingInputSourceCatalog(
            store,
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => new Guid("10000000-0000-0000-0000-000000000001"),
            () => Now);
        var second = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(store.Path),
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => new Guid("20000000-0000-0000-0000-000000000002"),
            () => Now);
        var firstRoot = Path.GetFullPath(Path.Combine(root, "first"));
        var secondRoot = Path.GetFullPath(Path.Combine(root, "second"));
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);

        var results = await Task.WhenAll(
            first.AddVerifiedAsync(
                "First Xbox", firstRoot, RootIdentity, TimeZoneId, enabled: false, Now),
            second.AddVerifiedAsync(
                "Second Xbox", secondRoot,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                TimeZoneId, enabled: false, Now));
        var loaded = store.Load();
        Assert(results.All(result => result.Status == RoutingInputSourceMutationStatus.Added) &&
               loaded.LoadedFromDisk && loaded.Document is
               {
                   Generation: 3,
                   Sources.Count: 2
               } &&
               loaded.Document.Sources.Select(source => source.SourceId).Distinct().Count() == 2,
            "Concurrent source adds must CAS-retry and retain both stable identities.");

        var duplicateStore = Store(Path.Combine(root, "same-root"));
        var duplicateRoot = Path.GetFullPath(Path.Combine(root, "same"));
        Directory.CreateDirectory(duplicateRoot);
        var duplicateFirst = new RoutingInputSourceCatalog(
            duplicateStore,
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => new Guid("30000000-0000-0000-0000-000000000003"),
            () => Now);
        var duplicateSecond = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(duplicateStore.Path),
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => new Guid("40000000-0000-0000-0000-000000000004"),
            () => Now);
        results = await Task.WhenAll(
            duplicateFirst.AddVerifiedAsync(
                "Xbox A", duplicateRoot, RootIdentity, TimeZoneId, false, Now),
            duplicateSecond.AddVerifiedAsync(
                "Xbox B", duplicateRoot, RootIdentity, TimeZoneId, false, Now));
        Assert(results.Count(result => result.Status == RoutingInputSourceMutationStatus.Added) == 1 &&
               results.Count(result =>
                   result.Status == RoutingInputSourceMutationStatus.AlreadyExists) == 1 &&
               duplicateStore.Load().Document!.Sources.Count == 1,
            "Concurrent registration of one canonical folder must converge on one source record.");
    }

    private static async Task AssertInvalidAndUntrustedStateIsNeverReplacedAsync(string root)
    {
        var badInputs = Fixture(
            Path.Combine(root, "inputs"),
            Guid.NewGuid(),
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced));
        var relative = await badInputs.Catalog.AddVerifiedAsync(
            "Xbox", "relative\\Xbox", RootIdentity, TimeZoneId, enabled: false, Now);
        var badZone = await badInputs.Catalog.AddVerifiedAsync(
            "Xbox",
            Path.GetFullPath(Path.Combine(root, "Xbox")),
            RootIdentity,
            "Definitely/Not/A/Windows/TimeZone",
            enabled: false,
            Now);
        Assert(relative.Status == RoutingInputSourceMutationStatus.InvalidInput &&
               badZone.Status == RoutingInputSourceMutationStatus.InvalidInput &&
               badInputs.Store.Load().Status == RoutingDocumentLoadStatus.Missing,
            "Invalid roots and time zones must not initialize durable source state.");

        var corrupt = Fixture(
            Path.Combine(root, "corrupt"),
            Guid.NewGuid(),
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced));
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt.Store.Path)!);
        await File.WriteAllTextAsync(corrupt.Store.Path, "{ definitely not json");
        var result = await corrupt.Catalog.AddVerifiedAsync(
            "Xbox",
            Path.GetFullPath(Path.Combine(root, "valid")),
            RootIdentity,
            TimeZoneId,
            enabled: false,
            Now);
        Assert(result.Status == RoutingInputSourceMutationStatus.StateUnavailable &&
               await File.ReadAllTextAsync(corrupt.Store.Path) == "{ definitely not json",
            "A corrupt source catalog must never be replaced by a mutation.");

        var unsupported = Fixture(
            Path.Combine(root, "unsupported"),
            Guid.NewGuid(),
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced));
        Directory.CreateDirectory(Path.GetDirectoryName(unsupported.Store.Path)!);
        await File.WriteAllTextAsync(
            unsupported.Store.Path,
            "{\"schemaVersion\":99,\"generation\":1}");
        result = await unsupported.Catalog.AddVerifiedAsync(
            "Xbox",
            Path.GetFullPath(Path.Combine(root, "valid-2")),
            RootIdentity,
            TimeZoneId,
            enabled: false,
            Now);
        Assert(unsupported.Store.Load().Status == RoutingDocumentLoadStatus.UnsupportedSchema &&
               result.Status == RoutingInputSourceMutationStatus.StateUnavailable,
            "An unsupported catalog schema must fail closed without downgrade or overwrite.");
    }

    private static async Task AssertOrdinaryWatchedFolderRegistrationAsync(string root)
    {
        Assert((int)RoutingInputSourceKind.XboxGameDvrOneDrive == 0 &&
               (int)RoutingInputSourceKind.SteelSeriesGg == 1 &&
               (int)RoutingInputSourceKind.Nvidia == 2,
            "Persisted input-source kind numbers must remain stable and append-only.");

        var ids = new Queue<Guid>(
        [
            new Guid("51000000-0000-0000-0000-000000000001"),
            new Guid("52000000-0000-0000-0000-000000000002")
        ]);
        var store = Store(root);
        var catalog = new RoutingInputSourceCatalog(
            store,
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => ids.Dequeue(),
            () => Now);
        var steelSeriesRoot = Path.GetFullPath(Path.Combine(root, "drive-e", "Moments"));
        var otherRoot = Path.GetFullPath(Path.Combine(root, "drive-f", "ShadowPlay"));
        Directory.CreateDirectory(steelSeriesRoot);
        Directory.CreateDirectory(otherRoot);
        const string otherIdentity =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        var added = await catalog.AddVerifiedWatchedFolderAsync(
            "My SteelSeries clips",
            RoutingInputSourceKind.SteelSeriesGg,
            steelSeriesRoot,
            RootIdentity,
            now: Now);
        Assert(added.Status == RoutingInputSourceMutationStatus.Added &&
               added.Source is
               {
                   Revision: 1,
                   Kind: RoutingInputSourceKind.SteelSeriesGg,
                   Enabled: false,
                   Health: RoutingInputSourceHealth.Ready,
                   AttentionReason: RoutingInputSourceAttentionReason.None
               } &&
               added.Source.CanonicalRoot == steelSeriesRoot &&
               added.Source.WindowsTimeZoneId == TimeZoneInfo.Utc.Id &&
               added.Reason.Contains("SteelSeries GG", StringComparison.Ordinal),
            "A verified SteelSeries folder on an arbitrary absolute path must persist Ready and disabled by default.");
        var initiallyDisabledMembership = (IRoutingInputSourceMembership)catalog;
        Assert(initiallyDisabledMembership.IsRouteBindable(
                   added.Source!.SourceId,
                   RoutingInputSourceKind.SteelSeriesGg) &&
               !initiallyDisabledMembership.IsReady(
                   added.Source.SourceId,
                   RoutingInputSourceKind.SteelSeriesGg),
            "A Ready source must be route-bindable before scanning is enabled so route persistence can happen first.");

        var duplicate = await catalog.AddVerifiedWatchedFolderAsync(
            "Same SteelSeries folder",
            RoutingInputSourceKind.SteelSeriesGg,
            steelSeriesRoot.ToUpperInvariant(),
            RootIdentity,
            now: Now.AddMinutes(1));
        Assert(duplicate.Status == RoutingInputSourceMutationStatus.AlreadyExists &&
               duplicate.Source!.SourceId == added.Source!.SourceId &&
               catalog.Inspect().Sources.Count == 1,
            "Repeated registration of one canonical watched source must converge on its stable id.");

        var conflictingKind = await catalog.AddVerifiedWatchedFolderAsync(
            "Wrong geometry for same root",
            RoutingInputSourceKind.Nvidia,
            steelSeriesRoot,
            RootIdentity,
            now: Now.AddMinutes(2));
        Assert(conflictingKind.Status == RoutingInputSourceMutationStatus.InvalidInput &&
               catalog.Inspect().Sources.Count == 1,
            "One active canonical root cannot be registered under two recorder geometries.");

        var nestedRoot = Path.Combine(steelSeriesRoot, "Nested NVIDIA root");
        Directory.CreateDirectory(nestedRoot);
        var nestedOverlap = await catalog.AddVerifiedWatchedFolderAsync(
            "Nested NVIDIA",
            RoutingInputSourceKind.Nvidia,
            nestedRoot,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            now: Now.AddMinutes(2));
        Assert(nestedOverlap.Status == RoutingInputSourceMutationStatus.InvalidInput &&
               catalog.Inspect().Sources.Count == 1,
            "A nested source root must be rejected even when its canonical path and native identity differ.");

        var authorityAlias = await catalog.AddVerifiedWatchedFolderAsync(
            "Same native folder under another path",
            RoutingInputSourceKind.SteelSeriesGg,
            otherRoot,
            RootIdentity,
            now: Now.AddMinutes(3));
        Assert(authorityAlias.Status == RoutingInputSourceMutationStatus.InvalidInput &&
               catalog.Inspect().Sources.Count == 1,
            "One native folder authority cannot be registered under a second canonical path.");

        var replacedAuthority = await catalog.AddVerifiedWatchedFolderAsync(
            "Changed folder authority",
            RoutingInputSourceKind.SteelSeriesGg,
            steelSeriesRoot,
            otherIdentity,
            now: Now.AddMinutes(4));
        Assert(replacedAuthority.Status == RoutingInputSourceMutationStatus.ReplacementRequired &&
               replacedAuthority.Source!.Revision == 1,
            "A path whose native folder identity changed must require explicit replacement.");

        var staleEnable = await catalog.SetEnabledAsync(
            added.Source.SourceId,
            expectedRevision: 2,
            enabled: true,
            now: Now.AddMinutes(5));
        var enabled = await catalog.SetEnabledAsync(
            added.Source.SourceId,
            expectedRevision: 1,
            enabled: true,
            now: Now.AddMinutes(5));
        var membership = (IRoutingInputSourceMembership)catalog;
        Assert(staleEnable.Status == RoutingInputSourceMutationStatus.Conflict &&
               enabled.Source is { Revision: 2, Enabled: true } &&
               membership.IsReady(
                   added.Source.SourceId,
                   RoutingInputSourceKind.SteelSeriesGg) &&
               !membership.IsReady(
                   added.Source.SourceId,
                   RoutingInputSourceKind.Nvidia),
            "Ordinary source activation must use revision CAS and exact-kind membership.");

        var nvidia = await catalog.AddVerifiedWatchedFolderAsync(
            "NVIDIA recordings",
            RoutingInputSourceKind.Nvidia,
            otherRoot,
            otherIdentity,
            now: Now.AddMinutes(6));
        Assert(nvidia.Status == RoutingInputSourceMutationStatus.Added &&
               nvidia.Source is { Kind: RoutingInputSourceKind.Nvidia, Enabled: false } &&
               nvidia.Reason.Contains("NVIDIA", StringComparison.Ordinal) &&
               store.Load().Document is { Generation: 4, Sources.Count: 2 },
            "NVIDIA must be a separate first-class watched source with kind-appropriate feedback.");

        var xboxRejected = await catalog.AddVerifiedWatchedFolderAsync(
            "Not an ordinary source",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            Path.GetFullPath(Path.Combine(root, "xbox")),
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            now: Now.AddMinutes(7));
        Assert(xboxRejected.Status == RoutingInputSourceMutationStatus.InvalidInput,
            "The ordinary watched-folder API must not bypass Xbox metadata verification.");
    }

    private static async Task AssertWatchedRootIdentityMatchesAdapterAsync(string root)
    {
        var steelSeriesRoot = Path.GetFullPath(Path.Combine(root, "SteelSeries"));
        Directory.CreateDirectory(steelSeriesRoot);
        var steelSeriesClip = Path.Combine(steelSeriesRoot, "Game 2026.09.01.mp4");
        await File.WriteAllBytesAsync(steelSeriesClip, [1, 2, 3, 4]);
        var steelSeriesFile = await RoutingWatchedSourceAdapters
            .Get(ClipCaptureSource.SteelSeriesGg)
            .OpenAndFingerprintAsync(steelSeriesRoot, steelSeriesClip);
        var steelSeriesIdentity = RoutingWatchedSourceRootIdentity.Create(
            RoutingInputSourceKind.SteelSeriesGg,
            steelSeriesRoot);
        Assert(steelSeriesIdentity == steelSeriesFile.RootIdentitySha256,
            "SteelSeries catalog verification and file admission must share one native-root identity domain.");

        var nvidiaRoot = Path.GetFullPath(Path.Combine(root, "NVIDIA"));
        var gameRoot = Path.Combine(nvidiaRoot, "Duskfade");
        Directory.CreateDirectory(gameRoot);
        var nvidiaClip = Path.Combine(gameRoot, "Highlight.mp4");
        await File.WriteAllBytesAsync(nvidiaClip, [5, 6, 7, 8]);
        var nvidiaFile = await RoutingWatchedSourceAdapters
            .Get(ClipCaptureSource.Nvidia)
            .OpenAndFingerprintAsync(nvidiaRoot, nvidiaClip);
        var nvidiaIdentity = RoutingWatchedSourceRootIdentity.Create(
            RoutingInputSourceKind.Nvidia,
            nvidiaRoot);
        Assert(nvidiaIdentity == nvidiaFile.RootIdentitySha256 &&
               nvidiaIdentity != steelSeriesIdentity,
            "NVIDIA catalog verification must use its adapter's distinct native-root identity domain.");
    }

    private static async Task AssertOrdinaryReplacementMessagesAsync(string root)
    {
        var ids = new Queue<Guid>(
        [
            new Guid("53000000-0000-0000-0000-000000000003"),
            new Guid("54000000-0000-0000-0000-000000000004"),
            new Guid("55000000-0000-0000-0000-000000000005")
        ]);
        var catalog = new RoutingInputSourceCatalog(
            Store(root),
            new TestReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => ids.Dequeue(),
            () => Now);
        var originalRoot = Path.GetFullPath(Path.Combine(root, "SteelSeries-old"));
        var replacementRoot = Path.GetFullPath(Path.Combine(root, "SteelSeries-new"));
        var guardRoot = Path.GetFullPath(Path.Combine(root, "NVIDIA-guard"));
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(replacementRoot);
        Directory.CreateDirectory(guardRoot);
        const string replacementIdentity =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var added = await catalog.AddVerifiedWatchedFolderAsync(
            "SteelSeries GG",
            RoutingInputSourceKind.SteelSeriesGg,
            originalRoot,
            RootIdentity,
            now: Now);
        var guard = await catalog.AddVerifiedWatchedFolderAsync(
            "NVIDIA guard",
            RoutingInputSourceKind.Nvidia,
            guardRoot,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            now: Now.AddMinutes(1));
        Assert(guard.Status == RoutingInputSourceMutationStatus.Added,
            "The replacement overlap fixture must establish a separate active source.");
        var attention = await catalog.SetNeedsAttentionAsync(
            added.Source!.SourceId,
            added.Source.Revision,
            RoutingInputSourceAttentionReason.RootAuthorityChanged,
            Now.AddMinutes(2));
        var overlappingReplacement = await catalog.RegisterReplacementAsync(
            added.Source.SourceId,
            attention.Source!.Revision,
            Path.Combine(guardRoot, "nested replacement"),
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            enabled: false,
            Now.AddMinutes(3));
        Assert(overlappingReplacement.Status ==
               RoutingInputSourceMutationStatus.InvalidInput,
            "A replacement source must not be admitted inside another active source root.");
        var replaced = await catalog.RegisterReplacementAsync(
            added.Source.SourceId,
            attention.Source.Revision,
            replacementRoot,
            replacementIdentity,
            enabled: false,
            Now.AddMinutes(4));
        Assert(replaced.Status == RoutingInputSourceMutationStatus.Added &&
               replaced.Source is
               {
                   Kind: RoutingInputSourceKind.SteelSeriesGg,
                   Enabled: false,
                   Revision: 1
               } &&
               replaced.Reason.Contains("SteelSeries GG", StringComparison.Ordinal) &&
               !replaced.Reason.Contains("Xbox", StringComparison.Ordinal),
            "Ordinary source replacement must preserve its kind and return recorder-specific guidance.");
        var replacement = replaced.Source ?? throw new InvalidOperationException(
            "The successful ordinary replacement did not return its source record.");

        var overlappingRelocation = await catalog.RelocateVerifiedAsync(
            replacement.SourceId,
            replacement.Revision,
            Path.Combine(guardRoot, "nested relocation"),
            replacementIdentity,
            Now.AddMinutes(5));
        Assert(overlappingRelocation.Status ==
               RoutingInputSourceMutationStatus.InvalidInput,
            "Relocating a source must not create a nested overlap with another active source root.");

        var unchanged = await catalog.RelocateVerifiedAsync(
            replacement.SourceId,
            replacement.Revision,
            replacementRoot,
            replacementIdentity,
            Now.AddMinutes(6));
        Assert(unchanged.Status == RoutingInputSourceMutationStatus.Unchanged &&
               unchanged.Reason.Contains("SteelSeries GG", StringComparison.Ordinal) &&
               !unchanged.Reason.Contains("Xbox", StringComparison.Ordinal),
            "Ordinary source relocation results must not leak Xbox-only copy.");
    }

    private static async Task AssertProductionReferenceProbeSeparatesSourceFamiliesAsync(
        string root)
    {
        var routingRoot = Path.GetFullPath(Path.Combine(root, "routing"));
        var routeStore = new RoutingSnapshotStore(
            Path.Combine(routingRoot, RoutingSnapshotStore.FileName));
        var catalog = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(
                Path.Combine(routingRoot, RoutingInputSourceCatalogStore.FileName)),
            new RoutingInputSourceReferenceProbe(routeStore, routingRoot),
            () => new Guid("61000000-0000-0000-0000-000000000001"),
            () => Now);
        var watchedRoot = Path.GetFullPath(Path.Combine(root, "SteelSeries"));
        Directory.CreateDirectory(watchedRoot);
        var added = await catalog.AddVerifiedWatchedFolderAsync(
            "SteelSeries",
            RoutingInputSourceKind.SteelSeriesGg,
            watchedRoot,
            RootIdentity,
            now: Now);

        var emptyRoutes = await routeStore.LoadOrCreateAsync(Now);
        var boundRoutes = RoutingSnapshotModel.ReplaceRoutes(
            emptyRoutes,
            [SourceBoundRoute(added.Source!.SourceId)],
            Now.AddMinutes(1));
        await routeStore.SaveAsync(boundRoutes, emptyRoutes.Generation);
        var referencedRemoval = await catalog.RemoveAsync(
            added.Source.SourceId,
            added.Source.Revision,
            Now.AddMinutes(2));
        Assert(referencedRemoval.Status == RoutingInputSourceMutationStatus.InUse,
            "A production route reference must block removal of an ordinary watched source.");

        var clearedRoutes = RoutingSnapshotModel.ReplaceRoutes(
            boundRoutes,
            [],
            Now.AddMinutes(3));
        await routeStore.SaveAsync(clearedRoutes, boundRoutes.Generation);
        var foreignXboxStore = new XboxDvrOccurrenceStore(
            routingRoot,
            added.Source.SourceId);
        Directory.CreateDirectory(Path.GetDirectoryName(foreignXboxStore.Path)!);
        await File.WriteAllTextAsync(foreignXboxStore.Path, "{ not an Xbox journal");
        var removed = await catalog.RemoveAsync(
            added.Source.SourceId,
            added.Source.Revision,
            Now.AddMinutes(4));
        Assert(removed.Status == RoutingInputSourceMutationStatus.Removed,
            "An unreferenced ordinary source must not be coupled to Xbox-only occurrence state.");

        var xboxRoutingRoot = Path.GetFullPath(Path.Combine(root, "xbox-routing"));
        var xboxRoutes = new RoutingSnapshotStore(
            Path.Combine(xboxRoutingRoot, RoutingSnapshotStore.FileName));
        var xboxCatalog = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(
                Path.Combine(xboxRoutingRoot, RoutingInputSourceCatalogStore.FileName)),
            new RoutingInputSourceReferenceProbe(xboxRoutes, xboxRoutingRoot),
            () => new Guid("62000000-0000-0000-0000-000000000002"),
            () => Now);
        var xboxRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(xboxRoot);
        var xbox = await xboxCatalog.AddVerifiedAsync(
            "Xbox",
            xboxRoot,
            RootIdentity,
            TimeZoneId,
            enabled: false,
            Now);
        var occurrenceStore = new XboxDvrOccurrenceStore(
            xboxRoutingRoot,
            xbox.Source!.SourceId);
        var journal = new XboxDvrOccurrenceJournal(
            occurrenceStore,
            xbox.Source.SourceId,
            RootIdentity,
            () => Now.AddMinutes(1));
        const string occurrence =
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        const string revision =
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        _ = await journal.SelectAsync(
            occurrence,
            revision,
            "Game_2026.09.01-12.00.mp4",
            "Game",
            Now,
            logicalBytes: 1,
            lastWriteUtcTicks: Now.UtcTicks);
        _ = await journal.BeginImportAsync(occurrence, revision);
        var xboxRemoval = await xboxCatalog.RemoveAsync(
            xbox.Source.SourceId,
            xbox.Source.Revision,
            Now.AddMinutes(2));
        Assert(xboxRemoval.Status == RoutingInputSourceMutationStatus.InUse,
            "Generalizing ordinary-source removal must not weaken pending Xbox occurrence protection.");
    }

    private static RoutingRoute SourceBoundRoute(string sourceId) => new(
        new Guid("71000000-0000-0000-0000-000000000001"),
        "Source-bound route",
        Enabled: false,
        Priority: 0,
        Revision: 1,
        RoutingRouteSource.User,
        RoutingRouteKind.Specific,
        RoutingTriggerKind.WatchedFolder,
        new RoutingPrepareSettings(false, false, RoutingMissingOutputBehavior.UseOriginal),
        [
            new RoutingCondition(
                new Guid("72000000-0000-0000-0000-000000000002"),
                RoutingConditionField.SourceConnection,
                RoutingConditionOperator.Equals,
                sourceId),
            new RoutingCondition(
                new Guid("73000000-0000-0000-0000-000000000003"),
                RoutingConditionField.CapturedAt,
                RoutingConditionOperator.GreaterThanOrEqual,
                Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
        ],
        [
            new RoutingAction(
                new Guid("74000000-0000-0000-0000-000000000004"),
                true,
                RoutingActionKind.FileIntoLibrary,
                null,
                null,
                null,
                null,
                RoutingDeliveryMode.Automatic,
                RoutingLibraryArea.LocalOnly,
                null)
        ],
        Now,
        Now,
        new RoutingXboxHistorySelection(Now, []));

    private static void AssertModelPinsSchemaGenerationAndRevisionShape(string root)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        var empty = RoutingInputSourceCatalogModel.CreateEmpty(Now);
        var source = new RoutingInputSourceRecord(
            "source.0123456789abcdef0123456789abcdef",
            Revision: 1,
            "Xbox captures",
            RoutingInputSourceKind.XboxGameDvrOneDrive,
            sourceRoot,
            Enabled: false,
            Retired: false,
            RoutingInputSourceHealth.Ready,
            RoutingInputSourceAttentionReason.None,
            RootIdentity,
            TimeZoneId,
            Now.AddMinutes(1),
            Now.AddMinutes(1));
        var populated = RoutingInputSourceCatalogModel.ReplaceSources(
            empty, [source], Now.AddMinutes(1));
        RoutingInputSourceCatalogModel.Validate(populated);

        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.Validate(populated with { SchemaVersion = 99 }),
            "Schema changes must be rejected before a source catalog is trusted.");
        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.Validate(populated with
            {
                Sources = [source with { SourceId = "source.NOT-CANONICAL" }]
            }),
            "Source ids must remain canonical opaque identifiers.");
        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.Validate(populated with
            {
                Sources = [source with { Revision = 0 }]
            }),
            "Every source must carry a positive revision.");
        var secondSource = source with
        {
            SourceId = "source.1123456789abcdef0123456789abcdef",
            DisplayName = "NVIDIA recordings",
            Kind = RoutingInputSourceKind.Nvidia,
            CanonicalRoot = Path.GetFullPath(Path.Combine(root, "NVIDIA")),
            RootIdentitySha256 =
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.Validate(populated with
            {
                Sources = [source, secondSource with { CanonicalRoot = sourceRoot }]
            }),
            "Active source roots must remain unique across recorder kinds.");
        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.Validate(populated with
            {
                Sources =
                [
                    source,
                    secondSource with
                    {
                        CanonicalRoot = Path.Combine(sourceRoot, "Nested source")
                    }
                ]
            }),
            "Persisted active source roots must reject parent/child overlap, not only exact duplicates.");
        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.Validate(populated with
            {
                Sources = [source, secondSource with { RootIdentitySha256 = RootIdentity }]
            }),
            "Native root authorities must remain unique across paths and recorder kinds.");

        var changedWithoutRevision = populated with
        {
            Generation = populated.Generation + 1,
            Sources = [source with { DisplayName = "Changed without revision" }],
            UpdatedUtc = Now.AddMinutes(2)
        };
        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.ValidateSuccessor(
                populated, changedWithoutRevision),
            "A source may not change without advancing exactly one revision.");
        var skippedRevision = changedWithoutRevision with
        {
            Sources =
            [
                source with
                {
                    Revision = 3,
                    DisplayName = "Skipped revision",
                    UpdatedUtc = Now.AddMinutes(2)
                }
            ]
        };
        AssertThrows<InvalidDataException>(() =>
            RoutingInputSourceCatalogModel.ValidateSuccessor(populated, skippedRevision),
            "A source revision may not skip durable history.");
    }

    private static CatalogFixture Fixture(
        string root,
        Guid id,
        IRoutingInputSourceReferenceProbe references)
    {
        var store = Store(root);
        return new CatalogFixture(
            new RoutingInputSourceCatalog(store, references, () => id, () => Now),
            store);
    }

    private static RoutingInputSourceCatalogStore Store(string root) => new(
        Path.Combine(root, "routing", RoutingInputSourceCatalogStore.FileName));

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

    private sealed record CatalogFixture(
        RoutingInputSourceCatalog Catalog,
        RoutingInputSourceCatalogStore Store);

    private sealed class TestReferenceProbe : IRoutingInputSourceReferenceProbe
    {
        private readonly object _sync = new();
        private Queue<RoutingInputSourceReferenceStatus> _sequence = new();
        private RoutingInputSourceReferenceStatus _fallback;

        internal TestReferenceProbe(RoutingInputSourceReferenceStatus fallback) =>
            _fallback = fallback;

        internal void Set(RoutingInputSourceReferenceStatus status)
        {
            lock (_sync)
            {
                _sequence.Clear();
                _fallback = status;
            }
        }

        internal void SetSequence(params RoutingInputSourceReferenceStatus[] statuses)
        {
            lock (_sync)
            {
                _sequence = new Queue<RoutingInputSourceReferenceStatus>(statuses);
                if (statuses.Length > 0) _fallback = statuses[^1];
            }
        }

        public RoutingInputSourceReferenceStatus Inspect(
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            lock (_sync)
            {
                return _sequence.Count > 0 ? _sequence.Dequeue() : _fallback;
            }
        }
    }
}
