using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal sealed class AboutView : UserControl
{
    internal const int FeatureIconLogicalSize = 48;
    internal const int ReleaseIconLogicalSize = 44;

    private readonly AppSettings _settings;
    private readonly Func<string>? _watcherStatusProvider;
    private readonly Func<bool> _discordRunningProvider;
    private readonly Func<string?> _ffmpegExecutableProvider;
    private readonly Action<ProcessStartInfo> _processStarter;
    private readonly Action<string> _clipboardWriter;
    private readonly string _dataDirectory;
    private readonly BrandedScrollHost _scrollHost;
    private readonly AboutContentLayout _content;
    private readonly OutlineButton _checkUpdatesButton;
    private readonly Label _updateStateLabel;
    private readonly Label _watcherLabel;
    private readonly Label _watcherDetailLabel;
    private readonly Label _routeLabel;
    private readonly Label _routeDetailLabel;
    private readonly Label _startupLabel;
    private readonly Label _startupDetailLabel;
    private readonly Label _installationLabel;
    private readonly Label _installationDetailLabel;
    private readonly Label _actionStatusLabel;
    private AboutStatusSnapshot? _snapshot;
    private bool _busy;

    internal event EventHandler? CheckUpdatesRequested;

    internal AboutView(
        AppSettings settings,
        Func<string>? watcherStatusProvider = null,
        Func<bool>? discordRunningProvider = null,
        Func<string?>? ffmpegExecutableProvider = null,
        Action<ProcessStartInfo>? processStarter = null,
        Action<string>? clipboardWriter = null,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _watcherStatusProvider = watcherStatusProvider;
        _discordRunningProvider = discordRunningProvider ?? DiscordDetector.IsRunning;
        _ffmpegExecutableProvider = ffmpegExecutableProvider ?? FfmpegCompressor.FindExecutable;
        _processStarter = processStarter ?? (start => Process.Start(start));
        _clipboardWriter = clipboardWriter ?? Clipboard.SetText;
        _dataDirectory = dataDirectory ?? SettingsStore.DataDirectory;

        Name = "AboutView";
        AccessibleName = "About ClipCord";
        AccessibleRole = AccessibleRole.Pane;
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;
        Font = ClipCordTheme.InterfaceFont(9.5f);

        var version = typeof(AboutView).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var formattedVersion = AboutPageSupport.FormatApplicationVersion(version);

        _watcherLabel = CreateValueLabel("AboutWatcherStatusLabel");
        _watcherDetailLabel = CreateDetailLabel("AboutWatcherDetailLabel");
        _routeLabel = CreateValueLabel("AboutRoutingStatusLabel");
        _routeDetailLabel = CreateDetailLabel("AboutRoutingDetailLabel");
        _startupLabel = CreateValueLabel("AboutStartupStatusLabel");
        _startupDetailLabel = CreateDetailLabel("AboutStartupDetailLabel");
        _installationLabel = CreateValueLabel("AboutInstallationStatusLabel");
        _installationDetailLabel = CreateDetailLabel("AboutInstallationDetailLabel");
        _actionStatusLabel = new Label
        {
            Name = "AboutActionStatusLabel",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = "No private values are included in copied diagnostics.",
            ForeColor = ClipCordTheme.SettingsMutedText,
            Font = ClipCordTheme.InterfaceFont(8.2f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 5, 0, 0),
            AccessibleRole = AccessibleRole.StaticText
        };

        _updateStateLabel = new Label
        {
            Name = "AboutUpdateStateLabel",
            Text = "Stable release channel",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(126, 218, 175),
            Font = ClipCordTheme.InterfaceFont(8.5f),
            TextAlign = ContentAlignment.TopLeft,
            AccessibleRole = AccessibleRole.StaticText
        };
        _checkUpdatesButton = CreateButton("Check for updates", "AboutCheckUpdatesButton");
        _checkUpdatesButton.Dock = DockStyle.Fill;
        _checkUpdatesButton.MinimumSize = new Size(0, 36);
        _checkUpdatesButton.Click += (_, _) =>
        {
            if (!_busy && _checkUpdatesButton.Enabled) CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        };

        var hero = BuildHero(formattedVersion);
        var status = BuildStatusCard();
        var diagnostics = BuildDiagnosticsCard();
        var privacy = BuildPrivacyCard();
        var credits = BuildCreditsCard();
        var disclaimer = new Label
        {
            Name = "AboutDisclaimerLabel",
            Text = "ClipCord is not affiliated with Discord or any recording-software vendor. " +
                   "FFmpeg is distributed under its applicable open-source license.",
            ForeColor = Color.FromArgb(119, 133, 154),
            Font = ClipCordTheme.InterfaceFont(8.2f),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            AccessibleRole = AccessibleRole.StaticText
        };

        _content = new AboutContentLayout(hero, status, diagnostics, privacy, credits, disclaimer)
        {
            Name = "AboutContent",
            AccessibleName = "About ClipCord content",
            AccessibleRole = AccessibleRole.Pane,
            BackColor = ClipCordTheme.Shell
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "AboutScrollHost",
            AccessibleName = "About ClipCord content",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.Shell,
            Content = _content
        };
        Controls.Add(_scrollHost);
        RefreshStatus();
    }

    internal bool HasOverflow => _scrollHost.HasOverflow;
    internal AboutStatusSnapshot? CurrentSnapshot => _snapshot;

    internal void RefreshViewport() => _scrollHost.RefreshContentLayout();

    internal void RefreshStatus()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            var version = typeof(AboutView).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var discordRunning = _discordRunningProvider();
            var facts = AboutRuntimeFacts.Capture(
                version,
                Application.ExecutablePath,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                !string.IsNullOrWhiteSpace(_ffmpegExecutableProvider()),
                discordRunning);
            _snapshot = AboutStatusSnapshot.Create(
                _settings,
                _watcherStatusProvider?.Invoke(),
                facts);
            ApplyStatus(_snapshot);
        }
        catch (Exception exception)
        {
            Log.Error("Could not refresh the About status snapshot.", exception);
            _watcherLabel.Text = "Status unavailable";
            _watcherDetailLabel.Text = "Status is temporarily unavailable";
        }
    }

    internal void UpdateWatcherStatus(string? rawStatus)
    {
        if (_snapshot is null || IsDisposed || Disposing) return;
        var watcher = AboutPageSupport.NormalizeWatcherStatus(
            rawStatus,
            _snapshot.Discord.Equals("Open", StringComparison.OrdinalIgnoreCase));
        _watcherLabel.Text = watcher.Label;
        _watcherDetailLabel.Text = watcher.Detail;
    }

    internal void SetUpdateState(string state)
    {
        if (!IsDisposed && !Disposing) _updateStateLabel.Text = state;
    }

    internal void SetBusy(bool busy, bool updateChecksAvailable)
    {
        _busy = busy;
        if (IsDisposed || Disposing) return;
        _checkUpdatesButton.Enabled = !busy && updateChecksAvailable;
        foreach (var button in EnumerateControls(this).OfType<Button>().Where(button => !ReferenceEquals(button, _checkUpdatesButton)))
        {
            button.Enabled = !busy;
        }
        if (busy) _updateStateLabel.Text = "Checking…";
    }

    private Control BuildHero(string version)
    {
        var hero = new AboutHeroPanel
        {
            Name = "AboutHero",
            AccessibleName = "ClipCord release overview",
            AccessibleRole = AccessibleRole.Grouping,
            Padding = new Padding(25, 18, 20, 18)
        };
        var layout = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        layout.SetLogicalColumnWidth(1, 250);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 5, 24, 3)
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        copy.Controls.Add(new Label
        {
            Name = "AboutTaglineLabel",
            Text = "Your clips. Your choice. Your Discord.",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.DisplayFont(19f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Name = "AboutDescriptionLabel",
            Text = "ClipCord watches your recording folder, routes new clips where you choose, " +
                   "and keeps your webhook, history, and gallery safely on this PC.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(174, 185, 204),
            Font = ClipCordTheme.InterfaceFont(9.5f),
            TextAlign = ContentAlignment.TopLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 1);

        var release = new RoundedPanel
        {
            Name = "AboutReleaseCard",
            AccessibleName = "Stable release information",
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 26, 44),
            BorderColor = Color.FromArgb(58, 73, 101),
            CornerRadius = 12,
            Margin = Padding.Empty,
            Padding = new Padding(12, 10, 12, 10),
            MinimumSize = new Size(250, 0)
        };
        var releaseLayout = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        releaseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        releaseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        releaseLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        releaseLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        releaseLayout.SetLogicalRowHeight(1, 36);
        var releaseIcon = CreateSectionIcon(BrandGlyph.Shield, ReleaseIconLogicalSize);
        releaseIcon.Name = "AboutReleaseIcon";
        releaseIcon.Margin = new Padding(0, 0, 8, 0);
        releaseLayout.Controls.Add(releaseIcon, 0, 0);
        var releaseCopy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 1, 0, 0)
        };
        releaseCopy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        releaseCopy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        releaseCopy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        releaseCopy.Controls.Add(new Label
        {
            Name = "AboutReleaseVersionLabel",
            Text = $"Stable · v{version}",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.InterfaceFont(9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        releaseCopy.Controls.Add(_updateStateLabel, 0, 1);
        releaseLayout.Controls.Add(releaseCopy, 1, 0);
        releaseLayout.Controls.Add(_checkUpdatesButton, 0, 1);
        releaseLayout.SetColumnSpan(_checkUpdatesButton, 2);
        release.Controls.Add(releaseLayout);

        layout.Controls.Add(copy, 0, 0);
        layout.Controls.Add(release, 1, 0);
        hero.Controls.Add(layout);
        return hero;
    }

    private RoundedPanel BuildStatusCard()
    {
        var card = CreateSectionCard(
            "AboutStatusCard",
            "App status",
            "A quick read-only snapshot of this installation.",
            BrandGlyph.AppStatus,
            out var body);
        body.ColumnCount = 2;
        body.RowCount = 2;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.Controls.Add(CreateStatusItem("AboutWatcherStatusItem", _watcherLabel, _watcherDetailLabel, false), 0, 0);
        body.Controls.Add(CreateStatusItem("AboutRoutingStatusItem", _routeLabel, _routeDetailLabel, true), 1, 0);
        body.Controls.Add(CreateStatusItem("AboutStartupStatusItem", _startupLabel, _startupDetailLabel, false), 0, 1);
        body.Controls.Add(CreateStatusItem("AboutInstallationStatusItem", _installationLabel, _installationDetailLabel, true), 1, 1);
        return card;
    }

    private RoundedPanel BuildDiagnosticsCard()
    {
        var card = CreateSectionCard(
            "AboutDiagnosticsCard",
            "Help & diagnostics",
            "Useful shortcuts when something needs attention.",
            BrandGlyph.Diagnostics,
            out var body);
        body.ColumnCount = 2;
        body.RowCount = 3;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        body.SetLogicalRowHeight(2, 21);
        var openLogs = CreateButton("Open logs", "AboutOpenLogsButton", glyph: BrandGlyph.FileText);
        openLogs.Click += (_, _) => OpenLogs();
        var copy = CreateButton("Copy diagnostics", "AboutCopyDiagnosticsButton", glyph: BrandGlyph.Copy);
        copy.Click += (_, _) => CopyDiagnostics();
        var data = CreateButton("Open data folder", "AboutOpenDataFolderButton", glyph: BrandGlyph.FolderOpen);
        data.Click += (_, _) => OpenDataFolder();
        var report = CreateButton("Report a problem", "AboutReportProblemButton", glyph: BrandGlyph.ReportProblem);
        report.Click += (_, _) => OpenLink(AboutLink.ReportProblem, "Could not open the issue form.");
        body.Controls.Add(openLogs, 0, 0);
        body.Controls.Add(copy, 1, 0);
        body.Controls.Add(data, 0, 1);
        body.Controls.Add(report, 1, 1);
        body.Controls.Add(_actionStatusLabel, 0, 2);
        body.SetColumnSpan(_actionStatusLabel, 2);
        return card;
    }

    private RoundedPanel BuildPrivacyCard()
    {
        var card = CreateSectionCard(
            "AboutPrivacyCard",
            "Privacy & security",
            "Clear answers about what ClipCord stores and sends.",
            BrandGlyph.Shield,
            out var body);
        body.ColumnCount = 1;
        body.RowCount = 5;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 4; index++) body.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        body.SetLogicalRowHeight(4, 36);
        var statements = new[]
        {
            "Activity, Gallery, and routing history stay on this PC.",
            "Your Discord webhook is encrypted for your Windows account.",
            "No ClipCord account, analytics, advertising, or behavioral tracking.",
            "Network access is limited to Discord uploads and verified GitHub updates."
        };
        for (var index = 0; index < statements.Length; index++)
        {
            body.Controls.Add(CreatePrivacyStatement(statements[index], index), 0, index);
        }
        var links = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 0),
            Padding = Padding.Empty
        };
        links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        links.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var privacy = CreateButton("Privacy details", "AboutPrivacyButton");
        privacy.Click += (_, _) => OpenLink(AboutLink.Privacy, "Could not open the privacy guide.");
        var security = CreateButton("Security design", "AboutSecurityButton");
        security.Click += (_, _) => OpenLink(AboutLink.SecurityDesign, "Could not open the security guide.");
        links.Controls.Add(privacy, 0, 0);
        links.Controls.Add(security, 1, 0);
        body.Controls.Add(links, 0, 4);
        return card;
    }

    private RoundedPanel BuildCreditsCard()
    {
        var card = CreateSectionCard(
            "AboutCreditsCard",
            "Credits & project",
            "The people, lore, and open-source work behind ClipCord.",
            BrandGlyph.Credits,
            out var body);
        body.ColumnCount = 1;
        body.RowCount = 3;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        body.SetLogicalRowHeight(2, 36);
        body.Controls.Add(CreateCredit("AboutDixonCredit", "DY", "Dixon Yamada", "Certified Looter", false), 0, 0);
        body.Controls.Add(CreateCredit(
            "AboutPapiCredit",
            "PJ",
            "Papi Jawn",
            "Certified Shooter · LeBron’s Legacy",
            true), 0, 1);
        var links = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 5, 0, 0),
            Padding = Padding.Empty
        };
        for (var index = 0; index < 4; index++) links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        links.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var repository = CreateButton("GitHub", "AboutGitHubButton", 8.2f);
        repository.Click += (_, _) => OpenLink(AboutLink.Repository, "Could not open the ClipCord repository.");
        var releases = CreateButton("Release notes", "AboutReleaseNotesButton", 8.2f);
        releases.Click += (_, _) => OpenLink(AboutLink.ReleaseNotes, "Could not open the release notes.");
        var roadmap = CreateButton("Roadmap", "AboutRoadmapButton", 8.2f);
        roadmap.Click += (_, _) => OpenLink(AboutLink.Roadmap, "Could not open the roadmap.");
        var licenses = CreateButton("Licenses", "AboutLicensesButton", 8.2f);
        licenses.Click += (_, _) => OpenLink(AboutLink.ThirdPartyNotices, "Could not open the license notices.");
        links.Controls.Add(repository, 0, 0);
        links.Controls.Add(releases, 1, 0);
        links.Controls.Add(roadmap, 2, 0);
        links.Controls.Add(licenses, 3, 0);
        body.Controls.Add(links, 0, 2);
        return card;
    }

    private static RoundedPanel CreateSectionCard(
        string name,
        string title,
        string subtitle,
        BrandGlyph glyph,
        out AboutMetricTableLayoutPanel body)
    {
        var card = new RoundedPanel
        {
            Name = name,
            AccessibleName = title,
            AccessibleRole = AccessibleRole.Grouping,
            BackColor = ClipCordTheme.SettingsCard,
            BorderColor = ClipCordTheme.SettingsCardBorder,
            CornerRadius = 14,
            Padding = new Padding(16),
            Margin = Padding.Empty
        };
        var layout = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.SetLogicalRowHeight(0, 48);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateSectionHeading(title, subtitle, glyph), 0, 0);
        body = new AboutMetricTableLayoutPanel
        {
            Name = name + "Body",
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.Controls.Add(body, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private static Control CreateSectionHeading(string title, string subtitle, BrandGlyph glyph)
    {
        var heading = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var icon = CreateSectionIcon(glyph, FeatureIconLogicalSize);
        icon.Name = "AboutFeatureIcon";
        icon.Margin = new Padding(0, 0, 10, 0);
        heading.Controls.Add(icon, 0, 0);
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.InterfaceFont(11f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(135, 148, 169),
            Font = ClipCordTheme.InterfaceFont(8.2f),
            TextAlign = ContentAlignment.TopLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 1);
        heading.Controls.Add(copy, 1, 0);
        return heading;
    }

    private static AboutSectionIcon CreateSectionIcon(BrandGlyph glyph, int size)
    {
        return new AboutSectionIcon
        {
            Size = new Size(size, size),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Glyph = glyph,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.None,
            AccessibleName = string.Empty
        };
    }

    private static RoundedPanel CreateStatusItem(string name, Label value, Label detail, bool violet)
    {
        var item = new RoundedPanel
        {
            Name = name,
            AccessibleName = value.AccessibleName,
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SettingsField,
            BorderColor = Color.Transparent,
            CornerRadius = 9,
            Margin = new Padding(0, 0, violet ? 0 : 5, 5),
            Padding = new Padding(10, 5, 8, 5)
        };
        var layout = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        layout.SetLogicalColumnWidth(0, 42);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "●",
            Dock = DockStyle.Fill,
            MinimumSize = new Size(20, 0),
            ForeColor = violet ? Color.FromArgb(154, 85, 246) : Color.FromArgb(88, 213, 151),
            Font = ClipCordTheme.InterfaceFont(10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleRole = AccessibleRole.None
        }, 0, 0);
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        copy.Controls.Add(value, 0, 0);
        copy.Controls.Add(detail, 0, 1);
        layout.Controls.Add(copy, 1, 0);
        item.Controls.Add(layout);
        return item;
    }

    private static Control CreatePrivacyStatement(string text, int index)
    {
        var statement = new AboutMetricTableLayoutPanel
        {
            Name = $"AboutPrivacyStatement{index + 1}",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        statement.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22));
        statement.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statement.SetLogicalColumnWidth(0, 22);
        statement.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statement.Controls.Add(new Label
        {
            Text = "◆",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(143, 84, 243),
            Font = ClipCordTheme.InterfaceFont(7.5f),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(2, 4, 0, 0),
            AccessibleRole = AccessibleRole.None
        }, 0, 0);
        statement.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(183, 193, 209),
            Font = ClipCordTheme.InterfaceFont(8.7f),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 2, 0, 0),
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);
        return statement;
    }

    private static RoundedPanel CreateCredit(
        string name,
        string initials,
        string person,
        string title,
        bool violet)
    {
        var credit = new RoundedPanel
        {
            Name = name,
            AccessibleName = $"{person}, {title}",
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SettingsField,
            CornerRadius = 9,
            Margin = new Padding(0, 0, 0, 5),
            Padding = new Padding(8, 4, 8, 4)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var avatar = new AboutAvatarControl(initials, violet)
        {
            Name = name + "Avatar",
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0)
        };
        layout.Controls.Add(avatar, 0, 0);
        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        copy.Controls.Add(new Label
        {
            Text = person,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(237, 241, 248),
            Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(155, 167, 186),
            Font = ClipCordTheme.InterfaceFont(8f),
            TextAlign = ContentAlignment.TopLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 1);
        layout.Controls.Add(copy, 1, 0);
        credit.Controls.Add(layout);
        return credit;
    }

    private static OutlineButton CreateButton(
        string text,
        string name,
        float fontSize = 8.8f,
        BrandGlyph? glyph = null) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = text,
        AccessibleRole = AccessibleRole.PushButton,
        Dock = DockStyle.Fill,
        AutoSize = false,
        SurfaceColor = ClipCordTheme.SettingsButton,
        HoverColor = ClipCordTheme.SettingsButtonHover,
        DisabledSurfaceColor = Color.FromArgb(28, 38, 55),
        DisabledTextColor = Color.FromArgb(112, 123, 142),
        OutlineColor = ClipCordTheme.SettingsFieldBorder,
        ForeColor = ClipCordTheme.ShellText,
        Font = ClipCordTheme.InterfaceFont(fontSize, FontStyle.Bold),
        LeadingGlyph = glyph,
        Margin = new Padding(0, 0, 5, 5),
        TabStop = true,
        MinimumSize = new Size(0, 34)
    };

    private static Label CreateValueLabel(string name) => new()
    {
        Name = name,
        AccessibleName = name.Replace("About", string.Empty).Replace("StatusLabel", string.Empty),
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = Color.FromArgb(233, 237, 245),
        Font = ClipCordTheme.InterfaceFont(8.6f, FontStyle.Bold),
        TextAlign = ContentAlignment.BottomLeft,
        AccessibleRole = AccessibleRole.StaticText
    };

    private static Label CreateDetailLabel(string name) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = Color.FromArgb(141, 154, 174),
        Font = ClipCordTheme.InterfaceFont(7.6f),
        TextAlign = ContentAlignment.TopLeft,
        AccessibleRole = AccessibleRole.StaticText
    };

    private void ApplyStatus(AboutStatusSnapshot snapshot)
    {
        _watcherLabel.Text = snapshot.Watcher;
        _watcherDetailLabel.Text = snapshot.WatcherDetail;
        _routeLabel.Text = snapshot.Routing;
        _routeDetailLabel.Text = snapshot.RoutingDetail;
        _startupLabel.Text = snapshot.Startup;
        _startupDetailLabel.Text = snapshot.StartupDetail;
        _installationLabel.Text = snapshot.Installation;
        _installationDetailLabel.Text = snapshot.InstallationDetail;
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var logPath = Path.Combine(_dataDirectory, "app.log");
            _processStarter(AboutPageSupport.CreateOpenLogsStartInfo(_dataDirectory, File.Exists(logPath)));
            SetActionStatus("Opened the ClipCord logs location.");
        }
        catch (Exception exception)
        {
            HandleActionFailure("Could not open the ClipCord logs folder.", "Windows could not open the logs location.", exception);
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            _processStarter(AboutPageSupport.CreateOpenDataFolderStartInfo(_dataDirectory));
            SetActionStatus("Opened the ClipCord data folder.");
        }
        catch (Exception exception)
        {
            HandleActionFailure("Could not open the ClipCord data folder.", "Windows could not open the data folder.", exception);
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            RefreshStatus();
            if (_snapshot is null) throw new InvalidOperationException("The diagnostic snapshot is unavailable.");
            _clipboardWriter(AboutPageSupport.BuildDiagnostics(_snapshot));
            SetActionStatus("Safe diagnostics copied — webhook and clip names excluded.");
        }
        catch (Exception exception)
        {
            HandleActionFailure("Could not copy the safe diagnostic summary.", "Windows could not copy the diagnostics.", exception);
        }
    }

    private void OpenLink(AboutLink link, string userMessage)
    {
        try
        {
            _processStarter(AboutPageSupport.CreateTrustedLinkStartInfo(link));
        }
        catch (Exception exception)
        {
            HandleActionFailure("Could not open a trusted ClipCord project link.", userMessage, exception);
        }
    }

    private void SetActionStatus(string text)
    {
        _actionStatusLabel.ForeColor = Color.FromArgb(126, 218, 175);
        _actionStatusLabel.Text = text;
        _actionStatusLabel.AccessibleDescription = text;
    }

    private void HandleActionFailure(string logMessage, string userMessage, Exception exception)
    {
        Log.Error(logMessage, exception);
        if (IsDisposed || Disposing) return;
        _actionStatusLabel.ForeColor = ClipCordTheme.Coral;
        _actionStatusLabel.Text = userMessage;
        MessageBox.Show(this, userMessage, "About ClipCord", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static IEnumerable<Control> EnumerateControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateControls(child)) yield return descendant;
        }
    }
}

