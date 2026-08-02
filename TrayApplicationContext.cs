using System.Diagnostics;

namespace ClipsToDiscord;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private AppSettings _settings;
    private DiscordAwareController? _controller;
    private bool _settingsOpen;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load();

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
            Icon = SystemIcons.Application,
            Text = "Clips to Discord",
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
            using var form = new SettingsForm(_settings);
            if (form.ShowDialog() == DialogResult.OK && form.SavedSettings is not null)
            {
                SettingsStore.Save(form.SavedSettings);
                _settings = form.SavedSettings;
                ApplySettings(_settings);
                _trayIcon.ShowBalloonTip(
                    2500,
                    "Clips to Discord",
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
    }

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

    protected override void ExitThreadCore()
    {
        _controller?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
