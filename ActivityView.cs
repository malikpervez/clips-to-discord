using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal sealed class ActivityView : UserControl
{
    internal const int ShowInFolderLogicalWidth = 135;
    internal const int ShowInFolderLogicalHeight = 31;
    private const int ActivityRowHeight = 64;

    private readonly ActivityHistoryStore _history;
    private readonly string _clipsFolder;
    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private readonly ActivityListPanel _activityList;
    private readonly BrandedScrollHost _scrollHost;
    private readonly Control _embeddedHeader;
    private readonly Label _summaryLabel;
    private readonly TextBox _searchText;
    private readonly FlowLayoutPanel _filterFlow;
    private readonly Dictionary<ActivityFilter, ActivityFilterChip> _filterChips = [];
    private readonly List<(Label Label, DateTime UpdatedUtc)> _relativeTimeLabels = [];
    private readonly System.Windows.Forms.Timer _relativeTimeTimer;
    private readonly IDisposable _subscription;
    private readonly bool _allowLocalOnlyEditing;
    private ClipActivitySnapshot _snapshot = new([]);
    private ActivityFilter _selectedFilter;
    private bool _embeddedHeaderShown = true;
    private Guid? _firstEntryId;

    /// <summary>Raised when a Local-only activity entry asks to open the clip editor.</summary>
    internal event Action<ClipActivityEntry>? EditClipRequested;

    internal ActivityView(
        ActivityHistoryStore history,
        string clipsFolder,
        bool allowLocalOnlyEditing = false)
    {
        _history = history;
        _clipsFolder = clipsFolder;
        _allowLocalOnlyEditing = allowLocalOnlyEditing;
        Name = "ActivityView";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        AccessibleName = "Recent clip activity";

        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(ScaleUi(28), ScaleUi(4), ScaleUi(28), ScaleUi(24)),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, ScaleUi(14)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(210)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(48)));

        var title = new BufferedTableLayoutPanel
        {
            Name = "ActivityEmbeddedHeader",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _embeddedHeader = title;
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        title.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        title.Controls.Add(new Label
        {
            Name = "ActivityHeading",
            Text = "Activity",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.DisplayFont(16f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        _summaryLabel = new Label
        {
            Name = "ActivitySummary",
            Text = "Recent clip activity stored on this PC",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.8f),
            Margin = new Padding(0, 2, 0, 0)
        };
        title.Controls.Add(_summaryLabel, 0, 1);
        header.Controls.Add(title, 0, 0);

        var searchHost = new RoundedPanel
        {
            Name = "ActivitySearchHost",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SettingsField,
            BorderColor = ClipCordTheme.SettingsFieldBorder,
            CornerRadius = 8,
            Padding = new Padding(ScaleUi(11), ScaleUi(9), ScaleUi(11), ScaleUi(7)),
            Margin = new Padding(ScaleUi(8), ScaleUi(7), ScaleUi(10), ScaleUi(7)),
            AccessibleName = "Search recent activity"
        };
        _searchText = new TextBox
        {
            Name = "ActivitySearchTextBox",
            AccessibleName = "Search clips or games",
            PlaceholderText = "Search clips or games",
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = ClipCordTheme.SettingsField,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = Padding.Empty
        };
        _searchText.TextChanged += (_, _) => RenderCurrentProjection(preserveAnchor: false);
        _searchText.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Escape || _searchText.TextLength == 0) return;
            _searchText.Clear();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        };
        searchHost.Controls.Add(_searchText);
        header.Controls.Add(searchHost, 1, 0);

        var openUploaded = CreateShellButton("Open uploaded folder", 164);
        openUploaded.Name = "OpenUploadedFolderButton";
        openUploaded.LeadingGlyph = BrandGlyph.FolderOpen;
        openUploaded.Enabled = Directory.Exists(_clipsFolder);
        openUploaded.Click += (_, _) => OpenUploadedFolder();
        openUploaded.Margin = new Padding(0, ScaleUi(7), ScaleUi(10), ScaleUi(7));
        header.Controls.Add(openUploaded, 2, 0);

        var openLogs = CreateShellButton("Open logs", 92);
        openLogs.Name = "OpenLogsButton";
        openLogs.LeadingGlyph = BrandGlyph.FileText;
        openLogs.Margin = new Padding(0, ScaleUi(7), 0, ScaleUi(7));
        openLogs.Click += (_, _) => OpenLogs();
        header.Controls.Add(openLogs, 3, 0);

        var filters = new BufferedTableLayoutPanel
        {
            Name = "ActivityFilters",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, ScaleUi(14)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _filterFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        AddFilterChip(ActivityFilter.All, "All", Color.Transparent);
        AddFilterChip(ActivityFilter.Uploaded, "Uploaded", Color.FromArgb(49, 177, 113));
        AddFilterChip(ActivityFilter.LocalOnly, "Local only", Color.FromArgb(91, 147, 255));
        AddFilterChip(ActivityFilter.Retrying, "Retrying", Color.FromArgb(224, 151, 54));
        AddFilterChip(ActivityFilter.Failed, "Failed", ClipCordTheme.Coral);
        filters.Controls.Add(_filterFlow, 0, 0);
        filters.Controls.Add(new Label
        {
            Name = "ActivitySortLabel",
            Text = "Newest first",
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = new Padding(ScaleUi(12), ScaleUi(7), 0, 0)
        }, 1, 0);

        _activityList = new ActivityListPanel
        {
            Name = "ActivityList",
            Margin = Padding.Empty,
            Padding = new Padding(ScaleUi(14), ScaleUi(6), ScaleUi(14), ScaleUi(6)),
            BackColor = ClipCordTheme.Card
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "ActivityScrollHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Card,
            Content = _activityList
        };

        var timeline = new RoundedPanel
        {
            Name = "ActivityTimeline",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.Card,
            BorderColor = ClipCordTheme.CardBorder,
            CornerRadius = 16,
            Padding = new Padding(1),
            Margin = Padding.Empty,
            AccessibleName = "Newest clip activity"
        };
        timeline.Controls.Add(_scrollHost);

        var privacy = new BufferedTableLayoutPanel
        {
            Name = "ActivityPrivacyNote",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, ScaleUi(12), 0, 0),
            Padding = new Padding(ScaleUi(4), 0, 0, 0),
            BackColor = ClipCordTheme.Shell
        };
        privacy.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(18)));
        privacy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        privacy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        privacy.Controls.Add(new BrandGlyphControl
        {
            Glyph = BrandGlyph.Shield,
            GlyphColor = Color.FromArgb(49, 177, 113),
            Size = new Size(ScaleUi(14), ScaleUi(14)),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            AccessibleName = "Privacy"
        }, 0, 0);
        privacy.Controls.Add(new Label
        {
            Text = "Activity is stored locally and never includes your webhook. Closing this window does not stop clip processing.",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = new Padding(5, 0, 0, 0),
            AccessibleRole = AccessibleRole.StaticText
        }, 1, 0);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(filters, 0, 1);
        root.Controls.Add(timeline, 0, 2);
        root.Controls.Add(privacy, 0, 3);
        Controls.Add(root);

        _selectedFilter = ActivityFilter.All;
        UpdateFilterSelection();
        _relativeTimeTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _relativeTimeTimer.Tick += (_, _) =>
        {
            if (Visible) RefreshRelativeTimes();
        };
        VisibleChanged += (_, _) =>
        {
            if (Visible) _relativeTimeTimer.Start();
            else _relativeTimeTimer.Stop();
        };
        var context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _subscription = _history.Subscribe(context, RenderSnapshot);
    }

    internal static ActivityPresentation GetPresentation(ClipActivityEntry entry) => entry.State switch
    {
        ClipActivityState.Discovered => new("Discovered", Color.FromArgb(91, 147, 255)),
        ClipActivityState.Waiting => new("Waiting for recorder", Color.FromArgb(128, 139, 158)),
        ClipActivityState.Hashing => new("Preparing", ClipCordTheme.Violet),
        ClipActivityState.Queued => new("Queued", Color.FromArgb(91, 147, 255)),
        ClipActivityState.Uploading => new("Uploading", ClipCordTheme.Violet),
        ClipActivityState.Compressing => new("Compressing", Color.FromArgb(185, 75, 245)),
        ClipActivityState.Retrying => new("Retry scheduled", Color.FromArgb(224, 151, 54)),
        ClipActivityState.Completed => new("Upload complete", Color.FromArgb(49, 177, 113)),
        ClipActivityState.Failed => new("Needs attention", ClipCordTheme.Coral),
        ClipActivityState.Archived when entry.Route == ClipActivityRoute.LocalOnly =>
            new("Saved locally", Color.FromArgb(91, 147, 255)),
        ClipActivityState.Archived when entry.Route == ClipActivityRoute.Duplicate =>
            new("Duplicate archived", Color.FromArgb(128, 139, 158)),
        ClipActivityState.Archived when entry.Route == ClipActivityRoute.Baseline =>
            new("Existing clip ignored", Color.FromArgb(128, 139, 158)),
        ClipActivityState.Archived => new("Uploaded", Color.FromArgb(49, 177, 113)),
        _ => new("Activity", ClipCordTheme.ShellMutedText)
    };

    internal void RefreshViewport()
    {
        PerformLayout();
        _scrollHost.RefreshContentLayout();
    }

    /// <summary>
    /// Hides only Activity's embedded title and subtitle when the outer shell supplies them.
    /// Search, folder/log actions, filters, and the timeline stay in this view's first body rows.
    /// </summary>
    internal void SetEmbeddedHeaderVisible(bool visible)
    {
        if (IsDisposed || Disposing || _embeddedHeaderShown == visible) return;
        _embeddedHeaderShown = visible;
        _embeddedHeader.Visible = visible;
        PerformLayout();
        _scrollHost.RefreshContentLayout();
    }

    private void RenderSnapshot(ClipActivitySnapshot snapshot)
    {
        if (IsDisposed || Disposing) return;
        _snapshot = snapshot;
        RenderCurrentProjection(preserveAnchor: true);
    }

    internal static ActivityProjection ProjectEntries(
        ClipActivitySnapshot snapshot,
        ActivityFilter filter,
        string? searchText)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var ordered = snapshot.Entries
            .OrderByDescending(entry => entry.UpdatedUtc)
            .ThenByDescending(entry => entry.CreatedUtc)
            .ThenBy(entry => entry.Id)
            .ToArray();
        var counts = new ActivityFilterCounts(
            ordered.Length,
            ordered.Count(entry => entry.Route == ClipActivityRoute.Uploaded),
            ordered.Count(entry => entry.Route == ClipActivityRoute.LocalOnly),
            ordered.Count(entry => entry.State == ClipActivityState.Retrying),
            ordered.Count(entry => entry.State == ClipActivityState.Failed));

        IEnumerable<ClipActivityEntry> visible = filter switch
        {
            ActivityFilter.Uploaded => ordered.Where(entry => entry.Route == ClipActivityRoute.Uploaded),
            ActivityFilter.LocalOnly => ordered.Where(entry => entry.Route == ClipActivityRoute.LocalOnly),
            ActivityFilter.Retrying => ordered.Where(entry => entry.State == ClipActivityState.Retrying),
            ActivityFilter.Failed => ordered.Where(entry => entry.State == ClipActivityState.Failed),
            _ => ordered
        };
        var query = searchText?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            visible = visible.Where(entry =>
                entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.GameName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return new ActivityProjection(visible.ToArray(), counts);
    }

    internal static string FormatRelativeTime(DateTime updatedUtc, DateTime utcNow)
    {
        var normalizedUpdatedUtc = updatedUtc.Kind == DateTimeKind.Utc
            ? updatedUtc
            : updatedUtc.ToUniversalTime();
        var normalizedNowUtc = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : utcNow.ToUniversalTime();
        var age = normalizedNowUtc - normalizedUpdatedUtc;
        if (age < TimeSpan.Zero || age < TimeSpan.FromMinutes(1)) return "Just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)} h ago";
        if (age < TimeSpan.FromDays(7)) return $"{Math.Max(1, (int)age.TotalDays)} d ago";
        return normalizedUpdatedUtc.ToLocalTime().ToString("MMM d");
    }

    private void AddFilterChip(ActivityFilter filter, string label, Color dotColor)
    {
        var chip = new ActivityFilterChip(filter, label, dotColor)
        {
            Name = $"ActivityFilter{filter}Button",
            AccessibleName = $"Show {label} activity",
            Margin = new Padding(_filterChips.Count == 0 ? 0 : 8, 0, 0, 0)
        };
        chip.Click += (_, _) =>
        {
            if (_selectedFilter == filter) return;
            _selectedFilter = filter;
            UpdateFilterSelection();
            RenderCurrentProjection(preserveAnchor: false);
        };
        _filterChips.Add(filter, chip);
        _filterFlow.Controls.Add(chip);
    }

    private void UpdateFilterSelection()
    {
        foreach (var (filter, chip) in _filterChips)
        {
            chip.Selected = filter == _selectedFilter;
            chip.AccessibleDescription = chip.Selected ? "Selected filter" : string.Empty;
        }
    }

    private void RenderCurrentProjection(bool preserveAnchor)
    {
        if (IsDisposed || Disposing) return;
        var projection = ProjectEntries(_snapshot, _selectedFilter, _searchText.Text);
        foreach (var (filter, chip) in _filterChips)
        {
            chip.Count = projection.Counts.For(filter);
        }

        var anchorAdjustment = 0;
        if (preserveAnchor && _firstEntryId is { } previousFirstId && _scrollHost.ScrollOffset > 0)
        {
            var previousFirstIndex = projection.Entries
                .Select((entry, index) => (entry.Id, index))
                .FirstOrDefault(item => item.Id == previousFirstId)
                .index;
            if (previousFirstIndex > 0)
            {
                anchorAdjustment = projection.Entries
                    .Take(previousFirstIndex)
                    .Sum(GetActivityCardOuterHeight);
            }
        }
        _firstEntryId = projection.Entries.FirstOrDefault()?.Id;
        _activityList.SuspendLayout();
        try
        {
            var previousControls = _activityList.Controls.Cast<Control>().ToArray();
            _activityList.Controls.Clear();
            _relativeTimeLabels.Clear();
            foreach (var control in previousControls) control.Dispose();

            if (projection.Entries.Count == 0)
            {
                _activityList.Controls.Add(BuildEmptyState(_snapshot.Entries.Count == 0));
                return;
            }

            for (var index = 0; index < projection.Entries.Count; index++)
            {
                _activityList.Controls.Add(BuildActivityCard(projection.Entries[index], index == 0));
            }
        }
        finally
        {
            _activityList.ResumeLayout(true);
            _scrollHost.RefreshContentLayout(
                preservePosition: preserveAnchor,
                anchorAdjustment: anchorAdjustment);
        }
    }

    private int GetActivityCardOuterHeight(ClipActivityEntry _) => ScaleUi(ActivityRowHeight);

    private Control BuildEmptyState(bool historyIsEmpty)
    {
        var panel = new Panel
        {
            Name = "ActivityEmptyState",
            Dock = DockStyle.Top,
            Height = ScaleUi(150),
            BackColor = ClipCordTheme.Card,
            Padding = new Padding(24),
            Margin = Padding.Empty
        };
        panel.Controls.Add(new Label
        {
            Text = historyIsEmpty
                ? "No clip activity yet\n\nTake a new clip and ClipCord will show each step here."
                : "No activity matches these filters.\n\nTry another status or search term.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(9.5f),
            TextAlign = ContentAlignment.MiddleCenter
        });
        return panel;
    }

    private Control BuildActivityCard(ClipActivityEntry entry, bool isFirst)
    {
        var presentation = GetPresentation(entry);
        var card = new ActivityRowPanel
        {
            Name = "ActivityCard",
            Dock = DockStyle.Top,
            Height = ScaleUi(ActivityRowHeight),
            BackColor = ClipCordTheme.Card,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            ShowSeparator = !isFirst,
            AccessibleName = $"{presentation.Label}: {entry.FileName}"
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = ClipCordTheme.Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(3)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = presentation.Accent,
            BorderColor = Color.Transparent,
            CornerRadius = ScaleUi(2),
            Margin = new Padding(0, ScaleUi(13), 0, ScaleUi(13))
        }, 0, 0);

        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ClipCordTheme.Card,
            Padding = new Padding(ScaleUi(14), ScaleUi(7), ScaleUi(8), ScaleUi(6)),
            Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(25)));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var headline = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = ClipCordTheme.Card,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        headline.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headline.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headline.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headline.Controls.Add(new Label
        {
            Text = presentation.Label.ToUpperInvariant(),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = presentation.Accent,
            Font = ClipCordTheme.InterfaceFont(7.2f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);

        var gameFont = ClipCordTheme.InterfaceFont(7.5f);
        var measuredGame = TextRenderer.MeasureText(
            entry.GameName,
            gameFont,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var gameChip = new RoundedPanel
        {
            Width = Math.Clamp(measuredGame.Width + 16, 42, 138),
            Height = 20,
            Anchor = AnchorStyles.Left,
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 5,
            Margin = new Padding(8, 1, 8, 1),
            Padding = new Padding(7, 1, 7, 1)
        };
        var gameName = new Label
        {
            Text = entry.GameName,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = gameFont,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty
        };
        _toolTip.SetToolTip(gameName, entry.GameName);
        gameChip.Controls.Add(gameName);
        headline.Controls.Add(gameChip, 1, 0);

        var fileName = new Label
        {
            Text = entry.FileName,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        _toolTip.SetToolTip(fileName, entry.FileName);
        headline.Controls.Add(fileName, 2, 0);
        content.Controls.Add(headline, 0, 0);

        var detailText = BuildDisplayDetail(entry);
        var detail = new Label
        {
            Text = detailText,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = entry.Error is null ? ClipCordTheme.TextTertiary : ClipCordTheme.Coral,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = Padding.Empty
        };
        _toolTip.SetToolTip(detail, detailText);
        content.Controls.Add(detail, 0, 1);
        layout.Controls.Add(content, 1, 0);

        var relativeTime = new Label
        {
            Text = FormatRelativeTime(entry.UpdatedUtc, DateTime.UtcNow),
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = new Padding(8, 0, 8, 0)
        };
        _relativeTimeLabels.Add((relativeTime, entry.UpdatedUtc));
        layout.Controls.Add(relativeTime, 2, 0);

        var actionHost = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            BackColor = ClipCordTheme.Card,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, ScaleUi(12), 0)
        };
        var openLocation = new OutlineButton
        {
            Name = "OpenFileLocationButton",
            Text = "Show in folder",
            Width = ScaleUi(ShowInFolderLogicalWidth),
            Height = ScaleUi(ShowInFolderLogicalHeight),
            AutoSize = false,
            LeadingGlyph = BrandGlyph.FolderOpen,
            SurfaceColor = ClipCordTheme.SettingsButton,
            HoverColor = ClipCordTheme.SettingsButtonHover,
            OutlineColor = ClipCordTheme.BorderStrong,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = Padding.Empty,
            Enabled = entry.CurrentPath is not null && File.Exists(entry.CurrentPath),
            AccessibleName = $"Show {entry.FileName} in its folder"
        };
        openLocation.Click += (_, _) => OpenFileLocation(entry);
        actionHost.Controls.Add(openLocation);
        if (CanEditLocalOnlyClip(entry))
        {
            var editClip = new OutlineButton
            {
                Name = "EditActivityClipButton",
                Text = "Edit & upload",
                Width = ScaleUi(112),
                Height = ScaleUi(31),
                AutoSize = false,
                Margin = new Padding(ScaleUi(8), 0, 0, 0),
                SurfaceColor = ClipCordTheme.VioletMuted,
                HoverColor = Color.FromArgb(60, 49, 88),
                OutlineColor = ClipCordTheme.Violet,
                ForeColor = ClipCordTheme.TextPrimary,
                Font = ClipCordTheme.InterfaceFont(8f),
                AccessibleName = $"Edit and upload {entry.FileName}"
            };
            editClip.Click += (_, _) => EditClipRequested?.Invoke(entry);
            actionHost.Controls.Add(editClip);
        }
        layout.Controls.Add(actionHost, 3, 0);
        card.Controls.Add(layout);
        return card;
    }

    private static string BuildDisplayDetail(ClipActivityEntry entry)
    {
        var detail = BuildDetail(entry)
            .Replace(" -> ", " → ", StringComparison.Ordinal)
            .Replace("  •  ", "  ·  ", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(entry.Error)
            ? detail
            : $"{detail}  ·  {entry.Error}";
    }

    private void RefreshRelativeTimes()
    {
        var utcNow = DateTime.UtcNow;
        foreach (var (label, updatedUtc) in _relativeTimeLabels)
        {
            if (!label.IsDisposed) label.Text = FormatRelativeTime(updatedUtc, utcNow);
        }
    }

    private bool CanEditLocalOnlyClip(ClipActivityEntry entry) =>
        _allowLocalOnlyEditing && IsEditableLocalOnlyEntry(entry);

    /// <summary>
    /// Only a Local-only clip that still exists on disk can enter the editor. Uploaded,
    /// duplicate, and baseline rows never expose the action, matching the Gallery card gate.
    /// </summary>
    internal static bool IsEditableLocalOnlyEntry(ClipActivityEntry entry) =>
        entry.Route == ClipActivityRoute.LocalOnly &&
        !string.IsNullOrWhiteSpace(entry.CurrentPath) &&
        File.Exists(entry.CurrentPath);

    internal static string BuildDetail(ClipActivityEntry entry)
    {
        var parts = new List<string>();
        if (entry.OriginalBytes > 0)
        {
            if (entry.CompressedBytes is > 0)
            {
                var reduction = Math.Max(0, (1d - entry.CompressedBytes.Value / (double)entry.OriginalBytes) * 100d);
                parts.Add(FormattableString.Invariant(
                    $"{FormatMegabytes(entry.OriginalBytes)} -> {FormatMegabytes(entry.CompressedBytes.Value)} ({reduction:F1}% smaller)"));
            }
            else
            {
                parts.Add(FormatMegabytes(entry.OriginalBytes));
            }
        }
        if (entry.CompressionTargetMb is > 0) parts.Add($"{entry.CompressionTargetMb} MB ceiling");
        if (entry.VideoKbps is > 0) parts.Add(FormattableString.Invariant($"{entry.VideoKbps / 1000d:F1} Mbps video"));
        if (entry.AttemptCount > 0) parts.Add($"Attempt {entry.AttemptCount}");
        if (!string.IsNullOrWhiteSpace(entry.Detail)) parts.Add(entry.Detail);
        return parts.Count == 0 ? "Clip activity updated." : string.Join("  •  ", parts);
    }

    private static string FormatMegabytes(long bytes) =>
        FormattableString.Invariant($"{bytes / 1024d / 1024d:F1} MB");

    private OutlineButton CreateShellButton(string text, int width) => new()
    {
        Text = text,
        Width = ScaleUi(width),
        Height = ScaleUi(38),
        SurfaceColor = ClipCordTheme.SettingsButton,
        HoverColor = ClipCordTheme.SettingsButtonHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9f),
        Margin = Padding.Empty
    };

    internal static int ScaleLogicalMetric(int value, int dpi) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, dpi) / 96d));

    private int ScaleUi(int value) => ScaleLogicalMetric(value, DeviceDpi);

    private void OpenFileLocation(ClipActivityEntry entry)
    {
        if (entry.CurrentPath is null || !File.Exists(entry.CurrentPath)) return;
        try
        {
            Process.Start(CreateSelectFileStartInfo(entry.CurrentPath));
        }
        catch (Exception exception)
        {
            Log.Error($"Could not open the file location for {entry.FileName}.", exception);
            MessageBox.Show(this, "Windows could not open this clip's location.", "Could not open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenUploadedFolder()
    {
        try
        {
            var folder = UploadedFolder.GetOrCreate(_clipsFolder);
            Process.Start(CreateOpenFolderStartInfo(folder));
        }
        catch (Exception exception)
        {
            Log.Error("Could not open the uploaded folder.", exception);
            MessageBox.Show(this, "Windows could not open the uploaded folder.", "Could not open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var logPath = Path.Combine(SettingsStore.DataDirectory, "app.log");
            if (File.Exists(logPath))
            {
                Process.Start(CreateSelectFileStartInfo(logPath));
            }
            else
            {
                Process.Start(CreateOpenFolderStartInfo(SettingsStore.DataDirectory));
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not open the ClipCord logs folder.", exception);
            MessageBox.Show(this, "Windows could not open the logs folder.", "Could not open logs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static ProcessStartInfo CreateOpenFolderStartInfo(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(folder);
        return start;
    }

    internal static ProcessStartInfo CreateSelectFileStartInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('"'))
        {
            throw new ArgumentException("Path cannot contain a double quote.", nameof(path));
        }
        // Explorer uses a legacy command-line parser for /select. ArgumentList
        // quotes a space-containing combined token as "/select,C:\\some path",
        // which Explorer can mistake for a folder and then fall back to Documents.
        // Keep the switch outside the quotes and quote only the exact file path.
        return new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _relativeTimeTimer.Stop();
            _relativeTimeTimer.Dispose();
            _subscription.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal readonly record struct ActivityPresentation(string Label, Color Accent);

internal enum ActivityFilter
{
    All,
    Uploaded,
    LocalOnly,
    Retrying,
    Failed
}

internal readonly record struct ActivityFilterCounts(
    int All,
    int Uploaded,
    int LocalOnly,
    int Retrying,
    int Failed)
{
    internal int For(ActivityFilter filter) => filter switch
    {
        ActivityFilter.Uploaded => Uploaded,
        ActivityFilter.LocalOnly => LocalOnly,
        ActivityFilter.Retrying => Retrying,
        ActivityFilter.Failed => Failed,
        _ => All
    };
}

internal sealed record ActivityProjection(
    IReadOnlyList<ClipActivityEntry> Entries,
    ActivityFilterCounts Counts);

internal sealed class ActivityFilterChip : Button
{
    private int _count;
    private bool _selected;
    private Size _lastRegionSize = Size.Empty;

    internal ActivityFilterChip(ActivityFilter filter, string label, Color dotColor)
    {
        Filter = filter;
        FilterLabel = label;
        DotColor = dotColor;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = ClipCordTheme.Shell;
        ForeColor = ClipCordTheme.TextSecondary;
        Font = ClipCordTheme.InterfaceFont(8.5f);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.RadioButton;
        Resize += (_, _) => UpdateRegion();
    }

    internal ActivityFilter Filter { get; }
    internal string FilterLabel { get; }
    internal Color DotColor { get; }

    internal int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            AccessibleName = $"{FilterLabel}, {value} clip{(value == 1 ? string.Empty : "s")}";
            Invalidate();
            Parent?.PerformLayout();
        }
    }

    internal bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var labelSize = TextRenderer.MeasureText(
            FilterLabel,
            Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var countSize = TextRenderer.MeasureText(
            Count.ToString(),
            ClipCordTheme.InterfaceFont(7.8f),
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var dotWidth = DotColor == Color.Transparent ? 0 : 14;
        return new Size(labelSize.Width + countSize.Width + dotWidth + 31, 30);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, Height / 2);
        using var fill = new SolidBrush(Selected ? ClipCordTheme.VioletMuted : ClipCordTheme.Card);
        using var outline = new Pen(Selected ? ClipCordTheme.Violet : ClipCordTheme.BorderDefault);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(outline, path);

        var x = DotColor == Color.Transparent ? 13 : 11;
        if (DotColor != Color.Transparent)
        {
            var dotSize = 7;
            using var dot = new SolidBrush(DotColor);
            eventArgs.Graphics.FillEllipse(dot, x, (Height - dotSize) / 2, dotSize, dotSize);
            x += dotSize + 7;
        }

        var labelColor = Selected ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary;
        var labelSize = TextRenderer.MeasureText(
            FilterLabel,
            Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            FilterLabel,
            Font,
            new Rectangle(x, 0, labelSize.Width, Height),
            labelColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        x += labelSize.Width + 7;
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Count.ToString(),
            ClipCordTheme.InterfaceFont(7.8f),
            new Rectangle(x, 0, Math.Max(1, Width - x - 8), Height),
            ClipCordTheme.TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ActivityFilterChipAccessibleObject(this);

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width, Height), Height / 2);
        Region?.Dispose();
        Region = new Region(path);
    }

    private sealed class ActivityFilterChipAccessibleObject(ActivityFilterChip owner)
        : Control.ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State |
            (owner.Selected ? AccessibleStates.Checked : AccessibleStates.None);
    }
}

internal sealed class ActivityRowPanel : Panel
{
    internal ActivityRowPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    internal bool ShowSeparator { get; init; }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (!ShowSeparator || Width <= 0) return;
        using var pen = new Pen(ClipCordTheme.BorderDefault);
        eventArgs.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}
