using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClipsToDiscord;

internal sealed class SingleFlightGate
{
    private int _active;

    internal bool TryEnter() => Interlocked.Exchange(ref _active, 1) == 0;
    internal void Exit() => Volatile.Write(ref _active, 0);
}

internal sealed class ReplayAutomaticStartCoordinator
{
    private readonly IReplayCaptureController _controller;
    private readonly Func<CaptureSettings> _settingsProvider;
    private readonly Func<bool> _hotkeyAvailableProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<Exception> _reportFailure;
    private readonly CancellationToken _lifetimeCancellation;
    private readonly SingleFlightGate _gate = new();

    internal ReplayAutomaticStartCoordinator(
        IReplayCaptureController controller,
        Func<CaptureSettings> settingsProvider,
        Func<bool> hotkeyAvailableProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<Exception> reportFailure,
        CancellationToken lifetimeCancellation)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _hotkeyAvailableProvider = hotkeyAvailableProvider ??
            throw new ArgumentNullException(nameof(hotkeyAvailableProvider));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _lifetimeCancellation = lifetimeCancellation;
    }

    internal async Task RequestAsync()
    {
        if (!_gate.TryEnter()) return;
        try
        {
            await ReplayAutomaticStartRunner.RunAsync(
                _controller,
                _settingsProvider,
                _hotkeyAvailableProvider,
                _delayAsync,
                _lifetimeCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _reportFailure(exception);
        }
        finally
        {
            _gate.Exit();
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                var settings = CaptureSettings.Normalize(_settingsProvider());
                if (ReplayCapturePolicy.ShouldAttemptAutomaticStart(
                        settings.InstantReplayEnabled,
                        _hotkeyAvailableProvider(),
                        _controller.ReplayStatus))
                {
                    _ = RequestAsync();
                }
            }
        }
    }
}

internal static class CaptureHotkeyRegistration
{
    internal static bool TryApply(
        GlobalHotkeyManager manager,
        CaptureSettings settings,
        out bool hotkeyAvailable,
        out int errorCode)
    {
        ArgumentNullException.ThrowIfNull(manager);
        settings = CaptureSettings.Normalize(settings);
        GlobalHotkeyBinding? binding = null;
        if (settings.InstantReplayEnabled)
        {
            if (!GlobalHotkeyBinding.TryParse(settings.SaveHotkey, out var parsed))
            {
                errorCode = 0;
                hotkeyAvailable = manager.GetBinding(GlobalHotkeyManager.CaptureHotkeyIdentifier) is not null;
                return false;
            }
            binding = parsed;
        }

        var applied = manager.TrySetBinding(
            GlobalHotkeyManager.CaptureHotkeyIdentifier,
            binding,
            out errorCode);
        hotkeyAvailable = applied ||
            manager.GetBinding(GlobalHotkeyManager.CaptureHotkeyIdentifier) is not null;
        return applied;
    }
}

internal static class ReactionCameraTrayIconFactory
{
    private const int IconSize = 32;

    internal static ReactionCameraRuntimeState SelectState(ReactionCameraRuntimeStatus status) =>
        status.IsActive
            ? ReactionCameraRuntimeState.Active
            : status.IsStarting
                ? ReactionCameraRuntimeState.Starting
                : status.ReleaseNeedsAttention
                    ? ReactionCameraRuntimeState.ReleaseNeedsAttention
                    : ReactionCameraRuntimeState.Off;

    internal static Icon Create(ReactionCameraRuntimeState state)
    {
        if (state is not (ReactionCameraRuntimeState.Starting or
            ReactionCameraRuntimeState.Active or
            ReactionCameraRuntimeState.ReleaseNeedsAttention))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var backgroundColor = state switch
            {
                ReactionCameraRuntimeState.Starting => Color.FromArgb(244, 177, 76),
                ReactionCameraRuntimeState.ReleaseNeedsAttention => Color.FromArgb(214, 55, 78),
                _ => ClipCordTheme.Coral
            };
            using var background = new SolidBrush(backgroundColor);
            using var outline = new Pen(Color.FromArgb(235, 255, 255, 255), 1.5f);
            var badgeBounds = new RectangleF(1.5f, 1.5f, IconSize - 3f, IconSize - 3f);
            graphics.FillEllipse(background, badgeBounds);
            graphics.DrawEllipse(outline, badgeBounds);
            FigmaIconRenderer.Draw(
                graphics,
                new Rectangle(7, 7, 18, 18),
                state == ReactionCameraRuntimeState.ReleaseNeedsAttention
                    ? FigmaIconAsset.Alert
                    : FigmaIconAsset.Camera,
                state == ReactionCameraRuntimeState.Starting
                    ? Color.FromArgb(10, 18, 32)
                    : Color.White);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}

internal enum ModeHotkeyBlockReason
{
    None,
    ShuttingDown,
    DialogOpen,
    ReconfigurationInProgress
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly Icon _applicationIcon;
    private readonly Icon _reactionCameraStartingIcon;
    private readonly Icon _reactionCameraActiveIcon;
    private readonly Icon _reactionCameraAttentionIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _disableReactionCameraItem;
    private readonly ToolStripMenuItem _uploadToDiscordItem;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly IUpdateDownloadService _updateDownloadService;
    private readonly ActivityHistoryStore _activityHistory;
    private readonly IFavoritesService _favorites;
    private readonly GlobalHotkeyManager _globalHotkey;
    private readonly ModeFeedbackOverlay _modeFeedbackOverlay;
    private readonly CaptureHostClient? _captureHostClient;
    private readonly IManualCaptureRecorder? _manualCaptureRecorder;
    private readonly ICaptureProjectCompletionSource? _captureProjectCompletionSource;
    private readonly SilhouetteProcessingCoordinator _silhouetteProcessingCoordinator;
    private readonly ClipProcessingOwnershipCoordinator _processingOwnership = new();
    // Anything created at or after this process boundary may still be completing in the
    // isolated capture host while startup reconciliation scans the shared library.
    private readonly DateTimeOffset _captureRecoveryCutoffUtc = DateTimeOffset.UtcNow;
    private volatile CaptureSettings _captureSettings;
    private string _baseTrayStatus = "Starting…";
    private ManualCaptureState _manualCaptureState = ManualCaptureState.NoTarget;
    private ReplayCaptureState _replayCaptureState = ReplayCaptureState.Off;
    private ReactionCameraRuntimeStatus _reactionCameraStatus = new(false, false);
    private volatile bool _captureHotkeyAvailable;
    private readonly ReplayAutomaticStartCoordinator? _replayStartCoordinator;
    private int _replaySaveInProgress;
    private int _reactionCameraDisableInProgress;
    private AppSettings _settings;
    private DiscordAwareController? _controller;
    private bool _settingsOpen;
    private bool _automaticUpdateCheckScheduled;
    private bool _updateDialogOpen;
    private bool _shutdownScheduled;
    private bool _reconfigurationInProgress;
    private bool _exitRequestedAfterReconfiguration;
    private CancellationTokenSource? _manualClipOperationCancellation;
    private SettingsForm? _settingsForm;

