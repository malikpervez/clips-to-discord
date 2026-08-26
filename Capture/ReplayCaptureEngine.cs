using System.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;

namespace ClipsToDiscord;

internal enum ReplayCaptureState
{
    Off,
    Starting,
    Buffering,
    Saving,
    Stopping,
    Failed
}

internal sealed record ReplayCaptureStatus(
    ReplayCaptureState State,
    ManualCaptureTarget? Target,
    string? LastError,
    TimeSpan BufferedDuration,
    long ResidentBytes,
    ManualCaptureResult? LastResult = null,
    bool ReactionCameraActive = false,
    string? ReactionCameraError = null,
    long ReactionCameraResidentBytes = 0,
    bool ReactionCameraStarting = false,
    bool ReactionCameraReleaseNeedsAttention = false);

internal enum ReplaySaveAvailability
{
    Off,
    WaitingForGame,
    Ready,
    NeedsAttention
}

internal static class ReplayCapturePolicy
{
    internal const string WaitingForGameAfterClose =
        "The detected game window closed. ClipCord is waiting for the next supported game.";

    internal static bool IsWaitingForGame(
        bool instantReplayEnabled,
        ReplayCaptureStatus status) =>
        instantReplayEnabled &&
        (status.State == ReplayCaptureState.Off ||
         status.State == ReplayCaptureState.Failed &&
         status.LastError?.Contains("game window closed", StringComparison.OrdinalIgnoreCase) == true);

