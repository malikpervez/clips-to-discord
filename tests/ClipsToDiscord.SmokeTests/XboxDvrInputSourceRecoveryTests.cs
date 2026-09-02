using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

internal static class XboxDvrInputSourceRecoveryTests
{
    private const string RootIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ReplacementIdentity =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ThirdIdentity =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await AssertRegistrationAndSameRootRecoveryAsync(
            Path.Combine(root, "same-root"));
        await AssertReplacementTombstoneAndRelocationAsync(
            Path.Combine(root, "authority-change"));
        await AssertRetiredTombstoneRegistrationSemanticsAsync(
            Path.Combine(root, "retired-registration"));
        await AssertRevisionRecoveryIsExactAndCasSafeAsync(
            Path.Combine(root, "revision"));
        await AssertBlockedOccurrenceResolutionIsExplicitAndDurableAsync(
            Path.Combine(root, "blocked-occurrence"));
        await AssertBlockedOccurrenceRetryIsRecoverableAndFailClosedAsync(
            Path.Combine(root, "blocked-retry"));
        await AssertSelectionAndRemovalAreLinearAsync(
            Path.Combine(root, "selection-removal"));
        await AssertImportingWorkBlocksDisableAndReplacementAsync(
            Path.Combine(root, "importing-management"));
        await AssertProductionReferenceProbeFailsClosedAsync(
            Path.Combine(root, "references"));
    }

    private static async Task AssertRegistrationAndSameRootRecoveryAsync(string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var catalog = Catalog(
            routingRoot,
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            new Guid("11111111-2222-3333-4444-555555555555"));
        var inspector = new TestIdentityInspector((_, _) =>
            new XboxDvrRuntimeMetadataSnapshot(RootIdentity, []));
        var service = new XboxDvrInputSourceRecoveryService(
            catalog, routingRoot, inspector, () => Now);

        var registered = await service.RegisterAsync(
            "Xbox captures", sourceRoot, TimeZoneInfo.Utc.Id, enabled: true);
        Assert(registered.Status == XboxDvrSourceRegistrationStatus.Registered &&
               registered.Source is
               {
                   Revision: 1,
                   Health: RoutingInputSourceHealth.Ready,
                   AttentionReason: RoutingInputSourceAttentionReason.None,
                   Retired: false
               } &&
               registered.Source.RootIdentitySha256 == RootIdentity,
            "Registration must persist the metadata-only native root identity before a source becomes Ready.");

        var attention = await catalog.SetNeedsAttentionAsync(
            registered.Source!.SourceId,
            RoutingInputSourceAttentionReason.MetadataCapacityExceeded,
            Now.AddMinutes(1));
        var restored = await service.RecheckAsync(
            attention.Source!.SourceId, attention.Source.Revision);
        Assert(restored.Status == XboxDvrSourceRecoveryStatus.Restored &&
               restored.Source is
               {
                   Revision: 3,
                   Health: RoutingInputSourceHealth.Ready,
                   AttentionReason: RoutingInputSourceAttentionReason.None
               } &&
               inspector.Inspections == 2,
            "A source whose same root is healthy and under-cap again must recover even before its occurrence journal exists.");

        attention = await catalog.SetNeedsAttentionAsync(
            restored.Source!.SourceId,
            RoutingInputSourceAttentionReason.MetadataInvalid,
            Now.AddSeconds(90));
        var metadataRestored = await service.RecheckAsync(
            attention.Source!.SourceId, attention.Source.Revision);
        Assert(metadataRestored.Status == XboxDvrSourceRecoveryStatus.Restored &&
               metadataRestored.Source is
               {
                   Health: RoutingInputSourceHealth.Ready,
                   AttentionReason: RoutingInputSourceAttentionReason.None
               },
            "Corrected source metadata plus a missing journal must restore a MetadataInvalid source.");

        var store = new XboxDvrOccurrenceStore(
            routingRoot, metadataRestored.Source!.SourceId);
        var occurrenceJournal = new XboxDvrOccurrenceJournal(
            store, metadataRestored.Source.SourceId, RootIdentity, () => Now);
        const string unknownOccurrence =
            "1111111111111111111111111111111111111111111111111111111111111111";
        const string unknownRevision =
            "2222222222222222222222222222222222222222222222222222222222222222";
        _ = await occurrenceJournal.SelectAsync(
            unknownOccurrence,
            unknownRevision,
            "Unknown-2026_09_01-12-00-00.mp4",
            "Unknown",
            Now,
            logicalBytes: 10,
            lastWriteUtcTicks: Now.UtcTicks);
        _ = await occurrenceJournal.MarkNeedsAttentionAsync(
            unknownOccurrence, unknownRevision, "unknown-durable-error");
        attention = await catalog.SetNeedsAttentionAsync(
            metadataRestored.Source.SourceId,
            RoutingInputSourceAttentionReason.MetadataInvalid,
            Now.AddSeconds(100));
        var unknownBlocked = await service.RecheckAsync(
            attention.Source!.SourceId, attention.Source.Revision);
        Assert(unknownBlocked.Status == XboxDvrSourceRecoveryStatus.StillNeedsAttention &&
               catalog.Inspect().Sources.Single().Health ==
               RoutingInputSourceHealth.NeedsAttention,
            "A MetadataInvalid source with unknown durable journal evidence must stay blocked.");

        attention = await catalog.SetNeedsAttentionAsync(
            unknownBlocked.Source!.SourceId,
            RoutingInputSourceAttentionReason.JournalUnavailable,
            Now.AddMinutes(2));
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        await File.WriteAllTextAsync(store.Path, "{ corrupt journal");
        var corrupt = await service.RecheckAsync(
            attention.Source!.SourceId, attention.Source.Revision);
        Assert(corrupt.Status == XboxDvrSourceRecoveryStatus.StillNeedsAttention &&
               catalog.Inspect().Sources.Single().Revision == attention.Source.Revision &&
               catalog.Inspect().Sources.Single().Health ==
               RoutingInputSourceHealth.NeedsAttention,
            "A corrupt occurrence journal must never be replaced or cleared by source recheck.");
    }

    private static async Task AssertReplacementTombstoneAndRelocationAsync(string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var originalRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        var relocatedRoot = Path.GetFullPath(Path.Combine(root, "Moved Xbox Game DVR"));
        var wrongRoot = Path.GetFullPath(Path.Combine(root, "Different Xbox Game DVR"));
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(relocatedRoot);
        Directory.CreateDirectory(wrongRoot);

        var references = new FixedReferenceProbe(
            RoutingInputSourceReferenceStatus.Referenced);
        var ids = new Queue<Guid>(
        [
            new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            new Guid("ffffffff-1111-2222-3333-444444444444")
        ]);
        var catalog = Catalog(routingRoot, references, () => ids.Dequeue());
        var snapshots = new Dictionary<string, XboxDvrRuntimeMetadataSnapshot>(
            StringComparer.OrdinalIgnoreCase)
        {
            [originalRoot] = new(RootIdentity, []),
            [relocatedRoot] = new(RootIdentity, []),
            [wrongRoot] = new(ReplacementIdentity, [])
        };
        var inspector = new TestIdentityInspector((path, _) => snapshots[path]);
        var service = new XboxDvrInputSourceRecoveryService(
            catalog, routingRoot, inspector, () => Now);
        var registered = await service.RegisterAsync(
            "Xbox captures", originalRoot, TimeZoneInfo.Utc.Id, enabled: false);
        var sourceId = registered.Source!.SourceId;
        var moveAttention = await catalog.SetNeedsAttentionAsync(
            sourceId,
            registered.Source.Revision,
            RoutingInputSourceAttentionReason.RootAuthorityChanged,
            Now.AddSeconds(1));

        var relocated = await service.RelocateAsync(
            sourceId, moveAttention.Source!.Revision, relocatedRoot);
        var relocatedSource = relocated.Source ??
            throw new InvalidOperationException("Relocation did not return its source.");
        Assert(relocated.Status == XboxDvrSourceRecoveryStatus.Relocated &&
               relocatedSource.SourceId == sourceId &&
               relocatedSource.RootIdentitySha256 == RootIdentity &&
               relocatedSource.CanonicalRoot == relocatedRoot &&
               relocatedSource.Health == RoutingInputSourceHealth.Ready &&
               relocatedSource.AttentionReason == RoutingInputSourceAttentionReason.None,
            "A verified path move of the same native root must preserve SourceId and clear only the resolved root-authority fault.");

        var wrongRelocation = await service.RelocateAsync(
            sourceId, relocatedSource.Revision, wrongRoot);
        Assert(wrongRelocation.Status ==
               XboxDvrSourceRecoveryStatus.ReplacementRequired &&
               catalog.Inspect().Sources.Single().CanonicalRoot == relocatedRoot,
            "A different native root must never inherit the existing SourceId through relocation.");

        inspector.BeforeInspect = () =>
        {
            _ = catalog.SetEnabledAsync(
                    sourceId,
                    relocatedSource.Revision,
                    enabled: true,
                    Now.AddMinutes(1))
                .GetAwaiter().GetResult();
            inspector.BeforeInspect = null;
        };
        var relocationConflict = await service.RelocateAsync(
            sourceId, relocatedSource.Revision, originalRoot);
        var afterConflict = catalog.Inspect().Sources.Single();
        Assert(relocationConflict.Status == XboxDvrSourceRecoveryStatus.Conflict &&
               afterConflict.CanonicalRoot == relocatedRoot &&
               afterConflict.Revision == relocatedSource.Revision + 1,
            "A concurrent source revision must prevent a stale verified relocation.");

        var metadataAttention = await catalog.SetNeedsAttentionAsync(
            sourceId,
            afterConflict.Revision,
            RoutingInputSourceAttentionReason.MetadataInvalid,
            Now.AddMinutes(2));
        var metadataRelocation = await service.RelocateAsync(
            sourceId, metadataAttention.Source!.Revision, originalRoot);
        Assert(metadataRelocation.Status == XboxDvrSourceRecoveryStatus.Relocated &&
               metadataRelocation.Source is
               {
                   Health: RoutingInputSourceHealth.NeedsAttention,
                   AttentionReason: RoutingInputSourceAttentionReason.MetadataInvalid
               },
            "Relocating the same authority must not clear unrelated invalid metadata evidence.");

        var attention = await catalog.SetNeedsAttentionAsync(
            sourceId,
            metadataRelocation.Source!.Revision,
            RoutingInputSourceAttentionReason.RootAuthorityChanged,
            Now.AddMinutes(3));
        var replacement = await service.RegisterReplacementAsync(
            sourceId,
            attention.Source!.Revision,
            wrongRoot,
            enabled: false);
        var records = catalog.Inspect().Sources;
        var retired = records.Single(item => item.SourceId == sourceId);
        var current = records.Single(item => item.SourceId != sourceId);
        Assert(replacement.Status == XboxDvrSourceRegistrationStatus.Registered &&
               records.Count == 2 && retired.Retired && !retired.Enabled &&
               retired.RootIdentitySha256 == RootIdentity &&
               retired.CanonicalRoot == originalRoot &&
               current.SourceId == replacement.Source!.SourceId &&
               current.RootIdentitySha256 == ReplacementIdentity &&
               current.CanonicalRoot == wrongRoot && !current.Retired &&
               current.Health == RoutingInputSourceHealth.Ready,
            "A different authority must atomically tombstone the old source and allocate a new SourceId without copying route/history bindings.");
        var blockedRemoval = await catalog.RemoveAsync(
            retired.SourceId, retired.Revision, Now.AddMinutes(4));
        Assert(blockedRemoval.Status == RoutingInputSourceMutationStatus.InUse,
            "Existing route/work references must continue to protect a retired source while the replacement remains usable.");
    }

    private static async Task AssertRevisionRecoveryIsExactAndCasSafeAsync(string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var catalog = Catalog(
            routingRoot,
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            new Guid("12345678-1234-1234-1234-123456789abc"));
        var original = Candidate("Battlefield 6-2026_09_01-12-00-00.mp4", 0x42);
        var changed = original with { LastWriteUtcTicks = original.LastWriteUtcTicks + 1 };
        var inspector = new TestIdentityInspector((_, _) =>
            new XboxDvrRuntimeMetadataSnapshot(RootIdentity, [original]));
        var service = new XboxDvrInputSourceRecoveryService(
            catalog, routingRoot, inspector, () => Now);
        var registered = await service.RegisterAsync(
            "Xbox captures", sourceRoot, TimeZoneInfo.Utc.Id, enabled: false);
        var source = registered.Source!;
        var parsed = XboxDvrFileNameParser.Parse(original.PortableRelativePath);
        var occurrenceId = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            source.SourceId, original.PortableRelativePath, original);
        var revisionId = XboxDvrSourceAdapter.CreateRevisionIdentity(
            occurrenceId, parsed.CapturedUtc!.Value, original);
        var store = new XboxDvrOccurrenceStore(routingRoot, source.SourceId);
        var journal = new XboxDvrOccurrenceJournal(
            store, source.SourceId, RootIdentity, () => Now);
        _ = await journal.SelectAsync(
            occurrenceId,
            revisionId,
            original.PortableRelativePath,
            parsed.GameName!,
            parsed.CapturedUtc.Value,
            original.LogicalBytes,
            original.LastWriteUtcTicks);
        _ = await journal.MarkNeedsAttentionAsync(
            occurrenceId, revisionId, "source-replaced");
        var attention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            RoutingInputSourceAttentionReason.SourceRevisionChanged,
            Now.AddMinutes(1));

        inspector.SnapshotFactory = (_, _) =>
            new XboxDvrRuntimeMetadataSnapshot(RootIdentity, [changed]);
        var persistentMismatch = await service.RecheckAsync(
            source.SourceId, attention.Source!.Revision);
        Assert(persistentMismatch.Status ==
               XboxDvrSourceRecoveryStatus.StillNeedsAttention &&
               journal.Load().Document!.Occurrences.Single().State ==
               XboxDvrOccurrenceState.NeedsAttention,
            "An unchanged bad clip revision must remain blocked instead of becoming Ready and immediately flipping back.");

        inspector.SnapshotFactory = (_, _) =>
            new XboxDvrRuntimeMetadataSnapshot(RootIdentity, [original]);
        inspector.BeforeInspect = () =>
        {
            _ = catalog.SetEnabledAsync(
                    source.SourceId,
                    attention.Source.Revision,
                    enabled: true,
                    Now.AddMinutes(2))
                .GetAwaiter().GetResult();
            inspector.BeforeInspect = null;
        };
        var conflict = await service.RecheckAsync(
            source.SourceId, attention.Source.Revision);
        var afterConflict = catalog.Inspect().Sources.Single();
        Assert(conflict.Status == XboxDvrSourceRecoveryStatus.Conflict &&
               journal.Load().Document!.Occurrences.Single().State ==
               XboxDvrOccurrenceState.NeedsAttention,
            "A catalog CAS conflict must happen before journal repair, leaving exact source-revision evidence retryable.");

        var recovered = await service.RecheckAsync(
            source.SourceId, afterConflict.Revision);
        var recoveredSource = recovered.Source ??
            throw new InvalidOperationException("Recovery did not return its source.");
        var recoveredOccurrence = journal.Load().Document!.Occurrences.Single();
        Assert(recovered.Status == XboxDvrSourceRecoveryStatus.Restored &&
               recoveredSource.Health == RoutingInputSourceHealth.Ready &&
               recoveredOccurrence.State == XboxDvrOccurrenceState.Selected &&
               recoveredOccurrence.ErrorCode is null,
            "Restoring the exact frozen revision must repair both catalog health and occurrence state without changing admission evidence.");

        var zeroRemainingAttention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            recoveredSource.Revision,
            RoutingInputSourceAttentionReason.SourceRevisionChanged,
            Now.AddMinutes(3));
        var zeroRemaining = await service.RecheckAsync(
            source.SourceId, zeroRemainingAttention.Source!.Revision);
        Assert(zeroRemaining.Status == XboxDvrSourceRecoveryStatus.Restored &&
               journal.Load().Document!.Occurrences.Single().State ==
               XboxDvrOccurrenceState.Selected,
            "Recovery must converge when the final journal restore committed before a lost response re-marked the source.");

        var second = Candidate("Battlefield 6-2026_09_01-12-01-00.mp4", 0x43);
        var secondParsed = XboxDvrFileNameParser.Parse(second.PortableRelativePath);
        var secondOccurrenceId = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            source.SourceId, second.PortableRelativePath, second);
        var secondRevisionId = XboxDvrSourceAdapter.CreateRevisionIdentity(
            secondOccurrenceId, secondParsed.CapturedUtc!.Value, second);
        _ = await journal.SelectAsync(
            secondOccurrenceId,
            secondRevisionId,
            second.PortableRelativePath,
            secondParsed.GameName!,
            secondParsed.CapturedUtc.Value,
            second.LogicalBytes,
            second.LastWriteUtcTicks);
        _ = await journal.MarkNeedsAttentionAsync(
            occurrenceId, revisionId, "source-replaced");
        _ = await journal.MarkNeedsAttentionAsync(
            secondOccurrenceId, secondRevisionId, "source-replaced");
        _ = await journal.RestoreSourceRevisionAsync(occurrenceId, revisionId);
        inspector.SnapshotFactory = (_, _) =>
            new XboxDvrRuntimeMetadataSnapshot(RootIdentity, [original, second]);
        var partialAttention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            zeroRemaining.Source!.Revision,
            RoutingInputSourceAttentionReason.SourceRevisionChanged,
            Now.AddMinutes(4));
        var partial = await service.RecheckAsync(
            source.SourceId, partialAttention.Source!.Revision);
        var partialEntries = journal.Load().Document!.Occurrences;
        Assert(partial.Status == XboxDvrSourceRecoveryStatus.Restored &&
               partialEntries.All(item =>
                   item.State == XboxDvrOccurrenceState.Selected &&
                   item.ErrorCode is null),
            "A partially repaired revision journal must validate prior repairs and resume the remaining exact entries.");
    }

    private static async Task AssertRetiredTombstoneRegistrationSemanticsAsync(
        string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var ids = new Queue<Guid>(
        [
            new Guid("10000000-0000-0000-0000-000000000001"),
            new Guid("20000000-0000-0000-0000-000000000002"),
            new Guid("30000000-0000-0000-0000-000000000003")
        ]);
        var catalog = Catalog(
            routingRoot,
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => ids.Dequeue());
        var original = await catalog.AddVerifiedAsync(
            "Xbox captures", sourceRoot, RootIdentity, TimeZoneInfo.Utc.Id,
            enabled: false, Now);
        var attention = await catalog.SetNeedsAttentionAsync(
            original.Source!.SourceId,
            original.Source.Revision,
            RoutingInputSourceAttentionReason.RootAuthorityChanged,
            Now.AddMinutes(1));
        var replacement = await catalog.RegisterReplacementAsync(
            original.Source.SourceId,
            attention.Source!.Revision,
            sourceRoot,
            ReplacementIdentity,
            enabled: false,
            Now.AddMinutes(2));
        var removed = await catalog.RemoveAsync(
            replacement.Source!.SourceId,
            replacement.Source.Revision,
            Now.AddMinutes(3));
        Assert(removed.Status == RoutingInputSourceMutationStatus.Removed,
            "An unreferenced active replacement must remain removable while its tombstone is retained.");

        var sameAuthority = await catalog.AddVerifiedAsync(
            "Original authority", sourceRoot, RootIdentity, TimeZoneInfo.Utc.Id,
            enabled: false, Now.AddMinutes(4));
        Assert(sameAuthority.Status == RoutingInputSourceMutationStatus.AlreadyExists &&
               sameAuthority.Source is { Retired: true },
            "A retired native authority must stay fail-closed instead of being duplicated.");

        var newAuthority = await catalog.AddVerifiedAsync(
            "New folder authority", sourceRoot, ThirdIdentity, TimeZoneInfo.Utc.Id,
            enabled: false, Now.AddMinutes(5));
        Assert(newAuthority.Status == RoutingInputSourceMutationStatus.Added &&
               newAuthority.Source is { Retired: false } &&
               newAuthority.Source.RootIdentitySha256 == ThirdIdentity,
            "A different authority must be registerable at a path occupied only by an old tombstone.");
    }

    private static async Task AssertProductionReferenceProbeFailsClosedAsync(string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        Directory.CreateDirectory(routingRoot);
        var routeStore = new RoutingSnapshotStore(
            Path.Combine(routingRoot, RoutingSnapshotStore.FileName));
        var probe = new RoutingInputSourceReferenceProbe(routeStore, routingRoot);
        var catalog = Catalog(
            routingRoot,
            probe,
            new Guid("abcdefab-cdef-abcd-efab-cdefabcdefab"));
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var added = await catalog.AddVerifiedAsync(
            "Xbox captures",
            sourceRoot,
            RootIdentity,
            TimeZoneInfo.Utc.Id,
            enabled: false,
            Now);
        var source = added.Source!;

        var empty = await routeStore.LoadOrCreateAsync(Now);
        var route = XboxRoute(source.SourceId);
        var withRoute = RoutingSnapshotModel.ReplaceRoutes(
            empty, [route], Now.AddMinutes(1));
        _ = await routeStore.SaveAsync(withRoute, empty.Generation);
        var routeBlocked = await catalog.RemoveAsync(
            source.SourceId, source.Revision, Now.AddMinutes(2));
        Assert(routeBlocked.Status == RoutingInputSourceMutationStatus.InUse,
            "The production reference probe must block removal while any enabled or disabled route names the source.");

        var withoutRoute = RoutingSnapshotModel.ReplaceRoutes(
            withRoute, [], Now.AddMinutes(3));
        _ = await routeStore.SaveAsync(withoutRoute, withRoute.Generation);
        var candidate = Candidate("Battlefield 6-2026_09_01-13-00-00.mp4", 0x33);
        var parsed = XboxDvrFileNameParser.Parse(candidate.PortableRelativePath);
        var occurrenceId = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            source.SourceId, candidate.PortableRelativePath, candidate);
        var revisionId = XboxDvrSourceAdapter.CreateRevisionIdentity(
            occurrenceId, parsed.CapturedUtc!.Value, candidate);
        var occurrenceStore = new XboxDvrOccurrenceStore(routingRoot, source.SourceId);
        var journal = new XboxDvrOccurrenceJournal(
            occurrenceStore, source.SourceId, RootIdentity, () => Now);
        _ = await journal.SelectAsync(
            occurrenceId,
            revisionId,
            candidate.PortableRelativePath,
            parsed.GameName!,
            parsed.CapturedUtc.Value,
            candidate.LogicalBytes,
            candidate.LastWriteUtcTicks);
        Assert(probe.Inspect(source.SourceId) ==
               RoutingInputSourceReferenceStatus.Referenced,
            "Selected occurrence work must remain a live source reference before explicit management reconciliation.");
        var validJournal = await File.ReadAllTextAsync(occurrenceStore.Path);
        await File.WriteAllTextAsync(occurrenceStore.Path, "{ corrupt journal");
        var corruptBlocked = await catalog.RemoveAsync(
            source.SourceId, source.Revision, Now.AddMinutes(4));
        Assert(corruptBlocked.Status == RoutingInputSourceMutationStatus.StateUnavailable,
            "Unreadable occurrence-reference state must fail source removal closed.");
        await File.WriteAllTextAsync(occurrenceStore.Path, validJournal);
        var reconciled = await catalog.RemoveAsync(
            source.SourceId, source.Revision, Now.AddMinutes(5));
        var terminal = occurrenceStore.Load().Document!.Occurrences.Single();
        Assert(reconciled.Status == RoutingInputSourceMutationStatus.Removed &&
               terminal.State == XboxDvrOccurrenceState.RouteRevoked,
            "With no remaining source route, management removal must terminalize Selected work under the shared authority gate.");
    }

    private static async Task AssertBlockedOccurrenceResolutionIsExplicitAndDurableAsync(
        string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var sourceSentinel = Path.Combine(sourceRoot, "original-stays-here.mp4");
        await File.WriteAllBytesAsync(sourceSentinel, [0x10, 0x20, 0x30]);
        var catalog = Catalog(
            routingRoot,
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            new Guid("44444444-5555-6666-7777-888888888888"));
        var inspector = new TestIdentityInspector((_, _) =>
            new XboxDvrRuntimeMetadataSnapshot(RootIdentity, []));
        var service = new XboxDvrInputSourceRecoveryService(
            catalog, routingRoot, inspector, () => Now);
        var registered = await service.RegisterAsync(
            "Xbox captures", sourceRoot, TimeZoneInfo.Utc.Id, enabled: true);
        var source = registered.Source!;
        var bad = Candidate("Battlefield 6-2026_09_01-14-00-00.mp4", 0x51);
        var good = Candidate("Battlefield 6-2026_09_01-14-01-00.mp4", 0x52);
        var journal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(routingRoot, source.SourceId),
            source.SourceId,
            source.RootIdentitySha256,
            () => Now);
        var badEntry = await SelectAsync(journal, source.SourceId, bad);
        var goodEntry = await SelectAsync(journal, source.SourceId, good);
        _ = await journal.MarkNeedsAttentionAsync(
            badEntry.OccurrenceId, badEntry.RevisionId, "invalid-media");
        var attention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            source.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(1));

        var resolved = await service.SkipBlockedOccurrencesAsync(
            source.SourceId, attention.Source!.Revision);
        var after = journal.Load().Document!.Occurrences;
        Assert(resolved.Status == XboxDvrBlockedOccurrenceResolutionStatus.Resolved &&
               resolved.SkippedCount == 1 &&
               resolved.Source is { Health: RoutingInputSourceHealth.Ready } &&
               after.Single(item => item.OccurrenceId == badEntry.OccurrenceId) is
               {
                   State: XboxDvrOccurrenceState.Skipped,
                   ErrorCode: "skipped-invalid-media"
               } &&
               after.Single(item => item.OccurrenceId == goodEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.Selected &&
               File.ReadAllBytes(sourceSentinel).SequenceEqual(new byte[] { 0x10, 0x20, 0x30 }),
            "Explicit skip must terminalize only the known blocker, preserve later work, and never touch the OneDrive original.");

        var replayAttention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            resolved.Source!.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(2));
        var replay = await new XboxDvrInputSourceRecoveryService(
                catalog, routingRoot, inspector, () => Now.AddMinutes(3))
            .SkipBlockedOccurrencesAsync(source.SourceId, replayAttention.Source!.Revision);
        Assert(replay.Status ==
               XboxDvrBlockedOccurrenceResolutionStatus.NoEligibleBlockedClips &&
               journal.Load().Document!.Occurrences.Single(item =>
                   item.OccurrenceId == badEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.Skipped,
            "A restart after the skip committed but readiness response was lost must converge without retrying the clip.");

        _ = await journal.MarkNeedsAttentionAsync(
            goodEntry.OccurrenceId, goodEntry.RevisionId, "source-replaced");
        var unsupportedAttention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            replay.Source!.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(4));
        var unsupported = await service.SkipBlockedOccurrencesAsync(
            source.SourceId, unsupportedAttention.Source!.Revision);
        Assert(unsupported.Status ==
               XboxDvrBlockedOccurrenceResolutionStatus.UnsupportedBlocker &&
               journal.Load().Document!.Occurrences.Single(item =>
                   item.OccurrenceId == goodEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.NeedsAttention,
            "An unsupported blocker must fail closed without skipping any additional occurrence.");

        var emptyRoot = Path.Combine(root, "empty-block");
        var emptyCatalog = Catalog(
            Path.Combine(emptyRoot, "routing"),
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            new Guid("99999999-aaaa-bbbb-cccc-dddddddddddd"));
        var emptyService = new XboxDvrInputSourceRecoveryService(
            emptyCatalog, Path.Combine(emptyRoot, "routing"), inspector, () => Now);
        var emptySourceRoot = Path.Combine(emptyRoot, "Xbox Game DVR");
        Directory.CreateDirectory(emptySourceRoot);
        var emptyRegistered = await emptyService.RegisterAsync(
            "Empty Xbox", emptySourceRoot, TimeZoneInfo.Utc.Id, enabled: true);
        var emptyJournal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(
                Path.Combine(emptyRoot, "routing"), emptyRegistered.Source!.SourceId),
            emptyRegistered.Source.SourceId,
            RootIdentity,
            () => Now);
        var emptyCandidate = Candidate(
            "Battlefield 6-2026_09_01-15-00-00.mp4", 0x53);
        _ = await SelectAsync(emptyJournal, emptyRegistered.Source.SourceId, emptyCandidate);
        var emptyAttention = await emptyCatalog.SetNeedsAttentionAsync(
            emptyRegistered.Source.SourceId,
            emptyRegistered.Source.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(1));
        var empty = await emptyService.SkipBlockedOccurrencesAsync(
            emptyRegistered.Source.SourceId, emptyAttention.Source!.Revision);
        Assert(empty.Status == XboxDvrBlockedOccurrenceResolutionStatus.UnsupportedBlocker &&
               emptyCatalog.Inspect().Sources.Single().Health ==
               RoutingInputSourceHealth.NeedsAttention,
            "A blocked source with no durable qualifying skip evidence must never become Ready.");

        await File.WriteAllTextAsync(
            new XboxDvrOccurrenceStore(
                Path.Combine(emptyRoot, "routing"), emptyRegistered.Source.SourceId).Path,
            "{ corrupt journal");
        var corrupt = await emptyService.SkipBlockedOccurrencesAsync(
            emptyRegistered.Source.SourceId, emptyAttention.Source.Revision);
        Assert(corrupt.Status ==
               XboxDvrBlockedOccurrenceResolutionStatus.StateUnavailable,
            "A corrupt blocked-occurrence journal must fail closed.");
    }

    private static async Task AssertBlockedOccurrenceRetryIsRecoverableAndFailClosedAsync(
        string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var sourceSentinel = Path.Combine(sourceRoot, "original-stays-here.mp4");
        await File.WriteAllBytesAsync(sourceSentinel, [0x31, 0x41, 0x59]);
        var catalog = Catalog(
            routingRoot,
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            new Guid("51515151-6262-7373-8484-959595959595"));
        var inspector = new TestIdentityInspector((_, _) =>
            new XboxDvrRuntimeMetadataSnapshot(RootIdentity, []));
        var service = new XboxDvrInputSourceRecoveryService(
            catalog, routingRoot, inspector, () => Now);
        var registered = await service.RegisterAsync(
            "Xbox captures", sourceRoot, TimeZoneInfo.Utc.Id, enabled: true);
        var source = registered.Source!;
        var first = Candidate("NBA 2K27-2026_09_01-14-00-00.mp4", 0x61);
        var later = Candidate("NBA 2K27-2026_09_01-14-01-00.mp4", 0x62);
        var journal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(routingRoot, source.SourceId),
            source.SourceId,
            source.RootIdentitySha256,
            () => Now);
        var firstEntry = await SelectAsync(journal, source.SourceId, first);
        var laterEntry = await SelectAsync(journal, source.SourceId, later);
        _ = await journal.BeginImportAsync(
            firstEntry.OccurrenceId, firstEntry.RevisionId);
        _ = await journal.MarkNeedsAttentionAsync(
            firstEntry.OccurrenceId, firstEntry.RevisionId, "library-conflict");
        var attention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            source.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(1));

        var retried = await service.RetryBlockedOccurrencesAsync(
            source.SourceId, attention.Source!.Revision);
        var afterRetry = journal.Load().Document!.Occurrences;
        Assert(retried is
               {
                   Status: XboxDvrBlockedOccurrenceRetryStatus.Retried,
                   RetriedCount: 1,
                   Source.Health: RoutingInputSourceHealth.Ready,
                   Source.AttentionReason: RoutingInputSourceAttentionReason.None
               } &&
               afterRetry.Single(item =>
                   item.OccurrenceId == firstEntry.OccurrenceId) is
               {
                   State: XboxDvrOccurrenceState.Importing,
                   ImportAttempts: 1,
                   ErrorCode: null
               } &&
               afterRetry.Single(item =>
                   item.OccurrenceId == laterEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.Selected &&
               File.ReadAllBytes(sourceSentinel).SequenceEqual(
                   new byte[] { 0x31, 0x41, 0x59 }),
            "Explicit retry must preserve import history, resume the blocker before later selected clips, restore source readiness, and never touch the Xbox original.");

        // Simulate the journal transition committing while the catalog readiness write or its
        // response is lost. A fresh service must recognize Importing as durable retry evidence
        // and finish only the catalog side of the two-document operation.
        _ = await journal.MarkNeedsAttentionAsync(
            firstEntry.OccurrenceId, firstEntry.RevisionId, "library-conflict");
        var pendingAttention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            retried.Source!.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(2));
        var durablePending = await journal.RetryBlockedAsync(
            firstEntry.OccurrenceId, firstEntry.RevisionId);
        var pendingGeneration = journal.Load().Document!.Generation;
        var converged = await new XboxDvrInputSourceRecoveryService(
                catalog, routingRoot, inspector, () => Now.AddMinutes(3))
            .RetryBlockedOccurrencesAsync(
                source.SourceId, pendingAttention.Source!.Revision);
        Assert(converged is
               {
                   Status: XboxDvrBlockedOccurrenceRetryStatus.RecoveryCompleted,
                   RetriedCount: 0,
                   Source.Health: RoutingInputSourceHealth.Ready
               } &&
               durablePending.State == XboxDvrOccurrenceState.Importing &&
               durablePending.ImportAttempts == 1 &&
               journal.Load().Document!.Generation == pendingGeneration,
            "A restart after retry evidence committed must restore source readiness without rewriting the occurrence or inflating attempts.");

        _ = await journal.MarkNeedsAttentionAsync(
            firstEntry.OccurrenceId, firstEntry.RevisionId, "invalid-media");
        _ = await journal.MarkNeedsAttentionAsync(
            laterEntry.OccurrenceId, laterEntry.RevisionId, "source-replaced");
        var mixedAttention = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            converged.Source!.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(4));
        var beforeUnsupported = journal.Load().Document!;
        var unsupported = await service.RetryBlockedOccurrencesAsync(
            source.SourceId, mixedAttention.Source!.Revision);
        var afterUnsupported = journal.Load().Document!;
        Assert(unsupported.Status ==
               XboxDvrBlockedOccurrenceRetryStatus.UnsupportedBlocker &&
               unsupported.RetriedCount == 0 &&
               afterUnsupported.Generation == beforeUnsupported.Generation &&
               afterUnsupported.Occurrences.SequenceEqual(
                   beforeUnsupported.Occurrences) &&
               afterUnsupported.Occurrences.Single(item =>
                   item.OccurrenceId == firstEntry.OccurrenceId).State ==
               XboxDvrOccurrenceState.NeedsAttention,
            "A mixed set containing an unsupported blocker must be rejected before any retry transition is written.");

        var stale = await service.RetryBlockedOccurrencesAsync(
            source.SourceId, mixedAttention.Source.Revision - 1);
        var afterStale = journal.Load().Document!;
        Assert(stale.Status == XboxDvrBlockedOccurrenceRetryStatus.Conflict &&
               afterStale.Generation == afterUnsupported.Generation &&
               afterStale.Occurrences.SequenceEqual(afterUnsupported.Occurrences),
            "A stale source revision must fail before changing any durable occurrence evidence.");

        var foreignRoot = Path.Combine(root, "foreign-authority");
        var foreignRouting = Path.Combine(foreignRoot, "routing");
        var foreignSourceRoot = Path.Combine(foreignRoot, "Xbox Game DVR");
        Directory.CreateDirectory(foreignSourceRoot);
        var foreignCatalog = Catalog(
            foreignRouting,
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            new Guid("61616161-7272-8383-9494-a5a5a5a5a5a5"));
        var foreignService = new XboxDvrInputSourceRecoveryService(
            foreignCatalog, foreignRouting, inspector, () => Now);
        var foreignRegistered = await foreignService.RegisterAsync(
            "Foreign Xbox", foreignSourceRoot, TimeZoneInfo.Utc.Id, enabled: true);
        var foreignSource = foreignRegistered.Source!;
        var foreignAttention = await foreignCatalog.SetNeedsAttentionAsync(
            foreignSource.SourceId,
            foreignSource.Revision,
            RoutingInputSourceAttentionReason.BlockedOccurrence,
            Now.AddMinutes(1));
        _ = await new XboxDvrOccurrenceStore(foreignRouting, foreignSource.SourceId)
            .LoadOrCreateAsync(
                foreignSource.SourceId, ReplacementIdentity, Now);
        var foreign = await foreignService.RetryBlockedOccurrencesAsync(
            foreignSource.SourceId, foreignAttention.Source!.Revision);
        Assert(foreign.Status == XboxDvrBlockedOccurrenceRetryStatus.StateUnavailable &&
               foreignCatalog.Inspect().Sources.Single().Health ==
               RoutingInputSourceHealth.NeedsAttention &&
               File.ReadAllBytes(sourceSentinel).SequenceEqual(
                   new byte[] { 0x31, 0x41, 0x59 }),
            "Retry must fail closed when the occurrence journal belongs to another native source authority, without touching any Xbox file.");
    }

    private static async Task<XboxDvrOccurrenceEntry> SelectAsync(
        XboxDvrOccurrenceJournal journal,
        string sourceId,
        XboxDvrCandidateMetadata candidate)
    {
        var parsed = XboxDvrFileNameParser.Parse(candidate.PortableRelativePath);
        var occurrenceId = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            sourceId, candidate.PortableRelativePath, candidate);
        var revisionId = XboxDvrSourceAdapter.CreateRevisionIdentity(
            occurrenceId, parsed.CapturedUtc!.Value, candidate);
        return await journal.SelectAsync(
            occurrenceId,
            revisionId,
            candidate.PortableRelativePath,
            parsed.GameName!,
            parsed.CapturedUtc.Value,
            candidate.LogicalBytes,
            candidate.LastWriteUtcTicks);
    }

    private static async Task AssertSelectionAndRemovalAreLinearAsync(string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        Directory.CreateDirectory(sourceRoot);
        var routes = new RoutingSnapshotStore(
            Path.Combine(routingRoot, RoutingSnapshotStore.FileName));
        var catalog = Catalog(
            routingRoot,
            new RoutingInputSourceReferenceProbe(routes, routingRoot),
            new Guid("abababab-cdcd-efef-1212-343434343434"));
        var service = new XboxDvrInputSourceRecoveryService(
            catalog,
            routingRoot,
            new TestIdentityInspector((_, _) =>
                new XboxDvrRuntimeMetadataSnapshot(RootIdentity, [])),
            () => Now);
        var registered = await service.RegisterAsync(
            "Xbox captures", sourceRoot, TimeZoneInfo.Utc.Id, enabled: false);
        var source = registered.Source ??
            throw new InvalidOperationException("The test Xbox source was not registered.");
        var journal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(routingRoot, source.SourceId),
            source.SourceId,
            RootIdentity,
            () => Now);
        var candidate = Candidate(
            "Battlefield 6-2026_09_01-12-30-00.mp4", 0x61);
        var emptyRoutes = await routes.LoadOrCreateAsync(Now);
        var boundRoute = XboxRoute(source.SourceId);
        _ = await routes.SaveAsync(
            RoutingSnapshotModel.ReplaceRoutes(
                emptyRoutes, [boundRoute], Now.AddMilliseconds(1)),
            emptyRoutes.Generation);

        var selectionGate = await RoutingInputSourceExecutionGate.EnterAsync(
            catalog.StorePath);
        var removalTask = catalog.RemoveAsync(
            source.SourceId, source.Revision, Now.AddSeconds(1));
        await Task.Delay(50);
        Assert(!removalTask.IsCompleted,
            "Source removal must wait while the metadata-only selection transaction owns the authority gate.");
        var selected = await SelectAsync(journal, source.SourceId, candidate);
        selectionGate.Dispose();
        var blockedRemoval = await removalTask;
        Assert(blockedRemoval.Status == RoutingInputSourceMutationStatus.InUse &&
               catalog.Inspect().Sources.Any(item => item.SourceId == source.SourceId),
            "If selection wins, Remove must observe the nonterminal journal and preserve its source authority.");

        var disabled = await catalog.SetEnabledAsync(
            source.SourceId, source.Revision, enabled: false, Now.AddMilliseconds(2));
        var manager = new RoutingRouteManager(
            routes,
            () => Now.AddMilliseconds(3),
            TestRouteMutationAuthority.Allowed,
            inputSourceMembership: catalog);
        await manager.DeleteAsync(boundRoute.RouteId);
        var removed = await catalog.RemoveAsync(
            source.SourceId, disabled.Source!.Revision, Now.AddSeconds(2));
        var dismissed = journal.Load().Document!.Occurrences.Single(item =>
            item.OccurrenceId == selected.OccurrenceId);
        Assert(dismissed.State == XboxDvrOccurrenceState.RouteRevoked &&
               dismissed.ErrorCode == "route-revoked" &&
               removed.Status == RoutingInputSourceMutationStatus.Removed,
            "After Disable and sole-route Delete, Remove must terminalize selected work and converge without touching the Xbox original.");
    }

    private static async Task AssertImportingWorkBlocksDisableAndReplacementAsync(
        string root)
    {
        var routingRoot = Path.Combine(root, "routing");
        var sourceRoot = Path.GetFullPath(Path.Combine(root, "Xbox Game DVR"));
        var libraryRoot = Path.GetFullPath(Path.Combine(root, "library"));
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(libraryRoot);
        var sourceIds = new Queue<Guid>(
        [
            new Guid("edededed-abab-cdcd-efef-121212121212"),
            new Guid("fefefefe-bcbc-dede-fafa-343434343434")
        ]);
        var catalog = Catalog(
            routingRoot,
            new FixedReferenceProbe(RoutingInputSourceReferenceStatus.NotReferenced),
            () => sourceIds.Dequeue());
        var added = await catalog.AddVerifiedAsync(
            "Xbox captures",
            sourceRoot,
            RootIdentity,
            TimeZoneInfo.Utc.Id,
            enabled: true,
            Now);
        var source = added.Source ??
            throw new InvalidOperationException("The test Xbox source was not registered.");
        var journal = new XboxDvrOccurrenceJournal(
            new XboxDvrOccurrenceStore(routingRoot, source.SourceId),
            source.SourceId,
            RootIdentity,
            () => Now);
        var selected = await SelectAsync(
            journal,
            source.SourceId,
            Candidate("Battlefield 6-2026_09_01-12-45-00.mp4", 0x62));
        _ = await journal.BeginImportAsync(
            selected.OccurrenceId, selected.RevisionId);
        var stagedPath = XboxDvrRuntimeLayout.GetStagedPath(
            libraryRoot, source.SourceId, selected.OccurrenceId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        await File.WriteAllBytesAsync(stagedPath, [0x41, 0x42, 0x43]);

        var disable = await catalog.SetEnabledAsync(
            source.SourceId, source.Revision, enabled: false, Now.AddSeconds(1));
        Assert(disable.Status == RoutingInputSourceMutationStatus.InUse &&
               catalog.Inspect().Sources.Single().Enabled,
            "Disable must wait while an Importing occurrence still requires promotion recovery.");

        var rootChanged = await catalog.SetNeedsAttentionAsync(
            source.SourceId,
            source.Revision,
            RoutingInputSourceAttentionReason.RootAuthorityChanged,
            Now.AddMilliseconds(1500));
        var recovery = new XboxDvrInputSourceRecoveryService(
            catalog,
            routingRoot,
            new TestIdentityInspector((_, _) =>
                new XboxDvrRuntimeMetadataSnapshot(ReplacementIdentity, [])),
            () => Now.AddSeconds(2),
            libraryRoot);
        var importBoundary = await RoutingInputSourceExecutionGate.EnterAsync(
            catalog.StorePath);
        var recheckTask = recovery.RecheckAsync(
            source.SourceId, rootChanged.Source!.Revision);
        await Task.Delay(50);
        Assert(!recheckTask.IsCompleted &&
               journal.Load().Document!.Occurrences.Single().State ==
               XboxDvrOccurrenceState.Importing &&
               File.Exists(stagedPath),
            "Source-independent recovery must wait for the active content boundary before changing journal or owned staging state.");
        importBoundary.Dispose();
        var rechecked = await recheckTask;
        var repairedEntry = journal.Load().Document!.Occurrences.Single();
        Assert(rechecked.Status == XboxDvrSourceRecoveryStatus.ReplacementRequired &&
               repairedEntry.State == XboxDvrOccurrenceState.Selected &&
               !File.Exists(stagedPath),
            $"Root replacement recovery must discard only unauthenticated owned staging and reset Importing without opening the old source. Status={rechecked.Status}, state={repairedEntry.State}, stageExists={File.Exists(stagedPath)}.");

        var replacement = await recovery.RegisterReplacementAsync(
            source.SourceId,
            rechecked.Source!.Revision,
            enabled: false);
        Assert(replacement.Status == XboxDvrSourceRegistrationStatus.Registered &&
               catalog.Inspect().Sources.Count == 2 &&
               catalog.Inspect().Sources.Single(item =>
                   item.SourceId == source.SourceId).Retired,
            "After source-independent Importing reconciliation, replacement must preserve the old authority as a retired tombstone and allocate a new SourceId.");
    }

    private static RoutingRoute XboxRoute(string sourceId) => new(
        Guid.NewGuid(),
        "Xbox route",
        Enabled: false,
        Priority: 0,
        Revision: 1,
        RoutingRouteSource.User,
        RoutingRouteKind.Specific,
        RoutingTriggerKind.WatchedFolder,
        new RoutingPrepareSettings(false, false, RoutingMissingOutputBehavior.UseOriginal),
        [
            new RoutingCondition(
                Guid.NewGuid(), RoutingConditionField.SourceConnection,
                RoutingConditionOperator.Equals, sourceId),
            new RoutingCondition(
                Guid.NewGuid(), RoutingConditionField.CapturedAt,
                RoutingConditionOperator.GreaterThanOrEqual,
                Now.ToString("O", CultureInfo.InvariantCulture))
        ],
        [
            new RoutingAction(
                Guid.NewGuid(), true, RoutingActionKind.FileIntoLibrary,
                null, null, null, null, RoutingDeliveryMode.Automatic,
                RoutingLibraryArea.LocalOnly, null)
        ],
        Now,
        Now,
        new RoutingXboxHistorySelection(Now, []));

    private static XboxDvrCandidateMetadata Candidate(string leaf, byte discriminator)
    {
        var fileId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(leaf)))
            .ToLowerInvariant()[..32];
        return new XboxDvrCandidateMetadata(
            leaf,
            LogicalBytes: 32,
            LastWriteUtcTicks: Now.UtcTicks + discriminator,
            VolumeSerialNumber: 0x1234,
            FileId128Hex: fileId,
            ProviderIdentitySha256: null,
            FileAttributes: 0x20,
            ReparseTag: 0);
    }

    private static RoutingInputSourceCatalog Catalog(
        string routingRoot,
        IRoutingInputSourceReferenceProbe references,
        Guid id) => Catalog(routingRoot, references, () => id);

    private static RoutingInputSourceCatalog Catalog(
        string routingRoot,
        IRoutingInputSourceReferenceProbe references,
        Func<Guid> idFactory) => new(
        new RoutingInputSourceCatalogStore(
            Path.Combine(routingRoot, RoutingInputSourceCatalogStore.FileName)),
        references,
        idFactory,
        () => Now);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixedReferenceProbe : IRoutingInputSourceReferenceProbe
    {
        private readonly RoutingInputSourceReferenceStatus _status;

        internal FixedReferenceProbe(RoutingInputSourceReferenceStatus status) =>
            _status = status;

        public RoutingInputSourceReferenceStatus Inspect(
            string sourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoutingInputSourceCatalogModel.ValidateSourceId(sourceId);
            return _status;
        }
    }

    private sealed class TestIdentityInspector : IXboxDvrSourceIdentityInspector
    {
        internal TestIdentityInspector(
            Func<string, CancellationToken, XboxDvrRuntimeMetadataSnapshot> snapshotFactory) =>
            SnapshotFactory = snapshotFactory;

        internal Func<string, CancellationToken, XboxDvrRuntimeMetadataSnapshot>
            SnapshotFactory { get; set; }
        internal Action? BeforeInspect { get; set; }
        internal int Inspections { get; private set; }

        public XboxDvrRuntimeMetadataSnapshot Inspect(
            string canonicalRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inspections++;
            BeforeInspect?.Invoke();
            return SnapshotFactory(canonicalRoot, cancellationToken);
        }
    }
}
