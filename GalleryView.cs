using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal sealed class GalleryView : UserControl
{
    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private readonly SynchronizationContext _uiContext;
    private readonly Label _headingLabel;
    private readonly Label _summaryLabel;
    private readonly OutlineButton _backButton;
    private readonly OutlineButton _refreshButton;
    private readonly OutlineButton _openClipsButton;
    private readonly GalleryGridPanel _gameGrid;
    private readonly ActivityListPanel _clipList;
    private readonly BrandedScrollHost _scrollHost;
    private CancellationTokenSource? _scanCancellation;
    private string _clipsFolder;
    private GallerySnapshot _snapshot = new([], []);
    private GalleryGameEntry? _selectedGame;
    private bool _active;
    private bool _disposed;

    internal GalleryView(string clipsFolder)
    {
        _clipsFolder = clipsFolder;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Name = "GalleryView";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        AccessibleName = "Clip gallery";

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(26, 12, 26, 12),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 12),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headingLabel = new Label
        {
            Name = "GalleryHeading",
            Text = "Gallery",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.DisplayFont(18f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        _summaryLabel = new Label
        {
            Name = "GallerySummary",
            Text = "Uploaded and local-only clips appear together by game.",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellMutedText,
            Font = ClipCordTheme.InterfaceFont(9.5f),
            Margin = new Padding(0, 2, 0, 0)
        };
        header.Controls.Add(_headingLabel, 0, 0);
        header.Controls.Add(_summaryLabel, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _backButton = CreateShellButton("All games", 105);
        _backButton.Name = "GalleryBackButton";
        _backButton.Visible = false;
        _backButton.Click += (_, _) => ShowLibrary();
        _refreshButton = CreateShellButton("Refresh", 94);
        _refreshButton.Name = "RefreshGalleryButton";
        _refreshButton.Margin = new Padding(10, 0, 0, 0);
        _refreshButton.Click += (_, _) => RefreshCatalog(_clipsFolder);
        _openClipsButton = CreateShellButton("Open clips folder", 155);
        _openClipsButton.Name = "OpenClipsFolderButton";
        _openClipsButton.Margin = new Padding(10, 0, 0, 0);
        _openClipsButton.Click += (_, _) => OpenClipsFolder();
        actions.Controls.Add(_backButton);
        actions.Controls.Add(_refreshButton);
        actions.Controls.Add(_openClipsButton);
        header.Controls.Add(actions, 1, 0);
        header.SetRowSpan(actions, 2);

        _gameGrid = new GalleryGridPanel
        {
            Name = "GalleryGameGrid",
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _clipList = new ActivityListPanel
        {
            Name = "GalleryClipList",
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "GalleryScrollHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            AccessibleName = "Clip gallery",
            Content = _gameGrid
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_scrollHost, 0, 1);
        Controls.Add(root);
    }

    internal async void RefreshCatalog(string clipsFolder)
    {
        if (_disposed || IsDisposed || Disposing || !_active) return;
        _clipsFolder = clipsFolder;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _refreshButton.Enabled = false;
        _summaryLabel.Text = "Scanning uploaded and local-only clips…";
        GallerySnapshot? snapshot = null;
        Exception? failure = null;
        try
        {
            if (string.IsNullOrWhiteSpace(clipsFolder) || !Directory.Exists(clipsFolder))
            {
                snapshot = new GallerySnapshot([], ["The clips folder is not available."]);
            }
            else
            {
                snapshot = await Task.Run(
                    () => GalleryCatalog.Scan(clipsFolder, cancellation.Token),
                    cancellation.Token).ConfigureAwait(false);
            }
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
        _refreshButton.Enabled = true;
        if (failure is not null)
        {
            Log.Error("Could not refresh the clip Gallery.", failure);
            snapshot = new GallerySnapshot([], ["ClipCord could not read the clip archive."]);
        }
        _snapshot = snapshot ?? new GallerySnapshot([], []);
        if (_selectedGame is not null)
        {
            _selectedGame = _snapshot.Games.FirstOrDefault(game =>
                game.Name.Equals(_selectedGame.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (_selectedGame is null) ShowLibrary();
        else ShowGame(_selectedGame);
    }

    internal void Activate(string clipsFolder)
    {
        _active = true;
        RefreshCatalog(clipsFolder);
        RefreshViewport();
    }

    internal void Deactivate()
    {
        _active = false;
        _scanCancellation?.Cancel();
        if (!_disposed && !IsDisposed && !Disposing) _refreshButton.Enabled = true;
    }

    internal void RefreshViewport()
    {
        PerformLayout();
        _scrollHost.RefreshContentLayout();
    }

    internal static ProcessStartInfo CreatePlayClipStartInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ProcessStartInfo(path) { UseShellExecute = true };
    }

    private void ShowLibrary()
    {
        _selectedGame = null;
        _headingLabel.Text = "Gallery";
        _backButton.Visible = false;
        _gameGrid.SuspendLayout();
        try
        {
            DisposeChildren(_gameGrid);
            foreach (var game in _snapshot.Games)
            {
                _gameGrid.Controls.Add(new GalleryGameCard(game, () => ShowGame(game)));
            }
        }
        finally
        {
            _gameGrid.ResumeLayout(true);
        }

        if (_snapshot.Games.Count == 0)
        {
            var message = _snapshot.Warnings.Count > 0
                ? string.Join(" ", _snapshot.Warnings)
                : "No archived clips yet. Uploaded and local-only clips will appear here.";
            _gameGrid.Controls.Add(CreateEmptyState(message));
        }
        _summaryLabel.Text = BuildLibrarySummary(_snapshot);
        _scrollHost.Content = _gameGrid;
        _scrollHost.RefreshContentLayout(preservePosition: false);
    }

    private void ShowGame(GalleryGameEntry game)
    {
        _selectedGame = game;
        _headingLabel.Text = game.Name;
        _summaryLabel.Text = $"{FormatClipCount(game.Clips.Count)} · {game.UploadedCount} uploaded · {game.LocalOnlyCount} local only · {FormatBytes(game.TotalBytes)}";
        _backButton.Visible = true;
        _clipList.SuspendLayout();
        try
        {
            DisposeChildren(_clipList);
            foreach (var clip in game.Clips)
            {
                _clipList.Controls.Add(BuildClipRow(clip));
            }
        }
        finally
        {
            _clipList.ResumeLayout(true);
        }
        _scrollHost.Content = _clipList;
        _scrollHost.RefreshContentLayout(preservePosition: false);
    }

    private Control BuildClipRow(GalleryClipEntry clip)
    {
        var gradient = GalleryCatalog.GetGradient(clip.GameName);
        var card = new RoundedPanel
        {
            Name = "GalleryClipCard",
            Height = 92,
            BackColor = ClipCordTheme.Card,
            BorderColor = ClipCordTheme.CardBorder,
            CornerRadius = 13,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = $"{clip.FileName}, {GetRouteLabel(clip.Route)}"
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // OutlineButton grows to its rendered text width at higher DPI, so reserve
        // enough room for both actions plus their spacing and right padding.
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new GalleryGradientTile(
            GalleryCatalog.GetInitials(clip.GameName),
            gradient.Start,
            gradient.End), 0, 0);

        var details = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16, 14, 8, 12),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var fileName = new Label
        {
            Text = clip.FileName,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.Text,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        _toolTip.SetToolTip(fileName, clip.FileName);
        details.Controls.Add(fileName, 0, 0);
        details.Controls.Add(new Label
        {
            Text = $"{GetRouteLabel(clip.Route)}  •  {clip.LastWriteTimeUtc.ToLocalTime():g}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = clip.Route == GalleryClipRoute.Uploaded
                ? Color.FromArgb(38, 145, 96)
                : ClipCordTheme.Violet,
            Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 0)
        }, 0, 1);
        details.Controls.Add(new Label
        {
            Text = FormatBytes(clip.Length),
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.MutedText,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = new Padding(0, 3, 0, 0)
        }, 0, 2);
        layout.Controls.Add(details, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 27, 12, 0),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        var play = CreateCardButton("Play clip", 90);
        play.Name = "PlayGalleryClipButton";
        play.AccessibleName = $"Play {clip.FileName}";
        play.Enabled = File.Exists(clip.Path);
        play.Click += (_, _) => PlayClip(clip);
        var show = CreateCardButton("Show in folder", 105);
        show.Name = "ShowGalleryClipButton";
        show.AccessibleName = $"Show {clip.FileName} in its folder";
        show.Margin = new Padding(8, 0, 0, 0);
        show.Enabled = File.Exists(clip.Path);
        show.Click += (_, _) => ShowClipInFolder(clip);
        actions.Controls.Add(play);
        actions.Controls.Add(show);
        layout.Controls.Add(actions, 2, 0);
        card.Controls.Add(layout);
        return card;
    }

    private void PlayClip(GalleryClipEntry clip)
    {
        if (!File.Exists(clip.Path)) return;
        try
        {
            Process.Start(CreatePlayClipStartInfo(clip.Path));
        }
        catch (Exception exception)
        {
            Log.Error($"Could not play Gallery clip {clip.FileName}.", exception);
            MessageBox.Show(this, "Windows could not open this clip.", "Could not play clip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowClipInFolder(GalleryClipEntry clip)
    {
        if (!File.Exists(clip.Path)) return;
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
        SurfaceColor = Color.FromArgb(25, 35, 52),
        HoverColor = Color.FromArgb(35, 46, 65),
        OutlineColor = Color.FromArgb(65, 76, 96),
        ForeColor = ClipCordTheme.ShellText,
        Font = ClipCordTheme.InterfaceFont(9f),
        Margin = Padding.Empty
    };

    private static OutlineButton CreateCardButton(string text, int width) => new()
    {
        Text = text,
        Width = width,
        Height = 36,
        SurfaceColor = Color.White,
        HoverColor = Color.FromArgb(246, 243, 253),
        OutlineColor = ClipCordTheme.CardBorder,
        ForeColor = ClipCordTheme.Text,
        Font = ClipCordTheme.InterfaceFont(8.5f),
        Margin = Padding.Empty
    };

    private static Control CreateEmptyState(string text)
    {
        var panel = new RoundedPanel
        {
            Name = "GalleryEmptyState",
            Height = 160,
            BackColor = ClipCordTheme.Card,
            BorderColor = ClipCordTheme.CardBorder,
            CornerRadius = 16,
            Padding = new Padding(24),
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
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
            _toolTip.Dispose();
            _gameGrid.Dispose();
            _clipList.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class GalleryGridPanel : Panel
{
    private const int Gap = 14;
    private const int TargetCardWidth = 270;
    private const int CardHeight = 210;
    private bool _reflowing;
    private int _contentHeight = 1;

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
            var availableWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            var columns = Math.Max(1, (availableWidth + Gap) / (TargetCardWidth + Gap));
            var cardWidth = Math.Max(1, (availableWidth - Gap * (columns - 1)) / columns);
            var index = 0;
            var emptyState = Controls.Count == 1 && Controls[0].Name == "GalleryEmptyState";
            foreach (Control control in Controls)
            {
                var row = index / columns;
                var column = index % columns;
                control.Dock = DockStyle.None;
                control.Bounds = new Rectangle(
                    emptyState ? Padding.Left : Padding.Left + column * (cardWidth + Gap),
                    Padding.Top + row * (CardHeight + Gap),
                    emptyState ? availableWidth : cardWidth,
                    emptyState ? 160 : CardHeight);
                index++;
            }
            if (emptyState)
            {
                _contentHeight = Math.Max(1, Padding.Vertical + 160);
            }
            else
            {
                var rows = Controls.Count == 0 ? 0 : (Controls.Count + columns - 1) / columns;
                _contentHeight = Math.Max(1, Padding.Vertical + rows * CardHeight + Math.Max(0, rows - 1) * Gap);
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

internal sealed class GalleryGradientTile : Control
{
    private readonly string _initials;
    private readonly Color _start;
    private readonly Color _end;

    internal GalleryGradientTile(string initials, Color start, Color end)
    {
        _initials = initials;
        _start = start;
        _end = end;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        TabStop = false;
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Game color";
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 0 || Height <= 0) return;
        using var brush = new LinearGradientBrush(ClientRectangle, _start, _end, 35f);
        eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _initials,
            ClipCordTheme.DisplayFont(15f, FontStyle.Bold),
            ClientRectangle,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
