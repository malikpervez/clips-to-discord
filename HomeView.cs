namespace ClipsToDiscord;

/// <summary>
/// Read-only dashboard for the current ClipCord configuration and recent local history.
/// Expensive archive discovery is deliberately activated and cancelled by the owning shell.
/// </summary>
internal sealed class HomeView : UserControl
{
    private const int RecentActivityLimit = 4;
    private readonly ActivityHistoryStore _history;
    private readonly Func<string>? _watcherStatusProvider;
    private readonly Func<bool> _discordRunningProvider;
    private readonly Func<string, CancellationToken, GallerySnapshot> _galleryScanner;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly BrandedScrollHost _scrollHost;
    private readonly HomeContentLayout _content;
    private readonly HomeStatusPill _watcherPill;
    private readonly Label _watcherDetailLabel;
    private readonly Label _sourceNameLabel;
    private readonly Label _sourcePathLabel;
    private readonly Label _captureSourceLabel;
    private readonly Label _destinationNameLabel;
    private readonly Label _destinationDetailLabel;
    private readonly Label _routeLabel;
    private readonly Label _recentCountLabel;
    private readonly Label _recentCountDetailLabel;
    private readonly Label _recentUploadsLabel;
    private readonly Label _recentUploadsDetailLabel;
    private readonly Label _archiveCountLabel;
    private readonly Label _archiveDetailLabel;
    private readonly BufferedTableLayoutPanel _recentRows;
    private readonly OutlineButton _openClipsFolderButton;
    private readonly OutlineButton _openUploadedFolderButton;
    private readonly OutlineButton _openLocalOnlyFolderButton;
    private readonly OutlineButton _checkUpdatesButton;
    private AppSettings _settings;
    private IDisposable? _activitySubscription;
    private CancellationTokenSource? _archiveCancellation;
    private bool _active;
    private bool _updateBusy;
    private int _archiveGeneration;

    internal event EventHandler? NavigateToActivityRequested;
    internal event EventHandler? OpenClipsFolderRequested;
    internal event EventHandler? OpenUploadedFolderRequested;
    internal event EventHandler? OpenLocalOnlyFolderRequested;
    internal event EventHandler? OpenLogsRequested;
    internal event EventHandler? CheckUpdatesRequested;

    internal HomeView(
        AppSettings settings,
        ActivityHistoryStore history,
        Func<string>? watcherStatusProvider = null,
        Func<bool>? discordRunningProvider = null,
        Func<string, CancellationToken, GallerySnapshot>? galleryScanner = null,
        Func<DateTime>? utcNowProvider = null,
        bool showPageHeader = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);

