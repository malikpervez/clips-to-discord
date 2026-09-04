namespace ClipsToDiscord;

internal enum LegacyRoutingActivationEvidenceStatus
{
    Loaded,
    LegacyStateUnavailable,
    InputUnavailable
}

internal sealed record LegacyRoutingActivationEvidence(
    LegacyRoutingActivationEvidenceStatus Status,
    WatchStateRoutingProbe LegacyState,
    LegacyRoutingCutoverReadiness? Readiness)
{
    internal bool Loaded => Status == LegacyRoutingActivationEvidenceStatus.Loaded &&
                            LegacyState.Loaded && Readiness is not null;
}

internal interface ILegacyRoutingActivationEvidenceSource
{
    /// <summary>
    /// Re-reads every legacy input that contributes to the migration source fingerprint. The
    /// returned evidence is never cached by the routing gate.
    /// </summary>
    LegacyRoutingActivationEvidence Inspect();
}

internal enum FreshRoutingActivationEvidenceStatus
{
    Loaded,
    WatchStateUnavailable,
    InputUnavailable
}

internal sealed record FreshRoutingActivationEvidence(
    FreshRoutingActivationEvidenceStatus Status,
    WatchStateRoutingProbe WatchState,
    LegacyRoutingMigrationPlan? Plan)
{
    internal bool Loaded => Status == FreshRoutingActivationEvidenceStatus.Loaded &&
                            WatchState.Loaded && WatchState.State is not null && Plan is not null;
}

internal interface IFreshRoutingActivationEvidenceSource
{
    /// <summary>
    /// Re-reads the strict current settings, watcher state, native watched-root identity, and
    /// Capture library identity that are payload-bound by a fresh setup marker. The marker's
    /// initial route is proof of the authority transition, not a permanent constraint on the
    /// editable live snapshot.
    /// </summary>
    FreshRoutingActivationEvidence Inspect(LegacyRoutingMigrationMarker marker);
}

internal sealed class FreshRoutingActivationEvidenceSource : IFreshRoutingActivationEvidenceSource
{
    private readonly WatchStateStore _watchState;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Func<RoutingCaptureLibraryBinding> _captureLibraryBindingProvider;
    private readonly Func<AppSettings, string> _watchedRootIdentityProvider;

    internal FreshRoutingActivationEvidenceSource(
        WatchStateStore watchState,
        Func<AppSettings> settingsProvider,
        Func<RoutingCaptureLibraryBinding> captureLibraryBindingProvider,
        Func<AppSettings, string>? watchedRootIdentityProvider = null)
    {
        _watchState = watchState ?? throw new ArgumentNullException(nameof(watchState));
        _settingsProvider = settingsProvider ??
                            throw new ArgumentNullException(nameof(settingsProvider));
        _captureLibraryBindingProvider = captureLibraryBindingProvider ??
                                         throw new ArgumentNullException(
                                             nameof(captureLibraryBindingProvider));
        _watchedRootIdentityProvider = watchedRootIdentityProvider ??
                                       InspectWatchedRootIdentity;
    }

    public FreshRoutingActivationEvidence Inspect(LegacyRoutingMigrationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var state = _watchState.ProbeForRoutingActivation();
        if (!state.Loaded || state.State is null)
        {
            return new FreshRoutingActivationEvidence(
                FreshRoutingActivationEvidenceStatus.WatchStateUnavailable,
                state,
                null);
        }

        try
        {
            var settings = _settingsProvider() ?? throw new InvalidDataException(
                "The fresh Routing settings evidence is missing.");
            var captureLibraryBinding = _captureLibraryBindingProvider() ??
                                        throw new InvalidDataException(
                                            "The Capture library identity evidence is missing.");
            var watchedRootIdentity = _watchedRootIdentityProvider(settings);
            var plan = FreshRoutingSetupPlanner.ReconstructCurrentPlan(
                marker,
                settings,
                state.State,
                captureLibraryBinding,
                watchedRootIdentity);
            return new FreshRoutingActivationEvidence(
                FreshRoutingActivationEvidenceStatus.Loaded,
                state,
                plan);
        }
        catch (Exception)
        {
            return new FreshRoutingActivationEvidence(
                FreshRoutingActivationEvidenceStatus.InputUnavailable,
                state,
                null);
        }
    }

