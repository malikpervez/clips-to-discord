using System.Diagnostics;

namespace ClipsToDiscord;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly UpdateCoordinator _updateCoordinator;
    private AppSettings _settings;
    private DiscordAwareController? _controller;
    private bool _settingsOpen;
    private bool _automaticUpdateCheckScheduled;

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
        _updateTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds
        };
        _updateTimer.Tick += UpdateTimerTick;

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        var configureItem = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        var openFolderItem = new ToolStripMenuItem("Open clips folder", null, (_, _) => OpenClipsFolder());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(configureItem);
        menu.Items.Add(openFolderItem);
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
            ApplySettings(_settings);
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

    private void ShowSettings(bool exitIfCancelled = false)
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
            if (form.ShowDialog() == DialogResult.OK && form.SavedSettings is not null)
            {
                SettingsStore.Save(form.SavedSettings);
                _settings = form.SavedSettings;
                ApplySettings(_settings);
                _trayIcon.ShowBalloonTip(
                    2500,
                    "ClipCord",
                    "Settings saved. The clip watcher follows Discord automatically.",
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

    private void ApplySettings(AppSettings settings)
    {
        _controller?.Dispose();
        StartupManager.Apply(settings.StartWithWindows);
        UploadedFolder.GetOrCreate(settings.ClipsFolder);
        _controller = new DiscordAwareController(settings, SetStatus);
        StartUpdateChecks();
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
        using var dialog = new UpdateAvailableDialog(release, (Icon)_applicationIcon.Clone());
        dialog.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        if (owner is null) dialog.ShowDialog();
        else dialog.ShowDialog(owner);

        switch (dialog.SelectedAction)
        {
            case UpdateDialogAction.ViewChanges:
            case UpdateDialogAction.DownloadUpdate:
                OpenReleasePage(release.ReleasePageUri, owner);
                break;
            case UpdateDialogAction.SkipVersion:
                if (!_updateCoordinator.Skip(release)) ShowPreferenceSaveError(owner);
                break;
            case UpdateDialogAction.RemindLater:
                if (!_updateCoordinator.RemindLater(release)) ShowPreferenceSaveError(owner);
                break;
        }
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
        Application.Idle -= CheckForUpdatesOnIdle;
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _lifetimeCancellation.Cancel();
        _controller?.Dispose();
        _updateCoordinator.Dispose();
        _lifetimeCancellation.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
        base.ExitThreadCore();
    }
}
