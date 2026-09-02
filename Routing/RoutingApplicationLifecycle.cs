namespace ClipsToDiscord;

internal enum RoutingApplicationLifecycleState
{
    LegacyReady,
    RoutingReady,
    LegacyRunning,
    RoutingRunning,
    RoutingQuiesced,
    RoutingRecoveryNeeded,
    NeedsAttention,
    Stopped
}

internal enum RoutingApplicationLifecycleStatus
{
    LegacyStarted,
    RoutingStarted,
    AlreadyRunning,
    Stopped,
    AlreadyStopped,
    NeedsAttention,
    StartFailed
}

internal sealed record RoutingApplicationLifecycleResult(
    RoutingApplicationLifecycleStatus Status,
    RoutingApplicationLifecycleState State,
    RoutingRuntimeTransitionResult? RoutingTransition = null,
    Exception? Error = null);

/// <summary>
/// Establishes process-local clip-processing ownership from the durable execution-authority
/// document before either runtime is allowed to start. Missing authority starts in Legacy only
/// when no committed migration fence exists; a committed pre-authority marker recovers under
/// Routing ownership. Committed authority starts only Routing. Every unreadable or unsupported
/// authority or migration state fails closed without acquiring either runtime owner.
/// </summary>
internal sealed class RoutingApplicationLifecycle
{
    private readonly ClipProcessingOwnershipCoordinator _ownership;
    private readonly ILegacyClipProcessingRuntime _legacyRuntime;
    private readonly RoutingRuntimeCoordinator? _coordinator;
    private readonly RoutingExecutionAuthorityInspection _authorityInspection;
    private readonly bool _routingSelectedAtBootstrap;
    private readonly bool _captureLibraryOperationsPermitted;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private ClipProcessingOwnershipLease? _legacyStartupLease;
    private Exception? _attentionError;
    private int _state;

    private RoutingApplicationLifecycle(
        ClipProcessingOwnershipCoordinator ownership,
        ILegacyClipProcessingRuntime legacyRuntime,
        RoutingRuntimeCoordinator? coordinator,
        RoutingExecutionAuthorityInspection authorityInspection,
        RoutingApplicationLifecycleState state,
        bool routingSelectedAtBootstrap = false,
        bool captureLibraryOperationsPermitted = false,
        ClipProcessingOwnershipLease? legacyStartupLease = null,
        Exception? attentionError = null)
    {
        _ownership = ownership;
        _legacyRuntime = legacyRuntime;
        _coordinator = coordinator;
        _authorityInspection = authorityInspection;
        _routingSelectedAtBootstrap = routingSelectedAtBootstrap;
        _captureLibraryOperationsPermitted = captureLibraryOperationsPermitted;
        _legacyStartupLease = legacyStartupLease;
        _attentionError = attentionError;
        _state = (int)state;
    }

