using ClipsToDiscord;

internal static class RoutingDeliveryReceiptTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 18, 0, 0, TimeSpan.Zero);

    internal static void Run(string testRoot) => RunAsync(testRoot).GetAwaiter().GetResult();

    private static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(
            testRoot,
            "routing-delivery-receipts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await AssertClaimPrecedesProviderAndConfirmedReuseAsync(
                Path.Combine(root, "confirmed"));
            await AssertUnknownBlocksUntilFreshExplicitRiskAsync(
                Path.Combine(root, "unknown"));
            await AssertInterruptedClaimCanUseExplicitRiskAsync(
                Path.Combine(root, "interrupted"));
            await AssertReleasedClaimRetriesSafelyAsync(Path.Combine(root, "released"));
            await AssertIntentionalDuplicateUsesSeparateClaimSlotsAsync(
                Path.Combine(root, "intentional"));
            AssertPhysicalDeliveryKeyShape(Path.Combine(root, "keying"));
            await AssertCorruptionFailsClosedAsync(Path.Combine(root, "corrupt"));
            AssertConfirmedClaimsCannotBeFabricated();
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Test cleanup is best effort and restricted to this unique directory.
            }
        }
    }

    private static async Task AssertClaimPrecedesProviderAndConfirmedReuseAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, RoutingDeliveryReceiptStore.FileName);
        var store = new RoutingDeliveryReceiptStore(path);
        var guard = new RoutingDeliveryReceiptGuard(store, Guid.NewGuid, () => At(2));
        var first = CreateStartedDelivery(
            "clip-receipt-first",
            "discord.connection-primary",
            settings: PrivateSettings());
        await using var artifact = Artifact(
            Path.Combine(root, "PRIVATE_WINDOWS_USER", "PRIVATE_CLIP_NAME.mp4"),
            Hash('a'),
            "PRIVATE_CLIP_NAME.mp4",
            "PRIVATE_GAME_NAME");
        var inner = new RecordingProvider((delivery, resolved, _) =>
        {
            var durable = store.Load();
            var claim = durable.Document?.Claims.SingleOrDefault();
            Assert(durable.LoadedFromDisk && claim is
                {
                    State: RoutingDeliveryClaimState.Claimed,
                    Epoch: 1
                } &&
                   claim.OwnerDeliveryId == delivery.DeliveryId &&
                   claim.OwnerAttemptId == delivery.CurrentAttemptId &&
                   claim.Key.ArtifactSha256 == resolved.Sha256,
                "The exact physical-content claim must be durable before provider entry.");
            return RoutingDeliveryAttemptResult.Confirmed("discord:message-100");
        });
        var provider = new RoutingReceiptGuardedDeliveryProvider(inner, guard);

        var result = await provider.SendAsync(first.Delivery, artifact, CancellationToken.None);
        var persisted = store.Load().Document?.Claims.SingleOrDefault();
        Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Confirmed && inner.Calls == 1 &&
               persisted is
               {
                   State: RoutingDeliveryClaimState.Confirmed,
                   RemoteReceiptReference: "discord:message-100",
                   Epoch: 1
               },
            "A confirmed provider result must complete the already-durable claim.");

        var json = File.ReadAllText(path);
        foreach (var privateValue in new[]
                 {
                     "PRIVATE_WINDOWS_USER",
                     "PRIVATE_CLIP_NAME",
                     "PRIVATE_GAME_NAME",
                     "PRIVATE_MESSAGE_BODY",
                     "PRIVATE_VIDEO_TITLE",
                     "PRIVATE_CAPTION_TEXT"
                 })
        {
            Assert(!json.Contains(privateValue, StringComparison.Ordinal),
                "Delivery receipts must not persist paths, filenames, game names, or post copy.");
        }

        var restartedStore = new RoutingDeliveryReceiptStore(path);
        var restartedProvider = new RoutingReceiptGuardedDeliveryProvider(
            inner,
            new RoutingDeliveryReceiptGuard(restartedStore, Guid.NewGuid, () => At(3)));
        var second = CreateStartedDelivery(
            "clip-receipt-second",
            "discord.connection-primary");
        var reused = await restartedProvider.SendAsync(
            second.Delivery,
            artifact,
            CancellationToken.None);
        var afterRestart = restartedStore.Load().Document;
        Assert(reused.Outcome == RoutingDeliveryAttemptOutcome.Confirmed &&
               reused.RemoteReceiptReference?.StartsWith("dedup.", StringComparison.Ordinal) == true &&
               inner.Calls == 1 && afterRestart?.Claims.Count == 1 &&
               afterRestart.Claims[0].ClaimId == persisted!.ClaimId,
            "A restart must reuse confirmed physical-content evidence without another provider call.");
    }

    private static async Task AssertUnknownBlocksUntilFreshExplicitRiskAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = Store(root);
        var outcomes = new Queue<RoutingDeliveryAttemptResult>(
        [
            RoutingDeliveryAttemptResult.Unknown("provider-result-unknown"),
            RoutingDeliveryAttemptResult.Confirmed("discord:message-retry")
        ]);
        var inner = new RecordingProvider((_, _, _) => outcomes.Dequeue());
        var provider = new RoutingReceiptGuardedDeliveryProvider(
            inner,
            new RoutingDeliveryReceiptGuard(store, Guid.NewGuid, () => At(2)));
        var first = CreateStartedDelivery("clip-unknown", "discord.connection-unknown");
        await using var artifact = Artifact(root, Hash('b'));

        var unknownResult = await provider.SendAsync(
            first.Delivery, artifact, CancellationToken.None);
        var unknownClaim = store.Load().Document!.Claims.Single();
        Assert(unknownResult.Outcome == RoutingDeliveryAttemptOutcome.Unknown &&
               inner.Calls == 1 && unknownClaim.State == RoutingDeliveryClaimState.Unknown,
            "An ambiguous provider result must persist an unknown claim.");

        var otherOwner = CreateStartedDelivery(
            "clip-unknown-other-owner",
            "discord.connection-unknown");
        var blocked = await provider.SendAsync(
            otherOwner.Delivery, artifact, CancellationToken.None);
        Assert(blocked.Outcome == RoutingDeliveryAttemptOutcome.Unknown &&
               blocked.ErrorCode == "content-delivery-claim-unknown" && inner.Calls == 1,
            "An unknown physical-content claim must block a different delivery without provider I/O.");

        var outbox = RoutingOutboxModel.MarkDeliveryUnknown(
            first.Document,
            first.Delivery.DeliveryId,
            first.AttemptId,
            "provider-result-unknown",
            At(4));
        outbox = RoutingOutboxModel.AuthorizeUnknownSendAgain(
            outbox, first.Delivery.DeliveryId, At(5));
        var retryAttempt = Guid.NewGuid();
        outbox = RoutingOutboxModel.StartDelivery(
            outbox, first.Delivery.DeliveryId, retryAttempt, At(6));
        var retry = outbox.Deliveries.Single();

        var retried = await provider.SendAsync(retry, artifact, CancellationToken.None);
        var confirmed = store.Load().Document!.Claims.Single();
        Assert(retried.Outcome == RoutingDeliveryAttemptOutcome.Confirmed && inner.Calls == 2 &&
               confirmed.State == RoutingDeliveryClaimState.Confirmed &&
               confirmed.Key == unknownClaim.Key && confirmed.Epoch == unknownClaim.Epoch + 1 &&
               confirmed.ClaimId != unknownClaim.ClaimId &&
               confirmed.OwnerDeliveryId == unknownClaim.OwnerDeliveryId &&
               confirmed.DuplicateRiskAcceptedUtc == retry.DuplicateRiskAcceptedUtc,
            "Fresh explicit duplicate-risk approval must reuse the same claim slot under a new fence.");
    }

    private static async Task AssertInterruptedClaimCanUseExplicitRiskAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = Store(root);
        var guard = new RoutingDeliveryReceiptGuard(store, Guid.NewGuid, () => At(2));
        var first = CreateStartedDelivery("clip-interrupted", "discord.connection-interrupted");
        await using var artifact = Artifact(root, Hash('c'));
        var acquired = await guard.AcquireAsync(first.Delivery, artifact);
        Assert(acquired.Status == RoutingDeliveryClaimAcquireStatus.Acquired &&
               acquired.Claim.State == RoutingDeliveryClaimState.Claimed,
            "The interrupted-request fixture must leave a durable in-flight claim.");

        var recovered = RoutingOutboxModel.MarkDeliveryUnknown(
            first.Document,
            first.Delivery.DeliveryId,
            first.AttemptId,
            "interrupted-after-send-started",
            At(4));
        recovered = RoutingOutboxModel.AuthorizeUnknownSendAgain(
            recovered, first.Delivery.DeliveryId, At(5));
        recovered = RoutingOutboxModel.StartDelivery(
            recovered, first.Delivery.DeliveryId, Guid.NewGuid(), At(6));
        var reacquired = await guard.AcquireAsync(recovered.Deliveries.Single(), artifact);
        Assert(reacquired.Status == RoutingDeliveryClaimAcquireStatus.Acquired &&
               reacquired.Claim.Key == acquired.Claim.Key && reacquired.Claim.Epoch == 2 &&
               reacquired.Claim.ClaimId != acquired.Claim.ClaimId &&
               reacquired.Claim.DuplicateRiskAcceptedUtc == At(5),
            "A fresh explicit-risk decision must recover a claim stranded before completion.");
        var consumedApproval = await guard.AcquireAsync(
            recovered.Deliveries.Single(), artifact);
        Assert(consumedApproval.Status == RoutingDeliveryClaimAcquireStatus.BlockedUnknown &&
               consumedApproval.Claim.ClaimId == reacquired.Claim.ClaimId,
            "One duplicate-risk approval must be consumed by exactly one replacement claim.");
    }

    private static async Task AssertReleasedClaimRetriesSafelyAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = Store(root);
        var outcomes = new Queue<RoutingDeliveryAttemptResult>(
        [
            RoutingDeliveryAttemptResult.Failed("provider-rejected"),
            RoutingDeliveryAttemptResult.Confirmed("discord:message-after-retry")
        ]);
        var inner = new RecordingProvider((_, _, _) => outcomes.Dequeue());
        var provider = new RoutingReceiptGuardedDeliveryProvider(
            inner,
            new RoutingDeliveryReceiptGuard(store, Guid.NewGuid, () => At(2)));
        var first = CreateStartedDelivery("clip-released", "discord.connection-released");
        await using var artifact = Artifact(root, Hash('d'));

        var failed = await provider.SendAsync(first.Delivery, artifact, CancellationToken.None);
        var released = store.Load().Document!.Claims.Single();
        Assert(failed.Outcome == RoutingDeliveryAttemptOutcome.Failed &&
               released.State == RoutingDeliveryClaimState.Released,
            "A definite provider rejection must release its content claim for a safe retry.");

        var outbox = RoutingOutboxModel.FailDelivery(
            first.Document,
            first.Delivery.DeliveryId,
            first.AttemptId,
            "provider-rejected",
            providerResumeReference: null,
            At(4));
        outbox = RoutingOutboxModel.RetryFailedDelivery(
            outbox, first.Delivery.DeliveryId, At(5));
        outbox = RoutingOutboxModel.StartDelivery(
            outbox, first.Delivery.DeliveryId, Guid.NewGuid(), At(6));
        var confirmedResult = await provider.SendAsync(
            outbox.Deliveries.Single(), artifact, CancellationToken.None);
        var confirmed = store.Load().Document!.Claims.Single();
        Assert(confirmedResult.Outcome == RoutingDeliveryAttemptOutcome.Confirmed &&
               inner.Calls == 2 && confirmed.State == RoutingDeliveryClaimState.Confirmed &&
               confirmed.Key == released.Key && confirmed.Epoch == 2 &&
               confirmed.ClaimId != released.ClaimId &&
               confirmed.DuplicateRiskAcceptedUtc is null,
            "A released claim must retry without inventing duplicate-risk approval.");
    }

    private static async Task AssertIntentionalDuplicateUsesSeparateClaimSlotsAsync(string root)
    {
        Directory.CreateDirectory(root);
        var planId = Guid.NewGuid();
        var sourceClipId = "clip-intentional-pair";
        var connectionId = "discord.connection-intentional";
        var output = new RoutingOutputReference(
            sourceClipId, RoutingOutputKind.Original, Hash('e'));
        var firstRoute = RouteReference(Guid.NewGuid(), Guid.NewGuid(), order: 0);
        var secondRoute = RouteReference(Guid.NewGuid(), Guid.NewGuid(), order: 1);
        var proof = new IntentionalDuplicateProvenance(
            Guid.NewGuid(),
            IntentionalDuplicateDecision.UserConfirmedDeliverTwice,
            new RoutingDeliveryKey(connectionId, output),
            firstRoute.RouteId,
            secondRoute.RouteId,
            At(0));
        var first = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, sourceClipId, firstRoute,
            RoutingDestinationKind.Discord, connectionId, output, output,
            RoutingMissingOutputBehavior.UseOriginal, RoutingDeliveryMode.Automatic,
            Settings(), artifactReady: true, proof, At(0));
        var second = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, sourceClipId, secondRoute,
            RoutingDestinationKind.Discord, connectionId, output, output,
            RoutingMissingOutputBehavior.UseOriginal, RoutingDeliveryMode.Automatic,
            Settings(), artifactReady: true, proof, At(0));
        var outbox = RoutingOutboxModel.AppendPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), planId, [first, second], [], At(1));
        outbox = RoutingOutboxModel.StartDelivery(
            outbox, first.DeliveryId, Guid.NewGuid(), At(2));
        outbox = RoutingOutboxModel.StartDelivery(
            outbox, second.DeliveryId, Guid.NewGuid(), At(3));

        var store = Store(root);
        var inner = new RecordingProvider((delivery, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed($"discord:{delivery.DeliveryId:N}"));
        var provider = new RoutingReceiptGuardedDeliveryProvider(
            inner,
            new RoutingDeliveryReceiptGuard(store, Guid.NewGuid, () => At(4)));
        await using var artifact = Artifact(root, Hash('e'));
        foreach (var delivery in outbox.Deliveries)
        {
            var result = await provider.SendAsync(delivery, artifact, CancellationToken.None);
            Assert(result.Outcome == RoutingDeliveryAttemptOutcome.Confirmed,
                "Each explicitly authorized duplicate must receive its own provider result.");
        }

        var claims = store.Load().Document!.Claims;
        var expectedPrefix = $"intentional.{proof.GroupId:N}.";
        Assert(inner.Calls == 2 && claims.Count == 2 &&
               claims.Select(claim => claim.Key.ClaimSlot).ToHashSet(StringComparer.Ordinal)
                   .SetEquals([expectedPrefix + "first", expectedPrefix + "second"]) &&
               claims.Select(claim => claim.Key with { ClaimSlot = "default" })
                   .Distinct().Count() == 1,
            "Deliver-twice proof must separate exactly two claim slots for the same physical key.");

        var forged = outbox.Deliveries[0] with
        {
            IntentionalDuplicate = proof with
            {
                DeliveryKey = proof.DeliveryKey with { ConnectionId = "discord.forged" }
            }
        };
        AssertThrows<InvalidDataException>(() =>
                RoutingDeliveryReceiptModel.CreateKey(forged, artifact),
            "A claim slot must reject duplicate proof not bound to the delivery key.");
    }

    private static void AssertPhysicalDeliveryKeyShape(string root)
    {
        Directory.CreateDirectory(root);
        var baseline = CreateStartedDelivery("clip-key-a", "discord.connection-a");
        var samePhysical = CreateStartedDelivery(
            "clip-key-b", "discord.connection-a", outputRevision: Hash('f'));
        var otherConnection = CreateStartedDelivery(
            "clip-key-c", "discord.connection-b");
        var otherDestination = CreateStartedDelivery(
            "clip-key-d", "discord.connection-a", RoutingDestinationKind.YouTube);
        var artifact = Artifact(root, Hash('a'));
        var uppercaseArtifact = Artifact(root, Hash('A'));
        var otherHashArtifact = Artifact(root, Hash('b'));

        var baselineKey = RoutingDeliveryReceiptModel.CreateKey(baseline.Delivery, artifact);
        Assert(baselineKey == RoutingDeliveryReceiptModel.CreateKey(
                   samePhysical.Delivery, uppercaseArtifact),
            "Logical clip/output identity must not split the same physical content claim.");
        Assert(baselineKey != RoutingDeliveryReceiptModel.CreateKey(
                   otherConnection.Delivery, artifact) &&
               baselineKey != RoutingDeliveryReceiptModel.CreateKey(
                   otherDestination.Delivery, artifact) &&
               baselineKey != RoutingDeliveryReceiptModel.CreateKey(
                   baseline.Delivery, otherHashArtifact) &&
               baselineKey.ClaimSlot == "default",
            "Destination, connection, and physical hash must each participate in the claim key.");
    }

    private static async Task AssertCorruptionFailsClosedAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = Store(root);
        File.WriteAllText(store.Path, "{ not valid receipt json");
        var inner = new RecordingProvider((_, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed("discord:must-not-run"));
        var provider = new RoutingReceiptGuardedDeliveryProvider(
            inner,
            new RoutingDeliveryReceiptGuard(store, Guid.NewGuid, () => At(2)));
        var delivery = CreateStartedDelivery("clip-corrupt", "discord.connection-corrupt");
        await using var artifact = Artifact(root, Hash('f'));

        await AssertThrowsAsync<InvalidDataException>(() => provider.SendAsync(
                delivery.Delivery, artifact, CancellationToken.None),
            "Corrupt receipt state must fail closed before provider entry.");
        Assert(inner.Calls == 0 && store.Load().Status == RoutingDocumentLoadStatus.Corrupt,
            "Corrupt receipt state must remain untouched and must not call the provider.");
    }

    private static void AssertConfirmedClaimsCannotBeFabricated()
    {
        var current = RoutingDeliveryReceiptModel.CreateEmpty(At(0));
        var key = new RoutingDeliveryClaimKey(
            RoutingDestinationKind.Discord,
            "discord.connection-fabricated",
            Hash('a'),
            "default");
        var fabricated = new RoutingDeliveryClaim(
            key,
            Epoch: 1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "clip-fabricated",
            Guid.NewGuid(),
            RoutingDeliveryClaimState.Confirmed,
            "discord:invented",
            ErrorCode: null,
            DuplicateRiskAcceptedUtc: null,
            ClaimedUtc: At(1),
            UpdatedUtc: At(1));
        var candidate = current with
        {
            Generation = current.Generation + 1,
            Claims = [fabricated],
            UpdatedUtc = At(1)
        };
        AssertThrows<InvalidDataException>(() =>
                RoutingDeliveryReceiptModel.ValidateSuccessor(current, candidate),
            "A receipt successor must not introduce a claim directly as confirmed.");
    }

    private static StartedDelivery CreateStartedDelivery(
        string sourceClipId,
        string connectionId,
        RoutingDestinationKind destination = RoutingDestinationKind.Discord,
        string? outputRevision = null,
        RoutingDeliverySettings? settings = null)
    {
        var planId = Guid.NewGuid();
        var output = new RoutingOutputReference(
            sourceClipId,
            RoutingOutputKind.Original,
            outputRevision ?? Hash('a'));
        var delivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(),
            planId,
            sourceClipId,
            RouteReference(Guid.NewGuid(), Guid.NewGuid(), order: 0),
            destination,
            connectionId,
            output,
            output,
            RoutingMissingOutputBehavior.UseOriginal,
            RoutingDeliveryMode.Automatic,
            settings ?? Settings(),
            artifactReady: true,
            intentionalDuplicate: null,
            At(0));
        var document = RoutingOutboxModel.AppendPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), planId, [delivery], [], At(1));
        var attemptId = Guid.NewGuid();
        document = RoutingOutboxModel.StartDelivery(
            document, delivery.DeliveryId, attemptId, At(2));
        return new StartedDelivery(document, document.Deliveries.Single(), attemptId);
    }

    private static RoutingRouteSnapshotReference RouteReference(
        Guid routeId,
        Guid actionId,
        int order) => new(
        RoutingGeneration: 1,
        routeId,
        RouteRevision: 1,
        "Receipt test route",
        actionId,
        Priority: order,
        Order: order);

    private static RoutingDeliveryReceiptStore Store(string root)
    {
        Directory.CreateDirectory(root);
        return new RoutingDeliveryReceiptStore(
            Path.Combine(root, RoutingDeliveryReceiptStore.FileName));
    }

    private static RoutingResolvedArtifact Artifact(
        string rootOrPath,
        string hash,
        string displayFileName = "clip.mp4",
        string gameName = "Receipt Test Game")
    {
        var path = Path.HasExtension(rootOrPath)
            ? rootOrPath
            : Path.Combine(rootOrPath, "clip.mp4");
        return new RoutingResolvedArtifact(
            path,
            displayFileName,
            gameName,
            byteLength: 42,
            hash);
    }

    private static RoutingDeliverySettings Settings() =>
        new(null, null, null, RoutingVisibility.Unspecified, false);

    private static RoutingDeliverySettings PrivateSettings() => new(
        "PRIVATE_MESSAGE_BODY",
        "PRIVATE_VIDEO_TITLE",
        "PRIVATE_CAPTION_TEXT",
        RoutingVisibility.Unlisted,
        NotifyFollowers: false);

    private static string Hash(char value) => new(value, 64);
    private static DateTimeOffset At(int seconds) => Now.AddSeconds(seconds);

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

    private sealed record StartedDelivery(
        RoutingOutboxDocument Document,
        PlannedDelivery Delivery,
        Guid AttemptId);

    private sealed class RecordingProvider(
        Func<PlannedDelivery, RoutingResolvedArtifact, CancellationToken,
            RoutingDeliveryAttemptResult> send) : IRoutingDeliveryProvider
    {
        private readonly Func<PlannedDelivery, RoutingResolvedArtifact, CancellationToken,
            RoutingDeliveryAttemptResult> _send = send;

        internal int Calls { get; private set; }

        public bool Supports(RoutingDestinationKind destination) => true;

        public Task<RoutingDeliveryAttemptResult> SendAsync(
            PlannedDelivery delivery,
            RoutingResolvedArtifact artifact,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_send(delivery, artifact, cancellationToken));
        }
    }
}