internal sealed class AboutContentLayout : Panel
{
    private const int LogicalPadding = 16;
    private const int LogicalGap = 14;
    private const int LogicalHeroHeight = 136;
    private const int LogicalUpperCardHeight = 188;
    private const int LogicalLowerCardHeight = 244;
    private const int LogicalDisclaimerHeight = 34;
    private float _syntheticScale = 1f;
    private readonly Control _hero;
    private readonly Control _status;
    private readonly Control _diagnostics;
    private readonly Control _privacy;
    private readonly Control _credits;
    private readonly Control _disclaimer;

    internal AboutContentLayout(
        Control hero,
        Control status,
        Control diagnostics,
        Control privacy,
        Control credits,
        Control disclaimer)
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        _hero = hero;
        _status = status;
        _diagnostics = diagnostics;
        _privacy = privacy;
        _credits = credits;
        _disclaimer = disclaimer;
        Controls.AddRange([hero, status, diagnostics, privacy, credits, disclaimer]);
    }

    internal int ScaledGap => ScaleLogical(LogicalGap);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(1, proposedSize.Width);
        return new Size(width, MeasureHeight(width));
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        var padding = ScaleLogical(LogicalPadding);
        var gap = ScaleLogical(LogicalGap);
        var innerWidth = Math.Max(1, ClientSize.Width - padding * 2);
        var top = padding;
        _hero.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(LogicalHeroHeight));
        top = _hero.Bottom + gap;

        var twoColumns = innerWidth >= ScaleLogical(720);
        if (twoColumns)
        {
            var cardWidth = Math.Max(1, (innerWidth - gap) / 2);
            var secondLeft = padding + cardWidth + gap;
            _status.Bounds = new Rectangle(padding, top, cardWidth, ScaleLogical(LogicalUpperCardHeight));
            _diagnostics.Bounds = new Rectangle(
                secondLeft,
                top,
                Math.Max(1, innerWidth - cardWidth - gap),
                ScaleLogical(LogicalUpperCardHeight));
            top = _status.Bottom + gap;
            _privacy.Bounds = new Rectangle(padding, top, cardWidth, ScaleLogical(LogicalLowerCardHeight));
            _credits.Bounds = new Rectangle(
                secondLeft,
                top,
                Math.Max(1, innerWidth - cardWidth - gap),
                ScaleLogical(LogicalLowerCardHeight));
            top = _privacy.Bottom + gap;
        }
        else
        {
            _status.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(LogicalUpperCardHeight));
            top = _status.Bottom + gap;
            _diagnostics.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(LogicalUpperCardHeight));
            top = _diagnostics.Bottom + gap;
            _privacy.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(LogicalLowerCardHeight));
            top = _privacy.Bottom + gap;
            _credits.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(LogicalLowerCardHeight));
            top = _credits.Bottom + gap;
        }
        _disclaimer.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(LogicalDisclaimerHeight));
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        PerformLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        _syntheticScale = 1f;
        PerformLayout();
        if (Parent is BrandedScrollHost scrollHost) scrollHost.RefreshContentLayout();
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        if (factor.Height > 0f && float.IsFinite(factor.Height))
        {
            _syntheticScale = Math.Max(
                _syntheticScale,
                Math.Max(1f, DeviceDpi / 96f) * factor.Height);
        }
        base.ScaleControl(factor, specified);
    }

    private int MeasureHeight(int width)
    {
        var padding = ScaleLogical(LogicalPadding);
        var gap = ScaleLogical(LogicalGap);
        var innerWidth = Math.Max(1, width - padding * 2);
        var cardHeight = innerWidth >= ScaleLogical(720)
            ? ScaleLogical(LogicalUpperCardHeight + LogicalLowerCardHeight) + gap
            : ScaleLogical(LogicalUpperCardHeight * 2 + LogicalLowerCardHeight * 2) + gap * 3;
        return padding * 2 +
               ScaleLogical(LogicalHeroHeight) +
               gap +
               cardHeight +
               gap +
               ScaleLogical(LogicalDisclaimerHeight);
    }

    private int ScaleLogical(int value)
    {
        var dpiScale = Math.Max(1f, DeviceDpi / 96f);
        return Math.Max(1, (int)Math.Round(value * Math.Max(dpiScale, _syntheticScale)));
    }
}

