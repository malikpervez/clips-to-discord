using System.Diagnostics;
using System.Text.Json.Nodes;
using ClipsToDiscord;

internal static class RoutingFoundationTests
{
    internal static void Run(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        AssertApprovedRouteContractAndStructuralEquality(testRoot);
        AssertPlanAndDispositionInvariants();
        AssertMissingArtifactOutcomesSurviveRestart(testRoot);
        AssertSelectiveMissingArtifactResolution(testRoot);
        AssertPlanFallbackCollisionsSurviveRestart(testRoot);
        AssertIntentionalDuplicateProvenance();
        AssertDeliveryAndDispositionRecovery();
        AssertOutboxPersistenceAndRecovery(testRoot);
        AssertAncestorReparsePointsAreRejected(testRoot);
    }

    private static void AssertApprovedRouteContractAndStructuralEquality(string testRoot)
    {
        var route = new RoutingRoute(
            Guid.NewGuid(), "Battlefield highlights", true, 0, 1,
            RoutingRouteSource.User, RoutingRouteKind.Specific,
            RoutingTriggerKind.AnyNewSourceClip,
            Prepare: new RoutingPrepareSettings(
                Landscape: true, Portrait: true,
                RoutingMissingOutputBehavior.NeedsAttention),
            Conditions:
            [
                Condition(RoutingConditionField.Game, RoutingConditionOperator.Contains, "Battlefield"),
                Condition(RoutingConditionField.ClipSource, RoutingConditionOperator.Equals, "watchedFolder"),
                Condition(RoutingConditionField.ReactionCamera, RoutingConditionOperator.Equals, "true"),
                Condition(RoutingConditionField.CaptureType, RoutingConditionOperator.Equals, "instantReplay"),
                Condition(RoutingConditionField.Duration, RoutingConditionOperator.GreaterThanOrEqual, "3000")
            ],
            Actions:
            [
                DeliveryAction(Guid.NewGuid(), "discord.friends",
                    RoutingOutputKind.Portrait, RoutingMissingOutputBehavior.UseOriginal,
                    RoutingDeliveryMode.Approval),
                FileAction(Guid.NewGuid(), RoutingLibraryArea.Uploaded)
            ],
            CreatedUtc: At(0), ModifiedUtc: At(0));
        var fallback = new RoutingRoute(
            Guid.NewGuid(), "Everything else to Local only", true, 1, 1,
            RoutingRouteSource.Migration, RoutingRouteKind.Fallback,
            RoutingTriggerKind.AnyNewSourceClip,
            new RoutingPrepareSettings(false, false, RoutingMissingOutputBehavior.UseOriginal), [],
            [FileAction(Guid.NewGuid(), RoutingLibraryArea.LocalOnly)], At(0), At(0));
        var document = new RoutingSnapshotDocument(
            RoutingSnapshotStore.CurrentSchemaVersion, 1, [route, fallback], At(0), At(0));
        RoutingSnapshotModel.Validate(document);

        var badFallback = document with
        {
            Routes = [fallback with { Conditions = [Condition(
                RoutingConditionField.Game, RoutingConditionOperator.Equals, "DUSKFADE")] }]
        };
        AssertThrows<InvalidDataException>(() => RoutingSnapshotModel.Validate(badFallback),
            "A fallback route must not carry a hidden specific condition.");

        var root = Path.Combine(testRoot, "snapshot-roundtrip");
        Directory.CreateDirectory(root);
        var store = new RoutingSnapshotStore(Path.Combine(root, RoutingSnapshotStore.FileName));
        _ = store.SaveAsync(document, 0).GetAwaiter().GetResult();
        var loaded = store.Load();
        Assert(loaded.LoadedFromDisk && loaded.Document == document,
            "A route snapshot must retain structural equality after JSON round-trip.");

        var revised = RoutingSnapshotModel.ReplaceRoutes(document,
            [route with { Revision = 2, Name = "Battlefield best moments", ModifiedUtc = At(1) }, fallback], At(1));
        _ = store.SaveAsync(revised, document.Generation).GetAwaiter().GetResult();
        AssertThrows<RoutingConcurrencyException>(
            () => store.SaveAsync(revised, document.Generation).GetAwaiter().GetResult(),
            "A stale route generation must lose the persistence CAS.");

        var validJson = File.ReadAllText(store.Path);
        var nullSettings = JsonNode.Parse(validJson)!;
        nullSettings["routes"]![0]!["actions"]![0]!["deliverySettings"] = null;
        File.WriteAllText(store.Path, nullSettings.ToJsonString());
        Assert(store.Load().Status == RoutingDocumentLoadStatus.Invalid,
            "A nested null that reaches validation must load as Invalid, not escape an exception.");
        var nullPrepare = JsonNode.Parse(validJson)!;
        nullPrepare["routes"]![0]!["prepare"] = null;
        File.WriteAllText(store.Path, nullPrepare.ToJsonString());
        Assert(store.Load().Status == RoutingDocumentLoadStatus.Invalid,
            "A null route prepare contract must load as Invalid.");
    }

