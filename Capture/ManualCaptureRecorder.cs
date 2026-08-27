using System.Runtime.InteropServices;
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage;
using ApiInformation = Windows.Foundation.Metadata.ApiInformation;

namespace ClipsToDiscord;

internal enum ManualCaptureState
{
    NoTarget,
    Ready,
    Starting,
    Recording,
    Finalizing,
    Failed
}

internal static class ManualCaptureStatePolicy
{
    internal static bool IsPipelineBusy(ManualCaptureState state) =>
        state is ManualCaptureState.Starting or
            ManualCaptureState.Recording or
            ManualCaptureState.Finalizing;

    internal static bool CanStart(ManualCaptureState state) =>
        state == ManualCaptureState.Ready;

    internal static bool CanStop(ManualCaptureState state) =>
        state == ManualCaptureState.Recording;

    internal static bool CanCommitStart(
        ManualCaptureState state,
        bool expectedTargetIsAttached,
        bool hasTarget,
        bool cancellationRequested) =>
        state == ManualCaptureState.Starting &&
        expectedTargetIsAttached &&
        hasTarget &&
        !cancellationRequested;
}

internal static class ReactionCameraLayerCommitPolicy
{
    internal static bool CanCommit(
        bool stoppedCleanly,
        TimeSpan? cameraStart,
        TimeSpan? cameraEnd,
        TimeSpan? gameplayStart,
        string? stagingPath) =>
        stoppedCleanly &&
        cameraStart is not null &&
        cameraEnd is not null &&
        cameraEnd > cameraStart &&
        gameplayStart is not null &&
        !string.IsNullOrWhiteSpace(stagingPath);

    internal static T? CompleteStop<T>(
        T? encoder,
        bool stoppedCleanly,
        string? stagingPath)
        where T : class
    {
        if (stoppedCleanly) return encoder;
        if (!string.IsNullOrWhiteSpace(stagingPath))
        {
            try
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
            }
            catch
            {
                // Best-effort cleanup inside ClipCord's private staging directory.
            }
        }
        return null;
    }
}

internal sealed record ManualCaptureTarget(
    string DisplayName,
    int Width,
    int Height);

internal sealed record ManualCaptureResult(
    string FilePath,
    string GameName,
    DateTimeOffset CapturedAt,
    TimeSpan Duration,
    CaptureProjectSaveResult? ReactionCameraLayer = null,
    string? ReactionCameraWarning = null);

internal interface IManualCaptureRecorder : IDisposable
{
    ManualCaptureState State { get; }
    ManualCaptureTarget? Target { get; }
    string? LastError { get; }
    event EventHandler? StateChanged;
    Task<ManualCaptureTarget?> SelectTargetAsync(IWin32Window owner);
    Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken = default);
    Task<ManualCaptureResult?> StopAsync(CancellationToken cancellationToken = default);
}

internal interface IAutomaticCaptureTargetRecorder
{
    Task<ManualCaptureTarget?> DetectTargetAsync(CancellationToken cancellationToken = default);
}

internal sealed record ReactionCameraRuntimeStatus(
    bool ManualCaptureActive,
    bool InstantReplayActive,
    string? LastError = null,
    bool ManualCaptureStarting = false,
    bool InstantReplayStarting = false,
    bool ReleaseNeedsAttention = false)
{
    internal bool IsActive => ManualCaptureActive || InstantReplayActive;
    internal bool IsStarting => ManualCaptureStarting || InstantReplayStarting;
    internal ReactionCameraRuntimeState State => ReleaseNeedsAttention
        ? ReactionCameraRuntimeState.ReleaseNeedsAttention
        : IsActive
            ? ReactionCameraRuntimeState.Active
            : IsStarting
            ? ReactionCameraRuntimeState.Starting
            : string.IsNullOrWhiteSpace(LastError)
                ? ReactionCameraRuntimeState.Off
                : ReactionCameraRuntimeState.Failed;
}

internal enum ReactionCameraRuntimeState
{
    Off,
    Starting,
    Active,
    ReleaseNeedsAttention,
    Failed
}

internal interface IReactionCameraController
{
    ReactionCameraRuntimeStatus ReactionCameraStatus { get; }
    event EventHandler? ReactionCameraStateChanged;
    Task DisableReactionCameraAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// First end-to-end Capture milestone. The user explicitly selects a Windows capture item, then
/// ClipCord records that exact item into its private staging directory and atomically promotes a
/// completed MP4 into Library/Game. This is intentionally a manual recorder; the replay-buffer
/// toggle stays unavailable until encoded packet retention is implemented.
/// </summary>
internal sealed class WindowsManualCaptureRecorder : IManualCaptureRecorder
{
    private readonly object _gate = new();
    private GraphicsCaptureItem? _captureItem;
    private WgcVideoFileEncoder? _encoder;
    private WgcVideoFileEncoder? _cameraEncoder;
    private CaptureAudioSession? _audioSession;
    private string? _stagingPath;
    private string? _videoStagingPath;
    private string? _cameraStagingPath;
    private string? _finalPath;
    private string? _gameName;
    private DateTimeOffset _capturedAt;
    private DateTimeOffset _startedAt;
    private SizeInt32 _outputSize;
    private CaptureSettings? _activeSettings;
    private string? _cameraWarning;
    private bool _cameraIsActive;
    private bool _cameraReleaseNeedsAttention;
    private bool _cameraSuppressedForCapture;
    private readonly OptionalCaptureStartup _cameraStartup = new();
    private readonly OptionalCaptureStopSingleFlight<WgcVideoFileEncoder?> _cameraStop = new();
    private WgcVideoFileEncoder? _cameraStopEncoder;
    private string? _cameraStopStagingPath;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _startCancellation;
    private bool _disposed;

    public ManualCaptureState State { get; private set; } = ManualCaptureState.NoTarget;
    public ManualCaptureTarget? Target { get; private set; }
    public string? LastError { get; private set; }
    internal bool ReactionCameraActive
    {
        get
        {
            lock (_gate)
            {
                RefreshCameraStateCore();
                return _cameraIsActive;
            }
        }
    }
    internal string? ReactionCameraError
    {
        get
        {
            lock (_gate)
            {
                RefreshCameraStateCore();
                return _cameraWarning;
            }
        }
    }
    internal bool ReactionCameraStarting => _cameraStartup.IsStarting;
    internal bool ReactionCameraReleaseNeedsAttention
    {
        get { lock (_gate) return _cameraReleaseNeedsAttention; }
    }
    public event EventHandler? StateChanged;

