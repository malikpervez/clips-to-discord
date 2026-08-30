using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClipsToDiscord;

internal static class CapturePipelineArbitration
{
    internal static bool CanStartManual(ReplayCaptureState replayState) =>
        replayState == ReplayCaptureState.Off;

    internal static bool CanStartReplay(ManualCaptureState manualState) =>
        !ManualCaptureStatePolicy.IsPipelineBusy(manualState);
}

internal static class CaptureHostCommandGate
{
    internal static void EnsureAllowed(
        string requestType,
        ManualCaptureState manualState,
        ReplayCaptureState replayState)
    {
        if (requestType == CaptureHostProtocol.StartRecording &&
            !CapturePipelineArbitration.CanStartManual(replayState))
        {
            throw new InvalidOperationException(
                "Turn Instant Replay off before starting a manual game recording.");
        }

        if (requestType == CaptureHostProtocol.StartReplay &&
            !CapturePipelineArbitration.CanStartReplay(manualState))
        {
            throw new InvalidOperationException(
                "Stop the manual game recording before enabling Instant Replay.");
        }
    }
}

internal static class CaptureHostReactionCameraPolicy
{
    internal static CaptureSettings PrepareForStart(
        CaptureSettings settings,
        bool otherPipelineReleaseNeedsAttention,
        long startCommandSequence,
        long latestOffCommandSequence) =>
        SuppressForLaterOff(
            SuppressForOtherPipelineRelease(settings, otherPipelineReleaseNeedsAttention),
            startCommandSequence,
            latestOffCommandSequence);

    internal static CaptureSettings SuppressForOtherPipelineRelease(
        CaptureSettings settings,
        bool otherPipelineReleaseNeedsAttention) =>
        otherPipelineReleaseNeedsAttention && settings.IncludeReactionCamera
            ? settings with { IncludeReactionCamera = false }
            : settings;

    internal static CaptureSettings SuppressForLaterOff(
        CaptureSettings settings,
        long startCommandSequence,
        long latestOffCommandSequence) =>
        latestOffCommandSequence > 0 &&
        startCommandSequence <= latestOffCommandSequence &&
        settings.IncludeReactionCamera
            ? settings with { IncludeReactionCamera = false }
            : settings;
}

internal static class CaptureHostReactionCameraOrder
{
    internal static long RecordOff(ref long latestOffSequence, long commandSequence)
    {
        if (commandSequence <= 0)
        {
            throw new InvalidDataException(
                "The urgent Reaction Camera command did not include a causal sequence.");
        }

        var observed = Volatile.Read(ref latestOffSequence);
        while (commandSequence > observed)
        {
            var prior = Interlocked.CompareExchange(
                ref latestOffSequence,
                commandSequence,
                observed);
            if (prior == observed) return commandSequence;
            observed = prior;
        }
        return observed;
    }
}

internal static class CaptureHostResponseFactory
{
    internal static CaptureHostMessage Create(
        CaptureHostMessage request,
        bool ok,
        int hostProcessId,
        ManualCaptureState recorderState,
        ManualCaptureTarget? recorderTarget,
        string? recorderError,
        ReplayCaptureStatus replay,
        string? error = null,
        ManualCaptureResult? result = null,
        bool manualReactionCameraActive = false,
        string? manualReactionCameraError = null,
        bool manualReactionCameraStarting = false,
        bool manualReactionCameraReleaseNeedsAttention = false) =>
        new(
            CaptureHostProtocol.Version,
            CaptureHostProtocol.Response,
            request.RequestId,
            ok,
            hostProcessId,
            error,
            RecorderState: recorderState,
            Target: recorderTarget,
            Result: result,
            RecorderError: recorderError,
            ReplayState: replay.State,
            ReplayTarget: replay.Target,
            ReplayError: replay.LastError,
            ReplayBufferedMilliseconds: (long)Math.Max(0, replay.BufferedDuration.TotalMilliseconds),
            ReplayResidentBytes: replay.ResidentBytes,
            ManualReactionCameraActive: manualReactionCameraActive,
            ManualReactionCameraError: manualReactionCameraError,
            ReplayReactionCameraActive: replay.ReactionCameraActive,
            ReplayReactionCameraError: replay.ReactionCameraError,
            ReplayReactionCameraResidentBytes: replay.ReactionCameraResidentBytes,
            ManualReactionCameraStarting: manualReactionCameraStarting,
            ReplayReactionCameraStarting: replay.ReactionCameraStarting,
            ManualReactionCameraReleaseNeedsAttention: manualReactionCameraReleaseNeedsAttention,
            ReplayReactionCameraReleaseNeedsAttention: replay.ReactionCameraReleaseNeedsAttention);
}

