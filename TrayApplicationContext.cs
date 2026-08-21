using System.Diagnostics;

namespace ClipsToDiscord;

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
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _uploadToDiscordItem;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly IUpdateDownloadService _updateDownloadService;
    private readonly ActivityHistoryStore _activityHistory;
    private readonly GlobalHotkeyManager _globalHotkey;
    private readonly ModeFeedbackOverlay _modeFeedbackOverlay;
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
        _settings = SettingsStore.Load();
        _activityHistory = new ActivityHistoryStore();
        _globalHotkey = new GlobalHotkeyManager();
        _globalHotkey.Pressed += ModeToggleHotkeyPressed;
        _modeFeedbackOverlay = new ModeFeedbackOverlay();
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
                    UploadPreparedEditedClipExclusiveAsync));
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
            var service = new EditedClipUploadService();
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
        _controller = new DiscordAwareController(settings, SetStatus, _activityHistory);
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

    private void StartUpdateChecks()
    {
        if (AppDistribution.UsesStoreUpdates) return;
        _updateTimer.Start();
        if (_automaticUpdateCheckScheduled) return;

        _automaticUpdateCheckScheduled = true;
        Application.Idle += CheckForUpdatesOnIdle;
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
        if (AppDistribution.UsesStoreUpdates)
        {
            OpenStoreUpdates(GetUsableOwner(owner));
            return;
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
            _statusItem.Text = status;
            _trayIcon.Text = status.Length <= 63 ? status : status[..63];
        }, null);
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
        _globalHotkey.Dispose();
        _modeFeedbackOverlay.Dispose();
        _controller?.Dispose();
        _updateCoordinator.Dispose();
        _updateDownloadService.Dispose();
        _activityHistory.Dispose();
        _lifetimeCancellation.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
        base.ExitThreadCore();
    }
}
