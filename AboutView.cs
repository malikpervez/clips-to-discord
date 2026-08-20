using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal sealed class AboutView : UserControl
{
    internal const int FeatureIconLogicalSize = 30;
    internal const int ReleaseIconLogicalSize = 48;

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
        AccessibleName = "About page";
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
            Font = ClipCordTheme.InterfaceFont(7.25f),
            TextAlign = ContentAlignment.MiddleLeft,
            // The preceding 14px metric row already supplies the Figma gap.
            // A second top margin steals height from this intentionally compact
            // 13px status row and clips the text at high DPI.
            Margin = Padding.Empty,
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
        _checkUpdatesButton = CreateButton("Check for updates", "AboutCheckUpdatesButton", glyph: BrandGlyph.Refresh);
        _checkUpdatesButton.Dock = DockStyle.Fill;
        _checkUpdatesButton.MinimumSize = new Size(164, 33);
        _checkUpdatesButton.Click += (_, _) => RequestUpdateCheck();

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
    internal Button UpdateActionButton => _checkUpdatesButton;

    internal void RefreshViewport() => _scrollHost.RefreshContentLayout();

    internal bool RequestUpdateCheck()
    {
        if (_busy || !_checkUpdatesButton.Enabled || IsDisposed || Disposing) return false;
        CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_checkUpdatesButton.IsDisposed) _checkUpdatesButton.Dispose();
        base.Dispose(disposing);
    }

    private Control BuildHero(string version)
    {
        var hero = new AboutHeroPanel
        {
            Name = "AboutHero",
            AccessibleName = "ClipCord release overview",
            AccessibleRole = AccessibleRole.Grouping,
            Padding = new Padding(20, 18, 20, 18)
        };
        var layout = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(25, 0, 0, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 181));
        layout.SetLogicalColumnWidth(0, 68);
        layout.SetLogicalColumnWidth(2, 181);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var logo = new ClipCordLogoControl
        {
            Name = "AboutHeroLogo",
            LogicalSide = ReleaseIconLogicalSize,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.None,
            AccessibleName = string.Empty
        };
        layout.Controls.Add(logo, 0, 0);

        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 20, 0),
            Margin = new Padding(0, 2, 0, 2)
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
            AutoEllipsis = false,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.DisplayFont(13.2f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Name = "AboutDescriptionLabel",
            Text = "ClipCord watches your recording folder, routes new clips where you choose, " +
                   "and keeps your webhook, history and gallery on this PC.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8f),
            TextAlign = ContentAlignment.TopLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 1);

        var release = new RoundedPanel
        {
            Name = "AboutReleaseCard",
            AccessibleName = "Stable release information",
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 10,
            Margin = new Padding(0, 6, 0, 6),
            Padding = new Padding(14, 9, 14, 9),
            MinimumSize = new Size(180, 0)
        };
        var releaseLayout = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        releaseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        releaseLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        releaseLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        var releaseIdentity = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        releaseIdentity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 13));
        releaseIdentity.SetLogicalColumnWidth(0, 13);
        releaseIdentity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        releaseIdentity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        releaseIdentity.Controls.Add(new Label
        {
            Name = "AboutReleaseStatusDot",
            Text = "●",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(50, 190, 126),
            Font = ClipCordTheme.InterfaceFont(7.8f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleRole = AccessibleRole.None,
            UseMnemonic = false,
            Margin = Padding.Empty
        }, 0, 0);
        releaseIdentity.Controls.Add(new Label
        {
            Name = "AboutReleaseVersionLabel",
            Text = $"Stable  ·  v{version}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.InterfaceFont(8.7f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            UseMnemonic = false,
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);
        releaseLayout.Controls.Add(releaseIdentity, 0, 0);
        releaseLayout.Controls.Add(_updateStateLabel, 0, 1);
        release.Controls.Add(releaseLayout);

        layout.Controls.Add(copy, 1, 0);
        layout.Controls.Add(release, 2, 0);
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
        body.ColumnCount = 1;
        body.RowCount = 4;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) body.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        body.Controls.Add(CreateStatusItem("AboutWatcherStatusItem", _watcherLabel, _watcherDetailLabel, StatusAccent.Green), 0, 0);
        body.Controls.Add(CreateStatusItem("AboutRoutingStatusItem", _routeLabel, _routeDetailLabel, StatusAccent.Violet), 0, 1);
        body.Controls.Add(CreateStatusItem("AboutStartupStatusItem", _startupLabel, _startupDetailLabel, StatusAccent.Green), 0, 2);
        body.Controls.Add(CreateStatusItem("AboutInstallationStatusItem", _installationLabel, _installationDetailLabel, StatusAccent.Blue, drawDivider: false), 0, 3);
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
        body.ColumnCount = 3;
        body.RowCount = 5;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        body.SetLogicalColumnWidth(1, 8);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var (row, height) in new[] { (0, 33f), (1, 8f), (2, 33f), (3, 14f), (4, 13f) })
        {
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            body.SetLogicalRowHeight(row, height);
        }
        var openLogs = CreateButton("Open logs", "AboutOpenLogsButton");
        openLogs.Click += (_, _) => OpenLogs();
        var copy = CreateButton("Copy diagnostics", "AboutCopyDiagnosticsButton");
        copy.Click += (_, _) => CopyDiagnostics();
        var data = CreateButton("Open data folder", "AboutOpenDataFolderButton");
        data.Click += (_, _) => OpenDataFolder();
        var report = CreateButton("Report a problem", "AboutReportProblemButton");
        report.Click += (_, _) => OpenLink(AboutLink.ReportProblem, "Could not open the issue form.");
        body.Controls.Add(openLogs, 0, 0);
        body.Controls.Add(copy, 2, 0);
        body.Controls.Add(data, 0, 2);
        body.Controls.Add(report, 2, 2);
        body.Controls.Add(_actionStatusLabel, 0, 4);
        body.SetColumnSpan(_actionStatusLabel, 3);
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
        body.RowCount = 6;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var privacyRows = new[] { 26f, 26f, 26f, 34f, 53f, 33f };
        for (var index = 0; index < privacyRows.Length; index++)
        {
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, privacyRows[index]));
            body.SetLogicalRowHeight(index, privacyRows[index]);
        }
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
        var links = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        links.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        links.SetLogicalColumnWidth(1, 8);
        links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        links.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var privacy = CreateButton("Privacy details", "AboutPrivacyButton");
        privacy.Click += (_, _) => OpenLink(AboutLink.Privacy, "Could not open the privacy guide.");
        var security = CreateButton("Security design", "AboutSecurityButton");
        security.Click += (_, _) => OpenLink(AboutLink.SecurityDesign, "Could not open the security guide.");
        links.Controls.Add(privacy, 0, 0);
        links.Controls.Add(security, 2, 0);
        body.Controls.Add(links, 0, 5);
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
        body.RowCount = 5;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var creditRows = new[] { 54f, 54f, 54f, 34f, 33f };
        for (var index = 0; index < creditRows.Length; index++)
        {
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, creditRows[index]));
            body.SetLogicalRowHeight(index, creditRows[index]);
        }
        body.Controls.Add(CreateCredit(
            "AboutDixonCredit", "DY", "Dixon Yamada", "Certified Looter", ClipCordTheme.Coral, drawDivider: true), 0, 0);
        body.Controls.Add(CreateCredit(
            "AboutPapiCredit",
            "PJ",
            "Papi Jawn",
            "Certified Shooter · LeBron’s Legacy",
            ClipCordTheme.Violet,
            drawDivider: true), 0, 1);
        body.Controls.Add(CreateCredit(
            "AboutTwspeakmanCredit",
            "TW",
            "twspeakman",
            "The Bald Headed Demon",
            Color.FromArgb(245, 166, 35),
            drawDivider: false), 0, 2);
        var links = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var index = 0; index < 7; index++)
        {
            if (index % 2 == 0) links.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            else
            {
                links.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
                links.SetLogicalColumnWidth(index, 8);
            }
        }
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
        links.Controls.Add(releases, 2, 0);
        links.Controls.Add(roadmap, 4, 0);
        links.Controls.Add(licenses, 6, 0);
        body.Controls.Add(links, 0, 4);
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
            CornerRadius = 12,
            Padding = new Padding(20, 18, 20, 18),
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
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        layout.SetLogicalRowHeight(0, 47);
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
        icon.Margin = new Padding(0, 2, 11, 0);
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
            Font = ClipCordTheme.InterfaceFont(10.3f, FontStyle.Bold),
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
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.7f),
            TextAlign = ContentAlignment.TopLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 1);
        heading.Controls.Add(copy, 1, 0);
        return heading;
    }

    private static AboutSectionIcon CreateSectionIcon(BrandGlyph glyph, int size)
    {
        return new AboutSectionIcon(size)
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Glyph = glyph,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.None,
            AccessibleName = string.Empty
        };
    }

    private enum StatusAccent
    {
        Green,
        Violet,
        Blue
    }

    private static Control CreateStatusItem(
        string name,
        Label value,
        Label detail,
        StatusAccent accent,
        bool drawDivider = true)
    {
        var item = new Panel
        {
            Name = name,
            AccessibleName = value.AccessibleName,
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 6),
            Padding = Padding.Empty
        };
        if (drawDivider)
        {
            item.Paint += (_, eventArgs) =>
            {
                using var pen = new Pen(ClipCordTheme.BorderDefault);
                eventArgs.Graphics.DrawLine(pen, 0, item.Height - 1, item.Width, item.Height - 1);
            };
        }
        var layout = new AboutMetricTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        layout.SetLogicalColumnWidth(0, 18);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        layout.SetLogicalColumnWidth(2, 148);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var accentColor = accent switch
        {
            StatusAccent.Violet => ClipCordTheme.Violet,
            StatusAccent.Blue => Color.FromArgb(91, 148, 255),
            _ => Color.FromArgb(55, 207, 133)
        };
        layout.Controls.Add(new Label
        {
            Text = "●",
            Dock = DockStyle.Fill,
            ForeColor = accentColor,
            Font = ClipCordTheme.InterfaceFont(7.3f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleRole = AccessibleRole.None
        }, 0, 0);
        value.TextAlign = ContentAlignment.MiddleLeft;
        detail.TextAlign = ContentAlignment.MiddleRight;
        layout.Controls.Add(value, 1, 0);
        layout.Controls.Add(detail, 2, 0);
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
        statement.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.Check,
            IconColor = Color.FromArgb(55, 207, 133),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Size = new Size(10, 10),
            Margin = new Padding(2, 6, 0, 0)
        }, 0, 0);
        statement.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.2f),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 2, 0, 0),
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);
        return statement;
    }

    private static Control CreateCredit(
        string name,
        string initials,
        string person,
        string title,
        Color accent,
        bool drawDivider)
    {
        var credit = new Panel
        {
            Name = name,
            AccessibleName = $"{person}, {title}",
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 4)
        };
        if (drawDivider) credit.Paint += (_, eventArgs) =>
        {
            using var pen = new Pen(ClipCordTheme.BorderDefault);
            eventArgs.Graphics.DrawLine(pen, 0, credit.Height - 1, credit.Width, credit.Height - 1);
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
        var avatar = new AboutAvatarControl(initials, accent)
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
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.4f),
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
        Font = ClipCordTheme.InterfaceFont(fontSize),
        LeadingGlyph = glyph,
        Margin = Padding.Empty,
        TabStop = true,
        MinimumSize = new Size(0, 33)
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
    private const int LogicalHorizontalPadding = 28;
    private const int LogicalTopPadding = 4;
    private const int LogicalBottomPadding = 0;
    private const int LogicalGap = 14;
    private const int LogicalHeroHeight = 105;
    private const int LogicalNarrowHeroHeight = 133;
    private const int LogicalStatusHeight = 215;
    private const int LogicalDiagnosticsHeight = 184;
    private const int LogicalPrivacyHeight = 281;
    private const int LogicalCreditsHeight = 312;
    private const int LogicalDisclaimerHeight = 30;
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
        var cardPadding = new Padding(
            ScaleLogical(20),
            ScaleLogical(18),
            ScaleLogical(20),
            ScaleLogical(18));
        foreach (var card in new[] { _hero, _status, _diagnostics, _privacy, _credits })
        {
            if (card.Padding != cardPadding) card.Padding = cardPadding;
        }
        var horizontalPadding = ScaleLogical(LogicalHorizontalPadding);
        var topPadding = ScaleLogical(LogicalTopPadding);
        var gap = ScaleLogical(LogicalGap);
        var innerWidth = Math.Max(1, ClientSize.Width - horizontalPadding * 2);
        var top = topPadding;
        var heroHeight = GetHeroHeight(innerWidth);
        _hero.Bounds = new Rectangle(horizontalPadding, top, innerWidth, heroHeight);
        top = _hero.Bottom + gap;

        var twoColumns = innerWidth >= ScaleLogical(720);
        if (twoColumns)
        {
            var cardWidth = Math.Max(1, (innerWidth - gap) / 2);
            var secondLeft = horizontalPadding + cardWidth + gap;
            var columnTop = top;
            _status.Bounds = new Rectangle(horizontalPadding, columnTop, cardWidth, ScaleLogical(LogicalStatusHeight));
            _diagnostics.Bounds = new Rectangle(secondLeft, columnTop, Math.Max(1, innerWidth - cardWidth - gap), ScaleLogical(LogicalDiagnosticsHeight));

            _privacy.Bounds = new Rectangle(
                horizontalPadding,
                _status.Bottom + gap,
                cardWidth,
                ScaleLogical(LogicalPrivacyHeight));
            _credits.Bounds = new Rectangle(
                secondLeft,
                _diagnostics.Bottom + gap,
                Math.Max(1, innerWidth - cardWidth - gap),
                ScaleLogical(LogicalCreditsHeight));
            top = Math.Max(_privacy.Bottom, _credits.Bottom) + gap;
        }
        else
        {
            _status.Bounds = new Rectangle(horizontalPadding, top, innerWidth, ScaleLogical(LogicalStatusHeight));
            top = _status.Bottom + gap;
            _diagnostics.Bounds = new Rectangle(horizontalPadding, top, innerWidth, ScaleLogical(LogicalDiagnosticsHeight));
            top = _diagnostics.Bottom + gap;
            _privacy.Bounds = new Rectangle(horizontalPadding, top, innerWidth, ScaleLogical(LogicalPrivacyHeight));
            top = _privacy.Bottom + gap;
            _credits.Bounds = new Rectangle(horizontalPadding, top, innerWidth, ScaleLogical(LogicalCreditsHeight));
            top = _credits.Bottom + gap;
        }
        _disclaimer.Bounds = new Rectangle(horizontalPadding, top, innerWidth, ScaleLogical(LogicalDisclaimerHeight));
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
        var horizontalPadding = ScaleLogical(LogicalHorizontalPadding);
        var topPadding = ScaleLogical(LogicalTopPadding);
        var bottomPadding = ScaleLogical(LogicalBottomPadding);
        var gap = ScaleLogical(LogicalGap);
        var innerWidth = Math.Max(1, width - horizontalPadding * 2);
        var cardHeight = innerWidth >= ScaleLogical(720)
            ? Math.Max(
                ScaleLogical(LogicalStatusHeight + LogicalPrivacyHeight),
                ScaleLogical(LogicalDiagnosticsHeight + LogicalCreditsHeight)) + gap
            : ScaleLogical(LogicalStatusHeight + LogicalDiagnosticsHeight + LogicalPrivacyHeight + LogicalCreditsHeight) + gap * 3;
        return topPadding + bottomPadding +
               GetHeroHeight(innerWidth) +
               gap +
               cardHeight +
               gap +
               ScaleLogical(LogicalDisclaimerHeight);
    }

    private int GetHeroHeight(int innerWidth) =>
        ScaleLogical(innerWidth >= ScaleLogical(720) ? LogicalHeroHeight : LogicalNarrowHeroHeight);

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
        using var fill = new SolidBrush(ClipCordTheme.SurfaceRaised);
        eventArgs.Graphics.FillPath(fill, path);
        using var border = new Pen(ClipCordTheme.BorderDefault);
        eventArgs.Graphics.DrawPath(border, path);
        var accentWidth = Math.Max(2, (int)Math.Round(DeviceDpi / 96f * 2));
        var accent = new Rectangle(Math.Max(16, Width / 44), Math.Max(16, Height / 5), accentWidth, Math.Max(1, Height * 3 / 5));
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
    private readonly int _logicalSide;
    private Size _lastRegionSize = Size.Empty;
    private float _syntheticScale = 1f;
    internal BrandGlyph Glyph { get; set; }

    internal AboutSectionIcon(int logicalSide)
    {
        _logicalSide = Math.Max(1, logicalSide);
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        BackColor = ClipCordTheme.SettingsCard;
        Size = new Size(_logicalSide, _logicalSide);
        Resize += (_, _) => UpdateRegion();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var side = ScaleLogical(_logicalSide);
        return new Size(side, side);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        Size = GetPreferredSize(Size.Empty);
        UpdateRegion();
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
        using var path = RoundedPanel.CreateRoundedPath(bounds, Math.Max(6, Math.Min(Width, Height) / 4));
        var accent = SystemInformation.HighContrast
            ? SystemColors.Highlight
            : Glyph switch
            {
                BrandGlyph.Diagnostics => Color.FromArgb(91, 148, 255),
                BrandGlyph.Credits => ClipCordTheme.Violet,
                _ => Color.FromArgb(55, 207, 133)
            };
        using var fill = new SolidBrush(Color.FromArgb(28, accent));
        eventArgs.Graphics.FillPath(fill, path);
        using var border = new Pen(accent);
        eventArgs.Graphics.DrawPath(border, path);
        var inset = Math.Max(6, Math.Min(Width, Height) / 4);
        BrandGlyphControl.DrawGlyph(
            eventArgs.Graphics,
            Rectangle.Inflate(bounds, -inset, -inset),
            Glyph,
            SystemInformation.HighContrast ? SystemColors.HighlightText : accent,
            Math.Max(1.2f, Math.Min(Width, Height) / 24f));
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(
            new Rectangle(0, 0, Width, Height),
            Math.Max(6, Math.Min(Width, Height) / 4));
        Region?.Dispose();
        Region = new Region(path);
    }

    private int ScaleLogical(int value)
    {
        var dpiScale = Math.Max(1f, DeviceDpi / 96f);
        return Math.Max(1, (int)Math.Round(value * Math.Max(dpiScale, _syntheticScale)));
    }
}

internal sealed class AboutAvatarControl : Control
{
    private readonly string _initials;
    private readonly Color _accent;
    private float _syntheticScale = 1f;

    internal AboutAvatarControl(string initials, Color accent)
    {
        _initials = initials;
        _accent = accent;
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        BackColor = ClipCordTheme.SettingsCard;
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
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var fill = new SolidBrush(Color.FromArgb(16, _accent));
        eventArgs.Graphics.FillPath(fill, path);
        using var border = new Pen(_accent);
        eventArgs.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _initials,
            ClipCordTheme.InterfaceFont(8.2f, FontStyle.Bold),
            ClientRectangle,
            _accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private int ScaleLogical(int value)
    {
        var scale = Math.Max(Math.Max(1f, DeviceDpi / 96f), _syntheticScale);
        return Math.Max(1, (int)Math.Round(value * scale));
    }
}
