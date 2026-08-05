using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

internal sealed class SettingsForm : Form
{
    internal const string ActivityComingSoonText = "Coming in a future release.";
    private static readonly Regex CompressionTargetPattern = new(
        @"^\s*(?<value>\d{1,3})\s*(?:MB)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
    private readonly TextBox _folderText = CreateTextBox("Clips folder");
    private readonly TextBox _webhookText = CreateTextBox("Discord webhook URL", usePasswordCharacter: true);
    private readonly TextBox _uploaderNameText = CreateTextBox("Uploader name");
    private readonly ComboBox _compressionTarget = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,
        FlatStyle = FlatStyle.Flat,
        Width = 140,
        Dock = DockStyle.Fill,
        Font = ClipCordTheme.InterfaceFont(10.5f),
        BackColor = Color.White,
        ForeColor = ClipCordTheme.Text,
        IntegralHeight = true,
        MaxDropDownItems = 7,
        AccessibleName = "Compression target in megabytes"
    };
    private readonly ToggleSwitch _startWithWindows = new() { Text = "Start with Windows" };
    private readonly OutlineButton _browseButton = CreateSecondaryButton("Browse", 112);
    private readonly OutlineButton _testButton = CreateSecondaryButton("Test webhook", 130);
    private readonly OutlineButton _checkUpdatesButton = CreateSecondaryButton("Check for updates", 154);
    private readonly GradientButton _saveButton = new()
    {
        Text = "Save changes",
        Size = new Size(175, 46),
        Margin = new Padding(12, 0, 0, 0)
    };
    private readonly OutlineButton _cancelButton = new()
    {
        Text = "Cancel",
        Size = new Size(118, 46),
        DialogResult = DialogResult.Cancel,
        SurfaceColor = Color.FromArgb(25, 35, 52),
        HoverColor = Color.FromArgb(35, 46, 65),
        OutlineColor = Color.FromArgb(65, 76, 96),
        ForeColor = ClipCordTheme.ShellText,
        Margin = Padding.Empty
    };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.ShellMutedText,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = ClipCordTheme.InterfaceFont(9.5f)
    };
    private readonly Func<IWin32Window, Task>? _checkForUpdatesAsync;
    private bool _busy;
    private string? _lastWatcherFullStatus;
    private Size _lastWindowRegionSize = Size.Empty;

    public AppSettings? SavedSettings { get; private set; }
    internal bool HasExplicitMaximizedBounds => !MaximizedBounds.IsEmpty;

    public SettingsForm(
        AppSettings settings,
        Icon? applicationIcon = null,
        Func<IWin32Window, Task>? checkForUpdatesAsync = null,
        Func<string>? watcherStatusProvider = null)
    {
        Text = "ClipCord — Settings";
        _ownedApplicationIcon = applicationIcon;
        _checkForUpdatesAsync = checkForUpdatesAsync;
        _watcherStatusProvider = watcherStatusProvider;
        if (_ownedApplicationIcon is not null) Icon = _ownedApplicationIcon;

        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1080, 780);
        MinimumSize = new Size(900, 650);
        BackColor = ClipCordTheme.Header;
        Padding = new Padding(ResizeGrip);
        Font = ClipCordTheme.InterfaceFont(9.5f);
        DoubleBuffered = true;

        _folderText.Text = settings.ClipsFolder;
        _webhookText.Text = settings.WebhookUrl;
        _uploaderNameText.MaxLength = AppSettings.MaximumUploaderNameLength;
        _uploaderNameText.Text = AppSettings.NormalizeUploaderName(settings.UploaderName);
        _compressionTarget.Items.AddRange(["5 MB", "10 MB", "25 MB", "50 MB", "75 MB", "95 MB", "100 MB"]);
        _compressionTarget.Text = $"{Math.Clamp(settings.CompressionTargetMb, 1, 100)} MB";
        _startWithWindows.Checked = settings.StartWithWindows;

        _browseButton.Click += BrowseClicked;
        _testButton.Click += TestClicked;
        _checkUpdatesButton.Click += CheckUpdatesClicked;
        _checkUpdatesButton.Enabled = _checkForUpdatesAsync is not null;
        _saveButton.Click += SaveClicked;
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
        };

        Controls.Add(BuildRootLayout());
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

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
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        return root;
    }

    private Control BuildHeader()
    {
        var header = new BufferedTableLayoutPanel
        {
            Name = "CustomTitleBar",
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(24, 0, 0, 0),
            BackColor = ClipCordTheme.Header
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var logo = new ClipCordLogoControl
        {
            Name = "HeaderLogo",
            Size = new Size(84, 84),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 12, 8, 12)
        };
        var version = typeof(SettingsForm).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var identity = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Header
        };
        var productName = new Label
        {
            Name = "ProductNameLabel",
            Text = "ClipCord",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.DisplayFont(25f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        var versionLabel = new Label
        {
            Text = $"{version.Major}.{version.Minor}.{version.Build}",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellMutedText,
            Font = ClipCordTheme.InterfaceFont(9.5f),
            Margin = new Padding(14, 14, 0, 0)
        };
        identity.Controls.Add(productName);
        identity.Controls.Add(versionLabel);

        var statusPill = new RoundedPanel
        {
            Name = "WatcherStatusPill",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(29, 38, 58),
            BorderColor = Color.FromArgb(61, 72, 96),
            CornerRadius = 20,
            Margin = new Padding(0, 27, 10, 33),
            Padding = new Padding(16, 0, 12, 0)
        };
        var statusLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusLayout.Controls.Add(new Label
        {
            Text = "●",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.Violet,
            Font = ClipCordTheme.InterfaceFont(12f, FontStyle.Bold)
        }, 0, 0);
        statusLayout.Controls.Add(_watcherStatusLabel, 1, 0);
        statusLayout.Controls.Add(new BrandGlyphControl
        {
            Glyph = BrandGlyph.Activity,
            GlyphColor = ClipCordTheme.Violet,
            StrokeWidth = 1.7f,
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 12, 2, 12),
            AccessibleName = "Watcher activity"
        }, 2, 0);
        statusPill.Controls.Add(statusLayout);

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

        EnableWindowDrag(header);
        EnableWindowDrag(logo);
        EnableWindowDrag(identity);
        EnableWindowDrag(productName);
        EnableWindowDrag(versionLabel);
        EnableWindowDrag(statusPill);
        EnableWindowDrag(statusLayout);
        foreach (Control statusChild in statusLayout.Controls) EnableWindowDrag(statusChild);
        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(identity, 1, 0);
        header.Controls.Add(statusPill, 3, 0);
        header.Controls.Add(windowActions, 4, 0);
        return header;
    }

    private Control BuildBody()
    {
        var body = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(BuildNavigation(), 0, 0);
        body.Controls.Add(BuildCards(), 1, 0);
        return body;
    }

    private Control BuildNavigation()
    {
        var navigation = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(16, 24, 16, 0),
            BackColor = ClipCordTheme.Sidebar
        };
        navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var settingsItem = CreateNavigationItem("Settings", BrandGlyph.Settings, selected: true);
        settingsItem.Name = "SettingsNavItem";
        settingsItem.AccessibleName = "Settings";
        settingsItem.AccessibleDescription = "Current page";
        settingsItem.AccessibleRole = AccessibleRole.MenuItem;
        settingsItem.EnableKeyboardAccess();

        var activityItem = CreateNavigationItem(
            "Activity",
            BrandGlyph.Activity,
            selected: false,
            unavailable: true);
        activityItem.Name = "ActivityNavItem";
        activityItem.Tag = ActivityComingSoonText;
        activityItem.AccessibleName = "Activity, unavailable";
        activityItem.AccessibleDescription = ActivityComingSoonText;
        activityItem.AccessibleRole = AccessibleRole.MenuItem;
        activityItem.AccessibilityUnavailable = true;
        activityItem.Cursor = Cursors.Help;
        activityItem.EnableKeyboardAccess();
        _toolTip.SetToolTip(activityItem, ActivityComingSoonText);
        foreach (Control child in EnumerateControls(activityItem))
        {
            _toolTip.SetToolTip(child, ActivityComingSoonText);
        }

        var aboutItem = CreateNavigationItem("About", BrandGlyph.About, selected: false);
        aboutItem.Name = "AboutNavItem";
        aboutItem.AccessibleName = "About ClipCord";
        aboutItem.AccessibleRole = AccessibleRole.MenuItem;
        aboutItem.EnableKeyboardAccess(ShowAbout);
        WireClick(aboutItem, ShowAbout);

        navigation.Controls.Add(settingsItem, 0, 0);
        navigation.Controls.Add(activityItem, 0, 1);
        navigation.Controls.Add(aboutItem, 0, 2);
        return navigation;
    }

    private static RoundedPanel CreateNavigationItem(
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
            BorderColor = selected ? Color.FromArgb(73, 62, 107) : Color.Transparent,
            CornerRadius = 12,
            Margin = new Padding(0, 0, 0, 10),
            Padding = Padding.Empty
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = selected ? 4 : 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        if (selected)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 5));
            layout.Controls.Add(new GradientStrip { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
        }
        var iconColumn = selected ? 1 : 0;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var icon = new BrandGlyphControl
        {
            Glyph = glyph,
            GlyphColor = unavailable
                ? Color.FromArgb(105, 115, 134)
                : selected ? ClipCordTheme.Violet : ClipCordTheme.ShellMutedText,
            StrokeWidth = 1.9f,
            Dock = DockStyle.Fill,
            Margin = new Padding(12, 10, 6, 10),
            Enabled = !unavailable
        };
        var label = new Label
        {
            Name = unavailable ? "ActivityNavLabel" : string.Empty,
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = unavailable ? Color.FromArgb(111, 121, 141) : ClipCordTheme.ShellText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ClipCordTheme.InterfaceFont(11f, selected ? FontStyle.Bold : FontStyle.Regular),
            Enabled = !unavailable,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        layout.Controls.Add(icon, iconColumn, 0);
        layout.Controls.Add(label, iconColumn + 1, 0);
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            var badge = new Label
            {
                Text = badgeText,
                AutoSize = true,
                ForeColor = Color.FromArgb(116, 124, 143),
                BackColor = Color.Transparent,
                Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Enabled = false,
                Margin = new Padding(5, 20, 12, 0)
            };
            layout.Controls.Add(badge, iconColumn + 2, 0);
        }
        surface.Controls.Add(layout);
        return surface;
    }

    private Control BuildCards()
    {
        var cards = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(26, 12, 26, 12),
            BackColor = ClipCordTheme.Shell
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cards.Controls.Add(BuildClipSourceCard(), 0, 0);
        cards.Controls.Add(BuildDiscordCard(), 0, 1);
        cards.Controls.Add(BuildUploadCard(), 0, 2);
        return cards;
    }

    private Control BuildClipSourceCard()
    {
        var layout = CreateCardContent(5);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.Controls.Add(CreateCardHeading("Clip source"), 0, 0);
        layout.Controls.Add(CreateFieldLabel("Clips folder"), 0, 1);
        layout.Controls.Add(CreateFieldRow(CreateFieldHost(_folderText), _browseButton), 0, 2);
        layout.Controls.Add(CreateHelper("New MP4 clips in this folder are detected automatically."), 0, 3);
        return CreateCard(BrandGlyph.Folder, "Clip source settings", layout, new Padding(0, 0, 0, 12));
    }

    private Control BuildDiscordCard()
    {
        var layout = CreateCardContent(6);
        layout.ColumnCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        var heading = CreateCardHeading("Discord destination");
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);
        layout.Controls.Add(CreateInlineFieldLabel("Uploader name"), 0, 1);
        layout.Controls.Add(CreateFieldHost(_uploaderNameText), 1, 1);
        layout.Controls.Add(CreateInlineFieldLabel("Webhook URL"), 0, 3);
        layout.Controls.Add(CreateFieldRow(CreateFieldHost(_webhookText), _testButton), 1, 3);
        layout.Controls.Add(CreateHelper("Encrypted for your Windows account."), 1, 4);
        return CreateCard(BrandGlyph.Destination, "Discord destination settings", layout, new Padding(0, 0, 0, 12));
    }

    private Control BuildUploadCard()
    {
        var layout = CreateCardContent(3);
        layout.ColumnCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = CreateCardHeading("Upload preferences");
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);

        var compression = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        var compressionLabel = new Label
        {
            Text = "Compression target",
            AutoSize = true,
            ForeColor = ClipCordTheme.Text,
            Font = ClipCordTheme.InterfaceFont(10f),
            Margin = new Padding(0, 11, 14, 0)
        };
        var compressionHost = CreateCompressionHost();
        compression.Controls.Add(compressionLabel);
        compression.Controls.Add(compressionHost);
        layout.Controls.Add(compression, 0, 1);
        layout.SetColumnSpan(compression, 2);
        _checkUpdatesButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _checkUpdatesButton.Margin = new Padding(0, 2, 0, 0);
        layout.Controls.Add(_checkUpdatesButton, 1, 2);
        _startWithWindows.Margin = new Padding(0, 2, 0, 0);
        layout.Controls.Add(_startWithWindows, 0, 2);
        return CreateCard(BrandGlyph.Sliders, "Upload preferences", layout, Padding.Empty);
    }

    private static RoundedPanel CreateCard(BrandGlyph glyph, string accessibleName, Control content, Padding margin)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, accessibleName.StartsWith("Discord", StringComparison.Ordinal) ? 170 : 125),
            BackColor = ClipCordTheme.Card,
            BorderColor = ClipCordTheme.CardBorder,
            CornerRadius = 16,
            Padding = new Padding(18, 10, 18, 10),
            Margin = margin,
            AccessibleName = accessibleName
        };
        var shell = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = ClipCordTheme.Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(new BrandIconTile
        {
            Glyph = glyph,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, 0, 14, 0),
            AccessibleName = accessibleName + " icon"
        }, 0, 0);
        shell.Controls.Add(content, 1, 0);
        card.Controls.Add(shell);
        return card;
    }

    private Control BuildFooter()
    {
        var footer = new BufferedTableLayoutPanel
        {
            Name = "FooterLayout",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(26, 18, 26, 18),
            BackColor = ClipCordTheme.Header
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var messages = new BufferedTableLayoutPanel
        {
            Name = "FooterMessages",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Header
        };
        messages.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        messages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        messages.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        messages.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var shield = new BrandGlyphControl
        {
            Glyph = BrandGlyph.Shield,
            GlyphColor = ClipCordTheme.ShellMutedText,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 5, 8, 5),
            AccessibleName = "Privacy"
        };
        messages.Controls.Add(shield, 0, 0);
        messages.SetRowSpan(shield, 2);
        messages.Controls.Add(_statusLabel, 1, 0);
        messages.Controls.Add(new Label
        {
            Text = "Your clips go directly to Discord.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.ShellMutedText,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ClipCordTheme.InterfaceFont(9f)
        }, 1, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Header
        };
        actions.Controls.Add(_saveButton);
        _cancelButton.Margin = new Padding(12, 0, 0, 0);
        actions.Controls.Add(_cancelButton);
        footer.Controls.Add(messages, 0, 0);
        footer.Controls.Add(actions, 1, 0);
        return footer;
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
            BackColor = ClipCordTheme.Card
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
        ForeColor = ClipCordTheme.Text,
        Font = ClipCordTheme.DisplayFont(14f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 0, 4)
    };

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.Text,
        Font = ClipCordTheme.InterfaceFont(9.5f, FontStyle.Bold),
        TextAlign = ContentAlignment.BottomLeft,
        Margin = new Padding(0, 0, 0, 2)
    };

    private static Label CreateInlineFieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.Text,
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
        ForeColor = ClipCordTheme.MutedText,
        Font = ClipCordTheme.InterfaceFont(9f),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private static RoundedPanel CreateFieldHost(Control field)
    {
        var host = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            BorderColor = Color.FromArgb(202, 206, 215),
            CornerRadius = 7,
            Padding = new Padding(11, 7, 11, 5),
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
            Width = 170,
            BackColor = Color.White,
            BorderColor = Color.FromArgb(202, 206, 215),
            CornerRadius = 7,
            Padding = new Padding(10, 5, 6, 4),
            Margin = new Padding(0, 2, 0, 0)
        };
        host.Tag = _compressionTarget;
        FitFieldHost(host, _compressionTarget);
        _compressionTarget.Margin = Padding.Empty;
        host.Controls.Add(_compressionTarget);
        return host;
    }

    private static Control CreateFieldRow(Control field, Button action)
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
            BackColor = ClipCordTheme.Card
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Tag = (field, action);
        FitFieldActionRow(row, field, action);
        field.Margin = new Padding(0, 0, 12, 0);
        action.Margin = Padding.Empty;
        row.Controls.Add(field, 0, 0);
        row.Controls.Add(action, 1, 0);
        return row;
    }

    private static void FitFieldHost(RoundedPanel host, Control field)
    {
        host.MaximumSize = Size.Empty;
        var hostHeight = Math.Max(38, field.PreferredSize.Height + host.Padding.Vertical + 2);
        var minimumWidth = host.Dock == DockStyle.Fill ? 0 : Math.Max(1, host.Width);
        host.Height = hostHeight;
        host.MinimumSize = new Size(minimumWidth, hostHeight);
        host.MaximumSize = new Size(0, hostHeight);
    }

    private static void FitFieldActionRow(BufferedTableLayoutPanel row, Control field, Button action)
    {
        row.MaximumSize = Size.Empty;
        var rowHeight = Math.Max(field.MinimumSize.Height, action.PreferredSize.Height);
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
        BackColor = Color.White,
        ForeColor = ClipCordTheme.Text,
        AccessibleName = accessibleName
    };

    private static OutlineButton CreateSecondaryButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 38,
        SurfaceColor = Color.White,
        HoverColor = Color.FromArgb(246, 243, 253),
        OutlineColor = ClipCordTheme.CardBorder,
        ForeColor = ClipCordTheme.Text,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Margin = Padding.Empty
    };

    private void UpdateWatcherStatus()
    {
        if (IsDisposed || Disposing) return;
        var fullStatus = _watcherStatusProvider?.Invoke() ?? "Settings";
        var conciseStatus = fullStatus switch
        {
            var value when value.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                           value.Contains("paused", StringComparison.OrdinalIgnoreCase) => "Paused",
            var value when value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                           value.Contains("failed", StringComparison.OrdinalIgnoreCase) => "Needs attention",
            var value when value.Contains("archive", StringComparison.OrdinalIgnoreCase) ||
                           value.Contains("move", StringComparison.OrdinalIgnoreCase) => "Archiving",
            var value when value.Contains("upload", StringComparison.OrdinalIgnoreCase) => "Uploading",
            var value when value.Contains("hash", StringComparison.OrdinalIgnoreCase) => "Preparing clip",
            var value when value.Contains("watch", StringComparison.OrdinalIgnoreCase) => "Watching",
            var value when value.Contains("setup", StringComparison.OrdinalIgnoreCase) => "Setup required",
            _ => fullStatus
        };
        if (conciseStatus.Length > 28)
        {
            var safeLength = char.IsHighSurrogate(conciseStatus[27]) ? 27 : 28;
            conciseStatus = conciseStatus[..safeLength];
        }
        if (_watcherStatusLabel.Text != conciseStatus) _watcherStatusLabel.Text = conciseStatus;
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
        if (!TryValidate(out _)) return;

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
            _statusLabel.ForeColor = ClipCordTheme.ShellMutedText;
            _statusLabel.Text = "Update check finished.";
        }
        catch (Exception exception)
        {
            Log.Error("Could not complete a manual update check.", exception);
            if (IsDisposed || Disposing) return;
            _statusLabel.ForeColor = ClipCordTheme.Coral;
            _statusLabel.Text = "Update check failed.";
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

    private void ShowAbout()
    {
        var version = typeof(SettingsForm).Assembly.GetName().Version ?? new Version(0, 0, 0);
        MessageBox.Show(
            this,
            $"ClipCord {version.Major}.{version.Minor}.{version.Build}\n\n" +
            "Automatically relay completed gaming clips to Discord.\n\n" +
            "ClipCord is not affiliated with Discord or any recording-software vendor.",
            "About ClipCord",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
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

        settings = new AppSettings(
            _folderText.Text.Trim(),
            _webhookText.Text.Trim(),
            _startWithWindows.Checked,
            compressionTargetMb,
            AppSettings.NormalizeUploaderName(_uploaderNameText.Text));

        if (string.IsNullOrWhiteSpace(_uploaderNameText.Text))
        {
            MessageBox.Show(this, "Enter the name Discord should show with uploaded clips.", "Invalid uploader name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!Directory.Exists(settings.ClipsFolder))
        {
            MessageBox.Show(this, "Choose an existing clips folder.", "Invalid folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!WebhookValidation.IsDiscordWebhook(settings.WebhookUrl))
        {
            MessageBox.Show(this, "Enter a valid HTTPS Discord webhook URL.", "Invalid webhook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
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
    }

    private void FormClosingWhileBusy(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_busy && eventArgs.CloseReason == CloseReason.UserClosing) eventArgs.Cancel = true;
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
        var availableWidth = Math.Max(MinimumSize.Width, workingArea.Width - 24);
        var availableHeight = Math.Max(MinimumSize.Height, workingArea.Height - 24);
        var fittedSize = new Size(Math.Min(Width, availableWidth), Math.Min(Height, availableHeight));
        if (fittedSize != Size)
        {
            Size = fittedSize;
            Location = new Point(
                workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
                workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));
        }
        base.OnShown(eventArgs);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
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
            _toolTip.Dispose();
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
