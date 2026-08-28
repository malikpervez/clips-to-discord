using ClipsToDiscord;

internal static class RoutingRuntimeCoordinatorTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await AssertDefaultIsInertAsync();
        await AssertSuccessfulCutoverAndStopAreIdempotentAsync();
        await AssertPreparationFailureRestoresLegacyAsync();
        await AssertPreparationCancellationRestoresLegacyAsync();
        await AssertRejectedGateRestoresLegacyAsync();
        await AssertRoutingStartFailureRestoresLegacyAsync();
        await AssertCancellationAfterLegacyStopRestoresLegacyAsync();
        await AssertRoutingStopFailureNeverStartsLegacyAsync();
        await AssertLegacyRestartFailureLeavesRoutingStoppedAsync();
    }

    private static async Task AssertDefaultIsInertAsync()
    {
        var fixture = Fixture.Create();
        try
        {
            var coordinator = fixture.CreateCoordinator(RoutingRuntimeCoordinatorOptions.Default);
            var start = await coordinator.StartAsync();
            var stop = await coordinator.StopAsync();

            Assert(start.Status == RoutingRuntimeTransitionStatus.DisabledByDefault &&
                   stop.Status == RoutingRuntimeTransitionStatus.DisabledByDefault &&
                   coordinator.State == RoutingRuntimeCoordinatorState.Disabled &&
                   fixture.Legacy.StopCalls == 0 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Routing.StartCalls == 0 && fixture.Routing.StopCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "The routing coordinator must be inert unless an explicit opt-in is supplied.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertSuccessfulCutoverAndStopAreIdempotentAsync()
    {
        var fixture = Fixture.Create();
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true));
            var started = await coordinator.StartAsync();
            var repeatedStart = await coordinator.StartAsync();

            Assert(started.Status == RoutingRuntimeTransitionStatus.Started &&
                   repeatedStart.Status == RoutingRuntimeTransitionStatus.AlreadyRunning &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingActive &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StopCalls == 1 && fixture.Legacy.StartCalls == 0 &&
                   fixture.Routing.PrepareCalls == 1 && fixture.Routing.StartCalls == 1,
                "A cutover must stop legacy first, start routing once, and make repeat starts inert.");

            var stopped = await coordinator.StopAsync();
            var repeatedStop = await coordinator.StopAsync();
            Assert(stopped.Status == RoutingRuntimeTransitionStatus.Stopped &&
                   repeatedStop.Status == RoutingRuntimeTransitionStatus.AlreadyStopped &&
                   coordinator.State == RoutingRuntimeCoordinatorState.LegacyActive &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy &&
                   fixture.Routing.StopCalls == 1 && fixture.Legacy.StartCalls == 1,
                "Stopping routing must quiesce it before legacy restarts, and repeat stops must be inert.");
            Assert(fixture.Events.SequenceEqual([
                       "legacy-stop", "routing-prepare", "routing-start", "routing-stop",
                       "legacy-start"
                   ]),
                "Runtime lifecycle ordering must never overlap legacy and routing processing.");
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
            var result = await fixture.CreateCoordinator(new(true)).StartAsync();
            Assert(result.Status ==
                   RoutingRuntimeTransitionStatus.PreparationFailedLegacyRestored &&
                   result.Error == fixture.Routing.PrepareError &&
                   fixture.Routing.PrepareCalls == 1 &&
                   fixture.Routing.StartCalls == 0 &&
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
                fixture.CreateCoordinator(new(true)).StartAsync(cancellation.Token));
            Assert(fixture.Routing.PrepareCalls == 1 &&
                   fixture.Routing.StartCalls == 0 &&
                   fixture.Legacy.StartCalls == 1 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "Cancellation during durable preparation must restore legacy before it reaches the caller.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertRejectedGateRestoresLegacyAsync()
    {
        var fixture = Fixture.Create();
        fixture.Routing.GateState = RoutingRuntimeGateState.MigrationMarkerMissing;
        try
        {
            var result = await fixture.CreateCoordinator(new(true)).StartAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.GateRejectedLegacyRestored &&
                   result.GateInspection?.State ==
                   RoutingRuntimeGateState.MigrationMarkerMissing &&
                   fixture.Routing.StartCalls == 0 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy &&
                   fixture.Legacy.StartCalls == 1,
                "A fail-closed feature gate must restore the legacy watcher without starting routing.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertRoutingStartFailureRestoresLegacyAsync()
    {
        var fixture = Fixture.Create();
        fixture.Routing.StartError = new InvalidOperationException("startup failed");
        try
        {
            var result = await fixture.CreateCoordinator(new(true)).StartAsync();
            Assert(result.Status ==
                   RoutingRuntimeTransitionStatus.RoutingStartFailedLegacyRestored &&
                   result.Error == fixture.Routing.StartError &&
                   fixture.Routing.StopCalls == 1 &&
                   fixture.Legacy.StartCalls == 1 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "A routing startup failure must quiesce partial routing work and restore legacy ownership.");
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
                fixture.CreateCoordinator(new(true)).StartAsync(cancellation.Token));
            Assert(fixture.Routing.StartCalls == 0 &&
                   fixture.Legacy.StartCalls == 1 &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "Cancellation after legacy stops must roll back before it is observed by the caller.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertRoutingStopFailureNeverStartsLegacyAsync()
    {
        var fixture = Fixture.Create();
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true));
            _ = await coordinator.StartAsync();
            fixture.Routing.StopError = new InvalidOperationException("stop failed");

            var result = await coordinator.StopAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.RoutingStopFailed &&
                   coordinator.State == RoutingRuntimeCoordinatorState.RoutingActive &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Routing &&
                   fixture.Legacy.StartCalls == 0,
                "An uncertain routing stop must retain routing authority and must never overlap a legacy restart.");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task AssertLegacyRestartFailureLeavesRoutingStoppedAsync()
    {
        var fixture = Fixture.Create();
        try
        {
            var coordinator = fixture.CreateCoordinator(new(true));
            _ = await coordinator.StartAsync();
            fixture.Legacy.StartError = new InvalidOperationException("legacy restart failed");

            var result = await coordinator.StopAsync();
            Assert(result.Status == RoutingRuntimeTransitionStatus.LegacyRestartFailed &&
                   coordinator.State == RoutingRuntimeCoordinatorState.LegacyRecoveryNeeded &&
                   fixture.Routing.StopCalls == 1 &&
                   fixture.Ownership.Owner is null,
                "A failed legacy restart must leave routing stopped and expose recovery-needed state.");

            fixture.Legacy.StartError = null;
            var recovered = await coordinator.StopAsync();
            Assert(recovered.Status == RoutingRuntimeTransitionStatus.Stopped &&
                   coordinator.State == RoutingRuntimeCoordinatorState.LegacyActive &&
                   fixture.Ownership.Owner == ClipProcessingRuntimeOwner.Legacy,
                "A later stop/recovery attempt must safely reacquire legacy after the cause is corrected.");
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
            List<string> events)
        {
            Ownership = ownership;
            Legacy = legacy;
            Routing = routing;
            Events = events;
        }

        internal ClipProcessingOwnershipCoordinator Ownership { get; }
        internal FakeLegacyRuntime Legacy { get; }
        internal FakeRoutingRuntime Routing { get; }
        internal List<string> Events { get; }

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

        internal RoutingRuntimeCoordinator CreateCoordinator(
            RoutingRuntimeCoordinatorOptions options) => new(
            Ownership,
            Legacy,
            Routing,
            options);

        public void Dispose() => Legacy.Dispose();
    }

    private sealed class FakeLegacyRuntime : ILegacyClipProcessingRuntime, IDisposable
    {
        private readonly ClipProcessingOwnershipCoordinator _ownership;
        private readonly List<string> _events;
        private ClipProcessingOwnershipLease? _owned;

        internal FakeLegacyRuntime(
            ClipProcessingOwnershipCoordinator ownership,
            ClipProcessingOwnershipLease initialOwnership,
            List<string> events)
        {
            _ownership = ownership;
            _events = events;
            Adopt(initialOwnership);
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
        internal Exception? StartError { get; set; }
        internal Exception? PrepareError { get; set; }
        internal Action? DuringPrepare { get; set; }
        internal int PrepareCalls { get; private set; }
        internal Exception? StopError { get; set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }

        public ValueTask PrepareActivationAsync(CancellationToken cancellationToken)
        {
            PrepareCalls++;
            events.Add("routing-prepare");
            DuringPrepare?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (PrepareError is not null) throw PrepareError;
            return ValueTask.CompletedTask;
        }

        public RoutingRuntimeGateInspection InspectActivation(
            ClipProcessingOwnershipLease ownership) => new(
            GateState,
            0,
            0,
            0,
            0,
            GateState == RoutingRuntimeGateState.Enabled ? 1 : null,
            GateState == RoutingRuntimeGateState.Enabled ? "test-fingerprint" : null,
            ownership.Epoch);

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            events.Add("routing-start");
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
