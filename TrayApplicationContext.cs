using System.Diagnostics;

namespace ClipsToDiscord;

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
    private AppSettings _settings;
    private DiscordAwareController? _controller;
    private bool _settingsOpen;
    private bool _automaticUpdateCheckScheduled;
    private bool _updateDialogOpen;
    private bool _shutdownScheduled;
    private bool _reconfigurationInProgress;

    internal UpdateLaunchRequest? PendingUpdateLaunch { get; private set; }

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _applicationIcon = LoadApplicationIcon();
        _settings = SettingsStore.Load();
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
        var configureItem = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
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
        menu.Items.Add(configureItem);
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
        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        if (_settings.IsValid)
        {
            StartController(_settings);
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

    private async void ShowSettings(bool exitIfCancelled = false)
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            using var form = new SettingsForm(
                _settings,
                (Icon)_applicationIcon.Clone(),
                CheckForUpdatesManuallyAsync,
                () => _statusItem.Text ?? "Starting…");
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
            }
        }
    }

    private async Task ApplySettingsAsync(AppSettings settings)
    {
        var previousController = _controller;
        _controller = null;
        if (previousController is not null)
        {
            SetStatus("Applying settings — stopping current watcher");
            await previousController.StopAsync();
        }

        if (_shutdownScheduled) return;
        StartController(settings);
    }

    private void RequestExit()
    {
        _shutdownScheduled = true;
        ExitThread();
    }

    private void StartController(AppSettings settings)
    {
        StartupManager.Apply(settings.StartWithWindows);
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
        _controller = new DiscordAwareController(settings, SetStatus);
        StartUpdateChecks();
    }

    private async void ToggleUploadModeFromTray()
    {
        var previousSettings = _settings;
        var updated = previousSettings with { UploadToDiscord = _uploadToDiscordItem.Checked };
        if (!updated.IsValid)
        {
            _uploadToDiscordItem.Checked = previousSettings.UploadToDiscord;
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
            _trayIcon.ShowBalloonTip(
                2500,
                "ClipCord",
                updated.UploadToDiscord
                    ? "Discord uploads enabled. New clips will be sent automatically."
                    : "Local-only mode enabled. New clips will not be sent to Discord.",
                ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _uploadToDiscordItem.Checked = _settings.UploadToDiscord;
            Log.Error("Could not change the clip upload mode.", exception);
            MessageBox.Show(
                "ClipCord could not save the upload-mode setting.",
                "Could not change upload mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void StartUpdateChecks()
    {
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
        _shutdownScheduled = true;
        _uiContext.Post(_ => ExitThread(), null);
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
        _controller?.Dispose();
        _updateCoordinator.Dispose();
        _updateDownloadService.Dispose();
        _lifetimeCancellation.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
        base.ExitThreadCore();
    }
}