internal sealed class AboutMetricTableLayoutPanel : TableLayoutPanel
{
    private readonly Dictionary<int, float> _logicalRows = [];
    private readonly Dictionary<int, float> _logicalColumns = [];
    private float _syntheticScale = 1f;
    private bool _applyingMetrics;

    internal AboutMetricTableLayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    internal void SetLogicalRowHeight(int row, float logicalPixels)
    {
        _logicalRows[row] = logicalPixels;
        ApplyMetrics();
    }

    internal void SetLogicalColumnWidth(int column, float logicalPixels)
    {
        _logicalColumns[column] = logicalPixels;
        ApplyMetrics();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyMetrics();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        _syntheticScale = 1f;
        ApplyMetrics();
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        if (factor.Height > 0f && float.IsFinite(factor.Height))
        {
            _syntheticScale = Math.Max(
                _syntheticScale,
                Math.Max(1f, DeviceDpi / 96f) * factor.Height);
        }
        base.ScaleControl(factor, specified);
        ApplyMetrics();
    }

    private void ApplyMetrics()
    {
        if (_applyingMetrics) return;
        _applyingMetrics = true;
        try
        {
            var scale = Math.Max(Math.Max(1f, DeviceDpi / 96f), _syntheticScale);
            foreach (var (row, logicalPixels) in _logicalRows)
            {
                if (row < 0 || row >= RowStyles.Count) continue;
                RowStyles[row].SizeType = SizeType.Absolute;
                RowStyles[row].Height = Math.Max(1f, logicalPixels * scale);
            }
            foreach (var (column, logicalPixels) in _logicalColumns)
            {
                if (column < 0 || column >= ColumnStyles.Count) continue;
                ColumnStyles[column].SizeType = SizeType.Absolute;
                ColumnStyles[column].Width = Math.Max(1f, logicalPixels * scale);
            }
        }
        finally
        {
            _applyingMetrics = false;
        }
    }
}

