using ClipsToDiscord;

internal static class RoutingEvaluatorTests
{
    internal static void Run(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        AssertEveryConditionAndAndSemantics();
        AssertTriggerMatrixFallbackAndNoFeedbackLoops();
        AssertOverlapsOrderingAndDisabledActions();
        AssertDuplicateWinnerAndDeliverTwice();
        AssertArtifactReadinessAndMissingPolicies(testRoot);
        AssertImmediateResolutionEvidenceIsExact();
        AssertAtomicFlagAndDuplicateSuppressionAreGuarded();
        AssertOrdinaryAppendAndIdempotencyRemainStrict();
        AssertNoOpDecisionAndRawStoreBoundary(testRoot);
        AssertBothFallbackCollisionDirections();
        AssertCrossOutputDeliverTwiceImmediateAndLater();
        AssertDispositionPrecedenceAndDependencies();
        AssertSkippedDependenciesSettleUploadedDisposition();
        AssertFrozenDeterministicProposal();
        AssertBadInputsAreRejected();
    }

    private static void AssertEveryConditionAndAndSemantics()
    {
        var expected = new List<Guid>();
        var routes = new List<RoutingRoute>();
        AddMatching(RoutingConditionField.Game, RoutingConditionOperator.Equals, "battlefield 6");
        AddMatching(RoutingConditionField.Game, RoutingConditionOperator.DoesNotEqual, "DUSKFADE");
        AddMatching(RoutingConditionField.Game, RoutingConditionOperator.Contains, "FIELD");
        AddMatching(RoutingConditionField.ClipSource, RoutingConditionOperator.Equals, "clipCordCapture");
        AddMatching(RoutingConditionField.ClipSource, RoutingConditionOperator.DoesNotEqual, "watchedFolder");
        AddMatching(RoutingConditionField.ReactionCamera, RoutingConditionOperator.Equals, "true");
        AddMatching(RoutingConditionField.ReactionCamera, RoutingConditionOperator.DoesNotEqual, "false");
        AddMatching(RoutingConditionField.CaptureType, RoutingConditionOperator.Equals, "instantReplay");
        AddMatching(RoutingConditionField.CaptureType, RoutingConditionOperator.DoesNotEqual, "manualRecording");
        AddMatching(RoutingConditionField.Duration, RoutingConditionOperator.GreaterThanOrEqual, "3000");
        AddMatching(RoutingConditionField.Duration, RoutingConditionOperator.LessThanOrEqual, "3000");

        var failedAnd = Route(
            routes.Count,
            RoutingTriggerKind.InstantReplay,
            [
                Condition(RoutingConditionField.Game, RoutingConditionOperator.Contains, "Battle"),
                Condition(RoutingConditionField.Duration, RoutingConditionOperator.GreaterThanOrEqual, "3001")
            ],
            [Delivery("discord.and-must-fail")]);
        routes.Add(failedAnd);

        var proposal = Evaluate(Snapshot(routes), CaptureFacts(duration: 3000));
        Assert(proposal.MatchedRouteIds.SequenceEqual(expected),
            "Every approved condition/operator must use AND-only matching with case-insensitive named values.");
        Assert(!proposal.MatchedRouteIds.Contains(failedAnd.RouteId),
            "A route with one false condition must not match when another condition is true.");

        void AddMatching(
            RoutingConditionField field,
            RoutingConditionOperator comparison,
            string value)
        {
            var priority = routes.Count;
            var route = Route(
                priority,
                RoutingTriggerKind.InstantReplay,
                [Condition(field, comparison, value)],
                [Delivery($"discord.condition-{priority}")]);
            routes.Add(route);
            expected.Add(route.RouteId);
        }
    }

    private static void AssertTriggerMatrixFallbackAndNoFeedbackLoops()
    {
        var instant = Route(0, RoutingTriggerKind.InstantReplay, [], [Delivery("discord.instant")]);
        var manual = Route(1, RoutingTriggerKind.ManualRecording, [], [Delivery("discord.manual")]);
        var watched = Route(2, RoutingTriggerKind.WatchedFolder, [], [Delivery("discord.watched")]);
        var any = Route(
            3,
            RoutingTriggerKind.AnyNewSourceClip,
            [Condition(RoutingConditionField.Game, RoutingConditionOperator.Equals, "Battlefield 6")],
            [Delivery("discord.any")]);
        var fallback = Route(
            4,
            RoutingTriggerKind.AnyNewSourceClip,
            [],
            [File(RoutingLibraryArea.LocalOnly)],
            RoutingRouteKind.Fallback);
        var snapshot = Snapshot([instant, manual, watched, any, fallback]);

        Assert(Evaluate(snapshot, CaptureFacts()).MatchedRouteIds.SequenceEqual([instant.RouteId, any.RouteId]),
            "Instant Replay and matching Any-new-source routes must both run.");
        Assert(Evaluate(snapshot, CaptureFacts(
                trigger: RoutingTriggerKind.ManualRecording,
                captureType: RoutingCaptureType.ManualRecording))
            .MatchedRouteIds.SequenceEqual([manual.RouteId, any.RouteId]),
            "Manual Recording must not be mistaken for Instant Replay.");
        Assert(Evaluate(snapshot, CaptureFacts(
                source: RoutingClipSource.WatchedFolder,
                trigger: RoutingTriggerKind.WatchedFolder,
                captureType: null))
            .MatchedRouteIds.SequenceEqual([watched.RouteId, any.RouteId]),
            "Watched-folder arrivals must match their own trigger plus Any new source clip.");
        Assert(Evaluate(snapshot, CaptureFacts(
                source: RoutingClipSource.ManualImport,
                trigger: RoutingTriggerKind.AnyNewSourceClip,
                captureType: null))
            .MatchedRouteIds.SequenceEqual([any.RouteId]),
            "Manual imports must enter only through Any new source clip.");

        var fallbackProposal = Evaluate(snapshot, CaptureFacts(
            source: RoutingClipSource.ManualImport,
            trigger: RoutingTriggerKind.AnyNewSourceClip,
            captureType: null,
            game: "Other game"));
        Assert(fallbackProposal.MatchedRouteIds.SequenceEqual([fallback.RouteId]) &&
               fallbackProposal.FileDisposition?.LibraryArea == RoutingLibraryArea.LocalOnly,
            "Fallback must run only when no enabled specific route matches.");

        foreach (var eventKind in new[]
                 {
                     RoutingEvaluationEventKind.DerivedRendition,
                     RoutingEvaluationEventKind.FileDisposition
                 })
        {
            var ignored = Evaluate(snapshot, CaptureFacts(eventKind: eventKind));
            Assert(!ignored.HasWork && ignored.MatchedRouteIds.Count == 0,
                "Derived renditions and file actions must never feed Any new source clip.");
        }
    }