    internal static RoutingApplicationLifecycle Create(
        RoutingExecutionAuthorityStore authorityStore,
        ClipProcessingOwnershipCoordinator ownership,
        ILegacyClipProcessingRuntime legacyRuntime,
        IRoutingClipProcessingRuntime routingRuntime,
        CancellationToken cancellationToken = default,
        LegacyRoutingMigrationMarkerStore? migrationMarkers = null,
        Func<RoutingCaptureLibraryBinding>? currentCaptureLibraryBinding = null)
    {
        ArgumentNullException.ThrowIfNull(authorityStore);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(legacyRuntime);
        ArgumentNullException.ThrowIfNull(routingRuntime);

        // This is deliberately synchronous. No runtime callback can occur until the durable
        // authority has been classified and exactly one process-local owner has been selected.
        var inspection = authorityStore.Inspect(cancellationToken);
        if (ownership.Owner is not null)
        {
            return Blocked(
                ownership,
                legacyRuntime,
                inspection,
                new InvalidOperationException(
                    "Clip processing ownership was acquired before authority bootstrap."));
        }

        RoutingDocumentLoadResult<LegacyRoutingMigrationMarker>? markerInspection = null;
        if (migrationMarkers is not null)
        {
            markerInspection = migrationMarkers.Load(cancellationToken);
            if (markerInspection.Status is not (RoutingDocumentLoadStatus.Missing or
                RoutingDocumentLoadStatus.Loaded))
            {
                return Blocked(
                    ownership,
                    legacyRuntime,
                    inspection,
                    new InvalidDataException(
                        $"Routing migration evidence needs attention ({markerInspection.Status})."));
            }
        }

        if (inspection.LegacyPermitted && markerInspection?.Document is
                { Phase: LegacyRoutingMigrationMarkerPhase.Committed })
        {
            var bindingError = CaptureLibraryBindingError(
                markerInspection.Document.CaptureLibraryBinding,
                currentCaptureLibraryBinding);
            if (bindingError is not null)
            {
                return Blocked(ownership, legacyRuntime, inspection, bindingError);
            }
            if (!ownership.TryAcquire(
                    ClipProcessingRuntimeOwner.Routing,
                    out var recoveryLease) || recoveryLease is null)
            {
                return Blocked(
                    ownership,
                    legacyRuntime,
                    inspection,
                    new InvalidOperationException(
                        "Committed migration recovery could not fence Legacy processing."));
            }
            try
            {
                var recoveryCoordinator = new RoutingRuntimeCoordinator(
                    ownership,
                    legacyRuntime,
                    routingRuntime,
                    new RoutingRuntimeCoordinatorOptions(
                        RequestedEnabled: true,
                        RecoverCommittedMigration: true),
                    recoveryLease);
                return new RoutingApplicationLifecycle(
                    ownership,
                    legacyRuntime,
                    recoveryCoordinator,
                    inspection,
                    RoutingApplicationLifecycleState.RoutingReady,
                    routingSelectedAtBootstrap: true,
                    captureLibraryOperationsPermitted: true);
            }
            catch
            {
                recoveryLease.Dispose();
                throw;
            }
        }

        if (inspection is
            {
                State: RoutingExecutionAuthorityInspectionState.LegacyPermitted,
                LoadStatus: RoutingDocumentLoadStatus.Missing,
                Document: null
            })
        {
            if (!ownership.TryAcquire(
                    ClipProcessingRuntimeOwner.Legacy,
                    out var legacyLease) || legacyLease is null)
            {
                return Blocked(
                    ownership,
                    legacyRuntime,
                    inspection,
                    new InvalidOperationException(
                        "Legacy clip-processing ownership could not be acquired."));
            }
            var coordinator = new RoutingRuntimeCoordinator(
                ownership,
                legacyRuntime,
                routingRuntime,
                new RoutingRuntimeCoordinatorOptions(RequestedEnabled: true));
            return new RoutingApplicationLifecycle(
                ownership,
                legacyRuntime,
                coordinator,
                inspection,
                RoutingApplicationLifecycleState.LegacyReady,
                captureLibraryOperationsPermitted: true,
                legacyStartupLease: legacyLease);
        }

        if (inspection is
            {
                State: RoutingExecutionAuthorityInspectionState.RoutingRequired,
                LoadStatus: RoutingDocumentLoadStatus.Loaded,
                Document: not null
            })
        {
            if (markerInspection is not null &&
                (markerInspection.Document is not
                    { Phase: LegacyRoutingMigrationMarkerPhase.Committed } committedMarker ||
                 committedMarker.MigrationId != inspection.Document.MigrationId ||
                 !committedMarker.PayloadFingerprint.Equals(
                     inspection.Document.MigrationPayloadFingerprint,
                     StringComparison.Ordinal) ||
                 !committedMarker.SourceFingerprint.Equals(
                     inspection.Document.SourceFingerprint,
                     StringComparison.Ordinal) ||
                 committedMarker.CaptureLibraryBinding !=
                 inspection.Document.CaptureLibraryBinding))
            {
                return Blocked(
                    ownership,
                    legacyRuntime,
                    inspection,
                    new InvalidDataException(
                        "Routing authority does not match its committed migration evidence."));
            }
            var bindingError = CaptureLibraryBindingError(
                inspection.Document.CaptureLibraryBinding,
                currentCaptureLibraryBinding);
            if (bindingError is not null)
            {
                return Blocked(ownership, legacyRuntime, inspection, bindingError);
            }
            if (!ownership.TryAcquire(
                    ClipProcessingRuntimeOwner.Routing,
                    out var routingLease) || routingLease is null)
            {
                return Blocked(
                    ownership,
                    legacyRuntime,
                    inspection,
                    new InvalidOperationException(
                        "Routing clip-processing ownership could not be acquired."));
            }
            try
            {
                var coordinator = new RoutingRuntimeCoordinator(
                    ownership,
                    legacyRuntime,
                    routingRuntime,
                    new RoutingRuntimeCoordinatorOptions(
                        RequestedEnabled: true,
                        AuthorityAlreadyCommitted: true),
                    routingLease);
                return new RoutingApplicationLifecycle(
                    ownership,
                    legacyRuntime,
                    coordinator,
                    inspection,
                    RoutingApplicationLifecycleState.RoutingReady,
                    routingSelectedAtBootstrap: true,
                    captureLibraryOperationsPermitted: true);
            }
            catch
            {
                routingLease.Dispose();
                throw;
            }
        }

        return Blocked(
            ownership,
            legacyRuntime,
            inspection,
            new InvalidDataException(
                $"Routing execution authority needs attention ({inspection.LoadStatus})."));
    }