    public async Task<ManualCaptureTarget?> SelectTargetAsync(IWin32Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ThrowIfDisposed();
        if (ManualCaptureStatePolicy.IsPipelineBusy(State))
        {
            throw new InvalidOperationException("Stop the current recording before choosing another game window.");
        }
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable on this PC.");
        }

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner.Handle);
        var item = await picker.PickSingleItemAsync();
        if (item is null) return null;

        return AttachCaptureItem(item, item.DisplayName);
    }

    internal Task<ManualCaptureTarget?> SelectTargetAsync(
        nint windowHandle,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (ManualCaptureStatePolicy.IsPipelineBusy(State))
        {
            throw new InvalidOperationException("Stop the current recording before choosing another game window.");
        }
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable on this PC.");
        }
        var item = GraphicsCaptureItemFactory.CreateForWindow(windowHandle);
        return Task.FromResult<ManualCaptureTarget?>(AttachCaptureItem(item, displayName));
    }

    private ManualCaptureTarget AttachCaptureItem(GraphicsCaptureItem item, string displayName)
    {
        lock (_gate)
        {
            DetachCaptureItem();
            _captureItem = item;
            _captureItem.Closed += CaptureItemClosed;
            Target = new ManualCaptureTarget(
                NormalizeTargetName(displayName),
                item.Size.Width,
                item.Size.Height);
            LastError = null;
            State = ManualCaptureState.Ready;
        }
        RaiseStateChanged();
        return Target;
    }

    public async Task StartAsync(
        CaptureSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        settings = CaptureSettings.Normalize(settings);
        GraphicsCaptureItem item;
        WgcVideoFileEncoder? encoder = null;
        string? videoStagingPath = null;
        string? stagingDirectory = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        lock (_gate)
        {
            if (!ManualCaptureStatePolicy.CanStart(State))
            {
                throw new InvalidOperationException(
                    ManualCaptureStatePolicy.IsPipelineBusy(State)
                        ? "A ClipCord test recording is already active."
                        : "Choose a game window before starting a test recording.");
            }
            item = _captureItem ??
                throw new InvalidOperationException("Choose a game window before starting a test recording.");

            _startCancellation = operationCancellation;
            _cameraSuppressedForCapture = false;
            LastError = null;
            State = ManualCaptureState.Starting;
        }
        RaiseStateChanged();

        try
        {
            var suppressSystemBorder = await BorderlessCaptureAccess
                .RequestAsync()
                .ConfigureAwait(false);
            operationCancellation.Token.ThrowIfCancellationRequested();
            if (settings.HasAudio && FfmpegCompressor.FindExecutable() is null)
            {
                throw new InvalidOperationException(
                    "ClipCord's bundled FFmpeg tool is required to record game, microphone, or voice-chat audio.");
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!ReferenceEquals(item, _captureItem))
                {
                    throw new InvalidOperationException(
                        "The selected game window closed before recording could start.");
                }

                _capturedAt = DateTimeOffset.Now;
                _startedAt = _capturedAt;
                _gameName = Target?.DisplayName ?? item.DisplayName;
                var recordingDirectory = CaptureLibraryLayout.GetRecordingDirectory(
                    settings.LibraryRoot,
                    _gameName);
                Directory.CreateDirectory(recordingDirectory);
                _finalPath = CaptureOutputPolicy.CreateAvailablePath(
                    recordingDirectory,
                    _gameName,
                    _capturedAt);

                stagingDirectory = CaptureLibraryLayout.GetStagingDirectory(settings.LibraryRoot);
                Directory.CreateDirectory(stagingDirectory);
                EnsureOrdinaryDirectory(stagingDirectory, "recording staging folder");
                var stageToken = Guid.NewGuid().ToString("N");
                _stagingPath = Path.Combine(
                    stagingDirectory,
                    $"manual-capture-{stageToken}.mp4");
                _videoStagingPath = Path.Combine(
                    stagingDirectory,
                    $"manual-capture-{stageToken}.video.mp4");
                videoStagingPath = _videoStagingPath;
                _cameraStagingPath = settings.IncludeReactionCamera
                    ? Path.Combine(stagingDirectory, $"manual-capture-{stageToken}.camera.mp4")
                    : null;
                _activeSettings = settings;
                if (!_cameraSuppressedForCapture) _cameraWarning = null;

                var outputSize = FitOutputSize(item.Size, settings.Resolution);
                _outputSize = outputSize;
                encoder = new WgcVideoFileEncoder(
                    item,
                    outputSize,
                    (uint)CaptureProfileCatalog.GetVideoBitrateKbps(
                        settings.Resolution,
                        settings.FramesPerSecond) * 1000u,
                    (uint)settings.FramesPerSecond,
                    suppressSystemBorder);
                _encoder = encoder;
            }

            if (settings.HasAudio)
            {
                var audioSession = CaptureAudioSession.Start(settings, stagingDirectory);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _audioSession = audioSession;
                }
            }

            await encoder.StartAsync(videoStagingPath, operationCancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                EnsureStartCanContinueCore(item, operationCancellation.Token);
                State = ManualCaptureState.Recording;
            }
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
                    _cameraWarning =
                        "Reaction Camera is unavailable until its previous device release completes or ClipCord restarts.";
                }
                if (canStartReactionCamera)
                {
                    // Schedule while holding the recorder gate. An urgent camera-off command can
                    // either suppress this capture first or cancel the published startup, never
                    // slip between the decision and publication.
                    _cameraStartup.Start(
                        token => TryStartReactionCameraAsync(item, settings, token),
                        HandleUnexpectedCameraStartupFailure);
                }
            }
            RaiseStateChanged();
        }
        catch (Exception exception)
        {
            FailRecording("ClipCord could not start the selected game recording.", exception);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_startCancellation, operationCancellation))
                {
                    _startCancellation = null;
                }
            }
        }
    }

    private async Task TryStartReactionCameraAsync(
        GraphicsCaptureItem item,
        CaptureSettings settings,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureCameraStartCanContinueCore(item, cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(settings.CameraDeviceId))
        {
            lock (_gate)
            {
                _cameraWarning = "Reaction Camera is enabled, but no camera device is selected.";
            }
            return;
        }

        string cameraPath;
        WgcVideoFileEncoder cameraEncoder;
        lock (_gate)
        {
            EnsureCameraStartCanContinueCore(item, cancellationToken);
            cameraPath = _cameraStagingPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cameraPath)) return;
            cameraEncoder = new WgcVideoFileEncoder(
                async token => await ReactionCameraFrameSource.CreateAsync(
                    settings.CameraDeviceId,
                    token).ConfigureAwait(false),
                new SizeInt32(1280, 720),
                (uint)CaptureProfileCatalog.ReactionCameraBitrateKbps * 1000u,
                30,
                "reaction camera");
            _cameraStop.ClearCompleted();
            _cameraStopEncoder = null;
            _cameraStopStagingPath = null;
            _cameraEncoder = cameraEncoder;
        }
        try
        {
            await cameraEncoder.StartAsync(cameraPath, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                EnsureCameraStartCanContinueCore(item, cancellationToken);
                if (ReferenceEquals(_cameraEncoder, cameraEncoder)) _cameraIsActive = true;
                _cameraReleaseNeedsAttention = false;
            }
            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cameraEncoder, cameraEncoder)) _cameraEncoder = null;
                _cameraIsActive = false;
            }
            cameraEncoder.Dispose();
            TryDelete(cameraPath);
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not start the optional Reaction Camera layer.", exception);
            lock (_gate)
            {
                if (ReferenceEquals(_cameraEncoder, cameraEncoder)) _cameraEncoder = null;
                _cameraIsActive = false;
                if (!_cameraReleaseNeedsAttention)
                {
                    _cameraWarning = exception is ReactionCameraUnavailableException
                        ? exception.Message
                        : "Reaction Camera was unavailable, so gameplay recording continued without it.";
                }
            }
            cameraEncoder.Dispose();
            TryDelete(cameraPath);
            RaiseStateChanged();
        }
    }

    private void HandleUnexpectedCameraStartupFailure(Exception exception)
    {
        Log.Error("ClipCord's optional Reaction Camera startup failed unexpectedly.", exception);
        lock (_gate)
        {
            _cameraIsActive = false;
            if (!_cameraReleaseNeedsAttention)
            {
                _cameraWarning =
                    "Reaction Camera was unavailable, so gameplay recording continued without it.";
            }
        }
        RaiseStateChanged();
    }

    internal async Task DisableReactionCameraAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        bool hasCameraWork;
        WgcVideoFileEncoder? cameraEncoder;
        lock (_gate)
        {
            var captureCanStillScheduleCamera = ManualCaptureStatePolicy.IsPipelineBusy(State);
            cameraEncoder = _cameraEncoder;
            hasCameraWork = _cameraStartup.IsStarting ||
                cameraEncoder is not null ||
                _cameraIsActive;
            if (!captureCanStillScheduleCamera && !hasCameraWork) return;
            _cameraSuppressedForCapture = true;
            _cameraIsActive = false;
            _cameraWarning =
                "Reaction Camera was turned off. Gameplay recording continues without new camera frames.";
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
                    "Reaction Camera is still releasing after a timeout. Gameplay recording is still active.");
            }
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not stop the optional Reaction Camera cleanly.", exception);
            AbandonReactionCameraStop(
                stopTask,
                "Reaction Camera stopped after an error. Gameplay recording is still active.");
        }
    }

    public async Task<ManualCaptureResult?> StopAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var commandStopwatch = Stopwatch.StartNew();
        WgcVideoFileEncoder? encoder;
        WgcVideoFileEncoder? cameraEncoder;
        CaptureAudioSession? audioSession;
        string? stagingPath;
        string? videoStagingPath;
        string? cameraStagingPath;
        string? finalPath;
        string gameName;
        DateTimeOffset capturedAt;
        DateTimeOffset startedAt;
        CaptureSettings? activeSettings;
        string? cameraWarning;
        lock (_gate)
        {
            if (!ManualCaptureStatePolicy.CanStop(State)) return null;
            State = ManualCaptureState.Finalizing;
            encoder = _encoder;
            audioSession = _audioSession;
            stagingPath = _stagingPath;
            videoStagingPath = _videoStagingPath;
            cameraStagingPath = _cameraStagingPath;
            finalPath = _finalPath;
            gameName = _gameName ?? "Unknown Game";
            capturedAt = _capturedAt;
            startedAt = _startedAt;
            activeSettings = _activeSettings;
            _cameraIsActive = false;
        }
        _cameraStartup.Cancel();
        RaiseStateChanged();
        var cameraFinalizeTask = GetOrStartReactionCameraStopTask();

        try
        {
            if (encoder is null || string.IsNullOrWhiteSpace(stagingPath) ||
                string.IsNullOrWhiteSpace(videoStagingPath) ||
                string.IsNullOrWhiteSpace(finalPath))
            {
                throw new InvalidOperationException("The active recording did not have a valid output reservation.");
            }

            // Once finalization begins it is a commit operation: finish closing the encoder and
            // promoting the staged file even if the initiating UI operation is later cancelled.
            var videoStop = encoder.StopAsync(CancellationToken.None);
            var audioStop = audioSession?.StopAsync(CancellationToken.None) ?? Task.CompletedTask;
            await Task.WhenAll(videoStop, audioStop).ConfigureAwait(false);

            var audioTracks = audioSession?.GetMuxTracks() ?? [];
            var videoStart = encoder.FirstFrameSystemRelativeTime ?? TimeSpan.Zero;
            await CaptureAudioMuxer.MuxAsync(
                videoStagingPath,
                audioTracks,
                videoStart,
                stagingPath,
                CancellationToken.None).ConfigureAwait(false);
            var staged = new FileInfo(stagingPath);
            if (!staged.Exists || staged.Length == 0)
            {
                throw new IOException("Windows completed the recording without producing a playable MP4.");
            }

            var captureSettings = activeSettings ??
                throw new InvalidOperationException("The active capture settings are unavailable.");
            var duration = DateTimeOffset.Now - startedAt;
            var journal = await CaptureJournalCaptureCommit.PromoteOriginalAsync(
                    captureSettings.LibraryRoot,
                    stagingPath,
                    finalPath,
                    CaptureJournalSourceKind.ManualCapture,
                    gameName,
                    capturedAt,
                    duration,
                    _outputSize.Width,
                    _outputSize.Height,
                    captureSettings,
                    CancellationToken.None)
                .ConfigureAwait(false);
            cameraEncoder = null;
            lock (_gate) cameraWarning = _cameraWarning;
            try
            {
                if (await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                        cameraFinalizeTask,
                        OptionalCaptureShutdownPolicy.MaximumWait,
                        CancellationToken.None).ConfigureAwait(false))
                {
                    cameraEncoder = await cameraFinalizeTask.ConfigureAwait(false);
                    lock (_gate) cameraWarning = _cameraWarning;
                }
                else
                {
                    cameraWarning =
                        "The gameplay recording was saved, but its Reaction Camera layer did not finalize in time.";
                    AbandonReactionCameraStop(cameraFinalizeTask, cameraWarning);
                    cameraStagingPath = null;
                }
            }
            catch (Exception exception)
            {
                Log.Error("ClipCord could not finish the optional Reaction Camera layer.", exception);
                cameraWarning =
                    "The gameplay recording was saved, but its Reaction Camera layer could not be finalized.";
                AbandonReactionCameraStop(cameraFinalizeTask, cameraWarning);
                cameraStagingPath = null;
            }
            CaptureProjectSaveResult? cameraLayer = null;
            var cameraStart = cameraEncoder?.FirstFrameSystemRelativeTime;
            var cameraEnd = cameraEncoder?.LastFrameSystemRelativeTime;
            var gameplayStart = encoder.FirstFrameSystemRelativeTime;
            if (ReactionCameraLayerCommitPolicy.CanCommit(
                    cameraEncoder is not null,
                    cameraStart,
                    cameraEnd,
                    gameplayStart,
                    cameraStagingPath) &&
                cameraStart is not null &&
                cameraEnd is not null &&
                gameplayStart is not null)
            {
                CancellationTokenSource? persistenceCancellation = null;
                IDisposable? persistenceLease = null;
                if (!ReactionCameraPersistenceGate.TryAcquire(out persistenceLease))
                {
                    cameraWarning =
                        "The gameplay recording was saved without a new Reaction Camera layer because a previous camera layer is still being stored.";
                }
                else
                {
                    try
                    {
                        persistenceCancellation = new CancellationTokenSource();
                        // The UI process attached reusable layout preferences to activeSettings
                        // when this capture started. The fallback is only for direct in-process
                        // callers that bypass the capture-host boundary.
                        var compositionSnapshot = CaptureCompositionSnapshotFactory.Create(
                            captureSettings,
                            finalPath,
                            mirrorCamera: true,
                            capturedAt);
                        var persistenceTask = CaptureProjectStore.SaveReactionCameraLayerAsync(
                            captureSettings.LibraryRoot,
                            finalPath,
                            cameraStagingPath!,
                            gameplayStart.Value,
                            cameraStart.Value,
                            cameraEnd.Value - cameraStart.Value,
                            mirrorCamera: true,
                            compositionSnapshot: compositionSnapshot,
                            createdUtc: capturedAt,
                            cancellationToken: persistenceCancellation.Token);
                        if (await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                                persistenceTask,
                                ReactionCameraPersistencePolicy.GetRemainingBudget(
                                    commandStopwatch.Elapsed),
                                CancellationToken.None).ConfigureAwait(false))
                        {
                            cameraLayer = await persistenceTask.ConfigureAwait(false);
                            persistenceCancellation.Dispose();
                            persistenceCancellation = null;
                            persistenceLease!.Dispose();
                            persistenceLease = null;
                        }
                        else
                        {
                            persistenceCancellation.Cancel();
                            var ownedCancellation = persistenceCancellation;
                            var ownedLease = persistenceLease;
                            _ = persistenceTask.ContinueWith(
                                completed =>
                                {
                                    _ = completed.Exception;
                                    ownedCancellation.Dispose();
                                    ownedLease?.Dispose();
                                },
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                            persistenceCancellation = null;
                            persistenceLease = null;
                            cameraWarning =
                                "The gameplay recording was saved, but storing its Reaction Camera layer timed out.";
                        }
                    }
                    catch (Exception exception)
                    {
                        persistenceCancellation?.Cancel();
                        persistenceCancellation?.Dispose();
                        persistenceLease?.Dispose();
                        Log.Error("ClipCord saved gameplay but could not promote its optional Reaction Camera layer.", exception);
                        cameraWarning = "The gameplay recording was saved, but its Reaction Camera layer could not be stored.";
                    }
                }
            }
            else if (cameraEncoder is not null && cameraWarning is null)
            {
                cameraWarning = "The gameplay recording was saved, but the Reaction Camera did not produce a usable segment.";
            }
            try
            {
                _ = await CaptureJournalCaptureCommit.FinalizeCameraAsync(
                        captureSettings.LibraryRoot,
                        journal.Clip.ClipId,
                        cameraLayer,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Gameplay and its source journal are already committed. Camera projection is
                // optional and startup reconciliation can safely repeat it.
                Log.Error(
                    "ClipCord saved gameplay but could not update its camera journal state.",
                    exception);
                cameraWarning ??=
                    "The gameplay recording was saved, but Reaction Camera processing needs attention.";
            }
            var result = new ManualCaptureResult(
                finalPath,
                gameName,
                capturedAt,
                duration,
                cameraLayer,
                cameraWarning);
            lock (_gate)
            {
                CleanupRecordingState();
                State = _captureItem is null ? ManualCaptureState.NoTarget : ManualCaptureState.Ready;
                LastError = null;
            }
            RaiseStateChanged();
            return result;
        }
        catch (Exception exception)
        {
            try
            {
                if (!await OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                        cameraFinalizeTask,
                        OptionalCaptureShutdownPolicy.MaximumWait,
                        CancellationToken.None).ConfigureAwait(false))
                {
                    AbandonReactionCameraStop(
                        cameraFinalizeTask,
                        "Reaction Camera release needs attention after gameplay finalization failed. Restart ClipCord before using the camera again.");
                }
            }
            catch (Exception cameraException)
            {
                Log.Error(
                    "Reaction Camera could not release after gameplay finalization failed.",
                    cameraException);
                AbandonReactionCameraStop(
                    cameraFinalizeTask,
                    "Reaction Camera release needs attention after gameplay finalization failed. Restart ClipCord before using the camera again.");
            }
            FailRecording("ClipCord could not finish the selected game recording.", exception);
            throw;
        }
    }

    private Task<WgcVideoFileEncoder?> GetOrStartReactionCameraStopTask()
    {
        if (_cameraStop.TryGetCurrent(out var current)) return current!;
        lock (_gate)
        {
            if (_cameraEncoder is null && !_cameraStartup.IsStarting)
            {
                return Task.FromResult<WgcVideoFileEncoder?>(null);
            }
        }
        return _cameraStop.GetOrStart(StopReactionCameraCoreAsync);
    }

    private async Task<WgcVideoFileEncoder?> StopReactionCameraCoreAsync()
    {
        WgcVideoFileEncoder? cameraEncoder;
        string? cameraStagingPath;
        lock (_gate)
        {
            cameraEncoder = _cameraEncoder;
            _cameraStopEncoder = cameraEncoder;
            _cameraStopStagingPath = _cameraStagingPath;
            cameraStagingPath = _cameraStopStagingPath;
            cameraEncoder?.RequestStop();
        }
        _cameraStartup.Cancel();
        await _cameraStartup.CancelAndWaitAsync(CancellationToken.None).ConfigureAwait(false);
        cameraEncoder?.RequestStop();
        var stoppedCleanly = true;
        if (cameraEncoder is not null)
        {
            try
            {
                await cameraEncoder.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // StopAsync reports encoder faults only after its worker has joined. Device
                // ownership is therefore confirmed released even though the optional layer failed.
                Log.Error("Reaction Camera released after its optional layer failed.", exception);
                lock (_gate)
                {
                    if (!_cameraReleaseNeedsAttention)
                    {
                        _cameraWarning =
                            "Reaction Camera stopped after an error. Gameplay recording continued safely.";
                    }
                }
                stoppedCleanly = false;
            }
        }
        return ReactionCameraLayerCommitPolicy.CompleteStop(
            cameraEncoder,
            stoppedCleanly,
            cameraStagingPath);
    }

    private void AbandonReactionCameraStop(Task stopTask, string warning)
    {
        _cameraStartup.Abandon();
        WgcVideoFileEncoder? cameraEncoder;
        string? cameraStagingPath;
        lock (_gate)
        {
            if (!_cameraStop.IsCurrent(stopTask)) return;
            cameraEncoder = _cameraStopEncoder;
            cameraStagingPath = _cameraStopStagingPath;
            if (ReferenceEquals(_cameraEncoder, cameraEncoder)) _cameraEncoder = null;
            if (string.Equals(
                    _cameraStagingPath,
                    cameraStagingPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _cameraStagingPath = null;
            }
            _cameraIsActive = false;
            _cameraReleaseNeedsAttention = true;
            _cameraWarning = warning;
        }
        cameraEncoder?.RequestStop();
        if (cameraEncoder is not null) _ = Task.Run(cameraEncoder.Dispose);
        _ = stopTask.ContinueWith(
            completed =>
            {
                TryDelete(cameraStagingPath);
                if (completed.Status == TaskStatus.RanToCompletion)
                {
                    lock (_gate)
                    {
                        if (_cameraStop.IsCurrent(stopTask) &&
                            _cameraReleaseNeedsAttention)
                        {
                            _cameraReleaseNeedsAttention = false;
                            _cameraWarning = null;
                            _cameraStopEncoder = null;
                            _cameraStopStagingPath = null;
                            _cameraStop.Clear(stopTask);
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

    private void CaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        CancellationTokenSource? startCancellation;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _captureItem)) return;
            var recordingWasActive = ManualCaptureStatePolicy.IsPipelineBusy(State);
            startCancellation = State == ManualCaptureState.Starting
                ? _startCancellation
                : null;
            _encoder?.RequestStop();
            _cameraEncoder?.RequestStop();
            _audioSession?.RequestStop();
            DetachCaptureItem();
            Target = null;
            if (!recordingWasActive)
            {
                State = ManualCaptureState.NoTarget;
            }
            else
            {
                LastError = State == ManualCaptureState.Starting
                    ? "The selected game window closed before recording could start."
                    : "The selected game window closed. Stop the recording to save the frames captured so far.";
            }
        }
        _cameraStartup.Cancel();
        TryCancel(startCancellation);
        RaiseStateChanged();
    }

    private void EnsureStartCanContinueCore(
        GraphicsCaptureItem expectedItem,
        CancellationToken cancellationToken)
    {
        var expectedTargetIsAttached = ReferenceEquals(expectedItem, _captureItem);
        var hasTarget = Target is not null;
        if (ManualCaptureStatePolicy.CanCommitStart(
                State,
                expectedTargetIsAttached,
                hasTarget,
                cancellationToken.IsCancellationRequested))
        {
            return;
        }

        if (!expectedTargetIsAttached || !hasTarget)
        {
            throw new InvalidOperationException(
                "The selected game window closed before recording could start.");
        }
        if (State != ManualCaptureState.Starting)
        {
            throw new InvalidOperationException(
                "The recording was interrupted while it was starting.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "The recording was interrupted while it was starting.");
    }

    private void EnsureCameraStartCanContinueCore(
        GraphicsCaptureItem expectedItem,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(expectedItem, _captureItem) &&
            Target is not null &&
            State == ManualCaptureState.Recording &&
            !_cameraSuppressedForCapture)
        {
            return;
        }
        throw new OperationCanceledException(
            "The recording stopped while Reaction Camera was starting.",
            cancellationToken);
    }

    private void FailRecording(string publicMessage, Exception exception)
    {
        Log.Error(publicMessage, exception);
        lock (_gate)
        {
            LastError = exception is InvalidOperationException &&
                !string.IsNullOrWhiteSpace(exception.Message)
                    ? exception.Message
                    : publicMessage;
            var stagingPath = _stagingPath;
            var libraryRoot = _activeSettings?.LibraryRoot;
            var preservePromotedStage =
                !string.IsNullOrWhiteSpace(stagingPath) &&
                !string.IsNullOrWhiteSpace(libraryRoot) &&
                CaptureJournalPromotionIntentStore.IsOriginalStageProtected(
                    libraryRoot,
                    stagingPath);
            CleanupRecordingState();
            if (!preservePromotedStage) TryDelete(stagingPath);
            State = _disposed ? ManualCaptureState.NoTarget : ManualCaptureState.Failed;
        }
        RaiseStateChanged();
    }

    private void CleanupRecordingState()
    {
        _cameraStartup.Cancel();
        _encoder?.Dispose();
        _encoder = null;
        _cameraEncoder?.Dispose();
        _cameraEncoder = null;
        _cameraIsActive = false;
        _audioSession?.Dispose();
        _audioSession = null;
        TryDelete(_videoStagingPath);
        TryDelete(_cameraStagingPath);
        _stagingPath = null;
        _videoStagingPath = null;
        _cameraStagingPath = null;
        _finalPath = null;
        _gameName = null;
        _outputSize = default;
        _activeSettings = null;
        _cameraSuppressedForCapture = false;
        if (!_cameraReleaseNeedsAttention)
        {
            _cameraWarning = null;
            _cameraStopEncoder = null;
            _cameraStopStagingPath = null;
            _cameraStop.ClearCompleted();
        }
    }

    private void RefreshCameraStateCore()
    {
        if (!_cameraIsActive || _cameraEncoder?.IsRunning != false) return;
        _cameraIsActive = false;
        _cameraWarning ??= _cameraEncoder.Failure is null
            ? "Reaction Camera stopped. Gameplay recording is still active."
            : "Reaction Camera stopped unexpectedly. Gameplay recording is still active.";
    }

    private void DetachCaptureItem()
    {
        if (_captureItem is not null)
        {
            _captureItem.Closed -= CaptureItemClosed;
            _captureItem = null;
        }
    }

    private static SizeInt32 FitOutputSize(SizeInt32 source, CaptureResolution resolution)
    {
        var maximum = CaptureProfileCatalog.GetDimensions(resolution);
        var scale = Math.Min(
            1d,
            Math.Min(
                maximum.Width / (double)Math.Max(1, source.Width),
                maximum.Height / (double)Math.Max(1, source.Height)));
        var width = Math.Max(2, (int)Math.Floor(source.Width * scale));
        var height = Math.Max(2, (int)Math.Floor(source.Height * scale));
        width -= width % 2;
        height -= height % 2;
        return new SizeInt32(width, height);
    }

    private static string NormalizeTargetName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown Game" : value.Trim();

    private static void EnsureOrdinaryDirectory(string path, string description)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            throw new IOException($"The {description} cannot be a symbolic link or junction.");
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup. The path remains inside ClipCord's private staging directory.
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (State == ManualCaptureState.Recording)
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Exit must continue. Private staging cleanup below handles an incomplete file.
            }
        }
        var cameraStopTask = GetOrStartReactionCameraStopTask();
        try
        {
            if (!OptionalCaptureShutdownPolicy.CompletesWithinAsync(
                    cameraStopTask,
                    OptionalCaptureShutdownPolicy.MaximumWait,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            {
                AbandonReactionCameraStop(
                    cameraStopTask,
                    "Reaction Camera release needs attention while ClipCord exits.");
            }
        }
        catch (Exception exception)
        {
            Log.Error("Reaction Camera could not release cleanly while ClipCord exited.", exception);
            AbandonReactionCameraStop(
                cameraStopTask,
                "Reaction Camera release needs attention while ClipCord exits.");
        }
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _cameraStartup.Dispose();
        lock (_gate)
        {
            _encoder?.RequestStop();
            _cameraEncoder?.RequestStop();
            _audioSession?.RequestStop();
            TryDelete(_stagingPath);
            CleanupRecordingState();
            DetachCaptureItem();
            Target = null;
            State = ManualCaptureState.NoTarget;
        }
        _lifetimeCancellation.Dispose();
    }
}

internal sealed class WgcVideoFileEncoder : IDisposable
{
    private const int NoSamplesProcessedHResult = unchecked((int)0xC00D4A44);
    private static readonly TimeSpan FirstSampleTimeout = TimeSpan.FromSeconds(5);
    private readonly GraphicsCaptureItem? _captureItem;
    private readonly Func<CancellationToken, Task<IVideoFrameSource>>? _frameSourceFactory;
    private readonly string _sourceDescription;
    private readonly SizeInt32 _outputSize;
    private readonly uint _bitrate;
    private readonly uint _frameRate;
    private readonly bool _suppressSystemBorder;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopCancellation = new();
    private IVideoFrameSource? _frameSource;
    private Task? _encodingTask;
    private Exception? _callbackFailure;
    private int _samplesAssigned;
    private long _firstFrameTicks = -1;
    private long _lastFrameTicks = -1;
    private bool _recording;
    private bool _disposed;

    internal WgcVideoFileEncoder(
        GraphicsCaptureItem captureItem,
        SizeInt32 outputSize,
        uint bitrate,
        uint frameRate,
        bool suppressSystemBorder = false)
    {
        _captureItem = captureItem ?? throw new ArgumentNullException(nameof(captureItem));
        _outputSize = outputSize;
        _bitrate = bitrate;
        _frameRate = frameRate;
        _suppressSystemBorder = suppressSystemBorder;
        _sourceDescription = "game window";
    }

    internal WgcVideoFileEncoder(
        Func<CancellationToken, Task<IVideoFrameSource>> frameSourceFactory,
        SizeInt32 outputSize,
        uint bitrate,
        uint frameRate,
        string sourceDescription)
    {
        _frameSourceFactory = frameSourceFactory ?? throw new ArgumentNullException(nameof(frameSourceFactory));
        _outputSize = outputSize;
        _bitrate = bitrate;
        _frameRate = frameRate;
        _sourceDescription = string.IsNullOrWhiteSpace(sourceDescription)
            ? "video source"
            : sourceDescription.Trim();
    }

    internal TimeSpan? FirstFrameSystemRelativeTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _firstFrameTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    internal TimeSpan? LastFrameSystemRelativeTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastFrameTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    internal bool IsRunning
    {
        get
        {
            lock (_gate) return _encodingTask is { IsCompleted: false };
        }
    }

    internal Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _callbackFailure ??
                    (_encodingTask?.IsFaulted == true
                        ? _encodingTask.Exception?.GetBaseException()
                        : null);
            }
        }
    }

    internal async Task StartAsync(string outputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_encodingTask is not null)
            {
                throw new InvalidOperationException("This recorder has already been started.");
            }
            _recording = true;
            _encodingTask = Task.Run(() => EncodeAsync(outputPath));
        }

        try
        {
            await _started.Task
                .WaitAsync(FirstSampleTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            await AbortFailedStartAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ClipCord did not receive a video frame from the {_sourceDescription}.",
                exception);
        }
        catch
        {
            await AbortFailedStartAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? encodingTask;
        lock (_gate)
        {
            _recording = false;
            _stopCancellation.Cancel();
            _frameSource?.Stop();
            encodingTask = _encodingTask;
        }
        if (encodingTask is not null)
        {
            try
            {
                await encodingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (COMException exception) when (
                exception.HResult == NoSamplesProcessedHResult &&
                Volatile.Read(ref _samplesAssigned) == 0)
            {
                throw new InvalidOperationException(
                    $"ClipCord did not capture any video frames from the {_sourceDescription}.",
                    GetCallbackFailure() ?? exception);
            }

            var callbackFailure = GetCallbackFailure();
            if (callbackFailure is not null)
            {
                throw new InvalidOperationException(
                    $"ClipCord could not process video frames from the {_sourceDescription}.",
                    callbackFailure);
            }
        }
    }

    private async Task AbortFailedStartAsync()
    {
        RequestStop();
        Task? encodingTask;
        lock (_gate)
        {
            encodingTask = _encodingTask;
        }
        if (encodingTask is null) return;
        try
        {
            await encodingTask.ConfigureAwait(false);
        }
        catch
        {
            // The startup exception or timeout is the actionable error returned to the caller.
        }
    }

    internal void RequestStop()
    {
        lock (_gate)
        {
            _recording = false;
            _stopCancellation.Cancel();
            _frameSource?.Stop();
        }
        if (Volatile.Read(ref _samplesAssigned) == 0)
        {
            _started.TrySetException(new InvalidOperationException(
                $"The {_sourceDescription} stopped before ClipCord received its first video frame."));
        }
    }

    private async Task EncodeAsync(string outputPath)
    {
        try
        {
            if (_frameSourceFactory is not null)
            {
                using var frameSource = await _frameSourceFactory(_stopCancellation.Token)
                    .ConfigureAwait(false);
                await EncodeFrameSourceAsync(frameSource, outputPath).ConfigureAwait(false);
            }
            else
            {
                var captureItem = _captureItem ??
                    throw new InvalidOperationException("ClipCord did not receive a game capture item.");
                using var d3dDevice = D3D11.D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
                using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
                using var winRtDevice = Direct3DInterop.CreateWinRtDevice(dxgiDevice);
                using var frameSource = new CaptureFrameSource(
                    d3dDevice,
                    winRtDevice,
                    captureItem,
                    captureItem.Size,
                    _suppressSystemBorder);
                await EncodeFrameSourceAsync(frameSource, outputPath).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _recording = false;
                _frameSource = null;
            }
        }
    }

    private async Task EncodeFrameSourceAsync(IVideoFrameSource frameSource, string outputPath)
    {
        lock (_gate) _frameSource = frameSource;
        try
        {
            var input = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8,
                (uint)frameSource.SourceSize.Width,
                (uint)frameSource.SourceSize.Height);
            var descriptor = new VideoStreamDescriptor(input);
            var source = new MediaStreamSource(descriptor) { BufferTime = TimeSpan.Zero };
            source.Starting += MediaSourceStarting;
            source.SampleRequested += MediaSourceSampleRequested;

            var profile = new MediaEncodingProfile();
            profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
            profile.Video.Subtype = MediaEncodingSubtypes.H264;
            profile.Video.Width = (uint)_outputSize.Width;
            profile.Video.Height = (uint)_outputSize.Height;
            profile.Video.Bitrate = _bitrate;
            profile.Video.FrameRate.Numerator = _frameRate;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;

            using var placeholder = File.Create(outputPath);
            placeholder.Close();
            var outputFile = await StorageFile.GetFileFromPathAsync(outputPath);
            using var outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                source,
                outputStream,
                profile);
            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException(
                    $"Windows could not prepare the hardware video encoder ({prepared.FailureReason}).");
            }
            try
            {
                await prepared.TranscodeAsync();
            }
            finally
            {
                source.Starting -= MediaSourceStarting;
                source.SampleRequested -= MediaSourceSampleRequested;
            }

            void MediaSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
            {
                try
                {
                    using var frame = frameSource.WaitForNewFrame();
                    if (frame is not null) ObserveFirstFrame(frame.SystemRelativeTime);
                    args.Request.SetActualStartPosition(frame?.SystemRelativeTime ?? TimeSpan.Zero);
                }
                catch (Exception exception)
                {
                    LatchCallbackFailure($"ClipCord could not prepare the first {_sourceDescription} frame.", exception);
                    args.Request.SetActualStartPosition(TimeSpan.Zero);
                    frameSource.Stop();
                }
            }

            void MediaSourceSampleRequested(
                MediaStreamSource sender,
                MediaStreamSourceSampleRequestedEventArgs args)
            {
                try
                {
                    lock (_gate)
                    {
                        if (!_recording)
                        {
                            args.Request.Sample = null;
                            return;
                        }
                    }
                    using var frame = frameSource.WaitForNewFrame();
                    if (frame is null)
                    {
                        args.Request.Sample = null;
                        return;
                    }
                    ObserveFirstFrame(frame.SystemRelativeTime);
                    Interlocked.Exchange(ref _lastFrameTicks, frame.SystemRelativeTime.Ticks);
                    args.Request.Sample = MediaStreamSample.CreateFromDirect3D11Surface(
                        frame.Surface,
                        frame.SystemRelativeTime);
                    Interlocked.Increment(ref _samplesAssigned);
                    _started.TrySetResult();
                }
                catch (Exception exception)
                {
                    LatchCallbackFailure($"ClipCord could not convert a {_sourceDescription} frame.", exception);
                    args.Request.Sample = null;
                    frameSource.Stop();
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_frameSource, frameSource)) _frameSource = null;
            }
        }
    }

    private void LatchCallbackFailure(string message, Exception exception)
    {
        var wrapped = new InvalidOperationException(message, exception);
        var shouldLog = false;
        lock (_gate)
        {
            if (_callbackFailure is null)
            {
                _callbackFailure = wrapped;
                shouldLog = true;
            }
        }
        if (shouldLog)
        {
            Log.Error(message, exception);
            _started.TrySetException(wrapped);
        }
    }

    private void ObserveFirstFrame(TimeSpan systemRelativeTime) =>
        Interlocked.CompareExchange(ref _firstFrameTicks, systemRelativeTime.Ticks, -1);

    private Exception? GetCallbackFailure()
    {
        lock (_gate)
        {
            return _callbackFailure;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RequestStop();
        Task? encodingTask;
        lock (_gate) encodingTask = _encodingTask;
        var completed = true;
        try { completed = encodingTask?.Wait(TimeSpan.FromSeconds(5)) ?? true; }
        catch { }
        if (completed)
        {
            _stopCancellation.Dispose();
        }
        else if (encodingTask is not null)
        {
            _ = encodingTask.ContinueWith(
                _ => _stopCancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

internal sealed class CapturedSurface : IDisposable
{
    private readonly IDisposable? _owner;

    internal CapturedSurface(
        IDirect3DSurface surface,
        TimeSpan systemRelativeTime,
        IDisposable? owner = null)
    {
        Surface = surface;
        SystemRelativeTime = systemRelativeTime;
        _owner = owner;
    }

    internal IDirect3DSurface Surface { get; }
    internal TimeSpan SystemRelativeTime { get; }
    public void Dispose()
    {
        Surface.Dispose();
        _owner?.Dispose();
    }
}

internal sealed class CaptureFrameSource : IVideoFrameSource
{
    private readonly object _gate = new();
    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Multithread _multithread;
    private readonly ID3D11Texture2D _blankTexture;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ManualResetEvent _frameEvent = new(false);
    private readonly ManualResetEvent _closedEvent = new(false);
    private Direct3D11CaptureFrame? _currentFrame;
    private Exception? _failure;
    private bool _disposed;

    public SizeInt32 SourceSize { get; }

    internal CaptureFrameSource(
        ID3D11Device d3dDevice,
        IDirect3DDevice winRtDevice,
        GraphicsCaptureItem item,
        SizeInt32 size,
        bool suppressSystemBorder)
    {
        _d3dDevice = d3dDevice;
        _context = d3dDevice.ImmediateContext;
        _multithread = d3dDevice.QueryInterface<ID3D11Multithread>();
        _multithread.SetMultithreadProtected(true);
        _item = item;
        SourceSize = size;
        _blankTexture = d3dDevice.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)size.Width,
            (uint)size.Height,
            1,
            1,
            BindFlags.ShaderResource | BindFlags.RenderTarget));
        using (var renderTarget = d3dDevice.CreateRenderTargetView(_blankTexture))
        {
            _context.ClearRenderTargetView(renderTarget, new Color4(0f, 0f, 0f, 1f));
        }

        _item.Closed += CaptureItemClosed;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);
        _framePool.FrameArrived += FrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        if (suppressSystemBorder &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) &&
            ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession",
                nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            _session.IsBorderRequired = false;
        }
        _session.StartCapture();
    }

    public CapturedSurface? WaitForNewFrame()
    {
        while (!_disposed)
        {
            var signaled = WaitHandle.WaitAny([_closedEvent, _frameEvent]);
            if (signaled == 0)
            {
                Exception? failure;
                lock (_gate)
                {
                    failure = _failure;
                }
                if (failure is not null)
                {
                    throw new InvalidOperationException(
                        "Windows stopped delivering frames from the selected game window.",
                        failure);
                }
                return null;
            }

            Direct3D11CaptureFrame? frame;
            lock (_gate)
            {
                frame = _currentFrame;
                _currentFrame = null;
                _frameEvent.Reset();
            }
            if (frame is null) continue;

            using (frame)
            using (var sourceTexture = Direct3DInterop.GetTexture(frame.Surface))
            {
                var description = sourceTexture.Description;
                description.Usage = ResourceUsage.Default;
                description.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
                description.CPUAccessFlags = CpuAccessFlags.None;
                description.MiscFlags = ResourceOptionFlags.None;
                using var copyTexture = _d3dDevice.CreateTexture2D(description);
                _multithread.Enter();
                try
                {
                    _context.CopyResource(copyTexture, _blankTexture);
                    var width = (uint)Math.Clamp(
                        frame.ContentSize.Width,
                        0,
                        (int)description.Width);
                    var height = (uint)Math.Clamp(
                        frame.ContentSize.Height,
                        0,
                        (int)description.Height);
                    _context.CopySubresourceRegion(
                        copyTexture,
                        0,
                        0,
                        0,
                        0,
                        sourceTexture,
                        0,
                        new Box(0, 0, 0, (int)width, (int)height, 1));
                    using var dxgiSurface = copyTexture.QueryInterface<IDXGISurface>();
                    var surface = Direct3DInterop.CreateWinRtSurface(dxgiSurface);
                    return new CapturedSurface(surface, frame.SystemRelativeTime);
                }
                finally
                {
                    _multithread.Leave();
                }
            }
        }
        return null;
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null) return;
            lock (_gate)
            {
                if (_disposed)
                {
                    frame.Dispose();
                    return;
                }
                _currentFrame?.Dispose();
                _currentFrame = frame;
                frame = null;
                _frameEvent.Set();
            }
        }
        catch (Exception exception)
        {
            frame?.Dispose();
            lock (_gate)
            {
                if (_disposed) return;
                _failure ??= exception;
            }
            Stop();
        }
    }

    private void CaptureItemClosed(GraphicsCaptureItem sender, object args) => Stop();

    public void Stop()
    {
        try
        {
            _closedEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // A late capture callback may race the final frame-pool teardown.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _item.Closed -= CaptureItemClosed;
        _framePool.FrameArrived -= FrameArrived;
        _session.Dispose();
        _framePool.Dispose();
        lock (_gate)
        {
            _currentFrame?.Dispose();
            _currentFrame = null;
        }
        _blankTexture.Dispose();
        _multithread.Dispose();
        _context.Dispose();
        _frameEvent.Dispose();
        _closedEvent.Dispose();
    }
}