internal static class CaptureHostParentLifetime
{
    internal static async Task WatchAsync(
        int parentProcessId,
        Action parentExited,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        ArgumentNullException.ThrowIfNull(parentExited);
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The parent exited before the watcher attached.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (!cancellationToken.IsCancellationRequested) parentExited();
    }
}

internal static class CaptureHostProcess
{
    internal static int Run(CaptureHostLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            ApplicationConfiguration.Initialize();
            using var context = new CaptureHostApplicationContext(options);
            Application.Run(context);
            return context.ExitCode;
        }
        catch
        {
            return 20;
        }
    }
}

internal sealed class CaptureHostGameTargetTracker : IDisposable
{
    private readonly IReplayTargetUnavailableSource _replay;
    private readonly ForegroundGameWindowMonitor _monitor;
    private bool _disposed;

    internal CaptureHostGameTargetTracker(
        IReplayTargetUnavailableSource replay,
        Func<int, bool>? isProcessAlive = null,
        Func<nint, int, bool>? isWindowOwnedByProcess = null)
    {
        _replay = replay ?? throw new ArgumentNullException(nameof(replay));
        _monitor = new ForegroundGameWindowMonitor(
            isProcessAlive ?? WindowsGameProcessLifetime.IsAlive,
            isWindowOwnedByProcess ?? WindowsGameWindowLifetime.IsOwnedByProcess);
        _replay.TargetUnavailable += ReplayTargetUnavailable;
    }

    internal GameWindowCandidate? Observe(GameWindowCandidate? candidate, DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _monitor.Observe(candidate, now);
    }

    internal GameWindowCandidate? Current(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _monitor.Current(now);
    }

    private void ReplayTargetUnavailable(GameWindowCandidate candidate) =>
        _monitor.Invalidate(candidate.WindowHandle, candidate.ProcessId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _replay.TargetUnavailable -= ReplayTargetUnavailable;
    }
}

internal sealed class CaptureHostApplicationContext : ApplicationContext
{
    private readonly CaptureHostLaunchOptions _options;
    private readonly Form _dispatcher;
    private readonly WindowsManualCaptureRecorder _recorder = new();
    private readonly WindowsReplayCaptureEngine _replay = new();
    private readonly IForegroundWindowSource _foregroundWindowSource = new WindowsForegroundWindowSource();
    private readonly CaptureHostGameTargetTracker _gameTargets;
    private readonly System.Windows.Forms.Timer _foregroundPollTimer;
    private long _latestReactionCameraOffSequence;
    private int _exitRequested;