        _settings = settings;
        _history = history;
        _watcherStatusProvider = watcherStatusProvider;
        _discordRunningProvider = discordRunningProvider ?? DiscordDetector.IsRunning;
        _galleryScanner = galleryScanner ?? ((folder, cancellationToken) =>
            GalleryCatalog.Scan(folder, cancellationToken));
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);

        Name = "HomeView";
        AccessibleName = "ClipCord Home";
        AccessibleRole = AccessibleRole.Pane;
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.SurfaceBase;
        Font = ClipCordTheme.InterfaceFont(9.5f);

        _watcherPill = new HomeStatusPill
        {
            Name = "HomeWatcherStatusPill",
            AccessibleName = "Watcher status",
            Size = new Size(ScaleUi(116), ScaleUi(28)),
            MinimumSize = new Size(ScaleUi(116), ScaleUi(28)),
            Margin = Padding.Empty
        };
        _watcherDetailLabel = CreateMetadataLabel("HomeWatcherDetailLabel", ContentAlignment.MiddleRight);
        _sourceNameLabel = CreateValueLabel("HomeSourceNameLabel", 13f);
        _sourcePathLabel = CreateMetadataLabel("HomeSourcePathLabel");
        _captureSourceLabel = CreateMetadataLabel("HomeCaptureSourceLabel");
        _destinationNameLabel = CreateValueLabel("HomeDestinationNameLabel", 13f);
        _destinationDetailLabel = CreateMetadataLabel("HomeDestinationDetailLabel");
        _routeLabel = CreateMetadataLabel("HomeRouteLabel", ContentAlignment.MiddleCenter);
        _recentCountLabel = CreateMetricValueLabel("HomeRecentActivityCountLabel");
        _recentCountDetailLabel = CreateMetadataLabel("HomeRecentActivityDetailLabel");
        _recentUploadsLabel = CreateMetricValueLabel("HomeRecentUploadsCountLabel");
        _recentUploadsDetailLabel = CreateMetadataLabel("HomeRecentUploadsDetailLabel");
        _archiveCountLabel = CreateMetricValueLabel("HomeLocalArchiveCountLabel");
        _archiveDetailLabel = CreateMetadataLabel("HomeLocalArchiveDetailLabel");
        _recentRows = new BufferedTableLayoutPanel
        {
            Name = "HomeRecentActivityRows",
            AccessibleName = "Recent clip activity",
            AccessibleRole = AccessibleRole.List,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = RecentActivityLimit,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _recentRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < RecentActivityLimit; index++)
        {
            _recentRows.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        }

        _openClipsFolderButton = CreateActionButton(
            "Open clips folder",
            "HomeOpenClipsFolderButton",
            BrandGlyph.Folder);
        _openClipsFolderButton.Click += (_, _) => OpenClipsFolderRequested?.Invoke(this, EventArgs.Empty);
        _openUploadedFolderButton = CreateActionButton(
            "Open uploaded folder",
            "HomeOpenUploadedFolderButton",
            BrandGlyph.Folder);
        _openUploadedFolderButton.Click += (_, _) => OpenUploadedFolderRequested?.Invoke(this, EventArgs.Empty);
        _openLocalOnlyFolderButton = CreateActionButton(
            "Open local-only folder",
            "HomeOpenLocalOnlyFolderButton",
            BrandGlyph.Folder);
        _openLocalOnlyFolderButton.Click += (_, _) => OpenLocalOnlyFolderRequested?.Invoke(this, EventArgs.Empty);
        var openLogsButton = CreateActionButton("Open logs", "HomeOpenLogsButton", BrandGlyph.External);
        openLogsButton.Click += (_, _) => OpenLogsRequested?.Invoke(this, EventArgs.Empty);
        _checkUpdatesButton = CreateActionButton(
            "Check for updates",
            "HomeCheckUpdatesButton",
            BrandGlyph.Refresh);
        _checkUpdatesButton.Click += (_, _) =>
        {
            if (!_updateBusy && _checkUpdatesButton.Enabled)
            {
                CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        var header = showPageHeader ? BuildHeader() : null;
        var hero = BuildRouteHero();
        var metrics = BuildMetrics();
        var bottom = BuildBottomRow(openLogsButton, openClipsFolderButton: null);
        _content = new HomeContentLayout(hero, metrics, bottom)
        {
            Name = "HomeContent",
            AccessibleName = "ClipCord dashboard",
            AccessibleRole = AccessibleRole.Pane,
            BackColor = ClipCordTheme.SurfaceBase
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "HomeScrollHost",
            AccessibleName = "ClipCord Home dashboard",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceBase,
            Content = _content
        };

        var root = new BufferedTableLayoutPanel
        {
            Name = "HomeRoot",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = showPageHeader ? 2 : 1,
            BackColor = ClipCordTheme.SurfaceBase,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (showPageHeader)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(header!, 0, 0);
            root.Controls.Add(_scrollHost, 0, 1);
        }
        else
        {
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(_scrollHost, 0, 0);
        }
        Controls.Add(root);

        ApplySettings(settings);
        RenderActivitySnapshot(GetHistorySnapshot());
        SetArchiveUnavailable("Open Home to scan the Local-only archive.");
        RefreshRuntimeStatus();
    }

    internal bool IsViewActive => _active;
    internal bool HasOverflow => _scrollHost.HasOverflow;

    /// <summary>Starts live activity observation and one cancellable archive scan.</summary>
    internal void ActivateView()
    {
        if (IsDisposed || Disposing) return;
        RefreshRuntimeStatus();
        RenderActivitySnapshot(GetHistorySnapshot());
        if (_active) return;

        _active = true;
        EnsureActivitySubscription();
        StartArchiveScan();
    }

    /// <summary>Stops all Home-only observation and scanning while another page is visible.</summary>
    internal void DeactivateView()
    {
        _active = false;
        DisposeActivitySubscription();
        CancelArchiveScan();
    }

    internal void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var folderChanged = !_settings.ClipsFolder.Equals(settings.ClipsFolder, StringComparison.OrdinalIgnoreCase);
        _settings = settings;

        var trimmedFolder = settings.ClipsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourceName = string.IsNullOrWhiteSpace(trimmedFolder)
            ? "Clips folder not configured"
            : Path.GetFileName(trimmedFolder);
        _sourceNameLabel.Text = string.IsNullOrWhiteSpace(sourceName) ? "Clips folder" : sourceName;
        _sourcePathLabel.Text = string.IsNullOrWhiteSpace(settings.ClipsFolder)
            ? "Choose a recording folder in Settings."
            : settings.ClipsFolder;
        _captureSourceLabel.Text = AppSettings.DescribeCaptureSource(settings.CaptureSource);

        if (settings.UploadToDiscord)
        {
            _destinationNameLabel.Text = "Discord webhook";
            _destinationDetailLabel.Text = string.IsNullOrWhiteSpace(settings.WebhookUrl)
                ? "Finish Discord setup before uploads can begin."
                : $"Posts as {AppSettings.NormalizeUploaderName(settings.UploaderName)} · webhook encrypted locally";
            _routeLabel.Text = "Discord route";
        }
        else
        {
            _destinationNameLabel.Text = "Local-only archive";
            _destinationDetailLabel.Text = "New clips stay on this PC; no Discord request is made.";
            _routeLabel.Text = "Local-only route";
        }

        var canOpenClips = Directory.Exists(settings.ClipsFolder);
        _openClipsFolderButton.Enabled = canOpenClips;
        _openUploadedFolderButton.Enabled = canOpenClips;
        _openLocalOnlyFolderButton.Enabled = canOpenClips;

        if (folderChanged && _active) StartArchiveScan();
    }

    internal void RefreshRuntimeStatus()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            var presentation = AboutPageSupport.NormalizeWatcherStatus(
                _watcherStatusProvider?.Invoke(),
                _discordRunningProvider());
            _watcherPill.Apply(presentation);
            _watcherDetailLabel.Text = presentation.Detail;
            _watcherDetailLabel.AccessibleDescription = presentation.Detail;
        }
        catch (Exception exception)
        {
            Log.Error("Could not refresh the Home watcher status.", exception);
            _watcherPill.Apply(new AboutWatcherPresentation(
                AboutWatcherState.Unavailable,
                "Status unavailable",
                "Live watcher status is temporarily unavailable"));
            _watcherDetailLabel.Text = "Live watcher status is temporarily unavailable";
        }
    }

    internal void SetUpdateBusy(bool busy, bool updateChecksAvailable)
    {
        _updateBusy = busy;
        if (!IsDisposed && !Disposing)
        {
            _checkUpdatesButton.Enabled = !busy && updateChecksAvailable;
            _checkUpdatesButton.Text = busy ? "Checking for updates…" : "Check for updates";
        }
    }

    internal void RefreshViewport()
    {
        PerformLayout();
        _scrollHost.RefreshContentLayout();
    }

    internal Button HeaderActionButton => _openClipsFolderButton;

    private Control BuildHeader()
    {
        var header = new BufferedTableLayoutPanel
        {
            Name = "HomeHeader",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = ClipCordTheme.SurfaceBase,
            Margin = Padding.Empty,
            Padding = new Padding(28, 12, 28, 8)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        copy.Controls.Add(new Label
        {
            Name = "HomeHeading",
            Text = "Home",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.DisplayFont(16f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Name = "HomeSubheading",
            Text = "Everything ClipCord is doing right now",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.6f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 1);
        _openClipsFolderButton.Anchor = AnchorStyles.Right;
        _openClipsFolderButton.MinimumSize = new Size(158, 36);
        header.Controls.Add(copy, 0, 0);
        header.Controls.Add(_openClipsFolderButton, 1, 0);
        return header;
    }

    private RoundedPanel BuildRouteHero()
    {
        var hero = CreateCard("HomeRouteHero", "Current clip route", 22);
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(32)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var status = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _watcherPill.Anchor = AnchorStyles.Left;
        _watcherDetailLabel.Dock = DockStyle.Fill;
        status.Controls.Add(_watcherPill, 0, 0);
        status.Controls.Add(_watcherDetailLabel, 1, 0);

        var pipeline = new BufferedTableLayoutPanel
        {
            Name = "HomeRoutePipeline",
            AccessibleName = "Watched folder routes to destination",
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, ScaleUi(18), 0, 0)
        };
        pipeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pipeline.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(120)));
        pipeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pipeline.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pipeline.Controls.Add(BuildEndpoint(
            "HomeSourceEndpoint",
            "WATCHED FOLDER",
            _sourceNameLabel,
            _sourcePathLabel,
            _captureSourceLabel,
            BrandGlyph.Folder,
            ClipCordTheme.TextTertiary), 0, 0);
        pipeline.Controls.Add(new HomeRouteArrow
        {
            Name = "HomeRouteArrow",
            AccessibleName = "Routes to",
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = new Padding(ScaleUi(8), 0, ScaleUi(8), 0)
        }, 1, 0);
        pipeline.Controls.Add(BuildEndpoint(
            "HomeDestinationEndpoint",
            "DESTINATION",
            _destinationNameLabel,
            _destinationDetailLabel,
            _routeLabel,
            BrandGlyph.Upload,
            ClipCordTheme.Violet), 2, 0);

        layout.Controls.Add(status, 0, 0);
        layout.Controls.Add(pipeline, 0, 1);
        hero.Controls.Add(layout);
        return hero;
    }

    private Control BuildMetrics()
    {
        var metrics = new BufferedTableLayoutPanel
        {
            Name = "HomeMetrics",
            AccessibleName = "Recent local metrics",
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333f));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333f));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3334f));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metrics.Controls.Add(CreateMetricCard(
            "HomeRecentActivityMetric",
            "RECENT ACTIVITY",
            _recentCountLabel,
            _recentCountDetailLabel,
            BrandGlyph.Activity,
            Color.FromArgb(91, 147, 255)), 0, 0);
        metrics.Controls.Add(CreateMetricCard(
            "HomeRecentUploadsMetric",
            "RECENT UPLOADS",
            _recentUploadsLabel,
            _recentUploadsDetailLabel,
            BrandGlyph.Upload,
            ClipCordTheme.Violet), 1, 0);
        metrics.Controls.Add(CreateMetricCard(
            "HomeLocalArchiveMetric",
            "LOCAL-ONLY ARCHIVE",
            _archiveCountLabel,
            _archiveDetailLabel,
            BrandGlyph.Film,
            ClipCordTheme.Coral), 2, 0);
        return metrics;
    }

    private Control BuildBottomRow(OutlineButton openLogsButton, OutlineButton? openClipsFolderButton)
    {
        return new HomeBottomLayout(BuildRecentActivityCard(), BuildShortcutsCard(openLogsButton, openClipsFolderButton))
        {
            Name = "HomeBottomRow",
            AccessibleName = "Recent activity and shortcuts",
            AccessibleRole = AccessibleRole.Grouping,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
    }

    private RoundedPanel BuildRecentActivityCard()
    {
        var card = CreateCard("HomeRecentActivityCard", "Recent activity", 18);
        card.Margin = Padding.Empty;
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(CreateCardHeading("Recent activity", "HomeRecentActivityHeading"), 0, 0);
        var viewAll = new Button
        {
            Name = "HomeViewAllActivityButton",
            AccessibleName = "View all activity",
            AccessibleRole = AccessibleRole.PushButton,
            Text = "View all  ›",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(176, 128, 255),
            Font = ClipCordTheme.InterfaceFont(8.6f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
            TabStop = true
        };
        viewAll.Click += (_, _) => NavigateToActivityRequested?.Invoke(this, EventArgs.Empty);
        heading.Controls.Add(viewAll, 1, 0);
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(_recentRows, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private RoundedPanel BuildShortcutsCard(OutlineButton openLogsButton, OutlineButton? openClipsFolderButton)
    {
        var card = CreateCard("HomeShortcutsCard", "Shortcuts", 16);
        card.Margin = Padding.Empty;
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = openClipsFolderButton is null ? 6 : 7,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(28)));
        var actionCount = openClipsFolderButton is null ? 4 : 5;
        for (var index = 0; index < actionCount; index++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(40)));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(42)));
        layout.Controls.Add(CreateCardHeading("Shortcuts", "HomeShortcutsHeading"), 0, 0);
        var row = 1;
        if (openClipsFolderButton is not null) layout.Controls.Add(openClipsFolderButton, 0, row++);
        layout.Controls.Add(_openUploadedFolderButton, 0, row++);
        layout.Controls.Add(_openLocalOnlyFolderButton, 0, row++);
        layout.Controls.Add(openLogsButton, 0, row++);
        layout.Controls.Add(_checkUpdatesButton, 0, row++);
        var privacy = new BufferedTableLayoutPanel
        {
            Name = "HomePrivacyRow",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(2, 8, 2, 0),
            Padding = Padding.Empty
        };
        privacy.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(22)));
        privacy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        privacy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        privacy.Controls.Add(new FigmaIconControl
        {
            Name = "HomePrivacyIcon",
            Asset = FigmaIconAsset.Shield,
            IconColor = Color.FromArgb(49, 177, 113),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Size = new Size(ScaleUi(15), ScaleUi(15)),
            Margin = new Padding(0, ScaleUi(2), ScaleUi(7), 0)
        }, 0, 0);
        privacy.Controls.Add(new Label
        {
            Name = "HomePrivacyNote",
            Text = "History and Gallery stay on this PC. Your webhook is encrypted locally and used only for configured Discord uploads.",
            Dock = DockStyle.Fill,
            AutoEllipsis = false,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);
        layout.Controls.Add(privacy, 0, row);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildEndpoint(
        string name,
        string caption,
        Label value,
        Label detail,
        Label route,
        BrandGlyph glyph,
        Color accent)
    {
        var endpoint = new BufferedTableLayoutPanel
        {
            Name = name,
            AccessibleName = caption,
            AccessibleRole = AccessibleRole.Grouping,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        endpoint.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(22)));
        endpoint.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        endpoint.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(20)));
        endpoint.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(26)));
        endpoint.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(22)));
        endpoint.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        endpoint.Controls.Add(new BrandGlyphControl
        {
            Glyph = glyph,
            GlyphColor = accent,
            StrokeWidth = 1.6f,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 7, 2)
        }, 0, 0);
        endpoint.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.2f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);
        value.Dock = DockStyle.Fill;
        detail.Dock = DockStyle.Fill;
        route.Dock = DockStyle.Fill;
        endpoint.Controls.Add(value, 0, 1);
        endpoint.SetColumnSpan(value, 2);
        endpoint.Controls.Add(detail, 0, 2);
        endpoint.SetColumnSpan(detail, 2);
        var destination = caption == "DESTINATION";
        var routeChip = new RoundedPanel
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            BackColor = destination ? ClipCordTheme.VioletMuted : ClipCordTheme.SurfaceControl,
            BorderColor = destination ? ClipCordTheme.Violet : ClipCordTheme.BorderStrong,
            CornerRadius = ScaleUi(7),
            Margin = new Padding(0, ScaleUi(3), 0, 0),
            Padding = new Padding(ScaleUi(7), 0, ScaleUi(7), 0),
            Size = new Size(ScaleUi(destination ? 110 : 118), ScaleUi(26)),
            MinimumSize = new Size(ScaleUi(destination ? 110 : 118), ScaleUi(26)),
            MaximumSize = new Size(ScaleUi(destination ? 110 : 118), ScaleUi(26)),
            AccessibleName = destination ? "Current route" : "Capture source",
            AccessibleRole = AccessibleRole.StaticText
        };
        var routeChipLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        routeChipLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(13)));
        routeChipLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        routeChipLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        routeChipLayout.Controls.Add(new HomeRouteDot
        {
            Accent = destination ? ClipCordTheme.Violet : Color.FromArgb(91, 147, 255),
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        }, 0, 0);
        route.ForeColor = ClipCordTheme.TextPrimary;
        route.TextAlign = ContentAlignment.MiddleLeft;
        routeChipLayout.Controls.Add(route, 1, 0);
        routeChip.Controls.Add(routeChipLayout);
        endpoint.Controls.Add(routeChip, 0, 3);
        endpoint.SetColumnSpan(routeChip, 2);
        return endpoint;
    }

    private RoundedPanel CreateMetricCard(
        string name,
        string caption,
        Label value,
        Label detail,
        BrandGlyph glyph,
        Color accent)
    {
        var card = CreateCard(name, caption, 14);
        card.Margin = name switch
        {
            "HomeRecentActivityMetric" => new Padding(0, 0, ScaleUi(7), 0),
            "HomeRecentUploadsMetric" => new Padding(ScaleUi(7), 0, ScaleUi(7), 0),
            _ => new Padding(ScaleUi(7), 0, 0, 0)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(38)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(18)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(30)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        value.Dock = DockStyle.Fill;
        detail.Dock = DockStyle.Fill;
        layout.Controls.Add(value, 0, 1);
        layout.Controls.Add(detail, 0, 2);
        layout.Controls.Add(new HomeMetricGlyph
        {
            Glyph = glyph,
            Accent = accent,
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 0, 0, 6)
        }, 1, 1);
        layout.SetRowSpan(layout.GetControlFromPosition(1, 1)!, 2);
        card.Controls.Add(layout);
        return card;
    }

    private void RenderActivitySnapshot(ClipActivitySnapshot snapshot)
    {
        if (IsDisposed || Disposing) return;
        _recentCountLabel.Text = snapshot.Entries.Count.ToString("N0");
        _recentCountDetailLabel.Text = $"Latest local history · up to {ActivityHistoryStore.MaximumEntries:N0} entries";

        var uploaded = snapshot.Entries
            .Where(entry => entry.Route == ClipActivityRoute.Uploaded &&
                            entry.State is ClipActivityState.Completed or ClipActivityState.Archived)
            .ToArray();
        _recentUploadsLabel.Text = uploaded.Length.ToString("N0");
        var sentBytes = uploaded.Aggregate(0L, (total, entry) =>
        {
            var next = Math.Max(0, entry.CompressedBytes ?? entry.OriginalBytes);
            return total > long.MaxValue - next ? long.MaxValue : total + next;
        });
        _recentUploadsDetailLabel.Text = uploaded.Length == 0
            ? "No Discord uploads in retained history"
            : $"{FormatBytes(sentBytes)} sent in retained history";

        var previous = _recentRows.Controls.Cast<Control>().ToArray();
        _recentRows.SuspendLayout();
        try
        {
            _recentRows.Controls.Clear();
            var entries = snapshot.Entries.Take(RecentActivityLimit).ToArray();
            if (entries.Length == 0)
            {
                var empty = new Label
                {
                    Name = "HomeRecentActivityEmpty",
                    Text = "No recent clips yet. ClipCord will show its latest local activity here.",
                    Dock = DockStyle.Fill,
                    ForeColor = ClipCordTheme.TextTertiary,
                    Font = ClipCordTheme.InterfaceFont(8.8f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Margin = Padding.Empty,
                    AccessibleRole = AccessibleRole.StaticText
                };
                _recentRows.Controls.Add(empty, 0, 0);
                _recentRows.SetRowSpan(empty, RecentActivityLimit);
            }
            else
            {
                for (var index = 0; index < entries.Length; index++)
                {
                    _recentRows.Controls.Add(CreateActivityRow(entries[index], index > 0), 0, index);
                }
            }
        }
        finally
        {
            _recentRows.ResumeLayout(true);
            foreach (var control in previous) control.Dispose();
        }
    }

    private Control CreateActivityRow(ClipActivityEntry entry, bool divider)
    {
        var presentation = ActivityView.GetPresentation(entry);
        var row = new HomeActivityRow
        {
            Name = $"HomeActivityRow_{entry.Id:N}",
            AccessibleName = $"{presentation.Label}: {entry.FileName}",
            AccessibleDescription = BuildActivityDetail(entry),
            AccessibleRole = AccessibleRole.ListItem,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            AccentColor = presentation.Accent,
            DrawTopDivider = divider,
            Margin = Padding.Empty,
            Padding = new Padding(13, 5, 0, 3)
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var title = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        title.Controls.Add(new Label
        {
            Text = presentation.Label.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = presentation.Accent,
            Font = ClipCordTheme.InterfaceFont(6.8f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0),
            Anchor = AnchorStyles.Left,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        title.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(entry.FileName) ? "Unknown clip.mp4" : entry.FileName,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.4f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = FormatRelativeTime(entry.UpdatedUtc, _utcNowProvider()),
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.4f),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);
        layout.Controls.Add(new Label
        {
            Text = BuildActivityDetail(entry),
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 1);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 1)!, 2);
        row.Controls.Add(layout);
        return row;
    }

    private static string BuildActivityDetail(ClipActivityEntry entry)
    {
        if (entry.Route == ClipActivityRoute.LocalOnly && entry.State == ClipActivityState.Archived)
        {
            return $"{FormatBytes(entry.OriginalBytes)} · kept on this PC; no Discord request was made";
        }

        if (entry.Route == ClipActivityRoute.Uploaded &&
            entry.State is ClipActivityState.Completed or ClipActivityState.Archived)
        {
            var sent = Math.Max(0, entry.CompressedBytes ?? entry.OriginalBytes);
            if (entry.CompressedBytes is { } compressed && entry.OriginalBytes > 0 && compressed < entry.OriginalBytes)
            {
                var reduction = 100d * (entry.OriginalBytes - compressed) / entry.OriginalBytes;
                var ceiling = entry.CompressionTargetMb is { } target ? $" · {target} MB ceiling" : string.Empty;
                return $"{FormatBytes(entry.OriginalBytes)} → {FormatBytes(compressed)} ({reduction:F1}% smaller){ceiling}";
            }
            return $"{FormatBytes(sent)} sent to Discord";
        }

        if (entry.State == ClipActivityState.Compressing)
        {
            return entry.CompressionTargetMb is { } target
                ? $"{FormatBytes(entry.OriginalBytes)} · encoding toward the {target} MB ceiling"
                : $"{FormatBytes(entry.OriginalBytes)} · preparing a smaller upload";
        }

        if (entry.State == ClipActivityState.Retrying)
        {
            return $"Attempt {Math.Max(1, entry.AttemptCount)} · retry scheduled; open Activity for details";
        }

        if (entry.State == ClipActivityState.Failed)
        {
            return "Clip needs attention · open Activity for safe details";
        }

        if (!string.IsNullOrWhiteSpace(entry.Detail)) return entry.Detail;
        return entry.OriginalBytes > 0 ? FormatBytes(entry.OriginalBytes) : "Processing locally";
    }

    private ClipActivitySnapshot GetHistorySnapshot()
    {
        try
        {
            return _history.GetSnapshot();
        }
        catch (ObjectDisposedException)
        {
            return new ClipActivitySnapshot([]);
        }
    }

    private void EnsureActivitySubscription()
    {
        if (!_active || _activitySubscription is not null || IsDisposed || Disposing) return;
        var context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        try
        {
            _activitySubscription = _history.Subscribe(context, RenderActivitySnapshot);
        }
        catch (ObjectDisposedException)
        {
            _activitySubscription = null;
        }
    }

    private void DisposeActivitySubscription()
    {
        var subscription = Interlocked.Exchange(ref _activitySubscription, null);
        subscription?.Dispose();
    }

    private void StartArchiveScan()
    {
        CancelArchiveScan();
        if (!_active || IsDisposed || Disposing) return;

        var generation = Interlocked.Increment(ref _archiveGeneration);
        var cancellation = new CancellationTokenSource();
        _archiveCancellation = cancellation;
        _archiveCountLabel.Text = "—";
        _archiveDetailLabel.Text = "Scanning this PC…";
        _ = RefreshArchiveAsync(_settings.ClipsFolder, generation, cancellation);
    }

    private async Task RefreshArchiveAsync(
        string clipsFolder,
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            var snapshot = await Task.Run(
                () => _galleryScanner(clipsFolder, cancellation.Token),
                cancellation.Token);
            if (!CanAdoptArchiveResult(generation, cancellation)) return;

            var localOnlyClips = snapshot.Games
                .SelectMany(game => game.Clips)
                .Where(clip => clip.Route == GalleryClipRoute.LocalOnly)
                .ToArray();
            var bytes = localOnlyClips.Aggregate(0L, (total, clip) =>
            {
                var next = Math.Max(0, clip.Length);
                return total > long.MaxValue - next ? long.MaxValue : total + next;
            });
            var localArchiveIncomplete = snapshot.Warnings.Any(warning =>
                warning.Contains("local-only", StringComparison.OrdinalIgnoreCase) ||
                warning.Contains("clips folder", StringComparison.OrdinalIgnoreCase));
            if (localArchiveIncomplete && localOnlyClips.Length == 0)
            {
                SetArchiveUnavailable("Archive scan incomplete; open Gallery to retry.");
                return;
            }
            _archiveCountLabel.Text = localOnlyClips.Length.ToString("N0");
            _archiveDetailLabel.Text = localArchiveIncomplete
                ? $"At least {FormatBytes(bytes)} found · some folders unavailable"
                : $"{FormatBytes(bytes)} kept on this PC";
            _archiveDetailLabel.AccessibleDescription = localArchiveIncomplete
                ? "Local-only archive scan was partial because one or more folders were unavailable."
                : "Local-only archive scan completed.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Leaving Home or changing the configured folder intentionally cancels the scan.
        }
        catch (Exception exception)
        {
            if (!CanAdoptArchiveResult(generation, cancellation)) return;
            Log.Error("Could not scan the Local-only archive for Home.", exception);
            SetArchiveUnavailable("Archive scan unavailable; open Gallery to retry.");
        }
        finally
        {
            if (ReferenceEquals(_archiveCancellation, cancellation)) _archiveCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool CanAdoptArchiveResult(int generation, CancellationTokenSource cancellation) =>
        _active &&
        !IsDisposed &&
        !Disposing &&
        !cancellation.IsCancellationRequested &&
        generation == Volatile.Read(ref _archiveGeneration) &&
        ReferenceEquals(_archiveCancellation, cancellation);

    private void CancelArchiveScan()
    {
        Interlocked.Increment(ref _archiveGeneration);
        var cancellation = Interlocked.Exchange(ref _archiveCancellation, null);
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void SetArchiveUnavailable(string detail)
    {
        _archiveCountLabel.Text = "—";
        _archiveDetailLabel.Text = detail;
        _archiveDetailLabel.AccessibleDescription = detail;
    }

    internal static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        if (value < 1024) return $"{value:N0} B";
        if (value < 1024L * 1024) return $"{value / 1024d:N1} KB";
        if (value < 1024L * 1024 * 1024) return $"{value / (1024d * 1024):N1} MB";
        return $"{value / (1024d * 1024 * 1024):N1} GB";
    }

    internal static string FormatRelativeTime(DateTime updatedUtc, DateTime utcNow)
    {
        var age = utcNow.ToUniversalTime() - updatedUtc.ToUniversalTime();
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)} hr ago";
        if (age < TimeSpan.FromDays(7)) return $"{Math.Max(1, (int)age.TotalDays)} d ago";
        return updatedUtc.ToLocalTime().ToString("MMM d");
    }

    private static RoundedPanel CreateCard(string name, string accessibleName, int padding) => new()
    {
        Name = name,
        AccessibleName = accessibleName,
        AccessibleRole = AccessibleRole.Grouping,
        Dock = DockStyle.Fill,
        BackColor = ClipCordTheme.SurfaceRaised,
        BorderColor = ClipCordTheme.BorderDefault,
        CornerRadius = 16,
        Margin = Padding.Empty,
        Padding = new Padding(padding)
    };

    private static OutlineButton CreateActionButton(string text, string name, BrandGlyph glyph) => new()
    {
        Name = name,
        AccessibleName = text,
        AccessibleRole = AccessibleRole.PushButton,
        Text = text,
        LeadingGlyph = glyph,
        TrailingIcon = FigmaIconAsset.ChevronRight,
        AlignContentLeft = true,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Height = 36,
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        DisabledSurfaceColor = ClipCordTheme.SurfaceSunken,
        DisabledTextColor = ClipCordTheme.TextTertiary,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.6f),
        Margin = new Padding(0, 2, 0, 2),
        TabStop = true
    };

    private static Label CreateCardHeading(string text, string name) => new()
    {
        Name = name,
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
        AccessibleRole = AccessibleRole.StaticText
    };

    private static Label CreateValueLabel(string name, float fontSize) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(fontSize, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
        AccessibleRole = AccessibleRole.StaticText
    };

    private static Label CreateMetricValueLabel(string name) => CreateValueLabel(name, 16f);

    private static Label CreateMetadataLabel(
        string name,
        ContentAlignment alignment = ContentAlignment.MiddleLeft) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        ForeColor = ClipCordTheme.TextTertiary,
        Font = ClipCordTheme.InterfaceFont(8f),
        TextAlign = alignment,
        Margin = Padding.Empty,
        AccessibleRole = AccessibleRole.StaticText
    };

    private int ScaleUi(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        if (_active) EnsureActivitySubscription();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _active = false;
            DisposeActivitySubscription();
            CancelArchiveScan();
            if (!_openClipsFolderButton.IsDisposed) _openClipsFolderButton.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class HomeContentLayout : Panel
{
    private const int LogicalHorizontalPadding = 28;
    private const int LogicalTopPadding = 0;
    private const int LogicalBottomPadding = 8;
    private const int LogicalGap = 14;
    private const int LogicalHeroHeight = 196;
    private const int LogicalMetricsHeight = 104;
    private const int LogicalBottomHeight = 323;
    private const int LogicalStackThreshold = 760;
    private readonly Control _hero;
    private readonly Control _metrics;
    private readonly Control _bottom;

    internal HomeContentLayout(Control hero, Control metrics, Control bottom)
    {
        _hero = hero;
        _metrics = metrics;
        _bottom = bottom;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Controls.Add(hero);
        Controls.Add(metrics);
        Controls.Add(bottom);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(1, proposedSize.Width);
        var scale = Math.Max(1, DeviceDpi) / 96d;
        var logicalWidth = width / scale;
        var bottomHeight = logicalWidth < LogicalStackThreshold
            ? LogicalBottomHeight * 2 + LogicalGap
            : LogicalBottomHeight;
        var logicalHeight = LogicalTopPadding + LogicalHeroHeight + LogicalGap +
                            LogicalMetricsHeight + LogicalGap + bottomHeight + LogicalBottomPadding;
        return new Size(width, Math.Max(1, (int)Math.Round(logicalHeight * scale)));
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        var left = ScaleLogical(LogicalHorizontalPadding);
        var top = ScaleLogical(LogicalTopPadding);
        var width = Math.Max(1, ClientSize.Width - left * 2);
        var gap = ScaleLogical(LogicalGap);
        var heroHeight = ScaleLogical(LogicalHeroHeight);
        var metricsHeight = ScaleLogical(LogicalMetricsHeight);
        var bottomHeight = ScaleLogical(LogicalBottomHeight);

        _hero.Bounds = new Rectangle(left, top, width, heroHeight);
        top = _hero.Bottom + gap;
        _metrics.Bounds = new Rectangle(left, top, width, metricsHeight);
        top = _metrics.Bottom + gap;

        var stacked = ClientSize.Width * 96d / Math.Max(1, DeviceDpi) < LogicalStackThreshold;
        _bottom.Bounds = new Rectangle(left, top, width, stacked ? bottomHeight * 2 + gap : bottomHeight);
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * Math.Max(1, DeviceDpi) / 96d));
}