    internal static bool IsUnavailableGameTargetFailure(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.Contains("game window closed", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("capture item for that game window", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("valid game window is required", StringComparison.OrdinalIgnoreCase));

    internal static bool IsSameTarget(
        GameWindowCandidate? activeTarget,
        GameWindowCandidate unavailableTarget) =>
        activeTarget is not null &&
        activeTarget.WindowHandle == unavailableTarget.WindowHandle &&
        activeTarget.ProcessId == unavailableTarget.ProcessId;

    internal static bool ShouldAttemptAutomaticStart(
        bool instantReplayEnabled,
        bool hotkeyAvailable,
        ReplayCaptureStatus status) =>
        hotkeyAvailable && IsWaitingForGame(instantReplayEnabled, status);

    internal static bool CanSave(
        bool instantReplayEnabled,
        ReplayCaptureStatus status) =>
        instantReplayEnabled && status.State == ReplayCaptureState.Buffering;

    internal static ReplaySaveAvailability ClassifySaveAvailability(
        bool instantReplayEnabled,
        ReplayCaptureStatus status)
    {
        if (!instantReplayEnabled) return ReplaySaveAvailability.Off;
        if (CanSave(true, status)) return ReplaySaveAvailability.Ready;
        return IsWaitingForGame(true, status)
            ? ReplaySaveAvailability.WaitingForGame
            : ReplaySaveAvailability.NeedsAttention;
    }

    internal static (ReplayCaptureState State, string? Error) CompleteSaveSuccess(
        ReplayCaptureState currentState,
        string? currentError) =>
        currentState == ReplayCaptureState.Saving
            ? (ReplayCaptureState.Buffering, null)
            : (currentState, currentError);

    internal static (ReplayCaptureState State, string? Error) CompleteSaveFailure(
        ReplayCaptureState currentState,
        string? currentError,
        bool encoderRunning,
        string publicSaveError) =>
        currentState == ReplayCaptureState.Saving
            ? (encoderRunning ? ReplayCaptureState.Buffering : ReplayCaptureState.Failed,
                publicSaveError)
            : (currentState, currentError);
}

internal static class ReplayAutomaticStartRunner
{
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    internal static async Task RunAsync(
        IReplayCaptureController controller,
        Func<CaptureSettings> settingsProvider,
        Func<bool> hotkeyAvailableProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(settingsProvider);
        ArgumentNullException.ThrowIfNull(hotkeyAvailableProvider);
        ArgumentNullException.ThrowIfNull(delayAsync);

        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = CaptureSettings.Normalize(settingsProvider());
            if (!ReplayCapturePolicy.ShouldAttemptAutomaticStart(
                    settings.InstantReplayEnabled,
                    hotkeyAvailableProvider(),
                    controller.ReplayStatus))
            {
                return;
            }

            try
            {
                await controller.StartReplayAsync(settings, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CaptureHostCommandException exception) when (
                exception.Snapshot.ReplayStatus is { } replayStatus &&
                ReplayCapturePolicy.IsWaitingForGame(
                    settings.InstantReplayEnabled,
                    replayStatus))
            {
                await delayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal static class ReplayCaptureTargetAcquisition
{
    internal static T Create<T>(
        GameWindowCandidate candidate,
        Func<nint, int, bool> isWindowOwnedByProcess,
        Func<nint, T> createForWindow)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(isWindowOwnedByProcess);
        ArgumentNullException.ThrowIfNull(createForWindow);
        if (!isWindowOwnedByProcess(candidate.WindowHandle, candidate.ProcessId))
        {
            throw new InvalidOperationException(ReplayCapturePolicy.WaitingForGameAfterClose);
        }
        try
        {
            return createForWindow(candidate.WindowHandle);
        }
        catch (Exception exception) when (
            GraphicsCaptureItemFactory.IsUnavailableWindowFailure(exception))
        {
            throw new InvalidOperationException(
                ReplayCapturePolicy.WaitingForGameAfterClose,
                exception);
        }
    }
}

internal interface IReplayCaptureController
{
    ReplayCaptureStatus ReplayStatus { get; }
    event EventHandler? ReplayStateChanged;
    Task StartReplayAsync(CaptureSettings settings, CancellationToken cancellationToken = default);
    Task StopReplayAsync(CancellationToken cancellationToken = default);
    Task<ManualCaptureResult?> SaveReplayAsync(CancellationToken cancellationToken = default);
}

internal interface IReplayTargetUnavailableSource
{
    event Action<GameWindowCandidate>? TargetUnavailable;
}

internal interface IReplayTargetLifetimeSignal : IDisposable
{
    void Start(Action unavailable);
}

internal sealed class ProcessExitReplayTargetSignal(Process process) : IReplayTargetLifetimeSignal
{
    private readonly Process _process = process ?? throw new ArgumentNullException(nameof(process));
    private EventHandler? _handler;
    private bool _disposed;

    public void Start(Action unavailable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(unavailable);
        if (_handler is not null) throw new InvalidOperationException("The process lifetime signal is already active.");
        _handler = (_, _) => unavailable();
        try
        {
            _process.Exited += _handler;
            _process.EnableRaisingEvents = true;
            if (_process.HasExited) unavailable();
        }
        catch
        {
            _process.Exited -= _handler;
            _handler = null;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handler is not null) _process.Exited -= _handler;
        _handler = null;
    }
}

internal sealed class CaptureItemClosedReplayTargetSignal(GraphicsCaptureItem captureItem) :
    IReplayTargetLifetimeSignal
{
    private readonly GraphicsCaptureItem _captureItem = captureItem ??
        throw new ArgumentNullException(nameof(captureItem));
    private Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object>? _handler;
    private bool _disposed;

    public void Start(Action unavailable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(unavailable);
        if (_handler is not null) throw new InvalidOperationException("The capture-item lifetime signal is already active.");
        _handler = (_, _) => unavailable();
        _captureItem.Closed += _handler;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handler is not null) _captureItem.Closed -= _handler;
        _handler = null;
    }
}

internal sealed class ReplayTargetLifetimeSubscription : IDisposable
{
    private readonly GameWindowCandidate _candidate;
    private readonly IReplayTargetLifetimeSignal _processExited;
    private readonly IReplayTargetLifetimeSignal _captureItemClosed;
    private readonly Action<GameWindowCandidate> _unavailable;
    private int _attached;
    private int _armed;
    private int _isUnavailable;
    private int _delivered;
    private int _disposed;

    internal ReplayTargetLifetimeSubscription(
        GameWindowCandidate candidate,
        IReplayTargetLifetimeSignal processExited,
        IReplayTargetLifetimeSignal captureItemClosed,
        Action<GameWindowCandidate> unavailable)
    {
        _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        _processExited = processExited ?? throw new ArgumentNullException(nameof(processExited));
        _captureItemClosed = captureItemClosed ?? throw new ArgumentNullException(nameof(captureItemClosed));
        _unavailable = unavailable ?? throw new ArgumentNullException(nameof(unavailable));
    }

    internal bool IsUnavailable => Volatile.Read(ref _isUnavailable) != 0;

    internal void Attach()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _attached, 1) != 0)
        {
            throw new InvalidOperationException("The replay target lifetime is already attached.");
        }
        try
        {
            _captureItemClosed.Start(SignalUnavailable);
            _processExited.Start(SignalUnavailable);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Arm()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _attached) == 0)
        {
            throw new InvalidOperationException("Attach the replay target lifetime before arming it.");
        }
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        TryDeliver();
    }

    private void SignalUnavailable()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _isUnavailable, 1);
        TryDeliver();
    }

    private void TryDeliver()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _armed) == 0 ||
            Volatile.Read(ref _isUnavailable) == 0 ||
            Interlocked.Exchange(ref _delivered, 1) != 0)
        {
            return;
        }
        _unavailable(_candidate);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _captureItemClosed.Dispose();
        _processExited.Dispose();
    }
}