    internal UpdateLaunchRequest? PendingUpdateLaunch { get; private set; }

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _applicationIcon = LoadApplicationIcon();
        _reactionCameraStartingIcon = ReactionCameraTrayIconFactory.Create(
            ReactionCameraRuntimeState.Starting);
        _reactionCameraActiveIcon = ReactionCameraTrayIconFactory.Create(
            ReactionCameraRuntimeState.Active);
        _reactionCameraAttentionIcon = ReactionCameraTrayIconFactory.Create(
            ReactionCameraRuntimeState.ReleaseNeedsAttention);
        _settings = SettingsStore.Load();
        _captureSettings = CaptureSettingsStore.Load();
        _activityHistory = new ActivityHistoryStore();
        _favorites = new FavoritesService();
        _globalHotkey = new GlobalHotkeyManager();
        _globalHotkey.Pressed += ModeToggleHotkeyPressed;
        _globalHotkey.HotkeyPressed += GlobalHotkeyPressed;
        _modeFeedbackOverlay = new ModeFeedbackOverlay();
        var captureCapability = new WindowsCaptureCapabilityProbe().Inspect();
        _captureHostClient = captureCapability.Readiness == CaptureReadiness.Ready
            ? new CaptureHostClient()
            : null;
        _manualCaptureRecorder = captureCapability.Readiness == CaptureReadiness.Ready
            ? new CaptureHostManualRecorder(_captureHostClient!)
            : null;
        if (_manualCaptureRecorder is IReplayCaptureController automaticReplayController)
        {
            _replayStartCoordinator = new ReplayAutomaticStartCoordinator(
                automaticReplayController,
                () => _captureSettings,
                () => _captureHotkeyAvailable,
                static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                exception => Log.Error(
                    "ClipCord could not restore the configured Instant Replay buffer.",
                    exception),
                _lifetimeCancellation.Token);
        }
        var recoveredCameraProjects = CaptureStagingRecovery.RemoveOrphanedReactionCameraProjects(
            _captureSettings.LibraryRoot);
        if (recoveredCameraProjects > 0)
        {
            Log.Info($"Removed {recoveredCameraProjects} abandoned temporary camera project(s).");
        }
        var removedOrphanedCameraProjects = CaptureProjectStore.RemoveOrphanedProjects(
            _captureSettings.LibraryRoot);
        if (removedOrphanedCameraProjects > 0)
        {
            Log.Info($"Removed {removedOrphanedCameraProjects} camera project(s) whose gameplay clip was gone.");
        }
        _silhouetteProcessingCoordinator = new SilhouetteProcessingCoordinator(
            _captureSettings.LibraryRoot);
        _silhouetteProcessingCoordinator.ProjectSettled += SilhouetteProjectSettled;
        _ = RecoverCaptureJournalsAsync(_captureSettings.LibraryRoot);
        _captureProjectCompletionSource =
            _manualCaptureRecorder as ICaptureProjectCompletionSource;
        if (_captureProjectCompletionSource is not null)
        {
            _captureProjectCompletionSource.ProjectCommitted += CaptureProjectCommitted;
        }
        var assemblyVersion = typeof(TrayApplicationContext).Assembly.GetName().Version ?? new Version(0, 0, 0);
        _updateCoordinator = new UpdateCoordinator(
            GitHubUpdateChecker.Create(),
            new UpdatePreferencesStore(),
            StableVersion.FromAssemblyVersion(assemblyVersion));
        _updateDownloadService = UpdateDownloadService.Create();
        _updateTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds
        };
        _updateTimer.Tick += UpdateTimerTick;

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _disableReactionCameraItem = new ToolStripMenuItem("Turn Reaction Camera off")
        {
            Name = "TurnReactionCameraOffMenuItem",
            Visible = false,
            Enabled = false
        };
        _disableReactionCameraItem.Click += (_, _) => _ = DisableReactionCameraFromTrayAsync();
        var homeItem = new ToolStripMenuItem("Open ClipCord…", null, (_, _) => ShowSettings(initialPage: SettingsPage.Home));
        var configureItem = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        var activityItem = new ToolStripMenuItem("Activity…", null, (_, _) => ShowSettings(initialPage: SettingsPage.Activity));
        var openFolderItem = new ToolStripMenuItem("Open clips folder", null, (_, _) => OpenClipsFolder());
        _uploadToDiscordItem = new ToolStripMenuItem("Upload new clips to Discord")
        {
            CheckOnClick = true,
            Checked = _settings.UploadToDiscord,
            Enabled = _settings.IsValid
        };
        _uploadToDiscordItem.Click += (_, _) => ToggleUploadModeFromTray();
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => RequestExit());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_disableReactionCameraItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(homeItem);
        menu.Items.Add(configureItem);
        menu.Items.Add(activityItem);
        menu.Items.Add(openFolderItem);
        menu.Items.Add(_uploadToDiscordItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "ClipCord",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings(initialPage: SettingsPage.Home);
        if (_manualCaptureRecorder is not null)
        {
            _manualCaptureState = _manualCaptureRecorder.State;
            _manualCaptureRecorder.StateChanged += ManualCaptureRecorderStateChanged;
            if (_manualCaptureRecorder is IReplayCaptureController replayController)
            {
                _replayCaptureState = replayController.ReplayStatus.State;
                replayController.ReplayStateChanged += ReplayCaptureStateChanged;
            }
            if (_manualCaptureRecorder is IReactionCameraController reactionCameraController)
            {
                _reactionCameraStatus = reactionCameraController.ReactionCameraStatus;
                reactionCameraController.ReactionCameraStateChanged += ReactionCameraStateChanged;
            }
        }

        if (_settings.IsValid)
        {
            if (!TryApplyModeToggleHotkey(_settings, out var hotkeyError))
            {
                Log.Error($"Could not register the global mode shortcut. Windows error {hotkeyError}.");
                _uiContext.Post(_ => ShowHotkeyNotification(
                    "Shortcut unavailable",
                    $"{AppSettings.NormalizeModeToggleHotkey(_settings.ModeToggleHotkey)} is already in use. Choose another shortcut in Settings.",
                    ToolTipIcon.Warning), null);
            }
            StartController(_settings);
            _ = ApplyInitialStartupPreferenceAsync(_settings.StartWithWindows);
        }
        else
        {
            SetStatus("Setup required");
            Application.Idle += ShowFirstRunSettings;
        }
        _ = TryApplyCaptureHotkey(
            _captureSettings,
            out var initialCaptureHotkeyAvailable,
            out var captureHotkeyError);
        _captureHotkeyAvailable = initialCaptureHotkeyAvailable;
        if (!_captureHotkeyAvailable)
        {
            Log.Error($"Could not register the Instant Replay shortcut. Windows error {captureHotkeyError}.");
            _uiContext.Post(_ => ShowHotkeyNotification(
                "Replay shortcut unavailable",
                $"{_captureSettings.SaveHotkey} is already in use. Choose another shortcut in Capture.",
                ToolTipIcon.Warning), null);
        }
        if (_manualCaptureRecorder is not null)
        {
            _ = WarmCaptureHostAsync();
        }
    }

    private void ShowFirstRunSettings(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= ShowFirstRunSettings;
        ShowSettings(exitIfCancelled: true);
    }

    private async void ShowSettings(
        bool exitIfCancelled = false,
        SettingsPage initialPage = SettingsPage.Settings)
    {
        if (_settingsOpen)
        {
            if (_settingsForm is { IsDisposed: false, Disposing: false } existingForm)
            {
                existingForm.ShowPage(initialPage);
                existingForm.Activate();
                if (existingForm.WindowState == FormWindowState.Minimized)
                {
                    existingForm.WindowState = FormWindowState.Normal;
                }
            }
            return;
        }
        _settingsOpen = true;
        try
        {
            using var form = new SettingsForm(
                _settings,
                (Icon)_applicationIcon.Clone(),
                CheckForUpdatesManuallyAsync,
                () => _statusItem.Text ?? "Starting…",
                _activityHistory,
                initialPage,
                new ManualClipEditCoordinator(
                    _settings,
                    UploadPreparedEditedClipExclusiveAsync),
                favorites: _favorites,
                captureSettings: _captureSettings,
                captureEngineAvailable: _manualCaptureRecorder is IReplayCaptureController,
                saveCaptureSettings: SaveAndApplyCaptureSettings,
                manualCaptureRecorder: _manualCaptureRecorder);
            form.GalleryRenditionRetryRequested += GalleryRenditionRetryRequested;
            _settingsForm = form;
            if (form.ShowDialog() == DialogResult.OK &&
                form.SavedSettings is not null &&
                !_shutdownScheduled)
            {
                await PersistAndApplySettingsAsync(form.SavedSettings);
                if (_shutdownScheduled) return;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "ClipCord",
                    _settings.UploadToDiscord
                        ? "Settings saved. New clips will upload to Discord."
                        : "Settings saved. Local-only mode will keep new clips on this PC.",
                    ToolTipIcon.Info);
            }
            else if (exitIfCancelled && !_settings.IsValid)
            {
                ExitThread();
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not save settings.", exception);
            MessageBox.Show(exception.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _settingsForm = null;
            _settingsOpen = false;
        }
    }

    private async Task PersistAndApplySettingsAsync(AppSettings updated)
    {
        if (_reconfigurationInProgress)
        {
            throw new InvalidOperationException("ClipCord is already applying another settings change.");
        }

        var previous = _settings;
        var persisted = false;
        _reconfigurationInProgress = true;
        _uploadToDiscordItem.Enabled = false;
        try
        {
            SettingsStore.Save(updated);
            persisted = true;
            await ApplySettingsAsync(updated);
            _settings = updated;
        }
        catch
        {
            if (persisted)
            {
                try
                {
                    SettingsStore.Save(previous);
                    await ApplySettingsAsync(previous);
                    _settings = previous;
                }
                catch (Exception recoveryException)
                {
                    Log.Error("Could not restore the previous settings after a reconfiguration failure.", recoveryException);
                }
            }
            throw;
        }
        finally
        {
            _reconfigurationInProgress = false;
            if (!_shutdownScheduled)
            {
                _uploadToDiscordItem.Checked = _settings.UploadToDiscord;
                _uploadToDiscordItem.Enabled = _settings.IsValid;
                UpdateModeToggleHotkeyDisplay(_settings);
            }
            ScheduleDeferredExitIfRequested();
        }
    }

    private async Task ApplySettingsAsync(AppSettings settings)
    {
        if (!TryApplyModeToggleHotkey(settings, out var hotkeyError))
        {
            Log.Error($"Could not register the requested global mode shortcut. Windows error {hotkeyError}.");
            throw new InvalidOperationException(
                $"Windows could not register {AppSettings.NormalizeModeToggleHotkey(settings.ModeToggleHotkey)}. " +
                "It may already be used by another application. Choose a different shortcut.");
        }

        var previousController = _controller;
        _controller = null;
        if (previousController is not null)
        {
            SetStatus("Applying settings — stopping current watcher");
            await previousController.StopAsync();
        }

        if (_shutdownScheduled) return;
        await StartupManager.ApplyAsync(settings.StartWithWindows);
        StartController(settings);
    }

    private static async Task ApplyInitialStartupPreferenceAsync(bool enabled)
    {
        try
        {
            await StartupManager.ApplyAsync(enabled);
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not apply the saved startup preference.", exception);
        }
    }

    private async Task<ManualClipEditResult> UploadPreparedEditedClipExclusiveAsync(
        PreparedClipEdit prepared,
        IProgress<ManualClipEditProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_shutdownScheduled)
        {
            throw new OperationCanceledException("ClipCord is shutting down.", cancellationToken);
        }
        if (_reconfigurationInProgress)
        {
            throw new InvalidOperationException("ClipCord is already applying another change.");
        }
        if (!WebhookValidation.IsDiscordWebhook(_settings.WebhookUrl))
        {
            throw new InvalidOperationException(
                "Add a valid Discord webhook in Settings before uploading a Local-only clip.");
        }

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        _manualClipOperationCancellation = operationCancellation;
        _reconfigurationInProgress = true;
        _uploadToDiscordItem.Enabled = false;
        var previousController = _controller;
        _controller = null;
        try
        {
            if (previousController is not null)
            {
                SetStatus("Preparing manual upload — pausing clip watcher");
                await previousController.StopAsync();
            }
            operationCancellation.Token.ThrowIfCancellationRequested();
            SetStatus("Uploading edited Local-only clip");
            var service = new EditedClipUploadService(
                dispositionProcessor: new EditedClipDispositionProcessor(favorites: _favorites));
            return await service.UploadAsync(
                _settings,
                prepared,
                _activityHistory,
                progress,
                operationCancellation.Token);
        }
        finally
        {
            try
            {
                if (!_shutdownScheduled && !_exitRequestedAfterReconfiguration)
                {
                    try
                    {
                        StartController(_settings);
                    }
                    catch (Exception exception)
                    {
                        // The manual upload result is authoritative. A watcher restart failure
                        // must not turn a confirmed Discord upload into an apparent upload error.
                        Log.Error("The edited clip operation finished, but ClipCord could not restart its watcher.", exception);
                        SetStatus("Watcher restart failed — open Settings to retry");
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(_manualClipOperationCancellation, operationCancellation))
                {
                    _manualClipOperationCancellation = null;
                }
                operationCancellation.Dispose();
                _reconfigurationInProgress = false;
                if (!_shutdownScheduled)
                {
                    _uploadToDiscordItem.Checked = _settings.UploadToDiscord;
                    _uploadToDiscordItem.Enabled = _settings.IsValid;
                }
                ScheduleDeferredExitIfRequested();
            }
        }
    }

    private void RequestExit()
    {
        if (TryDeferExitAndCancelManualOperation(
                _reconfigurationInProgress,
                ref _exitRequestedAfterReconfiguration,
                _manualClipOperationCancellation))
        {
            SetStatus("Finishing the current clip operation before exit");
            return;
        }
        _shutdownScheduled = true;
        ExitThread();
    }

    internal static bool TryDeferExitAndCancelManualOperation(
        bool reconfigurationInProgress,
        ref bool exitRequested,
        CancellationTokenSource? manualOperationCancellation)
    {
        if (!reconfigurationInProgress) return false;
        exitRequested = true;
        try { manualOperationCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    private void ScheduleDeferredExitIfRequested()
    {
        if (!_exitRequestedAfterReconfiguration || _shutdownScheduled || _reconfigurationInProgress) return;
        _exitRequestedAfterReconfiguration = false;
        _uiContext.Post(_ => RequestExit(), null);
    }

    private void StartController(AppSettings settings)
    {
        if (settings.UploadToDiscord)
        {
            UploadedFolder.GetOrCreate(settings.ClipsFolder);
        }
        else
        {
            UploadedFolder.GetOrCreateLocalOnly(settings.ClipsFolder);
        }
        _uploadToDiscordItem.Checked = settings.UploadToDiscord;
        _uploadToDiscordItem.Enabled = settings.IsValid;
        if (!_processingOwnership.TryAcquire(
                ClipProcessingRuntimeOwner.Legacy,
                out var legacyOwnership) || legacyOwnership is null)
        {
            throw new InvalidOperationException(
                "Another ClipCord pipeline still owns clip processing.");
        }
        try
        {
            _controller = new DiscordAwareController(
                settings,
                SetStatus,
                legacyOwnership,
                _activityHistory,
                _favorites);
        }
        catch
        {
            legacyOwnership.Dispose();
            throw;
        }
        StartUpdateChecks();
    }

    private async void ToggleUploadModeFromTray()
    {
        await ChangeUploadModeAsync(_uploadToDiscordItem.Checked, invokedByHotkey: false);
    }

    private async void ModeToggleHotkeyPressed(object? sender, EventArgs eventArgs)
    {
        switch (GetModeHotkeyBlockReason(
                    _shutdownScheduled,
                    _settingsOpen,
                    _updateDialogOpen,
                    _reconfigurationInProgress))
        {
            case ModeHotkeyBlockReason.ShuttingDown:
                return;
            case ModeHotkeyBlockReason.DialogOpen:
                ShowModeFeedback(ModeFeedbackPresentation.DialogOpen);
                return;
            case ModeHotkeyBlockReason.ReconfigurationInProgress:
                ShowModeFeedback(ModeFeedbackPresentation.ReconfigurationInProgress);
                return;
        }

        await ChangeUploadModeAsync(!_settings.UploadToDiscord, invokedByHotkey: true);
    }

    internal static ModeHotkeyBlockReason GetModeHotkeyBlockReason(
        bool shutdownScheduled,
        bool settingsOpen,
        bool updateDialogOpen,
        bool reconfigurationInProgress)
    {
        if (shutdownScheduled) return ModeHotkeyBlockReason.ShuttingDown;
        if (settingsOpen || updateDialogOpen) return ModeHotkeyBlockReason.DialogOpen;
        return reconfigurationInProgress
            ? ModeHotkeyBlockReason.ReconfigurationInProgress
            : ModeHotkeyBlockReason.None;
    }

    private async Task ChangeUploadModeAsync(bool uploadToDiscord, bool invokedByHotkey)
    {
        var previousSettings = _settings;
        var updated = previousSettings with { UploadToDiscord = uploadToDiscord };
        if (!updated.IsValid)
        {
            _uploadToDiscordItem.Checked = previousSettings.UploadToDiscord;
            if (invokedByHotkey)
            {
                ShowModeFeedback(ModeFeedbackPresentation.DiscordSetupRequired);
                return;
            }
            MessageBox.Show(
                "Open Settings and enter a valid Discord webhook before enabling uploads.",
                "Discord setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            ShowSettings();
            return;
        }

        try
        {
            await PersistAndApplySettingsAsync(updated);
            if (_shutdownScheduled) return;
            if (invokedByHotkey)
            {
                ShowModeFeedback(ModeFeedbackPresentation.ForUploadMode(updated.UploadToDiscord));
            }
            else
            {
                ShowHotkeyNotification(
                    "ClipCord",
                    updated.UploadToDiscord
                        ? "Discord uploads enabled. New clips will be sent automatically."
                        : "Local-only mode enabled. New clips will not be sent to Discord.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            _uploadToDiscordItem.Checked = _settings.UploadToDiscord;
            Log.Error("Could not change the clip upload mode.", exception);
            if (invokedByHotkey)
            {
                ShowModeFeedback(ModeFeedbackPresentation.SaveFailed);
                return;
            }
            MessageBox.Show(
                "ClipCord could not save the upload-mode setting.",
                "Could not change upload mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool TryApplyModeToggleHotkey(AppSettings settings, out int errorCode)
    {
        var normalized = AppSettings.NormalizeModeToggleHotkey(settings.ModeToggleHotkey);
        GlobalHotkeyBinding? binding = null;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            if (!GlobalHotkeyBinding.TryParse(normalized, out var parsed))
            {
                errorCode = 0;
                return false;
            }
            binding = parsed;
        }

        var applied = _globalHotkey.TrySetBinding(binding, out errorCode);
        if (applied) UpdateModeToggleHotkeyDisplay(settings);
        return applied;
    }

    private void UpdateModeToggleHotkeyDisplay(AppSettings settings)
    {
        _uploadToDiscordItem.ShortcutKeyDisplayString =
            AppSettings.NormalizeModeToggleHotkey(settings.ModeToggleHotkey);
    }

    private void ShowHotkeyNotification(string title, string message, ToolTipIcon icon)
    {
        if (_shutdownScheduled || !_trayIcon.Visible) return;
        _trayIcon.ShowBalloonTip(2500, title, message, icon);
    }

    private void ShowModeFeedback(ModeFeedbackPresentation presentation)
    {
        if (_shutdownScheduled) return;
        _modeFeedbackOverlay.ShowFeedback(presentation);
    }

    internal static bool ConfigureAutomaticUpdateChecks(
        AppUpdateRoute route,
        bool alreadyScheduled,
        Action startTimer,
        Action scheduleOnIdle)
    {
        ArgumentNullException.ThrowIfNull(startTimer);
        ArgumentNullException.ThrowIfNull(scheduleOnIdle);
        if (route != AppUpdateRoute.GitHub) return false;

        startTimer();
        if (alreadyScheduled) return true;

        scheduleOnIdle();
        return true;
    }

    private void StartUpdateChecks()
    {
        _automaticUpdateCheckScheduled = ConfigureAutomaticUpdateChecks(
            AppDistribution.SelectUpdateRoute(AppDistribution.IsPackaged, manual: false),
            _automaticUpdateCheckScheduled,
            _updateTimer.Start,
            () => Application.Idle += CheckForUpdatesOnIdle);
    }

    private async void CheckForUpdatesOnIdle(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= CheckForUpdatesOnIdle;
        _automaticUpdateCheckScheduled = false;
        await CheckForUpdatesAutomaticallyAsync();
    }

    private async void UpdateTimerTick(object? sender, EventArgs eventArgs) =>
        await CheckForUpdatesAutomaticallyAsync();

    private async Task CheckForUpdatesAutomaticallyAsync()
    {
        var cancellationToken = _lifetimeCancellation.Token;
        try
        {
            var result = await _updateCoordinator.CheckAsync(
                manual: false,
                cancellationToken);
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is not null)
            {
                var owner = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Visible);
                PresentAvailableUpdate(result.Release, owner);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error("Automatic update check failed.", exception);
        }
    }

    private async Task CheckForUpdatesManuallyAsync(IWin32Window owner)
    {
        var route = AppDistribution.SelectUpdateRoute(AppDistribution.IsPackaged, manual: true);
        if (route == AppUpdateRoute.MicrosoftStore)
        {
            OpenStoreUpdates(GetUsableOwner(owner));
            return;
        }
        if (route != AppUpdateRoute.GitHub)
        {
            throw new InvalidOperationException("Manual update checks require a trusted update route.");
        }

        var result = await _updateCoordinator.CheckAsync(
            manual: true,
            _lifetimeCancellation.Token);
        var safeOwner = GetUsableOwner(owner);
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Release is not null:
                PresentAvailableUpdate(result.Release, safeOwner);
                break;
            case UpdateCheckStatus.UpToDate:
                MessageBox.Show(
                    safeOwner,
                    "You already have the latest stable release.",
                    "No update available",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            case UpdateCheckStatus.Busy:
                MessageBox.Show(
                    safeOwner,
                    "An update check is already running.",
                    "Update check in progress",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            case UpdateCheckStatus.InvalidRelease:
                MessageBox.Show(
                    safeOwner,
                    "The latest release could not be verified safely. No download was opened.",
                    "Release verification failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;
            case UpdateCheckStatus.Failed:
                MessageBox.Show(
                    safeOwner,
                    "GitHub could not be reached. Clip watching and uploads are unaffected.",
                    "Update check unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;
        }
    }

    private static void OpenStoreUpdates(IWin32Window? owner)
    {
        try
        {
            Process.Start(AppDistribution.CreateStoreUpdatesStartInfo());
        }
        catch (Exception exception)
        {
            Log.Error("Could not open Microsoft Store updates.", exception);
            MessageBox.Show(
                owner,
                "Open Microsoft Store, select Library, then choose Get updates.",
                "Could not open Microsoft Store",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    internal static IWin32Window? GetUsableOwner(IWin32Window requestedOwner) =>
        requestedOwner is Control
        {
            IsDisposed: false,
            Disposing: false,
            IsHandleCreated: true,
            Visible: true
        } control
            ? control
            : null;

    private void PresentAvailableUpdate(UpdateRelease release, IWin32Window? owner)
    {
        if (_updateDialogOpen) return;
        _updateDialogOpen = true;
        try
        {
            using var dialog = new UpdateAvailableDialog(release, (Icon)_applicationIcon.Clone());
            dialog.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            if (owner is null) dialog.ShowDialog();
            else dialog.ShowDialog(owner);

            switch (dialog.SelectedAction)
            {
                case UpdateDialogAction.ViewChanges:
                    OpenReleasePage(release.ReleasePageUri, owner);
                    break;
                case UpdateDialogAction.InstallUpdate:
                    DownloadAndInstallUpdate(release, owner);
                    break;
                case UpdateDialogAction.SkipVersion:
                    if (!_updateCoordinator.Skip(release)) ShowPreferenceSaveError(owner);
                    break;
                case UpdateDialogAction.RemindLater:
                    if (!_updateCoordinator.RemindLater(release)) ShowPreferenceSaveError(owner);
                    break;
            }
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    private void DownloadAndInstallUpdate(UpdateRelease release, IWin32Window? owner)
    {
        using var dialog = new UpdateDownloadDialog(
            release,
            _updateDownloadService,
            (Icon)_applicationIcon.Clone(),
            _lifetimeCancellation.Token);
        dialog.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (result != DialogResult.OK || dialog.DownloadedUpdate is null) return;

        PendingUpdateLaunch = new UpdateLaunchRequest(
            release.Version,
            dialog.DownloadedUpdate.InstallerPath,
            release.InstallerSha256);
        // Use the same deferred-exit boundary as the tray Exit command. If a manual
        // clip operation is active, cancellable FFmpeg work stops while any started
        // webhook POST reaches an authoritative result and persists it before setup runs.
        _uiContext.Post(_ => RequestExit(), null);
    }

    private static void OpenReleasePage(Uri releasePageUri, IWin32Window? owner)
    {
        try
        {
            Process.Start(new ProcessStartInfo(releasePageUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Error("Could not open the verified release page.", exception);
            MessageBox.Show(
                owner,
                "Windows could not open the official GitHub release page.",
                "Could not open update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void ShowPreferenceSaveError(IWin32Window? owner) => MessageBox.Show(
        owner,
        "The update preference could not be saved. The application will continue normally.",
        "Could not save update preference",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);

    private void SetStatus(string status)
    {
        _uiContext.Post(_ =>
        {
            _baseTrayStatus = status;
            _statusItem.Text = status;
            UpdateTrayCaptureIndicator();
        }, null);
    }

    private async Task WarmCaptureHostAsync()
    {
        if (_captureHostClient is null) return;
        try
        {
            await _captureHostClient.EnsureReadyAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            await StartConfiguredReplayWhenGameAppearsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not warm its isolated capture worker.", exception);
        }
    }

    private async Task StartConfiguredReplayWhenGameAppearsAsync()
    {
        if (_replayStartCoordinator is not null)
        {
            await _replayStartCoordinator.RequestAsync().ConfigureAwait(false);
        }
    }

    private void SaveAndApplyCaptureSettings(CaptureSettings settings)
    {
        settings = CaptureSettings.Normalize(settings);
        var previous = _captureSettings;
        var failClosedCameraSettings = previous.IncludeReactionCamera &&
            !settings.IncludeReactionCamera
                ? previous with { IncludeReactionCamera = false }
                : null;
        if (failClosedCameraSettings is not null)
        {
            _captureSettings = failClosedCameraSettings;
        }
        if (!TryApplyCaptureHotkey(settings, out var appliedHotkeyAvailable, out var errorCode))
        {
            _captureHotkeyAvailable = appliedHotkeyAvailable;
            throw new InvalidOperationException(
                $"{settings.SaveHotkey} is already in use. Choose another Instant Replay shortcut.");
        }
        _captureHotkeyAvailable = appliedHotkeyAvailable;
        try
        {
            CaptureSettingsStore.Save(settings);
            _captureSettings = settings;
            _silhouetteProcessingCoordinator.UpdateLibraryRoot(settings.LibraryRoot);
            _ = RecoverCaptureJournalsAsync(settings.LibraryRoot);
            if (settings.InstantReplayEnabled)
            {
                _ = StartConfiguredReplayWhenGameAppearsAsync();
            }
        }
        catch
        {
            _ = TryApplyCaptureHotkey(previous, out var restoredHotkeyAvailable, out _);
            _captureHotkeyAvailable = restoredHotkeyAvailable;
            _captureSettings = failClosedCameraSettings ?? previous;
            _silhouetteProcessingCoordinator.UpdateLibraryRoot(_captureSettings.LibraryRoot);
            throw;
        }
    }

    private void CaptureProjectCommitted(
        object? sender,
        CaptureProjectCommittedEventArgs eventArgs)
    {
        _ = _silhouetteProcessingCoordinator.TryEnqueue(
            eventArgs.LibraryRoot,
            eventArgs.ProjectId);
    }

    private async void SilhouetteProjectSettled(
        object? sender,
        SilhouetteProjectSettledEventArgs eventArgs)
    {
        try
        {
            _ = await CaptureJournalCaptureCommit.ReconcileRenditionsAsync(
                    eventArgs.LibraryRoot,
                    eventArgs.ProjectId,
                    _lifetimeCancellation.Token,
                    processingSettled: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The silhouette rendition state remains durable and will be projected by startup
            // reconciliation. Do not turn an optional post-processing notification into a
            // capture or shutdown failure.
            Log.Error(
                $"ClipCord could not reconcile silhouette outputs for project {eventArgs.ProjectId}.",
                exception);
        }
    }

    private async Task RecoverCaptureJournalsAsync(string libraryRoot)
    {
        try
        {
            await CaptureJournalStartupRecovery.RecoverAsync(
                    libraryRoot,
                    _captureRecoveryCutoffUtc,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
            // Promotion recovery must run first: an old staged MP4 can still be owned by a
            // durable move-before-journal intent and must not be mistaken for an orphan.
            var removed = CaptureStagingRecovery.RemoveOrphanedManualCaptures(libraryRoot);
            if (removed > 0)
            {
                Log.Info($"Removed {removed} abandoned capture staging file(s).");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not finish capture-journal startup recovery.", exception);
        }
    }

    private async void GalleryRenditionRetryRequested(
        object? sender,
        GalleryRenditionRetryRequestedEventArgs eventArgs)
    {
        try
        {
            var scheduled = await _silhouetteProcessingCoordinator.RetryAsync(
                eventArgs.LibraryRoot,
                eventArgs.ProjectId,
                eventArgs.OrientationId,
                _lifetimeCancellation.Token);
            if (!scheduled && !_shutdownScheduled)
            {
                Log.Info(
                    $"The {GalleryCatalog.GetOrientationName(eventArgs.OrientationId)} silhouette retry " +
                    $"for project {eventArgs.ProjectId} was no longer eligible or was already active.");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error(
                $"ClipCord could not retry the {eventArgs.OrientationId} silhouette rendition.",
                exception);
            if (!_shutdownScheduled)
            {
                ShowHotkeyNotification(
                    "Rendition retry unavailable",
                    "ClipCord could not restart that local rendition. Refresh Gallery and try again.",
                    ToolTipIcon.Warning);
            }
        }
    }

    private bool TryApplyCaptureHotkey(
        CaptureSettings settings,
        out bool hotkeyAvailable,
        out int errorCode) =>
        CaptureHotkeyRegistration.TryApply(
            _globalHotkey,
            settings,
            out hotkeyAvailable,
            out errorCode);

    private void GlobalHotkeyPressed(object? sender, GlobalHotkeyPressedEventArgs eventArgs)
    {
        if (eventArgs.Identifier != GlobalHotkeyManager.CaptureHotkeyIdentifier) return;
        _ = SaveReplayFromHotkeyAsync();
    }

    private async Task SaveReplayFromHotkeyAsync()
    {
        if (_shutdownScheduled ||
            Interlocked.Exchange(ref _replaySaveInProgress, 1) != 0)
        {
            return;
        }
        try
        {
            var replayController = _manualCaptureRecorder as IReplayCaptureController;
            var status = replayController?.ReplayStatus;
            var availability = status is null
                ? ReplaySaveAvailability.Off
                : ReplayCapturePolicy.ClassifySaveAvailability(
                    _captureSettings.InstantReplayEnabled,
                    status);
            if (availability != ReplaySaveAvailability.Ready)
            {
                var presentation = availability switch
                {
                    ReplaySaveAvailability.WaitingForGame => (
                        "Waiting for a game",
                        "No clip was saved. ClipCord will begin buffering automatically after it detects a supported game.",
                        ToolTipIcon.Info),
                    ReplaySaveAvailability.NeedsAttention => (
                        "Instant Replay needs attention",
                        "No clip was saved. Open Capture to review the recorder status, then restart Instant Replay.",
                        ToolTipIcon.Warning),
                    _ => (
                        "Instant Replay is off",
                        "Enable Instant Replay in Capture before using the save shortcut.",
                        ToolTipIcon.Info)
                };
                ShowHotkeyNotification(
                    presentation.Item1,
                    presentation.Item2,
                    presentation.Item3);
                return;
            }
            var result = await replayController!.SaveReplayAsync(_lifetimeCancellation.Token);
            if (result is not null)
            {
                ShowModeFeedback(ModeFeedbackPresentation.ForCapturedClip(
                    result.GameName,
                    result.Duration));
                if (!string.IsNullOrWhiteSpace(result.ReactionCameraWarning))
                {
                    ShowHotkeyNotification(
                        "Gameplay clip saved",
                        "The gameplay clip was saved, but its optional Reaction Camera layer may be missing or incomplete.",
                        ToolTipIcon.Warning);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not save the Instant Replay hotkey snapshot.", exception);
            ShowModeFeedback(ModeFeedbackPresentation.CaptureFailed);
        }
        finally
        {
            Volatile.Write(ref _replaySaveInProgress, 0);
        }
    }

    private void ReplayCaptureStateChanged(object? sender, EventArgs eventArgs)
    {
        _uiContext.Post(_ =>
        {
            if (_shutdownScheduled || sender is not IReplayCaptureController replayController) return;
            var previous = _replayCaptureState;
            var replayStatus = replayController.ReplayStatus;
            _replayCaptureState = replayStatus.State;
            UpdateTrayCaptureIndicator();
            if (_replayCaptureState == ReplayCaptureState.Buffering &&
                previous == ReplayCaptureState.Starting)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureStarted);
            }
            else if (ReplayCapturePolicy.ShouldAttemptAutomaticStart(
                         _captureSettings.InstantReplayEnabled,
                         _captureHotkeyAvailable,
                         replayStatus))
            {
                _ = StartConfiguredReplayWhenGameAppearsAsync();
            }
            else if (_replayCaptureState == ReplayCaptureState.Failed)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureFailed);
            }
        }, null);
    }

    private void ManualCaptureRecorderStateChanged(object? sender, EventArgs eventArgs)
    {
        _uiContext.Post(_ =>
        {
            if (_shutdownScheduled || _manualCaptureRecorder is null) return;
            var previous = _manualCaptureState;
            _manualCaptureState = _manualCaptureRecorder.State;
            UpdateTrayCaptureIndicator();

            if (_manualCaptureState == ManualCaptureState.Recording &&
                previous != ManualCaptureState.Recording)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureStarted);
            }
            else if (_manualCaptureState == ManualCaptureState.Ready &&
                     previous == ManualCaptureState.Finalizing)
            {
                ShowModeFeedback(ModeFeedbackPresentation.ForCapturedClip(
                    _manualCaptureRecorder.Target?.DisplayName,
                    TimeSpan.Zero));
            }
            else if (_manualCaptureState == ManualCaptureState.Failed)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureFailed);
            }
        }, null);
    }

    private void ReactionCameraStateChanged(object? sender, EventArgs eventArgs)
    {
        _uiContext.Post(_ =>
        {
            if (_shutdownScheduled || sender is not IReactionCameraController reactionCameraController)
            {
                return;
            }
            _reactionCameraStatus = reactionCameraController.ReactionCameraStatus;
            UpdateTrayCaptureIndicator();
        }, null);
    }

    private async Task DisableReactionCameraFromTrayAsync()
    {
        if (_shutdownScheduled ||
            _manualCaptureRecorder is not IReactionCameraController reactionCameraController)
        {
            return;
        }
        var currentCameraStatus = reactionCameraController.ReactionCameraStatus;
        if (currentCameraStatus.ReleaseNeedsAttention)
        {
            RequestExit();
            return;
        }
        if (!(currentCameraStatus.IsActive || currentCameraStatus.IsStarting) ||
            Interlocked.Exchange(ref _reactionCameraDisableInProgress, 1) != 0)
        {
            return;
        }

        Exception? preferenceFailure = null;
        Exception? runtimeFailure = null;
        try
        {
            var disabledSettings = CaptureSettings.Normalize(_captureSettings with
            {
                IncludeReactionCamera = false
            });
            // Fail closed in memory even if the settings file cannot be updated. A later
            // automatically detected game in this process must not reopen a camera the user
            // explicitly turned off.
            _captureSettings = disabledSettings;
            _settingsForm?.ApplyExternalCaptureSettings(disabledSettings);
            try
            {
                CaptureSettingsStore.Save(disabledSettings);
            }
            catch (Exception exception)
            {
                preferenceFailure = exception;
                Log.Error(
                    "ClipCord could not save the Reaction Camera off preference from the tray.",
                    exception);
            }

            try
            {
                await reactionCameraController.DisableReactionCameraAsync(
                    _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                runtimeFailure = exception;
                Log.Error("ClipCord could not turn Reaction Camera off from the tray.", exception);
            }

            if (_shutdownScheduled) return;
            _reactionCameraStatus = reactionCameraController.ReactionCameraStatus;
            UpdateTrayCaptureIndicator();
            if (preferenceFailure is not null ||
                runtimeFailure is not null ||
                _reactionCameraStatus.ReleaseNeedsAttention)
            {
                ShowHotkeyNotification(
                    "Reaction Camera needs attention",
                    _reactionCameraStatus.ReleaseNeedsAttention
                        ? "The camera stopped accepting new frames, but Windows has not confirmed release. Exit ClipCord from the tray to guarantee the device closes; gameplay capture continues."
                        : _reactionCameraStatus.IsActive
                        ? "Reaction Camera may still be active. Gameplay capture continues; open Capture to review its status."
                        : "Reaction Camera is not currently active, but ClipCord could not save every part of the off request. Review Capture before the next game.",
                    ToolTipIcon.Warning);
            }
        }
        finally
        {
            Volatile.Write(ref _reactionCameraDisableInProgress, 0);
        }
    }

    private void UpdateTrayCaptureIndicator()
    {
        var captureText = _replayCaptureState switch
        {
            ReplayCaptureState.Starting => "ClipCord — Starting Instant Replay",
            ReplayCaptureState.Buffering => "ClipCord — Instant Replay active",
            ReplayCaptureState.Saving => "ClipCord — Saving replay",
            ReplayCaptureState.Stopping => "ClipCord — Stopping replay",
            ReplayCaptureState.Off when _captureSettings.InstantReplayEnabled =>
                "ClipCord — Waiting for a game",
            ReplayCaptureState.Failed when _captureSettings.InstantReplayEnabled =>
                "ClipCord — Waiting for a game",
            _ => _manualCaptureState switch
            {
            ManualCaptureState.Starting => "ClipCord — Starting capture",
            ManualCaptureState.Recording => "ClipCord — Recording game",
            ManualCaptureState.Finalizing => "ClipCord — Saving recording",
                _ => _baseTrayStatus
            }
        };
        var cameraIndicatorState = ReactionCameraTrayIconFactory.SelectState(_reactionCameraStatus);
        var cameraActive = cameraIndicatorState == ReactionCameraRuntimeState.Active;
        var cameraStarting = cameraIndicatorState == ReactionCameraRuntimeState.Starting;
        var cameraNeedsAttention =
            cameraIndicatorState == ReactionCameraRuntimeState.ReleaseNeedsAttention;
        var cameraInUse = cameraActive || cameraStarting || cameraNeedsAttention;
        var desiredIcon = cameraActive
            ? _reactionCameraActiveIcon
            : cameraStarting
                ? _reactionCameraStartingIcon
                : cameraNeedsAttention
                    ? _reactionCameraAttentionIcon
                    : _applicationIcon;
        if (!ReferenceEquals(_trayIcon.Icon, desiredIcon)) _trayIcon.Icon = desiredIcon;
        _disableReactionCameraItem.Visible = cameraInUse;
        _disableReactionCameraItem.Enabled = cameraInUse;
        _disableReactionCameraItem.Text = cameraNeedsAttention
            ? "Exit ClipCord to release Reaction Camera"
            : "Turn Reaction Camera off";
        _statusItem.Text = cameraNeedsAttention
            ? "Reaction Camera release needs attention"
            : cameraActive
            ? $"{_baseTrayStatus} · Reaction Camera active"
            : cameraStarting
                ? $"{_baseTrayStatus} · Reaction Camera starting"
                : _baseTrayStatus;

        var text = captureText;
        if (cameraNeedsAttention)
        {
            text = "ClipCord — Camera stopping; exit to force release";
        }
        else if (cameraActive)
        {
            const string clipCordPrefix = "ClipCord — ";
            const string cameraActivePrefix = "ClipCord — Reaction Camera active";
            const string separator = " · ";
            var context = captureText.StartsWith(clipCordPrefix, StringComparison.Ordinal)
                ? captureText[clipCordPrefix.Length..]
                : captureText;
            var maximumContextLength = 63 - cameraActivePrefix.Length - separator.Length;
            if (context.Length > maximumContextLength)
            {
                context = context[..maximumContextLength];
            }
            text = $"{cameraActivePrefix}{separator}{context}";
        }
        else if (cameraStarting)
        {
            const string clipCordPrefix = "ClipCord — ";
            const string cameraStartingPrefix = "ClipCord — Reaction Camera starting";
            const string separator = " · ";
            var context = captureText.StartsWith(clipCordPrefix, StringComparison.Ordinal)
                ? captureText[clipCordPrefix.Length..]
                : captureText;
            var maximumContextLength = 63 - cameraStartingPrefix.Length - separator.Length;
            if (context.Length > maximumContextLength)
            {
                context = context[..maximumContextLength];
            }
            text = $"{cameraStartingPrefix}{separator}{context}";
        }
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void OpenClipsFolder()
    {
        if (!_settings.IsValid) return;
        Process.Start(new ProcessStartInfo("explorer.exe", _settings.ClipsFolder) { UseShellExecute = true });
    }

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            using var extracted = Icon.ExtractAssociatedIcon(executablePath);
            if (extracted is not null) return (Icon)extracted.Clone();
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    protected override void ExitThreadCore()
    {
        _shutdownScheduled = true;
        Application.Idle -= CheckForUpdatesOnIdle;
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _lifetimeCancellation.Cancel();
        _globalHotkey.Pressed -= ModeToggleHotkeyPressed;
        _globalHotkey.HotkeyPressed -= GlobalHotkeyPressed;
        _globalHotkey.Dispose();
        _modeFeedbackOverlay.Dispose();
        if (_captureProjectCompletionSource is not null)
        {
            _captureProjectCompletionSource.ProjectCommitted -= CaptureProjectCommitted;
        }
        _silhouetteProcessingCoordinator.ProjectSettled -= SilhouetteProjectSettled;
        _silhouetteProcessingCoordinator.Dispose();
        if (_manualCaptureRecorder is not null)
        {
            _manualCaptureRecorder.StateChanged -= ManualCaptureRecorderStateChanged;
            if (_manualCaptureRecorder is IReplayCaptureController replayController)
            {
                replayController.ReplayStateChanged -= ReplayCaptureStateChanged;
            }
            if (_manualCaptureRecorder is IReactionCameraController reactionCameraController)
            {
                reactionCameraController.ReactionCameraStateChanged -= ReactionCameraStateChanged;
            }
        }
        _manualCaptureRecorder?.Dispose();
        _captureHostClient?.Dispose();
        _controller?.Dispose();
        _updateCoordinator.Dispose();
        _updateDownloadService.Dispose();
        _activityHistory.Dispose();
        _lifetimeCancellation.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _reactionCameraAttentionIcon.Dispose();
        _reactionCameraActiveIcon.Dispose();
        _reactionCameraStartingIcon.Dispose();
        _applicationIcon.Dispose();
        base.ExitThreadCore();
    }
}
