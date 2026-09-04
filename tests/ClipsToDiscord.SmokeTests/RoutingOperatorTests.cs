using ClipsToDiscord;

internal static class RoutingOperatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 15, 0, 0, TimeSpan.Zero);

    internal static void Run(string testRoot) => RunAsync(testRoot).GetAwaiter().GetResult();

    private static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertDeliveryControlsAreDurableAndIdempotentAsync(
            Path.Combine(testRoot, "delivery-controls"));
        await AssertAttentionAndDispositionControlsAsync(
            Path.Combine(testRoot, "attention-and-filing"));
        await AssertUnknownRequiresAnExplicitDecisionAsync(
            Path.Combine(testRoot, "unknown-decision"));
        await AssertConcurrentCommandsConvergeAsync(
            Path.Combine(testRoot, "concurrent-cas"));
        await AssertHistoryMergesAndConflictsFailClosedAsync(
            Path.Combine(testRoot, "history"));
        await AssertArchivedWorkIsNotActionableAsync(
            Path.Combine(testRoot, "archived"));
        await AssertCancellationPreventsMutationAsync(
            Path.Combine(testRoot, "cancellation"));
    }

    private static async Task AssertDeliveryControlsAreDurableAndIdempotentAsync(string root)
    {
        var store = CreateStore(root);
        var approval = Delivery(
            Guid.NewGuid(), "clip-approval", RoutingDeliveryMode.Approval,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var current = await AppendAsync(store, approval, Now);
        var generationBefore = current.Generation;
        var control = new RoutingOperatorControl(store, () => Now.AddMinutes(1));

        var result = await control.ApproveDeliveryAsync(approval.DeliveryId);
        Assert(result.Outcome == RoutingOperatorOutcome.Applied &&
               result.TargetKind == RoutingOperatorTargetKind.Delivery &&
               result.TargetId == approval.DeliveryId &&
               result.OutboxGeneration == generationBefore + 1,
            "Approving a waiting delivery must report the exact durable outbox generation.");
        var approved = store.Load().Document!.Deliveries.Single();
        Assert(approved.DeliveryId == approval.DeliveryId &&
               approved.PlanId == approval.PlanId &&
               approved.State == PlannedDeliveryState.Ready &&
               approved.ApprovedUtc == Now.AddMinutes(1) &&
               approved.Attempts == 0,
            "Approval must preserve plan identity and must not cross the provider boundary.");

        var duplicate = await control.ApproveDeliveryAsync(approval.DeliveryId);
        Assert(duplicate.Outcome == RoutingOperatorOutcome.AlreadyApplied &&
               store.Load().Document!.Generation == result.OutboxGeneration,
            "Repeating an approval must be an idempotent no-op.");
        var invalid = await control.RetryFailedDeliveryAsync(approval.DeliveryId);
        Assert(invalid.Outcome == RoutingOperatorOutcome.InvalidState,
            "A retry command must reject a delivery that is not failed.");
        var missing = await control.ApproveDeliveryAsync(Guid.NewGuid());
        Assert(missing.Outcome == RoutingOperatorOutcome.NotFound,
            "An unknown operator target must return a typed not-found result.");
    }

    private static async Task AssertAttentionAndDispositionControlsAsync(string root)
    {
        var store = CreateStore(root);
        var attention = Delivery(
            Guid.NewGuid(), "clip-needs-attention", RoutingDeliveryMode.Automatic,
            artifactReady: false, RoutingMissingOutputBehavior.NeedsAttention,
            requestedKind: RoutingOutputKind.Portrait);
        var current = await AppendAsync(store, attention, Now);
        var needsAttention = RoutingOutboxModel.ResolveMissingArtifacts(
            current, attention.PlanId, [attention.RequestedOutput],
            "portrait-render-failed", Now.AddSeconds(1));
        await store.SaveAsync(needsAttention, current.Generation);
        var control = new RoutingOperatorControl(store, () => Now.AddMinutes(1));

        var useOriginal = await control.ResolveNeedsAttentionAsync(
            attention.DeliveryId, RoutingNeedsAttentionResolution.UseOriginal);
        var recovered = store.Load().Document!.Deliveries.Single();
        Assert(useOriginal.Outcome == RoutingOperatorOutcome.Applied &&
               recovered.State == PlannedDeliveryState.Ready &&
               recovered.Output == recovered.OriginalOutput &&
               recovered.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
               recovered.ArtifactErrorCode == "portrait-render-failed",
            "Use original must retain failure evidence while making only the original ready.");
        var repeated = await control.ResolveNeedsAttentionAsync(
            attention.DeliveryId, RoutingNeedsAttentionResolution.UseOriginal);
        Assert(repeated.Outcome == RoutingOperatorOutcome.AlreadyApplied,
            "Repeating the same needs-attention resolution must not rewrite the outbox.");

        var filingStore = CreateStore(Path.Combine(root, "filing"));
        var disposition = Disposition(Guid.NewGuid(), "clip-file-failed");
        var filing = await AppendAsync(filingStore, disposition, Now);
        var attemptId = Guid.NewGuid();
        var moving = RoutingOutboxModel.StartFileDisposition(
            filing, disposition.DispositionId, attemptId, Now.AddSeconds(1));
        filing = await filingStore.SaveAsync(moving, filing.Generation);
        var failed = RoutingOutboxModel.FailFileDisposition(
            filing, disposition.DispositionId, attemptId,
            "library-move-failed", Now.AddSeconds(2));
        await filingStore.SaveAsync(failed, filing.Generation);
        var filingControl = new RoutingOperatorControl(
            filingStore, () => Now.AddMinutes(1));
        var retry = await filingControl.RetryFailedFileDispositionAsync(
            disposition.DispositionId);
        var retried = filingStore.Load().Document!.FileDispositions.Single();
        Assert(retry.Outcome == RoutingOperatorOutcome.Applied &&
               retried.DispositionId == disposition.DispositionId &&
               retried.State == PlannedFileDispositionState.Ready &&
               retried.Attempts == 1 && retried.CurrentAttemptId is null &&
               retried.AttemptStartedUtc is null && retried.ErrorCode is null,
            "Retrying a failed filing action must preserve identity and clear its attempt fence.");
        Assert((await filingControl.RetryFailedFileDispositionAsync(
                   disposition.DispositionId)).Outcome ==
               RoutingOperatorOutcome.AlreadyApplied,
            "Repeating a filing retry must be an idempotent no-op.");
    }

    private static async Task AssertUnknownRequiresAnExplicitDecisionAsync(string root)
    {
        var deliveredStore = CreateStore(Path.Combine(root, "delivered"));
        var unknown = Delivery(
            Guid.NewGuid(), "clip-unknown-delivered", RoutingDeliveryMode.Automatic,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var current = await AppendAsync(deliveredStore, unknown, Now);
        await PersistUnknownAsync(
            deliveredStore, current, unknown.DeliveryId, Now.AddSeconds(1));
        var control = new RoutingOperatorControl(deliveredStore, () => Now.AddMinutes(1));
        var resolved = await control.ResolveUnknownAsDeliveredAsync(
            unknown.DeliveryId, "discord:confirmed-message");
        var delivered = deliveredStore.Load().Document!.Deliveries.Single();
        Assert(resolved.Outcome == RoutingOperatorOutcome.Applied &&
               delivered.State == PlannedDeliveryState.Delivered &&
               delivered.RemoteReceiptReference == "discord:confirmed-message" &&
               delivered.DeliveryId == unknown.DeliveryId && delivered.Attempts == 1,
            "Marking unknown as delivered must keep its stable provider idempotency identity.");
        Assert((await control.ResolveUnknownAsDeliveredAsync(
                   unknown.DeliveryId, "discord:confirmed-message")).Outcome ==
               RoutingOperatorOutcome.AlreadyApplied,
            "Repeating the same unknown reconciliation must not rewrite completion evidence.");
        Assert((await control.ResolveUnknownAsDeliveredAsync(
                   unknown.DeliveryId, "discord:different-message")).Outcome ==
               RoutingOperatorOutcome.InvalidState,
            "A conflicting receipt must not be accepted as an idempotent reconciliation.");

        var resendStore = CreateStore(Path.Combine(root, "resend"));
        var resend = Delivery(
            Guid.NewGuid(), "clip-unknown-resend", RoutingDeliveryMode.Automatic,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var resendCurrent = await AppendAsync(resendStore, resend, Now);
        await PersistUnknownAsync(
            resendStore, resendCurrent, resend.DeliveryId, Now.AddSeconds(1));
        var resendControl = new RoutingOperatorControl(
            resendStore, () => Now.AddMinutes(2));
        Assert((await resendControl.RetryFailedDeliveryAsync(resend.DeliveryId)).Outcome ==
               RoutingOperatorOutcome.InvalidState,
            "An unknown result must never enter the ordinary retry path.");
        var authorized = await resendControl.AuthorizeUnknownSendAgainAsync(resend.DeliveryId);
        var ready = resendStore.Load().Document!.Deliveries.Single();
        Assert(authorized.Outcome == RoutingOperatorOutcome.Applied &&
               ready.State == PlannedDeliveryState.Ready &&
               ready.DuplicateRiskAcceptedUtc == Now.AddMinutes(2) &&
               ready.CurrentAttemptId is null && ready.AttemptStartedUtc is null &&
               ready.DeliveryId == resend.DeliveryId && ready.Attempts == 1,
            "Send again must persist explicit duplicate-risk evidence without changing the delivery id.");
        Assert((await resendControl.AuthorizeUnknownSendAgainAsync(resend.DeliveryId)).Outcome ==
               RoutingOperatorOutcome.AlreadyApplied,
            "Repeated resend authorization must converge on the original durable decision.");
    }

    private static async Task AssertConcurrentCommandsConvergeAsync(string root)
    {
        var store = CreateStore(root);
        var delivery = Delivery(
            Guid.NewGuid(), "clip-concurrent-failure", RoutingDeliveryMode.Automatic,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var current = await AppendAsync(store, delivery, Now);
        await PersistFailedAsync(
            store, current, delivery.DeliveryId, Now.AddSeconds(1));
        var controlA = new RoutingOperatorControl(store, () => Now.AddMinutes(1));
        var controlB = new RoutingOperatorControl(store, () => Now.AddMinutes(1));

        var outcomes = await Task.WhenAll(
            controlA.RetryFailedDeliveryAsync(delivery.DeliveryId),
            controlB.RetryFailedDeliveryAsync(delivery.DeliveryId));
        Assert(outcomes.Count(item => item.Outcome == RoutingOperatorOutcome.Applied) == 1 &&
               outcomes.Count(item => item.Outcome == RoutingOperatorOutcome.AlreadyApplied) == 1,
            "Concurrent identical operator actions must converge through generation CAS.");
        var retried = store.Load().Document!.Deliveries.Single();
        Assert(retried.State == PlannedDeliveryState.Ready &&
               retried.Attempts == 1 && retried.DeliveryId == delivery.DeliveryId,
            "CAS convergence must neither duplicate attempts nor replace the delivery id.");
    }

    private static async Task AssertHistoryMergesAndConflictsFailClosedAsync(string root)
    {
        var store = CreateStore(root);
        var delivered = Delivery(
            Guid.NewGuid(), "clip-history", RoutingDeliveryMode.Automatic,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var hot = await AppendAsync(store, delivered, Now);
        hot = await PersistDeliveredAsync(
            store, hot, delivered.DeliveryId, "discord:history", Now.AddSeconds(1));
        var archive = RoutingArchiveModel.Create(hot, delivered.PlanId);
        await store.ArchiveStore.PersistExactAsync(archive);

        var reader = new RoutingDeliveryHistoryReader(store);
        var overlap = reader.Read();
        Assert(overlap.Count == 1 && overlap[0].IsArchived &&
               overlap[0].ArchivedUtc == archive.ArchivedUtc &&
               overlap[0].Plan == archive.Plan &&
               overlap[0].Deliveries.SequenceEqual(archive.Deliveries),
            "An exact hot/archive compaction overlap must collapse to one immutable history item.");
        var compacted = await store.CompactTerminalPlansAsync(Now.AddMinutes(1));
        Assert(compacted.ArchivedPlanCount == 1 &&
               reader.Read() is [{ IsArchived: true }],
            "History must remain stable after the hot copy is compacted away.");

        var conflict = BuildTerminalOutbox(
            delivered.PlanId, "clip-history-conflict", "discord:other", Now.AddMinutes(2));
        await store.ArchiveStore.PersistExactAsync(
            RoutingArchiveModel.Create(conflict, delivered.PlanId));
        AssertThrows<InvalidDataException>(() => reader.Read(),
            "Different archived evidence for one plan id must fail closed instead of deduplicating.");
    }

    private static async Task AssertArchivedWorkIsNotActionableAsync(string root)
    {
        var store = CreateStore(root);
        var delivery = Delivery(
            Guid.NewGuid(), "clip-archived-control", RoutingDeliveryMode.Automatic,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var hot = await AppendAsync(store, delivery, Now);
        hot = await PersistDeliveredAsync(
            store, hot, delivery.DeliveryId, "discord:archived", Now.AddSeconds(1));
        await store.ArchiveStore.PersistExactAsync(
            RoutingArchiveModel.Create(hot, delivery.PlanId));
        var control = new RoutingOperatorControl(store, () => Now.AddMinutes(1));

        var result = await control.ResolveUnknownAsDeliveredAsync(
            delivery.DeliveryId, "discord:archived");
        Assert(result.Outcome == RoutingOperatorOutcome.Archived &&
               store.Load().Document!.Generation == hot.Generation,
            "An archived plan must be typed as immutable even during hot/archive overlap.");
        await store.CompactTerminalPlansAsync(Now.AddMinutes(2));
        result = await control.ResolveUnknownAsDeliveredAsync(
            delivery.DeliveryId, "discord:archived");
        Assert(result.Outcome == RoutingOperatorOutcome.Archived,
            "An archive-only delivery must remain discoverable but non-actionable.");
    }

    private static async Task AssertCancellationPreventsMutationAsync(string root)
    {
        var store = CreateStore(root);
        var delivery = Delivery(
            Guid.NewGuid(), "clip-cancelled-command", RoutingDeliveryMode.Approval,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var current = await AppendAsync(store, delivery, Now);
        var control = new RoutingOperatorControl(store, () => Now.AddMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            () => control.ApproveDeliveryAsync(delivery.DeliveryId, cancellation.Token),
            "A pre-cancelled operator action must not reach persistence.");
        var unchanged = store.Load().Document!;
        Assert(unchanged.Generation == current.Generation &&
               unchanged.Deliveries.Single().State ==
               PlannedDeliveryState.WaitingForApproval,
            "Cancellation must leave the durable outbox byte-for-byte semantically unchanged.");
    }

    private static RoutingOutboxStore CreateStore(string root)
    {
        Directory.CreateDirectory(root);
        return new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
    }

    private static async Task<RoutingOutboxDocument> AppendAsync(
        RoutingOutboxStore store,
        PlannedDelivery delivery,
        DateTimeOffset now)
    {
        var current = await store.LoadOrCreateAsync(now);
        var appended = RoutingOutboxModel.AppendPlan(
            current, delivery.PlanId, [delivery], [], now.AddMilliseconds(1));
        return await store.SaveAsync(appended, current.Generation);
    }

    private static async Task<RoutingOutboxDocument> AppendAsync(
        RoutingOutboxStore store,
        PlannedFileDisposition disposition,
        DateTimeOffset now)
    {
        var current = await store.LoadOrCreateAsync(now);
        var appended = RoutingOutboxModel.AppendPlan(
            current, disposition.PlanId, [], [disposition], now.AddMilliseconds(1));
        return await store.SaveAsync(appended, current.Generation);
    }

    private static PlannedDelivery Delivery(
        Guid planId,
        string clipId,
        RoutingDeliveryMode mode,
        bool artifactReady,
        RoutingMissingOutputBehavior missing,
        RoutingOutputKind requestedKind = RoutingOutputKind.Original)
    {
        var route = Route(clipId);
        var requested = new RoutingOutputReference(
            clipId, requestedKind, Hash(requestedKind == RoutingOutputKind.Original ? 'a' : 'b'));
        var original = new RoutingOutputReference(
            clipId, RoutingOutputKind.Original, Hash('a'));
        return RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, clipId, route,
            RoutingDestinationKind.Discord, "discord.test", requested, original,
            missing, mode,
            new RoutingDeliverySettings(
                null, null, null, RoutingVisibility.Unspecified, false),
            artifactReady, intentionalDuplicate: null, Now);
    }

    private static PlannedFileDisposition Disposition(Guid planId, string clipId) =>
        RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), planId, clipId, Hash('f'), Route(clipId),
            RoutingLibraryArea.LocalOnly, [], Now);

    private static RoutingRouteSnapshotReference Route(string name) => new(
        1, Guid.NewGuid(), 1, name, Guid.NewGuid(), 0, 0);

    private static async Task<RoutingOutboxDocument> PersistUnknownAsync(
        RoutingOutboxStore store,
        RoutingOutboxDocument current,
        Guid deliveryId,
        DateTimeOffset now)
    {
        var attemptId = Guid.NewGuid();
        var sending = RoutingOutboxModel.StartDelivery(current, deliveryId, attemptId, now);
        current = await store.SaveAsync(sending, current.Generation);
        var unknown = RoutingOutboxModel.MarkDeliveryUnknown(
            current, deliveryId, attemptId, "provider-result-unknown", now.AddMilliseconds(1));
        return await store.SaveAsync(unknown, current.Generation);
    }

    private static async Task<RoutingOutboxDocument> PersistFailedAsync(
        RoutingOutboxStore store,
        RoutingOutboxDocument current,
        Guid deliveryId,
        DateTimeOffset now)
    {
        var attemptId = Guid.NewGuid();
        var sending = RoutingOutboxModel.StartDelivery(current, deliveryId, attemptId, now);
        current = await store.SaveAsync(sending, current.Generation);
        var failed = RoutingOutboxModel.FailDelivery(
            current, deliveryId, attemptId, "discord-failed", null,
            now.AddMilliseconds(1));
        return await store.SaveAsync(failed, current.Generation);
    }

    private static async Task<RoutingOutboxDocument> PersistDeliveredAsync(
        RoutingOutboxStore store,
        RoutingOutboxDocument current,
        Guid deliveryId,
        string receipt,
        DateTimeOffset now)
    {
        var attemptId = Guid.NewGuid();
        var sending = RoutingOutboxModel.StartDelivery(current, deliveryId, attemptId, now);
        current = await store.SaveAsync(sending, current.Generation);
        var delivered = RoutingOutboxModel.CompleteDelivery(
            current, deliveryId, attemptId, receipt, now.AddMilliseconds(1));
        return await store.SaveAsync(delivered, current.Generation);
    }

    private static RoutingOutboxDocument Complete(
        RoutingOutboxDocument current,
        Guid deliveryId,
        string receipt,
        DateTimeOffset now)
    {
        var attemptId = Guid.NewGuid();
        current = RoutingOutboxModel.StartDelivery(current, deliveryId, attemptId, now);
        return RoutingOutboxModel.CompleteDelivery(
            current, deliveryId, attemptId, receipt, now.AddMilliseconds(1));
    }

    private static RoutingOutboxDocument BuildTerminalOutbox(
        Guid planId,
        string clipId,
        string receipt,
        DateTimeOffset now)
    {
        var delivery = Delivery(
            planId, clipId, RoutingDeliveryMode.Automatic,
            artifactReady: true, RoutingMissingOutputBehavior.NeedsAttention);
        var current = RoutingOutboxModel.CreateEmpty(now);
        current = RoutingOutboxModel.AppendPlan(
            current, planId, [delivery], [], now.AddMilliseconds(1));
        return Complete(current, delivery.DeliveryId, receipt, now.AddSeconds(1));
    }

    private static string Hash(char value) => new(value, 64);

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

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }
}