    private static void AssertPlanAndDispositionInvariants()
    {
        var empty = RoutingOutboxModel.CreateEmpty(At(0));
        var route = RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Discord and archive");
        var plan = Guid.NewGuid();
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.CreateDelivery(
                Guid.NewGuid(), plan, "clip-a", route, RoutingDestinationKind.Discord,
                "discord.friends", Output("clip-b", RoutingOutputKind.Original, 'a'),
                Output("clip-a", RoutingOutputKind.Original, 'a'),
                RoutingMissingOutputBehavior.NeedsAttention,
                RoutingDeliveryMode.Automatic, Settings(), true, null, At(0)),
            "A planned output must belong to the same source clip.");

        var delivery = Delivery(plan, "clip-a", route, 'a');
        var disposition = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), plan, "clip-a", Hash('a'),
            route with { ActionId = Guid.NewGuid() },
            RoutingLibraryArea.Uploaded, [delivery.DeliveryId], At(0));
        var missingDependency = disposition with { PrerequisiteDeliveryIds = [] };
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                empty, plan, [delivery], [missingDependency], At(1)),
            "A terminal disposition must depend on every sibling delivery.");
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                empty, plan, [delivery], [disposition, disposition with
                {
                    DispositionId = Guid.NewGuid()
                }], At(1)),
            "One plan/source cannot have two terminal dispositions.");

        var localPlan = Guid.NewGuid();
        var local = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), localPlan, "clip-local", Hash('b'), route,
            RoutingLibraryArea.LocalOnly, [], At(0));
        var localOnly = RoutingOutboxModel.AppendPlan(empty, localPlan, [], [local], At(1));
        Assert(localOnly.FileDispositions[0].State == PlannedFileDispositionState.Ready,
            "A deliberate local-management route must be ready without an upload dependency.");
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                empty, localPlan, [Delivery(localPlan, "clip-local", route, 'b')], [local], At(1)),
            "Local-only filing must not silently coexist with external delivery.");

        var anotherPlan = Guid.NewGuid();
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                localOnly, anotherPlan, [],
                [RoutingOutboxModel.CreateFileDisposition(
                    Guid.NewGuid(), anotherPlan, "clip-local", Hash('b'),
                    route with { ActionId = Guid.NewGuid() }, RoutingLibraryArea.LocalOnly, [], At(2))],
                At(2)),
            "A source clip must not be frozen into a second plan.");

        var generationPlan = Guid.NewGuid();
        var generationOne = Delivery(generationPlan, "clip-generation", route, 'c');
        var generationTwo = Delivery(generationPlan, "clip-generation",
            route with { RoutingGeneration = 2, RouteId = Guid.NewGuid(), ActionId = Guid.NewGuid() }, 'd');
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                empty, generationPlan, [generationOne, generationTwo], [], At(1)),
            "A plan cannot mix route snapshot generations.");

        var repeatedActionPlan = Guid.NewGuid();
        var firstAction = Delivery(repeatedActionPlan, "clip-action", route, 'e');
        var repeatedAction = firstAction with
        {
            DeliveryId = Guid.NewGuid(), ConnectionId = "discord.other",
            RequestedOutput = Output("clip-action", RoutingOutputKind.Original, 'f'),
            Output = Output("clip-action", RoutingOutputKind.Original, 'f'),
            OriginalOutput = Output("clip-action", RoutingOutputKind.Original, 'f')
        };
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                empty, repeatedActionPlan, [firstAction, repeatedAction], [], At(1)),
            "One route/action snapshot cannot produce multiple members in the same plan.");
    }

    private static void AssertMissingArtifactOutcomesSurviveRestart(string testRoot)
    {
        var root = Path.Combine(testRoot, "missing-artifact-outcomes");
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var current = store.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult();
        var cases = new[]
        {
            ("clip-fallback", RoutingMissingOutputBehavior.UseOriginal),
            ("clip-skip", RoutingMissingOutputBehavior.Skip),
            ("clip-needs", RoutingMissingOutputBehavior.NeedsAttention)
        };
        var deliveryIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var recoveryIds = new Dictionary<RoutingNeedsAttentionResolution, Guid>();
        var time = 1;
        foreach (var item in cases)
        {
            var plan = Guid.NewGuid();
            var route = RouteReference(Guid.NewGuid(), Guid.NewGuid(), item.Item1);
            var requested = Output(item.Item1, RoutingOutputKind.Portrait, 'a');
            var original = Output(item.Item1, RoutingOutputKind.Original, 'b');
            var delivery = RoutingOutboxModel.CreateDelivery(
                Guid.NewGuid(), plan, item.Item1, route, RoutingDestinationKind.Discord,
                "discord.friends", requested, original, item.Item2,
                RoutingDeliveryMode.Automatic, Settings(), artifactReady: false,
                intentionalDuplicate: null, At(time));
            var appended = RoutingOutboxModel.AppendPlan(current, plan, [delivery], [], At(time));
            _ = store.SaveAsync(appended, current.Generation).GetAwaiter().GetResult();
            current = RoutingOutboxModel.ResolveMissingArtifacts(
                appended, plan, [delivery.RequestedOutput], "rendition-failed", At(++time));
            _ = store.SaveAsync(current, appended.Generation).GetAwaiter().GetResult();
            deliveryIds[item.Item1] = delivery.DeliveryId;
            time++;
        }
        foreach (var resolution in Enum.GetValues<RoutingNeedsAttentionResolution>())
        {
            var clipId = $"clip-recover-{resolution.ToString().ToLowerInvariant()}";
            var plan = Guid.NewGuid();
            var delivery = RoutingOutboxModel.CreateDelivery(
                Guid.NewGuid(), plan, clipId,
                RouteReference(Guid.NewGuid(), Guid.NewGuid(), clipId),
                RoutingDestinationKind.Discord, "discord.friends",
                Output(clipId, RoutingOutputKind.Portrait, 'c'),
                Output(clipId, RoutingOutputKind.Original, 'd'),
                RoutingMissingOutputBehavior.NeedsAttention,
                RoutingDeliveryMode.Automatic, Settings(), false, null, At(time));
            var appended = RoutingOutboxModel.AppendPlan(current, plan, [delivery], [], At(time));
            _ = store.SaveAsync(appended, current.Generation).GetAwaiter().GetResult();
            var needsAttention = RoutingOutboxModel.ResolveMissingArtifacts(
                appended, plan, [delivery.RequestedOutput], "rendition-failed", At(++time));
            _ = store.SaveAsync(needsAttention, appended.Generation).GetAwaiter().GetResult();
            current = RoutingOutboxModel.ResolveNeedsAttention(
                needsAttention, delivery.DeliveryId, resolution, At(++time));
            _ = store.SaveAsync(current, needsAttention.Generation).GetAwaiter().GetResult();
            recoveryIds[resolution] = delivery.DeliveryId;
            time++;
        }

        var restarted = new RoutingOutboxStore(store.Path)
            .LoadAndRecoverAsync(At(time)).GetAwaiter().GetResult();
        var fallback = restarted.Deliveries.Single(item =>
            item.DeliveryId == deliveryIds["clip-fallback"]);
        var skipped = restarted.Deliveries.Single(item =>
            item.DeliveryId == deliveryIds["clip-skip"]);
        var needs = restarted.Deliveries.Single(item =>
            item.DeliveryId == deliveryIds["clip-needs"]);
        Assert(fallback.State == PlannedDeliveryState.Ready &&
               fallback.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
               fallback.Output == fallback.OriginalOutput &&
               fallback.OnMissingOutput == RoutingMissingOutputBehavior.UseOriginal,
            "Use original must survive restart as the frozen effective output.");
        Assert(skipped.State == PlannedDeliveryState.Skipped &&
               skipped.ArtifactOutcome == RoutingMissingArtifactOutcome.Skipped &&
               skipped.CompletedUtc is not null,
            "Skip must survive restart as a durable terminal outcome.");
        Assert(needs.State == PlannedDeliveryState.NeedsAttention &&
               needs.ArtifactOutcome == RoutingMissingArtifactOutcome.NeedsAttention &&
               needs.ArtifactErrorCode == "rendition-failed",
            "Needs attention must survive restart with its safe failure reason.");
        var recoveredOriginal = restarted.Deliveries.Single(item => item.DeliveryId ==
            recoveryIds[RoutingNeedsAttentionResolution.UseOriginal]);
        var recoveredSkip = restarted.Deliveries.Single(item => item.DeliveryId ==
            recoveryIds[RoutingNeedsAttentionResolution.Skip]);
        var recoveredRetry = restarted.Deliveries.Single(item => item.DeliveryId ==
            recoveryIds[RoutingNeedsAttentionResolution.RetryOrRebuild]);
        Assert(recoveredOriginal.State == PlannedDeliveryState.Ready &&
               recoveredOriginal.ArtifactOutcome ==
                   RoutingMissingArtifactOutcome.OriginalFallback &&
               recoveredOriginal.Output == recoveredOriginal.OriginalOutput,
            "Needs-attention use-original recovery must persist across restart.");
        Assert(recoveredSkip.State == PlannedDeliveryState.Skipped &&
               recoveredSkip.ArtifactOutcome == RoutingMissingArtifactOutcome.Skipped &&
               recoveredSkip.CompletedUtc is not null,
            "Needs-attention skip recovery must persist across restart.");
        Assert(recoveredRetry.State == PlannedDeliveryState.WaitingForArtifact &&
               recoveredRetry.ArtifactOutcome == RoutingMissingArtifactOutcome.RequestedOutput &&
               recoveredRetry.ArtifactErrorCode is null &&
               recoveredRetry.Output == recoveredRetry.RequestedOutput,
            "Needs-attention retry/rebuild must return durably to artifact waiting.");
    }

    private static void AssertPlanFallbackCollisionsSurviveRestart(string testRoot)
    {
        var root = Path.Combine(testRoot, "fallback-collisions");
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var current = store.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult();

        var plan = Guid.NewGuid();
        const string clipId = "clip-precedence";
        var original = Output(clipId, RoutingOutputKind.Original, 'e');
        var highRoute = RouteReference(
            Guid.NewGuid(), Guid.NewGuid(), "Higher route", priority: 0, order: 1);
        var lowRoute = RouteReference(
            Guid.NewGuid(), Guid.NewGuid(), "Lower route", priority: 10, order: 2);
        var high = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), plan, clipId, highRoute, RoutingDestinationKind.Discord,
            "discord.friends", Output(clipId, RoutingOutputKind.Landscape, 'f'), original,
            RoutingMissingOutputBehavior.UseOriginal, RoutingDeliveryMode.Automatic,
            Settings(), false, null, At(0));
        var low = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), plan, clipId, lowRoute, RoutingDestinationKind.YouTube,
            "discord.friends", Output(clipId, RoutingOutputKind.Portrait, 'f'), original,
            RoutingMissingOutputBehavior.UseOriginal, RoutingDeliveryMode.Automatic,
            Settings(), false, null, At(0));
        var disposition = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), plan, clipId, Hash('e'),
            RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Archive", priority: 20, order: 3),
            RoutingLibraryArea.Uploaded, [low.DeliveryId, high.DeliveryId], At(0));
        var appended = RoutingOutboxModel.AppendPlan(
            current, plan, [low, high], [disposition], At(1));
        _ = store.SaveAsync(appended, current.Generation).GetAwaiter().GetResult();
        current = RoutingOutboxModel.ResolveMissingArtifacts(
            appended, plan, [high.RequestedOutput, low.RequestedOutput],
            "rendition-failed", At(2));
        Assert(current.Generation == appended.Generation + 1,
            "A whole-plan missing-artifact decision must commit in one generation.");
        _ = store.SaveAsync(current, appended.Generation).GetAwaiter().GetResult();
        var sendAttempt = Guid.NewGuid();
        var sending = RoutingOutboxModel.StartDelivery(current, high.DeliveryId, sendAttempt, At(3));
        _ = store.SaveAsync(sending, current.Generation).GetAwaiter().GetResult();
        var delivered = RoutingOutboxModel.CompleteDelivery(
            sending, high.DeliveryId, sendAttempt, "discord.receipt-fallback", At(4));
        _ = store.SaveAsync(delivered, sending.Generation).GetAwaiter().GetResult();
        current = RoutingOutboxModel.RefreshFileDisposition(
            delivered, disposition.DispositionId, At(5));
        _ = store.SaveAsync(current, delivered.Generation).GetAwaiter().GetResult();

        var deliberatePlan = Guid.NewGuid();
        const string deliberateClip = "clip-deliver-twice";
        var requested = Output(deliberateClip, RoutingOutputKind.Portrait, 'a');
        var deliberateOriginal = Output(deliberateClip, RoutingOutputKind.Original, 'b');
        var firstRoute = RouteReference(
            Guid.NewGuid(), Guid.NewGuid(), "First deliberate", priority: 0, order: 0);
        var secondRoute = RouteReference(
            Guid.NewGuid(), Guid.NewGuid(), "Second deliberate", priority: 1, order: 1);
        var first = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), deliberatePlan, deliberateClip, firstRoute,
            RoutingDestinationKind.Discord, "discord.friends", requested,
            deliberateOriginal, RoutingMissingOutputBehavior.UseOriginal,
            RoutingDeliveryMode.Automatic, Settings(), false, null, At(6));
        var second = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), deliberatePlan, deliberateClip, secondRoute,
            RoutingDestinationKind.YouTube, "discord.friends", requested,
            deliberateOriginal, RoutingMissingOutputBehavior.UseOriginal,
            RoutingDeliveryMode.Automatic, Settings(), false, null, At(6));
        var proof = new IntentionalDuplicateProvenance(
            Guid.NewGuid(), IntentionalDuplicateDecision.UserConfirmedDeliverTwice,
            first.DeliveryKey, firstRoute.RouteId, secondRoute.RouteId, At(6));
        first = first with { IntentionalDuplicate = proof };
        second = second with { IntentionalDuplicate = proof };
        appended = RoutingOutboxModel.AppendPlan(
            current, deliberatePlan, [first, second], [], At(6));
        _ = store.SaveAsync(appended, current.Generation).GetAwaiter().GetResult();
        current = RoutingOutboxModel.ResolveMissingArtifacts(
            appended, deliberatePlan, [requested], "rendition-failed", At(7));
        _ = store.SaveAsync(current, appended.Generation).GetAwaiter().GetResult();

        var restarted = new RoutingOutboxStore(store.Path)
            .LoadAndRecoverAsync(At(8)).GetAwaiter().GetResult();
        var precedenceMembers = restarted.Deliveries.Where(item => item.PlanId == plan).ToArray();
        Assert(precedenceMembers.Single(item => item.Route.RouteId == highRoute.RouteId).State ==
                   PlannedDeliveryState.Delivered &&
               precedenceMembers.Single(item => item.Route.RouteId == lowRoute.RouteId)
                   .ArtifactOutcome == RoutingMissingArtifactOutcome.DuplicateSuppressed,
            "An unapproved fallback collision must keep the higher frozen route only.");
        Assert(restarted.FileDispositions.Single(item => item.DispositionId ==
                   disposition.DispositionId).State == PlannedFileDispositionState.Ready,
            "Uploaded filing must treat duplicate suppression as settled after one confirmed delivery.");
        var deliberateMembers = restarted.Deliveries
            .Where(item => item.PlanId == deliberatePlan).ToArray();
        Assert(deliberateMembers.All(item =>
                   item.State == PlannedDeliveryState.Ready &&
                   item.IntentionalDuplicate is not null &&
                   item.IntentionalDuplicate.DeliveryKey == item.DeliveryKey) &&
               deliberateMembers.Select(item => item.IntentionalDuplicate!.GroupId)
                   .Distinct().Count() == 1,
            "Exact deliver-twice proof must be atomically rebound to the fallback output key.");
    }

    private static void AssertSelectiveMissingArtifactResolution(string testRoot)
    {
        var root = Path.Combine(testRoot, "selective-missing-output");
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var empty = store.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult();
        var plan = Guid.NewGuid();
        const string clipId = "clip-mixed-renditions";
        var originalRef = Output(clipId, RoutingOutputKind.Original, '1');
        var portrait = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), plan, clipId,
            RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Portrait route", 0, 0),
            RoutingDestinationKind.TikTok, "tiktok.account",
            Output(clipId, RoutingOutputKind.Portrait, '2'), originalRef,
            RoutingMissingOutputBehavior.UseOriginal, RoutingDeliveryMode.Automatic,
            Settings(), false, null, At(0));
        var landscape = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), plan, clipId,
            RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Landscape route", 1, 1),
            RoutingDestinationKind.YouTube, "youtube.account",
            Output(clipId, RoutingOutputKind.Landscape, '3'), originalRef,
            RoutingMissingOutputBehavior.NeedsAttention, RoutingDeliveryMode.Automatic,
            Settings(), false, null, At(0));
        var readyOriginal = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), plan, clipId,
            RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Original route", 2, 2),
            RoutingDestinationKind.Discord, "discord.friends", originalRef, originalRef,
            RoutingMissingOutputBehavior.NeedsAttention, RoutingDeliveryMode.Automatic,
            Settings(), true, null, At(0));
        var appended = RoutingOutboxModel.AppendPlan(
            empty, plan, [portrait, landscape, readyOriginal], [], At(1));
        _ = store.SaveAsync(appended, empty.Generation).GetAwaiter().GetResult();
        var resolved = RoutingOutboxModel.ResolveMissingArtifacts(
            appended, plan, [portrait.RequestedOutput], "portrait-render-failed", At(2));
        _ = store.SaveAsync(resolved, appended.Generation).GetAwaiter().GetResult();
        var restarted = new RoutingOutboxStore(store.Path)
            .LoadAndRecoverAsync(At(3)).GetAwaiter().GetResult();
        Assert(restarted.Deliveries.Single(item => item.DeliveryId == portrait.DeliveryId)
                   .ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
               restarted.Deliveries.Single(item => item.DeliveryId == landscape.DeliveryId).State ==
                   PlannedDeliveryState.WaitingForArtifact &&
               restarted.Deliveries.Single(item => item.DeliveryId == readyOriginal.DeliveryId).State ==
                   PlannedDeliveryState.Ready,
            "A portrait failure must not resolve a pending landscape or disturb an already-ready output.");
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.ResolveMissingArtifacts(
                restarted, plan,
                [Output(clipId, RoutingOutputKind.Portrait, '9')],
                "unknown-output", At(4)),
            "A failure report must name an exact waiting output reference from the plan.");
    }

    private static void AssertIntentionalDuplicateProvenance()
    {
        var empty = RoutingOutboxModel.CreateEmpty(At(0));
        var planId = Guid.NewGuid();
        var routeOne = RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Route one");
        var routeTwo = RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Route two");
        var output = Output("clip-duplicate", RoutingOutputKind.Landscape, 'c');
        var first = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, "clip-duplicate", routeOne,
            RoutingDestinationKind.Discord, "shared.connection", output,
            Output("clip-duplicate", RoutingOutputKind.Original, 'd'),
            RoutingMissingOutputBehavior.NeedsAttention,
            RoutingDeliveryMode.Automatic, Settings(), true, null, At(0));
        var second = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, "clip-duplicate", routeTwo,
            RoutingDestinationKind.Discord, "shared.connection", output,
            Output("clip-duplicate", RoutingOutputKind.Original, 'd'),
            RoutingMissingOutputBehavior.NeedsAttention,
            RoutingDeliveryMode.Automatic, Settings(), true, null, At(0));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                empty, planId, [first, second], [], At(1)),
            "An exact provider/connection/output duplicate requires explicit proof.");

        var proof = new IntentionalDuplicateProvenance(
            Guid.NewGuid(), IntentionalDuplicateDecision.UserConfirmedDeliverTwice,
            first.DeliveryKey, routeOne.RouteId, routeTwo.RouteId, At(0));
        var planned = RoutingOutboxModel.AppendPlan(empty, planId,
            [first with { IntentionalDuplicate = proof }, second with { IntentionalDuplicate = proof }],
            [], At(1));
        Assert(planned.Deliveries.Select(item => item.ProviderIdempotencyKey).Distinct().Count() == 2,
            "Deliver twice must persist two stable, independent planned-delivery ids.");

        var wrongRoutes = proof with { SecondRouteId = Guid.NewGuid() };
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(empty, planId,
                [first with { IntentionalDuplicate = wrongRoutes },
                 second with { IntentionalDuplicate = wrongRoutes }], [], At(1)),
            "Duplicate proof must bind exactly the two member routes.");

        var typedPlan = Guid.NewGuid();
        var discord = first with { DeliveryId = Guid.NewGuid(), PlanId = typedPlan };
        var youtube = second with
        {
            DeliveryId = Guid.NewGuid(), PlanId = typedPlan,
            Destination = RoutingDestinationKind.YouTube, IntentionalDuplicate = null
        };
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                empty, typedPlan, [discord, youtube], [], At(1)),
            "Destination must not distinguish the exact connection/output duplicate key.");
    }

    private static void AssertDeliveryAndDispositionRecovery()
    {
        var plan = Guid.NewGuid();
        var route = RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Discord archive");
        var delivery = Delivery(plan, "clip-recovery", route, 'd');
        var disposition = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), plan, "clip-recovery", Hash('d'),
            route with { ActionId = Guid.NewGuid() },
            RoutingLibraryArea.Uploaded, [delivery.DeliveryId], At(0));
        var current = RoutingOutboxModel.AppendPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), plan, [delivery], [disposition], At(1));

        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.RefreshFileDisposition(
                current, disposition.DispositionId, At(2)),
            "Uploaded filing cannot begin while delivery is merely active or settled-as-failed.");
        var attempt = Guid.NewGuid();
        current = RoutingOutboxModel.StartDelivery(current, delivery.DeliveryId, attempt, At(2));
        var providerKey = current.Deliveries[0].ProviderIdempotencyKey;
        current = RoutingOutboxModel.RecoverInterruptedWork(current, At(3));
        Assert(current.Deliveries[0].State == PlannedDeliveryState.DeliveryUnknown,
            "An interrupted send must become DeliveryUnknown.");
        current = RoutingOutboxModel.AuthorizeUnknownSendAgain(current, delivery.DeliveryId, At(4));
        var retryAttempt = Guid.NewGuid();
        current = RoutingOutboxModel.StartDelivery(current, delivery.DeliveryId, retryAttempt, At(5));
        Assert(current.Deliveries[0].ProviderIdempotencyKey == providerKey,
            "Retry must reuse its planned-delivery id.");
        current = RoutingOutboxModel.CompleteDelivery(
            current, delivery.DeliveryId, retryAttempt, "discord.receipt-1", At(6));
        current = RoutingOutboxModel.RefreshFileDisposition(current, disposition.DispositionId, At(7));
        var moveAttempt = Guid.NewGuid();
        current = RoutingOutboxModel.StartFileDisposition(
            current, disposition.DispositionId, moveAttempt, At(8));
        current = RoutingOutboxModel.RecoverInterruptedWork(current, At(9));
        current = RoutingOutboxModel.CompleteRecoveredFileDisposition(
            current, disposition.DispositionId, "library.item-1", At(10));
        Assert(current.FileDispositions[0].State == PlannedFileDispositionState.Completed,
            "Hash-verified startup reconciliation must complete without moving twice.");

        var impossible = current.Deliveries[0] with
        {
            State = PlannedDeliveryState.Delivered,
            RemoteReceiptReference = null
        };
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.Validate(current with
            {
                Deliveries = [impossible]
            }), "A state label without its required durable evidence must be rejected.");
    }

    private static void AssertOutboxPersistenceAndRecovery(string testRoot)
    {
        var root = Path.Combine(testRoot, "outbox-roundtrip");
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var empty = store.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult();
        var plan = Guid.NewGuid();
        var route = RouteReference(Guid.NewGuid(), Guid.NewGuid(), "Persisted route");
        var delivery = Delivery(plan, "clip-persisted", route, 'e');
        var planned = RoutingOutboxModel.AppendPlan(empty, plan, [delivery], [], At(1));
        _ = store.SaveAsync(planned, empty.Generation).GetAwaiter().GetResult();
        var loaded = store.Load();
        Assert(loaded.LoadedFromDisk && loaded.Document == planned,
            "The durable outbox must retain structural equality after JSON round-trip.");

        var sending = RoutingOutboxModel.StartDelivery(
            planned, delivery.DeliveryId, Guid.NewGuid(), At(2));
        _ = store.SaveAsync(sending, planned.Generation).GetAwaiter().GetResult();
        var recovered = store.LoadAndRecoverAsync(At(3)).GetAwaiter().GetResult();
        Assert(recovered.State(delivery.DeliveryId) == PlannedDeliveryState.DeliveryUnknown &&
               store.Load().Document == recovered,
            "Startup must durably persist conservative outbox recovery before returning.");
        Assert(!Directory.EnumerateFiles(root, ".outbox.json.*.tmp").Any(),
            "Atomic outbox saves must clean their exact owned temporary file.");
    }

    private static void AssertAncestorReparsePointsAreRejected(string testRoot)
    {
        var root = Path.Combine(testRoot, "reparse-parent");
        var target = Path.Combine(root, "target");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);
        try
        {
            var store = new RoutingSnapshotStore(Path.Combine(junction, "nested", "routes.json"));
            Assert(store.Load().Status == RoutingDocumentLoadStatus.Unavailable,
                "Loading through an ancestor junction must be refused before reporting Missing.");
            AssertThrows<InvalidDataException>(() => store.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult(),
                "Saving through an ancestor junction must be refused.");
        }
        finally
        {
            Directory.Delete(junction);
        }
        Assert(!Directory.Exists(Path.Combine(target, "nested")),
            "Rejecting a junctioned ancestor must happen before creating target content.");
    }

    private static void CreateJunction(string junction, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the junction helper.");
        Assert(process.WaitForExit(5000) && process.ExitCode == 0,
            "The mandatory routing ancestor-junction test could not be prepared.");
    }

    private static RoutingCondition Condition(
        RoutingConditionField field, RoutingConditionOperator op, string value) =>
        new(Guid.NewGuid(), field, op, value);

    private static RoutingAction DeliveryAction(
        Guid actionId,
        string connectionId,
        RoutingOutputKind output = RoutingOutputKind.Landscape,
        RoutingMissingOutputBehavior missing = RoutingMissingOutputBehavior.NeedsAttention,
        RoutingDeliveryMode mode = RoutingDeliveryMode.Automatic) =>
        new(actionId, true, RoutingActionKind.Deliver, RoutingDestinationKind.Discord,
            connectionId, output, missing, mode, null, Settings());

    private static RoutingAction FileAction(Guid actionId, RoutingLibraryArea area) =>
        new(actionId, true, RoutingActionKind.FileIntoLibrary, null, null, null, null,
            RoutingDeliveryMode.Automatic, area, null);

    private static PlannedDelivery Delivery(
        Guid planId, string clipId, RoutingRouteSnapshotReference route, char hash) =>
        RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, clipId, route, RoutingDestinationKind.Discord,
            "discord.friends", Output(clipId, RoutingOutputKind.Original, hash),
            Output(clipId, RoutingOutputKind.Original, hash),
            RoutingMissingOutputBehavior.NeedsAttention,
            RoutingDeliveryMode.Automatic, Settings(), true, null, At(0));

    private static RoutingRouteSnapshotReference RouteReference(
        Guid routeId, Guid actionId, string name, int priority = 0, int order = 0) =>
        new(1, routeId, 1, name, actionId, priority, order);

    private static RoutingOutputReference Output(
        string clipId, RoutingOutputKind kind, char hash) => new(clipId, kind, Hash(hash));

    private static string Hash(char value) => new(value, 64);
    private static RoutingDeliverySettings Settings() =>
        new(null, null, null, RoutingVisibility.Unspecified, false);
    private static DateTimeOffset At(int seconds) =>
        new(2026, 8, 27, 12, 0, seconds, TimeSpan.Zero);

    private static PlannedDeliveryState State(this RoutingOutboxDocument document, Guid id) =>
        document.Deliveries.Single(item => item.DeliveryId == id).State;

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
}
