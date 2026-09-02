using ClipsToDiscord;

internal static class RoutingArchiveTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 22, 0, 0, TimeSpan.Zero);

    internal static void Run(string testRoot) => RunAsync(testRoot).GetAwaiter().GetResult();

    private static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertArchivePrecedesHotRemovalAsync(Path.Combine(testRoot, "archive-first"));
        await AssertFullTerminalEvidenceAndEligibilityAsync(Path.Combine(testRoot, "evidence"));
        await AssertActionableCapacityIsTypedAsync(Path.Combine(testRoot, "capacity"));
    }

    private static async Task AssertArchivePrecedesHotRemovalAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var empty = await store.LoadOrCreateAsync(Now);
        var planId = Guid.NewGuid();
        const string clipId = "clip-archive-first";
        var proposal = new RoutingPlanProposal(
            planId, clipId, 1, [], [], null, [], [], RequiresAtomicResolvedAppend: false);
        var hot = RoutingOutboxModel.AppendEvaluatedPlan(empty, proposal, Now.AddSeconds(1));
        hot = await store.SaveAsync(hot, empty.Generation);
        var archive = RoutingArchiveModel.Create(hot, planId);

        await store.ArchiveStore.PersistExactAsync(archive);
        var idempotent = await store.ArchiveStore.PersistExactAsync(archive);
        Assert(idempotent == archive,
            "Persisting the same immutable source tombstone twice must be idempotent.");
        using (store.ArchiveStore.OpenValidatedLease(archive))
        {
            var archivePath = store.ArchiveStore.PathForSource(clipId);
            AssertThrows<IOException>(() => File.WriteAllText(archivePath, "replacement"),
                "A validated archive lease must deny writes through the hot-outbox replacement.");
            AssertThrows<IOException>(() => File.Delete(archivePath),
                "A validated archive lease must deny deletion through the hot-outbox replacement.");
            store.ArchiveStore.RequireExact(archive);
        }
        var mismatched = archive with
        {
            Plan = archive.Plan with { PlanId = Guid.NewGuid() }
        };
        await AssertThrowsAsync<InvalidDataException>(
            () => store.ArchiveStore.PersistExactAsync(mismatched),
            "A different plan for the same source-key path must fail closed.");
        Assert(store.Load().Document!.Plans.Single().PlanId == planId,
            "Committing the immutable archive first must leave the hot plan intact until its own CAS.");
        Assert(store.ArchiveStore.LoadBySource(clipId).Document == archive,
            "The archive must retain the exact no-op routing decision as a permanent tombstone.");

        var compacted = RoutingArchiveModel.RemoveArchivedPlans(hot, [archive], Now.AddSeconds(-1));
        Assert(compacted.UpdatedUtc == hot.UpdatedUtc,
            "Compaction must clamp a rolled-back clock instead of moving outbox time backwards.");
        await AssertThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(compacted, hot.Generation),
            "Ordinary outbox persistence must remain append-only and reject plan removal.");
        var newerPlanId = Guid.NewGuid();
        const string newerClipId = "clip-newer-actionable";
        var newerRoute = new RoutingRouteSnapshotReference(
            1, Guid.NewGuid(), 1, "Newer actionable work", Guid.NewGuid(), 0, 0);
        var newerOutput = new RoutingOutputReference(
            newerClipId, RoutingOutputKind.Original, new string('c', 64));
        var newerDelivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), newerPlanId, newerClipId, newerRoute,
            RoutingDestinationKind.Discord, "discord.newer", newerOutput, newerOutput,
            RoutingMissingOutputBehavior.NeedsAttention, RoutingDeliveryMode.Automatic,
            new RoutingDeliverySettings(null, null, null, RoutingVisibility.Unspecified, false),
            true, null, Now.AddSeconds(2));
        var newer = RoutingOutboxModel.AppendPlan(
            hot, newerPlanId, [newerDelivery], [], Now.AddSeconds(2));
        await store.SaveAsync(newer, hot.Generation);
        await AssertThrowsAsync<RoutingConcurrencyException>(
            () => store.SaveCompactedAsync(compacted, hot.Generation, [archive]),
            "A stale compaction CAS must lose after unrelated newer outbox work is appended.");

        var saved = await store.CompactTerminalPlansAsync(Now.AddSeconds(3));
        Assert(saved.ArchivedPlanCount == 1 && saved.Document.Plans.Count == 1 &&
               saved.Document.Plans.Single().PlanId == newerPlanId &&
               saved.Document.Deliveries.Single().DeliveryId == newerDelivery.DeliveryId,
            "Retrying compaction must remove only the archived terminal plan and preserve newer work.");

        var replay = await store.CompactTerminalPlansAsync(Now.AddSeconds(4));
        Assert(replay.ArchivedPlanCount == 0 && replay.Document.Plans.Count == 1 &&
               store.ArchiveStore.LoadBySource(clipId).Document == archive,
            "Restart compaction must be idempotent after the archive-only state is reached.");
    }

    private static async Task AssertFullTerminalEvidenceAndEligibilityAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var current = await store.LoadOrCreateAsync(Now);
        var planId = Guid.NewGuid();
        const string clipId = "clip-terminal-evidence";
        var route = new RoutingRouteSnapshotReference(
            1, Guid.NewGuid(), 1, "Archive evidence", Guid.NewGuid(), 0, 0);
        var output = new RoutingOutputReference(
            clipId, RoutingOutputKind.Original, new string('a', 64));
        var delivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, clipId, route, RoutingDestinationKind.Discord,
            "discord.archive", output, output, RoutingMissingOutputBehavior.NeedsAttention,
            RoutingDeliveryMode.Automatic,
            new RoutingDeliverySettings(null, null, null, RoutingVisibility.Unspecified, false),
            artifactReady: true, intentionalDuplicate: null, Now);
        var disposition = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), planId, clipId, new string('a', 64), route with
            {
                ActionId = Guid.NewGuid(),
                Order = 1
            }, RoutingLibraryArea.Uploaded, [delivery.DeliveryId], Now);
        current = RoutingOutboxModel.AppendPlan(
            current, planId, [delivery], [disposition], Now.AddSeconds(1));
        Assert(!RoutingArchiveModel.CanArchive(current, planId),
            "Ready deliveries and dependency-waiting file work must remain actionable in the hot outbox.");

        var deliveryAttempt = Guid.NewGuid();
        current = RoutingOutboxModel.StartDelivery(
            current, delivery.DeliveryId, deliveryAttempt, Now.AddSeconds(2));
        current = RoutingOutboxModel.CompleteDelivery(
            current, delivery.DeliveryId, deliveryAttempt, "discord:archive-message",
            Now.AddSeconds(3));
        current = RoutingOutboxModel.RefreshFileDisposition(
            current, disposition.DispositionId, Now.AddSeconds(4));
        var fileAttempt = Guid.NewGuid();
        current = RoutingOutboxModel.StartFileDisposition(
            current, disposition.DispositionId, fileAttempt, Now.AddSeconds(5));
        current = RoutingOutboxModel.CompleteFileDisposition(
            current, disposition.DispositionId, fileAttempt, "capture:archive:original",
            Now.AddSeconds(6));
        Assert(RoutingArchiveModel.CanArchive(current, planId),
            "A fully delivered and filed plan must become archive eligible.");

        var archive = RoutingArchiveModel.Create(current, planId);
        await store.ArchiveStore.PersistExactAsync(archive);
        var roundTrip = store.ArchiveStore.LoadBySource(clipId);
        Assert(roundTrip.LoadedFromDisk && roundTrip.Document == archive &&
               roundTrip.Document!.Deliveries.Single().RemoteReceiptReference ==
               "discord:archive-message" &&
               roundTrip.Document.FileDispositions.Single().FinalLibraryItemReference ==
               "capture:archive:original" &&
               roundTrip.Document.Deliveries.Single().Attempts == 1,
            "The full immutable archive must preserve receipts, filing identity, attempts, and history.");

        var failedPlanId = Guid.NewGuid();
        const string failedClipId = "clip-actionable-failure";
        var failedOutput = output with { ClipId = failedClipId };
        var failedDelivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), failedPlanId, failedClipId, route with
            {
                RouteId = Guid.NewGuid(), ActionId = Guid.NewGuid()
            }, RoutingDestinationKind.Discord, "discord.archive", failedOutput, failedOutput,
            RoutingMissingOutputBehavior.NeedsAttention, RoutingDeliveryMode.Automatic,
            new RoutingDeliverySettings(null, null, null, RoutingVisibility.Unspecified, false),
            true, null, Now.AddSeconds(7));
        var failedDocument = RoutingOutboxModel.AppendPlan(
            RoutingOutboxModel.CreateEmpty(Now.AddSeconds(7)), failedPlanId,
            [failedDelivery], [], Now.AddSeconds(8));
        var failedAttempt = Guid.NewGuid();
        failedDocument = RoutingOutboxModel.StartDelivery(
            failedDocument, failedDelivery.DeliveryId, failedAttempt, Now.AddSeconds(9));
        failedDocument = RoutingOutboxModel.FailDelivery(
            failedDocument, failedDelivery.DeliveryId, failedAttempt,
            "discord-rejected", null, Now.AddSeconds(10));
        Assert(!RoutingArchiveModel.CanArchive(failedDocument, failedPlanId),
            "A retryable failed delivery must never be archived as final history.");
        AssertThrows<InvalidDataException>(() => RoutingArchiveModel.Create(failedDocument, failedPlanId),
            "Archive creation must fail closed for actionable or ambiguous state.");

        var unknownDocument = RoutingOutboxModel.RetryFailedDelivery(
            failedDocument, failedDelivery.DeliveryId, Now.AddSeconds(11));
        var unknownAttempt = Guid.NewGuid();
        unknownDocument = RoutingOutboxModel.StartDelivery(
            unknownDocument, failedDelivery.DeliveryId, unknownAttempt, Now.AddSeconds(12));
        unknownDocument = RoutingOutboxModel.MarkDeliveryUnknown(
            unknownDocument, failedDelivery.DeliveryId, unknownAttempt,
            "discord-result-unknown", Now.AddSeconds(13));
        Assert(!RoutingArchiveModel.CanArchive(unknownDocument, failedPlanId),
            "An ambiguous delivery must remain hot until the user resolves duplicate risk.");

        var recoveryPlanId = Guid.NewGuid();
        const string recoveryClipId = "clip-recovery-pending";
        var recoveryDisposition = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), recoveryPlanId, recoveryClipId, new string('d', 64),
            route with { RouteId = Guid.NewGuid(), ActionId = Guid.NewGuid() },
            RoutingLibraryArea.LocalOnly, [], Now.AddSeconds(14));
        var recoveryDocument = RoutingOutboxModel.AppendPlan(
            RoutingOutboxModel.CreateEmpty(Now.AddSeconds(14)), recoveryPlanId,
            [], [recoveryDisposition], Now.AddSeconds(15));
        var recoveryAttempt = Guid.NewGuid();
        recoveryDocument = RoutingOutboxModel.StartFileDisposition(
            recoveryDocument, recoveryDisposition.DispositionId, recoveryAttempt,
            Now.AddSeconds(16));
        recoveryDocument = RoutingOutboxModel.MarkFileDispositionRecoveryPending(
            recoveryDocument, recoveryDisposition.DispositionId, recoveryAttempt,
            "library-result-unknown", Now.AddSeconds(17));
        Assert(!RoutingArchiveModel.CanArchive(recoveryDocument, recoveryPlanId),
            "An ambiguous file operation must remain hot until exact identity reconciliation completes.");
    }

    private static async Task AssertActionableCapacityIsTypedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(
            Path.Combine(root, RoutingOutboxStore.FileName), planningAdmissionBytes: 1024);
        var empty = await store.LoadOrCreateAsync(Now);
        var planId = Guid.NewGuid();
        const string clipId = "clip-actionable-capacity";
        var route = new RoutingRouteSnapshotReference(
            1, Guid.NewGuid(), 1, "Capacity attention", Guid.NewGuid(), 0, 0);
        var output = new RoutingOutputReference(
            clipId, RoutingOutputKind.Original, new string('b', 64));
        var delivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, clipId, route, RoutingDestinationKind.Discord,
            "discord.capacity", output, output, RoutingMissingOutputBehavior.NeedsAttention,
            RoutingDeliveryMode.Automatic,
            new RoutingDeliverySettings(null, null, null, RoutingVisibility.Unspecified, false),
            true, null, Now);
        var actionable = RoutingOutboxModel.AppendPlan(
            empty, planId, [delivery], [], Now.AddSeconds(1));
        await store.SaveAsync(actionable, empty.Generation);

        var admission = await store.PrepareForPlanningAsync(Now.AddSeconds(2));
        Assert(!admission.CanAcceptNewPlan && admission.ArchivedPlanCount == 0 &&
               admission.Document.Deliveries.Single().State == PlannedDeliveryState.Ready &&
               store.ArchiveStore.LoadBySource(clipId).Status == RoutingDocumentLoadStatus.Missing,
            "An actionable backlog at the admission limit must return typed capacity attention " +
            "without pruning, archiving, or corrupting retryable work.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }
}