internal sealed class HomeBottomLayout : Panel
{
    private const int LogicalGap = 16;
    private const int LogicalShortcutWidth = 300;
    private const int LogicalStackThreshold = 760;
    private readonly Control _activity;
    private readonly Control _shortcuts;

    internal HomeBottomLayout(Control activity, Control shortcuts)
    {
        _activity = activity;
        _shortcuts = shortcuts;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Controls.Add(activity);
        Controls.Add(shortcuts);
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        var gap = ScaleLogical(LogicalGap);
        var logicalWidth = ClientSize.Width * 96d / Math.Max(1, DeviceDpi);
        if (logicalWidth < LogicalStackThreshold)
        {
            var height = Math.Max(1, (ClientSize.Height - gap) / 2);
            _activity.Bounds = new Rectangle(0, 0, ClientSize.Width, height);
            _shortcuts.Bounds = new Rectangle(0, height + gap, ClientSize.Width, Math.Max(1, ClientSize.Height - height - gap));
            return;
        }

        var shortcutWidth = Math.Min(ScaleLogical(LogicalShortcutWidth), Math.Max(1, ClientSize.Width / 2));
        _activity.Bounds = new Rectangle(0, 0, Math.Max(1, ClientSize.Width - shortcutWidth - gap), ClientSize.Height);
        _shortcuts.Bounds = new Rectangle(_activity.Right + gap, 0, shortcutWidth, ClientSize.Height);
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * Math.Max(1, DeviceDpi) / 96d));
}

