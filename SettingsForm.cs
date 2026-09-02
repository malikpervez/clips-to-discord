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
    Capture,
    Routes,
    SilhouetteLayouts,
    Gallery,
    About
}

internal sealed class SettingsForm : Form
{
    internal event EventHandler<GalleryRenditionRetryRequestedEventArgs>?
        GalleryRenditionRetryRequested;

    internal static readonly Size DesignedClientSize = new(1200, 760);
    internal static readonly Size SettingsDesignedClientSize = DesignedClientSize;
    internal static readonly Size ActivityDesignedClientSize = DesignedClientSize;
    internal static readonly Size GalleryDesignedClientSize = DesignedClientSize;
    internal static readonly Size CaptureDesignedClientSize = DesignedClientSize;
    internal static readonly Size RoutesDesignedClientSize = DesignedClientSize;
    internal static readonly Size SilhouetteLayoutsDesignedClientSize = DesignedClientSize;
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
        Name = "WatcherStatusLabel",
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.ShellText,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold)
    };
    private readonly Label _watcherStatusDetailLabel = new()
    {
        Name = "WatcherStatusDetailLabel",
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
        AutoEllipsis = true,
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
    private readonly OutlineButton _manageRoutingConnectionsButton =
        CreateSecondaryButton("Manage connections", 150);
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
    private readonly IFavoritesService _favorites;
    private readonly CaptureSettings _initialCaptureSettings;
    private readonly bool _captureEngineAvailable;
    private readonly Action<CaptureSettings>? _saveCaptureSettings;
    private readonly IManualCaptureRecorder? _manualCaptureRecorder;
    private readonly Func<bool>? _captureLibraryAccessAllowed;
    private readonly Func<string, bool>? _repairCaptureLibraryRoot;
    private readonly string _silhouetteSettingsDirectory;
    private readonly DiscordConnectionCatalog? _discordConnectionCatalog;
    private readonly RoutingRouteManager? _routingRouteManager;
    private readonly Func<RoutesRuntimeViewState>? _routesRuntimeStateProvider;
    private readonly Func<Task<bool>>? _retryRoutesRuntimeAsync;
    private readonly IRoutingLocalOnlyModeViewSource? _routingLocalOnlyMode;
    private readonly Func<RoutingUiPresentationSnapshot?>? _routingPresentationProvider;
    private RoundedPanel? _settingsNavigationItem;
    private RoundedPanel? _homeNavigationItem;
    private RoundedPanel? _activityNavigationItem;
    private RoundedPanel? _captureNavigationItem;
    private RoundedPanel? _routesNavigationItem;
    private RoundedPanel? _galleryNavigationItem;
    private RoundedPanel? _aboutNavigationItem;
    private BufferedTableLayoutPanel? _rootLayout;
    private Control? _saveBar;
    private Control? _navigationRail;
    private Label? _railRoutingTitleLabel;
    private TableLayoutPanel? _railRouteSelector;
    private RoutingRailSummaryControl? _railDestinationSummary;
    private OutlineButton? _railDiscordRouteButton;
    private OutlineButton? _railLocalRouteButton;
    private Label? _railHotkeyHint;
    private HomeRouteDot? _railWatcherStatusDot;
    private Control? _clipsFolderLabelBlock;
    private Control? _captureSourceLabelBlock;
    private Control? _webhookLabelBlock;
    private Control? _modeHotkeyLabelBlock;
    private HomeView? _homePage;
    private Control? _settingsPage;
    private BrandedScrollHost? _settingsScrollHost;
    private ActivityView? _activityPage;
    private CaptureView? _capturePage;
    private RoutesView? _routesPage;
    private SilhouetteLayoutEditorView? _silhouetteLayoutsPage;
    private GalleryView? _galleryPage;
    private AboutView? _aboutPage;
    private bool _busy;
    private bool _galleryBusy;
    private bool _homeRoutingActionBusy;
    private bool _dirtyTrackingReady;
    private bool _settingsDirty;
    private bool? _lastLegacySettingsControlsAvailable;
    private readonly List<RoundedPanel> _managedSettingsNavigationRows = [];
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
        IGalleryThumbnailProvider? thumbnailProvider = null,
        IFavoritesService? favorites = null,
        CaptureSettings? captureSettings = null,
        bool captureEngineAvailable = false,
        Action<CaptureSettings>? saveCaptureSettings = null,
        IManualCaptureRecorder? manualCaptureRecorder = null,
        string? silhouetteSettingsDirectory = null,
        DiscordConnectionCatalog? discordConnectionCatalog = null,
        RoutingRouteManager? routingRouteManager = null,
        Func<bool>? captureLibraryAccessAllowed = null,
        Func<string, bool>? repairCaptureLibraryRoot = null,
        Func<RoutesRuntimeViewState>? routesRuntimeStateProvider = null,
        Func<Task<bool>>? retryRoutesRuntimeAsync = null,
        IRoutingLocalOnlyModeViewSource? localOnlyMode = null,
        Func<RoutingUiPresentationSnapshot?>? routingPresentationProvider = null)
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
        _favorites = favorites ?? new FavoritesService();
        _initialCaptureSettings = CaptureSettings.Normalize(captureSettings);
        _captureEngineAvailable = captureEngineAvailable;
        _saveCaptureSettings = saveCaptureSettings;
        _manualCaptureRecorder = manualCaptureRecorder;
        _captureLibraryAccessAllowed = captureLibraryAccessAllowed;
        _repairCaptureLibraryRoot = repairCaptureLibraryRoot;
        _discordConnectionCatalog = discordConnectionCatalog;
        _routingRouteManager = routingRouteManager;
        _routesRuntimeStateProvider = routesRuntimeStateProvider;
        _retryRoutesRuntimeAsync = retryRoutesRuntimeAsync;
        _routingLocalOnlyMode = localOnlyMode;
        _routingPresentationProvider = routingPresentationProvider;
        _silhouetteSettingsDirectory = Path.GetFullPath(
            silhouetteSettingsDirectory ?? SettingsStore.DataDirectory);
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
        _modeToggleHotkeyAction.Name = "LegacyModeHotkeyActionButton";
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
        _browseButton.Name = "BrowseClipsFolderButton";
        _testButton.Click += TestClicked;
        _testButton.Name = "TestWebhookButton";
        _testButton.AccessibleName = "Test legacy Discord webhook";
        _manageRoutingConnectionsButton.Name = "ManageRoutingConnectionsButton";
        _manageRoutingConnectionsButton.AccessibleName = "Manage Discord connections in Routes";
        _manageRoutingConnectionsButton.Click += (_, _) => ShowPage(SettingsPage.Routes);
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
        pageIdentity.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        pageIdentity.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
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
            showPageHeader: false,
            routingPresentationProvider: _routingPresentationProvider);
        _homePage.NavigateToActivityRequested += (_, _) => ShowPage(SettingsPage.Activity);
        _homePage.OpenClipsFolderRequested += (_, _) => OpenHomeFolder(_folderText.Text);
        _homePage.OpenUploadedFolderRequested += (_, _) =>
            OpenHomeFolder(UploadedFolder.FindExistingUploaded(_folderText.Text));
        _homePage.OpenLocalOnlyFolderRequested += (_, _) =>
            OpenHomeFolder(UploadedFolder.FindExistingLocalOnly(_folderText.Text));
        _homePage.OpenLogsRequested += (_, _) => OpenHomeLogs();
        _homePage.CheckUpdatesRequested += CheckUpdatesClicked;
        _homePage.RoutingActionRequested += HomeRoutingActionRequested;
        var legacySettingsControls = UsesLegacySettingsControls();
        _lastLegacySettingsControlsAvailable = legacySettingsControls;
        _settingsScrollHost = new BrandedScrollHost
        {
            Name = "SettingsScrollHost",
            AccessibleName = "ClipCord settings",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell,
            Content = BuildSettingsCards(legacySettingsControls)
        };
        _settingsPage = _settingsScrollHost;
        _activityPage = new ActivityView(
            _activityHistory,
            _folderText.Text,
            allowLocalOnlyEditing: _manualClipEditService is not null);
        _activityPage.SetEmbeddedHeaderVisible(false);
        _activityPage.EditClipRequested += ActivityEditClipRequested;
        _capturePage = new CaptureView(
            _appliedSettings,
            _initialCaptureSettings,
            _captureEngineAvailable,
            _saveCaptureSettings,
            _manualCaptureRecorder,
            _captureLibraryAccessAllowed,
            _repairCaptureLibraryRoot,
            modeHotkeyProvider: GetAuthoritativeModeHotkey);
        var inputSourceCatalog = new RoutingInputSourceCatalog();
        var routeManager = _routingRouteManager ?? new RoutingRouteManager(
            connectionMembership: _discordConnectionCatalog,
            inputSourceMembership: inputSourceCatalog);
        _routesPage = new RoutesView(
            routeManager,
            connections: _discordConnectionCatalog is null
                ? new LegacyDiscordConnectionViewSource(() => _appliedSettings)
                : new DiscordConnectionCatalogViewSource(_discordConnectionCatalog),
            runtimeStateProvider: _routesRuntimeStateProvider,
            retryRuntimeAsync: _retryRoutesRuntimeAsync,
            inputSources: new RoutingInputSourceCatalogViewSource(
                inputSourceCatalog,
                new WindowsXboxDvrMetadataFileSystem(),
                libraryRoot: _initialCaptureSettings.LibraryRoot,
                legacyWatchedRoot: _appliedSettings.ClipsFolder),
            localOnlyMode: _routingLocalOnlyMode,
            migratedInputSource: new RoutingMigratedInputSourceDisplay(
                $"{AppSettings.DescribeCaptureSource(_appliedSettings.CaptureSource)} · migrated source",
                $"Existing 1.x folder · {Path.GetFileName(Path.TrimEndingDirectorySeparator(_appliedSettings.ClipsFolder))} · locked to the committed Routing migration",
                AppSettings.NormalizeCaptureSource(_appliedSettings.CaptureSource)));
        _routesPage.OpenSettingsRequested += (_, _) => ShowPage(SettingsPage.Settings);
        _routesPage.DeliveryHistoryRequested += (_, _) => ShowRoutingDeliveryHistory();
        _routesPage.LocalOnlyModeChanged += (_, _) => RefreshRoutingPresentation();
        _silhouetteLayoutsPage = new SilhouetteLayoutEditorView(
            _silhouetteSettingsDirectory,
            mirrorCameraDefault: true);
        _silhouetteLayoutsPage.SetEmbeddedHeaderVisible(false);
        _silhouetteLayoutsPage.BackRequested += (_, _) => ShowPage(SettingsPage.Capture);
        _capturePage.EditSilhouetteLayoutsRequested += (_, _) =>
            ShowPage(SettingsPage.SilhouetteLayouts);
        _galleryPage = new GalleryView(
            _folderText.Text,
            _manualClipEditService,
            _launchMediaFile,
            _playbackPreparer,
            _thumbnailProvider,
            _favorites,
            _initialCaptureSettings.LibraryRoot,
            _appliedSettings.CaptureSource,
            _captureLibraryAccessAllowed,
            additionalExternalRootsProvider: () =>
            {
                var snapshot = inputSourceCatalog.Inspect();
                if (!snapshot.IsUsable) return [];
                return snapshot.Sources
                    .Where(IsTrustedNamedGallerySource)
                    .Select(source => new GalleryExternalSourceRoot(
                        source.CanonicalRoot,
                        source.Kind == RoutingInputSourceKind.Nvidia
                            ? GalleryClipSource.Nvidia
                            : GalleryClipSource.SteelSeriesGg))
                    .ToArray();
            },
            captureRoutingHistory: new RoutingDeliveryHistoryReader(
                new RoutingOutboxStore()).Read);
        _capturePage.SettingsChanged += settings => _galleryPage.SetCaptureLibraryRoot(settings.LibraryRoot);
        _galleryPage.SetEmbeddedHeaderVisible(false);
        _galleryPage.HeaderChanged += (title, subtitle) =>
        {
            if (_currentPage != SettingsPage.Gallery || IsDisposed || Disposing) return;
            _pageTitleLabel.Text = title;
            _pageSubtitleLabel.Text = subtitle;
        };
        _galleryPage.OperationBusyChanged += GalleryOperationBusyChanged;
        _galleryPage.RenditionRetryRequested += GalleryRenditionRetryRequestedFromView;
        _aboutPage = new AboutView(
            _appliedSettings,
            _watcherStatusProvider,
            routingPresentationProvider: _routingPresentationProvider);
        _aboutPage.CheckUpdatesRequested += CheckUpdatesClicked;
        _aboutPage.SetBusy(false, _checkForUpdatesAsync is not null);
        _homePage.SetUpdateBusy(false, _checkForUpdatesAsync is not null);
        pageHost.Controls.Add(_homePage);
        pageHost.Controls.Add(_settingsPage);
        pageHost.Controls.Add(_activityPage);
        pageHost.Controls.Add(_capturePage);
        pageHost.Controls.Add(_routesPage);
        pageHost.Controls.Add(_silhouetteLayoutsPage);
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

    private void ShowRoutingDeliveryHistory()
    {
        using var dialog = new RoutingDeliveryHistoryDialog();
        dialog.ShowDialog(this);
        _routesPage?.ActivateView();
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
            RowCount = 7,
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
        _captureNavigationItem = CreateNavigationItem("Capture", BrandGlyph.Capture, selected: false);
        ConfigureNavigationItem(
            _captureNavigationItem,
            "CaptureNavItem",
            "Capture",
            "Configure ClipCord's optional Instant Replay recorder",
            SettingsPage.Capture);
        _routesNavigationItem = CreateNavigationItem("Routes", BrandGlyph.Routes, selected: false);
        ConfigureNavigationItem(
            _routesNavigationItem,
            "RoutesNavItem",
            "Routes",
            "Choose how new clips are prepared, delivered, and filed",
            SettingsPage.Routes);
        _galleryNavigationItem = CreateNavigationItem("Gallery", BrandGlyph.Gallery, selected: false);
        ConfigureNavigationItem(_galleryNavigationItem, "GalleryNavItem", "Gallery", "Browse uploaded and local-only clips", SettingsPage.Gallery);
        _aboutNavigationItem = CreateNavigationItem("About", BrandGlyph.About, selected: false);
        ConfigureNavigationItem(_aboutNavigationItem, "AboutNavItem", "About ClipCord", "Privacy, diagnostics, and project credits", SettingsPage.About);
        navigation.Controls.Add(_homeNavigationItem, 0, 0);
        navigation.Controls.Add(_settingsNavigationItem, 0, 1);
        navigation.Controls.Add(_activityNavigationItem, 0, 2);
        navigation.Controls.Add(_captureNavigationItem, 0, 3);
        navigation.Controls.Add(_routesNavigationItem, 0, 4);
        navigation.Controls.Add(_galleryNavigationItem, 0, 5);
        navigation.Controls.Add(_aboutNavigationItem, 0, 6);

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
        _railRoutingTitleLabel = new Label
        {
            Name = "RailRoutingTitleLabel",
            Text = "NEW CLIPS GO TO",
            AutoSize = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.25f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 5)
        };
        layout.Controls.Add(_railRoutingTitleLabel, 0, 0);

        _railRouteSelector = new BufferedTableLayoutPanel
        {
            Name = "RailRouteSelector",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(2),
            BackColor = ClipCordTheme.SurfaceBase
        };
        _railRouteSelector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _railRouteSelector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _railRouteSelector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _railDiscordRouteButton = CreateRailRouteButton("● Discord", "Route new clips to Discord");
        _railLocalRouteButton = CreateRailRouteButton("● Local", "Keep new clips local only");
        _railDiscordRouteButton.Name = "RailDiscordRouteButton";
        _railLocalRouteButton.Name = "RailLocalRouteButton";
        _railDiscordRouteButton.Click += (_, _) => StageRailRoute(uploadToDiscord: true);
        _railLocalRouteButton.Click += (_, _) => StageRailRoute(uploadToDiscord: false);
        _railRouteSelector.Controls.Add(_railDiscordRouteButton, 0, 0);
        _railRouteSelector.Controls.Add(_railLocalRouteButton, 1, 0);
        layout.Controls.Add(_railRouteSelector, 0, 1);
        _railDestinationSummary = new RoutingRailSummaryControl
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Visible = false
        };
        layout.Controls.Add(_railDestinationSummary, 0, 1);

        _railHotkeyHint = new Label
        {
            Name = "RailHotkeyHint",
            Text = $"{AppSettings.NormalizeModeToggleHotkey(_modeToggleHotkeyText.Text)}  to swap",
            AutoSize = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = new Padding(0, 6, 0, 5)
        };
        layout.Controls.Add(_railHotkeyHint, 0, 2);
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
        _railWatcherStatusDot = new HomeRouteDot
        {
            Name = "RailWatcherStatusDot",
            Dock = DockStyle.Fill,
            Accent = Color.FromArgb(49, 196, 130),
            Margin = Padding.Empty,
            AccessibleName = string.Empty
        };
        watcherHeadline.Controls.Add(_railWatcherStatusDot, 0, 0);
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
        if (!UsesLegacyRailPresentation())
        {
            ShowPage(SettingsPage.Routes);
            return;
        }
        if (_busy || _galleryBusy || _uploadToDiscord.Checked == uploadToDiscord) return;
        _uploadToDiscord.Checked = uploadToDiscord;
        // Routing a watcher is a durable lifecycle change. Bring the user to the
        // staged Settings draft rather than silently restarting the controller from
        // a decorative shell control.
        ShowPage(SettingsPage.Settings);
    }

    internal void ApplyExternalCaptureSettings(CaptureSettings settings) =>
        _capturePage?.ApplyExternalSettings(settings);

    internal void ShowPage(SettingsPage page)
    {
        if (_settingsPage is null || _activityPage is null || _capturePage is null ||
            _routesPage is null ||
            _silhouetteLayoutsPage is null || _galleryPage is null || _aboutPage is null)
        {
            return;
        }
        if (_galleryBusy && page != SettingsPage.Gallery) return;

        _currentPage = page;
        var showHome = page == SettingsPage.Home;
        var showSettings = page == SettingsPage.Settings;
        var showActivity = page == SettingsPage.Activity;
        var showCapture = page == SettingsPage.Capture;
        var showRoutes = page == SettingsPage.Routes;
        var showSilhouetteLayouts = page == SettingsPage.SilhouetteLayouts;
        var showGallery = page == SettingsPage.Gallery;
        var showAbout = page == SettingsPage.About;
        if (_homePage is not null) _homePage.Visible = showHome;
        _settingsPage.Visible = showSettings;
        _activityPage.Visible = showActivity;
        _capturePage.Visible = showCapture;
        _routesPage.Visible = showRoutes;
        _silhouetteLayoutsPage.Visible = showSilhouetteLayouts;
        _galleryPage.Visible = showGallery;
        _aboutPage.Visible = showAbout;
        if (showHome)
        {
            _activityHistory.RefreshExternalEntries();
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
            _activityHistory.RefreshExternalEntries();
            _homePage?.DeactivateView();
            _galleryPage.Deactivate();
            _activityPage.BringToFront();
            _activityPage.RefreshViewport();
        }
        else if (showCapture)
        {
            _homePage?.DeactivateView();
            _galleryPage.Deactivate();
            _capturePage.BringToFront();
            _capturePage.RefreshViewport();
        }
        else if (showRoutes)
        {
            _homePage?.DeactivateView();
            _galleryPage.Deactivate();
            _routesPage.BringToFront();
            _routesPage.ActivateView();
        }
        else if (showSilhouetteLayouts)
        {
            _homePage?.DeactivateView();
            _galleryPage.Deactivate();
            _silhouetteLayoutsPage.BringToFront();
            _silhouetteLayoutsPage.RefreshViewport();
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
        UpdateNavigationSelection(_captureNavigationItem, showCapture || showSilhouetteLayouts);
        UpdateNavigationSelection(_routesNavigationItem, showRoutes);
        UpdateNavigationSelection(_galleryNavigationItem, showGallery);
        UpdateNavigationSelection(_aboutNavigationItem, showAbout);
        UpdatePageHeaderAction(page);
        UpdateSaveBarVisibility();
        Text = page switch
        {
            SettingsPage.Home => "ClipCord — Home",
            SettingsPage.Activity => "ClipCord — Activity",
            SettingsPage.Capture => "ClipCord — Capture",
            SettingsPage.Routes => "ClipCord — Routes",
            SettingsPage.SilhouetteLayouts => "ClipCord — Silhouette layouts",
            SettingsPage.Gallery => "ClipCord — Gallery",
            SettingsPage.About => "ClipCord — About",
            _ => "ClipCord — Settings"
        };
        (_pageTitleLabel.Text, _pageSubtitleLabel.Text) = page switch
        {
            SettingsPage.Home => ("Home", "Everything ClipCord is doing right now"),
            SettingsPage.Settings => UsesLegacySettingsControls()
                ? ("Settings", "Where clips come from, and where they go")
                : ("Settings", "App preferences · clip sources, connections and delivery live in Routes"),
            SettingsPage.Activity => ("Activity", "Recent clip activity stored on this PC"),
            SettingsPage.Capture => ("Capture", "Save the last minutes of gameplay locally, encoded on your GPU"),
            SettingsPage.Routes => ("Routes", "Decide what happens to every new clip"),
            SettingsPage.SilhouetteLayouts => (
                "Silhouette layouts",
                "Reusable defaults · every capture with the reaction camera on uses these layouts"),
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
        var captureAction = _capturePage?.HeaderStatusPill;
        var routesAction = _routesPage?.HeaderActions;
        var silhouetteBackAction = _silhouetteLayoutsPage?.HeaderBackButton;
        var useSharedGalleryHeader = page == SettingsPage.Gallery &&
                                     ClientSize.Width >= ScaleLogical(1050);
        var action = page switch
        {
            SettingsPage.Home => homeAction,
            SettingsPage.About => aboutAction,
            SettingsPage.Capture => captureAction,
            SettingsPage.Routes => routesAction,
            SettingsPage.SilhouetteLayouts => silhouetteBackAction,
            SettingsPage.Gallery when useSharedGalleryHeader => galleryAction,
            _ => null
        };
        foreach (var candidate in new[]
                 {
                     homeAction,
                     aboutAction,
                     galleryAction,
                     captureAction,
                     routesAction,
                     silhouetteBackAction
                 })
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
        if (ReferenceEquals(action, galleryAction) || ReferenceEquals(action, routesAction))
        {
            action.AutoSize = true;
        }
        else
        {
            action.AutoSize = false;
            var logicalWidth = page switch
            {
                SettingsPage.Home => 158,
                SettingsPage.Capture => 190,
                SettingsPage.Routes => 136,
                SettingsPage.SilhouetteLayouts => 152,
                _ => 164
            };
            var logicalHeight = page == SettingsPage.Capture ? 25 : 34;
            action.Size = new Size(ScaleLogical(logicalWidth), ScaleLogical(logicalHeight));
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

    private Control BuildSettingsCards(bool legacySettingsControls)
    {
        _managedSettingsNavigationRows.Clear();
        return legacySettingsControls
            ? BuildLegacySettingsCards()
            : BuildRoutingOwnedSettingsCards();
    }

    private Control BuildLegacySettingsCards()
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

    private Control BuildRoutingOwnedSettingsCards()
    {
        var cards = new BufferedTableLayoutPanel
        {
            Name = "SettingsCards",
            Dock = DockStyle.Fill,
            // BrandedScrollHost owns this surface's width. Let GetPreferredSize
            // report the vertical extent without allowing TableLayoutPanel's
            // AutoSize pass to grow the cards back past a constrained viewport.
            AutoSize = false,
            AutoScroll = false,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = ScalePadding(new Padding(28, 4, 28, 20)),
            BackColor = ClipCordTheme.Shell
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < cards.RowCount; row++)
            cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        cards.Controls.Add(CreateRoutingSettingsSection(
            "IDENTITY & DELIVERY DEFAULTS",
            BuildIdentityDeliveryDefaultsCard(),
            new Padding(0, 0, 0, 14)), 0, 0);
        cards.Controls.Add(CreateRoutingSettingsSection(
            "APPLICATION",
            BuildGeneralApplicationCard(),
            new Padding(0, 0, 0, 14)), 0, 1);
        cards.Controls.Add(CreateRoutingSettingsSection(
            "MANAGED IN ROUTES & CAPTURE",
            BuildManagedSettingsCard(),
            Padding.Empty), 0, 2);
        cards.Controls.Add(new Label
        {
            Name = "ManagedSettingsFooterLabel",
            Text = "Clip sources, connections and delivery rules moved to Routes in ClipCord 2.0. " +
                   "Settings keeps only preferences that apply to the whole app.",
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(ScaleLogical(760), 0),
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = ScalePadding(new Padding(2, 10, 2, 0)),
            UseMnemonic = false
        }, 0, 3);
        return cards;
    }

    private Control CreateRoutingSettingsSection(string title, Control card, Padding margin)
    {
        var section = new BufferedTableLayoutPanel
        {
            Name = title.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("&", "And", StringComparison.Ordinal) + "Section",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = ScalePadding(margin),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(24)));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextTertiary,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ClipCordTheme.InterfaceFont(7.75f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 0, 0);
        card.Margin = Padding.Empty;
        section.Controls.Add(card, 0, 1);
        return section;
    }

    private Control BuildIdentityDeliveryDefaultsCard()
    {
        var layout = CreateRoutingSettingsCardContent(3);
        layout.ColumnCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(50)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(1)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(56)));

        layout.Controls.Add(CreateRoutingPreferenceCopy(
            "Uploader name",
            "Shown beside every clip ClipCord posts."), 0, 0);
        var uploaderHost = CreateFieldHost(_uploaderNameText);
        uploaderHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        uploaderHost.Margin = ScalePadding(new Padding(0, 7, 0, 7));
        layout.Controls.Add(uploaderHost, 1, 0);

        var divider = CreateRoutingSettingsDivider();
        layout.Controls.Add(divider, 0, 1);
        layout.SetColumnSpan(divider, 2);

        layout.Controls.Add(CreateRoutingPreferenceCopy(
            "Discord compression target",
            "The size ClipCord compresses toward before sending to Discord. " +
            "95 MB is the current Discord default."), 0, 2);
        var compressionHost = CreateCompressionHost();
        compressionHost.Anchor = AnchorStyles.Right;
        compressionHost.Margin = ScalePadding(new Padding(0, 9, 0, 9));
        layout.Controls.Add(compressionHost, 1, 2);

        return CreateRoutingSettingsCard(
            "IdentityDeliveryDefaultsCard",
            "Identity and delivery defaults",
            layout,
            132);
    }

    private Control BuildGeneralApplicationCard()
    {
        var layout = CreateRoutingSettingsCardContent(3);
        layout.ColumnCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(48)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(1)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(50)));

        layout.Controls.Add(CreateRoutingPreferenceCopy(
            "Start with Windows",
            "ClipCord opens minimised to the tray."), 0, 0);
        _startWithWindows.Text = string.Empty;
        _startWithWindows.Name = "StartWithWindowsToggle";
        _startWithWindows.AccessibleName = "Start with Windows";
        _startWithWindows.Anchor = AnchorStyles.Right;
        _startWithWindows.Margin = Padding.Empty;
        layout.Controls.Add(_startWithWindows, 1, 0);

        var divider = CreateRoutingSettingsDivider();
        layout.Controls.Add(divider, 0, 1);
        layout.SetColumnSpan(divider, 2);

        layout.Controls.Add(CreateRoutingPreferenceCopy(
            "Updates",
            "Stable release channel."), 0, 2);
        _checkUpdatesButton.Anchor = AnchorStyles.Right;
        _checkUpdatesButton.Margin = Padding.Empty;
        layout.Controls.Add(_checkUpdatesButton, 1, 2);

        return CreateRoutingSettingsCard(
            "GeneralApplicationCard",
            "Application preferences",
            layout,
            124);
    }

    private Control BuildManagedSettingsCard()
    {
        var layout = CreateRoutingSettingsCardContent(3);
        layout.ColumnCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(CreateManagedSettingsNavigationRow(
            "ManagedClipSourcesRow",
            FigmaIconAsset.Folder,
            "Clip sources",
            "Recorders and folders ClipCord watches",
            "Routes › Connections",
            () => OpenRoutesSettingsSection(showConnections: true),
            new Padding(0, 0, 6, 8)), 0, 0);
        layout.Controls.Add(CreateManagedSettingsNavigationRow(
            "ManagedConnectionsRow",
            FigmaIconAsset.Connection,
            "Connections",
            "Discord, YouTube and TikTok accounts",
            "Routes › Connections",
            () => OpenRoutesSettingsSection(showConnections: true),
            new Padding(6, 0, 0, 8)), 1, 0);
        layout.Controls.Add(CreateManagedSettingsNavigationRow(
            "ManagedRecordingCameraRow",
            FigmaIconAsset.Capture,
            "Recording and camera",
            "Resolution, encoder, reaction camera",
            "Capture",
            () => ShowPage(SettingsPage.Capture),
            new Padding(0, 0, 6, 8)), 0, 1);
        layout.Controls.Add(CreateManagedSettingsNavigationRow(
            "ManagedDeliveryRulesRow",
            FigmaIconAsset.Routes,
            "Delivery rules",
            "What happens to each new clip",
            "Routes",
            () => OpenRoutesSettingsSection(showConnections: false),
            new Padding(6, 0, 0, 8)), 1, 1);
        var localOnly = CreateManagedSettingsNavigationRow(
            "ManagedLocalOnlyModeRow",
            FigmaIconAsset.Shield,
            "Local-only mode",
            "Pause external delivery for future clips",
            "Routes",
            () => OpenRoutesSettingsSection(showConnections: false),
            Padding.Empty);
        layout.Controls.Add(localOnly, 0, 2);
        layout.SetColumnSpan(localOnly, 2);

        return CreateRoutingSettingsCard(
            "ManagedSettingsCard",
            "Preferences managed in Routes and Capture",
            layout,
            190);
    }

    private RoundedPanel CreateManagedSettingsNavigationRow(
        string name,
        FigmaIconAsset asset,
        string title,
        string subtitle,
        string destination,
        Action action,
        Padding margin)
    {
        var row = new RoundedPanel
        {
            Name = name,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, ScaleLogical(54)),
            BackColor = ClipCordTheme.SettingsField,
            BorderColor = ClipCordTheme.SettingsCardBorder,
            CornerRadius = ScaleLogical(10),
            Padding = ScalePadding(new Padding(12, 7, 10, 7)),
            Margin = ScalePadding(margin),
            AccessibleName = title,
            AccessibleDescription = $"{subtitle}. Opens {destination}.",
            AccessibleRole = AccessibleRole.Link
        };
        row.EnableKeyboardAccess(action);

        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsField
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(32)));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(22)));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new FigmaIconControl
        {
            Asset = asset,
            IconColor = ClipCordTheme.Violet,
            Dock = DockStyle.Fill,
            Padding = ScalePadding(new Padding(5)),
            Margin = ScalePadding(new Padding(0, 2, 6, 2))
        }, 0, 0);
        content.Controls.Add(CreateManagedSettingsRowCopy(title, subtitle), 1, 0);
        content.Controls.Add(new Label
        {
            Text = destination,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = ScalePadding(new Padding(8, 0, 4, 0)),
            UseMnemonic = false,
            TabStop = false
        }, 2, 0);
        content.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.ChevronRight,
            IconColor = ClipCordTheme.TextTertiary,
            Dock = DockStyle.Fill,
            Padding = ScalePadding(new Padding(6)),
            Margin = Padding.Empty
        }, 3, 0);
        row.Controls.Add(content);
        WireClick(row, action);
        _managedSettingsNavigationRows.Add(row);
        return row;
    }

    private static Control CreateManagedSettingsRowCopy(string title, string subtitle)
    {
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SettingsField,
            TabStop = false
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9.5f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false,
            TabStop = false
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty,
            UseMnemonic = false,
            TabStop = false
        }, 0, 1);
        return copy;
    }

    private static BufferedTableLayoutPanel CreateRoutingSettingsCardContent(int rows)
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

    private Control CreateRoutingPreferenceCopy(string title, string subtitle)
    {
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, ScaleLogical(18), 0),
            BackColor = ClipCordTheme.SettingsCard
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9.75f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(ScaleLogical(430), 0),
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = new Padding(0, ScaleLogical(2), 0, 0),
            UseMnemonic = false
        }, 0, 1);
        return copy;
    }

    private RoundedPanel CreateRoutingSettingsCard(
        string name,
        string accessibleName,
        Control content,
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
            CornerRadius = ScaleLogical(16),
            Padding = ScalePadding(new Padding(18, 10, 18, 10)),
            Margin = Padding.Empty,
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.Grouping
        };
        content.Margin = Padding.Empty;
        card.Controls.Add(content);
        return card;
    }

    private static Panel CreateRoutingSettingsDivider() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = ClipCordTheme.BorderDefault,
        Margin = Padding.Empty,
        TabStop = false
    };

    private void OpenRoutesSettingsSection(bool showConnections)
    {
        ShowPage(SettingsPage.Routes);
        if (_routesPage is null) return;
        var targetName = showConnections ? "ConnectionsRouteTab" : "RoutesRouteTab";
        var tab = EnumerateControls(_routesPage)
            .OfType<OutlineButton>()
            .FirstOrDefault(candidate => candidate.Name == targetName);
        tab?.PerformClick();
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
        _clipsFolderLabelBlock = CreateFieldLabelBlock(
            "Clips folder",
            "Any folder that receives finished MP4 clips.");
        _clipsFolderLabelBlock.Name = "ClipsFolderLabelBlock";
        layout.Controls.Add(_clipsFolderLabelBlock, 0, 0);
        layout.Controls.Add(CreateFieldRow(CreateFieldHost(_folderText), _browseButton), 1, 0);
        _captureSourceLabelBlock = CreateFieldLabelBlock(
            "Recorded with",
            "Tells ClipCord how your recorder files clips.");
        _captureSourceLabelBlock.Name = "CaptureSourceLabelBlock";
        layout.Controls.Add(_captureSourceLabelBlock, 0, 2);

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
        if (!UsesLegacySettingsControls()) return;
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
        _captureSourceHelper.Text = UsesLegacySettingsControls()
            ? _captureSource == ClipCaptureSource.Nvidia
                ? @"New MP4 clips inside this folder's <game> subfolders are detected automatically."
                : "New MP4 clips in this folder are detected automatically."
            : "Routing owns the active watched source. Open Routes to manage what happens to its clips.";
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
        _webhookLabelBlock = CreateFieldLabelBlock(
            "Webhook URL",
            "Encrypted with Windows DPAPI for this account only.");
        _webhookLabelBlock.Name = "WebhookLabelBlock";
        layout.Controls.Add(_webhookLabelBlock, 0, 2);
        var webhookRow = (BufferedTableLayoutPanel)CreateFieldRow(
            CreateFieldHost(_webhookText),
            _testButton);
        _manageRoutingConnectionsButton.Dock = DockStyle.Fill;
        _manageRoutingConnectionsButton.Margin = Padding.Empty;
        _manageRoutingConnectionsButton.Visible = false;
        _manageRoutingConnectionsButton.TabStop = false;
        webhookRow.Controls.Add(_manageRoutingConnectionsButton, 1, 0);
        layout.Controls.Add(webhookRow, 1, 2);
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
        _modeHotkeyLabelBlock = CreateFieldLabelBlock(
            "Mode shortcut",
            "Swaps the route for future clips.");
        _modeHotkeyLabelBlock.Name = "ModeHotkeyLabelBlock";
        shortcut.Controls.Add(_modeHotkeyLabelBlock, 0, 0);
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
        UpdateRailRoutingPresentation();
        UpdateRoutingOwnedSettingsPresentation();
        if (_routesPage is { Visible: true }) _routesPage.RefreshRuntimeStatus();
        var fullStatus = _watcherStatusProvider?.Invoke() ?? "Settings";
        if (_aboutPage is { Visible: true }) _aboutPage.UpdateWatcherStatus(fullStatus);
        if (!UsesLegacyRailPresentation())
        {
            _lastWatcherFullStatus = fullStatus;
            return;
        }
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
        if (!UsesLegacySettingsControls())
        {
            ShowPage(SettingsPage.Routes);
            return;
        }
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where your clipping tool saves MP4 clips",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderText.Text) ? _folderText.Text : string.Empty,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var candidate = Path.GetFullPath(dialog.SelectedPath);
        var captureLibrary = _capturePage?.CurrentSettings.LibraryRoot ??
            _initialCaptureSettings.LibraryRoot;
        if (!CanUseWatchedFolder(candidate, captureLibrary))
        {
            MessageBox.Show(
                this,
                "Choose a watched folder outside the ClipCord Capture library. Keeping them separate prevents ClipCord from importing its own recordings.",
                "Folders must stay separate",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        _folderText.Text = candidate;
    }

    private async void TestClicked(object? sender, EventArgs eventArgs)
    {
        if (!UsesLegacySettingsControls())
        {
            ShowPage(SettingsPage.Routes);
            return;
        }
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
        if (!UsesLegacySettingsControls())
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            return;
        }
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
        if (!UsesLegacySettingsControls())
        {
            ShowPage(SettingsPage.Routes);
            return;
        }
        _modeToggleHotkeyText.Text = string.IsNullOrWhiteSpace(_modeToggleHotkeyText.Text)
            ? GlobalHotkeyBinding.DefaultDisplayText
            : string.Empty;
        UpdateModeToggleHotkeyEditor();
    }

    private void UpdateModeToggleHotkeyEditor()
    {
        if (!UsesLegacySettingsControls())
        {
            SetFieldLabelBlockText(
                _modeHotkeyLabelBlock,
                "Legacy mode shortcut",
                "Unavailable while Routes owns delivery.");
            const string managedGuidance =
                "Destinations are managed in Routes; the legacy Discord/Local shortcut is unavailable.";
            _modeToggleHotkeyText.AccessibleDescription = managedGuidance;
            _toolTip.SetToolTip(_modeToggleHotkeyText, managedGuidance);
            _toolTip.SetToolTip(_modeToggleHotkeyAction, "Open Routes to manage delivery destinations.");
            RecomputeSettingsDirty();
            return;
        }

        SetFieldLabelBlockText(
            _modeHotkeyLabelBlock,
            "Mode shortcut",
            "Swaps the route for future clips.");
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
        if (_railHotkeyHint is not null && UsesLegacyRailPresentation())
        {
            var shortcut = disabled ? "Shortcut off" : AppSettings.NormalizeModeToggleHotkey(_modeToggleHotkeyText.Text);
            _railHotkeyHint.Text = $"{shortcut}  to swap";
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

        var legacySettingsControls = UsesLegacySettingsControls();
        var modeToggleHotkey = legacySettingsControls
            ? _modeToggleHotkeyText.Text.Trim()
            : AppSettings.NormalizeModeToggleHotkey(_appliedSettings.ModeToggleHotkey);
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
            legacySettingsControls ? _folderText.Text.Trim() : _appliedSettings.ClipsFolder,
            legacySettingsControls ? _webhookText.Text.Trim() : _appliedSettings.WebhookUrl,
            _startWithWindows.Checked,
            compressionTargetMb,
            AppSettings.NormalizeUploaderName(_uploaderNameText.Text),
            legacySettingsControls ? _uploadToDiscord.Checked : _appliedSettings.UploadToDiscord,
            modeToggleHotkey,
            legacySettingsControls
                ? _captureSource
                : AppSettings.NormalizeCaptureSource(_appliedSettings.CaptureSource));

        if (settings.UploadToDiscord && string.IsNullOrWhiteSpace(_uploaderNameText.Text))
        {
            MessageBox.Show(this, "Enter the name Discord should show with uploaded clips.", "Invalid uploader name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!Directory.Exists(settings.ClipsFolder))
        {
            MessageBox.Show(this, "Choose an existing clips folder.", "Invalid folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var captureLibrary = _capturePage?.CurrentSettings.LibraryRoot ??
            _initialCaptureSettings.LibraryRoot;
        if (!CanUseWatchedFolder(settings.ClipsFolder, captureLibrary))
        {
            MessageBox.Show(
                this,
                "Choose a watched folder outside the ClipCord Capture library. Keeping them separate prevents ClipCord from importing its own recordings.",
                "Folders must stay separate",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (settings.UploadToDiscord && !WebhookValidation.IsDiscordWebhook(settings.WebhookUrl))
        {
            MessageBox.Show(this, "Enter a valid HTTPS Discord webhook URL.", "Invalid webhook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    internal static bool CanUseWatchedFolder(string watchedFolder, string captureLibraryRoot) =>
        !CapturePathPolicy.PathsOverlap(watchedFolder, captureLibraryRoot);

    private void UpdateUploadModeText()
    {
        if (!UsesLegacySettingsControls())
        {
            _uploadModeHelper.Text =
                "Delivery destinations are managed in Routes. Compression remains available here.";
            _toolTip.SetToolTip(
                _uploadToDiscord,
                "Open Routes to manage Discord and Library delivery actions.");
            UpdateRailRouteSelection();
            RecomputeSettingsDirty();
            return;
        }

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
        var legacySettingsControls = UsesLegacySettingsControls();
        if (legacySettingsControls &&
            !string.Equals(_folderText.Text.Trim(), _appliedSettings.ClipsFolder, StringComparison.Ordinal)) changed++;
        if (legacySettingsControls &&
            !string.Equals(_webhookText.Text.Trim(), _appliedSettings.WebhookUrl, StringComparison.Ordinal)) changed++;
        if (!string.Equals(
                AppSettings.NormalizeUploaderName(_uploaderNameText.Text),
                AppSettings.NormalizeUploaderName(_appliedSettings.UploaderName),
                StringComparison.Ordinal)) changed++;
        if (!TryParseCompressionTarget(_compressionTarget.Text, out var target) ||
            target != Math.Clamp(_appliedSettings.CompressionTargetMb, 1, 100)) changed++;
        if (legacySettingsControls && !string.Equals(
                AppSettings.NormalizeModeToggleHotkey(_modeToggleHotkeyText.Text),
                AppSettings.NormalizeModeToggleHotkey(_appliedSettings.ModeToggleHotkey),
                StringComparison.Ordinal)) changed++;
        if (_startWithWindows.Checked != _appliedSettings.StartWithWindows) changed++;
        if (legacySettingsControls && _uploadToDiscord.Checked != _appliedSettings.UploadToDiscord) changed++;
        if (legacySettingsControls &&
            _captureSource != AppSettings.NormalizeCaptureSource(_appliedSettings.CaptureSource)) changed++;
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
        if (!UsesLegacyRailPresentation())
        {
            ApplyRailRouteButtonState(_railDiscordRouteButton, selected: false);
            ApplyRailRouteButtonState(_railLocalRouteButton, selected: false);
            return;
        }
        ApplyRailRouteButtonState(_railDiscordRouteButton, _uploadToDiscord.Checked);
        ApplyRailRouteButtonState(_railLocalRouteButton, !_uploadToDiscord.Checked);
    }

    internal void RefreshRoutingPresentation()
    {
        if (IsDisposed || Disposing) return;
        UpdateRailRoutingPresentation();
        UpdateRoutingOwnedSettingsPresentation();
        _homePage?.RefreshRuntimeStatus();
        if (_routesPage is { Visible: true }) _routesPage.RefreshRuntimeStatus();
        if (_aboutPage is { Visible: true }) _aboutPage.RefreshStatus();
    }

    private async void HomeRoutingActionRequested(
        object? sender,
        HomeRoutingActionRequestedEventArgs eventArgs)
    {
        await HandleHomeRoutingActionAsync(eventArgs.Action).ConfigureAwait(true);
    }

    internal async Task HandleHomeRoutingActionAsync(HomeRoutingAction action)
    {
        if (action is HomeRoutingAction.OpenRoutes or HomeRoutingAction.CreateRoute)
        {
            ShowPage(SettingsPage.Routes);
            if (action == HomeRoutingAction.CreateRoute &&
                _routesPage is not null && !_routesPage.TryBeginCreateRoute())
            {
                Log.Error("The Home create-route action opened Routes, but route creation is not currently available.");
            }
            return;
        }
        if (_homeRoutingActionBusy) return;
        if (_routingLocalOnlyMode is null)
        {
            ShowPage(SettingsPage.Routes);
            return;
        }

        _homeRoutingActionBusy = true;
        try
        {
            var result = await _routingLocalOnlyMode.SetEnabledAsync(
                    action == HomeRoutingAction.EnableLocalOnlyMode)
                .ConfigureAwait(true);
            RefreshRoutingPresentation();
            if (!result.Succeeded)
            {
                Log.Error("The Home routing action did not change saved Local-only mode.");
                ShowPage(SettingsPage.Routes);
            }
        }
        catch (Exception exception)
        {
            Log.Error("The Home routing action could not update Local-only mode.", exception);
            RefreshRoutingPresentation();
            ShowPage(SettingsPage.Routes);
        }
        finally
        {
            _homeRoutingActionBusy = false;
        }
    }

    private bool UsesLegacyRailPresentation()
    {
        if (_routesRuntimeStateProvider is null) return true;
        try
        {
            return _routesRuntimeStateProvider() is
                RoutesRuntimeViewState.LegacySetupNeeded or
                RoutesRuntimeViewState.LegacyActive;
        }
        catch
        {
            // A status provider failure must not expose legacy delivery controls as
            // authoritative. The Routes page owns recovery and can explain the block.
            return false;
        }
    }

    private bool UsesLegacySettingsControls()
    {
        if (_routesRuntimeStateProvider is null) return true;
        try
        {
            return _routesRuntimeStateProvider() is
                RoutesRuntimeViewState.LegacySetupNeeded or
                RoutesRuntimeViewState.LegacyActive;
        }
        catch
        {
            return false;
        }
    }

    internal string GetAuthoritativeModeHotkey()
    {
        var legacyHotkey = AppSettings.NormalizeModeToggleHotkey(
            _appliedSettings.ModeToggleHotkey);
        if (UsesLegacySettingsControls()) return legacyHotkey;
        if (_routingLocalOnlyMode is null) return string.Empty;
        try
        {
            return _routingLocalOnlyMode.Inspect().HotkeyDisplayText ?? string.Empty;
        }
        catch (Exception exception)
        {
            Log.Error("The Routing-owned Local-only shortcut could not be read.", exception);
            return string.Empty;
        }
    }

    private void UpdateRoutingOwnedSettingsPresentation()
    {
        var legacy = UsesLegacySettingsControls();
        var presentationChanged = _lastLegacySettingsControlsAvailable != legacy;
        if (_lastLegacySettingsControlsAvailable == true && !legacy)
        {
            ResetRoutingProtectedDraftFields();
        }
        _lastLegacySettingsControlsAvailable = legacy;

        if (presentationChanged && _settingsScrollHost is not null)
        {
            _settingsScrollHost.Content = BuildSettingsCards(legacy);
            _settingsScrollHost.RefreshContentLayout(preservePosition: false);
        }

        var legacyControlsEnabled = legacy && !_busy && !_galleryBusy;
        _folderText.Enabled = legacyControlsEnabled;
        _folderText.TabStop = legacyControlsEnabled;
        _browseButton.Enabled = legacyControlsEnabled;
        _browseButton.TabStop = legacyControlsEnabled;
        _steelSeriesSourceButton.Enabled = legacyControlsEnabled;
        _steelSeriesSourceButton.TabStop = legacyControlsEnabled;
        _nvidiaSourceButton.Enabled = legacyControlsEnabled;
        _nvidiaSourceButton.TabStop = legacyControlsEnabled;
        _webhookText.Enabled = legacyControlsEnabled;
        _webhookText.TabStop = legacyControlsEnabled;
        _testButton.Visible = legacy;
        _testButton.Enabled = legacyControlsEnabled;
        _testButton.TabStop = legacyControlsEnabled;
        _manageRoutingConnectionsButton.Visible = false;
        _manageRoutingConnectionsButton.Enabled = false;
        _manageRoutingConnectionsButton.TabStop = false;
        _uploadToDiscord.Visible = legacy;
        _uploadToDiscord.Enabled = legacyControlsEnabled;
        _uploadToDiscord.TabStop = legacyControlsEnabled;
        _modeToggleHotkeyText.Enabled = legacyControlsEnabled;
        _modeToggleHotkeyText.TabStop = legacyControlsEnabled;
        _modeToggleHotkeyAction.Enabled = legacyControlsEnabled;
        _modeToggleHotkeyAction.TabStop = legacyControlsEnabled;
        var managedNavigationEnabled = !legacy && !_busy && !_galleryBusy;
        foreach (var row in _managedSettingsNavigationRows)
        {
            row.Enabled = managedNavigationEnabled;
            row.TabStop = managedNavigationEnabled;
        }

        SetFieldLabelBlockText(
            _clipsFolderLabelBlock,
            "Clips folder",
            legacy
                ? "Any folder that receives finished MP4 clips."
                : "Locked while Routing owns watched-source processing.");
        SetFieldLabelBlockText(
            _captureSourceLabelBlock,
            "Recorded with",
            legacy
                ? "Tells ClipCord how your recorder files clips."
                : "Routing owns this watched source until delivery is stopped.");
        SetFieldLabelBlockText(
            _webhookLabelBlock,
            legacy ? "Webhook URL" : "Discord connections",
            legacy
                ? "Encrypted with Windows DPAPI for this account only."
                : "Add, test, or remove webhooks in Routes → Connections.");

        const string routesGuidance =
            "Routing owns this setting. Open Routes to manage delivery safely.";
        _folderText.AccessibleDescription = legacy ? string.Empty : routesGuidance;
        _webhookText.AccessibleDescription = legacy
            ? string.Empty
            : "Webhook credentials are managed in Routes → Connections.";
        _uploadToDiscord.AccessibleName = legacy
            ? "Upload new clips to Discord"
            : "Delivery destinations are managed in Routes";
        _uploadToDiscord.AccessibleDescription = legacy ? string.Empty : routesGuidance;
        _manageRoutingConnectionsButton.AccessibleDescription =
            "Discord connections are available from the Connections row in Settings.";

        if (legacy) _testButton.BringToFront();

        if (!presentationChanged) return;
        if (_currentPage == SettingsPage.Settings && !_statusLabel.Visible)
        {
            _pageSubtitleLabel.Text = legacy
                ? "Where clips come from, and where they go"
                : "App preferences · clip sources, connections and delivery live in Routes";
        }
        UpdateCaptureSourceSelection();
        UpdateModeToggleHotkeyEditor();
        UpdateUploadModeText();
        RecomputeSettingsDirty();
    }

    private void ResetRoutingProtectedDraftFields()
    {
        var wasTracking = _dirtyTrackingReady;
        _dirtyTrackingReady = false;
        try
        {
            _folderText.Text = _appliedSettings.ClipsFolder;
            _webhookText.Text = _appliedSettings.WebhookUrl;
            _modeToggleHotkeyText.Text =
                AppSettings.NormalizeModeToggleHotkey(_appliedSettings.ModeToggleHotkey);
            _uploadToDiscord.Checked = _appliedSettings.UploadToDiscord;
            _captureSource = AppSettings.NormalizeCaptureSource(_appliedSettings.CaptureSource);
        }
        finally
        {
            _dirtyTrackingReady = wasTracking;
        }
    }

    private static void SetFieldLabelBlockText(
        Control? block,
        string title,
        string subtitle)
    {
        if (block is not TableLayoutPanel layout) return;
        if (layout.GetControlFromPosition(0, 0) is Label titleLabel)
            titleLabel.Text = title;
        if (layout.GetControlFromPosition(0, 1) is Label subtitleLabel)
            subtitleLabel.Text = subtitle;
    }

    private void UpdateRailRoutingPresentation()
    {
        if (_railRoutingTitleLabel is null || _railRouteSelector is null ||
            _railDiscordRouteButton is null || _railLocalRouteButton is null ||
            _railHotkeyHint is null || _railDestinationSummary is null)
        {
            return;
        }

        var legacy = UsesLegacyRailPresentation();
        if (!legacy)
        {
            var routing = CaptureRailRoutingPresentation();
            _railRoutingTitleLabel.Text = "NEW CLIPS GO TO";
            _railRouteSelector.Visible = false;
            _railDiscordRouteButton.TabStop = false;
            _railLocalRouteButton.TabStop = false;
            _railDestinationSummary.Apply(routing);
            _railDestinationSummary.Visible = true;
            _railDestinationSummary.BringToFront();
            _railHotkeyHint.Text = FormatRailRouteComposition(routing);
            ApplyManagedRailStatus(routing);
            return;
        }

        _railDestinationSummary.Visible = false;
        _railRouteSelector.Visible = true;
        _railRouteSelector.SuspendLayout();
        try
        {
            _railRoutingTitleLabel.Text = "NEW CLIPS GO TO";
            _railDiscordRouteButton.Visible = true;
            _railDiscordRouteButton.TabStop = true;
            _railDiscordRouteButton.AccessibleRole = AccessibleRole.RadioButton;

            _railRouteSelector.SetColumnSpan(_railLocalRouteButton, 1);
            _railRouteSelector.SetCellPosition(
                _railLocalRouteButton,
                new TableLayoutPanelCellPosition(1, 0));
            _railLocalRouteButton.Text = "● Local";
            _railLocalRouteButton.AccessibleName = "Keep new clips local only";
            _railLocalRouteButton.AccessibleRole = AccessibleRole.RadioButton;
            _railLocalRouteButton.TabStop = true;
            _railHotkeyHint.Text = $"{(string.IsNullOrWhiteSpace(_modeToggleHotkeyText.Text) ? "Shortcut off" : AppSettings.NormalizeModeToggleHotkey(_modeToggleHotkeyText.Text))}  to swap";
            _railLocalRouteButton.AccessibleDescription = string.Empty;
            UpdateRailRouteSelection();
        }
        finally
        {
            _railRouteSelector.ResumeLayout(performLayout: true);
        }
    }

    private RoutingUiPresentationSnapshot CaptureRailRoutingPresentation()
    {
        try
        {
            return (_routingPresentationProvider?.Invoke() ??
                    new RoutingUiPresentationSnapshot(
                        RoutingUiState.Unavailable,
                        ActiveRouteCount: 0,
                        WatchingSourceCount: 0,
                        LocalOnlyModeEnabled: false))
                .Normalize();
        }
        catch (Exception exception)
        {
            Log.Error("The privacy-safe Routing rail projection could not be read.", exception);
            return new RoutingUiPresentationSnapshot(
                RoutingUiState.Unavailable,
                ActiveRouteCount: 0,
                WatchingSourceCount: 0,
                LocalOnlyModeEnabled: true);
        }
    }

    private static string FormatRailRouteComposition(RoutingUiPresentationSnapshot routing)
    {
        var parts = new List<string>(3);
        if (routing.SpecificRouteCount > 0) parts.Add($"{routing.SpecificRouteCount:N0} specific");
        if (routing.FallbackRouteCount > 0) parts.Add($"{routing.FallbackRouteCount:N0} fallback");
        if (routing.PausedRouteCount > 0) parts.Add($"{routing.PausedRouteCount:N0} paused");
        return parts.Count > 0
            ? string.Join(" · ", parts)
            : routing.State is RoutingUiState.NeedsAttention or RoutingUiState.Unavailable
                ? "Routing status unavailable"
                : "No routes yet";
    }

    private void ApplyManagedRailStatus(RoutingUiPresentationSnapshot routing)
    {
        string headline;
        string detail;
        Color accent;
        if (routing.State is RoutingUiState.NeedsAttention or RoutingUiState.Unavailable)
        {
            headline = "Routes need attention";
            detail = "Open Routes to review";
            accent = ClipCordTheme.Coral;
        }
        else if (routing.LocalOnlyModeEnabled)
        {
            headline = "Local-only mode on";
            detail = "Future clips stay here";
            accent = Color.FromArgb(224, 151, 54);
        }
        else if (routing.State == RoutingUiState.Active)
        {
            headline = routing.ActiveRouteCount > 0 ? "Routes armed" : "No routes yet";
            detail = "Library always on";
            accent = routing.ActiveRouteCount > 0
                ? Color.FromArgb(49, 196, 130)
                : ClipCordTheme.TextTertiary;
        }
        else
        {
            headline = "Routing paused";
            detail = "Library always on";
            accent = Color.FromArgb(224, 151, 54);
        }

        _watcherStatusLabel.Text = headline;
        _watcherStatusLabel.AccessibleDescription = $"{headline}. {detail}.";
        _watcherStatusDetailLabel.Text = detail;
        if (_railWatcherStatusDot is not null) _railWatcherStatusDot.Accent = accent;
        _toolTip.SetToolTip(_watcherStatusLabel, $"{headline} · {detail}");
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
        UpdateRoutingOwnedSettingsPresentation();
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
        if (_captureNavigationItem is not null) _captureNavigationItem.Enabled = !busy;
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
        UpdateRoutingOwnedSettingsPresentation();
        UpdateSaveBarVisibility();
    }

    private void GalleryRenditionRetryRequestedFromView(
        object? sender,
        GalleryRenditionRetryRequestedEventArgs eventArgs) =>
        GalleryRenditionRetryRequested?.Invoke(this, eventArgs);

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

    private static bool IsTrustedNamedGallerySource(RoutingInputSourceRecord source)
    {
        if (source.Kind is not (RoutingInputSourceKind.SteelSeriesGg or
                RoutingInputSourceKind.Nvidia))
            return false;
        try
        {
            return RoutingWatchedSourceRootIdentity.Create(
                    source.Kind,
                    source.CanonicalRoot)
                .Equals(source.RootIdentitySha256, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
                UnauthorizedAccessException or NotSupportedException or
                System.Security.SecurityException)
        {
            return false;
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
        if (keyData == Keys.Escape && _currentPage == SettingsPage.SilhouetteLayouts &&
            !_busy && !_galleryBusy)
        {
            ShowPage(SettingsPage.Capture);
            return true;
        }
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
        SettingsPage.Capture => CaptureDesignedClientSize,
        SettingsPage.Routes => RoutesDesignedClientSize,
        SettingsPage.SilhouetteLayouts => SilhouetteLayoutsDesignedClientSize,
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
            if (_galleryPage is not null)
            {
                _galleryPage.RenditionRetryRequested -= GalleryRenditionRetryRequestedFromView;
            }
            GalleryRenditionRetryRequested = null;
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