    internal RoutingRuntimeCoordinator? Coordinator => _coordinator;
    internal RoutingExecutionAuthorityInspection AuthorityInspection => _authorityInspection;
    internal bool RoutingSelectedAtBootstrap => _routingSelectedAtBootstrap;
    internal bool CaptureLibraryOperationsPermitted =>
        _captureLibraryOperationsPermitted;
    internal bool LegacyOperationsPermitted =>
        _authorityInspection.LegacyPermitted &&
        !_routingSelectedAtBootstrap &&
        (State is RoutingApplicationLifecycleState.LegacyReady or
            RoutingApplicationLifecycleState.LegacyRunning) &&
        _coordinator?.State == RoutingRuntimeCoordinatorState.LegacyActive;
    internal RoutingApplicationLifecycleState State =>
        (RoutingApplicationLifecycleState)Volatile.Read(ref _state);
    internal Exception? AttentionError => Volatile.Read(ref _attentionError);

    /// <summary>
    /// Starts the runtime selected by durable authority. For a first activation this starts only
    /// Legacy; ActivateRoutingAsync is the explicit, separately observable cutover operation.
    /// </summary>
    internal async Task<RoutingApplicationLifecycleResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return State switch
            {
                RoutingApplicationLifecycleState.LegacyReady =>
                    await StartLegacyAsync(cancellationToken).ConfigureAwait(false),
                RoutingApplicationLifecycleState.RoutingReady or
                RoutingApplicationLifecycleState.RoutingQuiesced or
                RoutingApplicationLifecycleState.RoutingRecoveryNeeded =>
                    await StartRoutingAsync(cancellationToken).ConfigureAwait(false),
                RoutingApplicationLifecycleState.LegacyRunning or
                RoutingApplicationLifecycleState.RoutingRunning =>
                    Result(RoutingApplicationLifecycleStatus.AlreadyRunning),
                RoutingApplicationLifecycleState.Stopped =>
                    Result(RoutingApplicationLifecycleStatus.AlreadyStopped),
                _ => Result(
                    RoutingApplicationLifecycleStatus.NeedsAttention,
                    error: AttentionError)
            };
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<RoutingApplicationLifecycleResult> ActivateRoutingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RoutingApplicationLifecycleState.RoutingRunning)
                return Result(RoutingApplicationLifecycleStatus.AlreadyRunning);
            if (State is not (RoutingApplicationLifecycleState.LegacyRunning or
                RoutingApplicationLifecycleState.RoutingReady or
                RoutingApplicationLifecycleState.RoutingQuiesced or
                RoutingApplicationLifecycleState.RoutingRecoveryNeeded))
            {
                return Result(
                    RoutingApplicationLifecycleStatus.NeedsAttention,
                    error: AttentionError ?? new InvalidOperationException(
                        "The selected clip-processing runtime is not ready for Routing activation."));
            }
            return await StartRoutingAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<RoutingApplicationLifecycleResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RoutingApplicationLifecycleState.Stopped)
                return Result(RoutingApplicationLifecycleStatus.AlreadyStopped);
            if (State == RoutingApplicationLifecycleState.LegacyReady)
            {
                _legacyStartupLease?.Dispose();
                _legacyStartupLease = null;
                SetState(RoutingApplicationLifecycleState.Stopped);
                return Result(RoutingApplicationLifecycleStatus.Stopped);
            }
            if (State == RoutingApplicationLifecycleState.LegacyRunning)
            {
                try
                {
                    await _legacyRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    return NeedsAttention(exception);
                }
                if (_ownership.Owner is not null)
                {
                    return NeedsAttention(new InvalidOperationException(
                        "The Legacy runtime did not release clip-processing ownership."));
                }
                SetState(RoutingApplicationLifecycleState.Stopped);
                return Result(RoutingApplicationLifecycleStatus.Stopped);
            }
            if (_coordinator is not null &&
                (_authorityInspection.RoutingRequired || _routingSelectedAtBootstrap ||
                 State is RoutingApplicationLifecycleState.RoutingRunning or
                     RoutingApplicationLifecycleState.RoutingQuiesced or
                     RoutingApplicationLifecycleState.RoutingRecoveryNeeded))
            {
                var stopped = await _coordinator.StopAsync(cancellationToken)
                    .ConfigureAwait(false);
                SyncFromCoordinator();
                return Result(
                    stopped.Status == RoutingRuntimeTransitionStatus.AlreadyStopped
                        ? RoutingApplicationLifecycleStatus.AlreadyStopped
                        : stopped.Status == RoutingRuntimeTransitionStatus.Stopped
                            ? RoutingApplicationLifecycleStatus.Stopped
                            : RoutingApplicationLifecycleStatus.NeedsAttention,
                    stopped,
                    stopped.Error);
            }
            return Result(
                RoutingApplicationLifecycleStatus.NeedsAttention,
                error: AttentionError);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<RoutingApplicationLifecycleResult> StartLegacyAsync(
        CancellationToken cancellationToken)
    {
        var lease = _legacyStartupLease;
        if (lease is null || !lease.IsCurrent ||
            lease.Owner != ClipProcessingRuntimeOwner.Legacy)
        {
            return NeedsAttention(new InvalidOperationException(
                "The Legacy startup lease is unavailable."));
        }
        try
        {
            await _legacyRuntime.StartAsync(lease, cancellationToken).ConfigureAwait(false);
            if (lease.IsCurrent || _ownership.Owner != ClipProcessingRuntimeOwner.Legacy)
            {
                throw new InvalidOperationException(
                    "The Legacy runtime did not adopt its startup ownership lease.");
            }
            _legacyStartupLease = null;
            SetState(RoutingApplicationLifecycleState.LegacyRunning);
            return Result(RoutingApplicationLifecycleStatus.LegacyStarted);
        }
        catch (Exception exception)
        {
            await CleanupFailedLegacyStartAsync(lease).ConfigureAwait(false);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            return Result(
                RoutingApplicationLifecycleStatus.StartFailed,
                error: exception);
        }
    }

    private async Task CleanupFailedLegacyStartAsync(ClipProcessingOwnershipLease lease)
    {
        lease.Dispose();
        try
        {
            await _legacyRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The uncertain owner below keeps both processing runtimes fenced.
        }
        _legacyStartupLease = null;
        if (_ownership.Owner is null &&
            _ownership.TryAcquire(ClipProcessingRuntimeOwner.Legacy, out var retryLease) &&
            retryLease is not null)
        {
            _legacyStartupLease = retryLease;
            SetState(RoutingApplicationLifecycleState.LegacyReady);
            return;
        }
        _attentionError = new InvalidOperationException(
            "Legacy startup did not quiesce cleanly; Routing remains blocked.");
        SetState(RoutingApplicationLifecycleState.NeedsAttention);
    }

    private async Task<RoutingApplicationLifecycleResult> StartRoutingAsync(
        CancellationToken cancellationToken)
    {
        var coordinator = _coordinator ?? throw new InvalidOperationException(
            "Routing cannot start without an authority-backed coordinator.");
        try
        {
            var transition = await coordinator.StartAsync(cancellationToken)
                .ConfigureAwait(false);
            SyncFromCoordinator();
            return Result(
                transition.Status == RoutingRuntimeTransitionStatus.AlreadyRunning
                    ? RoutingApplicationLifecycleStatus.AlreadyRunning
                    : transition.RoutingRunning
                        ? RoutingApplicationLifecycleStatus.RoutingStarted
                        : transition.LegacyRunning
                            ? RoutingApplicationLifecycleStatus.StartFailed
                            : RoutingApplicationLifecycleStatus.NeedsAttention,
                transition,
                transition.Error);
        }
        catch
        {
            SyncFromCoordinator();
            throw;
        }
    }

    private void SyncFromCoordinator()
    {
        if (_coordinator is null) return;
        SetState(_coordinator.State switch
        {
            RoutingRuntimeCoordinatorState.LegacyActive =>
                RoutingApplicationLifecycleState.LegacyRunning,
            RoutingRuntimeCoordinatorState.RoutingActive =>
                RoutingApplicationLifecycleState.RoutingRunning,
            RoutingRuntimeCoordinatorState.RoutingQuiesced =>
                RoutingApplicationLifecycleState.RoutingQuiesced,
            _ => RoutingApplicationLifecycleState.RoutingRecoveryNeeded
        });
    }

    private RoutingApplicationLifecycleResult NeedsAttention(Exception exception)
    {
        _attentionError = exception;
        SetState(RoutingApplicationLifecycleState.NeedsAttention);
        return Result(RoutingApplicationLifecycleStatus.NeedsAttention, error: exception);
    }

    private RoutingApplicationLifecycleResult Result(
        RoutingApplicationLifecycleStatus status,
        RoutingRuntimeTransitionResult? routingTransition = null,
        Exception? error = null) => new(status, State, routingTransition, error);

    private void SetState(RoutingApplicationLifecycleState state) =>
        Volatile.Write(ref _state, (int)state);

    private static RoutingApplicationLifecycle Blocked(
        ClipProcessingOwnershipCoordinator ownership,
        ILegacyClipProcessingRuntime legacyRuntime,
        RoutingExecutionAuthorityInspection inspection,
        Exception error) => new(
            ownership,
            legacyRuntime,
            coordinator: null,
            inspection,
            RoutingApplicationLifecycleState.NeedsAttention,
            attentionError: error);

    private static Exception? CaptureLibraryBindingError(
        RoutingCaptureLibraryBinding expected,
        Func<RoutingCaptureLibraryBinding>? currentCaptureLibraryBinding)
    {
        try
        {
            if (currentCaptureLibraryBinding is null)
            {
                throw new InvalidDataException(
                    "Routing requires strict Capture library identity evidence.");
            }
            RoutingCaptureLibraryBindingModel.RequireExact(
                expected,
                currentCaptureLibraryBinding());
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or PathTooLongException or
                System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return new InvalidDataException(
                "The Capture library does not match durable Routing authority. " +
                "Capture recovery and clip processing remain paused.",
                exception);
        }
    }
}