    internal CaptureHostApplicationContext(CaptureHostLaunchOptions options)
    {
        _options = options;
        _dispatcher = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32_000, -32_000),
            Size = new Size(1, 1),
            Opacity = 0
        };
        MainForm = _dispatcher;
        _gameTargets = new CaptureHostGameTargetTracker(_replay);
        _dispatcher.Shown += DispatcherShown;
        _foregroundPollTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _foregroundPollTimer.Tick += ForegroundPollTimerTick;
    }

    internal int ExitCode { get; private set; }

    private void DispatcherShown(object? sender, EventArgs eventArgs)
    {
        _dispatcher.Hide();
        ObserveForegroundWindow();
        _foregroundPollTimer.Start();
        _ = Task.Run(() => CaptureHostParentLifetime.WatchAsync(
            _options.ParentProcessId,
            () => Environment.Exit(0)));
        _ = Task.Run(RunPipeAsync);
        _ = Task.Run(RunUrgentPipeAsync);
    }

    private async Task RunPipeAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var connectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            while (true)
            {
                var payload = await reader.ReadLineAsync().ConfigureAwait(false);
                if (payload is null) break;

                CaptureHostMessage request;
                try
                {
                    request = CaptureHostProtocol.Deserialize(payload);
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException)
                {
                    ExitCode = 21;
                    break;
                }

                var response = await HandleRequestAsync(request).ConfigureAwait(false);
                await writer.WriteLineAsync(CaptureHostProtocol.Serialize(response)).ConfigureAwait(false);
                if (request.Type == CaptureHostProtocol.Shutdown && response.Ok)
                {
                    RequestExit(0);
                    return;
                }
            }
            RequestExit(0);
        }
        catch
        {
            RequestExit(20);
        }
    }

    private async Task RunUrgentPipeAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _options.UrgentPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var connectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            while (true)
            {
                var payload = await reader.ReadLineAsync().ConfigureAwait(false);
                if (payload is null) return;

                CaptureHostMessage request;
                try
                {
                    request = CaptureHostProtocol.Deserialize(payload);
                }
                catch (Exception exception) when (exception is InvalidDataException or JsonException)
                {
                    Log.Error("The isolated capture worker rejected an invalid urgent command.", exception);
                    return;
                }

                var response = await HandleUrgentRequestAsync(request).ConfigureAwait(false);
                await writer.WriteLineAsync(CaptureHostProtocol.Serialize(response)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The ordinary control channel and gameplay pipeline remain authoritative. Losing the
            // urgent channel must not tear down a replay save; the parent decides whether an
            // irrecoverable camera-off signaling failure requires terminating this process.
            Log.Error("The isolated capture worker lost its urgent camera control channel.", exception);
        }
    }

    private Task<CaptureHostMessage> HandleUrgentRequestAsync(CaptureHostMessage request)
    {
        if (request.ProtocolVersion != CaptureHostProtocol.Version)
        {
            return Task.FromResult(Response(request, false, "Unsupported capture-host protocol."));
        }
        if (!CaptureHostCommandRouting.UsesUrgentChannel(request.Type))
        {
            return Task.FromResult(Response(request, false, "Unsupported urgent capture-host request."));
        }
        try
        {
            CaptureHostReactionCameraOrder.RecordOff(
                ref _latestReactionCameraOffSequence,
                request.CommandSequence);
        }
        catch (InvalidDataException exception)
        {
            return Task.FromResult(Response(request, false, exception.Message));
        }

        return InvokeOnUiAsync(async () =>
        {
            try
            {
                await DisableReactionCameraCoreAsync().ConfigureAwait(true);
                return Response(request, true);
            }
            catch (Exception exception)
            {
                Log.Error("The isolated capture worker could not turn Reaction Camera off.", exception);
                return Response(
                    request,
                    false,
                    "ClipCord could not confirm that Reaction Camera released. Exit ClipCord to guarantee release.");
            }
        });
    }

    private async Task DisableReactionCameraCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await _recorder.DisableReactionCameraAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await _replay.DisableReactionCameraAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "One or more Reaction Camera pipelines did not release cleanly.",
                failure);
        }
    }

    private Task<CaptureHostMessage> HandleRequestAsync(CaptureHostMessage request)
    {
        if (request.ProtocolVersion != CaptureHostProtocol.Version)
        {
            return Task.FromResult(Response(request, false, "Unsupported capture-host protocol."));
        }
        if (CaptureHostCommandRouting.UsesUrgentChannel(request.Type))
        {
            return Task.FromResult(Response(
                request,
                false,
                "Reaction Camera off must use the urgent capture-host channel."));
        }
        if ((request.Type is CaptureHostProtocol.StartRecording or CaptureHostProtocol.StartReplay) &&
            request.CommandSequence <= 0)
        {
            return Task.FromResult(Response(
                request,
                false,
                "The capture-host start command did not include a causal sequence."));
        }

        return InvokeOnUiAsync(async () =>
        {
            ManualCaptureResult? result = null;
            try
            {
                CaptureHostCommandGate.EnsureAllowed(
                    request.Type,
                    _recorder.State,
                    _replay.Status.State);
                switch (request.Type)
                {
                    case CaptureHostProtocol.Hello:
                    case CaptureHostProtocol.Ping:
                    case CaptureHostProtocol.Status:
                        break;
                    case CaptureHostProtocol.SelectTarget:
                        if (request.OwnerWindowHandle == 0)
                        {
                            throw new InvalidOperationException("ClipCord did not provide a valid owner window.");
                        }
                        await _recorder.SelectTargetAsync(
                            new WindowHandleOwner((nint)request.OwnerWindowHandle)).ConfigureAwait(true);
                        break;
                    case CaptureHostProtocol.DetectTarget:
                        var candidate = _gameTargets.Current(DateTimeOffset.UtcNow) ??
                            throw new InvalidOperationException(
                                "ClipCord has not detected a stable game window yet. Focus the game briefly, then try again or use Choose game.");
                        await _recorder.SelectTargetAsync(
                            candidate.WindowHandle,
                            candidate.DisplayName).ConfigureAwait(true);
                        break;
                    case CaptureHostProtocol.StartRecording:
                        var manualSettings = request.Settings ??
                            throw new InvalidOperationException("ClipCord did not provide capture settings.");
                        manualSettings = PrepareReactionCameraForStart(
                            manualSettings,
                            _replay.Status.ReactionCameraReleaseNeedsAttention,
                            request.CommandSequence);
                        await _recorder.StartAsync(manualSettings)
                            .ConfigureAwait(true);
                        break;
                    case CaptureHostProtocol.StopRecording:
                        result = await _recorder.StopAsync().ConfigureAwait(true);
                        break;
                    case CaptureHostProtocol.StartReplay:
                        var replayCandidate = _gameTargets.Current(DateTimeOffset.UtcNow) ??
                            throw new InvalidOperationException(
                                "ClipCord has not detected a stable game window yet. Focus the game briefly, then enable Instant Replay again.");
                        var replaySettings = request.Settings ?? throw new InvalidOperationException(
                            "ClipCord did not provide Instant Replay settings.");
                        replaySettings = PrepareReactionCameraForStart(
                            replaySettings,
                            _recorder.ReactionCameraReleaseNeedsAttention,
                            request.CommandSequence);
                        await _replay.StartAsync(
                            replayCandidate,
                            replaySettings).ConfigureAwait(true);
                        break;
                    case CaptureHostProtocol.StopReplay:
                        await _replay.StopAsync().ConfigureAwait(true);
                        break;
                    case CaptureHostProtocol.SaveReplay:
                        result = await _replay.SaveAsync().ConfigureAwait(true);
                        break;
                    case CaptureHostProtocol.Shutdown:
                        if (_replay.Status.State != ReplayCaptureState.Off)
                        {
                            await _replay.StopAsync().ConfigureAwait(true);
                        }
                        if (_recorder.State == ManualCaptureState.Recording)
                        {
                            result = await _recorder.StopAsync().ConfigureAwait(true);
                        }
                        break;
                    default:
                        return Response(request, false, "Unsupported capture-host request.");
                }
                return Response(request, true, result: result);
            }
            catch (Exception exception)
            {
                var waitingForGame = request.Type == CaptureHostProtocol.StartReplay &&
                    exception is InvalidOperationException &&
                    ReplayCapturePolicy.IsWaitingForGame(
                        request.Settings?.InstantReplayEnabled == true,
                        _replay.Status);
                if (!waitingForGame)
                {
                    Log.Error("The isolated capture worker could not complete a command.", exception);
                }
                return Response(request, false, GetPublicError(request.Type, exception), result);
            }
        });
    }

    internal CaptureSettings PrepareReactionCameraForStart(
        CaptureSettings settings,
        bool otherPipelineReleaseNeedsAttention,
        long startCommandSequence) =>
        CaptureHostReactionCameraPolicy.PrepareForStart(
            settings,
            otherPipelineReleaseNeedsAttention,
            startCommandSequence,
            Volatile.Read(ref _latestReactionCameraOffSequence));

    private CaptureHostMessage Response(
        CaptureHostMessage request,
        bool ok,
        string? error = null,
        ManualCaptureResult? result = null)
    {
        var replay = _replay.Status;
        return CaptureHostResponseFactory.Create(
            request,
            ok,
            Environment.ProcessId,
            _recorder.State,
            _recorder.Target,
            _recorder.LastError,
            replay,
            error,
            result,
            manualReactionCameraActive: _recorder.ReactionCameraActive,
            manualReactionCameraError: _recorder.ReactionCameraError,
            manualReactionCameraStarting: _recorder.ReactionCameraStarting,
            manualReactionCameraReleaseNeedsAttention:
                _recorder.ReactionCameraReleaseNeedsAttention) with
        {
            AppliedReactionCameraOffSequence = Volatile.Read(
                ref _latestReactionCameraOffSequence)
        };
    }

    private string GetPublicError(string requestType, Exception exception)
    {
        if (requestType == CaptureHostProtocol.StartReplay &&
            exception is InvalidOperationException &&
            !string.IsNullOrWhiteSpace(exception.Message))
        {
            return exception.Message;
        }
        var subsystemError = requestType is CaptureHostProtocol.StartReplay or
            CaptureHostProtocol.StopReplay or CaptureHostProtocol.SaveReplay
                ? _replay.Status.LastError
                : _recorder.LastError;
        return subsystemError ??
            (exception is InvalidOperationException && !string.IsNullOrWhiteSpace(exception.Message)
                ? exception.Message
                : "The isolated capture worker could not complete that request.");
    }

    private Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatcher.BeginInvoke(new Action(async () =>
            {
                try { completion.TrySetResult(await action().ConfigureAwait(true)); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        return completion.Task;
    }

    private void RequestExit(int exitCode)
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0) return;
        ExitCode = exitCode;
        try
        {
            _dispatcher.BeginInvoke(new Action(ExitThread));
        }
        catch
        {
            Environment.Exit(exitCode);
        }
    }

    private void ForegroundPollTimerTick(object? sender, EventArgs eventArgs) =>
        ObserveForegroundWindow();

    private void ObserveForegroundWindow()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = _foregroundWindowSource.InspectForegroundWindow();
            var candidate = GameWindowCandidatePolicy.Create(
                snapshot,
                now,
                Environment.ProcessId,
                _options.ParentProcessId);
            _gameTargets.Observe(candidate, now);
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not inspect the foreground game-window candidate.", exception);
        }
    }

    protected override void ExitThreadCore()
    {
        _dispatcher.Shown -= DispatcherShown;
        _gameTargets.Dispose();
        _foregroundPollTimer.Stop();
        _recorder.Dispose();
        _replay.Dispose();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recorder.Dispose();
            _replay.Dispose();
            _gameTargets.Dispose();
            _foregroundPollTimer.Dispose();
            _dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class WindowHandleOwner(nint handle) : IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
