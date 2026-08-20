using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

internal enum SettingsPage
{
    Home,
    Settings,
    Activity,
    Gallery,
    About
}

internal sealed class SettingsForm : Form
{
    internal static readonly Size DesignedClientSize = new(1200, 760);
    internal static readonly Size SettingsDesignedClientSize = DesignedClientSize;
    internal static readonly Size ActivityDesignedClientSize = DesignedClientSize;
    internal static readonly Size GalleryDesignedClientSize = DesignedClientSize;
    internal static readonly Size AboutDesignedClientSize = DesignedClientSize;
    internal static readonly Size HomeDesignedClientSize = DesignedClientSize;
    internal static readonly Size MinimumDesignedClientSize = new(960, 620);
    internal const int NavigationRailLogicalWidth = 216;
    internal const int PageHeaderLogicalHeight = 64;
    internal const int TitleBarButtonLogicalWidth = 46;
    internal const int SaveBarLogicalHeight = 66;
    // Kept as a compatibility name for older layout probes; the redesigned shell
    // uses this height only for the conditional Settings save bar.
    internal const int FooterLogicalHeight = SaveBarLogicalHeight;
    private static readonly Regex CompressionTargetPattern = new(
        @"^\s*(?<value>\d{1,3})\s*(?:MB)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    internal static IReadOnlyList<int> CompressionTargetPresets { get; } =
        Array.AsReadOnly([5, 10, 25, 50, 75, 95, 100]);

    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundPreference = 2;
    internal const int ResizeGrip = 12;

    private readonly Icon? _ownedApplicationIcon;
    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private readonly System.Windows.Forms.Timer _watcherStatusTimer;
    private readonly Func<string>? _watcherStatusProvider;
    private readonly TitleBarButton _minimizeButton = new()
    {
        Name = "MinimizeButton",
        Glyph = BrandGlyph.Minimize,
        AccessibleName = "Minimize"
    };
    private readonly TitleBarButton _maximizeButton = new()
    {
        Name = "MaximizeButton",
        Glyph = BrandGlyph.Maximize,
        AccessibleName = "Maximize"
    };
    private readonly TitleBarButton _closeButton = new()
    {
        Name = "CloseButton",
        Glyph = BrandGlyph.Close,
        AccessibleName = "Close"
    };
    private readonly Label _watcherStatusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.ShellText,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold)
    };
    private readonly Label _watcherStatusDetailLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.TextTertiary,
        TextAlign = ContentAlignment.TopLeft,
        Font = ClipCordTheme.InterfaceFont(8.25f)
    };
    private readonly Label _pageTitleLabel = new()
    {
        Name = "PageTitleLabel",
        Dock = DockStyle.Fill,
        AutoSize = false,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.DisplayFont(18f, FontStyle.Bold),
        TextAlign = ContentAlignment.BottomLeft,
        UseMnemonic = false,
        Margin = Padding.Empty
    };
    private readonly Label _pageSubtitleLabel = new()
    {
        Name = "PageSubtitleLabel",
        Dock = DockStyle.Fill,
        AutoSize = false,
        ForeColor = ClipCordTheme.TextTertiary,
        Font = ClipCordTheme.InterfaceFont(9f),
        TextAlign = ContentAlignment.TopLeft,
        UseMnemonic = false,
        Margin = Padding.Empty
    };
    private FlowLayoutPanel? _pageActionHost;
    private readonly TextBox _folderText = CreateTextBox("Clips folder");
    private readonly TextBox _webhookText = CreateTextBox("Discord webhook URL", usePasswordCharacter: true);
    private readonly TextBox _uploaderNameText = CreateTextBox("Uploader name");
    private readonly TextBox _compressionTarget = CreateTextBox("Compression target in megabytes");
    private readonly OutlineButton _compressionTargetPresetButton = new()
    {
        Name = "CompressionTargetPresetButton",
        Text = "▾",
        AccessibleName = "Choose a compression target preset",
        AccessibleRole = AccessibleRole.PushButton,
        AutoSize = false,
        Size = new Size(32, 30),
        Font = ClipCordTheme.InterfaceFont(11f),
        SurfaceColor = ClipCordTheme.SettingsField,
        HoverColor = ClipCordTheme.SettingsButtonHover,
        OutlineColor = Color.Transparent,
        ForeColor = ClipCordTheme.ShellText,
        Margin = Padding.Empty
    };
    private readonly ContextMenuStrip _compressionTargetMenu = new()
    {
        Name = "CompressionTargetPresetMenu",
        ShowImageMargin = false,
        ShowCheckMargin = false,
        AutoSize = true,
        MinimumSize = new Size(120, 0),
        Padding = new Padding(3)
    };
    private readonly TextBox _modeToggleHotkeyText = CreateTextBox("Global upload-mode shortcut");
    private readonly OutlineButton _modeToggleHotkeyAction = CreateSecondaryButton("Disable", 92);
    private readonly ToggleSwitch _startWithWindows = new()
    {
        Name = "StartWithWindowsToggle",
        Text = "Start with Windows",
        BackColor = ClipCordTheme.SettingsCard,
        ForeColor = ClipCordTheme.ShellText
    };
    private readonly ToggleSwitch _uploadToDiscord = new()
    {
        Name = "UploadToDiscordToggle",
        Text = "Upload new clips to Discord",
        AccessibleName = "Upload new clips to Discord",
        BackColor = ClipCordTheme.SettingsCard,
        ForeColor = ClipCordTheme.ShellText
    };
    private readonly Label _uploadModeHelper = CreateHelper(string.Empty);
    private readonly Label _privacySummaryLabel = new()
    {
        Name = "PrivacySummaryLabel",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        ForeColor = ClipCordTheme.ShellMutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = ClipCordTheme.InterfaceFont(9f)
    };
    private readonly Label _dirtySummaryLabel = new()
    {
        Name = "DirtySettingsSummaryLabel",
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.TextSecondary,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = ClipCordTheme.InterfaceFont(9f),
        UseMnemonic = false
    };
    private readonly OutlineButton _browseButton = CreateSecondaryButton("Browse", 112);
    private readonly OutlineButton _steelSeriesSourceButton = CreateSecondaryButton("SteelSeries GG", 148);
    private readonly OutlineButton _nvidiaSourceButton = CreateSecondaryButton("NVIDIA", 108);
    private readonly Label _captureSourceHelper = CreateHelper(string.Empty);
    private readonly OutlineButton _testButton = CreateSecondaryButton("Test webhook", 130);
    private readonly OutlineButton _checkUpdatesButton = CreateSecondaryButton("Check for updates", 166);
    private readonly GradientButton _saveButton = new()
    {
        Text = "Save changes",
        Size = new Size(175, 46),
        Margin = new Padding(12, 0, 0, 0)
    };
    private readonly OutlineButton _cancelButton = new()
    {
        Name = "DiscardSettingsButton",
        Text = "Discard",
        Size = new Size(118, 46),
        SurfaceColor = Color.FromArgb(25, 35, 52),
        HoverColor = Color.FromArgb(35, 46, 65),
        OutlineColor = Color.FromArgb(65, 76, 96),
        ForeColor = ClipCordTheme.ShellText,
        Margin = Padding.Empty
    };
    private readonly Label _statusLabel = new()
    {
        Name = "FooterStatusLabel",
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        ForeColor = ClipCordTheme.ShellMutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = ClipCordTheme.InterfaceFont(9.5f)
    };
    private readonly Func<IWin32Window, Task>? _checkForUpdatesAsync;
    private readonly AppSettings _appliedSettings;
    private readonly ActivityHistoryStore _activityHistory;
    private readonly IManualClipEditService? _manualClipEditService;
    private readonly Func<string, bool>? _launchMediaFile;
    private readonly IClipPlaybackPreparer? _playbackPreparer;
    private readonly IGalleryThumbnailProvider? _thumbnailProvider;
    private readonly SettingsPage _openingPage;
    private readonly bool _ownsActivityHistory;
    private RoundedPanel? _settingsNavigationItem;
    private RoundedPanel? _homeNavigationItem;
    private RoundedPanel? _activityNavigationItem;
    private RoundedPanel? _galleryNavigationItem;
    private RoundedPanel? _aboutNavigationItem;
    private BufferedTableLayoutPanel? _rootLayout;
    private Control? _saveBar;
    private Control? _navigationRail;
    private OutlineButton? _railDiscordRouteButton;
    private OutlineButton? _railLocalRouteButton;
    private HomeView? _homePage;
    private Control? _settingsPage;
    private BrandedScrollHost? _settingsScrollHost;
    private ActivityView? _activityPage;
    private GalleryView? _galleryPage;
    private AboutView? _aboutPage;
    private bool _busy;
    private bool _galleryBusy;
    private bool _dirtyTrackingReady;
    private bool _settingsDirty;
    private SettingsPage _currentPage;
    private ClipCaptureSource _captureSource = ClipCaptureSource.SteelSeriesGg;
    private string? _lastWatcherFullStatus;
    private Size _lastWindowRegionSize = Size.Empty;

    public AppSettings? SavedSettings { get; private set; }
    internal bool HasExplicitMaximizedBounds => !MaximizedBounds.IsEmpty;

    public SettingsForm(
        AppSettings settings,
        Icon? applicationIcon = null,
        Func<IWin32Window, Task>? checkForUpdatesAsync = null,
        Func<string>? watcherStatusProvider = null,
        ActivityHistoryStore? activityHistory = null,
        SettingsPage initialPage = SettingsPage.Settings,
        IManualClipEditService? manualClipEditService = null,
        Func<string, bool>? launchMediaFile = null,
        IClipPlaybackPreparer? playbackPreparer = null,
        IGalleryThumbnailProvider? thumbnailProvider = null)
    {
        Text = "ClipCord — Settings";
        _ownedApplicationIcon = applicationIcon;
        _appliedSettings = settings;
        _checkForUpdatesAsync = checkForUpdatesAsync;
        _watcherStatusProvider = watcherStatusProvider;
        _activityHistory = activityHistory ?? new ActivityHistoryStore(string.Empty);
        _manualClipEditService = manualClipEditService;
        _launchMediaFile = launchMediaFile;
        _playbackPreparer = playbackPreparer;
        _thumbnailProvider = thumbnailProvider;
        _ownsActivityHistory = activityHistory is null;
        _openingPage = initialPage;
        if (_ownedApplicationIcon is not null) Icon = _ownedApplicationIcon;

        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = GetDesignedClientSize(initialPage);
        MinimumSize = MinimumDesignedClientSize;
        BackColor = ClipCordTheme.Header;
        Padding = new Padding(ResizeGrip);
        Font = ClipCordTheme.InterfaceFont(9.5f);
        DoubleBuffered = true;

        _folderText.Text = settings.ClipsFolder;
        _webhookText.Text = settings.WebhookUrl;
        _uploaderNameText.MaxLength = AppSettings.MaximumUploaderNameLength;
        _uploaderNameText.Text = AppSettings.NormalizeUploaderName(settings.UploaderName);
        _compressionTarget.Text = $"{Math.Clamp(settings.CompressionTargetMb, 1, 100)} MB";
        ConfigureCompressionTargetPicker();
        _modeToggleHotkeyText.ReadOnly = true;
        _modeToggleHotkeyText.ShortcutsEnabled = false;
        _modeToggleHotkeyText.Text = AppSettings.NormalizeModeToggleHotkey(settings.ModeToggleHotkey);
        _modeToggleHotkeyText.KeyDown += CaptureModeToggleHotkey;
        _modeToggleHotkeyText.Enter += (_, _) => _modeToggleHotkeyText.SelectAll();
        _modeToggleHotkeyText.Leave += (_, _) => UpdateModeToggleHotkeyEditor();
        _modeToggleHotkeyAction.Click += (_, _) => ToggleModeHotkeyEnabled();
        UpdateModeToggleHotkeyEditor();
        _captureSource = AppSettings.NormalizeCaptureSource(settings.CaptureSource);
        _steelSeriesSourceButton.Click += (_, _) => SetCaptureSource(ClipCaptureSource.SteelSeriesGg);
        _nvidiaSourceButton.Click += (_, _) => SetCaptureSource(ClipCaptureSource.Nvidia);
        UpdateCaptureSourceSelection();
        _startWithWindows.Checked = settings.StartWithWindows;
        _uploadToDiscord.Checked = settings.UploadToDiscord;
        _uploadModeHelper.Name = "UploadModeHelperLabel";
        _uploadToDiscord.CheckedChanged += (_, _) => UpdateUploadModeText();
        UpdateUploadModeText();

        _browseButton.Click += BrowseClicked;
        _testButton.Click += TestClicked;
        _checkUpdatesButton.Click += CheckUpdatesClicked;
        _checkUpdatesButton.Name = "SettingsCheckUpdatesButton";
        _checkUpdatesButton.Enabled = _checkForUpdatesAsync is not null;
        _saveButton.Click += SaveClicked;
        _cancelButton.Click += (_, _) => ResetSettingsDraft();
        _statusLabel.Visible = false;
        _statusLabel.TextChanged += (_, _) =>
        {
            _statusLabel.Visible = !string.IsNullOrWhiteSpace(_statusLabel.Text);
            if (_statusLabel.Visible && !IsDisposed && !Disposing)
            {
                _pageSubtitleLabel.Text = _statusLabel.Text;
            }
        };
        _minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximizeButton.Click += (_, _) => ToggleMaximize();
        _closeButton.Click += (_, _) => Close();
        FormClosing += FormClosingWhileBusy;
        Resize += (_, _) =>
        {
            _maximizeButton.Glyph = WindowState == FormWindowState.Maximized
                ? BrandGlyph.Restore
                : BrandGlyph.Maximize;
            _maximizeButton.AccessibleName = WindowState == FormWindowState.Maximized ? "Restore" : "Maximize";
            _maximizeButton.Invalidate();
            UpdateWindowRegion();
            if (_currentPage == SettingsPage.Gallery) UpdatePageHeaderAction(_currentPage);
        };

        Controls.Add(BuildRootLayout());
        AcceptButton = null;
        CancelButton = null;
        ShowPage(initialPage);
        WireDirtyTracking();
        _dirtyTrackingReady = true;
        RecomputeSettingsDirty();

        _watcherStatusTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _watcherStatusTimer.Tick += (_, _) => UpdateWatcherStatus();
        UpdateWatcherStatus();
        _watcherStatusTimer.Start();
    }

    private Control BuildRootLayout()
    {
        var root = new BufferedTableLayoutPanel
        {
            Name = "RootLayout",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(NavigationRailLogicalWidth)));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(PageHeaderLogicalHeight)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        _navigationRail = BuildNavigationRail();
        root.Controls.Add(_navigationRail, 0, 0);
        root.SetRowSpan(_navigationRail, 3);
        root.Controls.Add(BuildHeader(), 1, 0);
        root.Controls.Add(BuildBody(), 1, 1);
        _saveBar = BuildSaveBar();
        root.Controls.Add(_saveBar, 1, 2);
        _rootLayout = root;
        return root;
    }

    private Control BuildHeader()
    {
        var header = new BufferedTableLayoutPanel
        {
            Name = "CustomTitleBar",
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(ScaleLogical(28), 0, 0, 0),
            BackColor = ClipCordTheme.Header
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Sum the independently rounded button widths. At fractional DPI scales,
        // scaling their 138px aggregate can be a few pixels narrower than scaling
        // each 46px button, which clips the Close action at the right edge.
        header.ColumnStyles.Add(new ColumnStyle(
            SizeType.Absolute,
            3 * ScaleLogical(TitleBarButtonLogicalWidth)));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var pageIdentity = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleLogical(7), 0, ScaleLogical(5)),
            BackColor = ClipCordTheme.Header
        };
        pageIdentity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pageIdentity.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        pageIdentity.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        pageIdentity.Controls.Add(_pageTitleLabel, 0, 0);
        pageIdentity.Controls.Add(_pageSubtitleLabel, 0, 1);

        var windowActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Header
        };
        windowActions.Controls.Add(_minimizeButton);
        windowActions.Controls.Add(_maximizeButton);
        windowActions.Controls.Add(_closeButton);
        foreach (var action in new[] { _minimizeButton, _maximizeButton, _closeButton })
        {
            action.Size = new Size(ScaleLogical(TitleBarButtonLogicalWidth), ScaleLogical(34));
        }

        _pageActionHost = new FlowLayoutPanel
        {
            Name = "PageActionHost",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(ScaleLogical(10), 0, ScaleLogical(10), 0),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Header
        };

        EnableWindowDrag(header);
        EnableWindowDrag(pageIdentity);
        EnableWindowDrag(_pageTitleLabel);
        EnableWindowDrag(_pageSubtitleLabel);
        header.Controls.Add(pageIdentity, 0, 0);
        header.Controls.Add(_pageActionHost, 1, 0);
        header.Controls.Add(windowActions, 2, 0);
        return header;
    }

    private Control BuildBody()
    {
        var pageHost = new Panel
        {
            Name = "PageHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _homePage = new HomeView(
            _appliedSettings,
            _activityHistory,
            _watcherStatusProvider,
            showPageHeader: false);
        _homePage.NavigateToActivityRequested += (_, _) => ShowPage(SettingsPage.Activity);
        _homePage.OpenClipsFolderRequested += (_, _) => OpenHomeFolder(_folderText.Text);
        _homePage.OpenUploadedFolderRequested += (_, _) =>
            OpenHomeFolder(UploadedFolder.FindExistingUploaded(_folderText.Text));
        _homePage.OpenLocalOnlyFolderRequested += (_, _) =>
            OpenHomeFolder(UploadedFolder.FindExistingLocalOnly(_folderText.Text));
        _homePage.OpenLogsRequested += (_, _) => OpenHomeLogs();
        _homePage.CheckUpdatesRequested += CheckUpdatesClicked;
        _settingsScrollHost = new BrandedScrollHost
        {
            Name = "SettingsScrollHost",
            AccessibleName = "ClipCord settings",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell,
            Content = BuildCards()
        };
        _settingsPage = _settingsScrollHost;
        _activityPage = new ActivityView(
            _activityHistory,
            _folderText.Text,
            allowLocalOnlyEditing: _manualClipEditService is not null);
        _activityPage.SetEmbeddedHeaderVisible(false);
        _activityPage.EditClipRequested += ActivityEditClipRequested;
        _galleryPage = new GalleryView(
            _folderText.Text,
            _manualClipEditService,
            _launchMediaFile,
            _playbackPreparer,
            _thumbnailProvider);
        _galleryPage.SetEmbeddedHeaderVisible(false);
        _galleryPage.HeaderChanged += (title, subtitle) =>
        {
            if (_currentPage != SettingsPage.Gallery || IsDisposed || Disposing) return;
            _pageTitleLabel.Text = title;
            _pageSubtitleLabel.Text = subtitle;
        };
        _galleryPage.OperationBusyChanged += GalleryOperationBusyChanged;
        _aboutPage = new AboutView(_appliedSettings, _watcherStatusProvider);
        _aboutPage.CheckUpdatesRequested += CheckUpdatesClicked;
        _aboutPage.SetBusy(false, _checkForUpdatesAsync is not null);
        _homePage.SetUpdateBusy(false, _checkForUpdatesAsync is not null);
        pageHost.Controls.Add(_homePage);
        pageHost.Controls.Add(_settingsPage);
        pageHost.Controls.Add(_activityPage);
        pageHost.Controls.Add(_galleryPage);
        pageHost.Controls.Add(_aboutPage);
        return pageHost;
    }

    private void OpenHomeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Process.Start(ActivityView.CreateOpenFolderStartInfo(path));
        }
        catch (Exception exception)
        {
            Log.Error("Could not open a Home shortcut folder.", exception);
            MessageBox.Show(
                this,
                "Windows could not open that folder.",
                "Could not open folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OpenHomeLogs()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var logPath = Path.Combine(SettingsStore.DataDirectory, "app.log");
            Process.Start(File.Exists(logPath)
                ? ActivityView.CreateSelectFileStartInfo(logPath)
                : ActivityView.CreateOpenFolderStartInfo(SettingsStore.DataDirectory));
        }
        catch (Exception exception)
        {
            Log.Error("Could not open logs from Home.", exception);
            MessageBox.Show(
                this,
                "Windows could not open ClipCord's logs.",
                "Could not open logs",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private Control BuildNavigationRail()
    {
        var rail = new BufferedTableLayoutPanel
        {
            Name = "NavigationRail",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(ScaleLogical(14), 0, ScaleLogical(14), ScaleLogical(14)),
            BackColor = ClipCordTheme.Sidebar,
            AccessibleName = "ClipCord navigation",
            AccessibleRole = AccessibleRole.MenuBar
        };
        rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(68)));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(164)));

        var brand = new BufferedTableLayoutPanel
        {
            Name = "RailBrand",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Sidebar
        };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(38)));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logo = new ClipCordLogoControl
        {
            Name = "HeaderLogo",
            Size = new Size(ScaleLogical(28), ScaleLogical(28)),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty
        };
        var version = typeof(SettingsForm).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var brandCopy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleLogical(17), 0, ScaleLogical(10)),
            BackColor = ClipCordTheme.Sidebar
        };
        brandCopy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brandCopy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        brandCopy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        brandCopy.Controls.Add(new Label
        {
            Name = "ProductNameLabel",
            Text = "ClipCord",
            AutoSize = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.DisplayFont(12f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        brandCopy.Controls.Add(new Label
        {
            Name = "ProductVersionLabel",
            Text = $"{version.Major}.{version.Minor}.{version.Build}",
            AutoSize = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.75f),
            Margin = Padding.Empty
        }, 0, 1);
        brand.Controls.Add(logo, 0, 0);
        brand.Controls.Add(brandCopy, 1, 0);
        EnableWindowDrag(brand);
        EnableWindowDrag(logo);
        EnableWindowDrag(brandCopy);
        foreach (Control child in brandCopy.Controls) EnableWindowDrag(child);

        var navigation = new BufferedTableLayoutPanel
        {
            Name = "SideNavigation",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 0),
            BackColor = ClipCordTheme.Sidebar
        };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < navigation.RowCount; index++)
        {
            navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(39)));
        }

        _homeNavigationItem = CreateNavigationItem("Home", BrandGlyph.Home, selected: false);
        ConfigureNavigationItem(_homeNavigationItem, "HomeNavItem", "Home", "Current ClipCord status", SettingsPage.Home);
        _settingsNavigationItem = CreateNavigationItem("Settings", BrandGlyph.Settings, selected: true);
        ConfigureNavigationItem(_settingsNavigationItem, "SettingsNavItem", "Settings", "ClipCord settings", SettingsPage.Settings);
        _activityNavigationItem = CreateNavigationItem("Activity", BrandGlyph.Activity, selected: false);
        ConfigureNavigationItem(_activityNavigationItem, "ActivityNavItem", "Activity", "Recent clip activity", SettingsPage.Activity);
        _galleryNavigationItem = CreateNavigationItem("Gallery", BrandGlyph.Gallery, selected: false);
        ConfigureNavigationItem(_galleryNavigationItem, "GalleryNavItem", "Gallery", "Browse uploaded and local-only clips", SettingsPage.Gallery);
        _aboutNavigationItem = CreateNavigationItem("About", BrandGlyph.About, selected: false);
        ConfigureNavigationItem(_aboutNavigationItem, "AboutNavItem", "About ClipCord", "Privacy, diagnostics, and project credits", SettingsPage.About);
        navigation.Controls.Add(_homeNavigationItem, 0, 0);
        navigation.Controls.Add(_settingsNavigationItem, 0, 1);
        navigation.Controls.Add(_activityNavigationItem, 0, 2);
        navigation.Controls.Add(_galleryNavigationItem, 0, 3);
        navigation.Controls.Add(_aboutNavigationItem, 0, 4);

        var modeCard = BuildRailStatusCard();
        rail.Controls.Add(brand, 0, 0);
        rail.Controls.Add(navigation, 0, 1);
        rail.Controls.Add(modeCard, 0, 2);
        return rail;
    }

    private void ConfigureNavigationItem(
        RoundedPanel item,
        string name,
        string accessibleName,
        string accessibleDescription,
        SettingsPage page)
    {
        item.Name = name;
        item.AccessibleName = accessibleName;
        item.AccessibleDescription = accessibleDescription;
        item.AccessibleRole = AccessibleRole.MenuItem;
        item.EnableKeyboardAccess(() => ShowPage(page));
        WireClick(item, () => ShowPage(page));
    }

    private Control BuildRailStatusCard()
    {
        var card = new RoundedPanel
        {
            Name = "RailStatusCard",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 12,
            Padding = new Padding(ScaleLogical(10), ScaleLogical(9), ScaleLogical(10), ScaleLogical(8)),
            Margin = Padding.Empty,
            AccessibleName = "Routing and watcher status"
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceSunken
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(34)));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(1)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(24)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "NEW CLIPS GO TO",
            AutoSize = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.25f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 5)
        }, 0, 0);

        var routes = new BufferedTableLayoutPanel
        {
            Name = "RailRouteSelector",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(2),
            BackColor = ClipCordTheme.SurfaceBase
        };
        routes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        routes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        routes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _railDiscordRouteButton = CreateRailRouteButton("● Discord", "Route new clips to Discord");
        _railLocalRouteButton = CreateRailRouteButton("● Local", "Keep new clips local only");
        _railDiscordRouteButton.Click += (_, _) => StageRailRoute(uploadToDiscord: true);
        _railLocalRouteButton.Click += (_, _) => StageRailRoute(uploadToDiscord: false);
        routes.Controls.Add(_railDiscordRouteButton, 0, 0);
        routes.Controls.Add(_railLocalRouteButton, 1, 0);
        layout.Controls.Add(routes, 0, 1);

        var hotkey = new Label
        {
            Name = "RailHotkeyHint",
            Text = $"{AppSettings.NormalizeModeToggleHotkey(_modeToggleHotkeyText.Text)}  to swap",
            AutoSize = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = new Padding(0, 6, 0, 5)
        };
        layout.Controls.Add(hotkey, 0, 2);
        layout.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.BorderDefault,
            Margin = Padding.Empty
        }, 0, 3);

        var watcherHeadline = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 0),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceSunken
        };
        watcherHeadline.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(16)));
        watcherHeadline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        watcherHeadline.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        watcherHeadline.Controls.Add(new HomeRouteDot
        {
            Name = "RailWatcherStatusDot",
            Dock = DockStyle.Fill,
            Accent = Color.FromArgb(49, 196, 130),
            Margin = Padding.Empty,
            AccessibleName = string.Empty
        }, 0, 0);
        _watcherStatusLabel.Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold);
        _watcherStatusLabel.ForeColor = ClipCordTheme.TextPrimary;
        watcherHeadline.Controls.Add(_watcherStatusLabel, 1, 0);
        layout.Controls.Add(watcherHeadline, 0, 4);
        layout.Controls.Add(_watcherStatusDetailLabel, 0, 5);
        card.Controls.Add(layout);
        UpdateRailRouteSelection();
        return card;
    }

    private static OutlineButton CreateRailRouteButton(string text, string accessibleName) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        AccessibleName = accessibleName,
        AccessibleRole = AccessibleRole.RadioButton,
        SurfaceColor = ClipCordTheme.SurfaceBase,
        HoverColor = ClipCordTheme.SurfaceControl,
        OutlineColor = Color.Transparent,
        ForeColor = ClipCordTheme.TextSecondary,
        Font = ClipCordTheme.InterfaceFont(8.25f)
    };

    private void StageRailRoute(bool uploadToDiscord)
    {
        if (_busy || _galleryBusy || _uploadToDiscord.Checked == uploadToDiscord) return;
        _uploadToDiscord.Checked = uploadToDiscord;
        // Routing a watcher is a durable lifecycle change. Bring the user to the
        // staged Settings draft rather than silently restarting the controller from
        // a decorative shell control.
        ShowPage(SettingsPage.Settings);
    }

    internal void ShowPage(SettingsPage page)
    {
        if (_settingsPage is null || _activityPage is null || _galleryPage is null || _aboutPage is null) return;
        if (_galleryBusy && page != SettingsPage.Gallery) return;

        _currentPage = page;
        var showHome = page == SettingsPage.Home;
        var showSettings = page == SettingsPage.Settings;
        var showActivity = page == SettingsPage.Activity;
        var showGallery = page == SettingsPage.Gallery;
        var showAbout = page == SettingsPage.About;
        if (_homePage is not null) _homePage.Visible = showHome;
        _settingsPage.Visible = showSettings;
        _activityPage.Visible = showActivity;
        _galleryPage.Visible = showGallery;
        _aboutPage.Visible = showAbout;
        if (showHome)
        {
            _galleryPage.Deactivate();
            _homePage?.BringToFront();
            _homePage?.ActivateView();
            _homePage?.RefreshViewport();
        }
        else if (showSettings)
        {
            _homePage?.DeactivateView();
            _galleryPage.Deactivate();
            _settingsPage.BringToFront();
            _settingsPage.PerformLayout();
            _settingsScrollHost?.RefreshContentLayout();
        }
        else if (showActivity)
        {
            _homePage?.DeactivateView();
            _galleryPage.Deactivate();
            _activityPage.BringToFront();
            _activityPage.RefreshViewport();
        }
        else if (showGallery)
        {
            _homePage?.DeactivateView();
            _galleryPage.BringToFront();
            _galleryPage.Activate(_folderText.Text);
        }
        else
        {
            _homePage?.DeactivateView();
            _galleryPage.Deactivate();
            _aboutPage.BringToFront();
            _aboutPage.RefreshStatus();
            _aboutPage.RefreshViewport();
        }

        UpdateNavigationSelection(_homeNavigationItem, showHome);
        UpdateNavigationSelection(_settingsNavigationItem, showSettings);
        UpdateNavigationSelection(_activityNavigationItem, showActivity);
        UpdateNavigationSelection(_galleryNavigationItem, showGallery);
        UpdateNavigationSelection(_aboutNavigationItem, showAbout);
        UpdatePageHeaderAction(page);
        UpdateSaveBarVisibility();
        Text = page switch
        {
            SettingsPage.Home => "ClipCord — Home",
            SettingsPage.Activity => "ClipCord — Activity",
            SettingsPage.Gallery => "ClipCord — Gallery",
            SettingsPage.About => "ClipCord — About",
            _ => "ClipCord — Settings"
        };
        (_pageTitleLabel.Text, _pageSubtitleLabel.Text) = page switch
        {
            SettingsPage.Home => ("Home", "Everything ClipCord is doing right now"),
            SettingsPage.Settings => ("Settings", "Where clips come from, and where they go"),
            SettingsPage.Activity => ("Activity", "Recent clip activity stored on this PC"),
            SettingsPage.Gallery => ("Gallery", "Uploaded and local-only archives, organised by game"),
            SettingsPage.About => ("About", "What ClipCord is, what it keeps, and who made it"),
            _ => ("ClipCord", string.Empty)
        };
    }

    private void UpdatePageHeaderAction(SettingsPage page)
    {
        if (_pageActionHost is null || _aboutPage is null) return;
        var homeAction = _homePage?.HeaderActionButton;
        var aboutAction = _aboutPage.UpdateActionButton;
        var galleryAction = _galleryPage?.HeaderActions;
        var useSharedGalleryHeader = page == SettingsPage.Gallery &&
                                     ClientSize.Width >= ScaleLogical(1050);
        var action = page switch
        {
            SettingsPage.Home => homeAction,
            SettingsPage.About => aboutAction,
            SettingsPage.Gallery when useSharedGalleryHeader => galleryAction,
            _ => null
        };
        foreach (var candidate in new[] { homeAction, aboutAction, galleryAction })
        {
            if (candidate is not null && ReferenceEquals(candidate.Parent, _pageActionHost) &&
                !ReferenceEquals(candidate, action))
            {
                _pageActionHost.Controls.Remove(candidate);
            }
        }
        if (!ReferenceEquals(action, galleryAction)) _galleryPage?.RestoreEmbeddedHeaderActions();
        if (action is null)
        {
            _pageActionHost.Visible = false;
            return;
        }

        action.Dock = DockStyle.None;
        if (ReferenceEquals(action, galleryAction))
        {
            action.AutoSize = true;
        }
        else
        {
            action.AutoSize = false;
            var logicalWidth = page == SettingsPage.Home ? 158 : 164;
            action.Size = new Size(ScaleLogical(logicalWidth), ScaleLogical(34));
        }
        action.Margin = Padding.Empty;
        if (!ReferenceEquals(action.Parent, _pageActionHost)) _pageActionHost.Controls.Add(action);
        _pageActionHost.Visible = true;
        action.BringToFront();
    }

    private static void UpdateNavigationSelection(RoundedPanel? item, bool selected)
    {
        if (item is null) return;
        item.BackColor = selected ? ClipCordTheme.VioletMuted : ClipCordTheme.Sidebar;
        item.BorderColor = Color.Transparent;
        item.AccessibleDescription = selected ? "Current page" : string.Empty;
        foreach (var control in EnumerateControls(item))
        {
            switch (control)
            {
                case GradientStrip strip when strip.Name == "NavigationSelectionStrip":
                    strip.Visible = selected;
                    break;
                case BrandGlyphControl glyph when glyph.Name == "NavigationGlyph":
                    glyph.GlyphColor = selected ? ClipCordTheme.Violet : ClipCordTheme.ShellMutedText;
                    glyph.Invalidate();
                    break;
                case Label label when label.Name == "NavigationLabel":
                    label.ForeColor = ClipCordTheme.ShellText;
                    label.Font = ClipCordTheme.InterfaceFont(
                        10f,
                        selected ? FontStyle.Bold : FontStyle.Regular);
                    break;
            }
        }
        item.Invalidate(true);
    }

    private RoundedPanel CreateNavigationItem(
        string text,
        BrandGlyph glyph,
        bool selected,
        bool unavailable = false,
        string? badgeText = null)
    {
        var surface = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = selected ? ClipCordTheme.VioletMuted : ClipCordTheme.Sidebar,
            BorderColor = Color.Transparent,
            CornerRadius = ScaleLogical(8),
            Margin = new Padding(0, 0, 0, ScaleLogical(3)),
            Padding = Padding.Empty
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(3)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(38)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selectionStrip = new GradientStrip
        {
            Name = "NavigationSelectionStrip",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 8),
            Visible = selected,
            Horizontal = false
        };
        layout.Controls.Add(selectionStrip, 0, 0);

        var icon = new BrandGlyphControl
        {
            Name = "NavigationGlyph",
            Glyph = glyph,
            GlyphColor = unavailable
                ? Color.FromArgb(105, 115, 134)
                : selected ? ClipCordTheme.Violet : ClipCordTheme.ShellMutedText,
            StrokeWidth = 1.9f,
            Dock = DockStyle.Fill,
            // The Figma rail uses a compact 16px glyph inside the existing 38px
            // identity column. Keep the column stable so every label remains
            // aligned, and inset the exact asset symmetrically at every DPI.
            Margin = new Padding(
                ScaleLogical(11),
                ScaleLogical(10),
                ScaleLogical(11),
                ScaleLogical(10)),
            Enabled = !unavailable
        };
        var label = new Label
        {
            Name = "NavigationLabel",
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = unavailable ? Color.FromArgb(111, 121, 141) : ClipCordTheme.ShellText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ClipCordTheme.InterfaceFont(10f, selected ? FontStyle.Bold : FontStyle.Regular),
            Enabled = !unavailable,
            Margin = new Padding(0, 0, 8, 0),
            BackColor = Color.Transparent
        };
        layout.Controls.Add(icon, 1, 0);
        layout.Controls.Add(label, 2, 0);
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            surface.AccessibleDescription = badgeText;
        }
        surface.Controls.Add(layout);
        return surface;
    }

    private Control BuildCards()
    {
        var cards = new BufferedTableLayoutPanel
        {
            Name = "SettingsCards",
            Dock = DockStyle.Fill,
            AutoScroll = false,
            ColumnCount = 2,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = ScalePadding(new Padding(28, 4, 28, 10)),
            BackColor = ClipCordTheme.Shell
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cards.Controls.Add(CreateSettingsSection(
            "CLIP SOURCE", BrandGlyph.Folder, BuildClipSourceCard(), new Padding(0, 0, 0, 10)), 0, 0);
        cards.SetColumnSpan(cards.GetControlFromPosition(0, 0)!, 2);
        cards.Controls.Add(CreateSettingsSection(
            "DISCORD DESTINATION", BrandGlyph.Upload, BuildDiscordCard(), new Padding(0, 0, 0, 10)), 0, 1);
        cards.SetColumnSpan(cards.GetControlFromPosition(0, 1)!, 2);
        cards.Controls.Add(CreateSettingsSection(
            "ROUTING & QUALITY", BrandGlyph.Film, BuildUploadBehaviorCard(), new Padding(0, 0, 7, 0)), 0, 2);
        cards.Controls.Add(CreateSettingsSection(
            "APPLICATION", BrandGlyph.Settings, BuildAppPreferencesCard(), new Padding(7, 0, 0, 0)), 1, 2);
        return cards;
    }

    private Control CreateSettingsSection(
        string title,
        BrandGlyph glyph,
        Control card,
        Padding margin)
    {
        var section = new BufferedTableLayoutPanel
        {
            Name = title.Replace(" ", string.Empty, StringComparison.Ordinal) + "Section",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = ScalePadding(margin),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(24)));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(22)));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.Controls.Add(new BrandGlyphControl
        {
            Glyph = glyph,
            GlyphColor = ClipCordTheme.TextTertiary,
            StrokeWidth = 1.4f,
            Dock = DockStyle.Fill,
            Margin = ScalePadding(new Padding(0, 3, 4, 3))
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextTertiary,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ClipCordTheme.InterfaceFont(7.75f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 1, 0);
        card.Margin = Padding.Empty;
        section.Controls.Add(heading, 0, 0);
        section.Controls.Add(card, 0, 1);
        return section;
    }

    private Control BuildClipSourceCard()
    {
        var layout = CreateCardContent(4);
        layout.ColumnCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(208)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(10)));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateFieldLabelBlock(
            "Clips folder",
            "Any folder that receives finished MP4 clips."), 0, 0);
        layout.Controls.Add(CreateFieldRow(CreateFieldHost(_folderText), _browseButton), 1, 0);
        layout.Controls.Add(CreateFieldLabelBlock(
            "Recorded with",
            "Tells ClipCord how your recorder files clips."), 0, 2);

        var sourceChoices = new FlowLayoutPanel
        {
            Name = "CaptureSourceSelector",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = ClipCordTheme.SettingsCard,
            Margin = Padding.Empty
        };
        _steelSeriesSourceButton.Name = "SteelSeriesCaptureSourceButton";
        _steelSeriesSourceButton.AccessibleName = "Record with SteelSeries GG";
        _nvidiaSourceButton.Name = "NvidiaCaptureSourceButton";
        _nvidiaSourceButton.AccessibleName = "Record with NVIDIA";
        _nvidiaSourceButton.Margin = ScalePadding(new Padding(8, 0, 0, 0));
        sourceChoices.Controls.Add(_steelSeriesSourceButton);
        sourceChoices.Controls.Add(_nvidiaSourceButton);
        layout.Controls.Add(sourceChoices, 1, 2);

        _captureSourceHelper.Name = "CaptureSourceHelperLabel";
        layout.Controls.Add(_captureSourceHelper, 1, 3);
        return CreateCard(
            BrandGlyph.ClipSource,
            "ClipSourceCard",
            "Clip source settings",
            "Clip source",
            "Any folder that receives MP4 clips.",
            layout,
            new Padding(0, 0, 0, 10),
            150);
    }

    private void SetCaptureSource(ClipCaptureSource source)
    {
        var normalized = AppSettings.NormalizeCaptureSource(source);
        if (_captureSource == normalized) return;
        _captureSource = normalized;
        UpdateCaptureSourceSelection();
        RecomputeSettingsDirty();
    }

    private void UpdateCaptureSourceSelection()
    {
        SetCaptureSourceSelected(_steelSeriesSourceButton, _captureSource == ClipCaptureSource.SteelSeriesGg);
        SetCaptureSourceSelected(_nvidiaSourceButton, _captureSource == ClipCaptureSource.Nvidia);
        _captureSourceHelper.Text = _captureSource == ClipCaptureSource.Nvidia
            ? @"New MP4 clips inside this folder's <game> subfolders are detected automatically."
            : "New MP4 clips in this folder are detected automatically.";
    }

    private static void SetCaptureSourceSelected(OutlineButton button, bool selected)
    {
        button.SurfaceColor = selected ? Color.FromArgb(67, 50, 104) : ClipCordTheme.SettingsButton;
        button.HoverColor = selected ? Color.FromArgb(78, 59, 121) : ClipCordTheme.SettingsButtonHover;
        button.OutlineColor = selected ? ClipCordTheme.Violet : ClipCordTheme.SettingsFieldBorder;
        button.AccessibilitySelected = selected;
        button.AccessibleDescription = selected ? "Selected capture source" : string.Empty;
        button.Invalidate();
    }

    private Control BuildDiscordCard()
    {
        var layout = CreateCardContent(4);
        layout.ColumnCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(208)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(8)));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateFieldLabelBlock(
            "Uploader name",
            "Shown beside every clip you send."), 0, 0);
        layout.Controls.Add(CreateFieldHost(_uploaderNameText), 1, 0);
        layout.Controls.Add(CreateFieldLabelBlock(
            "Webhook URL",
            "Encrypted with Windows DPAPI for this account only."), 0, 2);
        layout.Controls.Add(CreateFieldRow(CreateFieldHost(_webhookText), _testButton), 1, 2);
        return CreateCard(
            BrandGlyph.DiscordDestination,
            "DiscordDestinationCard",
            "Discord destination settings",
            "Discord destination",
            "Identify who sent the clip and where it goes.",
            layout,
            new Padding(0, 0, 0, 10),
            134);
    }

    private Control BuildUploadBehaviorCard()
    {
        var layout = CreateCardContent(4);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _uploadToDiscord.Margin = Padding.Empty;
        layout.Controls.Add(_uploadToDiscord, 0, 0);
        _uploadModeHelper.Margin = ScalePadding(new Padding(0, 0, 0, 6));
        layout.Controls.Add(_uploadModeHelper, 0, 1);
        layout.Controls.Add(new Panel
        {
            Dock = DockStyle.Top,
            Height = ScaleLogical(1),
            BackColor = ClipCordTheme.BorderDefault,
            Margin = ScalePadding(new Padding(0, 8, 0, 8))
        }, 0, 2);
        var compression = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, ScaleLogical(52)),
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard
        };
        compression.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        compression.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(132)));
        compression.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var compressionCopy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard
        };
        compressionCopy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        compressionCopy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        compressionCopy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        compressionCopy.Controls.Add(new Label
        {
            Text = "Compression target",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.InterfaceFont(10f),
            Margin = Padding.Empty
        }, 0, 0);
        var compressionHelper = new Label
        {
            Text = "Smaller targets are retried if Discord refuses a clip.",
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(ScaleLogical(125), 0),
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = new Padding(0, ScaleLogical(2), ScaleLogical(12), 0)
        };
        compressionCopy.Controls.Add(compressionHelper, 0, 1);
        var compressionHost = CreateCompressionHost();
        compressionHost.Dock = DockStyle.Bottom;
        compression.Controls.Add(compressionCopy, 0, 0);
        compression.Controls.Add(compressionHost, 1, 0);
        layout.Controls.Add(compression, 0, 3);
        return CreateCard(
            BrandGlyph.UploadBehavior,
            "UploadBehaviorCard",
            "Upload behavior settings",
            "Upload behavior",
            "Control routing and preferred quality.",
            layout,
            new Padding(0, 0, 6, 0),
            190);
    }

    private Control BuildAppPreferencesCard()
    {
        var layout = CreateCardContent(3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var shortcut = new BufferedTableLayoutPanel
        {
            Name = "ModeHotkeyEditor",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard
        };
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
        shortcut.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shortcut.Controls.Add(CreateFieldLabelBlock(
            "Mode shortcut",
            "Swaps the route for future clips."), 0, 0);
        shortcut.Controls.Add(
            CreateFieldRow(CreateFieldHost(_modeToggleHotkeyText), _modeToggleHotkeyAction),
            1,
            0);
        layout.Controls.Add(shortcut, 0, 0);

        var startup = CreatePreferenceRow(
            "Start with Windows",
            "ClipCord opens minimised to the tray.",
            _startWithWindows);
        _startWithWindows.Text = string.Empty;
        _startWithWindows.Name = "StartWithWindowsToggle";
        _startWithWindows.AccessibleName = "Start with Windows";
        _startWithWindows.Anchor = AnchorStyles.Right;
        _startWithWindows.Margin = Padding.Empty;
        startup.Margin = ScalePadding(new Padding(0, 2, 0, 0));
        layout.Controls.Add(startup, 0, 1);

        var updates = CreatePreferenceRow(
            "Updates",
            "Stable release channel.",
            _checkUpdatesButton);
        _checkUpdatesButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _checkUpdatesButton.Margin = Padding.Empty;
        updates.Margin = ScalePadding(new Padding(0, 2, 0, 0));
        layout.Controls.Add(updates, 0, 2);
        return CreateCard(
            BrandGlyph.AppPreferences,
            "AppPreferencesCard",
            "App preferences settings",
            "App preferences",
            "Startup, shortcut, and updates.",
            layout,
            new Padding(6, 0, 0, 0),
            190);
    }

    private BufferedTableLayoutPanel CreatePreferenceRow(
        string title,
        string subtitle,
        Control action)
    {
        var row = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Controls.Add(CreateFieldLabelBlock(title, subtitle), 0, 0);
        action.Anchor = AnchorStyles.Right;
        row.Controls.Add(action, 1, 0);
        return row;
    }

    private RoundedPanel CreateCard(
        BrandGlyph glyph,
        string name,
        string accessibleName,
        string title,
        string subtitle,
        Control content,
        Padding margin,
        int minimumHeight)
    {
        var card = new RoundedPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, ScaleLogical(minimumHeight)),
            BackColor = ClipCordTheme.SettingsCard,
            BorderColor = ClipCordTheme.SettingsCardBorder,
            CornerRadius = ScaleLogical(14),
            Padding = ScalePadding(new Padding(18, 12, 18, 12)),
            Margin = ScalePadding(margin),
            AccessibleName = accessibleName,
            AccessibleDescription = $"{title}. {subtitle}"
        };
        content.Margin = Padding.Empty;
        card.Controls.Add(content);
        return card;
    }

    private Control BuildSaveBar()
    {
        var outer = new Panel
        {
            Name = "SettingsSaveBarHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = ScalePadding(new Padding(28, 6, 28, 6)),
            BackColor = ClipCordTheme.SurfaceBase,
            Visible = false
        };
        var bar = new RoundedPanel
        {
            Name = "SettingsSaveBar",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = Color.FromArgb(245, 166, 35),
            CornerRadius = ScaleLogical(12),
            Padding = ScalePadding(new Padding(12, 5, 12, 5)),
            Margin = Padding.Empty,
            AccessibleName = "Unsaved settings"
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(26)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(92)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(126)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "⚠",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(245, 166, 35),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ClipCordTheme.InterfaceFont(10f),
            Margin = Padding.Empty,
            AccessibleName = "Warning"
        }, 0, 0);
        layout.Controls.Add(_dirtySummaryLabel, 1, 0);

        _cancelButton.Dock = DockStyle.Fill;
        _cancelButton.Size = new Size(ScaleLogical(82), ScaleLogical(36));
        _cancelButton.Margin = ScalePadding(new Padding(4, 2, 4, 2));
        _saveButton.Dock = DockStyle.Fill;
        _saveButton.Size = new Size(ScaleLogical(116), ScaleLogical(36));
        _saveButton.Margin = ScalePadding(new Padding(4, 2, 0, 2));
        layout.Controls.Add(_cancelButton, 2, 0);
        layout.Controls.Add(_saveButton, 3, 0);
        bar.Controls.Add(layout);
        outer.Controls.Add(bar);
        return outer;
    }

    private static TableLayoutPanel CreateCardContent(int rows)
    {
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = rows,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static Label CreateCardHeading(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.ShellText,
        Font = ClipCordTheme.DisplayFont(14f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 0, 4)
    };

    private static Label CreateCardSubtitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.SettingsMutedText,
        Font = ClipCordTheme.InterfaceFont(9f),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private Control CreateFieldLabelBlock(string title, string subtitle)
    {
        var block = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, ScaleLogical(16), 0),
            BackColor = ClipCordTheme.SettingsCard
        };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        block.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        block.Controls.Add(CreateInlineFieldLabel(title), 0, 0);
        block.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(ScaleLogical(205), 0),
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = new Padding(0, ScaleLogical(2), 0, 0),
            UseMnemonic = false
        }, 0, 1);
        return block;
    }

    private static Label CreateInlineFieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.ShellText,
        Font = ClipCordTheme.InterfaceFont(10f),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private static Label CreateHelper(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        MinimumSize = new Size(0, ClipCordTheme.InterfaceFont(9f).Height + 4),
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.SettingsMutedText,
        Font = ClipCordTheme.InterfaceFont(9f),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private RoundedPanel CreateFieldHost(Control field)
    {
        var host = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SettingsField,
            BorderColor = ClipCordTheme.SettingsFieldBorder,
            CornerRadius = ScaleLogical(7),
            Padding = ScalePadding(new Padding(11, 7, 11, 5)),
            Margin = Padding.Empty
        };
        host.Tag = field;
        FitFieldHost(host, field);
        field.Dock = DockStyle.Fill;
        field.Margin = Padding.Empty;
        host.Controls.Add(field);
        return host;
    }

    private Control CreateCompressionHost()
    {
        var host = new RoundedPanel
        {
            Width = ScaleLogical(132),
            BackColor = ClipCordTheme.SettingsField,
            BorderColor = ClipCordTheme.SettingsFieldBorder,
            CornerRadius = ScaleLogical(7),
            Padding = ScalePadding(new Padding(10, 5, 6, 4)),
            Margin = ScalePadding(new Padding(0, 2, 0, 0))
        };
        host.Tag = _compressionTarget;
        FitFieldHost(host, _compressionTarget);
        var picker = new BufferedTableLayoutPanel
        {
            Name = "CompressionTargetPicker",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsField
        };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(32)));
        picker.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _compressionTarget.Dock = DockStyle.Fill;
        _compressionTarget.Margin = Padding.Empty;
        _compressionTargetPresetButton.Dock = DockStyle.Fill;
        _compressionTargetPresetButton.Margin = Padding.Empty;
        picker.Controls.Add(_compressionTarget, 0, 0);
        picker.Controls.Add(_compressionTargetPresetButton, 1, 0);
        host.Controls.Add(picker);
        return host;
    }

    private void ConfigureCompressionTargetPicker()
    {
        var highContrast = SystemInformation.HighContrast;
        if (highContrast)
        {
            _compressionTarget.BackColor = SystemColors.Window;
            _compressionTarget.ForeColor = SystemColors.WindowText;
            _compressionTargetPresetButton.SurfaceColor = SystemColors.Window;
            _compressionTargetPresetButton.HoverColor = SystemColors.Highlight;
            _compressionTargetPresetButton.OutlineColor = SystemColors.WindowText;
            _compressionTargetPresetButton.ForeColor = SystemColors.WindowText;
        }
        _compressionTargetMenu.BackColor = highContrast ? SystemColors.Window : ClipCordTheme.SettingsField;
        _compressionTargetMenu.ForeColor = highContrast ? SystemColors.WindowText : ClipCordTheme.ShellText;
        _compressionTargetMenu.Font = ClipCordTheme.InterfaceFont(10f);
        _compressionTargetMenu.Renderer = highContrast
            ? new ToolStripSystemRenderer()
            : new ToolStripProfessionalRenderer(new CompressionMenuColorTable());
        foreach (var value in CompressionTargetPresets)
        {
            var item = new ToolStripMenuItem($"{value} MB")
            {
                BackColor = _compressionTargetMenu.BackColor,
                ForeColor = _compressionTargetMenu.ForeColor,
                AccessibleName = $"Use {value} MB compression target"
            };
            item.Click += (_, _) =>
            {
                _compressionTarget.Text = $"{value} MB";
                _compressionTarget.Focus();
                _compressionTarget.SelectAll();
            };
            _compressionTargetMenu.Items.Add(item);
        }

        _compressionTargetPresetButton.Click += (_, _) => ShowCompressionTargetPresets();
        _compressionTarget.KeyDown += HandleCompressionTargetKeyDown;
        _toolTip.SetToolTip(_compressionTargetPresetButton, "Choose a common compression target");
    }

    private void HandleCompressionTargetKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode != Keys.F4 && !(eventArgs.Alt && eventArgs.KeyCode == Keys.Down)) return;
        ShowCompressionTargetPresets();
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }

    private void ShowCompressionTargetPresets()
    {
        if (_compressionTargetMenu.Visible)
        {
            _compressionTargetMenu.Close();
            return;
        }

        var preferredWidth = Math.Max(_compressionTargetMenu.MinimumSize.Width, _compressionTargetMenu.PreferredSize.Width);
        _compressionTargetMenu.Show(
            _compressionTargetPresetButton,
            new Point(_compressionTargetPresetButton.Width - preferredWidth, _compressionTargetPresetButton.Height));
    }

    private Control CreateFieldRow(Control field, Button action)
    {
        var row = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Tag = (field, action);
        FitFieldActionRow(row, field, action);
        field.Margin = ScalePadding(new Padding(0, 0, 12, 0));
        action.Margin = Padding.Empty;
        row.Controls.Add(field, 0, 0);
        row.Controls.Add(action, 1, 0);
        return row;
    }

    private void FitFieldHost(RoundedPanel host, Control field)
    {
        host.MaximumSize = Size.Empty;
        var hostHeight = Math.Max(
            ScaleLogical(38),
            field.PreferredSize.Height + host.Padding.Vertical + ScaleLogical(2));
        var minimumWidth = host.Dock == DockStyle.Fill ? 0 : Math.Max(1, host.Width);
        host.Height = hostHeight;
        host.MinimumSize = new Size(minimumWidth, hostHeight);
        host.MaximumSize = new Size(0, hostHeight);
    }

    private void FitFieldActionRow(BufferedTableLayoutPanel row, Control field, Button action)
    {
        row.MaximumSize = Size.Empty;
        action.Height = Math.Max(ScaleLogical(38), action.PreferredSize.Height);
        var rowHeight = Math.Max(field.MinimumSize.Height, action.Height);
        row.Height = rowHeight;
        row.MinimumSize = new Size(0, rowHeight);
        row.MaximumSize = new Size(0, rowHeight);
    }

    internal void RefitDpiSensitiveControls()
    {
        if (IsDisposed || Disposing) return;
        SuspendLayout();
        foreach (var host in EnumerateControls(this)
                     .OfType<RoundedPanel>()
                     .Where(control => control.Tag is Control))
        {
            FitFieldHost(host, (Control)host.Tag!);
        }
        foreach (var row in EnumerateControls(this)
                     .OfType<BufferedTableLayoutPanel>()
                     .Where(control => control.Tag is ValueTuple<Control, Button>))
        {
            var (field, action) = ((Control, Button))row.Tag!;
            FitFieldActionRow(row, field, action);
        }
        ResumeLayout(true);
        PerformLayout();
    }

    private static TextBox CreateTextBox(string accessibleName, bool usePasswordCharacter = false) => new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = usePasswordCharacter,
        Font = ClipCordTheme.InterfaceFont(10.5f),
        BorderStyle = BorderStyle.None,
        BackColor = ClipCordTheme.SettingsField,
        ForeColor = ClipCordTheme.ShellText,
        AccessibleName = accessibleName
    };

    private static OutlineButton CreateSecondaryButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 38,
        SurfaceColor = ClipCordTheme.SettingsButton,
        HoverColor = ClipCordTheme.SettingsButtonHover,
        DisabledSurfaceColor = Color.FromArgb(28, 38, 55),
        DisabledTextColor = Color.FromArgb(112, 123, 142),
        OutlineColor = ClipCordTheme.SettingsFieldBorder,
        ForeColor = ClipCordTheme.ShellText,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Margin = Padding.Empty
    };

    private void UpdateWatcherStatus()
    {
        if (IsDisposed || Disposing) return;
        var fullStatus = _watcherStatusProvider?.Invoke() ?? "Settings";
        if (_aboutPage is { Visible: true }) _aboutPage.UpdateWatcherStatus(fullStatus);
        var presentation = AboutPageSupport.NormalizeWatcherStatus(
            fullStatus,
            !fullStatus.StartsWith("Discord closed", StringComparison.OrdinalIgnoreCase));
        var conciseStatus = presentation.Label;
        if (conciseStatus.Length > 28)
        {
            var safeLength = char.IsHighSurrogate(conciseStatus[27]) ? 27 : 28;
            conciseStatus = conciseStatus[..safeLength];
        }
        if (_watcherStatusLabel.Text != conciseStatus) _watcherStatusLabel.Text = conciseStatus;
        if (_watcherStatusDetailLabel.Text != presentation.Detail)
        {
            _watcherStatusDetailLabel.Text = presentation.Detail;
        }
        if (_lastWatcherFullStatus == fullStatus) return;
        _lastWatcherFullStatus = fullStatus;
        _watcherStatusLabel.AccessibleDescription = fullStatus;
        _toolTip.SetToolTip(_watcherStatusLabel, fullStatus);
    }

    private void BrowseClicked(object? sender, EventArgs eventArgs)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where your clipping tool saves MP4 clips",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderText.Text) ? _folderText.Text : string.Empty,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folderText.Text = dialog.SelectedPath;
    }

    private async void TestClicked(object? sender, EventArgs eventArgs)
    {
        if (!WebhookValidation.IsDiscordWebhook(_webhookText.Text.Trim()))
        {
            MessageBox.Show(this, "Enter a valid HTTPS Discord webhook URL.", "Invalid webhook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true, "Testing webhook…");
        try
        {
            using var client = new DiscordWebhookClient();
            await client.TestConnectionAsync(
                _webhookText.Text.Trim(),
                AppSettings.NormalizeUploaderName(_uploaderNameText.Text),
                CancellationToken.None);
            if (IsDisposed || Disposing) return;
            _statusLabel.ForeColor = Color.FromArgb(78, 214, 142);
            _statusLabel.Text = "Connection successful — check the Discord channel.";
        }
        catch (Exception exception)
        {
            if (IsDisposed || Disposing) return;
            _statusLabel.ForeColor = ClipCordTheme.Coral;
            _statusLabel.Text = "Connection failed.";
            MessageBox.Show(this, exception.Message, "Webhook test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed && !Disposing) SetBusy(false);
        }
    }

    private void SaveClicked(object? sender, EventArgs eventArgs)
    {
        if (!TryValidate(out var settings)) return;
        SavedSettings = settings;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async void CheckUpdatesClicked(object? sender, EventArgs eventArgs)
    {
        if (_checkForUpdatesAsync is null) return;

        SetBusy(true, "Checking for updates…");
        try
        {
            await _checkForUpdatesAsync(this);
            if (IsDisposed || Disposing) return;
            _aboutPage?.SetUpdateState("Update check finished");
            _statusLabel.ForeColor = ClipCordTheme.ShellMutedText;
            _statusLabel.Text = "Update check finished.";
        }
        catch (Exception exception)
        {
            Log.Error("Could not complete a manual update check.", exception);
            if (IsDisposed || Disposing) return;
            _statusLabel.ForeColor = ClipCordTheme.Coral;
            _statusLabel.Text = "Update check failed.";
            _aboutPage?.SetUpdateState("Update check unavailable");
            MessageBox.Show(
                this,
                "The update check could not be completed.",
                "Update check failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CaptureModeToggleHotkey(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Tab) return;
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;

        if (eventArgs.Modifiers == Keys.None && eventArgs.KeyCode is Keys.Back or Keys.Delete)
        {
            _modeToggleHotkeyText.Text = string.Empty;
            UpdateModeToggleHotkeyEditor();
            return;
        }

        if (!GlobalHotkeyBinding.TryFromKeyData(eventArgs.KeyData, out var binding)) return;
        _modeToggleHotkeyText.Text = binding.DisplayText;
        UpdateModeToggleHotkeyEditor();
    }

    private void ToggleModeHotkeyEnabled()
    {
        _modeToggleHotkeyText.Text = string.IsNullOrWhiteSpace(_modeToggleHotkeyText.Text)
            ? GlobalHotkeyBinding.DefaultDisplayText
            : string.Empty;
        UpdateModeToggleHotkeyEditor();
    }

    private void UpdateModeToggleHotkeyEditor()
    {
        var disabled = string.IsNullOrWhiteSpace(_modeToggleHotkeyText.Text);
        _modeToggleHotkeyAction.Text = disabled ? "Use default" : "Disable";
        var guidance = disabled
            ? "The global mode shortcut is disabled. Use the default or focus this field and press a new shortcut."
            : "Works while ClipCord is running. Focus this field and press a new shortcut; Backspace disables it.";
        _modeToggleHotkeyText.AccessibleDescription = guidance;
        _toolTip.SetToolTip(_modeToggleHotkeyText, guidance);
        _toolTip.SetToolTip(_modeToggleHotkeyAction, disabled
            ? $"Restore {GlobalHotkeyBinding.DefaultDisplayText}."
            : "Disable the global mode shortcut.");
        if (_navigationRail is not null)
        {
            var hint = EnumerateControls(_navigationRail)
                .OfType<Label>()
                .FirstOrDefault(label => label.Name == "RailHotkeyHint");
            if (hint is not null)
            {
                var shortcut = disabled ? "Shortcut off" : AppSettings.NormalizeModeToggleHotkey(_modeToggleHotkeyText.Text);
                hint.Text = $"{shortcut}  to swap";
            }
        }
        RecomputeSettingsDirty();
    }

    private bool TryValidate([NotNullWhen(true)] out AppSettings? settings)
    {
        if (!TryParseCompressionTarget(_compressionTarget.Text, out var compressionTargetMb))
        {
            settings = null;
            MessageBox.Show(
                this,
                "Enter a compression target from 1 to 100 MB.",
                "Invalid compression target",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var modeToggleHotkey = _modeToggleHotkeyText.Text.Trim();
        GlobalHotkeyBinding parsedHotkey = default;
        if (!string.IsNullOrWhiteSpace(modeToggleHotkey) &&
            !GlobalHotkeyBinding.TryParse(modeToggleHotkey, out parsedHotkey))
        {
            settings = null;
            MessageBox.Show(
                this,
                "Choose a shortcut containing Ctrl or Alt plus a letter, number, or F-key.",
                "Invalid mode shortcut",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(modeToggleHotkey)) modeToggleHotkey = parsedHotkey.DisplayText;

        settings = new AppSettings(
            _folderText.Text.Trim(),
            _webhookText.Text.Trim(),
            _startWithWindows.Checked,
            compressionTargetMb,
            AppSettings.NormalizeUploaderName(_uploaderNameText.Text),
            _uploadToDiscord.Checked,
            modeToggleHotkey,
            _captureSource);

        if (_uploadToDiscord.Checked && string.IsNullOrWhiteSpace(_uploaderNameText.Text))
        {
            MessageBox.Show(this, "Enter the name Discord should show with uploaded clips.", "Invalid uploader name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!Directory.Exists(settings.ClipsFolder))
        {
            MessageBox.Show(this, "Choose an existing clips folder.", "Invalid folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (_uploadToDiscord.Checked && !WebhookValidation.IsDiscordWebhook(settings.WebhookUrl))
        {
            MessageBox.Show(this, "Enter a valid HTTPS Discord webhook URL.", "Invalid webhook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void UpdateUploadModeText()
    {
        if (_uploadToDiscord.Checked)
        {
            _uploadModeHelper.Text = "New clips upload to Discord and move to uploaded.";
            _toolTip.SetToolTip(
                _uploadToDiscord,
                "New clips are sent to Discord and then organized under uploaded by game.");
        }
        else
        {
            _uploadModeHelper.Text = "No Discord request; new clips move to local-only.";
            _toolTip.SetToolTip(
                _uploadToDiscord,
                "New clips are not sent to Discord and are organized under local-only by game.");
        }
        UpdateRailRouteSelection();
        RecomputeSettingsDirty();
    }

    private void WireDirtyTracking()
    {
        _folderText.TextChanged += (_, _) => RecomputeSettingsDirty();
        _webhookText.TextChanged += (_, _) => RecomputeSettingsDirty();
        _uploaderNameText.TextChanged += (_, _) => RecomputeSettingsDirty();
        _compressionTarget.TextChanged += (_, _) => RecomputeSettingsDirty();
        _modeToggleHotkeyText.TextChanged += (_, _) => RecomputeSettingsDirty();
        _startWithWindows.CheckedChanged += (_, _) => RecomputeSettingsDirty();
    }

    private void RecomputeSettingsDirty()
    {
        if (!_dirtyTrackingReady || IsDisposed || Disposing) return;
        var changedCount = GetChangedSettingsFieldCount();
        _settingsDirty = changedCount > 0;
        _dirtySummaryLabel.Text = _settingsDirty
            ? $"{changedCount} unsaved change{(changedCount == 1 ? string.Empty : "s")}  ·  clips keep routing with the saved settings until you apply them"
            : string.Empty;
        UpdateSaveBarVisibility();
    }

    private int GetChangedSettingsFieldCount()
    {
        var changed = 0;
        if (!string.Equals(_folderText.Text.Trim(), _appliedSettings.ClipsFolder, StringComparison.Ordinal)) changed++;
        if (!string.Equals(_webhookText.Text.Trim(), _appliedSettings.WebhookUrl, StringComparison.Ordinal)) changed++;
        if (!string.Equals(
                AppSettings.NormalizeUploaderName(_uploaderNameText.Text),
                AppSettings.NormalizeUploaderName(_appliedSettings.UploaderName),
                StringComparison.Ordinal)) changed++;
        if (!TryParseCompressionTarget(_compressionTarget.Text, out var target) ||
            target != Math.Clamp(_appliedSettings.CompressionTargetMb, 1, 100)) changed++;
        if (!string.Equals(
                AppSettings.NormalizeModeToggleHotkey(_modeToggleHotkeyText.Text),
                AppSettings.NormalizeModeToggleHotkey(_appliedSettings.ModeToggleHotkey),
                StringComparison.Ordinal)) changed++;
        if (_startWithWindows.Checked != _appliedSettings.StartWithWindows) changed++;
        if (_uploadToDiscord.Checked != _appliedSettings.UploadToDiscord) changed++;
        if (_captureSource != AppSettings.NormalizeCaptureSource(_appliedSettings.CaptureSource)) changed++;
        return changed;
    }

    private void ResetSettingsDraft()
    {
        if (_busy || _galleryBusy) return;
        _dirtyTrackingReady = false;
        try
        {
            _folderText.Text = _appliedSettings.ClipsFolder;
            _webhookText.Text = _appliedSettings.WebhookUrl;
            _uploaderNameText.Text = AppSettings.NormalizeUploaderName(_appliedSettings.UploaderName);
            _compressionTarget.Text = $"{Math.Clamp(_appliedSettings.CompressionTargetMb, 1, 100)} MB";
            _modeToggleHotkeyText.Text = AppSettings.NormalizeModeToggleHotkey(_appliedSettings.ModeToggleHotkey);
            _startWithWindows.Checked = _appliedSettings.StartWithWindows;
            _uploadToDiscord.Checked = _appliedSettings.UploadToDiscord;
            _captureSource = AppSettings.NormalizeCaptureSource(_appliedSettings.CaptureSource);
            UpdateModeToggleHotkeyEditor();
            UpdateCaptureSourceSelection();
            UpdateUploadModeText();
        }
        finally
        {
            _dirtyTrackingReady = true;
        }
        RecomputeSettingsDirty();
    }

    private void UpdateRailRouteSelection()
    {
        if (_railDiscordRouteButton is null || _railLocalRouteButton is null) return;
        ApplyRailRouteButtonState(_railDiscordRouteButton, _uploadToDiscord.Checked);
        ApplyRailRouteButtonState(_railLocalRouteButton, !_uploadToDiscord.Checked);
    }

    private static void ApplyRailRouteButtonState(OutlineButton button, bool selected)
    {
        button.SurfaceColor = selected ? ClipCordTheme.VioletMuted : ClipCordTheme.SurfaceBase;
        button.HoverColor = selected ? Color.FromArgb(61, 48, 91) : ClipCordTheme.SurfaceControl;
        button.OutlineColor = selected ? ClipCordTheme.Violet : Color.Transparent;
        button.ForeColor = selected ? ClipCordTheme.TextPrimary : ClipCordTheme.TextTertiary;
        button.AccessibilitySelected = selected;
        button.AccessibleDescription = selected ? "Selected route" : string.Empty;
        button.Invalidate();
    }

    private void UpdateSaveBarVisibility()
    {
        if (_rootLayout is null || _saveBar is null || _rootLayout.RowStyles.Count < 3) return;
        var visible = _currentPage == SettingsPage.Settings && _settingsDirty;
        _rootLayout.RowStyles[2].SizeType = SizeType.Absolute;
        _rootLayout.RowStyles[2].Height = visible ? ScaleLogical(SaveBarLogicalHeight) : 0;
        _saveBar.Visible = visible;
        AcceptButton = visible && !_busy && !_galleryBusy ? _saveButton : null;
    }

    internal static bool TryParseCompressionTarget(string? text, out int value)
    {
        value = 0;
        if (text is null) return false;
        var match = CompressionTargetPattern.Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups["value"].Value, out var parsed) ||
            parsed is < 1 or > 100)
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (IsDisposed || Disposing) return;

        _browseButton.Enabled = !busy;
        _testButton.Enabled = !busy;
        _checkUpdatesButton.Enabled = !busy && _checkForUpdatesAsync is not null;
        _aboutPage?.SetBusy(busy, _checkForUpdatesAsync is not null);
        _homePage?.SetUpdateBusy(busy, _checkForUpdatesAsync is not null);
        _modeToggleHotkeyText.Enabled = !busy;
        _modeToggleHotkeyAction.Enabled = !busy;
        _uploadToDiscord.Enabled = !busy;
        if (_railDiscordRouteButton is not null) _railDiscordRouteButton.Enabled = !busy;
        if (_railLocalRouteButton is not null) _railLocalRouteButton.Enabled = !busy;
        _saveButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _minimizeButton.Enabled = !busy;
        _maximizeButton.Enabled = !busy;
        _closeButton.Enabled = !busy;
        if (status is not null)
        {
            _statusLabel.ForeColor = ClipCordTheme.ShellMutedText;
            _statusLabel.Text = status;
        }
        UpdateSaveBarVisibility();
    }

    private void ActivityEditClipRequested(ClipActivityEntry entry)
    {
        if (_galleryPage is null || IsDisposed || Disposing) return;
        // A manual upload owns the shell while it runs; do not navigate out from under it.
        if (_galleryBusy) return;
        ShowPage(SettingsPage.Gallery);
        if (_galleryPage.TryOpenEditorFor(entry.CurrentPath ?? string.Empty)) return;
        MessageBox.Show(
            this,
            "This clip is no longer available in the Local-only archive, so it cannot be edited.",
            "Cannot edit clip",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void GalleryOperationBusyChanged(bool busy)
    {
        _galleryBusy = busy;
        if (IsDisposed || Disposing) return;
        if (_homeNavigationItem is not null) _homeNavigationItem.Enabled = !busy;
        if (_settingsNavigationItem is not null) _settingsNavigationItem.Enabled = !busy;
        if (_activityNavigationItem is not null) _activityNavigationItem.Enabled = !busy;
        if (_galleryNavigationItem is not null) _galleryNavigationItem.Enabled = true;
        if (_aboutNavigationItem is not null) _aboutNavigationItem.Enabled = !busy;
        if (_railDiscordRouteButton is not null) _railDiscordRouteButton.Enabled = !busy && !_busy;
        if (_railLocalRouteButton is not null) _railLocalRouteButton.Enabled = !busy && !_busy;
        _saveButton.Enabled = !busy && !_busy;
        _cancelButton.Enabled = !busy && !_busy;
        _closeButton.Enabled = !busy && !_busy;
        _maximizeButton.Enabled = !busy && !_busy;
        if (busy)
        {
            _statusLabel.ForeColor = ClipCordTheme.ShellMutedText;
            _statusLabel.Text = "Editing and manual upload in progress…";
            _privacySummaryLabel.Text = "The Local-only original remains protected until Discord confirms success.";
        }
        else if (_galleryPage is { Visible: true })
        {
            _statusLabel.Text = "Gallery reads uploaded and local-only archives only while this page is open.";
            _privacySummaryLabel.Text = "Playing or browsing a local-only clip never uploads it.";
        }
        UpdateSaveBarVisibility();
    }

    private void FormClosingWhileBusy(object? sender, FormClosingEventArgs eventArgs)
    {
        if ((_busy || _galleryBusy) && eventArgs.CloseReason == CloseReason.UserClosing) eventArgs.Cancel = true;
    }

    private void EnableWindowDrag(Control control)
    {
        control.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left) return;
            if (eventArgs.Clicks > 1)
            {
                ToggleMaximize();
                return;
            }
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
        };
    }

    internal void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            return;
        }

        WindowState = FormWindowState.Maximized;
    }

    private static IEnumerable<Control> EnumerateControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateControls(child)) yield return descendant;
        }
    }

    private static void WireClick(Control control, Action action)
    {
        control.Cursor = Cursors.Hand;
        control.Click += (_, _) => action();
        foreach (Control child in control.Controls) WireClick(child, action);
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        var workingArea = Screen.FromControl(this).WorkingArea;
        var availableWidth = Math.Max(1, workingArea.Width - 24);
        var availableHeight = Math.Max(1, workingArea.Height - 24);
        var scaledMinimum = GetScaledMinimumSize(DeviceDpi);
        MinimumSize = new Size(
            Math.Min(scaledMinimum.Width, availableWidth),
            Math.Min(scaledMinimum.Height, availableHeight));
        var designedSize = GetDesignedOpeningSize(_openingPage, DeviceDpi);
        var fittedSize = new Size(
            Math.Min(designedSize.Width, availableWidth),
            Math.Min(designedSize.Height, availableHeight));
        if (fittedSize != Size)
        {
            Size = fittedSize;
            Location = new Point(
                workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
        }
        base.OnShown(eventArgs);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && _galleryPage is { Visible: true } &&
            _galleryPage.HandleEscape())
        {
            return true;
        }
        if (keyData == Keys.Escape && !_busy && !_galleryBusy)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    internal static Size GetDesignedOpeningSize(SettingsPage page, int dpi)
    {
        var clientSize = GetDesignedClientSize(page);
        var scale = Math.Max(96, dpi) / 96d;
        return new Size(
            (int)Math.Round(clientSize.Width * scale),
            (int)Math.Round(clientSize.Height * scale));
    }

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));

    private Padding ScalePadding(Padding value) => new(
        value.Left == 0 ? 0 : ScaleLogical(value.Left),
        value.Top == 0 ? 0 : ScaleLogical(value.Top),
        value.Right == 0 ? 0 : ScaleLogical(value.Right),
        value.Bottom == 0 ? 0 : ScaleLogical(value.Bottom));

    private static Size GetDesignedClientSize(SettingsPage page) => page switch
    {
        SettingsPage.Home => HomeDesignedClientSize,
        SettingsPage.Activity => ActivityDesignedClientSize,
        SettingsPage.Gallery => GalleryDesignedClientSize,
        SettingsPage.About => AboutDesignedClientSize,
        _ => SettingsDesignedClientSize
    };

    internal static Size GetScaledMinimumSize(int dpi)
    {
        var scale = Math.Max(96, dpi) / 96d;
        return new Size(
            (int)Math.Round(MinimumDesignedClientSize.Width * scale),
            (int)Math.Round(MinimumDesignedClientSize.Height * scale));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        var workingArea = Screen.FromControl(this).WorkingArea;
        var scaledMinimum = GetScaledMinimumSize(DeviceDpi);
        MinimumSize = new Size(
            Math.Min(scaledMinimum.Width, Math.Max(1, workingArea.Width - 24)),
            Math.Min(scaledMinimum.Height, Math.Max(1, workingArea.Height - 24)));
        RefitDpiSensitiveControls();
        if (IsHandleCreated && !IsDisposed && !Disposing)
        {
            try
            {
                BeginInvoke((Action)RefitDpiSensitiveControls);
            }
            catch (InvalidOperationException)
            {
                // The window closed while the post-DPI layout pass was being queued.
            }
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        var preference = DwmRoundPreference;
        _ = DwmSetWindowAttribute(
            Handle,
            DwmWindowCornerPreference,
            ref preference,
            Marshal.SizeOf<int>());
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        if (WindowState == FormWindowState.Maximized)
        {
            Region?.Dispose();
            Region = null;
            _lastWindowRegionSize = Size.Empty;
            return;
        }
        if (_lastWindowRegionSize == Size) return;
        _lastWindowRegionSize = Size;
        Region?.Dispose();
        using var path = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width, Height), 10);
        Region = new Region(path);
    }

    private sealed class CompressionMenuColorTable : ProfessionalColorTable
    {
        private static Color Surface =>
            SystemInformation.HighContrast ? SystemColors.Window : ClipCordTheme.SettingsField;
        private static Color Selection =>
            SystemInformation.HighContrast ? SystemColors.Highlight : ClipCordTheme.SettingsButtonHover;
        private static Color Border =>
            SystemInformation.HighContrast ? SystemColors.WindowText : ClipCordTheme.SettingsFieldBorder;

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmGetMinMaxInfo) ApplyWorkingAreaBounds(message.LParam);
        base.WndProc(ref message);
        if (message.Msg != WmNcHitTest || WindowState != FormWindowState.Normal ||
            message.Result.ToInt32() != 1)
        {
            return;
        }

        var value = message.LParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(value & 0xffff)), unchecked((short)((value >> 16) & 0xffff)));
        var point = PointToClient(screenPoint);
        message.Result = (IntPtr)HitTestResizeGrip(point);
    }

    internal int HitTestResizeGrip(Point point)
    {
        var left = point.X <= ResizeGrip;
        var right = point.X >= ClientSize.Width - ResizeGrip;
        var top = point.Y <= ResizeGrip;
        var bottom = point.Y >= ClientSize.Height - ResizeGrip;
        return top && left ? HtTopLeft :
            top && right ? HtTopRight :
            bottom && left ? HtBottomLeft :
            bottom && right ? HtBottomRight :
            left ? HtLeft :
            right ? HtRight :
            top ? HtTop :
            bottom ? HtBottom : 1;
    }

    private void ApplyWorkingAreaBounds(IntPtr data)
    {
        var monitor = MonitorFromWindow(Handle, 2);
        if (monitor == IntPtr.Zero) return;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(data);
        minMax.MaxPosition.X = Math.Abs(monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left);
        minMax.MaxPosition.Y = Math.Abs(monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top);
        minMax.MaxSize.X = monitorInfo.WorkArea.Width;
        minMax.MaxSize.Y = monitorInfo.WorkArea.Height;
        Marshal.StructureToPtr(minMax, data, false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcherStatusTimer.Stop();
            _watcherStatusTimer.Dispose();
            _compressionTargetMenu.Dispose();
            _toolTip.Dispose();
            if (_ownsActivityHistory) _activityHistory.Dispose();
            _ownedApplicationIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle MonitorArea;
        public Rectangle WorkArea;
        public uint Flags;
    }
}