internal sealed class AboutHeroPanel : Panel
{
    private Size _lastRegionSize = Size.Empty;

    internal AboutHeroPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = ClipCordTheme.Shell;
        Resize += (_, _) => UpdateRegion();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        UpdateRegion();
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(ClipCordTheme.Shell);
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, 16);
        using var fill = new LinearGradientBrush(
            bounds,
            Color.FromArgb(23, 36, 58),
            Color.FromArgb(22, 27, 54),
            LinearGradientMode.Horizontal);
        eventArgs.Graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(49, 65, 94));
        eventArgs.Graphics.DrawPath(border, path);
        var accent = new Rectangle(0, 0, Math.Max(5, Width / 220), Height);
        using var accentBrush = new LinearGradientBrush(accent, ClipCordTheme.Coral, ClipCordTheme.Violet, 90f);
        eventArgs.Graphics.FillRectangle(accentBrush, accent);
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width, Height), 16);
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class AboutSectionIcon : Control
{
    private Size _lastRegionSize = Size.Empty;
    internal BrandGlyph Glyph { get; set; }

    internal AboutSectionIcon()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        BackColor = ClipCordTheme.SettingsCard;
        Resize += (_, _) => UpdateRegion();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(BackColor);
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, Math.Max(8, Math.Min(Width, Height) / 4));
        var start = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(240, 90, 84);
        var end = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(147, 64, 238);
        using var fill = new LinearGradientBrush(bounds, start, end, 140f);
        eventArgs.Graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(66, Color.White));
        eventArgs.Graphics.DrawPath(border, path);
        var inset = Math.Max(7, Math.Min(Width, Height) / 5);
        BrandGlyphControl.DrawGlyph(
            eventArgs.Graphics,
            Rectangle.Inflate(bounds, -inset, -inset),
            Glyph,
            SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White,
            Math.Max(1.4f, Math.Min(Width, Height) / 23f));
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(
            new Rectangle(0, 0, Width, Height),
            Math.Max(8, Math.Min(Width, Height) / 4));
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class AboutAvatarControl : Control
{
    private readonly string _initials;
    private readonly bool _violet;
    private float _syntheticScale = 1f;

    internal AboutAvatarControl(string initials, bool violet)
    {
        _initials = initials;
        _violet = violet;
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        BackColor = ClipCordTheme.SettingsField;
        AccessibleRole = AccessibleRole.None;
        Size = new Size(34, 34);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var side = ScaleLogical(34);
        return new Size(side, side);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        _syntheticScale = 1f;
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        if (factor.Height > 0f && float.IsFinite(factor.Height))
        {
            _syntheticScale = Math.Max(
                _syntheticScale,
                Math.Max(1f, DeviceDpi / 96f) * factor.Height);
        }
        base.ScaleControl(factor, specified);
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(BackColor);
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, Math.Max(7, Math.Min(Width, Height) / 4));
        var start = _violet ? Color.FromArgb(89, 105, 244) : Color.FromArgb(240, 90, 84);
        var end = _violet ? Color.FromArgb(152, 68, 238) : Color.FromArgb(150, 66, 245);
        using var fill = new LinearGradientBrush(bounds, start, end, 135f);
        eventArgs.Graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(58, Color.White));
        eventArgs.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _initials,
            ClipCordTheme.InterfaceFont(8.2f, FontStyle.Bold),
            ClientRectangle,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private int ScaleLogical(int value)
    {
        var scale = Math.Max(Math.Max(1f, DeviceDpi / 96f), _syntheticScale);
        return Math.Max(1, (int)Math.Round(value * scale));
    }
}