internal sealed class HomeStatusPill : Control
{
    private string _label = "STARTING";
    private Color _accent = ClipCordTheme.TextTertiary;

    internal HomeStatusPill()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        TabStop = false;
        Size = new Size(116, 28);
        MinimumSize = new Size(116, 28);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
    }

    internal void Apply(AboutWatcherPresentation presentation)
    {
        _label = presentation.Label.ToUpperInvariant();
        _accent = presentation.State switch
        {
            AboutWatcherState.Watching => Color.FromArgb(49, 177, 113),
            AboutWatcherState.NeedsAttention or AboutWatcherState.SetupRequired => ClipCordTheme.Coral,
            AboutWatcherState.Paused => Color.FromArgb(224, 151, 54),
            AboutWatcherState.LocalOnly => Color.FromArgb(91, 147, 255),
            AboutWatcherState.Preparing or AboutWatcherState.Uploading or AboutWatcherState.Compressing or AboutWatcherState.Archiving => ClipCordTheme.Violet,
            _ => ClipCordTheme.TextTertiary
        };
        AccessibleDescription = presentation.Detail;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, Height / 2);
        using var fill = new SolidBrush(ClipCordTheme.SurfaceSunken);
        using var border = new Pen(ClipCordTheme.BorderDefault);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(border, path);
        var dotSize = Math.Max(6, (int)Math.Round(7 * DeviceDpi / 96d));
        var dot = new Rectangle(
            Math.Max(8, (int)Math.Round(10 * DeviceDpi / 96d)),
            (Height - dotSize) / 2,
            dotSize,
            dotSize);
        using var dotBrush = new SolidBrush(_accent);
        eventArgs.Graphics.FillEllipse(dotBrush, dot);
        var textBounds = new Rectangle(dot.Right + 7, 0, Math.Max(1, Width - dot.Right - 11), Height);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _label,
            ClipCordTheme.InterfaceFont(7.2f, FontStyle.Bold),
            textBounds,
            _accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