    private static void AssertOverlapsOrderingAndDisabledActions()
    {
        var lowPriority = Route(
            20,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("discord.low-priority")]);
        var highPriority = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [
                Delivery("discord.disabled", enabled: false),
                Delivery("discord.high-first"),
                Delivery("discord.high-second")
            ]);
        var middlePriority = Route(
            10,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("discord.middle")]);
        var snapshot = Snapshot([lowPriority, middlePriority, highPriority]);

        var proposal = Evaluate(snapshot, CaptureFacts());
        Assert(proposal.MatchedRouteIds.SequenceEqual(
                [highPriority.RouteId, middlePriority.RouteId, lowPriority.RouteId]),
            "Every matching specific route must run in priority order, not serialized list order.");
        Assert(proposal.Deliveries.Select(item => item.ConnectionId).SequenceEqual(
                ["discord.high-first", "discord.high-second", "discord.middle", "discord.low-priority"]),
            "Enabled actions must retain route/action ordering and disabled actions must disappear.");
        Assert(proposal.Deliveries.All(item => item.ConnectionId != "discord.disabled"),
            "A disabled action must not produce durable work.");
    }

    private static void AssertDuplicateWinnerAndDeliverTwice()
    {
        var high = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("shared.connection", destination: RoutingDestinationKind.Discord)]);
        var low = Route(
            1,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("shared.connection", destination: RoutingDestinationKind.YouTube)]);
        var snapshot = Snapshot([low, high]);
        var facts = CaptureFacts();
        var unapproved = Evaluate(snapshot, facts);
        Assert(unapproved.Deliveries.Count == 1 &&
               unapproved.Deliveries[0].Route.RouteId == high.RouteId &&
               unapproved.Deliveries[0].Destination == RoutingDestinationKind.Discord,
            "The duplicate key must exclude provider metadata and keep the higher route.");

        var original = facts.Outputs.Single(item =>
            item.Output.Kind == RoutingOutputKind.Original).Output;
        var proof = new IntentionalDuplicateProvenance(
            Guid.NewGuid(),
            IntentionalDuplicateDecision.UserConfirmedDeliverTwice,
            new RoutingDeliveryKey("shared.connection", original),
            high.RouteId,
            low.RouteId,
            At(0));
        var approved = RoutingEvaluator.CreatePlan(
            snapshot, facts, Guid.NewGuid(), [proof], At(1));
        Assert(approved.Deliveries.Count == 2 &&
               approved.Deliveries.All(item => item.IntentionalDuplicate == proof) &&
               approved.Deliveries.Select(item => item.ProviderIdempotencyKey).Distinct().Count() == 2,
            "Exact persisted deliver-twice authorization must produce two independent delivery ids.");

        var wrongPair = proof with { SecondRouteId = Guid.NewGuid() };
        var wrongPairProposal = RoutingEvaluator.CreatePlan(
            snapshot, facts, Guid.NewGuid(), [wrongPair], At(1));
        Assert(wrongPairProposal.Deliveries.Count == 1 &&
               wrongPairProposal.Deliveries[0].Route.RouteId == high.RouteId,
            "Authorization for a different exact route pair must not disable deduplication.");
    }

    private static void AssertArtifactReadinessAndMissingPolicies(string testRoot)
    {
        var route = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [
                Delivery("discord.original", RoutingOutputKind.Original),
                Delivery("youtube.pending", RoutingOutputKind.Landscape,
                    RoutingMissingOutputBehavior.NeedsAttention,
                    RoutingDeliveryMode.Approval,
                    destination: RoutingDestinationKind.YouTube),
                Delivery("tiktok.fallback", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.UseOriginal,
                    destination: RoutingDestinationKind.TikTok),
                Delivery("tiktok.skip", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.Skip,
                    destination: RoutingDestinationKind.TikTok),
                Delivery("tiktok.attention", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.NeedsAttention,
                    destination: RoutingDestinationKind.TikTok)
            ]);
        var facts = CaptureFacts(outputs:
        [
            Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
            Output("clip-evaluator", RoutingOutputKind.Landscape, 'b', RoutingOutputAvailability.Pending),
            Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
        ]);
        var proposal = Evaluate(Snapshot([route]), facts);
        var byConnection = proposal.Deliveries.ToDictionary(item => item.ConnectionId);
        Assert(byConnection["discord.original"].State == PlannedDeliveryState.Ready,
            "A ready original must be independently deliverable.");
        Assert(byConnection["youtube.pending"].State == PlannedDeliveryState.WaitingForArtifact &&
               byConnection["youtube.pending"].Mode == RoutingDeliveryMode.Approval,
            "A pending landscape must wait for only that artifact before approval.");
        Assert(byConnection["tiktok.fallback"].State == PlannedDeliveryState.Ready &&
               byConnection["tiktok.fallback"].ArtifactOutcome ==
                   RoutingMissingArtifactOutcome.OriginalFallback &&
               byConnection["tiktok.fallback"].Output.Kind == RoutingOutputKind.Original,
            "Use original must be resolved immediately for a permanently missing rendition.");
        Assert(byConnection["tiktok.skip"].State == PlannedDeliveryState.Skipped &&
               byConnection["tiktok.skip"].ArtifactOutcome == RoutingMissingArtifactOutcome.Skipped,
            "Skip must be resolved immediately for a permanently missing rendition.");
        Assert(byConnection["tiktok.attention"].State == PlannedDeliveryState.NeedsAttention &&
               byConnection["tiktok.attention"].ArtifactOutcome ==
                   RoutingMissingArtifactOutcome.NeedsAttention,
            "Needs attention must be resolved immediately for a permanently missing rendition.");
        Assert(proposal.RequiresAtomicResolvedAppend &&
               proposal.ImmediateMissingResolutions.Count == 3 &&
               proposal.ImmediateMissingResolutions.All(item =>
                   item.RequestedOutput.Kind == RoutingOutputKind.Portrait &&
                   item.ErrorCode == "portrait-render-failed"),
            "The proposal must flag exact missing-output states for one-generation persistence.");

        var persistenceRoot = Path.Combine(testRoot, "atomic-missing-roundtrip");
        Directory.CreateDirectory(persistenceRoot);
        var store = new RoutingOutboxStore(
            Path.Combine(persistenceRoot, RoutingOutboxStore.FileName));
        var empty = store.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult();
        var appended = RoutingOutboxModel.AppendEvaluatedPlan(empty, proposal, At(2));
        Assert(appended.Generation == empty.Generation + 1 &&
               appended.Deliveries.Count == 5 &&
               appended.Deliveries.Any(item =>
                   item.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback) &&
               appended.Deliveries.Any(item =>
                   item.ArtifactOutcome == RoutingMissingArtifactOutcome.Skipped) &&
               appended.Deliveries.Any(item =>
                   item.ArtifactOutcome == RoutingMissingArtifactOutcome.NeedsAttention),
            "Use-original, skip, and needs-attention resolutions must append in one outbox generation.");
        _ = store.SaveAsync(appended, empty.Generation).GetAwaiter().GetResult();
        var reloaded = store.Load();
        Assert(reloaded.LoadedFromDisk && reloaded.Document == appended,
            "An atomically resolved evaluated plan must survive persistence round-trip exactly.");

        var absentPortrait = Evaluate(
            Snapshot([Route(0, RoutingTriggerKind.InstantReplay, [],
                [Delivery("tiktok.unproduced", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.UseOriginal,
                    destination: RoutingDestinationKind.TikTok)])]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready)
            ]));
        var absentResolution = absentPortrait.ImmediateMissingResolutions.Single();
        var logicalOne = absentResolution.RequestedOutput;
        var logicalTwo = RoutingEvaluator.CreateLogicalOutputReference(
            "clip-evaluator", Hash('a'), RoutingOutputKind.Portrait);
        Assert(logicalOne == logicalTwo && logicalOne.Revision.Length == 64 &&
               logicalOne.Revision.All(Uri.IsHexDigit) &&
               absentResolution.ErrorCode == "output-not-produced",
            "An output disabled in Capture must get stable logical identity and immediate fallback, not fake readiness.");
    }

    private static void AssertImmediateResolutionEvidenceIsExact()
    {
        var route = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("tiktok.evidence", RoutingOutputKind.Portrait,
                RoutingMissingOutputBehavior.UseOriginal,
                destination: RoutingDestinationKind.TikTok)]);
        var proposal = Evaluate(
            Snapshot([route]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]));
        var evidence = proposal.ImmediateMissingResolutions.Single();
        var current = RoutingOutboxModel.CreateEmpty(At(0));

        AssertEvidenceRejected(evidence with { DeliveryId = Guid.NewGuid() }, "delivery id");
        AssertEvidenceRejected(evidence with
        {
            RequestedOutput = new RoutingOutputReference(
                evidence.RequestedOutput.ClipId,
                evidence.RequestedOutput.Kind,
                Hash('d'))
        }, "requested output");
        AssertEvidenceRejected(evidence with
        {
            Outcome = RoutingMissingArtifactOutcome.NeedsAttention
        }, "outcome");
        AssertEvidenceRejected(evidence with
        {
            EffectiveOutput = new RoutingOutputReference(
                evidence.EffectiveOutput.ClipId,
                evidence.EffectiveOutput.Kind,
                Hash('e'))
        }, "effective output");
        AssertEvidenceRejected(evidence with
        {
            State = PlannedDeliveryState.NeedsAttention
        }, "state");
        AssertEvidenceRejected(evidence with { ErrorCode = "different-safe-error" }, "error code");

        void AssertEvidenceRejected(
            RoutingImmediateMissingResolution changed,
            string field)
        {
            AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendEvaluatedPlan(
                    current,
                    proposal with { ImmediateMissingResolutions = [changed] },
                    At(2)),
                $"Atomic append must reject mismatched immediate-resolution {field} evidence.");
        }
    }

    private static void AssertAtomicFlagAndDuplicateSuppressionAreGuarded()
    {
        var missingRoute = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("tiktok.flag", RoutingOutputKind.Portrait,
                RoutingMissingOutputBehavior.Skip,
                destination: RoutingDestinationKind.TikTok)]);
        var missingProposal = Evaluate(
            Snapshot([missingRoute]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendEvaluatedPlan(
                RoutingOutboxModel.CreateEmpty(At(0)),
                missingProposal with { RequiresAtomicResolvedAppend = false },
                At(2)),
            "Resolved evidence with a false atomic marker must be rejected.");

        var ordinaryProposal = Evaluate(
            Snapshot([Route(0, RoutingTriggerKind.InstantReplay, [],
                [Delivery("discord.no-resolution")])]),
            CaptureFacts());
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendEvaluatedPlan(
                RoutingOutboxModel.CreateEmpty(At(0)),
                ordinaryProposal with { RequiresAtomicResolvedAppend = true },
                At(2)),
            "An atomic marker without resolved evidence must be rejected.");

        var high = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("shared.fallback", RoutingOutputKind.Original)]);
        var low = Route(
            1,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("shared.fallback", RoutingOutputKind.Portrait,
                RoutingMissingOutputBehavior.UseOriginal,
                destination: RoutingDestinationKind.TikTok)]);
        var fallbackProposal = Evaluate(
            Snapshot([low, high]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]));
        var suppressed = fallbackProposal.Deliveries.Single(item =>
            item.ArtifactOutcome == RoutingMissingArtifactOutcome.DuplicateSuppressed);
        var survivor = fallbackProposal.Deliveries.Single(item =>
            item.ArtifactOutcome != RoutingMissingArtifactOutcome.DuplicateSuppressed);
        Assert(survivor.Route.Priority < suppressed.Route.Priority &&
               survivor.DeliveryKey == suppressed.DeliveryKey,
            "The evaluator fixture must contain a real higher-precedence fallback-key survivor.");
        var appended = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), fallbackProposal, At(2));
        Assert(appended.Deliveries.Count == 2,
            "A valid duplicate-suppressed fallback with its survivor must append atomically.");
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendEvaluatedPlan(
                RoutingOutboxModel.CreateEmpty(At(0)),
                fallbackProposal with { Deliveries = [suppressed] },
                At(2)),
            "A duplicate-suppressed fallback without its higher-precedence survivor must be rejected.");
    }

    private static void AssertOrdinaryAppendAndIdempotencyRemainStrict()
    {
        var route = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("tiktok.atomic-only", RoutingOutputKind.Portrait,
                RoutingMissingOutputBehavior.NeedsAttention,
                destination: RoutingDestinationKind.TikTok)]);
        var snapshot = Snapshot([route]);
        var facts = CaptureFacts(outputs:
        [
            Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
            Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
        ]);
        var planId = Guid.NewGuid();
        var proposal = RoutingEvaluator.CreatePlan(snapshot, facts, planId, [], At(1));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendPlan(
                RoutingOutboxModel.CreateEmpty(At(0)),
                proposal.PlanId,
                proposal.Deliveries,
                [],
                At(2)),
            "Ordinary AppendPlan must continue rejecting pre-resolved additions.");

        var empty = RoutingOutboxModel.CreateEmpty(At(0));
        var appended = RoutingOutboxModel.AppendEvaluatedPlan(empty, proposal, At(2));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendEvaluatedPlan(
                appended, proposal, At(3)),
            "The same evaluated plan must not be appended twice.");
        var secondPlan = RoutingEvaluator.CreatePlan(
            snapshot, facts, Guid.NewGuid(), [], At(3));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendEvaluatedPlan(
                appended, secondPlan, At(4)),
            "A source clip must not receive a second plan under a different plan id.");

        var recreated = RoutingEvaluator.CreatePlan(snapshot, facts, planId, [], At(1));
        Assert(recreated.SourceClipId == proposal.SourceClipId &&
               recreated.Deliveries.Single().SourceClipId == proposal.SourceClipId &&
               recreated.Deliveries.Single().DeliveryId == proposal.Deliveries.Single().DeliveryId &&
               recreated.Deliveries.Single().ProviderIdempotencyKey ==
                   proposal.Deliveries.Single().ProviderIdempotencyKey,
            "Re-evaluating the same frozen plan id must retain source identity and provider idempotency.");
    }

    private static void AssertNoOpDecisionAndRawStoreBoundary(string testRoot)
    {
        var noRoutes = Snapshot([]);
        var noOp = RoutingEvaluator.CreatePlan(
            noRoutes, CaptureFacts(), Guid.NewGuid(), [], At(1));
        Assert(!noOp.HasWork && noOp.MatchedRouteIds.Count == 0,
            "An unmatched source must produce an explicit no-work proposal.");
        var root = Path.Combine(testRoot, "no-op-plan-header");
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var empty = store.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult();
        var appended = RoutingOutboxModel.AppendEvaluatedPlan(empty, noOp, At(1));
        Assert(appended.Generation == empty.Generation + 1 &&
               appended.Plans.Count == 1 &&
               appended.Plans[0].PlanId == noOp.PlanId &&
               appended.Plans[0].SourceClipId == noOp.SourceClipId &&
               appended.Plans[0].RoutingGeneration == noOp.RoutingGeneration &&
               appended.Plans[0].MatchedRouteIds.Count == 0 &&
               appended.Deliveries.Count == 0 && appended.FileDispositions.Count == 0,
            "A no-match evaluation must persist its exact plan/source/generation decision.");
        _ = store.SaveAsync(appended, empty.Generation).GetAwaiter().GetResult();
        Assert(store.Load().Document == appended,
            "A no-op plan header must survive persistence and structural equality.");
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.AppendEvaluatedPlan(
                appended,
                RoutingEvaluator.CreatePlan(noRoutes, CaptureFacts(), Guid.NewGuid(), [], At(2)),
                At(2)),
            "A persisted no-op source decision must prevent later re-evaluation.");

        var missingRoute = Route(0, RoutingTriggerKind.InstantReplay, [],
            [Delivery("tiktok.raw-bypass", RoutingOutputKind.Portrait,
                RoutingMissingOutputBehavior.Skip, destination: RoutingDestinationKind.TikTok)]);
        var resolved = RoutingEvaluator.CreatePlan(
            Snapshot([missingRoute]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]),
            Guid.NewGuid(), [], At(2));
        var bypassRoot = Path.Combine(testRoot, "raw-store-bypass");
        Directory.CreateDirectory(bypassRoot);
        var bypassStore = new RoutingOutboxStore(
            Path.Combine(bypassRoot, RoutingOutboxStore.FileName));
        var bypassEmpty = bypassStore.LoadOrCreateAsync(At(0)).GetAwaiter().GetResult();
        var incompleteHeader = new RoutingPlanDecision(
            resolved.PlanId, resolved.SourceClipId, resolved.RoutingGeneration,
            resolved.MatchedRouteIds, [], resolved.LatentDuplicateAuthorizations, At(2));
        var forged = bypassEmpty with
        {
            Generation = bypassEmpty.Generation + 1,
            Plans = [incompleteHeader],
            Deliveries = resolved.Deliveries,
            UpdatedUtc = At(2)
        };
        AssertThrows<InvalidDataException>(() => bypassStore.SaveAsync(
                forged, bypassEmpty.Generation).GetAwaiter().GetResult(),
            "Raw successor persistence must reject pre-resolved additions without exact header evidence.");

        var secondNoOp = RoutingEvaluator.CreatePlan(
            noRoutes,
            CaptureFacts() with { ClipId = "clip-evaluator-two", Outputs =
            [
                Output("clip-evaluator-two", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready)
            ] },
            Guid.NewGuid(), [], At(2));
        var twoAtOnce = bypassEmpty with
        {
            Generation = bypassEmpty.Generation + 1,
            Plans =
            [
                new RoutingPlanDecision(noOp.PlanId, noOp.SourceClipId, noOp.RoutingGeneration,
                    [], [], [], At(2)),
                new RoutingPlanDecision(secondNoOp.PlanId, secondNoOp.SourceClipId,
                    secondNoOp.RoutingGeneration, [], [], [], At(2))
            ],
            UpdatedUtc = At(2)
        };
        AssertThrows<InvalidDataException>(() => bypassStore.SaveAsync(
                twoAtOnce, bypassEmpty.Generation).GetAwaiter().GetResult(),
            "One outbox generation must never append two source decisions.");
    }

    private static void AssertBothFallbackCollisionDirections()
    {
        foreach (var loserPolicy in new[]
                 {
                     RoutingMissingOutputBehavior.Skip,
                     RoutingMissingOutputBehavior.NeedsAttention
                 })
        {
            var missingHigh = Route(0, RoutingTriggerKind.InstantReplay, [],
                [Delivery("shared.reverse", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.UseOriginal,
                    destination: RoutingDestinationKind.TikTok)]);
            var readyLow = Route(1, RoutingTriggerKind.InstantReplay, [],
                [Delivery("shared.reverse", RoutingOutputKind.Original, loserPolicy)]);
            var proposal = RoutingEvaluator.CreatePlan(
                Snapshot([readyLow, missingHigh]),
                CaptureFacts(outputs:
                [
                    Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                    Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                        RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
                ]),
                Guid.NewGuid(), [], At(1));
            var winner = proposal.Deliveries.Single(item => item.Route.RouteId == missingHigh.RouteId);
            var loser = proposal.Deliveries.Single(item => item.Route.RouteId == readyLow.RouteId);
            Assert(winner.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
                   loser.ArtifactOutcome == RoutingMissingArtifactOutcome.DuplicateSuppressed &&
                   loser.OnMissingOutput == loserPolicy &&
                   proposal.ImmediateMissingResolutions.Select(item => item.DeliveryId).ToHashSet()
                       .SetEquals(proposal.Deliveries.Select(item => item.DeliveryId)),
                "A higher missing-output fallback must suppress a lower ready original regardless of the loser's policy.");
            _ = RoutingOutboxModel.AppendEvaluatedPlan(
                RoutingOutboxModel.CreateEmpty(At(0)), proposal, At(1));
        }

        var readyHigh = Route(0, RoutingTriggerKind.InstantReplay, [],
            [Delivery("shared.forward", RoutingOutputKind.Original,
                RoutingMissingOutputBehavior.NeedsAttention)]);
        var missingLow = Route(1, RoutingTriggerKind.InstantReplay, [],
            [Delivery("shared.forward", RoutingOutputKind.Portrait,
                RoutingMissingOutputBehavior.UseOriginal,
                destination: RoutingDestinationKind.TikTok)]);
        var forward = RoutingEvaluator.CreatePlan(
            Snapshot([missingLow, readyHigh]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]),
            Guid.NewGuid(), [], At(1));
        Assert(forward.Deliveries.Single(item => item.Route.RouteId == readyHigh.RouteId)
                   .ArtifactOutcome == RoutingMissingArtifactOutcome.RequestedOutput &&
               forward.Deliveries.Single(item => item.Route.RouteId == missingLow.RouteId)
                   .ArtifactOutcome == RoutingMissingArtifactOutcome.DuplicateSuppressed,
            "A higher ready original must win the forward fallback collision.");
    }

    private static void AssertCrossOutputDeliverTwiceImmediateAndLater()
    {
        var landscape = Route(0, RoutingTriggerKind.InstantReplay, [],
            [Delivery("shared.cross-output", RoutingOutputKind.Landscape,
                RoutingMissingOutputBehavior.UseOriginal,
                destination: RoutingDestinationKind.YouTube)]);
        var portrait = Route(1, RoutingTriggerKind.InstantReplay, [],
            [Delivery("shared.cross-output", RoutingOutputKind.Portrait,
                RoutingMissingOutputBehavior.UseOriginal,
                destination: RoutingDestinationKind.TikTok)]);
        var snapshot = Snapshot([portrait, landscape]);
        var original = new RoutingOutputReference(
            "clip-evaluator", RoutingOutputKind.Original, Hash('a'));
        var proof = new IntentionalDuplicateProvenance(
            Guid.NewGuid(), IntentionalDuplicateDecision.UserConfirmedDeliverTwice,
            new RoutingDeliveryKey("shared.cross-output", original),
            landscape.RouteId, portrait.RouteId, At(0));

        var unauthorizedPending = RoutingEvaluator.CreatePlan(
            snapshot,
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Landscape, 'b', RoutingOutputAvailability.Pending),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c', RoutingOutputAvailability.Pending)
            ]),
            Guid.NewGuid(), [], At(1));
        var unauthorizedCurrent = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), unauthorizedPending, At(1));
        var forgedProof = proof with { GroupId = Guid.NewGuid(), ConfirmedUtc = At(2) };
        var forgedCandidate = unauthorizedCurrent with
        {
            Generation = unauthorizedCurrent.Generation + 1,
            Deliveries = unauthorizedCurrent.Deliveries.Select(delivery => delivery with
            {
                Output = original,
                ArtifactOutcome = RoutingMissingArtifactOutcome.OriginalFallback,
                ArtifactErrorCode = "forged-render-failure",
                State = PlannedDeliveryState.Ready,
                IntentionalDuplicate = forgedProof,
                UpdatedUtc = At(2)
            }).ToArray(),
            UpdatedUtc = At(2)
        };
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.ValidateSuccessor(
                unauthorizedCurrent, forgedCandidate),
            "A raw successor cannot forge deliver-twice proof absent from the immutable plan decision.");

        var immediate = RoutingEvaluator.CreatePlan(
            snapshot,
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Landscape, 'b',
                    RoutingOutputAvailability.PermanentlyMissing, "render-failed"),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "render-failed")
            ]),
            Guid.NewGuid(), [proof], At(1));
        Assert(immediate.Deliveries.Count == 2 &&
               immediate.Deliveries.All(item =>
                   item.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
                   item.IntentionalDuplicate?.GroupId == proof.GroupId) &&
               immediate.LatentDuplicateAuthorizations.Single().GroupId == proof.GroupId,
            "Two different missing outputs authorized for the final original key must both survive immediately.");
        _ = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), immediate, At(1));

        var pending = RoutingEvaluator.CreatePlan(
            snapshot,
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Landscape, 'b', RoutingOutputAvailability.Pending),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c', RoutingOutputAvailability.Pending)
            ]),
            Guid.NewGuid(), [proof], At(1));
        var current = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), pending, At(1));
        var landscapeRef = pending.Deliveries.Single(item =>
            item.RequestedOutput.Kind == RoutingOutputKind.Landscape).RequestedOutput;
        var portraitRef = pending.Deliveries.Single(item =>
            item.RequestedOutput.Kind == RoutingOutputKind.Portrait).RequestedOutput;
        current = RoutingOutboxModel.ResolveMissingArtifacts(
            current, pending.PlanId, [landscapeRef], "landscape-failed", At(2));
        current = RoutingOutboxModel.ResolveMissingArtifacts(
            current, pending.PlanId, [portraitRef], "portrait-failed", At(3));
        Assert(current.Deliveries.Count(item => item.PlanId == pending.PlanId) == 2 &&
               current.Deliveries.Where(item => item.PlanId == pending.PlanId).All(item =>
                   item.Output == original &&
                   item.ArtifactOutcome == RoutingMissingArtifactOutcome.OriginalFallback &&
                   item.IntentionalDuplicate?.GroupId == proof.GroupId),
            "Latent exact authorization must survive the plan header and activate after later fallbacks converge.");
    }

    private static void AssertDispositionPrecedenceAndDependencies()
    {
        var high = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("discord.high"), File(RoutingLibraryArea.Uploaded)]);
        var low = Route(
            1,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("youtube.low", destination: RoutingDestinationKind.YouTube),
             File(RoutingLibraryArea.LocalOnly)]);
        var proposal = Evaluate(Snapshot([low, high]), CaptureFacts());
        Assert(proposal.FileDisposition is { LibraryArea: RoutingLibraryArea.Uploaded } disposition &&
               disposition.Route.RouteId == high.RouteId &&
               disposition.PrerequisiteDeliveryIds.ToHashSet()
                   .SetEquals(proposal.Deliveries.Select(item => item.DeliveryId)),
            "The highest-precedence terminal action must win and depend on all surviving deliveries.");

        var localFirst = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [File(RoutingLibraryArea.LocalOnly)]);
        var uploadedSecond = Route(
            1,
            RoutingTriggerKind.InstantReplay,
            [],
            [Delivery("discord.conflict"), File(RoutingLibraryArea.Uploaded)]);
        AssertThrows<InvalidDataException>(() => Evaluate(
                Snapshot([uploadedSecond, localFirst]), CaptureFacts()),
            "A higher Local-only disposition must reject rather than silently coexist with external delivery.");

        var noDisposition = Evaluate(
            Snapshot([Route(0, RoutingTriggerKind.InstantReplay, [], [Delivery("discord.no-file")])]),
            CaptureFacts());
        Assert(noDisposition.FileDisposition is null,
            "A route without File into Library must not invent a source disposition.");
    }

    private static void AssertSkippedDependenciesSettleUploadedDisposition()
    {
        var configuredSkip = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [
                Delivery("discord.skip-control"),
                Delivery("tiktok.configured-skip", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.Skip,
                    destination: RoutingDestinationKind.TikTok),
                File(RoutingLibraryArea.Uploaded)
            ]);
        var configuredProposal = RoutingEvaluator.CreatePlan(
            Snapshot([configuredSkip]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]),
            Guid.NewGuid(), [], At(1));
        var configuredCurrent = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), configuredProposal, At(1));
        var configuredDelivered = configuredCurrent.Deliveries.Single(item =>
            item.ConnectionId == "discord.skip-control");
        var configuredAttempt = Guid.NewGuid();
        configuredCurrent = RoutingOutboxModel.StartDelivery(
            configuredCurrent, configuredDelivered.DeliveryId, configuredAttempt, At(2));
        configuredCurrent = RoutingOutboxModel.CompleteDelivery(
            configuredCurrent, configuredDelivered.DeliveryId, configuredAttempt,
            "discord.skip-receipt", At(3));
        configuredCurrent = RoutingOutboxModel.RefreshFileDisposition(
            configuredCurrent, configuredProposal.FileDisposition!.DispositionId, At(4));
        Assert(configuredCurrent.FileDispositions.Single().State ==
                   PlannedFileDispositionState.Ready,
            "A configured missing-output Skip must settle after another sibling is delivered.");

        var attentionSkip = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [
                Delivery("discord.attention-control"),
                Delivery("tiktok.attention-skip", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.NeedsAttention,
                    destination: RoutingDestinationKind.TikTok),
                File(RoutingLibraryArea.Uploaded)
            ]);
        var attentionProposal = RoutingEvaluator.CreatePlan(
            Snapshot([attentionSkip]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]),
            Guid.NewGuid(), [], At(10));
        var attentionCurrent = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(9)), attentionProposal, At(10));
        var attentionDelivered = attentionCurrent.Deliveries.Single(item =>
            item.ConnectionId == "discord.attention-control");
        var attentionAttempt = Guid.NewGuid();
        attentionCurrent = RoutingOutboxModel.StartDelivery(
            attentionCurrent, attentionDelivered.DeliveryId, attentionAttempt, At(11));
        attentionCurrent = RoutingOutboxModel.CompleteDelivery(
            attentionCurrent, attentionDelivered.DeliveryId, attentionAttempt,
            "discord.attention-receipt", At(12));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.RefreshFileDisposition(
                attentionCurrent, attentionProposal.FileDisposition!.DispositionId, At(13)),
            "An unresolved needs-attention sibling must continue blocking uploaded filing.");
        var attentionDelivery = attentionCurrent.Deliveries.Single(item =>
            item.ConnectionId == "tiktok.attention-skip");
        attentionCurrent = RoutingOutboxModel.ResolveNeedsAttention(
            attentionCurrent, attentionDelivery.DeliveryId,
            RoutingNeedsAttentionResolution.Skip, At(13));
        attentionCurrent = RoutingOutboxModel.RefreshFileDisposition(
            attentionCurrent, attentionProposal.FileDisposition!.DispositionId, At(14));
        Assert(attentionCurrent.FileDispositions.Single().State ==
                   PlannedFileDispositionState.Ready,
            "A user-confirmed Skip must settle needs-attention after another sibling is delivered.");

        var allSkippedRoute = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [
                Delivery("tiktok.all-skipped", RoutingOutputKind.Portrait,
                    RoutingMissingOutputBehavior.Skip,
                    destination: RoutingDestinationKind.TikTok),
                File(RoutingLibraryArea.Uploaded)
            ]);
        var allSkippedProposal = RoutingEvaluator.CreatePlan(
            Snapshot([allSkippedRoute]),
            CaptureFacts(outputs:
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c',
                    RoutingOutputAvailability.PermanentlyMissing, "portrait-render-failed")
            ]),
            Guid.NewGuid(), [], At(20));
        var allSkippedCurrent = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(19)), allSkippedProposal, At(20));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.RefreshFileDisposition(
                allSkippedCurrent, allSkippedProposal.FileDisposition!.DispositionId, At(21)),
            "Uploaded filing must still require at least one confirmed delivery.");

        var failureRoute = Route(
            0,
            RoutingTriggerKind.InstantReplay,
            [],
            [
                Delivery("discord.failure-control"),
                Delivery("youtube.retryable-failure", RoutingOutputKind.Landscape,
                    destination: RoutingDestinationKind.YouTube),
                File(RoutingLibraryArea.Uploaded)
            ]);
        var failureProposal = Evaluate(Snapshot([failureRoute]), CaptureFacts());
        var failureCurrent = RoutingOutboxModel.AppendEvaluatedPlan(
            RoutingOutboxModel.CreateEmpty(At(29)), failureProposal, At(30));
        var successful = failureCurrent.Deliveries.Single(item =>
            item.ConnectionId == "discord.failure-control");
        var successfulAttempt = Guid.NewGuid();
        failureCurrent = RoutingOutboxModel.StartDelivery(
            failureCurrent, successful.DeliveryId, successfulAttempt, At(31));
        failureCurrent = RoutingOutboxModel.CompleteDelivery(
            failureCurrent, successful.DeliveryId, successfulAttempt,
            "discord.failure-control-receipt", At(32));
        var retryable = failureCurrent.Deliveries.Single(item =>
            item.ConnectionId == "youtube.retryable-failure");
        var retryableAttempt = Guid.NewGuid();
        failureCurrent = RoutingOutboxModel.StartDelivery(
            failureCurrent, retryable.DeliveryId, retryableAttempt, At(33));
        failureCurrent = RoutingOutboxModel.FailDelivery(
            failureCurrent, retryable.DeliveryId, retryableAttempt,
            "provider-temporary-failure", null, At(34));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.RefreshFileDisposition(
                failureCurrent, failureProposal.FileDisposition!.DispositionId, At(35)),
            "A retryable failed sibling must continue blocking uploaded filing.");
    }

    private static void AssertFrozenDeterministicProposal()
    {
        var settings = new RoutingDeliverySettings(
            "A message", "A title", "A caption", RoutingVisibility.Unlisted, true);
        var action = Delivery("discord.frozen", settings: settings);
        var route = Route(0, RoutingTriggerKind.InstantReplay, [], [action]);
        var snapshot = Snapshot([route]);
        var facts = CaptureFacts();
        var planId = Guid.NewGuid();
        var first = RoutingEvaluator.CreatePlan(snapshot, facts, planId, [], At(1));
        var second = RoutingEvaluator.CreatePlan(snapshot, facts, planId, [], At(1));
        var firstDelivery = first.Deliveries.Single();
        var secondDelivery = second.Deliveries.Single();
        Assert(firstDelivery == secondDelivery &&
               firstDelivery.DeliveryId == secondDelivery.DeliveryId &&
               firstDelivery.Route.RoutingGeneration == snapshot.Generation &&
               firstDelivery.Route.RouteRevision == route.Revision &&
               firstDelivery.Route.RouteName == route.Name &&
               firstDelivery.Route.ActionId == action.ActionId,
            "Identical frozen inputs and plan id must produce identical plan members.");
        Assert(firstDelivery.Settings == settings &&
               !ReferenceEquals(firstDelivery.Settings, settings),
            "Resolved delivery settings must be copied into the frozen delivery snapshot.");
    }

    private static void AssertBadInputsAreRejected()
    {
        var route = Route(0, RoutingTriggerKind.InstantReplay, [], [Delivery("discord.valid")]);
        var snapshot = Snapshot([route]);
        AssertThrows<InvalidDataException>(() => RoutingEvaluator.CreatePlan(
                snapshot, CaptureFacts(), Guid.Empty, [], At(1)),
            "An empty plan id must be rejected.");
        AssertThrows<InvalidDataException>(() => Evaluate(snapshot,
                CaptureFacts() with { ClipId = "..\\escape" }),
            "A path-like clip id must not enter logical routing identity.");
        AssertThrows<InvalidDataException>(() => Evaluate(snapshot,
                CaptureFacts() with
                {
                    ArrivalTrigger = RoutingTriggerKind.ManualRecording,
                    CaptureType = RoutingCaptureType.InstantReplay
                }),
            "Inconsistent trigger/capture facts must be rejected.");
        AssertThrows<InvalidDataException>(() => Evaluate(snapshot,
                CaptureFacts(outputs:
                [
                    Output("clip-evaluator", RoutingOutputKind.Original, 'a',
                        RoutingOutputAvailability.Pending)
                ])),
            "Planning must not begin before the original is committed.");
        AssertThrows<InvalidDataException>(() => Evaluate(snapshot,
                CaptureFacts(outputs:
                [
                    Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                    Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready)
                ])),
            "Duplicate logical output kinds must be rejected.");
        AssertThrows<InvalidDataException>(() => Evaluate(snapshot,
                CaptureFacts(outputs:
                [
                    Output("another-clip", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready)
                ])),
            "An output from another clip must be rejected.");

        var badProof = new IntentionalDuplicateProvenance(
            Guid.NewGuid(), IntentionalDuplicateDecision.UserConfirmedDeliverTwice,
            new RoutingDeliveryKey("discord.valid",
                new RoutingOutputReference("another-clip", RoutingOutputKind.Original, Hash('a'))),
            Guid.NewGuid(), Guid.NewGuid(), At(0));
        AssertThrows<InvalidDataException>(() => RoutingEvaluator.CreatePlan(
                snapshot, CaptureFacts(), Guid.NewGuid(), [badProof], At(1)),
            "Duplicate authorization for another clip must be rejected even when unused.");

        var invalidRoute = route with
        {
            Conditions =
            [
                Condition(RoutingConditionField.Game,
                    (RoutingConditionOperator)999,
                    "Battlefield 6")
            ]
        };
        AssertThrows<InvalidDataException>(() => Evaluate(SnapshotUnchecked([invalidRoute]), CaptureFacts()),
            "The evaluator must validate the frozen snapshot before matching it.");
    }

    private static RoutingPlanProposal Evaluate(
        RoutingSnapshotDocument snapshot,
        RoutingClipFacts facts) =>
        RoutingEvaluator.CreatePlan(snapshot, facts, Guid.NewGuid(), [], At(1));

    private static RoutingSnapshotDocument Snapshot(IEnumerable<RoutingRoute> routes)
    {
        var snapshot = SnapshotUnchecked(routes);
        RoutingSnapshotModel.Validate(snapshot);
        return snapshot;
    }

    private static RoutingSnapshotDocument SnapshotUnchecked(IEnumerable<RoutingRoute> routes) =>
        new(
            RoutingSnapshotStore.CurrentSchemaVersion,
            19,
            routes.ToArray(),
            At(0),
            At(0));

    private static RoutingRoute Route(
        int priority,
        RoutingTriggerKind trigger,
        IReadOnlyList<RoutingCondition> conditions,
        IReadOnlyList<RoutingAction> actions,
        RoutingRouteKind kind = RoutingRouteKind.Specific) =>
        new(
            Guid.NewGuid(),
            $"Route {priority}",
            Enabled: true,
            priority,
            Revision: 3,
            RoutingRouteSource.User,
            kind,
            trigger,
            new RoutingPrepareSettings(
                Landscape: true,
                Portrait: true,
                RoutingMissingOutputBehavior.NeedsAttention),
            conditions,
            actions,
            At(0),
            At(0));

    private static RoutingCondition Condition(
        RoutingConditionField field,
        RoutingConditionOperator comparison,
        string value) =>
        new(Guid.NewGuid(), field, comparison, value);

    private static RoutingAction Delivery(
        string connectionId,
        RoutingOutputKind output = RoutingOutputKind.Original,
        RoutingMissingOutputBehavior missing = RoutingMissingOutputBehavior.NeedsAttention,
        RoutingDeliveryMode mode = RoutingDeliveryMode.Automatic,
        bool enabled = true,
        RoutingDestinationKind destination = RoutingDestinationKind.Discord,
        RoutingDeliverySettings? settings = null) =>
        new(
            Guid.NewGuid(),
            enabled,
            RoutingActionKind.Deliver,
            destination,
            connectionId,
            output,
            missing,
            mode,
            null,
            settings ?? Settings());

    private static RoutingAction File(RoutingLibraryArea area) =>
        new(
            Guid.NewGuid(),
            true,
            RoutingActionKind.FileIntoLibrary,
            null,
            null,
            null,
            null,
            RoutingDeliveryMode.Automatic,
            area,
            null);

    private static RoutingClipFacts CaptureFacts(
        RoutingClipSource source = RoutingClipSource.ClipCordCapture,
        RoutingTriggerKind trigger = RoutingTriggerKind.InstantReplay,
        RoutingCaptureType? captureType = RoutingCaptureType.InstantReplay,
        string game = "Battlefield 6",
        long duration = 3000,
        RoutingEvaluationEventKind eventKind = RoutingEvaluationEventKind.SourceArrival,
        IReadOnlyList<RoutingClipOutputRevision>? outputs = null) =>
        new(
            "clip-evaluator",
            eventKind,
            source,
            trigger,
            captureType,
            game,
            ReactionCamera: true,
            duration,
            Hash('a'),
            outputs ??
            [
                Output("clip-evaluator", RoutingOutputKind.Original, 'a', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Landscape, 'b', RoutingOutputAvailability.Ready),
                Output("clip-evaluator", RoutingOutputKind.Portrait, 'c', RoutingOutputAvailability.Ready)
            ]);

    private static RoutingClipOutputRevision Output(
        string clipId,
        RoutingOutputKind kind,
        char revision,
        RoutingOutputAvailability availability,
        string? failureCode = null) =>
        new(new RoutingOutputReference(clipId, kind, Hash(revision)), availability, failureCode);

    private static RoutingDeliverySettings Settings() =>
        new(null, null, null, RoutingVisibility.Unspecified, false);

    private static string Hash(char value) => new(value, 64);

    private static DateTimeOffset At(int seconds) =>
        new(2026, 8, 27, 12, 0, seconds, TimeSpan.Zero);

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
}