internal sealed class WindowsReplayCaptureEngine : IDisposable, IReplayTargetUnavailableSource
{
    private readonly object _gate = new();
    private readonly Func<nint, int, bool> _isWindowOwnedByProcess;
    private GraphicsCaptureItem? _captureItem;
    private EncodedReplayRing? _videoRing;
    private ReplayVideoSegmentEncoder? _videoEncoder;
    private EncodedReplayRing? _cameraRing;
    private ReplayVideoSegmentEncoder? _cameraEncoder;
    private ReplayAudioSession? _audioSession;
    private Process? _targetProcess;
    private ReplayTargetLifetimeSubscription? _targetLifetime;
    private CaptureSettings? _settings;
    private ManualCaptureTarget? _target;
    private GameWindowCandidate? _targetCandidate;
    private string? _lastError;
    private ManualCaptureResult? _lastResult;
    private string? _cameraError;
    private bool _cameraReleaseNeedsAttention;
    private bool _cameraSuppressedForCapture;
    private readonly OptionalCaptureStartup _cameraStartup = new();
    private Task<ReplayVideoSegmentEncoder?>? _cameraStopTask;
    private ReplayVideoSegmentEncoder? _cameraStopEncoder;
    private EncodedReplayRing? _cameraStopRing;
    private ReplayCaptureState _state = ReplayCaptureState.Off;
    private int _primaryFailureCleanupScheduled;
    private bool _disposed;

    internal WindowsReplayCaptureEngine(
        Func<nint, int, bool>? isWindowOwnedByProcess = null)
    {
        _isWindowOwnedByProcess = isWindowOwnedByProcess ??
            WindowsGameWindowLifetime.IsOwnedByProcess;
    }

    internal ReplayCaptureStatus Status
    {
        get
        {
            bool changed;
            ReplayCaptureStatus status;
            lock (_gate)
            {
                changed = RefreshFailureCore();
                status = new ReplayCaptureStatus(
                    _state,
                    _target,
                    _lastError,
                    _videoRing?.BufferedDuration ?? TimeSpan.Zero,
                    (_videoRing?.TotalBytes ?? 0) +
                        (_audioSession?.TotalBytes ?? 0) +
                        (_cameraRing?.TotalBytes ?? 0),
                    _lastResult,
                    _cameraEncoder?.IsRunning == true && _cameraEncoder.Failure is null,
                    _cameraError,
                    _cameraRing?.TotalBytes ?? 0,
                    _cameraStartup.IsStarting,
                    _cameraReleaseNeedsAttention);
            }
            if (changed) RaiseStateChanged();
            return status;
        }
    }

    internal event EventHandler? StateChanged;
    public event Action<GameWindowCandidate>? TargetUnavailable;