    private static string InspectWatchedRootIdentity(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var source = AppSettings.NormalizeCaptureSource(settings.CaptureSource);
        if (!Enum.IsDefined(source))
            throw new InvalidDataException("The fresh watched-source type is invalid.");
        return RoutingWatchedSourceAdapters.Get(source)
            .InspectRootIdentity(settings.ClipsFolder);
    }
}

/// <summary>
/// Strict adapter for the current settings, durable watcher state, and opaque connection ids.
/// Providers are invoked on every inspection so a settings, source-folder, connection, or hash
/// change immediately invalidates an older committed cutover marker.
/// </summary>
internal sealed class LegacyRoutingActivationEvidenceSource : ILegacyRoutingActivationEvidenceSource
{
    private static readonly DateTimeOffset EvaluationTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly WatchStateStore _watchState;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Func<IReadOnlyList<string>> _discordConnectionIdsProvider;
    private readonly Func<RoutingCaptureLibraryBinding> _captureLibraryBindingProvider;
    private readonly Func<LegacyRoutingMigrationAdmission> _admissionProvider;
    private readonly Func<int> _importedRouteLabelVersionProvider;

    internal LegacyRoutingActivationEvidenceSource(
        WatchStateStore watchState,
        Func<AppSettings> settingsProvider,
        Func<IReadOnlyList<string>> discordConnectionIdsProvider,
        Func<RoutingCaptureLibraryBinding> captureLibraryBindingProvider,
        Func<LegacyRoutingMigrationAdmission> admissionProvider,
        Func<int>? importedRouteLabelVersionProvider = null)
    {
        _watchState = watchState ?? throw new ArgumentNullException(nameof(watchState));
        _settingsProvider = settingsProvider ??
                            throw new ArgumentNullException(nameof(settingsProvider));
        _discordConnectionIdsProvider = discordConnectionIdsProvider ??
                                        throw new ArgumentNullException(
                                            nameof(discordConnectionIdsProvider));
        _captureLibraryBindingProvider = captureLibraryBindingProvider ??
                                         throw new ArgumentNullException(
                                             nameof(captureLibraryBindingProvider));
        _admissionProvider = admissionProvider ??
                             throw new ArgumentNullException(nameof(admissionProvider));
        _importedRouteLabelVersionProvider = importedRouteLabelVersionProvider ?? (() =>
            LegacyRoutingMigrationPlanner.CurrentImportedRouteLabelVersion);
    }

    public LegacyRoutingActivationEvidence Inspect()
    {
        var state = _watchState.ProbeForRoutingActivation();
        if (!state.Loaded || state.State is null)
        {
            return new LegacyRoutingActivationEvidence(
                LegacyRoutingActivationEvidenceStatus.LegacyStateUnavailable,
                state,
                null);
        }

        try
        {
            var settings = _settingsProvider() ??
                           throw new InvalidDataException(
                               "The legacy settings evidence is missing.");
            var connectionIds = _discordConnectionIdsProvider()?.ToArray() ??
                                throw new InvalidDataException(
                                    "The legacy connection evidence is missing.");
            var captureLibraryBinding = _captureLibraryBindingProvider() ??
                                        throw new InvalidDataException(
                                            "The Capture library identity evidence is missing.");
            var readiness = LegacyRoutingMigrationPlanner.Evaluate(
                new LegacyRoutingMigrationInput(
                    settings,
                    state.State,
                    LegacyWorkerQuiesced: true,
                    connectionIds,
                    captureLibraryBinding,
                    _admissionProvider(),
                    _importedRouteLabelVersionProvider()),
                EvaluationTimestamp);
            return new LegacyRoutingActivationEvidence(
                LegacyRoutingActivationEvidenceStatus.Loaded,
                state,
                readiness);
        }
        catch (Exception)
        {
            return new LegacyRoutingActivationEvidence(
                LegacyRoutingActivationEvidenceStatus.InputUnavailable,
                state,
                null);
        }
    }
}

internal enum ClipProcessingRuntimeOwner
{
    Legacy,
    Transition,
    Routing
}