internal sealed class HomeRouteArrow : Control
{
    internal HomeRouteArrow()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var scale = Math.Max(1d, DeviceDpi / 96d);
        var diameter = Math.Min((int)Math.Round(38 * scale), Math.Min(Width, Height - (int)Math.Round(20 * scale)));
        var circle = new Rectangle((Width - diameter) / 2, Math.Max(0, (Height - diameter - 14) / 2), diameter, diameter);
        using var fill = new SolidBrush(ClipCordTheme.VioletMuted);
        using var outline = new Pen(ClipCordTheme.Violet, 1.2f);
        eventArgs.Graphics.FillEllipse(fill, circle);
        eventArgs.Graphics.DrawEllipse(outline, circle);
        var iconSide = Math.Max(1, (int)Math.Round(18 * scale));
        FigmaIconRenderer.Draw(
            eventArgs.Graphics,
            new Rectangle(
                circle.Left + (circle.Width - iconSide) / 2,
                circle.Top + (circle.Height - iconSide) / 2,
                iconSide,
                iconSide),
            FigmaIconAsset.ArrowRight,
            Color.FromArgb(176, 128, 255));
        var labelBounds = new Rectangle(0, circle.Bottom + 3, Width, Math.Max(1, Height - circle.Bottom - 3));
        TextRenderer.DrawText(
            eventArgs.Graphics,
            "ROUTES TO",
            ClipCordTheme.InterfaceFont(6.8f, FontStyle.Bold),
            labelBounds,
            ClipCordTheme.TextTertiary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

internal sealed class HomeRouteDot : Control
{
    internal Color Accent { get; init; } = ClipCordTheme.Violet;

    internal HomeRouteDot()
    {
        DoubleBuffered = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.None;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var size = Math.Max(4, (int)Math.Round(6 * Math.Max(96, DeviceDpi) / 96d));
        using var brush = new SolidBrush(Accent);
        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        eventArgs.Graphics.FillEllipse(brush, 0, Math.Max(0, (Height - size) / 2), size, size);
    }
}

internal sealed class HomeMetricGlyph : Control
{
    internal BrandGlyph Glyph { get; init; }
    internal Color Accent { get; init; } = ClipCordTheme.Violet;

    internal HomeMetricGlyph()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 2 || Height <= 2) return;
        eventArgs.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var maximumSize = Math.Max(1, (int)Math.Round(34 * Math.Max(96, DeviceDpi) / 96d));
        var size = Math.Min(maximumSize, Math.Min(Width - 1, Height - 1));
        var bounds = new Rectangle(Width - size - 1, 1, size, size);
        using var fill = new SolidBrush(Color.FromArgb(42, Accent));
        using var outline = new Pen(Accent);
        var cornerRadius = Math.Max(1, (int)Math.Round(9 * Math.Max(96, DeviceDpi) / 96d));
        using var path = RoundedPanel.CreateRoundedPath(bounds, cornerRadius);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(outline, path);
        var inset = Math.Max(1, (int)Math.Round(9 * Math.Max(96, DeviceDpi) / 96d));
        BrandGlyphControl.DrawGlyph(eventArgs.Graphics, Rectangle.Inflate(bounds, -inset, -inset), Glyph, Accent, 1.5f);
    }
}

internal sealed class HomeActivityRow : Panel
{
    internal Color AccentColor { get; init; } = ClipCordTheme.Violet;
    internal bool DrawTopDivider { get; init; }

    internal HomeActivityRow()
    {
        DoubleBuffered = true;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (DrawTopDivider)
        {
            using var divider = new Pen(ClipCordTheme.BorderDefault);
            eventArgs.Graphics.DrawLine(divider, 0, 0, Width, 0);
        }
        using var accent = new SolidBrush(AccentColor);
        eventArgs.Graphics.FillRectangle(accent, 0, Math.Max(6, Height / 5), 3, Math.Max(12, Height * 3 / 5));
    }
}
