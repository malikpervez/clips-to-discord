using ClipsToDiscord;

internal static class RoutingRuntimeCoordinatorTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await AssertDefaultIsInertAsync();
        AssertLegacyRequiresExplicitPermission();
        await AssertColdStartAdoptsCommittedRoutingAsync();
        AssertInvalidColdStartConstructionFailsClosed();
        await AssertSuccessfulCutoverQuiescesWithoutLegacyAsync();
        await AssertPreparationFailureRestoresLegacyAsync();
        await AssertPreparationCancellationRestoresLegacyAsync();
        await AssertDurablePreparationFailureFencesLegacyAsync();
        await AssertDurablePreparationCancellationFencesLegacyAsync();
        await AssertCommitFailureIsIrreversibleAndRetryableAsync();
        await AssertCancellationAtCommitBoundaryNeverRestoresLegacyAsync();
        await AssertRejectedGatePreservesRoutingAuthorityAsync();
        await AssertMissingSourceCoveragePreservesRoutingAuthorityAsync();
        await AssertRoutingStartFailurePreservesRoutingAndRetriesAsync();
        await AssertCancellationAfterLegacyStopRestoresLegacyAsync();
        await AssertRoutingStopFailureRetriesUnderRoutingAuthorityAsync();
        await AssertFreshActivationNeverTouchesLegacyAsync();
        await AssertFreshPreFenceFailuresReturnToSetupAsync();
        await AssertFreshDurableFailuresRemainRoutingFencedAsync();
    }

    private static async Task AssertDefaultIsInertAsync()
    {
        var fixture = Fixture.CreateFresh();
        try
        {
            var coordinator = fixture.CreateCoordinator(RoutingRuntimeCoordinatorOptions.Default);
            var start = await coordinator.StartAsync();
            var stop = await coordinator.StopAsync();

            Assert(start.Status == RoutingRuntimeTransitionStatus.DisabledByDefault &&
                   stop.Status == RoutingRuntimeTransitionStatus.DisabledByDefault &&
                   coordinator.State == RoutingRuntimeCoordinatorState.Disabled &&
                   fixture.Legacy.StopCalls == 0 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Routing.CommitCalls == 0 && fixture.Routing.StartCalls == 0 &&
                   fixture.Routing.StopCalls == 0 &&
                   fixture.Ownership.Owner is null,
                "The routing coordinator must be inert unless an explicit opt-in is supplied.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertSuccessfulCutoverQuiescesWithoutLegacyAsync()
    {
        var fixture = Fixture.Create();
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            var started = await coordinator.StartAsync();
            var repeatedStart = await coordinator.StartAsync();

            Assert(started.Status == RoutingRuntimeTransitionStatus.Started &&
                   repeatedStart.Status == RoutingRuntimeTransitionStatus.AlreadyRunning &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingActive &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StopCalls == 1 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Routing.PrepareCalls == 1 && fixture.Routing.CommitCalls == 1 &&
                   fixture.Routing.StartCalls == 1,
                $"A cutover must prepare, transfer, commit authority, and then start routing once " +
                $"(start={started.Status}, repeat={repeatedStart.Status}, state={coordinator.State}, " +
                $"owner={fixture.Ownership.Owner}, legacy-stop={fixture.Legacy.StopCalls}, " +
                $"legacy-start={fixture.Legacy.StartCalls}, prepare={fixture.Routing.PrepareCalls}, " +
                $"commit={fixture.Routing.CommitCalls}, routing-start={fixture.Routing.StartCalls}, " +
                $"error={started.Error}).");

            var stopped = await coordinator.StopAsync();
            var repeatedStop = await coordinator.StopAsync();
            Assert(stopped.Status == RoutingRuntimeTransitionStatus.Stopped &&
                   repeatedStop.Status == RoutingRuntimeTransitionStatus.AlreadyStopped &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingQuiesced &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Routing.StopCalls == 1 && fixture.Legacy.StartCalls == 0,
                "Stopping committed routing must quiesce it while preserving Routing ownership.");

            var recovered = await coordinator.StartAsync();
            Assert(recovered.Status == RoutingRuntimeTransitionStatus.Started &&
                   fixture.Routing.PrepareCalls == 1 && fixture.Routing.CommitCalls == 2 &&
                   fixture.Routing.StartCalls == 2 && fixture.Legacy.StartCalls == 0,
                "A repeated start must recover Routing without re-preparing or reviving legacy.");
            Assert(fixture.Events.SequenceEqual([
                       "legacy-stop", "routing-prepare", "routing-commit", "routing-start",
                       "routing-stop", "routing-commit", "routing-start"
                   ]),
                "Authority commit must precede every routing start and legacy must stay fenced.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertPreparationFailureRestoresLegacyAsync()
    {
        var fixture = Fixture.Create();
        fixture.Routing.PrepareError = new InvalidOperationException("migration failed");
        try
        {
            var result = await fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true)).StartAsync();
            Assert(result.Status ==
                   RoutingRuntimeTransitionStatus.PreparationFailedLegacyRestored &&
                   result.Error == fixture.Routing.PrepareError &&
                   fixture.Routing.PrepareCalls == 1 &&
                   fixture.Routing.CommitCalls == 0 && fixture.Routing.StartCalls == 0 &&
                   fixture.Legacy.StartCalls == 1 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "A durable cutover preparation failure must restore legacy before routing owns the pipeline.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertPreparationCancellationRestoresLegacyAsync()
    {
        var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        fixture.Routing.DuringPrepare = cancellation.Cancel;
        try
        {
            await AssertThrowsAsync<OperationCanceledException>(() =>
                fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true)).StartAsync(cancellation.Token));
            Assert(fixture.Routing.PrepareCalls == 1 &&
                   fixture.Routing.CommitCalls == 0 && fixture.Routing.StartCalls == 0 &&
                   fixture.Legacy.StartCalls == 1 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "Cancellation during durable preparation must restore legacy before it reaches the caller.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static void AssertLegacyRequiresExplicitPermission()
    {
        var fixture = Fixture.Create();
        try
        {
            AssertThrows<ArgumentException>(
                () => fixture.CreateCoordinator(new(RequestedEnabled: true)),
                "Legacy ownership must be rejected unless the admitted-upgrade path explicitly permits it.");
            Assert(fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy &&
                   fixture.Legacy.StopCalls == 0 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Routing.CommitCalls == 0 && fixture.Routing.StartCalls == 0,
                "Rejecting implicit Legacy permission must not disturb either runtime.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertFreshActivationNeverTouchesLegacyAsync()
    {
        var fixture = Fixture.CreateFresh();
        try
        {
            var coordinator = fixture.CreateCoordinator(new(
                RequestedEnabled: true,
                LegacyRuntimeAllowed: false));
            Assert(coordinator.State == RoutingRuntimeCoordinatorState.SetupReady &&
                   fixture.Ownership.Owner is null,
                "A fresh coordinator must begin setup-ready without acquiring an owner.");

            var started = await coordinator.StartAsync();
            Assert(started.Status == RoutingRuntimeTransitionStatus.Started &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingActive &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StartCalls == 0 && fixture.Legacy.StopCalls == 0 &&
                   fixture.Events.SequenceEqual([
                       "routing-prepare", "routing-commit", "routing-start"
                   ]),
                "Fresh activation must move directly through Transition into Routing without touching Legacy.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertFreshPreFenceFailuresReturnToSetupAsync()
    {
        var failed = Fixture.CreateFresh();
        try
        {
            failed.Routing.PrepareError = new IOException("fresh preparation failed");
            var coordinator = failed.CreateCoordinator(new(true, LegacyRuntimeAllowed: false));
            var result = await coordinator.StartAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.PreparationFailedSetupReady &&
                   coordinator.State == RoutingRuntimeCoordinatorState.SetupReady &&
                   failed.Ownership.Owner is null &&
                   failed.Legacy.StartCalls == 0 && failed.Legacy.StopCalls == 0 &&
                   failed.Routing.CommitCalls == 0 && failed.Routing.StartCalls == 0,
                "A reversible fresh preparation failure must release Transition and return to setup-ready.");
        }
        finally
        {
            failed.Dispose();
        }

        var cancelled = Fixture.CreateFresh();
        using var cancellation = new CancellationTokenSource();
        try
        {
            cancelled.Routing.DuringPrepare = cancellation.Cancel;
            var coordinator = cancelled.CreateCoordinator(new(true, LegacyRuntimeAllowed: false));
            await AssertThrowsAsync<OperationCanceledException>(() =>
                coordinator.StartAsync(cancellation.Token));
            Assert(coordinator.State == RoutingRuntimeCoordinatorState.SetupReady &&
                   cancelled.Ownership.Owner is null &&
                   cancelled.Legacy.StartCalls == 0 && cancelled.Legacy.StopCalls == 0 &&
                   cancelled.Routing.CommitCalls == 0 && cancelled.Routing.StartCalls == 0,
                "Cancellation before a durable fresh fence must release ownership without starting Legacy.");
        }
        finally
        {
            cancelled.Dispose();
        }
    }

    private static async Task AssertFreshDurableFailuresRemainRoutingFencedAsync()
    {
        var prepared = Fixture.CreateFresh();
        try
        {
            prepared.Routing.PreparationFence = true;
            prepared.Routing.PrepareError = new IOException("durable fresh marker written");
            var coordinator = prepared.CreateCoordinator(new(true, LegacyRuntimeAllowed: false));
            var result = await coordinator.StartAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.PreparationFailedRecoveryNeeded &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded &&
                   prepared.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   prepared.Legacy.StartCalls == 0 && prepared.Legacy.StopCalls == 0,
                "Once fresh preparation is durable, failure must retain Routing ownership for recovery.");

            prepared.Routing.PrepareError = null;
            var recovered = await coordinator.StartAsync();
            Assert(recovered.Status == RoutingRuntimeTransitionStatus.Started &&
                   prepared.Routing.PrepareCalls == 2 &&
                   prepared.Routing.CommitCalls == 1 &&
                   prepared.Routing.StartCalls == 1 &&
                   prepared.Legacy.StartCalls == 0 && prepared.Legacy.StopCalls == 0,
                "A same-process retry after a fenced fresh preparation failure must resume preparation before authority commit and never touch Legacy.");
        }
        finally
        {
            prepared.Dispose();
        }

        var commit = Fixture.CreateFresh();
        try
        {
            commit.Routing.CommitError = new IOException("authority commit uncertain");
            var coordinator = commit.CreateCoordinator(new(true, LegacyRuntimeAllowed: false));
            var result = await coordinator.StartAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.AuthorityCommitFailedRecoveryNeeded &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded &&
                   commit.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   commit.Legacy.StartCalls == 0 && commit.Legacy.StopCalls == 0,
                "A fresh failure at the authority boundary must remain fenced under Routing.");
        }
        finally
        {
            commit.Dispose();
        }
    }

    private static async Task AssertDurablePreparationFailureFencesLegacyAsync()
    {
        var fixture = Fixture.Create();
        var failure = new IOException("failed after committed migration marker");
        fixture.Routing.PreparationFence = true;
        fixture.Routing.PrepareError = failure;
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            var result = await coordinator.StartAsync();
            Assert(result.Status ==
                       RoutingRuntimeTransitionStatus.PreparationFailedRecoveryNeeded &&
                   ReferenceEquals(result.Error, failure) &&
                   fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded,
                "A preparation error after a durable committed marker must transfer into the Routing fence and must never restore Legacy.");

            fixture.Routing.PrepareError = null;
            var recovered = await coordinator.StartAsync();
            Assert(recovered.Status == RoutingRuntimeTransitionStatus.Started &&
                   fixture.Routing.PrepareCalls == 1 &&
                   fixture.Routing.CommitCalls == 1 &&
                   fixture.Routing.StartCalls == 1 &&
                   fixture.Legacy.StartCalls == 0,
                "Retry after a fenced preparation failure must continue at authority commit without preparing or reviving Legacy again.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertDurablePreparationCancellationFencesLegacyAsync()
    {
        var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        fixture.Routing.PreparationFence = true;
        fixture.Routing.IgnorePrepareCancellation = true;
        fixture.Routing.DuringPrepare = cancellation.Cancel;
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            await AssertThrowsAsync<OperationCanceledException>(() =>
                coordinator.StartAsync(cancellation.Token));
            Assert(fixture.Routing.PrepareCalls == 1 &&
                   fixture.Routing.CommitCalls == 1 &&
                   fixture.Routing.StartCalls == 1 &&
                   fixture.Routing.StopCalls == 1 &&
                   fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded,
                "Cancellation after successful committed-marker preparation must cross the non-cancellable authority boundary and quiesce Routing without Legacy fallback.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertColdStartAdoptsCommittedRoutingAsync()
    {
        var fixture = Fixture.CreateColdRouting();
        try
        {
            var coordinator = fixture.CreateCoordinator(
                new RoutingRuntimeCoordinatorOptions(
                    RequestedEnabled: true,
                    AuthorityAlreadyCommitted: true),
                fixture.InitialRoutingOwnership);
            Assert(coordinator.State == RoutingRuntimeCoordinatorState.RoutingQuiesced,
                "Cold-start adoption must begin quiesced under the supplied Routing lease.");

            var started = await coordinator.StartAsync();
            var stopped = await coordinator.StopAsync();
            Assert(started.Status == RoutingRuntimeTransitionStatus.Started &&
                   stopped.Status == RoutingRuntimeTransitionStatus.Stopped &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingQuiesced &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StopCalls == 0 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Routing.PrepareCalls == 0 && fixture.Routing.CommitCalls == 1 &&
                   fixture.Routing.StartCalls == 1 && fixture.Routing.StopCalls == 1 &&
                   fixture.Events.SequenceEqual([
                       "routing-commit", "routing-start", "routing-stop"
                   ]),
                "A restarted process must recommit, inspect, start, and quiesce Routing without touching legacy or migration preparation.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static void AssertInvalidColdStartConstructionFailsClosed()
    {
        var cold = Fixture.CreateColdRouting();
        var otherCold = Fixture.CreateColdRouting();
        try
        {
            AssertThrows<ArgumentException>(() => cold.CreateCoordinator(new(true)),
                "Existing Routing ownership without explicit adoption must be rejected.");
            AssertThrows<ArgumentException>(() => cold.CreateCoordinator(
                    new RoutingRuntimeCoordinatorOptions(false, true),
                    cold.InitialRoutingOwnership),
                "Committed authority cannot be adopted while Routing is disabled.");
            AssertThrows<ArgumentException>(() => cold.CreateCoordinator(
                    new RoutingRuntimeCoordinatorOptions(true, false),
                    cold.InitialRoutingOwnership),
                "A Routing lease cannot be supplied without committed-authority adoption.");
            AssertThrows<ArgumentException>(() => cold.CreateCoordinator(
                    new RoutingRuntimeCoordinatorOptions(true, true),
                    otherCold.InitialRoutingOwnership),
                "A current Routing lease issued by another coordinator must be rejected.");
            Assert(cold.Legacy.StopCalls == 0 && cold.Legacy.StartCalls == 0 &&
                   cold.Routing.PrepareCalls == 0 && cold.Routing.CommitCalls == 0 &&
                   cold.Routing.StartCalls == 0 && cold.Routing.StopCalls == 0 &&
                   cold.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "Rejected cold-start construction must not touch either runtime or release Routing ownership.");
        }
        finally
        {
            cold.Dispose();
            otherCold.Dispose();
        }

        var legacy = Fixture.Create();
        try
        {
            AssertThrows<ArgumentException>(() => legacy.CreateCoordinator(
                    new RoutingRuntimeCoordinatorOptions(true, true)),
                "Committed-authority adoption without a Routing lease must be rejected.");
            Assert(legacy.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy &&
                   legacy.Legacy.StopCalls == 0 && legacy.Legacy.StartCalls == 0,
                "A missing cold-start lease must fail before disturbing legacy ownership.");
        }
        finally
        {
            legacy.Dispose();
        }
    }

    private static async Task AssertCommitFailureIsIrreversibleAndRetryableAsync()
    {
        var fixture = Fixture.Create();
        fixture.Routing.CommitError = new IOException("commit outcome ambiguous");
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            var failed = await coordinator.StartAsync();
            Assert(failed.Status ==
                   RoutingRuntimeTransitionStatus.AuthorityCommitFailedRecoveryNeeded &&
                   failed.Error == fixture.Routing.CommitError &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded &&
                   fixture.Routing.CommitCalls == 1 && fixture.Routing.StartCalls == 0 &&
                   fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "An ambiguous authority commit must permanently fence legacy processing.");

            fixture.Routing.CommitError = null;
            var recovered = await coordinator.StartAsync();
            Assert(recovered.Status == RoutingRuntimeTransitionStatus.Started &&
                   fixture.Routing.CommitCalls == 2 && fixture.Routing.StartCalls == 1 &&
                   fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "Retry must converge through the idempotent commit under Routing ownership.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertCancellationAtCommitBoundaryNeverRestoresLegacyAsync()
    {
        var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        fixture.Routing.DuringCommit = cancellation.Cancel;
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            await AssertThrowsAsync<OperationCanceledException>(() =>
                coordinator.StartAsync(cancellation.Token));
            Assert(fixture.Routing.CommitCalls == 1 && fixture.Routing.StartCalls == 1 &&
                   fixture.Routing.StopCalls == 1 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded,
                "Cancellation at the commit boundary must quiesce Routing without reviving legacy.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertRejectedGatePreservesRoutingAuthorityAsync()
    {
        var fixture = Fixture.Create();
        fixture.Routing.GateState = RoutingRuntimeGateState.MigrationMarkerMissing;
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            var result = await coordinator.StartAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.GateRejectedRecoveryNeeded &&
                   result.GateInspection?.State ==
                   RoutingRuntimeGateState.MigrationMarkerMissing &&
                   fixture.Routing.CommitCalls == 1 && fixture.Routing.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StartCalls == 0 &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded,
                "A post-commit gate rejection must preserve sticky Routing ownership.");

            var stopped = await coordinator.StopAsync();
            Assert(stopped.Status == RoutingRuntimeTransitionStatus.Stopped &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingQuiesced &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StartCalls == 0,
                "Shutdown after gate rejection must quiesce without reviving legacy.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertMissingSourceCoveragePreservesRoutingAuthorityAsync()
    {
        var fixture = Fixture.Create();
        fixture.Routing.CoveredLegacySources =
            new HashSet<ClipCaptureSource> { ClipCaptureSource.Nvidia };
        try
        {
            var result = await fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true)).StartAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.GateRejectedRecoveryNeeded &&
                   result.GateInspection?.State == RoutingRuntimeGateState.Enabled &&
                   result.GateInspection.RequiredLegacySource == ClipCaptureSource.SteelSeriesGg &&
                   !result.GateInspection.HasRequiredSourceCoverage &&
                   fixture.Routing.CommitCalls == 1 && fixture.Routing.StartCalls == 0 &&
                   fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "Missing adapter coverage after commit must fail closed under Routing ownership.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertRoutingStartFailurePreservesRoutingAndRetriesAsync()
    {
        var fixture = Fixture.Create();
        fixture.Routing.StartError = new InvalidOperationException("startup failed");
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            var result = await coordinator.StartAsync();
            Assert(result.Status ==
                   RoutingRuntimeTransitionStatus.RoutingStartFailedRecoveryNeeded &&
                   result.Error == fixture.Routing.StartError &&
                   fixture.Routing.StopCalls == 1 &&
                   fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "A routing startup failure must quiesce partial work and retain Routing ownership.");

            fixture.Routing.StartError = null;
            var recovered = await coordinator.StartAsync();
            Assert(recovered.Status == RoutingRuntimeTransitionStatus.Started &&
                   fixture.Routing.CommitCalls == 2 && fixture.Routing.StartCalls == 2 &&
                   fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "Retry must restart Routing without preparation or a legacy handoff.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertCancellationAfterLegacyStopRestoresLegacyAsync()
    {
        var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        fixture.Legacy.AfterStop = cancellation.Cancel;
        try
        {
            await AssertThrowsAsync<OperationCanceledException>(() =>
                fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true)).StartAsync(cancellation.Token));
            Assert(fixture.Routing.StartCalls == 0 &&
                   fixture.Routing.CommitCalls == 0 &&
                   fixture.Legacy.StartCalls == 1 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "Cancellation after legacy stops must roll back before it is observed by the caller.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertRoutingStopFailureRetriesUnderRoutingAuthorityAsync()
    {
        var fixture = Fixture.Create();
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true, LegacyRuntimeAllowed: true));
            _ = await coordinator.StartAsync();
            fixture.Routing.StopError = new InvalidOperationException("stop failed");

            var result = await coordinator.StopAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.RoutingStopFailed &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingRecoveryNeeded &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StartCalls == 0,
                "An uncertain routing stop must retain routing authority and never overlap legacy.");

            fixture.Routing.StopError = null;
            var recovered = await coordinator.StartAsync();
            Assert(recovered.Status == RoutingRuntimeTransitionStatus.Started &&
                   fixture.Routing.StopCalls == 2 && fixture.Routing.CommitCalls == 2 &&
                   fixture.Routing.StartCalls == 2 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing,
                "Retry must quiesce uncertain callbacks before recommitting and starting Routing.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
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

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}, but the operation completed.");
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

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            ClipProcessingOwnershipCoordinator ownership,
            FakeLegacyRuntime legacy,
            FakeRoutingRuntime routing,
            List<string> events,
            ClipProcessingOwnershipLease? initialRoutingOwnership = null)
        {
            Ownership = ownership;
            Legacy = legacy;
            Routing = routing;
            Events = events;
            InitialRoutingOwnership = initialRoutingOwnership;
        }

        internal ClipProcessingOwnershipCoordinator Ownership { get; }
        internal FakeLegacyRuntime Legacy { get; }
        internal FakeRoutingRuntime Routing { get; }
        internal List<string> Events { get; }
        internal ClipProcessingOwnershipLease? InitialRoutingOwnership { get; }

        internal static Fixture Create()
        {
            var ownership = new ClipProcessingOwnershipCoordinator();
            Assert(ownership.TryAcquire(
                       ClipProcessingRuntimeOwner.Legacy,
                       out var initialOwnership) && initialOwnership is not null,
                "The coordinator fixture must acquire initial legacy ownership.");
            var events = new List<string>();
            var legacy = new FakeLegacyRuntime(
                ownership,
                initialOwnership ?? throw new InvalidOperationException(
                    "Initial legacy ownership is missing."),
                events);
            return new Fixture(ownership, legacy, new FakeRoutingRuntime(events), events);
        }

        internal static Fixture CreateColdRouting()
        {
            var ownership = new ClipProcessingOwnershipCoordinator();
            Assert(ownership.TryAcquire(
                       ClipProcessingRuntimeOwner.Routing,
                       out var routingOwnership) && routingOwnership is not null,
                "The cold-start fixture must acquire initial Routing ownership.");
            var events = new List<string>();
            return new Fixture(
                ownership,
                new FakeLegacyRuntime(ownership, initialOwnership: null, events),
                new FakeRoutingRuntime(events),
                events,
                routingOwnership);
        }

        internal static Fixture CreateFresh()
        {
            var ownership = new ClipProcessingOwnershipCoordinator();
            var events = new List<string>();
            return new Fixture(
                ownership,
                new FakeLegacyRuntime(ownership, initialOwnership: null, events),
                new FakeRoutingRuntime(events),
                events);
        }

        internal RoutingRuntimeCoordinator CreateCoordinator(
            RoutingRuntimeCoordinatorOptions options,
            ClipProcessingOwnershipLease? initialRoutingOwnership = null) => new(
            Ownership,
            Legacy,
            Routing,
            options,
            initialRoutingOwnership);

        public void Dispose()
        {
            Legacy.Dispose();
            InitialRoutingOwnership?.Dispose();
        }
    }

    private sealed class FakeLegacyRuntime : ILegacyClipProcessingRuntime, IDisposable
    {
        private readonly ClipProcessingOwnershipCoordinator _ownership;
        private readonly List<string> _events;
        private ClipProcessingOwnershipLease? _owned;

        internal FakeLegacyRuntime(
            ClipProcessingOwnershipCoordinator ownership,
            ClipProcessingOwnershipLease? initialOwnership,
            List<string> events)
        {
            _ownership = ownership;
            _events = events;
            if (initialOwnership is not null) Adopt(initialOwnership);
        }

        internal int StopCalls { get; private set; }
        internal int StartCalls { get; private set; }
        internal Action? AfterStop { get; set; }
        internal Exception? StartError { get; set; }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            _events.Add("legacy-stop");
            _owned?.Dispose();
            _owned = null;
            AfterStop?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            _events.Add("legacy-start");
            if (StartError is not null) throw StartError;
            Adopt(ownership);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            _owned?.Dispose();
            _owned = null;
        }

        private void Adopt(ClipProcessingOwnershipLease ownership)
        {
            if (ownership.Owner != ClipProcessingRuntimeOwner.Legacy ||
                !ownership.IsCurrent ||
                !ownership.TryReissue(out var adopted) || adopted is null)
            {
                throw new InvalidOperationException(
                    "The fake legacy runtime could not adopt ownership.");
            }
            _owned = adopted;
            Assert(_ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "Adopting legacy ownership must preserve the owner kind.");
        }
    }

    private sealed class FakeRoutingRuntime(List<string> events) : IRoutingClipProcessingRuntime
    {
        internal RoutingRuntimeGateState GateState { get; set; } =
            RoutingRuntimeGateState.Enabled;
        internal ClipCaptureSource RequiredLegacySource { get; set; } =
            ClipCaptureSource.SteelSeriesGg;
        internal IReadOnlySet<ClipCaptureSource> CoveredLegacySources { get; set; } =
            new HashSet<ClipCaptureSource>
            {
                ClipCaptureSource.SteelSeriesGg,
                ClipCaptureSource.Nvidia
            };
        internal Exception? StartError { get; set; }
        internal Exception? PrepareError { get; set; }
        internal Action? DuringPrepare { get; set; }
        internal bool PreparationFence { get; set; }
        internal bool IgnorePrepareCancellation { get; set; }
        internal int PrepareCalls { get; private set; }
        internal Exception? CommitError { get; set; }
        internal Action? DuringCommit { get; set; }
        internal int CommitCalls { get; private set; }
        internal Exception? StopError { get; set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }

        public ValueTask PrepareActivationAsync(CancellationToken cancellationToken)
        {
            PrepareCalls++;
            events.Add("routing-prepare");
            DuringPrepare?.Invoke();
            if (!IgnorePrepareCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (PrepareError is not null) throw PrepareError;
            return ValueTask.CompletedTask;
        }

        public bool RequiresRoutingFenceAfterPreparation() => PreparationFence;

        public ValueTask CommitExecutionAuthorityAsync(
            ClipProcessingOwnershipLease ownership)
        {
            CommitCalls++;
            events.Add("routing-commit");
            Assert(ownership.Owner == ClipProcessingRuntimeOwner.Routing && ownership.IsCurrent,
                "Routing authority must commit with a current Routing lease.");
            DuringCommit?.Invoke();
            if (CommitError is not null) throw CommitError;
            return ValueTask.CompletedTask;
        }

        public RoutingRuntimeGateInspection InspectActivation(
            ClipProcessingOwnershipLease ownership)
        {
            Assert(CommitCalls > 0,
                "Routing activation must never be inspected before sticky authority commit begins.");
            return new RoutingRuntimeGateInspection(
                GateState,
                0,
                0,
                0,
                0,
                GateState == RoutingRuntimeGateState.Enabled ? 1 : null,
                GateState == RoutingRuntimeGateState.Enabled ? "test-fingerprint" : null,
                ownership.Epoch,
                RequiredLegacySource,
                CoveredLegacySources,
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            events.Add("routing-start");
            Assert(CommitCalls > 0,
                "Routing callbacks must not start before authority commit.");
            cancellationToken.ThrowIfCancellationRequested();
            if (StartError is not null) throw StartError;
            Assert(ownership.Owner == ClipProcessingRuntimeOwner.Routing && ownership.IsCurrent,
                "Routing must start with a current Routing lease.");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            events.Add("routing-stop");
            if (StopError is not null) throw StopError;
            return ValueTask.CompletedTask;
        }
    }
}