/// <summary>
/// Process-local ownership for the one clip-processing authority. Program's application mutex
/// already prevents two ClipCord UI processes; this coordinator prevents the legacy watcher and
/// routing runtime from overlapping inside that process. A lease remains authoritative until it
/// is disposed or atomically transferred, and stale leases can never become current again.
/// </summary>
internal sealed class ClipProcessingOwnershipCoordinator
{
    private readonly object _sync = new();
    private ClipProcessingRuntimeOwner? _owner;
    private long _epoch;

    internal ClipProcessingRuntimeOwner? Owner
    {
        get
        {
            lock (_sync) return _owner;
        }
    }

    internal bool TryAcquire(
        ClipProcessingRuntimeOwner owner,
        out ClipProcessingOwnershipLease? lease)
    {
        if (!Enum.IsDefined(owner)) throw new ArgumentOutOfRangeException(nameof(owner));
        lock (_sync)
        {
            if (_owner is not null)
            {
                lease = null;
                return false;
            }

            _owner = owner;
            _epoch = NextEpoch(_epoch);
            lease = new ClipProcessingOwnershipLease(this, owner, _epoch);
            return true;
        }
    }

    internal bool TryTransfer(
        ClipProcessingOwnershipLease current,
        ClipProcessingRuntimeOwner nextOwner,
        out ClipProcessingOwnershipLease? next)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!Enum.IsDefined(nextOwner)) throw new ArgumentOutOfRangeException(nameof(nextOwner));
        lock (_sync)
        {
            if (!current.IsIssuedBy(this) ||
                !IsCurrentCore(current.Owner, current.Epoch) ||
                !current.TryMarkTransferred())
            {
                next = null;
                return false;
            }

            _owner = nextOwner;
            _epoch = NextEpoch(_epoch);
            next = new ClipProcessingOwnershipLease(this, nextOwner, _epoch);
            return true;
        }
    }

    /// <summary>
    /// Atomically replaces a caller-owned lease with a runtime-owned lease for the same owner.
    /// The old handle becomes stale before the replacement is returned, so code that constructed
    /// a long-running runtime cannot later dispose or transfer its original handle out from under
    /// that runtime.
    /// </summary>
    internal bool TryReissue(
        ClipProcessingOwnershipLease current,
        out ClipProcessingOwnershipLease? replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_sync)
        {
            if (!current.IsIssuedBy(this) ||
                !IsCurrentCore(current.Owner, current.Epoch) ||
                !current.TryMarkTransferred())
            {
                replacement = null;
                return false;
            }

            _epoch = NextEpoch(_epoch);
            replacement = new ClipProcessingOwnershipLease(this, current.Owner, _epoch);
            return true;
        }
    }

    internal bool IsCurrent(ClipProcessingRuntimeOwner owner, long epoch)
    {
        lock (_sync) return IsCurrentCore(owner, epoch);
    }

    internal void Release(ClipProcessingRuntimeOwner owner, long epoch)
    {
        lock (_sync)
        {
            if (!IsCurrentCore(owner, epoch)) return;
            _owner = null;
            _epoch = NextEpoch(_epoch);
        }
    }

    private bool IsCurrentCore(ClipProcessingRuntimeOwner owner, long epoch) =>
        _owner == owner && _epoch == epoch;

    private static long NextEpoch(long current) =>
        current == long.MaxValue ? 1 : current + 1;
}

internal sealed class ClipProcessingOwnershipLease : IDisposable
{
    private readonly ClipProcessingOwnershipCoordinator _coordinator;
    private int _released;

    internal ClipProcessingOwnershipLease(
        ClipProcessingOwnershipCoordinator coordinator,
        ClipProcessingRuntimeOwner owner,
        long epoch)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        Owner = owner;
        Epoch = epoch;
    }

    internal ClipProcessingRuntimeOwner Owner { get; }
    internal long Epoch { get; }
    internal bool IsCurrent =>
        Volatile.Read(ref _released) == 0 && _coordinator.IsCurrent(Owner, Epoch);

    internal bool TryMarkTransferred() => Interlocked.Exchange(ref _released, 1) == 0;
    internal bool IsIssuedBy(ClipProcessingOwnershipCoordinator coordinator) =>
        ReferenceEquals(_coordinator, coordinator);
    internal bool TryReissue(out ClipProcessingOwnershipLease? replacement) =>
        _coordinator.TryReissue(this, out replacement);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _coordinator.Release(Owner, Epoch);
    }
}
