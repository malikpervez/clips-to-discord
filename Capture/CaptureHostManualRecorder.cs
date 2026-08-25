namespace ClipsToDiscord;

internal sealed class CaptureProjectCommittedEventArgs : EventArgs
{
    internal CaptureProjectCommittedEventArgs(string libraryRoot, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        LibraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot.Trim()));
        ProjectId = projectId;
    }

    internal string LibraryRoot { get; }
    internal string ProjectId { get; }
}

internal interface ICaptureProjectCompletionSource
{
    event EventHandler<CaptureProjectCommittedEventArgs>? ProjectCommitted;
}

internal sealed class CaptureHostManualRecorder : IManualCaptureRecorder, IAutomaticCaptureTargetRecorder,
    IReplayCaptureController, IReactionCameraController, ICaptureProjectCompletionSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private readonly ICaptureHostRecorderClient _client;
    private readonly string _silhouetteSettingsDirectory;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly System.Threading.Timer _statusTimer;
    private ManualCaptureState _state = ManualCaptureState.NoTarget;
    private ManualCaptureTarget? _target;
    private string? _lastError;
    private ReplayCaptureStatus _replayStatus = new(
        ReplayCaptureState.Off, null, null, TimeSpan.Zero, 0);
    private ReactionCameraRuntimeStatus _reactionCameraStatus = new(false, false);
    private string? _manualCaptureLibraryRoot;
    private string? _replayCaptureLibraryRoot;
    private int _polling;
    private bool _disposed;

    internal CaptureHostManualRecorder(
        ICaptureHostRecorderClient client,
        string? silhouetteSettingsDirectory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        var settingsDirectory = silhouetteSettingsDirectory ?? SettingsStore.DataDirectory;
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        _silhouetteSettingsDirectory = Path.GetFullPath(settingsDirectory);
        _statusTimer = new System.Threading.Timer(
            _ => _ = PollStatusAsync(),
            null,
            PollInterval,
            PollInterval);
    }

    public ManualCaptureState State { get { lock (_gate) return _state; } }
    public ManualCaptureTarget? Target { get { lock (_gate) return _target; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public event EventHandler? StateChanged;
    public event EventHandler? ReplayStateChanged;
    public event EventHandler? ReactionCameraStateChanged;
    public event EventHandler<CaptureProjectCommittedEventArgs>? ProjectCommitted;
    public ReplayCaptureStatus ReplayStatus { get { lock (_gate) return _replayStatus; } }
    public ReactionCameraRuntimeStatus ReactionCameraStatus
    {
        get { lock (_gate) return _reactionCameraStatus; }
    }

    public async Task<ManualCaptureTarget?> SelectTargetAsync(IWin32Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ThrowIfDisposed();
        if (ManualCaptureStatePolicy.IsPipelineBusy(State))
        {
            throw new InvalidOperationException("Stop the current recording before choosing another game window.");
        }

        try
        {
            var snapshot = await _client.SelectTargetAsync(
                owner.Handle,
                _lifetimeCancellation.Token).ConfigureAwait(false);
            Apply(snapshot);
            return snapshot.Target;
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            SetLocalState(
                ManualCaptureState.Failed,
                "ClipCord lost contact with its isolated capture worker.");
            throw;
        }
    }

    public async Task<ManualCaptureTarget?> DetectTargetAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (ManualCaptureStatePolicy.IsPipelineBusy(State))
        {
            throw new InvalidOperationException("Stop the current recording before detecting another game window.");
        }
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            var snapshot = await _client.DetectTargetAsync(operationCancellation.Token)
                .ConfigureAwait(false);
            Apply(snapshot);
            return snapshot.Target;
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            SetLocalState(
                ManualCaptureState.Failed,
                "ClipCord lost contact with its isolated capture worker.");
            throw;
        }
    }

    public async Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        if (!ManualCaptureStatePolicy.CanStart(State))
        {
            throw new InvalidOperationException(
                ManualCaptureStatePolicy.IsPipelineBusy(State)
                    ? "A ClipCord recording is already active."
                    : "Choose a game window before starting a recording.");
        }
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var normalized = SnapshotSilhouettePreferencesForStart(
            CaptureSettings.Normalize(settings));
        var cameraRequested = IsReactionCameraRequested(normalized);
        var appliedHostSnapshot = false;
        SetLocalState(ManualCaptureState.Starting, null);
        SetLocalReactionCameraStarting(manualCapture: true, cameraRequested);
        try
        {
            var snapshot = await _client.StartRecordingAsync(
                normalized,
                operationCancellation.Token).ConfigureAwait(false);
            lock (_gate) _manualCaptureLibraryRoot = normalized.LibraryRoot;
            Apply(snapshot);
            appliedHostSnapshot = true;
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            const string error = "ClipCord lost contact with its isolated capture worker.";
            SetLocalState(
                ManualCaptureState.Failed,
                error);
            SetLocalReactionCameraStartFailed(
                manualCapture: true,
                cameraRequested,
                error);
            throw;
        }
        finally
        {
            if (!appliedHostSnapshot) ClearLocalReactionCameraStarting(manualCapture: true);
        }
    }

    public async Task StartReplayAsync(
        CaptureSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var normalized = SnapshotSilhouettePreferencesForStart(
            CaptureSettings.Normalize(settings));
        var cameraRequested = IsReactionCameraRequested(normalized);
        var appliedHostSnapshot = false;
        SetLocalReplayState(ReplayCaptureState.Starting, null);
        SetLocalReactionCameraStarting(manualCapture: false, cameraRequested);
        try
        {
            var snapshot = await _client.StartReplayAsync(
                normalized,
                operationCancellation.Token).ConfigureAwait(false);
            lock (_gate) _replayCaptureLibraryRoot = normalized.LibraryRoot;
            Apply(snapshot);
            appliedHostSnapshot = true;
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            const string error = "ClipCord lost contact with its isolated replay worker.";
            SetLocalReplayState(
                ReplayCaptureState.Failed,
                error);
            SetLocalReactionCameraStartFailed(
                manualCapture: false,
                cameraRequested,
                error);
            throw;
        }
        finally
        {
            if (!appliedHostSnapshot) ClearLocalReactionCameraStarting(manualCapture: false);
        }
    }

    public async Task StopReplayAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        SetLocalReplayState(ReplayCaptureState.Stopping, null);
        try
        {
            var snapshot = await _client.StopReplayAsync(operationCancellation.Token)
                .ConfigureAwait(false);
            lock (_gate) _replayCaptureLibraryRoot = null;
            Apply(snapshot);
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            SetLocalReplayState(
                ReplayCaptureState.Failed,
                "ClipCord lost contact while stopping its isolated replay worker.");
            throw;
        }
    }

    public async Task<ManualCaptureResult?> SaveReplayAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        SetLocalReplayState(ReplayCaptureState.Saving, null);
        try
        {
            var snapshot = await _client.SaveReplayAsync(operationCancellation.Token).ConfigureAwait(false);
            string? libraryRoot;
            lock (_gate) libraryRoot = _replayCaptureLibraryRoot;
            Apply(snapshot);
            RaiseProjectCommitted(libraryRoot, snapshot.Result);
            return snapshot.Result;
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            SetLocalReplayState(
                ReplayCaptureState.Failed,
                "ClipCord lost contact while saving the buffered replay.");
            throw;
        }
    }

    public async Task DisableReactionCameraAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            Apply(await _client.DisableReactionCameraAsync(operationCancellation.Token)
                .ConfigureAwait(false));
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            throw new InvalidOperationException(
                "ClipCord lost contact while turning Reaction Camera off.",
                exception);
        }
    }

    public async Task<ManualCaptureResult?> StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!ManualCaptureStatePolicy.CanStop(State)) return null;
        SetLocalState(ManualCaptureState.Finalizing, null);
        try
        {
            var snapshot = await _client.StopRecordingAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            string? libraryRoot;
            lock (_gate)
            {
                libraryRoot = _manualCaptureLibraryRoot;
                _manualCaptureLibraryRoot = null;
            }
            Apply(snapshot);
            RaiseProjectCommitted(libraryRoot, snapshot.Result);
            return snapshot.Result;
        }
        catch (CaptureHostCommandException exception)
        {
            Apply(exception.Snapshot);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            SetLocalState(
                ManualCaptureState.Failed,
                "ClipCord lost contact with its isolated capture worker while saving the recording.");
            throw;
        }
    }

    private async Task PollStatusAsync()
    {
        var cameraStatus = ReactionCameraStatus;
        if (_disposed || (State == ManualCaptureState.NoTarget &&
            ReplayStatus.State == ReplayCaptureState.Off &&
            !cameraStatus.IsActive &&
            !cameraStatus.IsStarting &&
            !cameraStatus.ReleaseNeedsAttention) ||
            Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }
        try
        {
            var snapshot = await _client.GetRecorderStatusAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            Apply(snapshot);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            if (State != ManualCaptureState.NoTarget)
            {
                SetLocalState(
                    ManualCaptureState.Failed,
                    "ClipCord lost contact with its isolated capture worker.");
            }
            if (ReplayStatus.State != ReplayCaptureState.Off)
            {
                SetLocalReplayState(
                    ReplayCaptureState.Failed,
                    "ClipCord lost contact with its isolated replay worker.");
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void Apply(CaptureHostRecorderSnapshot snapshot)
    {
        var changed = false;
        var replayChanged = false;
        var cameraChanged = false;
        lock (_gate)
        {
            changed = _state != snapshot.State ||
                !Equals(_target, snapshot.Target) ||
                !string.Equals(_lastError, snapshot.LastError, StringComparison.Ordinal);
            _state = snapshot.State;
            _target = snapshot.Target;
            _lastError = snapshot.LastError;
            if (snapshot.ReplayStatus is { } replay)
            {
                replayChanged = _replayStatus != replay;
                _replayStatus = replay;
            }
            if (snapshot.ReactionCameraStatus is { } camera)
            {
                cameraChanged = _reactionCameraStatus != camera;
                _reactionCameraStatus = camera;
            }
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        if (replayChanged) ReplayStateChanged?.Invoke(this, EventArgs.Empty);
        if (cameraChanged) ReactionCameraStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetLocalReplayState(ReplayCaptureState state, string? error)
    {
        lock (_gate)
        {
            _replayStatus = _replayStatus with { State = state, LastError = error };
        }
        ReplayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsReactionCameraRequested(CaptureSettings settings) =>
        settings.IncludeReactionCamera &&
        settings.ReactionCameraConsentGranted &&
            !string.IsNullOrWhiteSpace(settings.CameraDeviceId);

    private void RaiseProjectCommitted(string? libraryRoot, ManualCaptureResult? result)
    {
        var project = result?.ReactionCameraLayer;
        if (string.IsNullOrWhiteSpace(libraryRoot) || project is null) return;

        CaptureProjectCommittedEventArgs eventArgs;
        try
        {
            eventArgs = new CaptureProjectCommittedEventArgs(libraryRoot, project.ProjectId);
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            Log.Error("ClipCord could not prepare a post-capture project notification.", exception);
            return;
        }
        var handlers = ProjectCommitted;
        if (handlers is null) return;
        foreach (EventHandler<CaptureProjectCommittedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                // Capture has already committed. A notification subscriber must never turn that
                // successful save into a failed Stop/Save Replay command; reconciliation remains
                // the durable fallback for a missed in-memory notification.
                Log.Error("ClipCord could not notify a post-capture project subscriber.", exception);
            }
        }
    }

    private CaptureSettings SnapshotSilhouettePreferencesForStart(
        CaptureSettings settings)
    {
        if (!settings.IncludeReactionCamera)
        {
            return settings with { SilhouettePreferencesSnapshot = null };
        }

        // This is the single mutable-default read for a capture. The normalized document is
        // serialized into the start request, retained by the child capture pipeline, and later
        // committed with the project. Layout edits made after Start cannot move this clip.
        var snapshot = settings.SilhouettePreferencesSnapshot ??
            SilhouettePreferencesStore.LoadOrDefault(
                _silhouetteSettingsDirectory,
                mirrorCamera: true).Document;
        return CaptureSettings.Normalize(settings with
        {
            SilhouettePreferencesSnapshot = snapshot
        });
    }

    private void SetLocalReactionCameraStarting(bool manualCapture, bool requested)
    {
        if (!requested) return;
        bool changed;
        lock (_gate)
        {
            var updated = manualCapture
                ? _reactionCameraStatus with
                {
                    ManualCaptureStarting = true,
                    LastError = null
                }
                : _reactionCameraStatus with
                {
                    InstantReplayStarting = true,
                    LastError = null
                };
            changed = updated != _reactionCameraStatus;
            _reactionCameraStatus = updated;
        }
        if (changed) ReactionCameraStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearLocalReactionCameraStarting(bool manualCapture)
    {
        bool changed;
        lock (_gate)
        {
            var updated = manualCapture
                ? _reactionCameraStatus with { ManualCaptureStarting = false }
                : _reactionCameraStatus with { InstantReplayStarting = false };
            changed = updated != _reactionCameraStatus;
            _reactionCameraStatus = updated;
        }
        if (changed) ReactionCameraStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetLocalReactionCameraStartFailed(
        bool manualCapture,
        bool requested,
        string error)
    {
        if (!requested) return;
        bool changed;
        lock (_gate)
        {
            var updated = manualCapture
                ? _reactionCameraStatus with
                {
                    ManualCaptureStarting = false,
                    LastError = error
                }
                : _reactionCameraStatus with
                {
                    InstantReplayStarting = false,
                    LastError = error
                };
            changed = updated != _reactionCameraStatus;
            _reactionCameraStatus = updated;
        }
        if (changed) ReactionCameraStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetLocalState(ManualCaptureState state, string? error)
    {
        lock (_gate)
        {
            _state = state;
            _lastError = error;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _statusTimer.Dispose();
        lock (_gate)
        {
            _manualCaptureLibraryRoot = null;
            _replayCaptureLibraryRoot = null;
        }
        ProjectCommitted = null;
        ReplayStateChanged = null;
        ReactionCameraStateChanged = null;
        StateChanged = null;
        // Linked operations may still be unwinding after cancellation. The source is left for GC
        // rather than being disposed underneath those in-flight token registrations.
    }
}