internal static class BorderlessCaptureAccess
{
    private static readonly object Gate = new();
    private static Task<bool>? _request;

    internal static Task<bool> RequestAsync()
    {
        lock (Gate)
        {
            return _request ??= RequestCoreAsync();
        }
    }

    private static async Task<bool> RequestCoreAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ||
            !ApiInformation.IsTypePresent(
                "Windows.Graphics.Capture.GraphicsCaptureAccess") ||
            !ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession",
                nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            Log.Info("Borderless capture is unavailable on this Windows version; the system capture indicator will remain visible.");
            return false;
        }

        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(
                GraphicsCaptureAccessKind.Borderless);
            if (status == AppCapabilityAccessStatus.Allowed)
            {
                Log.Info("Windows granted borderless capture access.");
                return true;
            }

            Log.Info($"Windows did not grant borderless capture access ({status}); the system capture indicator will remain visible.");
            return false;
        }
        catch (Exception)
        {
            // Unpackaged development builds cannot declare the required package capability.
            // Recording remains functional and Windows keeps its normal capture indicator.
            Log.Info("Borderless capture permission was unavailable; the system capture indicator will remain visible.");
            return false;
        }
    }
}

internal static class Direct3DInterop
{
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    internal static IDirect3DDevice CreateWinRtDevice(IDXGIDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(
            device.NativePointer,
            out var pointer));
        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal static IDirect3DSurface CreateWinRtSurface(IDXGISurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(
            surface.NativePointer,
            out var pointer));
        try
        {
            return WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var unknown = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unknown);
            var iid = Texture2DIid;
            var pointer = access.GetInterface(ref iid);
            return new ID3D11Texture2D(pointer);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(
        IntPtr dxgiSurface,
        out IntPtr graphicsSurface);
}