    internal async Task StartAsync(
        GameWindowCandidate candidate,
        CaptureSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        settings = CaptureSettings.Normalize(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var priorCameraStop = GetOrStartReactionCameraStopTask();
        try
        {
            if (!await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                    priorCameraStop,
                    TimeSpan.Zero,
                    cancellationToken).ConfigureAwait(false))
            {
                AbandonReactionCameraStop(
                    priorCameraStop,
                    "A previous Reaction Camera session is still releasing. Gameplay buffering will continue without a new camera session.");
            }
        }
        catch (Exception exception)
        {
            Log.Error("A previous Reaction Camera session did not release cleanly.", exception);
            AbandonReactionCameraStop(
                priorCameraStop,
                "A previous Reaction Camera session needs attention. Gameplay buffering will continue without a new camera session.");
        }
        lock (_gate)
        {
            RefreshFailureCore();
            if (_state is not (ReplayCaptureState.Off or ReplayCaptureState.Failed))
            {
                throw new InvalidOperationException("ClipCord Instant Replay is already active.");
            }
            CleanupCore(clearRing: true);
            _state = ReplayCaptureState.Starting;
            _cameraSuppressedForCapture = false;
            _lastError = null;
            _lastResult = null;
            _cameraError = null;
        }
        RaiseStateChanged();

        try
        {
            if (!_isWindowOwnedByProcess(candidate.WindowHandle, candidate.ProcessId))
            {
                throw new InvalidOperationException(ReplayCapturePolicy.WaitingForGameAfterClose);
            }
            if (FfmpegCompressor.FindExecutable() is null)
            {
                throw new InvalidOperationException(
                    "ClipCord's bundled FFmpeg tool is required to save Instant Replay clips.");
            }
            var suppressBorder = await BorderlessCaptureAccess.RequestAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var item = ReplayCaptureTargetAcquisition.Create(
                candidate,
                _isWindowOwnedByProcess,
                GraphicsCaptureItemFactory.CreateForWindow);
            Process? targetProcess = null;
            ReplayTargetLifetimeSubscription? targetLifetime = null;
            ReplayVideoSegmentEncoder? encoder = null;
            ReplayAudioSession? audio = null;
            try
            {
                targetProcess = Process.GetProcessById(candidate.ProcessId);
                targetLifetime = new ReplayTargetLifetimeSubscription(
                    candidate,
                    new ProcessExitReplayTargetSignal(targetProcess),
                    new CaptureItemClosedReplayTargetSignal(item),
                    TransitionToWaitingForGame);
                targetLifetime.Attach();
                if (targetLifetime.IsUnavailable)
                {
                    throw new InvalidOperationException(
                        ReplayCapturePolicy.WaitingForGameAfterClose);
                }
                var ring = new EncodedReplayRing(
                    TimeSpan.FromSeconds(settings.ReplaySeconds),
                    ReplayMemoryPolicy.GetGameplayMaximumBytes(settings));
                var outputSize = FitOutputSize(item.Size, settings.Resolution);
                encoder = new ReplayVideoSegmentEncoder(
                    item,
                    outputSize,
                    (uint)CaptureProfileCatalog.GetVideoBitrateKbps(
                        settings.Resolution,
                        settings.FramesPerSecond) * 1000u,
                    (uint)settings.FramesPerSecond,
                    suppressBorder,
                    ring);
                if (settings.HasAudio) audio = ReplayAudioSession.Start(settings);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_state != ReplayCaptureState.Starting)
                    {
                        throw new OperationCanceledException(
                            "Instant Replay stopped while its capture pipeline was starting.");
                    }
                    _captureItem = item;
                    _videoRing = ring;
                    _videoEncoder = encoder;
                    _audioSession = audio;
                    _targetProcess = targetProcess;
                    _targetLifetime = targetLifetime;
                    _settings = settings;
                    _targetCandidate = candidate;
                    _target = new ManualCaptureTarget(
                        candidate.DisplayName,
                        item.Size.Width,
                        item.Size.Height);
                }
                targetLifetime.Arm();
                if (targetLifetime.IsUnavailable)
                {
                    throw new InvalidOperationException(
                        ReplayCapturePolicy.WaitingForGameAfterClose);
                }
                encoder.SegmentReady += VideoSegmentReady;
                await encoder.StartAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    var canStartReactionCamera = ReactionCameraStartPolicy.CanStart(
                        settings.IncludeReactionCamera,
                        _cameraReleaseNeedsAttention,
                        _cameraSuppressedForCapture);
                    if (settings.IncludeReactionCamera &&
                        !canStartReactionCamera &&
                        !_cameraSuppressedForCapture)
                    {
                        _cameraError =
                            "Reaction Camera is unavailable until its previous device release completes or ClipCord restarts.";
                    }
                    if (canStartReactionCamera)
                    {
                        // Publish startup under the same gate used by urgent camera-off. That
                        // makes suppression win before scheduling or cancellation win afterward.
                        _cameraStartup.Start(
                            token => TryStartReactionCameraAsync(settings, token),
                            HandleUnexpectedCameraStartupFailure);
                    }
                }
                lock (_gate)
                {
                    if (_state == ReplayCaptureState.Failed &&
                        string.Equals(
                            _lastError,
                            ReplayCapturePolicy.WaitingForGameAfterClose,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            ReplayCapturePolicy.WaitingForGameAfterClose);
                    }
                }
            }
            catch
            {
                if (targetLifetime is not null && !ReferenceEquals(targetLifetime, _targetLifetime))
                {
                    targetLifetime.Dispose();
                }
                if (targetProcess is not null)
                {
                    if (!ReferenceEquals(targetProcess, _targetProcess)) targetProcess.Dispose();
                }
                encoder?.Dispose();
                audio?.Dispose();
                throw;
            }
            // Remain in Starting until the first complete, independently decodable segment enters
            // the ring. This lets games that temporarily pause rendering resume without forcing the
            // user to toggle Instant Replay off and on again.
        }
        catch (Exception exception)
        {
            FailDuringStart(
                "ClipCord could not start Instant Replay for that game window.",
                exception,
                candidate);
            throw;
        }
    }

    private async Task TryStartReactionCameraAsync(
        CaptureSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state is not (ReplayCaptureState.Starting or ReplayCaptureState.Buffering) ||
                _cameraSuppressedForCapture)
            {
                throw new OperationCanceledException(
                    "Instant Replay stopped Reaction Camera before its device opened.",
                    cancellationToken);
            }
        }
        if (string.IsNullOrWhiteSpace(settings.CameraDeviceId))
        {
            lock (_gate)
            {
                _cameraError = "Reaction Camera is enabled, but no camera device is selected.";
            }
            RaiseStateChanged();
            return;
        }

        var ring = new EncodedReplayRing(
            TimeSpan.FromSeconds(settings.ReplaySeconds),
            ReplayMemoryPolicy.GetReactionCameraMaximumBytes(settings));
        var encoder = new ReplayVideoSegmentEncoder(
            async token => await ReactionCameraFrameSource.CreateAsync(
                settings.CameraDeviceId,
                token).ConfigureAwait(false),
            new SizeInt32(1280, 720),
            (uint)CaptureProfileCatalog.ReactionCameraBitrateKbps * 1000u,
            30,
            ring);
        try
        {
            encoder.SegmentReady += CameraSegmentReady;
            lock (_gate)
            {
                if (_state is not (ReplayCaptureState.Starting or ReplayCaptureState.Buffering) ||
                    _cameraSuppressedForCapture)
                {
                    throw new OperationCanceledException(
                        "Instant Replay stopped while Reaction Camera was starting.");
                }
                // Publish the pending encoder before opening the device. A game-close callback
                // must be able to cancel camera startup immediately instead of leaving the camera
                // active while the UI has already returned to waiting-for-game.
                _cameraRing = ring;
                _cameraEncoder = encoder;
                _cameraStopTask = null;
                _cameraStopEncoder = null;
                _cameraStopRing = null;
            }
            await encoder.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_cameraEncoder, encoder) ||
                    _state is not (ReplayCaptureState.Starting or ReplayCaptureState.Buffering) ||
                    _cameraSuppressedForCapture)
                {
                    throw new OperationCanceledException(
                        "Instant Replay stopped while Reaction Camera was starting.");
                }
                _cameraError = null;
                _cameraReleaseNeedsAttention = false;
            }
            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cameraEncoder, encoder)) _cameraEncoder = null;
                if (ReferenceEquals(_cameraRing, ring)) _cameraRing = null;
            }
            encoder.SegmentReady -= CameraSegmentReady;
            encoder.Dispose();
            ring.Clear();
            RaiseStateChanged();
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cameraEncoder, encoder)) _cameraEncoder = null;
                if (ReferenceEquals(_cameraRing, ring)) _cameraRing = null;
            }
            encoder.SegmentReady -= CameraSegmentReady;
            encoder.Dispose();
            ring.Clear();
            Log.Error("ClipCord could not start the optional Instant Replay camera layer.", exception);
            lock (_gate)
            {
                if (!_cameraReleaseNeedsAttention)
                {
                    _cameraError = exception is ReactionCameraUnavailableException
                        ? exception.Message
                        : "Reaction Camera was unavailable, so gameplay buffering continued without it.";
                }
            }
            RaiseStateChanged();
        }
    }

    private void HandleUnexpectedCameraStartupFailure(Exception exception)
    {
        Log.Error("ClipCord's optional replay camera startup failed unexpectedly.", exception);
        lock (_gate)
        {
            if (!_cameraReleaseNeedsAttention)
            {
                _cameraError =
                    "Reaction Camera was unavailable, so gameplay buffering continued without it.";
            }
        }
        RaiseStateChanged();
    }

    internal async Task<ManualCaptureResult?> SaveAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EncodedReplaySnapshot snapshot;
        EncodedReplaySnapshot? cameraSnapshot;
        ReplayAudioSession? audio;
        CaptureSettings settings;
        string gameName;
        string? cameraWarning;
        lock (_gate)
        {
            RefreshFailureCore();
            if (_state != ReplayCaptureState.Buffering)
            {
                throw new InvalidOperationException(
                    _state == ReplayCaptureState.Failed
                        ? _lastError ?? "Instant Replay stopped unexpectedly."
                        : "Instant Replay is not ready to save yet.");
            }
            snapshot = _videoRing?.Snapshot() ??
                throw new InvalidOperationException("Instant Replay has not buffered a complete video segment yet.");
            settings = _settings ?? throw new InvalidOperationException("Instant Replay settings are unavailable.");
            audio = _audioSession;
            gameName = _target?.DisplayName ?? "Unknown Game";
            cameraWarning = _cameraError;
            if (ReactionCameraPersistenceGate.IsPending)
            {
                cameraSnapshot = null;
                cameraWarning ??=
                    "The replay was saved without a new Reaction Camera layer because a previous camera layer is still being stored.";
            }
            else
            {
                cameraSnapshot = _cameraRing?.Snapshot(
                    snapshot.StartTimestamp,
                    snapshot.EndTimestamp);
            }
            _state = ReplayCaptureState.Saving;
            _lastError = null;
        }
        RaiseStateChanged();
        try
        {
            var result = await ReplaySnapshotMaterializer.SaveAsync(
                snapshot,
                audio,
                settings,
                gameName,
                cancellationToken,
                reactionCameraSnapshot: cameraSnapshot,
                reactionCameraWarning: cameraWarning).ConfigureAwait(false);
            lock (_gate)
            {
                _lastResult = result;
                (_state, _lastError) = ReplayCapturePolicy.CompleteSaveSuccess(
                    _state,
                    _lastError);
            }
            RaiseStateChanged();
            return result;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                var publicError = exception is InvalidOperationException &&
                    !string.IsNullOrWhiteSpace(exception.Message)
                        ? exception.Message
                        : "ClipCord could not save the buffered replay.";
                (_state, _lastError) = ReplayCapturePolicy.CompleteSaveFailure(
                    _state,
                    _lastError,
                    _videoEncoder?.IsRunning == true,
                    publicError);
            }
            Log.Error("ClipCord could not save the buffered replay.", exception);
            RaiseStateChanged();
            throw;
        }
    }

    internal async Task DisableReactionCameraAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool hasCameraWork;
        ReplayVideoSegmentEncoder? cameraEncoder;
        lock (_gate)
        {
            var captureCanStillScheduleCamera = _state is
                ReplayCaptureState.Starting or
                ReplayCaptureState.Buffering or
                ReplayCaptureState.Saving;
            cameraEncoder = _cameraEncoder;
            hasCameraWork = _cameraStartup.IsStarting || cameraEncoder is not null;
            if (!captureCanStillScheduleCamera && !hasCameraWork) return;
            _cameraSuppressedForCapture = true;
            _cameraError =
                "Reaction Camera was turned off. Gameplay buffering continues without new camera frames.";
            cameraEncoder?.RequestStop();
        }
        _cameraStartup.Cancel();
        RaiseStateChanged();
        if (!hasCameraWork) return;
        var stopTask = GetOrStartReactionCameraStopTask();
        try
        {
            if (!await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                    stopTask,
                    OptionalCaptureShutdownPolicy.MaximumWait,
                    cancellationToken).ConfigureAwait(false))
            {
                AbandonReactionCameraStop(
                    stopTask,
                    "Reaction Camera is still releasing after a timeout. Gameplay buffering is still active.");
            }
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not stop the optional replay camera cleanly.", exception);
            AbandonReactionCameraStop(
                stopTask,
                "Reaction Camera stopped after an error. Gameplay buffering is still active.");
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReplayVideoSegmentEncoder? encoder;
        ReplayVideoSegmentEncoder? cameraEncoder;
        ReplayAudioSession? audio;
        lock (_gate)
        {
            if (_state == ReplayCaptureState.Off) return;
            _state = ReplayCaptureState.Stopping;
            encoder = _videoEncoder;
            cameraEncoder = _cameraEncoder;
            audio = _audioSession;
            cameraEncoder?.RequestStop();
        }
        _cameraStartup.Cancel();
        RaiseStateChanged();
        var cameraStopTask = GetOrStartReactionCameraStopTask();
        Exception? failure = null;
        try
        {
            if (encoder is not null) await encoder.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        try
        {
            if (audio is not null) await audio.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { failure ??= exception; }
        try
        {
            if (!await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                    cameraStopTask,
                    OptionalCaptureShutdownPolicy.MaximumWait,
                    cancellationToken).ConfigureAwait(false))
            {
                AbandonReactionCameraStop(
                    cameraStopTask,
                    "Reaction Camera is still releasing after a timeout. Instant Replay stopped safely.");
            }
        }
        catch (Exception exception)
        {
            Log.Error("Reaction Camera stopped after its optional replay input failed.", exception);
            AbandonReactionCameraStop(
                cameraStopTask,
                "Reaction Camera stopped after an error. Instant Replay stopped safely.");
        }
        lock (_gate)
        {
            CleanupCore(clearRing: true);
            _state = ReplayCaptureState.Off;
            _lastError = failure is null ? null : "Instant Replay stopped after a capture input failed.";
        }
        if (failure is not null) Log.Error("Instant Replay stopped after a capture input failed.", failure);
        RaiseStateChanged();
    }

    private Task<ReplayVideoSegmentEncoder?> GetOrStartReactionCameraStopTask()
    {
        TaskCompletionSource<ReplayVideoSegmentEncoder?> completion;
        ReplayVideoSegmentEncoder? cameraEncoder;
        lock (_gate)
        {
            if (_cameraStopTask is not null) return _cameraStopTask;
            if (_cameraEncoder is null && !_cameraStartup.IsStarting)
            {
                return Task.FromResult<ReplayVideoSegmentEncoder?>(null);
            }
            cameraEncoder = _cameraEncoder;
            _cameraStopEncoder = cameraEncoder;
            _cameraStopRing = _cameraRing;
            cameraEncoder?.RequestStop();
            _cameraStartup.Cancel();
            completion = new TaskCompletionSource<ReplayVideoSegmentEncoder?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _cameraStopTask = completion.Task;
        }
        _ = CompleteReactionCameraStopAsync(completion, cameraEncoder);
        return completion.Task;
    }

    private async Task CompleteReactionCameraStopAsync(
        TaskCompletionSource<ReplayVideoSegmentEncoder?> completion,
        ReplayVideoSegmentEncoder? cameraEncoder)
    {
        try
        {
            _cameraStartup.Cancel();
            await _cameraStartup.CancelAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
            cameraEncoder?.RequestStop();
            if (cameraEncoder is not null)
            {
                try
                {
                    await cameraEncoder.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // The replay encoder reports Failure after its worker task has joined. Preserve
                    // the layer warning without falsely claiming that webcam release is unconfirmed.
                    Log.Error("Reaction Camera released after its optional replay layer failed.", exception);
                    lock (_gate)
                    {
                        if (!_cameraReleaseNeedsAttention)
                        {
                            _cameraError =
                                "Reaction Camera stopped after an error. Gameplay buffering continued safely.";
                        }
                    }
                }
            }
            lock (_gate)
            {
                if (ReferenceEquals(_cameraStopEncoder, cameraEncoder))
                {
                    _cameraReleaseNeedsAttention = false;
                }
            }
            completion.TrySetResult(cameraEncoder);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void AbandonReactionCameraStop(Task stopTask, string warning)
    {
        _cameraStartup.Abandon();
        ReplayVideoSegmentEncoder? cameraEncoder;
        EncodedReplayRing? cameraRing;
        lock (_gate)
        {
            if (!ReferenceEquals(_cameraStopTask, stopTask)) return;
            cameraEncoder = _cameraStopEncoder;
            cameraRing = _cameraStopRing;
            if (ReferenceEquals(_cameraEncoder, cameraEncoder))
            {
                if (cameraEncoder is not null) cameraEncoder.SegmentReady -= CameraSegmentReady;
                _cameraEncoder = null;
            }
            if (ReferenceEquals(_cameraRing, cameraRing)) _cameraRing = null;
            _cameraReleaseNeedsAttention = true;
            _cameraError = warning;
        }
        cameraEncoder?.RequestStop();
        if (cameraEncoder is not null) _ = Task.Run(cameraEncoder.Dispose);
        _ = stopTask.ContinueWith(
            completed =>
            {
                cameraRing?.Clear();
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cameraStopTask, stopTask) &&
                            _cameraReleaseNeedsAttention)
                        {
                            _cameraReleaseNeedsAttention = false;
                            _cameraError = null;
                            _cameraStopTask = null;
                            _cameraStopEncoder = null;
                            _cameraStopRing = null;
                        }
                    }
                    RaiseStateChanged();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        RaiseStateChanged();
    }

    private void VideoSegmentReady(object? sender, EventArgs eventArgs)
    {
        lock (_gate)
        {
            if (ReferenceEquals(sender, _videoEncoder) &&
                _state == ReplayCaptureState.Starting)
            {
                _state = ReplayCaptureState.Buffering;
                _lastError = null;
            }
        }
        RaiseStateChanged();
    }

    private void CameraSegmentReady(object? sender, EventArgs eventArgs)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _cameraEncoder)) return;
        }
        RaiseStateChanged();
    }

    private void TransitionToWaitingForGame(GameWindowCandidate candidate)
    {
        ReplayAudioSession? audio;
        GameWindowCandidate? unavailableTarget;
        var changed = false;
        lock (_gate)
        {
            if (_state is not (ReplayCaptureState.Starting or ReplayCaptureState.Buffering or
                ReplayCaptureState.Saving)) return;
            if (!ReplayCapturePolicy.IsSameTarget(_targetCandidate, candidate))
            {
                return;
            }
            _lastError = ReplayCapturePolicy.WaitingForGameAfterClose;
            _state = ReplayCaptureState.Failed;
            _target = null;
            unavailableTarget = _targetCandidate;
            _targetCandidate = null;
            _videoEncoder?.RequestStop();
            _cameraEncoder?.RequestStop();
            audio = _audioSession;
            _audioSession = null;
            changed = true;
        }
        if (changed)
        {
            _cameraStartup.Cancel();
            ScheduleTerminalCameraCleanupCore(
                "Reaction Camera release needs attention after the game window closed. Restart ClipCord before using the camera again.");
        }
        if (changed && unavailableTarget is not null)
        {
            TargetUnavailable?.Invoke(unavailableTarget);
        }
        if (changed) RaiseStateChanged();
        if (audio is not null)
        {
            _ = Task.Run(audio.Dispose);
        }
    }

    private bool RefreshFailureCore()
    {
        var cameraFailure = _cameraEncoder?.Failure;
        if (cameraFailure is not null && _cameraError is null)
        {
            _cameraError = "Reaction Camera stopped unexpectedly. Gameplay buffering is still active.";
            _cameraEncoder?.RequestStop();
            Log.Error(_cameraError, cameraFailure);
        }
        var failure = _videoEncoder?.Failure ?? _audioSession?.Failure;
        if (_state is ReplayCaptureState.Starting or ReplayCaptureState.Buffering or
            ReplayCaptureState.Saving &&
            failure is not null)
        {
            _lastError = "An Instant Replay capture input stopped unexpectedly.";
            _state = ReplayCaptureState.Failed;
            _videoEncoder?.RequestStop();
            _cameraEncoder?.RequestStop();
            _cameraStartup.Cancel();
            var audio = _audioSession;
            _audioSession = null;
            audio?.RequestStop();
            if (audio is not null) _ = Task.Run(audio.Dispose);
            ScheduleTerminalCameraCleanupCore(
                "Reaction Camera release needs attention after Instant Replay stopped unexpectedly. Restart ClipCord before using the camera again.");
            Log.Error(_lastError, failure);
            return true;
        }
        return false;
    }

    private void ScheduleTerminalCameraCleanupCore(string releaseWarning)
    {
        if (Interlocked.Exchange(ref _primaryFailureCleanupScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            var cameraStopTask = GetOrStartReactionCameraStopTask();
            try
            {
                if (!await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                        cameraStopTask,
                        OptionalCaptureShutdownPolicy.MaximumWait,
                        CancellationToken.None).ConfigureAwait(false))
                {
                    AbandonReactionCameraStop(
                        cameraStopTask,
                        releaseWarning);
                    return;
                }

                ReplayVideoSegmentEncoder? cameraEncoder;
                EncodedReplayRing? cameraRing;
                lock (_gate)
                {
                    if (!ReferenceEquals(_cameraStopTask, cameraStopTask)) return;
                    cameraEncoder = _cameraStopEncoder;
                    cameraRing = _cameraStopRing;
                    if (cameraEncoder is not null)
                    {
                        cameraEncoder.SegmentReady -= CameraSegmentReady;
                    }
                    _cameraEncoder = null;
                    _cameraRing = null;
                    _cameraReleaseNeedsAttention = false;
                    _cameraStopTask = null;
                    _cameraStopEncoder = null;
                    _cameraStopRing = null;
                }
                cameraEncoder?.Dispose();
                cameraRing?.Clear();
                RaiseStateChanged();
            }
            catch (Exception exception)
            {
                Log.Error("Reaction Camera could not release after Instant Replay failed.", exception);
                AbandonReactionCameraStop(
                    cameraStopTask,
                    releaseWarning);
            }
            finally
            {
                Volatile.Write(ref _primaryFailureCleanupScheduled, 0);
            }
        });
    }

    private void FailDuringStart(
        string publicMessage,
        Exception exception,
        GameWindowCandidate candidate)
    {
        Log.Error(publicMessage, exception);
        var changed = false;
        var targetUnavailable = ReplayCapturePolicy.IsUnavailableGameTargetFailure(exception.Message);
        lock (_gate)
        {
            if (_state != ReplayCaptureState.Starting) return;
            _lastError = targetUnavailable
                ? ReplayCapturePolicy.WaitingForGameAfterClose
                : exception is InvalidOperationException && !string.IsNullOrWhiteSpace(exception.Message)
                    ? exception.Message
                    : publicMessage;
            CleanupCore(clearRing: true);
            _state = ReplayCaptureState.Failed;
            changed = true;
        }
        if (changed && targetUnavailable) TargetUnavailable?.Invoke(candidate);
        if (changed) RaiseStateChanged();
    }

    private void CleanupCore(bool clearRing)
    {
        _cameraStartup.Cancel();
        _targetLifetime?.Dispose();
        _targetProcess?.Dispose();
        if (_videoEncoder is not null) _videoEncoder.SegmentReady -= VideoSegmentReady;
        if (_cameraEncoder is not null) _cameraEncoder.SegmentReady -= CameraSegmentReady;
        _cameraEncoder?.Dispose();
        _videoEncoder?.Dispose();
        _audioSession?.Dispose();
        if (clearRing) _videoRing?.Clear();
        if (clearRing) _cameraRing?.Clear();
        _captureItem = null;
        _targetProcess = null;
        _targetLifetime = null;
        _videoEncoder = null;
        _cameraEncoder = null;
        _audioSession = null;
        _videoRing = null;
        _cameraRing = null;
        _settings = null;
        _target = null;
        _targetCandidate = null;
        _cameraSuppressedForCapture = false;
        if (!_cameraReleaseNeedsAttention)
        {
            _cameraError = null;
            _cameraStopTask = null;
            _cameraStopEncoder = null;
            _cameraStopRing = null;
        }
    }

    private static SizeInt32 FitOutputSize(SizeInt32 source, CaptureResolution resolution)
    {
        var maximum = CaptureProfileCatalog.GetDimensions(resolution);
        var scale = Math.Min(1d, Math.Min(
            maximum.Width / (double)Math.Max(1, source.Width),
            maximum.Height / (double)Math.Max(1, source.Height)));
        var width = Math.Max(2, (int)Math.Floor(source.Width * scale));
        var height = Math.Max(2, (int)Math.Floor(source.Height * scale));
        return new SizeInt32(width - width % 2, height - height % 2);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        try { StopAsync().GetAwaiter().GetResult(); }
        catch { }
        _disposed = true;
        _cameraStartup.Dispose();
        lock (_gate) CleanupCore(clearRing: true);
        StateChanged = null;
        TargetUnavailable = null;
    }
}
