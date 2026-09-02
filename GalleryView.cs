using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal sealed class GalleryView : UserControl
{
    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private readonly SynchronizationContext _uiContext;
    private readonly System.Windows.Forms.Timer _renditionRefreshTimer;
    private readonly Label _headingLabel;
    private readonly Label _summaryLabel;
    private readonly OutlineButton _backButton;
    private readonly OutlineButton _refreshButton;
    private readonly OutlineButton _openClipsButton;
    private readonly RoundedPanel _searchHost;
    private readonly TextBox _searchText;
    private readonly OutlineButton _sortButton;
    private readonly FlowLayoutPanel _filterBar;
    private readonly OutlineButton _allFilterButton;
    private readonly OutlineButton _localOnlyFilterButton;
    private readonly OutlineButton _uploadedFilterButton;
    private readonly BufferedTableLayoutPanel _rootLayout;
    private readonly BufferedTableLayoutPanel _embeddedHeader;
    private readonly FlowLayoutPanel _headerActions;
    private readonly BufferedTableLayoutPanel _libraryLayout;
    private readonly RoundedPanel _gameRail;
    private readonly BufferedTableLayoutPanel _gameRailLayout;
    private readonly Label _gamesHeading;
    private readonly ActivityListPanel _gameFilterList;
    private readonly BrandedScrollHost _gameScrollHost;
    private readonly GalleryGridPanel _clipGrid;
    private readonly BrandedScrollHost _scrollHost;
    private readonly IManualClipEditService? _manualClipEditService;
    private readonly Func<string, bool>? _launchMediaFile;
    private readonly IClipPlaybackPreparer _playbackPreparer;
    private readonly IGalleryThumbnailProvider _thumbnailProvider;
    private readonly IFavoritesService _favorites;
    private readonly Func<bool> _captureLibraryAccessAllowed;
    private readonly Func<IReadOnlyList<GalleryExternalSourceRoot>>
        _additionalExternalRootsProvider;
    private readonly Func<string, CancellationToken, string?, GalleryClipSource, GallerySnapshot>
        _scanCatalog;
    private int? _effectiveDpiForTests;
    private string? _captureLibraryRoot;
    private readonly GalleryClipSource _externalClipSource;
    private readonly Dictionary<GalleryThumbnailTile, GalleryClipEntry> _thumbnailClips = [];
    private readonly HashSet<GalleryThumbnailTile> _requestedThumbnailTiles = [];
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private int _thumbnailGeneration;
    private bool _thumbnailSchedulePending;
    private string _clipsFolder;
    private GallerySnapshot _snapshot = new([], []);
    private GalleryGameEntry? _selectedGame;
    private bool _showFavorites;
    private GalleryClipRoute? _routeFilter;
    private bool _sortNewestFirst = true;
    private LocalClipEditorView? _editor;
    private ClipPlayerView? _player;
    private Control? _playerContent;
    private GalleryClipEntry? _playerClip;
    private CancellationTokenSource? _playbackPrewarmCancellation;
    private string? _playbackPrewarmPath;
    private GalleryScreen _screen;
    private bool _active;
    private bool _scanInProgress;
    private bool _renditionRetrySawActiveWork;
    private int _renditionRetryGracePolls;
    private bool _disposed;

    internal event Action<bool>? OperationBusyChanged;
    internal event Action<string, string>? HeaderChanged;
    internal event EventHandler<GalleryRenditionRetryRequestedEventArgs>? RenditionRetryRequested;
    internal Control HeaderActions => _headerActions;

    internal void SetCaptureLibraryRoot(string? root)
    {
        _captureLibraryRoot = root;
        if (_active) RefreshCatalog(_clipsFolder);
    }

    internal void SetEffectiveDpiForTests(int effectiveDpi)
    {
        if (effectiveDpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveDpi));
        }
        _effectiveDpiForTests = effectiveDpi;
    }

    internal GalleryView(
        string clipsFolder,
        IManualClipEditService? manualClipEditService = null,
        Func<string, bool>? launchMediaFile = null,
        IClipPlaybackPreparer? playbackPreparer = null,
        IGalleryThumbnailProvider? thumbnailProvider = null,
        IFavoritesService? favorites = null,
        string? captureLibraryRoot = null,
        ClipCaptureSource externalCaptureSource = ClipCaptureSource.SteelSeriesGg,
        Func<bool>? captureLibraryAccessAllowed = null,
        Func<string, CancellationToken, string?, GalleryClipSource, GallerySnapshot>? scanCatalog = null,
        Func<IReadOnlyList<GalleryExternalSourceRoot>>? additionalExternalRootsProvider = null,
        Func<CancellationToken, IReadOnlyList<RoutingDeliveryHistoryItem>>?
            captureRoutingHistory = null)
    {
        _clipsFolder = clipsFolder;
        _manualClipEditService = manualClipEditService;
        _launchMediaFile = launchMediaFile;
        _playbackPreparer = playbackPreparer ?? new ClipPlaybackPreparer();
        _thumbnailProvider = thumbnailProvider ?? new GalleryThumbnailProvider();
        _favorites = favorites ?? new FavoritesService();
        _captureLibraryRoot = captureLibraryRoot;
        _captureLibraryAccessAllowed = captureLibraryAccessAllowed ?? (() => true);
        _additionalExternalRootsProvider = additionalExternalRootsProvider ?? (() => []);
        _scanCatalog = scanCatalog ?? ((folder, cancellationToken, captureRoot, source) =>
            GalleryCatalog.Scan(
                folder,
                cancellationToken,
                captureLibraryRoot: captureRoot,
                externalSource: source,
                additionalExternalRoots: _additionalExternalRootsProvider(),
                captureRoutingHistory: captureRoutingHistory));
        _externalClipSource = externalCaptureSource == ClipCaptureSource.Nvidia
            ? GalleryClipSource.Nvidia
            : GalleryClipSource.SteelSeriesGg;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _renditionRefreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _renditionRefreshTimer.Tick += RenditionRefreshTimerTick;
        _favorites.Changed += FavoritesChanged;
        Name = "GalleryView";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        AccessibleName = "Clip gallery";

        var root = new BufferedTableLayoutPanel
        {
            Name = "GalleryRootLayout",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(26, 12, 26, 12),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _rootLayout = root;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new BufferedTableLayoutPanel
        {
            Name = "GalleryEmbeddedHeader",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 12),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _embeddedHeader = header;
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headingLabel = new Label
        {
            Name = "GalleryHeading",
            Text = "Gallery",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.DisplayFont(18f, FontStyle.Bold),
            UseMnemonic = false,
            Margin = Padding.Empty
        };
        _summaryLabel = new Label
        {
            Name = "GallerySummary",
            Text = "Uploaded and local-only clips appear together by game.",
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Height = 24,
            ForeColor = ClipCordTheme.ShellMutedText,
            Font = ClipCordTheme.InterfaceFont(9.5f),
            Margin = new Padding(0, 2, 0, 0)
        };
        header.Controls.Add(_headingLabel, 0, 0);
        header.Controls.Add(_summaryLabel, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Name = "GalleryHeaderActions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _headerActions = actions;
        _backButton = CreateShellButton("All games", 105);
        _backButton.Name = "GalleryBackButton";
        _backButton.Visible = false;
        _backButton.Click += (_, _) => NavigateBack();
        _searchHost = new RoundedPanel
        {
            Name = "GallerySearchHost",
            Width = 160,
            Height = 38,
            BackColor = ClipCordTheme.SettingsField,
            BorderColor = ClipCordTheme.SettingsFieldBorder,
            CornerRadius = 8,
            Padding = new Padding(10, 8, 10, 6),
            Margin = Padding.Empty,
            AccessibleName = "Search Gallery clips"
        };
        _searchText = new TextBox
        {
            Name = "GallerySearchTextBox",
            AccessibleName = "Search clips or games",
            PlaceholderText = "Search clips",
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = ClipCordTheme.SettingsField,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = Padding.Empty
        };
        _searchText.TextChanged += (_, _) =>
        {
            if (_screen is GalleryScreen.Library or GalleryScreen.Game) RenderLibrary();
        };
        _searchText.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Escape || _searchText.TextLength == 0) return;
            _searchText.Clear();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        };
        var searchLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        searchLayout.Controls.Add(new FigmaIconControl
        {
            Name = "GallerySearchIcon",
            Asset = FigmaIconAsset.Search,
            IconColor = ClipCordTheme.TextTertiary,
            Anchor = AnchorStyles.Left,
            Size = new Size(14, 14),
            Margin = new Padding(0, 0, 6, 0)
        }, 0, 0);
        searchLayout.Controls.Add(_searchText, 1, 0);
        _searchHost.Controls.Add(searchLayout);
        _refreshButton = CreateShellButton(string.Empty, 42);
        _refreshButton.Name = "RefreshGalleryButton";
        _refreshButton.AccessibleName = "Refresh Gallery";
        _refreshButton.LeadingGlyph = BrandGlyph.Refresh;
        _refreshButton.Margin = new Padding(10, 0, 0, 0);
        _toolTip.SetToolTip(_refreshButton, "Refresh Gallery");
        _refreshButton.Click += (_, _) => RefreshCatalog(_clipsFolder);
        _openClipsButton = CreateShellButton("Open clips folder", 155);
        _openClipsButton.Name = "OpenClipsFolderButton";
        _openClipsButton.LeadingGlyph = BrandGlyph.Folder;
        _openClipsButton.Margin = new Padding(10, 0, 0, 0);
        _openClipsButton.Click += (_, _) => OpenClipsFolder();
        actions.Controls.Add(_backButton);
        actions.Controls.Add(_searchHost);
        actions.Controls.Add(_refreshButton);
        actions.Controls.Add(_openClipsButton);
        header.Controls.Add(actions, 1, 0);
        header.SetRowSpan(actions, 2);

        _filterBar = new FlowLayoutPanel
        {
            Name = "GalleryRouteFilters",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0),
            BackColor = ClipCordTheme.Shell,
            Visible = true
        };
        _allFilterButton = CreateFilterButton("All", "GalleryAllFilterButton", () => SetRouteFilter(null));
        _localOnlyFilterButton = CreateFilterButton("Local only", "GalleryLocalOnlyFilterButton", () => SetRouteFilter(GalleryClipRoute.LocalOnly));
        _uploadedFilterButton = CreateFilterButton("Uploaded", "GalleryUploadedFilterButton", () => SetRouteFilter(GalleryClipRoute.Uploaded));
        _localOnlyFilterButton.Margin = new Padding(8, 0, 0, 0);
        _uploadedFilterButton.Margin = new Padding(8, 0, 0, 0);
        _filterBar.Controls.Add(_allFilterButton);
        _filterBar.Controls.Add(_uploadedFilterButton);
        _filterBar.Controls.Add(_localOnlyFilterButton);
        _sortButton = CreateShellButton("Newest first", 112);
        _sortButton.Name = "GallerySortButton";
        _sortButton.Height = 32;
        _sortButton.TrailingIcon = FigmaIconAsset.ChevronRight;
        _sortButton.AccessibleName = "Sort clips oldest first";
        _sortButton.Click += (_, _) =>
        {
            _sortNewestFirst = !_sortNewestFirst;
            _sortButton.Text = _sortNewestFirst ? "Newest first" : "Oldest first";
            _sortButton.AccessibleName = _sortNewestFirst
                ? "Sort clips oldest first"
                : "Sort clips newest first";
            RenderLibrary();
        };
        var filterRow = new BufferedTableLayoutPanel
        {
            Name = "GalleryFilterRow",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        filterRow.Controls.Add(_filterBar, 0, 0);
        filterRow.Controls.Add(_sortButton, 1, 0);
        header.Controls.Add(filterRow, 0, 2);
        header.SetColumnSpan(filterRow, 2);

        _gameFilterList = new ActivityListPanel
        {
            Name = "GalleryGameFilterList",
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        _gameScrollHost = new BrandedScrollHost
        {
            Name = "GalleryGameScrollHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised,
            AccessibleName = "Games in the clip gallery",
            Content = _gameFilterList
        };
        _gameRail = new RoundedPanel
        {
            Name = "GalleryGameRail",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 14,
            Padding = new Padding(12, 12, 8, 10),
            Margin = new Padding(0, 0, 16, 0),
            AccessibleName = "Filter clips by game",
            AccessibleRole = AccessibleRole.Grouping
        };
        var gameRailLayout = new BufferedTableLayoutPanel
        {
            Name = "GalleryGameRailLayout",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        _gameRailLayout = gameRailLayout;
        gameRailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameRailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gameRailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _gamesHeading = new Label
        {
            Name = "GalleryGamesHeading",
            Text = "GAMES",
            AutoSize = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f, FontStyle.Bold),
            Margin = new Padding(2, 0, 0, 9),
            AccessibleRole = AccessibleRole.StaticText
        };
        gameRailLayout.Controls.Add(_gamesHeading, 0, 0);
        gameRailLayout.Controls.Add(_gameScrollHost, 0, 1);
        _gameRail.Controls.Add(gameRailLayout);

        _clipGrid = new GalleryGridPanel
        {
            Name = "GalleryClipGrid",
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "GalleryScrollHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            AccessibleName = "Clip gallery",
            Content = _clipGrid
        };
        _clipGrid.LocationChanged += (_, _) => ScheduleVisibleThumbnails();
        _clipGrid.Layout += (_, _) => ScheduleVisibleThumbnails();
        _scrollHost.Resize += (_, _) => ScheduleVisibleThumbnails();

        _libraryLayout = new BufferedTableLayoutPanel
        {
            Name = "GalleryLibraryLayout",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase
        };
        // 188px rail plus the 16px breathing room shown in the approved Gallery.
        _libraryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 204));
        _libraryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _libraryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _libraryLayout.Controls.Add(_gameRail, 0, 0);
        _libraryLayout.Controls.Add(_scrollHost, 1, 0);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_libraryLayout, 0, 1);
        Controls.Add(root);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyDpiLayoutMetrics();
        RebuildVisibleLibraryForDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        ApplyDpiLayoutMetrics();
        RebuildVisibleLibraryForDpi();
    }

    private void RebuildVisibleLibraryForDpi()
    {
        if (_clipGrid.Controls.Count > 0 && _screen is GalleryScreen.Library or GalleryScreen.Game)
        {
            RenderLibrary();
        }
    }

    private void ApplyDpiLayoutMetrics()
    {
        if (IsDisposed || Disposing) return;
        SuspendLayout();
        try
        {
            _rootLayout.Padding = ScalePadding(26, 12, 26, 12);
            _embeddedHeader.Margin = ScalePadding(0, 0, 0, 12);
            _summaryLabel.Height = ScaleUi(24);
            _summaryLabel.Margin = ScalePadding(0, 2, 0, 0);

            SizeShellButton(_backButton, 105, 38);
            _searchHost.Size = new Size(ScaleUi(160), ScaleUi(38));
            _searchHost.CornerRadius = ScaleUi(8);
            _searchHost.Padding = ScalePadding(10, 8, 10, 6);
            SizeShellButton(_refreshButton, 42, 38);
            SizeShellButton(_openClipsButton, 155, 38);
            _refreshButton.Margin = ScalePadding(10, 0, 0, 0);
            _openClipsButton.Margin = ScalePadding(10, 0, 0, 0);

            _filterBar.Margin = ScalePadding(0, 10, 0, 0);
            SizeShellButton(_allFilterButton, 72, 32);
            SizeShellButton(_uploadedFilterButton, 104, 32);
            SizeShellButton(_localOnlyFilterButton, 104, 32);
            SizeShellButton(_sortButton, 112, 32);
            _uploadedFilterButton.Margin = ScalePadding(8, 0, 0, 0);
            _localOnlyFilterButton.Margin = ScalePadding(8, 0, 0, 0);

            _gameRail.Padding = ScalePadding(12, 12, 8, 10);
            _gameRail.Margin = ScalePadding(0, 0, 16, 0);
            _gameRail.CornerRadius = ScaleUi(14);
            _gamesHeading.Margin = ScalePadding(2, 0, 0, 9);
            _libraryLayout.ColumnStyles[0].Width = ScaleUi(204);
            _headerActions.PerformLayout();
            _gameRailLayout.PerformLayout();
            _clipGrid.Reflow();
        }
        finally
        {
            ResumeLayout(true);
        }
    }

    private void SizeShellButton(Control button, int logicalWidth, int logicalHeight) =>
        button.Size = new Size(ScaleUi(logicalWidth), ScaleUi(logicalHeight));

    private Padding ScalePadding(int left, int top, int right, int bottom) => new(
        ScaleUi(left),
        ScaleUi(top),
        ScaleUi(right),
        ScaleUi(bottom));

    private int ScaleUi(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(1f, DeviceDpi / 96f)));

    internal static int GetFavoriteButtonSide(int dpi) =>
        Math.Max(1, (int)Math.Round(26 * Math.Max(1f, dpi / 96f)));

    internal async void RefreshCatalog(string clipsFolder)
    {
        if (_disposed || IsDisposed || Disposing || !_active) return;
        _clipsFolder = clipsFolder;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _scanInProgress = true;
        _refreshButton.Enabled = false;
        SetHeader(_headingLabel.Text, "Scanning uploaded and local-only clips…");
        GallerySnapshot? snapshot = null;
        Exception? failure = null;
        var captureLibraryRoot = HasCaptureLibraryAccess()
            ? _captureLibraryRoot
            : null;
        try
        {
            snapshot = await Task.Run(
                () => _scanCatalog(
                    clipsFolder,
                    cancellation.Token,
                    captureLibraryRoot,
                    _externalClipSource),
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        _uiContext.Post(_ => CompleteRefresh(cancellation, snapshot, failure), null);
    }

    private void CompleteRefresh(
        CancellationTokenSource cancellation,
        GallerySnapshot? snapshot,
        Exception? failure)
    {
        if (_disposed || IsDisposed || Disposing || !_active || cancellation.IsCancellationRequested ||
            !ReferenceEquals(_scanCancellation, cancellation)) return;
        _scanInProgress = false;
        _refreshButton.Enabled = true;
        if (failure is not null)
        {
            Log.Error("Could not refresh the clip Gallery.", failure);
            snapshot = new GallerySnapshot([], ["ClipCord could not read the clip archive."]);
        }
        _snapshot = HasCaptureLibraryAccess()
            ? snapshot ?? new GallerySnapshot([], [])
            : WithoutCaptureLibraryEntries(snapshot);
        ObserveRenditionPollResult();
        if (_screen is GalleryScreen.Editor or GalleryScreen.Player)
        {
            _refreshButton.Enabled = false;
            var currentName = _selectedGame?.Name;
            var refreshedGame = currentName is null
                ? null
                : _snapshot.Games.FirstOrDefault(game =>
                    game.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase));
            if (refreshedGame is not null) _selectedGame = refreshedGame;
            UpdateRenditionRefreshTimer();
            return;
        }
        if (_selectedGame is not null)
        {
            _selectedGame = _snapshot.Games.FirstOrDefault(game =>
                game.Name.Equals(_selectedGame.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (_selectedGame is null)
        {
            if (_showFavorites) ShowFavorites();
            else ShowLibrary();
        }
        else ShowGame(_selectedGame);
    }

    internal void Activate(string clipsFolder)
    {
        _active = true;
        RefreshCatalog(clipsFolder);
        UpdateRenditionRefreshTimer();
        RefreshViewport();
    }

    internal void Deactivate()
    {
        _active = false;
        _scanInProgress = false;
        _renditionRefreshTimer.Stop();
        _scanCancellation?.Cancel();
        CancelThumbnailRequests();
        CancelPlaybackPrewarm();
        DisposePlayer();
        if (_editor?.IsBusy == true)
        {
            _editor.CancelActiveOperation();
        }
        else if (_screen is GalleryScreen.Editor or GalleryScreen.Player && _selectedGame is not null)
        {
            // Leaving the page released the player, so the screen must not stay on a view
            // whose content has already been torn down.
            ShowGame(_selectedGame);
        }
        else if (_screen is GalleryScreen.Editor or GalleryScreen.Player)
        {
            if (_showFavorites) ShowFavorites();
            else ShowLibrary();
        }
        if (!_disposed && !IsDisposed && !Disposing) _refreshButton.Enabled = true;
    }

    /// <summary>
    /// Opens the clip editor for a specific Local-only clip, used when another page
    /// (such as Activity) hands off an entry. Resolves the clip from disk so it does not
    /// have to wait for the on-demand catalog scan; the editor still revalidates the path.
    /// </summary>
    internal bool TryOpenEditorFor(string clipPath)
    {
        if (_manualClipEditService is null || string.IsNullOrWhiteSpace(clipPath)) return false;
        if (!TryResolveLocalOnlyClip(clipPath, out var clip)) return false;
        // Align the surrounding view state so leaving the editor lands on the clip's game.
        _showFavorites = false;
        _routeFilter = GalleryClipRoute.LocalOnly;
        _selectedGame = _snapshot.Games.FirstOrDefault(game =>
            game.Name.Equals(clip.GameName, StringComparison.OrdinalIgnoreCase));
        ShowEditor(clip);
        return _screen == GalleryScreen.Editor;
    }

    private bool TryResolveLocalOnlyClip(string clipPath, out GalleryClipEntry clip)
    {
        clip = null!;
        FileInfo file;
        string? localOnlyRoot;
        try
        {
            file = new FileInfo(Path.GetFullPath(clipPath));
            localOnlyRoot = UploadedFolder.FindExistingLocalOnly(Path.GetFullPath(_clipsFolder));
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (localOnlyRoot is null || !file.Exists ||
            !file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(localOnlyRoot, file.FullName);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 1 or > 2) return false;

        var gameName = segments.Length == 2
            ? segments[0].Normalize(System.Text.NormalizationForm.FormC)
            : "Uncategorized";
        clip = new GalleryClipEntry(
            file.FullName,
            file.Name,
            gameName,
            GalleryClipRoute.LocalOnly,
            file.Length,
            file.LastWriteTimeUtc);
        return true;
    }

    internal bool HandleEscape()
    {
        if ((_screen is GalleryScreen.Library or GalleryScreen.Game) && _searchText.TextLength > 0)
        {
            _searchText.Clear();
            _searchText.Focus();
            return true;
        }
        if (_screen == GalleryScreen.Library) return false;
        NavigateBack();
        return true;
    }

    internal void RefreshViewport()
    {
        PerformLayout();
        _scrollHost.RefreshContentLayout();
        _gameScrollHost.RefreshContentLayout();
        ScheduleVisibleThumbnails();
    }

    internal void SetEmbeddedHeaderVisible(bool visible)
    {
        _headingLabel.Visible = visible;
        _summaryLabel.Visible = visible;
        PerformLayout();
    }

    internal void RestoreEmbeddedHeaderActions()
    {
        if (ReferenceEquals(_headerActions.Parent, _embeddedHeader)) return;
        _embeddedHeader.Controls.Add(_headerActions, 1, 0);
        _embeddedHeader.SetRowSpan(_headerActions, 2);
        _embeddedHeader.PerformLayout();
        PerformLayout();
    }

    private void SetHeader(string title, string subtitle)
    {
        _headingLabel.Text = title;
        _summaryLabel.Text = subtitle;
        HeaderChanged?.Invoke(title, subtitle);
    }

    internal static ProcessStartInfo CreatePlayClipStartInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ProcessStartInfo(path) { UseShellExecute = true };
    }

    private void ShowLibrary()
    {
        CancelPlaybackPrewarm();
        DisposeEditor();
        DisposePlayer();
        _screen = GalleryScreen.Library;
        _selectedGame = null;
        _showFavorites = false;
        _backButton.Visible = false;
        SetLibraryLayoutVisible(true);
        _refreshButton.Enabled = true;
        RenderLibrary();
    }

    private void ShowFavorites()
    {
        CancelPlaybackPrewarm();
        DisposeEditor();
        DisposePlayer();
        _screen = GalleryScreen.Library;
        _selectedGame = null;
        _showFavorites = true;
        _backButton.Visible = false;
        SetLibraryLayoutVisible(true);
        _refreshButton.Enabled = true;
        RenderLibrary();
    }

    private void ShowGame(GalleryGameEntry game)
    {
        CancelPlaybackPrewarm();
        DisposeEditor();
        DisposePlayer();
        _screen = GalleryScreen.Game;
        _selectedGame = game;
        _showFavorites = false;
        _backButton.Visible = false;
        SetLibraryLayoutVisible(true);
        _refreshButton.Enabled = true;
        RenderLibrary();
    }

    private void SetRouteFilter(GalleryClipRoute? route)
    {
        if (_screen is not (GalleryScreen.Library or GalleryScreen.Game)) return;
        _routeFilter = route;
        RenderLibrary();
    }

    private void RenderLibrary()
    {
        CancelThumbnailRequests();
        UpdateFilterButtons();
        RebuildGameFilters();
        var visibleClips = GetVisibleClips().ToArray();
        _clipGrid.SuspendLayout();
        try
        {
            DisposeChildren(_clipGrid);
            foreach (var clip in visibleClips)
            {
                _clipGrid.Controls.Add(BuildClipCard(clip));
            }
            if (visibleClips.Length == 0)
            {
                _clipGrid.Controls.Add(CreateEmptyState(BuildEmptyLibraryMessage()));
            }
        }
        finally
        {
            _clipGrid.ResumeLayout(true);
        }

        var summary = _showFavorites
            ? BuildFavoritesSummary(visibleClips.Length)
            : _selectedGame is null
                ? BuildLibrarySummary(_snapshot)
            : BuildGameSummary(_selectedGame, visibleClips.Length);
        SetHeader(_showFavorites ? "Favorites" : _selectedGame?.Name ?? "Gallery", summary);
        _scrollHost.Content = _clipGrid;
        _scrollHost.RefreshContentLayout(preservePosition: false);
        _gameScrollHost.RefreshContentLayout(preservePosition: true);
        StartThumbnailRequests();
        UpdateRenditionRefreshTimer();
    }

    private void StartThumbnailRequests()
    {
        if (!_active || _disposed || IsDisposed || Disposing ||
            _screen is not (GalleryScreen.Library or GalleryScreen.Game) ||
            _thumbnailClips.Count == 0)
        {
            return;
        }

        _thumbnailCancellation = new CancellationTokenSource();
        ScheduleVisibleThumbnails();
    }

    private void ScheduleVisibleThumbnails()
    {
        if (_thumbnailSchedulePending || !_active || _disposed || IsDisposed || Disposing ||
            !IsHandleCreated || _thumbnailCancellation is not { IsCancellationRequested: false } ||
            _screen is not (GalleryScreen.Library or GalleryScreen.Game))
        {
            return;
        }

        _thumbnailSchedulePending = true;
        try
        {
            BeginInvoke((Action)(() =>
            {
                _thumbnailSchedulePending = false;
                BeginVisibleThumbnailRequests();
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            _thumbnailSchedulePending = false;
        }
    }

    private void BeginVisibleThumbnailRequests()
    {
        var cancellation = _thumbnailCancellation;
        if (!_active || _disposed || IsDisposed || Disposing ||
            cancellation is null || cancellation.IsCancellationRequested ||
            _screen is not (GalleryScreen.Library or GalleryScreen.Game))
        {
            return;
        }

        var viewport = _scrollHost.ClientRectangle;
        var generation = Volatile.Read(ref _thumbnailGeneration);
        foreach (var pair in _thumbnailClips.ToArray())
        {
            var tile = pair.Key;
            if (!CanAccessClip(pair.Value) ||
                tile.IsDisposed || tile.Disposing || tile.Width <= 0 || tile.Height <= 0 ||
                _requestedThumbnailTiles.Contains(tile))
            {
                continue;
            }

            var bounds = GetBoundsRelativeTo(tile, _scrollHost);
            if (bounds.IsEmpty || !bounds.IntersectsWith(viewport)) continue;
            _requestedThumbnailTiles.Add(tile);
            _ = LoadThumbnailAsync(tile, pair.Value, cancellation, generation);
        }
    }

    private async Task LoadThumbnailAsync(
        GalleryThumbnailTile tile,
        GalleryClipEntry clip,
        CancellationTokenSource cancellation,
        int generation)
    {
        if (!CanAccessClip(clip)) return;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task
                .Run(
                    () => _thumbnailProvider.GetThumbnailAsync(_clipsFolder, clip, cancellation.Token),
                    cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // A thumbnail is decoration, never a reason to make a clip unavailable.
            // The deterministic gradient remains visible for stale/unsafe media,
            // missing codecs, cache failures, and malformed files.
            return;
        }

        if (bitmap is null) return;
        try
        {
            _uiContext.Post(_ =>
            {
                if (!_active || _disposed || IsDisposed || Disposing ||
                    cancellation.IsCancellationRequested ||
                    !ReferenceEquals(_thumbnailCancellation, cancellation) ||
                    generation != Volatile.Read(ref _thumbnailGeneration) ||
                    tile.IsDisposed || tile.Disposing ||
                    !_thumbnailClips.TryGetValue(tile, out var current) ||
                    current != clip)
                {
                    bitmap.Dispose();
                    return;
                }

                tile.SetThumbnail(bitmap);
            }, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            bitmap.Dispose();
        }
    }

    private void CancelThumbnailRequests()
    {
        Interlocked.Increment(ref _thumbnailGeneration);
        _thumbnailSchedulePending = false;
        var cancellation = Interlocked.Exchange(ref _thumbnailCancellation, null);
        if (cancellation is not null)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            cancellation.Dispose();
        }
        _requestedThumbnailTiles.Clear();
        _thumbnailClips.Clear();
    }

    private static Rectangle GetBoundsRelativeTo(Control control, Control ancestor)
    {
        var point = control.Location;
        var parent = control.Parent;
        while (parent is not null && !ReferenceEquals(parent, ancestor))
        {
            point.Offset(parent.Location);
            parent = parent.Parent;
        }
        return ReferenceEquals(parent, ancestor)
            ? new Rectangle(point, control.Size)
            : Rectangle.Empty;
    }

    private IEnumerable<GalleryClipEntry> GetVisibleClips()
    {
        var clips = GetScopedClips();
        if (_routeFilter is not null)
        {
            clips = clips.Where(clip => clip.Route == _routeFilter.Value);
        }
        var search = _searchText.Text.Trim();
        if (search.Length > 0)
        {
            clips = clips.Where(clip =>
                clip.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                clip.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                clip.GameName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        return _sortNewestFirst
            ? clips.OrderByDescending(clip => clip.LastWriteTimeUtc)
            : clips.OrderBy(clip => clip.LastWriteTimeUtc);
    }

    private IEnumerable<GalleryClipEntry> GetScopedClips()
    {
        var clips = _selectedGame is null
            ? _snapshot.Games.SelectMany(game => game.Clips)
            : _selectedGame.Clips;
        return _showFavorites ? clips.Where(_favorites.IsFavorite) : clips;
    }

    private string BuildEmptyLibraryMessage()
    {
        if (_searchText.TextLength > 0)
        {
            return _showFavorites
                ? "No Favorite clips match this search."
                : "No clips match this search.";
        }
        if (_showFavorites)
        {
            return _routeFilter switch
            {
                GalleryClipRoute.LocalOnly => "No Local-only clips in Favorites.",
                GalleryClipRoute.Uploaded => "No uploaded clips in Favorites.",
                _ => "No Favorite clips yet. Select the heart on any clip to add it here."
            };
        }
        if (_snapshot.Games.Count == 0)
        {
            return _snapshot.Warnings.Count > 0
                ? string.Join(" ", _snapshot.Warnings)
                : "No archived clips yet. Uploaded and local-only clips will appear here.";
        }
        var scope = _selectedGame is null ? "the Gallery" : _selectedGame.Name;
        return _routeFilter switch
        {
            GalleryClipRoute.LocalOnly => $"No Local-only clips in {scope}.",
            GalleryClipRoute.Uploaded => $"No uploaded clips in {scope}.",
            _ => $"No clips in {scope}."
        };
    }

    private void RebuildGameFilters()
    {
        _gameFilterList.SuspendLayout();
        try
        {
            DisposeChildren(_gameFilterList);
            _gameFilterList.Controls.Add(CreateGameFilterButton(
                "All clips",
                _snapshot.TotalClips,
                _selectedGame is null && !_showFavorites,
                ShowLibrary));
            _gameFilterList.Controls.Add(CreateGameFilterButton(
                "Favorites",
                _favorites.CountFavorites(_snapshot.Games.SelectMany(game => game.Clips)),
                _showFavorites,
                ShowFavorites,
                FigmaIconAsset.HeartFill,
                ClipCordTheme.Coral));
            foreach (var game in _snapshot.Games)
            {
                var selected = _selectedGame is not null &&
                    game.Name.Equals(_selectedGame.Name, StringComparison.OrdinalIgnoreCase);
                _gameFilterList.Controls.Add(CreateGameFilterButton(
                    game.Name,
                    game.Clips.Count,
                    selected,
                    () => ShowGame(game)));
            }
        }
        finally
        {
            _gameFilterList.ResumeLayout(true);
        }
    }

    private GalleryGameFilterButton CreateGameFilterButton(
        string label,
        int count,
        bool selected,
        Action select,
        FigmaIconAsset? leadingIcon = null,
        Color? leadingIconColor = null)
    {
        var button = new GalleryGameFilterButton(
            label,
            count,
            selected,
            select,
            leadingIcon,
            leadingIconColor)
        {
            Height = ScaleUi(36),
            Margin = ScalePadding(0, 0, 0, 2)
        };
        return button;
    }

    private void SetLibraryLayoutVisible(bool visible)
    {
        _gameRail.Visible = visible;
        _libraryLayout.ColumnStyles[0].SizeType = visible ? SizeType.Absolute : SizeType.AutoSize;
        _filterBar.Visible = visible;
        _sortButton.Visible = visible;
        _searchHost.Visible = visible;
        _refreshButton.Visible = visible;
        _libraryLayout.PerformLayout();
    }

    private void UpdateFilterButtons()
    {
        var clips = GetScopedClips().ToArray();
        var total = clips.Length;
        var uploaded = clips.Count(clip => clip.Route == GalleryClipRoute.Uploaded);
        var localOnly = clips.Count(clip => clip.Route == GalleryClipRoute.LocalOnly);
        _allFilterButton.Text = $"All  {total}";
        _uploadedFilterButton.Text = $"Uploaded  {uploaded}";
        _localOnlyFilterButton.Text = $"Local only  {localOnly}";
        _allFilterButton.AccessibleName = $"Show all {total} clips";
        _uploadedFilterButton.AccessibleName = $"Show {uploaded} uploaded clips";
        _localOnlyFilterButton.AccessibleName = $"Show {localOnly} local-only clips";
        SetFilterSelected(_allFilterButton, _routeFilter is null);
        SetFilterSelected(_localOnlyFilterButton, _routeFilter == GalleryClipRoute.LocalOnly);
        SetFilterSelected(_uploadedFilterButton, _routeFilter == GalleryClipRoute.Uploaded);
    }

    private static void SetFilterSelected(OutlineButton button, bool selected)
    {
        button.SurfaceColor = selected ? ClipCordTheme.VioletMuted : ClipCordTheme.SurfaceControl;
        button.OutlineColor = selected ? ClipCordTheme.Violet : ClipCordTheme.BorderStrong;
        button.ForeColor = ClipCordTheme.TextPrimary;
        button.AccessibleDescription = selected ? "Selected filter" : string.Empty;
        button.Invalidate();
    }

    private void NavigateBack()
    {
        if (_screen == GalleryScreen.Player)
        {
            DisposePlayer();
            if (_selectedGame is not null) ShowGame(_selectedGame);
            else if (_showFavorites) ShowFavorites();
            else ShowLibrary();
            return;
        }
        if (_screen == GalleryScreen.Editor)
        {
            if (_editor?.IsBusy == true)
            {
                _editor.CancelActiveOperation();
                return;
            }
            if (_selectedGame is not null) ShowGame(_selectedGame);
            else ShowLibrary();
            return;
        }
        ShowLibrary();
    }

    private void ShowEditor(GalleryClipEntry clip)
    {
        if (_manualClipEditService is null ||
            clip.Route != GalleryClipRoute.LocalOnly ||
            !CanAccessClip(clip) ||
            !File.Exists(clip.Path)) return;
        _scanCancellation?.Cancel();
        CancelThumbnailRequests();
        CancelPlaybackPrewarm();
        DisposeEditor();
        DisposePlayer();
        _screen = GalleryScreen.Editor;
        SetHeader("Edit & upload", $"{clip.GameName} · Local only · {clip.Title}");
        _backButton.Visible = true;
        _backButton.Text = "Back to clips";
        SetLibraryLayoutVisible(false);
        _refreshButton.Enabled = false;
        var canonicalClip = clip with { GameName = _selectedGame?.Name ?? clip.GameName };
        _editor = new LocalClipEditorView(canonicalClip, _manualClipEditService, _launchMediaFile, _playbackPreparer);
        _editor.BusyChanged += EditorBusyChanged;
        _editor.Cancelled += EditorCancelled;
        _editor.Completed += EditorCompleted;
        _scrollHost.Content = _editor;
        _scrollHost.RefreshContentLayout(preservePosition: false);
        BeginInvoke((Action)(() =>
        {
            if (_editor is not null && !_editor.IsDisposed) _editor.Focus();
        }));
    }

    private void EditorBusyChanged(bool busy)
    {
        _backButton.Enabled = !busy;
        OperationBusyChanged?.Invoke(busy);
    }

    private void EditorCancelled()
    {
        if (_selectedGame is not null)
        {
            ShowGame(_selectedGame);
            BeginInvoke((Action)(() => GetSelectedFilterButton().Focus()));
        }
        else if (_showFavorites) ShowFavorites();
        else ShowLibrary();
    }

    private void EditorCompleted(string editedGameName, GalleryClipRoute resultRoute)
    {
        var selectedName = UploadedFolder.SanitizeGameFolderName(editedGameName);
        if (_disposed || IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke((Action)(() => CompleteEditorUpload(selectedName, resultRoute)));
    }

    private void CompleteEditorUpload(string selectedName, GalleryClipRoute resultRoute)
    {
        if (_disposed || IsDisposed || Disposing) return;
        _routeFilter = resultRoute;
        DisposeEditor();
        DisposePlayer();
        _screen = GalleryScreen.Game;
        _showFavorites = false;
        _selectedGame = selectedName is null
            ? null
            : _snapshot.Games.FirstOrDefault(game =>
                game.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
              ?? new GalleryGameEntry(selectedName, []);
        RefreshCatalog(_clipsFolder);
    }

    private OutlineButton GetSelectedFilterButton() => _routeFilter switch
    {
        GalleryClipRoute.LocalOnly => _localOnlyFilterButton,
        GalleryClipRoute.Uploaded => _uploadedFilterButton,
        _ => _allFilterButton
    };

    /// <summary>
    /// Shows a clip inside ClipCord rather than handing it to whichever application owns
    /// .mp4 on this PC. Local-only clips are the reason this matters: passing one to an
    /// unknown player can put it somewhere that syncs off the machine.
    /// </summary>
    private void ShowPlayer(GalleryClipEntry clip) => ShowPlayer(clip, clip);

    private void ShowPlayer(
        GalleryClipEntry playbackClip,
        GalleryClipEntry favoriteOwner)
    {
        if (!CanAccessClip(playbackClip) ||
            !CanAccessClip(favoriteOwner) ||
            !File.Exists(playbackClip.Path)) return;
        _scanCancellation?.Cancel();
        CancelThumbnailRequests();
        if (!string.Equals(_playbackPrewarmPath, playbackClip.Path, StringComparison.OrdinalIgnoreCase))
        {
            CancelPlaybackPrewarm();
        }
        DisposeEditor();
        DisposePlayer();
        _screen = GalleryScreen.Player;
        var route = playbackClip.Route == GalleryClipRoute.LocalOnly ? "Local only" : "Uploaded";
        var rendition = favoriteOwner.Renditions?.Outputs.FirstOrDefault(output =>
            output.ArtifactPath.Equals(playbackClip.Path, StringComparison.OrdinalIgnoreCase));
        var playbackKind = rendition is null ? route : $"{rendition.DisplayName} rendition";
        SetHeader("Play clip", $"{playbackClip.GameName} · {playbackKind} · {playbackClip.Title}");
        _backButton.Visible = true;
        _backButton.Text = "Back to clips";
        SetLibraryLayoutVisible(false);
        _refreshButton.Enabled = false;
        _player = new ClipPlayerView(
            playbackClip,
            _playbackPreparer,
            _favorites.IsFavorite(favoriteOwner),
            favorite => _favorites.SetFavorite(favoriteOwner, favorite),
            favoriteOwner.Title,
            _effectiveDpiForTests);
        _playerClip = favoriteOwner;
        _playerContent = favoriteOwner.HasRenditions
            ? BuildProjectDetailContent(_player, favoriteOwner, playbackClip.Path)
            : _player;
        _scrollHost.Content = _playerContent;
        _scrollHost.RefreshContentLayout(preservePosition: false);
        BeginInvoke((Action)(() =>
        {
            if (_player is not null && !_player.IsDisposed) _player.Focus();
        }));
    }

    private Control BuildProjectDetailContent(
        ClipPlayerView player,
        GalleryClipEntry favoriteOwner,
        string playbackPath)
    {
        var renditionPanel = BuildRenditionPanel(favoriteOwner, playbackPath);
        var content = new GalleryProjectDetailPanel(
            renditionPanel,
            player,
            ScaleUi(12))
        {
            Name = "GalleryProjectDetailContent",
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceBase,
            AccessibleName = "Clip and Reaction Camera formats",
            AccessibleRole = AccessibleRole.Pane
        };
        return content;
    }

    private Control BuildRenditionPanel(
        GalleryClipEntry clip,
        string playbackPath)
    {
        var presentation = clip.Renditions ??
            throw new InvalidOperationException("A rendition panel requires a project presentation.");
        var panel = new RoundedPanel
        {
            Name = "GalleryRenditionPanel",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = presentation.Status == GalleryRenditionAggregateStatus.Failed
                ? Color.FromArgb(128, 69, 82)
                : ClipCordTheme.BorderDefault,
            CornerRadius = ScaleUi(14),
            Padding = ScalePadding(18, 16, 18, 16),
            Margin = Padding.Empty,
            AccessibleName = $"Reaction Camera, {presentation.FormatsLabel}, {presentation.StatusLabel}",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new BufferedTableLayoutPanel
        {
            Name = "GalleryRenditionLayout",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = presentation.Outputs.Count + 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = panel.BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var output in presentation.Outputs)
        {
            layout.RowStyles.Add(new RowStyle(
                SizeType.Absolute,
                ScaleUi(GetRenditionOutputRowLogicalHeight(EffectiveDpi, output.CanPlay))));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(70)));

        layout.Controls.Add(BuildRenditionHeading(presentation), 0, 0);
        layout.Controls.Add(BuildRenditionStatusMessage(presentation), 0, 1);
        var row = 2;
        foreach (var output in presentation.Outputs)
        {
            layout.Controls.Add(
                BuildRenditionOutputRow(clip, presentation, output, playbackPath),
                0,
                row++);
        }
        layout.Controls.Add(BuildProjectSourceRow(presentation), 0, row);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control BuildRenditionHeading(GalleryRenditionPresentation presentation)
    {
        var heading = new BufferedTableLayoutPanel
        {
            Name = "GalleryRenditionHeading",
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new FigmaIconControl
        {
            Name = "GalleryRenditionHeadingIcon",
            Asset = FigmaIconAsset.Silhouette,
            IconColor = ClipCordTheme.TextPrimary,
            Size = new Size(ScaleUi(24), ScaleUi(24)),
            Margin = ScalePadding(0, 1, 10, 0),
            AccessibleRole = AccessibleRole.None
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Name = "GalleryRenditionTitle",
            Text = "Reaction Camera formats",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.DisplayFont(11.5f, FontStyle.Bold),
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 1, 0);
        heading.Controls.Add(
            CreateRenditionStatusChip(
                $"{presentation.FormatsLabel}  ·  {presentation.StatusLabel}",
                presentation.Status),
            2,
            0);
        var subtitle = new Label
        {
            Name = "GalleryRenditionSubtitle",
            Text = "One captured moment, with each requested layout kept inside the same Gallery entry.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.8f),
            Margin = ScalePadding(34, 3, 0, 0),
            UseMnemonic = false
        };
        heading.Controls.Add(subtitle, 0, 1);
        heading.SetColumnSpan(subtitle, 3);
        return heading;
    }

    private Control BuildRenditionStatusMessage(GalleryRenditionPresentation presentation)
    {
        var warning = presentation.Status is GalleryRenditionAggregateStatus.Failed or
            GalleryRenditionAggregateStatus.PartiallyReady;
        var message = new RoundedPanel
        {
            Name = "GalleryRenditionStatusMessage",
            Dock = DockStyle.Top,
            Height = ScaleUi(38),
            BackColor = warning ? Color.FromArgb(48, 30, 39) : ClipCordTheme.SurfaceSunken,
            BorderColor = warning ? Color.FromArgb(135, 70, 87) : ClipCordTheme.BorderDefault,
            CornerRadius = ScaleUi(8),
            Padding = ScalePadding(10, 7, 10, 6),
            Margin = ScalePadding(0, 11, 0, 8),
            AccessibleRole = AccessibleRole.StaticText
        };
        message.Controls.Add(new Label
        {
            Name = "GalleryRenditionStatusLabel",
            Text = presentation.StatusMessage ?? presentation.StatusLabel,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = warning ? Color.FromArgb(255, 205, 211) : ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.7f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            UseMnemonic = false
        });
        return message;
    }

    private Control BuildRenditionOutputRow(
        GalleryClipEntry favoriteOwner,
        GalleryRenditionPresentation presentation,
        GalleryRenditionOutputPresentation output,
        string playbackPath)
    {
        var token = GetOrientationControlToken(output.OrientationId);
        var failed = output.Status == GalleryRenditionOutputStatus.Failed;
        var canUseRendition = HasCaptureLibraryAccess();
        var canPlay = canUseRendition && output.CanPlay;
        var playing = canPlay &&
            output.ArtifactPath.Equals(playbackPath, StringComparison.OrdinalIgnoreCase);
        var stackActions = ShouldStackRenditionActions(EffectiveDpi) && canPlay;
        var row = new RoundedPanel
        {
            Name = $"Gallery{token}RenditionRow",
            Dock = DockStyle.Fill,
            BackColor = failed ? Color.FromArgb(43, 28, 39) : ClipCordTheme.SurfaceSunken,
            BorderColor = playing
                ? ClipCordTheme.Violet
                : failed ? Color.FromArgb(133, 70, 89) : ClipCordTheme.BorderDefault,
            CornerRadius = ScaleUi(10),
            Padding = ScalePadding(12, 10, 12, 10),
            Margin = ScalePadding(0, 4, 0, 4),
            AccessibleName = $"{output.DisplayName} rendition, {output.StatusLabel}",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = stackActions ? 2 : 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = row.BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(36)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (stackActions)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        layout.Controls.Add(new FigmaIconControl
        {
            Name = $"Gallery{token}RenditionIcon",
            Asset = output.OrientationId == CompositionOrientationIds.Landscape
                ? FigmaIconAsset.Landscape
                : FigmaIconAsset.Portrait,
            IconColor = failed ? ClipCordTheme.Coral : ClipCordTheme.TextSecondary,
            Size = new Size(ScaleUi(24), ScaleUi(24)),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.None
        }, 0, 0);

        var copy = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = row.BackColor
        };
        copy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        copy.Controls.Add(new Label
        {
            Name = $"Gallery{token}RenditionTitle",
            Text = $"{output.DisplayName}  ·  {output.StatusLabel}{(playing ? "  ·  Playing" : string.Empty)}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9.2f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 0, 0);
        copy.Controls.Add(new Label
        {
            Name = $"Gallery{token}RenditionDetail",
            Text = output.Status switch
            {
                GalleryRenditionOutputStatus.Ready => GalleryView.FormatBytes(output.Length),
                GalleryRenditionOutputStatus.Failed => output.FailureReason ?? "This format could not be rendered.",
                GalleryRenditionOutputStatus.Processing => "Rendering locally…",
                GalleryRenditionOutputStatus.Waiting => "Waiting for local processing",
                _ => output.FailureReason ?? "Status unavailable"
            },
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = failed ? Color.FromArgb(255, 181, 190) : ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.2f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 0, 1);
        layout.Controls.Add(copy, 1, 0);
        if (stackActions)
        {
            layout.SetColumnSpan(copy, 2);
        }

        var actions = new FlowLayoutPanel
        {
            Name = $"Gallery{token}RenditionActions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = stackActions ? FlowDirection.TopDown : FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = row.BackColor
        };
        if (canPlay)
        {
            var play = CreateCardButton("Play", 72);
            play.Name = $"Gallery{token}RenditionPlayButton";
            play.Size = new Size(ScaleUi(72), ScaleUi(34));
            play.LeadingIcon = FigmaIconAsset.Play;
            play.AccessibleName = $"Play {output.DisplayName} rendition";
            play.Enabled = true;
            play.Click += (_, _) => PlayRendition(favoriteOwner, output);
            actions.Controls.Add(play);

            var folder = CreateCardButton("Folder", 82);
            folder.Name = $"Gallery{token}RenditionFolderButton";
            folder.Size = new Size(ScaleUi(82), ScaleUi(34));
            folder.LeadingIcon = FigmaIconAsset.Folder;
            folder.Margin = stackActions
                ? ScalePadding(0, 8, 0, 0)
                : ScalePadding(8, 0, 0, 0);
            folder.AccessibleName = $"Open the {output.DisplayName} rendition folder";
            folder.Enabled = true;
            folder.Click += (_, _) => ShowRenditionInFolder(output);
            actions.Controls.Add(folder);
        }
        else if (output.CanRetry)
        {
            var retry = CreateCardButton($"Retry {output.DisplayName}", 130);
            retry.Name = $"Gallery{token}RenditionRetryButton";
            retry.Size = new Size(ScaleUi(130), ScaleUi(34));
            retry.LeadingIcon = FigmaIconAsset.Refresh;
            retry.SurfaceColor = ClipCordTheme.VioletMuted;
            retry.HoverColor = Color.FromArgb(69, 53, 105);
            retry.OutlineColor = ClipCordTheme.Violet;
            retry.AccessibleName = $"Retry {output.DisplayName} rendition";
            retry.Enabled = canUseRendition && RenditionRetryRequested is not null;
            retry.Click += (_, _) => RequestRenditionRetry(retry, presentation, output);
            actions.Controls.Add(retry);
        }
        if (stackActions)
        {
            layout.Controls.Add(actions, 0, 1);
            layout.SetColumnSpan(actions, 3);
        }
        else
        {
            layout.Controls.Add(actions, 2, 0);
        }
        row.Controls.Add(layout);
        return row;
    }

    private Control BuildProjectSourceRow(GalleryRenditionPresentation presentation)
    {
        var source = new RoundedPanel
        {
            Name = "GalleryRenditionSourceFiles",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleUi(9),
            Padding = ScalePadding(10, 9, 10, 8),
            Margin = ScalePadding(0, 6, 0, 0),
            AccessibleName = "Untouched source files",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(30)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.Controls.Add(new FigmaIconControl
        {
            Name = "GalleryRenditionSourceIcon",
            Asset = FigmaIconAsset.Layers,
            IconColor = ClipCordTheme.TextTertiary,
            Size = new Size(ScaleUi(22), ScaleUi(22)),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.None
        }, 0, 0);
        layout.SetRowSpan(layout.Controls[0], 2);
        layout.Controls.Add(new Label
        {
            Name = "GalleryOriginalGameplaySource",
            Text = "Original gameplay clip · untouched",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 1, 0);
        layout.Controls.Add(new Label
        {
            Name = "GalleryReactionCameraSource",
            Text = "Sources stay untouched · ready formats are saved in this game folder",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.2f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty,
            UseMnemonic = false
        }, 1, 1);
        source.Controls.Add(layout);
        return source;
    }

    private static Control CreateRenditionStatusChip(
        string text,
        GalleryRenditionAggregateStatus status)
    {
        var (background, border, foreground) = status switch
        {
            GalleryRenditionAggregateStatus.Ready => (
                ClipCordTheme.SuccessSurface,
                ClipCordTheme.SuccessBorder,
                ClipCordTheme.SuccessText),
            GalleryRenditionAggregateStatus.Failed or GalleryRenditionAggregateStatus.PartiallyReady => (
                Color.FromArgb(54, 25, 39),
                ClipCordTheme.Coral,
                Color.FromArgb(255, 213, 216)),
            _ => (
                ClipCordTheme.VioletMuted,
                ClipCordTheme.Violet,
                Color.FromArgb(214, 203, 255))
        };
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = foreground,
            Font = ClipCordTheme.InterfaceFont(8.1f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Location = new Point(9, 4),
            Margin = Padding.Empty,
            UseMnemonic = false,
            AccessibleRole = AccessibleRole.None
        };
        return new RoundedPanel
        {
            Name = "GalleryRenditionStatusChip",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = background,
            BorderColor = border,
            CornerRadius = 8,
            Padding = new Padding(9, 4, 9, 4),
            Margin = Padding.Empty,
            AccessibleName = text,
            AccessibleRole = AccessibleRole.StaticText,
            Controls = { label }
        };
    }

    private void PlayRendition(
        GalleryClipEntry favoriteOwner,
        GalleryRenditionOutputPresentation output)
    {
        if (!HasCaptureLibraryAccess() || !output.CanPlay) return;
        var renditionClip = favoriteOwner with
        {
            Path = output.ArtifactPath,
            FileName = Path.GetFileName(output.ArtifactPath),
            Length = output.Length,
            DisplayFileName = null
        };
        ShowPlayer(renditionClip, favoriteOwner);
    }

    private void ShowRenditionInFolder(GalleryRenditionOutputPresentation output)
    {
        if (!HasCaptureLibraryAccess() || !output.CanPlay) return;
        try
        {
            Process.Start(ActivityView.CreateSelectFileStartInfo(output.ArtifactPath));
        }
        catch (Exception exception)
        {
            Log.Error($"Could not open the {output.DisplayName} rendition location.", exception);
            MessageBox.Show(
                this,
                "Windows could not open this rendition's location.",
                "Could not open folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RequestRenditionRetry(
        OutlineButton button,
        GalleryRenditionPresentation presentation,
        GalleryRenditionOutputPresentation output)
    {
        var handler = RenditionRetryRequested;
        if (!HasCaptureLibraryAccess() || handler is null || !output.CanRetry) return;
        button.Enabled = false;
        button.Text = "Retry requested";
        _renditionRetrySawActiveWork = false;
        _renditionRetryGracePolls = 3;
        UpdateRenditionRefreshTimer();
        handler.Invoke(
            this,
            new GalleryRenditionRetryRequestedEventArgs(
                presentation.LibraryRoot,
                presentation.ProjectId,
                output.OrientationId));
    }

    private void RenditionRefreshTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!HasCaptureLibraryAccess() ||
            !_active || _disposed || IsDisposed || Disposing ||
            _screen is not (GalleryScreen.Library or GalleryScreen.Game))
        {
            _renditionRefreshTimer.Stop();
            return;
        }
        if (_scanInProgress) return;
        if (HasRenditionStateChanged())
        {
            RefreshCatalog(_clipsFolder);
            return;
        }
        if (_renditionRetryGracePolls > 0)
        {
            _renditionRetryGracePolls--;
            UpdateRenditionRefreshTimer();
        }
    }

    private void ObserveRenditionPollResult()
    {
        if (HasActiveRenditionWork())
        {
            _renditionRetrySawActiveWork = true;
            _renditionRetryGracePolls = 0;
            return;
        }
        if (_renditionRetrySawActiveWork)
        {
            _renditionRetrySawActiveWork = false;
            _renditionRetryGracePolls = 0;
            return;
        }
        if (_renditionRetryGracePolls > 0) _renditionRetryGracePolls--;
    }

    private bool HasActiveRenditionWork() => _snapshot.Games
        .SelectMany(game => game.Clips)
        .Select(clip => clip.Renditions)
        .Any(renditions => renditions?.Status is GalleryRenditionAggregateStatus.Pending or
            GalleryRenditionAggregateStatus.Processing);

    private bool HasRenditionStateChanged() => _snapshot.Games
        .SelectMany(game => game.Clips)
        .Select(clip => clip.Renditions)
        .Where(renditions => renditions is not null)
        .Cast<GalleryRenditionPresentation>()
        .Any(renditions => GalleryCatalog.GetRenditionStateStamp(
            renditions.LibraryRoot,
            renditions.ProjectId) != renditions.StateStamp);

    private void UpdateRenditionRefreshTimer()
    {
        var shouldRun = HasCaptureLibraryAccess() &&
            _active && !_disposed && !IsDisposed && !Disposing &&
            (_screen is GalleryScreen.Library or GalleryScreen.Game) &&
            (HasActiveRenditionWork() || _renditionRetryGracePolls > 0);
        if (shouldRun)
        {
            if (!_renditionRefreshTimer.Enabled) _renditionRefreshTimer.Start();
        }
        else
        {
            _renditionRefreshTimer.Stop();
        }
    }

    private static string GetOrientationControlToken(string orientationId) => orientationId switch
    {
        CompositionOrientationIds.Landscape => "Landscape",
        CompositionOrientationIds.Portrait => "Portrait",
        _ => throw new ArgumentException(
            "The rendition orientation is not supported.",
            nameof(orientationId))
    };

    private void DisposePlayer()
    {
        if (_player is null) return;
        _player.StopPlayback();
        if (ReferenceEquals(_scrollHost.Content, _playerContent)) _scrollHost.Content = null;
        if (_playerContent is not null && !ReferenceEquals(_playerContent, _player))
        {
            _playerContent.Dispose();
        }
        else
        {
            _player.Dispose();
        }
        _player = null;
        _playerContent = null;
        _playerClip = null;
    }

    private void DisposeEditor()
    {
        if (_editor is null) return;
        _editor.BusyChanged -= EditorBusyChanged;
        _editor.Cancelled -= EditorCancelled;
        _editor.Completed -= EditorCompleted;
        _editor.CancelActiveOperation();
        if (ReferenceEquals(_scrollHost.Content, _editor)) _scrollHost.Content = null;
        _editor.Dispose();
        _editor = null;
        OperationBusyChanged?.Invoke(false);
    }

    private Control BuildClipCard(GalleryClipEntry clip)
    {
        var canAccessClip = CanAccessClip(clip);
        var gradient = GalleryCatalog.GetGradient(clip.GameName);
        var card = new RoundedPanel
        {
            Name = "GalleryClipCard",
            Height = ScaleUi(246),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleUi(13),
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            AccessibleName = clip.Renditions is null
                ? $"{clip.Title}, {GetRouteLabel(clip.Route)}"
                : $"{clip.Title}, {GetRouteLabel(clip.Route)}, {clip.Renditions.FormatsLabel}, {clip.Renditions.StatusLabel}",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(132)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(44)));

        var art = new GalleryThumbnailTile(
            GalleryCatalog.GetInitials(clip.GameName),
            gradient.Start,
            gradient.End)
        {
            Name = "GalleryClipArt",
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceChrome
        };
        _thumbnailClips.Add(art, clip);
        var routeBadge = CreateRouteBadge(clip.Route);
        routeBadge.Location = new Point(ScaleUi(10), ScaleUi(9));
        var renditionBadge = clip.Renditions is null
            ? null
            : CreateRenditionBadge(clip.Renditions);
        var favorite = new FavoriteButton(
            _favorites.IsFavorite(clip),
            logicalSize: 26,
            logicalIconSize: 14,
            revealWhenOff: true)
        {
            Name = "GalleryClipFavoriteButton",
            AccessibleName = $"Toggle Favorites for {clip.Title}"
        };
        var favoriteSide = GetFavoriteButtonSide(DeviceDpi);
        favorite.Size = new Size(favoriteSide, favoriteSide);
        favorite.Click += (_, _) =>
        {
            var next = !favorite.IsFavorite;
            if (_favorites.SetFavorite(clip, next)) favorite.SetFavorite(next);
        };
        var play = CreateCardButton("Play", 62);
        play.Size = new Size(ScaleUi(62), ScaleUi(36));
        play.Name = "PlayGalleryClipButton";
        play.AccessibleName = $"Play {clip.Title}";
        play.LeadingGlyph = BrandGlyph.Play;
        play.Enabled = canAccessClip && File.Exists(clip.Path);
        play.Click += (_, _) => PlayClip(clip);
        play.MouseEnter += (_, _) => BeginPlaybackPrewarm(clip);
        play.GotFocus += (_, _) => BeginPlaybackPrewarm(clip);
        void PlaceThumbnailControls()
        {
            play.Location = new Point(
                Math.Max(ScaleUi(8), (art.ClientSize.Width - play.Width) / 2),
                Math.Max(ScaleUi(8), (art.ClientSize.Height - play.Height) / 2));
            favorite.Location = new Point(
                Math.Max(ScaleUi(8), art.ClientSize.Width - favorite.Width - ScaleUi(10)),
                ScaleUi(9));
            if (renditionBadge is not null)
            {
                renditionBadge.Location = new Point(
                    ScaleUi(10),
                    Math.Max(ScaleUi(9), art.ClientSize.Height - renditionBadge.Height - ScaleUi(9)));
            }
        }
        art.MouseEnter += (_, _) => favorite.SetReveal(true);
        art.MouseLeave += (_, _) => favorite.SetReveal(false);
        favorite.MouseEnter += (_, _) => favorite.SetReveal(true);
        favorite.MouseLeave += (_, _) => favorite.SetReveal(false);
        art.Resize += (_, _) => PlaceThumbnailControls();
        art.Controls.Add(routeBadge);
        if (renditionBadge is not null) art.Controls.Add(renditionBadge);
        art.Controls.Add(favorite);
        art.Controls.Add(play);
        art.Layout += (_, _) =>
        {
            routeBadge.BringToFront();
            renditionBadge?.BringToFront();
            favorite.BringToFront();
            play.BringToFront();
            PlaceThumbnailControls();
        };
        routeBadge.BringToFront();
        renditionBadge?.BringToFront();
        favorite.BringToFront();
        play.BringToFront();
        PlaceThumbnailControls();
        layout.Controls.Add(art, 0, 0);

        var details = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = ScalePadding(11, 9, 11, 4),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var fileName = new Label
        {
            Text = clip.Title,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9.5f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        _toolTip.SetToolTip(fileName, clip.FileName);
        details.Controls.Add(fileName, 0, 0);
        details.Controls.Add(new Label
        {
            Text = clip.Renditions is null
                ? $"{clip.GameName} · {clip.SourceLabel} · {FormatBytes(clip.Length)} · {clip.LastWriteTimeUtc.ToLocalTime():t}"
                : $"{clip.GameName} · {clip.SourceLabel} · {clip.Renditions.FormatsLabel} · {clip.Renditions.StatusLabel}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.2f),
            Margin = ScalePadding(0, 3, 0, 0)
        }, 0, 1);
        layout.Controls.Add(details, 0, 1);

        var actions = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = ScalePadding(10, 2, 10, 8),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var canEdit = clip.Route == GalleryClipRoute.LocalOnly && _manualClipEditService is not null;
        if (canEdit)
        {
            var edit = CreateCardButton("Edit & upload", 112);
            edit.Size = new Size(ScaleUi(112), ScaleUi(36));
            edit.Name = "EditGalleryClipButton";
            edit.AccessibleName = $"Edit and upload {clip.Title}";
            edit.LeadingGlyph = BrandGlyph.Trim;
            edit.Dock = DockStyle.Fill;
            edit.SurfaceColor = ClipCordTheme.VioletMuted;
            edit.HoverColor = Color.FromArgb(69, 53, 105);
            edit.OutlineColor = ClipCordTheme.Violet;
            edit.ForeColor = ClipCordTheme.TextPrimary;
            edit.Enabled = canAccessClip && File.Exists(clip.Path);
            edit.Click += (_, _) => ShowEditor(clip);
            actions.Controls.Add(edit, 0, 0);
        }
        else
        {
            actions.Controls.Add(new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ClipCordTheme.SurfaceRaised,
                Margin = Padding.Empty,
                TabStop = false
            }, 0, 0);
        }
        var show = CreateCardButton("Folder", 76);
        show.Size = new Size(ScaleUi(76), ScaleUi(36));
        show.Name = "ShowGalleryClipButton";
        show.AccessibleName = $"Show {clip.Title} in its folder";
        show.LeadingGlyph = BrandGlyph.Folder;
        show.Margin = ScalePadding(8, 0, 0, 0);
        show.Enabled = canAccessClip && File.Exists(clip.Path);
        show.Click += (_, _) => ShowClipInFolder(clip);
        actions.Controls.Add(show, 1, 0);
        layout.Controls.Add(actions, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control CreateRouteBadge(GalleryClipRoute route)
    {
        var localOnly = route == GalleryClipRoute.LocalOnly;
        var label = new Label
        {
            Text = localOnly ? "●  Local only" : "●  Discord",
            AutoSize = true,
            ForeColor = localOnly ? Color.FromArgb(255, 213, 216) : Color.FromArgb(214, 203, 255),
            Font = ClipCordTheme.InterfaceFont(7.8f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Location = new Point(ScaleUi(7), ScaleUi(3)),
            UseMnemonic = false,
            AccessibleRole = AccessibleRole.None
        };
        return new RoundedPanel
        {
            Name = "GalleryClipRouteBadge",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = ClipCordTheme.SurfaceChrome,
            BorderColor = localOnly ? ClipCordTheme.Coral : ClipCordTheme.Violet,
            CornerRadius = ScaleUi(7),
            Padding = ScalePadding(7, 3, 7, 3),
            AccessibleName = localOnly ? "Local only clip" : "Uploaded to Discord",
            AccessibleRole = AccessibleRole.StaticText,
            Controls = { label }
        };
    }

    private Control CreateRenditionBadge(GalleryRenditionPresentation presentation)
    {
        var ready = presentation.Status == GalleryRenditionAggregateStatus.Ready;
        var attention = presentation.Status is GalleryRenditionAggregateStatus.Failed or
            GalleryRenditionAggregateStatus.PartiallyReady;
        var text = $"{presentation.FormatsLabel}  ·  {presentation.StatusLabel}";
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = attention
                ? Color.FromArgb(255, 213, 216)
                : ready ? ClipCordTheme.SuccessText : Color.FromArgb(214, 203, 255),
            Font = ClipCordTheme.InterfaceFont(7.6f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Location = new Point(ScaleUi(7), ScaleUi(3)),
            Margin = Padding.Empty,
            UseMnemonic = false,
            AccessibleRole = AccessibleRole.None
        };
        return new RoundedPanel
        {
            Name = "GalleryClipFormatsBadge",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = attention
                ? Color.FromArgb(54, 25, 39)
                : ready ? ClipCordTheme.SuccessSurface : ClipCordTheme.VioletMuted,
            BorderColor = attention
                ? ClipCordTheme.Coral
                : ready ? ClipCordTheme.SuccessBorder : ClipCordTheme.Violet,
            CornerRadius = ScaleUi(7),
            Padding = ScalePadding(7, 3, 7, 3),
            AccessibleName = text,
            AccessibleRole = AccessibleRole.StaticText,
            Controls = { label }
        };
    }

    private void PlayClip(GalleryClipEntry clip)
    {
        if (!CanAccessClip(clip) || !File.Exists(clip.Path)) return;
        var preferredRendition = SelectPreferredReadyRendition(clip);
        if (preferredRendition is not null)
        {
            PlayRendition(clip, preferredRendition);
            return;
        }
        ShowPlayer(clip);
    }

    internal static GalleryRenditionOutputPresentation? SelectPreferredReadyRendition(
        GalleryClipEntry clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var outputs = clip.Renditions?.Outputs;
        if (outputs is null) return null;

        return outputs.FirstOrDefault(output =>
                   output.OrientationId == CompositionOrientationIds.Landscape && output.CanPlay) ??
               outputs.FirstOrDefault(output =>
                   output.OrientationId == CompositionOrientationIds.Portrait && output.CanPlay);
    }

    internal static bool ShouldStackRenditionActions(int dpi) => dpi >= 288;

    private int EffectiveDpi => _effectiveDpiForTests ?? DeviceDpi;

    internal static int GetRenditionOutputRowLogicalHeight(int dpi, bool canPlay) =>
        ShouldStackRenditionActions(dpi) && canPlay ? 148 : 82;

    private void BeginPlaybackPrewarm(GalleryClipEntry clip)
    {
        if (!CanAccessClip(clip) ||
            !_active || _disposed || IsDisposed || Disposing || !File.Exists(clip.Path)) return;
        if (string.Equals(_playbackPrewarmPath, clip.Path, StringComparison.OrdinalIgnoreCase) &&
            _playbackPrewarmCancellation is { IsCancellationRequested: false })
        {
            return;
        }

        CancelPlaybackPrewarm();
        var cancellation = new CancellationTokenSource();
        _playbackPrewarmCancellation = cancellation;
        _playbackPrewarmPath = clip.Path;
        _ = PrewarmPlaybackAsync(clip, cancellation);
    }

    private async Task PrewarmPlaybackAsync(
        GalleryClipEntry clip,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _playbackPreparer
                .PrepareAsync(clip.Path, cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error($"Could not prewarm Gallery playback for {clip.FileName}.", exception);
        }
        finally
        {
            cancellation.Dispose();
            _uiContext.Post(_ =>
            {
                if (!ReferenceEquals(_playbackPrewarmCancellation, cancellation)) return;
                _playbackPrewarmCancellation = null;
                _playbackPrewarmPath = null;
            }, null);
        }
    }

    private void CancelPlaybackPrewarm()
    {
        var cancellation = _playbackPrewarmCancellation;
        _playbackPrewarmCancellation = null;
        _playbackPrewarmPath = null;
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ShowClipInFolder(GalleryClipEntry clip)
    {
        if (!CanAccessClip(clip) || !File.Exists(clip.Path)) return;
        try
        {
            Process.Start(ActivityView.CreateSelectFileStartInfo(clip.Path));
        }
        catch (Exception exception)
        {
            Log.Error($"Could not open the Gallery clip location for {clip.FileName}.", exception);
            MessageBox.Show(this, "Windows could not open this clip's location.", "Could not open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenClipsFolder()
    {
        if (!Directory.Exists(_clipsFolder)) return;
        try
        {
            Process.Start(ActivityView.CreateOpenFolderStartInfo(_clipsFolder));
        }
        catch (Exception exception)
        {
            Log.Error("Could not open the Gallery clips folder.", exception);
            MessageBox.Show(this, "Windows could not open the clips folder.", "Could not open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string BuildLibrarySummary(GallerySnapshot snapshot)
    {
        if (snapshot.Games.Count == 0)
        {
            return snapshot.Warnings.Count > 0
                ? string.Join(" ", snapshot.Warnings)
                : "Uploaded and local-only clips appear together by game.";
        }
        var summary = $"{FormatClipCount(snapshot.TotalClips)} across {snapshot.Games.Count} game{(snapshot.Games.Count == 1 ? string.Empty : "s")} · {snapshot.UploadedCount} uploaded · {snapshot.LocalOnlyCount} local only";
        return snapshot.Warnings.Count == 0 ? summary : summary + " · Some clips could not be read";
    }

    private string BuildFavoritesSummary(int visibleCount)
    {
        var favorites = _snapshot.Games
            .SelectMany(game => game.Clips)
            .Where(_favorites.IsFavorite)
            .ToArray();
        if (favorites.Length == 0) return "Keep the moments you want to find again.";
        var games = favorites
            .Select(clip => clip.GameName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var baseSummary = $"{FormatClipCount(favorites.Length)} across {games} game{(games == 1 ? string.Empty : "s")}";
        return visibleCount == favorites.Length
            ? baseSummary
            : $"Showing {FormatClipCount(visibleCount)} · {baseSummary}";
    }

    private void FavoritesChanged()
    {
        _uiContext.Post(_ =>
        {
            if (_disposed || IsDisposed || Disposing) return;
            if (_screen is GalleryScreen.Library or GalleryScreen.Game)
            {
                RenderLibrary();
                return;
            }
            if (_screen == GalleryScreen.Player && _player is not null && _playerClip is not null)
            {
                _player.SetFavoriteState(_favorites.IsFavorite(_playerClip));
            }
        }, null);
    }

    private string BuildGameSummary(GalleryGameEntry game, int visibleCount)
    {
        var baseSummary = $"{FormatClipCount(game.Clips.Count)} · {game.UploadedCount} uploaded · {game.LocalOnlyCount} local only · {FormatBytes(game.TotalBytes)}";
        return _routeFilter is null ? baseSummary : $"Showing {FormatClipCount(visibleCount)} · {baseSummary}";
    }

    private static string FormatClipCount(int count) => $"{count} clip{(count == 1 ? string.Empty : "s")}";

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return FormattableString.Invariant($"{bytes / 1024d / 1024d / 1024d:F1} GB");
        if (bytes >= 1024L * 1024) return FormattableString.Invariant($"{bytes / 1024d / 1024d:F1} MB");
        if (bytes >= 1024L) return FormattableString.Invariant($"{bytes / 1024d:F1} KB");
        return $"{Math.Max(0, bytes)} B";
    }

    private static string GetRouteLabel(GalleryClipRoute route) => route == GalleryClipRoute.Uploaded
        ? "Uploaded"
        : "Local only";

    private static OutlineButton CreateShellButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 38,
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        DisabledSurfaceColor = Color.FromArgb(20, 30, 46),
        DisabledTextColor = ClipCordTheme.TextTertiary,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9f),
        Margin = Padding.Empty
    };

    private static OutlineButton CreateFilterButton(string text, string name, Action clicked)
    {
        var button = CreateShellButton(text, text.Length > 6 ? 104 : 72);
        button.Name = name;
        button.Height = 32;
        button.AccessibleName = $"Show {text.ToLowerInvariant()} clips";
        button.Click += (_, _) => clicked();
        return button;
    }

    private static OutlineButton CreateCardButton(string text, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Height = 36,
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        DisabledSurfaceColor = Color.FromArgb(27, 37, 54),
        DisabledTextColor = ClipCordTheme.TextTertiary,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.5f),
        Margin = Padding.Empty
    };

    private Control CreateEmptyState(string text)
    {
        var panel = new RoundedPanel
        {
            Name = "GalleryEmptyState",
            Height = ScaleUi(160),
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleUi(16),
            Padding = new Padding(ScaleUi(24)),
            Margin = Padding.Empty
        };
        panel.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.MutedText,
            Font = ClipCordTheme.InterfaceFont(10.5f),
            TextAlign = ContentAlignment.MiddleCenter
        });
        return panel;
    }

    private static void DisposeChildren(Control parent)
    {
        var controls = parent.Controls.Cast<Control>().ToArray();
        parent.Controls.Clear();
        foreach (var control in controls) control.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _active = false;
            _scanInProgress = false;
            _renditionRefreshTimer.Stop();
            _renditionRefreshTimer.Tick -= RenditionRefreshTimerTick;
            _renditionRefreshTimer.Dispose();
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            CancelThumbnailRequests();
            CancelPlaybackPrewarm();
            DisposeEditor();
            DisposePlayer();
            _favorites.Changed -= FavoritesChanged;
            _toolTip.Dispose();
            _clipGrid.Dispose();
            _gameFilterList.Dispose();
        }
        base.Dispose(disposing);
    }

    private enum GalleryScreen
    {
        Library,
        Game,
        Editor,
        Player
    }

    private bool CanAccessClip(GalleryClipEntry clip) =>
        !IsCaptureLibrarySource(clip.Source) || HasCaptureLibraryAccess();

    private static bool IsCaptureLibrarySource(GalleryClipSource source) =>
        source is GalleryClipSource.ClipCord or GalleryClipSource.Xbox;

    private bool HasCaptureLibraryAccess()
    {
        try
        {
            return _captureLibraryAccessAllowed();
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not validate Capture library access for Gallery.", exception);
            return false;
        }
    }

    private static GallerySnapshot WithoutCaptureLibraryEntries(GallerySnapshot? snapshot)
    {
        if (snapshot is null) return new GallerySnapshot([], []);
        var games = snapshot.Games
            .Select(game => new GalleryGameEntry(
                game.Name,
                game.Clips
                    .Where(clip => !IsCaptureLibrarySource(clip.Source))
                    .ToArray()))
            .Where(game => game.Clips.Count > 0)
            .ToArray();
        return new GallerySnapshot(games, snapshot.Warnings);
    }

    /// <summary>
    /// Stacks the rendition selector and player at the width assigned by the branded
    /// scroll host. A stock auto-sized TableLayoutPanel collapses to its 150px default
    /// width after the host assigns its bounds, which made the player surface one pixel
    /// tall and pushed the rendition actions outside their rows.
    /// </summary>
    private sealed class GalleryProjectDetailPanel : Panel
    {
        private readonly Control _renditionPanel;
        private readonly ClipPlayerView _player;
        private readonly int _gap;
        private bool _layingOut;

        internal GalleryProjectDetailPanel(
            Control renditionPanel,
            ClipPlayerView player,
            int gap)
        {
            _renditionPanel = renditionPanel;
            _player = player;
            _gap = Math.Max(0, gap);
            AutoSize = false;
            Dock = DockStyle.None;
            _renditionPanel.AutoSize = false;
            _renditionPanel.Dock = DockStyle.None;
            _renditionPanel.Margin = Padding.Empty;
            _player.AutoSize = false;
            _player.Dock = DockStyle.None;
            _player.Margin = Padding.Empty;
            Controls.Add(_renditionPanel);
            Controls.Add(_player);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var width = proposedSize.Width is > 0 and < int.MaxValue
                ? proposedSize.Width
                : Math.Max(1, Width);
            var renditionHeight = MeasureHeight(_renditionPanel, width);
            var playerHeight = MeasureHeight(_player, width);
            return new Size(
                width,
                (int)Math.Clamp(
                    (long)renditionHeight + _gap + playerHeight,
                    1,
                    int.MaxValue));
        }

        protected override void OnLayout(LayoutEventArgs eventArgs)
        {
            base.OnLayout(eventArgs);
            if (_layingOut) return;
            _layingOut = true;
            try
            {
                var width = Math.Max(1, ClientSize.Width);
                var renditionHeight = MeasureHeight(_renditionPanel, width);
                _renditionPanel.Bounds = new Rectangle(0, 0, width, renditionHeight);
                var playerTop = renditionHeight + _gap;
                _player.Bounds = new Rectangle(
                    0,
                    playerTop,
                    width,
                    MeasureHeight(_player, width));
            }
            finally
            {
                _layingOut = false;
            }
        }

        private static int MeasureHeight(Control control, int width)
        {
            var preferred = control.GetPreferredSize(new Size(Math.Max(1, width), int.MaxValue));
            return Math.Max(control.MinimumSize.Height, Math.Max(1, preferred.Height));
        }
    }
}

internal sealed class GalleryGameFilterButton : Control
{
    internal const TextFormatFlags CountTextFlags =
        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private readonly string _label;
    private readonly int _count;
    private readonly Action _select;
    private readonly FigmaIconAsset? _leadingIcon;
    private readonly Color _leadingIconColor;
    private bool _hovered;

    internal GalleryGameFilterButton(
        string label,
        int count,
        bool selected,
        Action select,
        FigmaIconAsset? leadingIcon = null,
        Color? leadingIconColor = null)
    {
        _label = label;
        _count = Math.Max(0, count);
        _select = select;
        _leadingIcon = leadingIcon;
        _leadingIconColor = leadingIconColor ?? ClipCordTheme.TextSecondary;
        Selected = selected;
        Name = label switch
        {
            "All clips" => "GalleryAllGamesFilterButton",
            "Favorites" => "GalleryFavoritesFilterButton",
            _ => "GalleryGameFilterButton"
        };
        Height = 36;
        Margin = new Padding(0, 0, 0, 2);
        BackColor = ClipCordTheme.SurfaceRaised;
        Font = ClipCordTheme.InterfaceFont(8.8f);
        Cursor = Cursors.Hand;
        TabStop = true;
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = $"Show {label}, {_count} clips";
        AccessibleDescription = selected ? "Selected game filter" : string.Empty;
        SetStyle(ControlStyles.Selectable, true);
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
    }

    internal bool Selected { get; }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left) Focus();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        base.OnClick(eventArgs);
        _select();
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode is Keys.Enter or Keys.Space)
        {
            _select();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, Math.Max(6, Height / 5));
        var fillColor = Selected
            ? ClipCordTheme.VioletMuted
            : _hovered ? ClipCordTheme.SurfaceControlHover : ClipCordTheme.SurfaceRaised;
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(Selected ? ClipCordTheme.Violet : Color.Transparent);
        eventArgs.Graphics.FillPath(fill, path);
        if (Selected) eventArgs.Graphics.DrawPath(border, path);

        var scale = Math.Max(1f, Height / 36f);
        var inset = Math.Max(8, (int)Math.Round(10 * scale));
        var iconSize = _leadingIcon is null ? 0 : Math.Max(12, (int)Math.Round(14 * scale));
        var iconGap = _leadingIcon is null ? 0 : Math.Max(5, (int)Math.Round(7 * scale));
        var countText = _count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var countSize = TextRenderer.MeasureText(
            countText,
            ClipCordTheme.InterfaceFont(8.2f),
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _label,
            Font,
            new Rectangle(
                inset + iconSize + iconGap,
                0,
                Math.Max(1, Width - inset * 2 - countSize.Width - 8 - iconSize - iconGap),
                Height),
            Selected ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            countText,
            ClipCordTheme.InterfaceFont(8.2f),
            new Rectangle(Math.Max(inset, Width - inset - countSize.Width), 0, countSize.Width, Height),
            ClipCordTheme.TextTertiary,
            CountTextFlags);
        if (_leadingIcon is not null)
        {
            FigmaIconRenderer.Draw(
                eventArgs.Graphics,
                new Rectangle(inset, (Height - iconSize) / 2, iconSize, iconSize),
                _leadingIcon.Value,
                _leadingIconColor);
        }
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}

internal sealed class FavoriteButton : Control
{
    private readonly int _logicalSize;
    private readonly int _logicalIconSize;
    private readonly bool _revealWhenOff;
    private bool _revealed;
    private bool _hovered;

    internal FavoriteButton(
        bool isFavorite,
        int logicalSize,
        int logicalIconSize,
        bool revealWhenOff)
    {
        _logicalSize = logicalSize;
        _logicalIconSize = logicalIconSize;
        _revealWhenOff = revealWhenOff;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);
        Size = new Size(logicalSize, logicalSize);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        SetFavorite(isFavorite);
        MouseEnter += (_, _) =>
        {
            _hovered = true;
            _revealed = true;
            Invalidate();
        };
        MouseLeave += (_, _) =>
        {
            _hovered = false;
            Invalidate();
        };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
    }

    internal bool IsFavorite { get; private set; }

    internal void SetFavorite(bool favorite)
    {
        IsFavorite = favorite;
        AccessibleDescription = favorite ? "In Favorites" : "Not in Favorites";
        if (IsHandleCreated)
        {
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
        Invalidate();
    }

    internal void SetReveal(bool reveal)
    {
        if (!_revealWhenOff || _revealed == reveal) return;
        _revealed = reveal;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left) Focus();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new FavoriteButtonAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 1 || Height <= 1 ||
            (!IsFavorite && _revealWhenOff && !_revealed && !Focused))
        {
            return;
        }

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Math.Max(
            0.1f,
            Math.Min(Width / (float)_logicalSize, Height / (float)_logicalSize));
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var fillColor = IsFavorite
            ? Color.FromArgb(58, 34, 48)
            : _hovered ? ClipCordTheme.SurfaceControlHover : ClipCordTheme.SurfaceSunken;
        var borderColor = IsFavorite ? ClipCordTheme.Coral : ClipCordTheme.BorderStrong;
        using (var path = RoundedPanel.CreateRoundedPath(bounds, Math.Max(5, (int)Math.Round(9 * scale))))
        using (var fill = new SolidBrush(fillColor))
        using (var border = new Pen(borderColor, Math.Max(1f, scale)))
        {
            eventArgs.Graphics.FillPath(fill, path);
            eventArgs.Graphics.DrawPath(border, path);
        }

        var iconSize = Math.Max(1, (int)Math.Round(_logicalIconSize * scale));
        FigmaIconRenderer.Draw(
            eventArgs.Graphics,
            new Rectangle(
                (Width - iconSize) / 2,
                (Height - iconSize) / 2,
                iconSize,
                iconSize),
            IsFavorite ? FigmaIconAsset.HeartFill : FigmaIconAsset.Heart,
            IsFavorite ? ClipCordTheme.Coral : Color.FromArgb(135, 146, 168));
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
        }
    }

    private sealed class FavoriteButtonAccessibleObject(FavoriteButton owner)
        : Control.ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State |
            (owner.IsFavorite ? AccessibleStates.Checked : AccessibleStates.None);
    }
}

internal sealed class GalleryGridPanel : Panel
{
    private const int LogicalGap = 14;
    private const int LogicalTargetCardWidth = 220;
    private const int LogicalCardHeight = 246;
    private bool _reflowing;
    private int _contentHeight = 1;
    private float _syntheticScale = 1f;

    internal GalleryGridPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    internal void Reflow()
    {
        if (_reflowing) return;
        _reflowing = true;
        try
        {
            var gap = ScaleLogical(LogicalGap);
            var targetCardWidth = ScaleLogical(LogicalTargetCardWidth);
            var cardHeight = ScaleLogical(LogicalCardHeight);
            var availableWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            var columns = Math.Max(1, (availableWidth + gap) / (targetCardWidth + gap));
            var cardWidth = Math.Max(1, (availableWidth - gap * (columns - 1)) / columns);
            var index = 0;
            var emptyState = Controls.Count == 1 && Controls[0].Name == "GalleryEmptyState";
            foreach (Control control in Controls)
            {
                var row = index / columns;
                var column = index % columns;
                control.Dock = DockStyle.None;
                control.Bounds = new Rectangle(
                    emptyState ? Padding.Left : Padding.Left + column * (cardWidth + gap),
                    Padding.Top + row * (cardHeight + gap),
                    emptyState ? availableWidth : cardWidth,
                    emptyState ? ScaleLogical(160) : cardHeight);
                index++;
            }
            if (emptyState)
            {
                _contentHeight = Math.Max(1, Padding.Vertical + ScaleLogical(160));
            }
            else
            {
                var rows = Controls.Count == 0 ? 0 : (Controls.Count + columns - 1) / columns;
                _contentHeight = Math.Max(1, Padding.Vertical + rows * cardHeight + Math.Max(0, rows - 1) * gap);
            }
        }
        finally
        {
            _reflowing = false;
        }
    }

    internal int MeasureContentHeight()
    {
        Reflow();
        return _contentHeight;
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Math.Max(1, proposedSize.Width), MeasureContentHeight());

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        Reflow();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        _syntheticScale = 1f;
        Reflow();
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

    private int ScaleLogical(int value)
    {
        var dpiScale = Math.Max(1f, DeviceDpi / 96f);
        return Math.Max(1, (int)Math.Round(value * Math.Max(dpiScale, _syntheticScale)));
    }
}

internal sealed class GalleryGameCard : Control
{
    private readonly GalleryGameEntry _game;
    private readonly Action _open;
    private readonly GalleryGradient _gradient;
    private bool _hovered;
    private Size _lastRegionSize = Size.Empty;

    internal GalleryGameCard(GalleryGameEntry game, Action open)
    {
        _game = game;
        _open = open;
        _gradient = GalleryCatalog.GetGradient(game.Name);
        Name = "GalleryGameCard";
        Height = 210;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = $"Open {game.Name}, {game.Clips.Count} clips, {game.UploadedCount} uploaded, {game.LocalOnlyCount} local only";
        DoubleBuffered = true;
        Resize += (_, _) => UpdateRegion();
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        Focus();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        base.OnClick(eventArgs);
        _open();
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode is Keys.Enter or Keys.Space)
        {
            _open();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new SolidBrush(ClipCordTheme.Card))
        {
            eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        }
        var artBounds = new Rectangle(0, 0, Width, 145);
        using (var gradient = new LinearGradientBrush(artBounds, _gradient.Start, _gradient.End, 28f))
        {
            eventArgs.Graphics.FillRectangle(gradient, artBounds);
        }
        using (var glow = new SolidBrush(Color.FromArgb(36, Color.White)))
        {
            eventArgs.Graphics.FillEllipse(glow, Width - 125, -25, 145, 145);
        }
        TextRenderer.DrawText(
            eventArgs.Graphics,
            GalleryCatalog.GetInitials(_game.Name),
            ClipCordTheme.DisplayFont(24f, FontStyle.Bold),
            new Rectangle(16, 18, Math.Max(1, Width - 32), 88),
            Color.FromArgb(225, 255, 255, 255),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _game.Name,
            ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            new Rectangle(16, 153, Math.Max(1, Width - 32), 24),
            ClipCordTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            $"{_game.Clips.Count} clips  •  {_game.UploadedCount} uploaded  •  {_game.LocalOnlyCount} local",
            ClipCordTheme.InterfaceFont(8.5f),
            new Rectangle(16, 180, Math.Max(1, Width - 32), 20),
            ClipCordTheme.MutedText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        using var borderPath = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 13);
        using var border = new Pen(_hovered || Focused ? ClipCordTheme.Violet : ClipCordTheme.CardBorder, _hovered || Focused ? 2f : 1f);
        eventArgs.Graphics.DrawPath(border, borderPath);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
        }
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width, Height), 13);
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class GalleryThumbnailTile : Panel
{
    private readonly string _initials;
    private readonly Color _start;
    private readonly Color _end;
    private Bitmap? _thumbnail;

    internal GalleryThumbnailTile(string initials, Color start, Color end)
    {
        _initials = initials;
        _start = start;
        _end = end;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        TabStop = false;
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Grouping;
    }

    internal bool HasThumbnail => _thumbnail is not null;

    internal void SetThumbnail(Bitmap thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        if (IsDisposed || Disposing)
        {
            thumbnail.Dispose();
            return;
        }
        var previous = _thumbnail;
        _thumbnail = thumbnail;
        previous?.Dispose();
        AccessibleDescription = "Beginning frame loaded";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 0 || Height <= 0) return;
        using var brush = new LinearGradientBrush(ClientRectangle, _start, _end, 35f);
        eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
        if (_thumbnail is not null)
        {
            eventArgs.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var source = CalculateCoverSource(_thumbnail.Size, ClientSize);
            eventArgs.Graphics.DrawImage(
                _thumbnail,
                ClientRectangle,
                source.X,
                source.Y,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel);
            using var overlay = new SolidBrush(Color.FromArgb(34, 4, 8, 18));
            eventArgs.Graphics.FillRectangle(overlay, ClientRectangle);
            return;
        }

        TextRenderer.DrawText(
            eventArgs.Graphics,
            _initials,
            ClipCordTheme.DisplayFont(15f, FontStyle.Bold),
            ClientRectangle,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    internal static RectangleF CalculateCoverSource(Size source, Size destination)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0)
        {
            return RectangleF.Empty;
        }
        var destinationRatio = destination.Width / (double)destination.Height;
        var sourceRatio = source.Width / (double)source.Height;
        if (sourceRatio > destinationRatio)
        {
            var width = (float)(source.Height * destinationRatio);
            return new RectangleF((source.Width - width) / 2f, 0f, width, source.Height);
        }
        var height = (float)(source.Width / destinationRatio);
        return new RectangleF(0f, (source.Height - height) / 2f, source.Width, height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail?.Dispose();
            _thumbnail = null;
        }
        base.Dispose(disposing);
    }
}