[Flags]
internal enum RoutingProtectedSetting
{
    None = 0,
    WatchedClipsFolder = 1,
    CaptureSource = 2,
    UploadToDiscord = 4,
    LegacyWebhookIdentity = 8
}

internal enum RoutingSettingsGuardStatus
{
    Allowed,
    RequiresSwitchTransaction,
    AuthorityNeedsAttention
}

internal sealed record RoutingSettingsGuardResult(
    RoutingSettingsGuardStatus Status,
    RoutingProtectedSetting ProtectedChanges)
{
    internal bool Allowed => Status == RoutingSettingsGuardStatus.Allowed;
}

/// <summary>
/// Pure settings boundary for sticky Routing authority. Source and legacy destination identity
/// cannot change until a future durable switch transaction exists; unrelated UI/runtime settings
/// remain editable. No webhook value is returned or logged.
/// </summary>
internal static class RoutingSettingsAuthorityGuard
{
    internal static RoutingSettingsGuardResult Evaluate(
        RoutingExecutionAuthorityInspection authority,
        AppSettings current,
        AppSettings candidate)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);

        if (authority.LegacyPermitted)
            return new(RoutingSettingsGuardStatus.Allowed, RoutingProtectedSetting.None);

        var changes = RoutingProtectedSetting.None;
        if (!SamePath(current.ClipsFolder, candidate.ClipsFolder))
            changes |= RoutingProtectedSetting.WatchedClipsFolder;
        if (current.CaptureSource != candidate.CaptureSource)
            changes |= RoutingProtectedSetting.CaptureSource;
        if (current.UploadToDiscord != candidate.UploadToDiscord)
            changes |= RoutingProtectedSetting.UploadToDiscord;
        if (!string.Equals(current.WebhookUrl, candidate.WebhookUrl, StringComparison.Ordinal))
            changes |= RoutingProtectedSetting.LegacyWebhookIdentity;

        if (changes == RoutingProtectedSetting.None)
            return new(RoutingSettingsGuardStatus.Allowed, changes);
        return new(
            authority.RoutingRequired
                ? RoutingSettingsGuardStatus.RequiresSwitchTransaction
                : RoutingSettingsGuardStatus.AuthorityNeedsAttention,
            changes);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
