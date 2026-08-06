using System.Diagnostics;

namespace ClipsToDiscord;

internal sealed class ActivityView : UserControl
{
    private readonly ActivityHistoryStore _history;
    private readonly string _clipsFolder;
    private readonly ToolTip _toolTip = new() { ShowAlways = true };
    private readonly ActivityListPanel _activityList;
    private readonly BrandedScrollHost _scrollHost;
    private readonly Label _summaryLabel;
    private readonly IDisposable _subscription;
    private Guid? _firstEntryId;

    internal ActivityView(ActivityHistoryStore history, string clipsFolder)
    {
        _history = history;
        _clipsFolder = clipsFolder;
        Name = "ActivityView";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        AccessibleName = "Recent clip activity";

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
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 12),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Name = "ActivityHeading",
            Text = "Recent activity",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellText,
            Font = ClipCordTheme.DisplayFont(18f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        _summaryLabel = new Label
        {
            Name = "ActivitySummary",
            Text = "Your latest clips and upload results appear here.",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellMutedText,
            Font = ClipCordTheme.InterfaceFont(9.5f),
            Margin = new Padding(0, 2, 0, 0)
        };
        header.Controls.Add(_summaryLabel, 0, 1);

        var headerActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        var openUploaded = CreateShellButton("Open uploaded folder", 170);
        openUploaded.Name = "OpenUploadedFolderButton";
        openUploaded.Enabled = Directory.Exists(_clipsFolder);
        openUploaded.Click += (_, _) => OpenUploadedFolder();
        var openLogs = CreateShellButton("Open logs", 105);
        openLogs.Name = "OpenLogsButton";
        openLogs.Margin = new Padding(10, 0, 0, 0);
        openLogs.Click += (_, _) => OpenLogs();
        headerActions.Controls.Add(openUploaded);
        headerActions.Controls.Add(openLogs);
        headerActions.MinimumSize = new Size(
            openUploaded.PreferredSize.Width + openUploaded.Margin.Horizontal +
            openLogs.PreferredSize.Width + openLogs.Margin.Horizontal,
            Math.Max(openUploaded.PreferredSize.Height, openLogs.PreferredSize.Height));
        headerActions.Margin = new Padding(0, 10, 0, 0);
        header.Controls.Add(headerActions, 0, 2);

        _activityList = new ActivityListPanel
        {
            Name = "ActivityList",
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "ActivityScrollHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Content = _activityList
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_scrollHost, 0, 1);
        Controls.Add(root);

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

    private void RenderSnapshot(ClipActivitySnapshot snapshot)
    {
        if (IsDisposed || Disposing) return;
        var anchorAdjustment = 0;
        if (_firstEntryId is { } previousFirstId && _scrollHost.ScrollOffset > 0)
        {
            var previousFirstIndex = snapshot.Entries
                .Select((entry, index) => (entry.Id, index))
                .FirstOrDefault(item => item.Id == previousFirstId)
                .index;
            if (previousFirstIndex > 0)
            {
                anchorAdjustment = snapshot.Entries
                    .Take(previousFirstIndex)
                    .Sum(GetActivityCardOuterHeight);
            }
        }
        _firstEntryId = snapshot.Entries.FirstOrDefault()?.Id;
        _activityList.SuspendLayout();
        try
        {
            var previousControls = _activityList.Controls.Cast<Control>().ToArray();
            _activityList.Controls.Clear();
            foreach (var control in previousControls) control.Dispose();
            _summaryLabel.Text = snapshot.Entries.Count == 0
                ? "Your latest clips and upload results appear here."
                : $"Showing {snapshot.Entries.Count} most recent clip{(snapshot.Entries.Count == 1 ? string.Empty : "s")}.";

            if (snapshot.Entries.Count == 0)
            {
                _activityList.Controls.Add(BuildEmptyState());
                return;
            }

            for (var index = 0; index < snapshot.Entries.Count; index++)
            {
                _activityList.Controls.Add(BuildActivityCard(snapshot.Entries[index]));
            }
        }
        finally
        {
            _activityList.ResumeLayout(true);
            _scrollHost.RefreshContentLayout(anchorAdjustment: anchorAdjustment);
        }
    }

    private static int GetActivityCardOuterHeight(ClipActivityEntry entry) =>
        (entry.Error is null ? 116 : 136) + 10;

    private Control BuildEmptyState()
    {
        var panel = new RoundedPanel
        {
            Name = "ActivityEmptyState",
            Dock = DockStyle.Top,
            Height = 150,
            BackColor = ClipCordTheme.Card,
            BorderColor = ClipCordTheme.CardBorder,
            CornerRadius = 16,
            Padding = new Padding(24),
            Margin = Padding.Empty
        };
        panel.Controls.Add(new Label
        {
            Text = "No clip activity yet\n\nTake a new clip while Discord is open and ClipCord will show each step here.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.MutedText,
            Font = ClipCordTheme.InterfaceFont(10.5f),
            TextAlign = ContentAlignment.MiddleCenter
        });
        return panel;
    }

    private Control BuildActivityCard(ClipActivityEntry entry)
    {
        var presentation = GetPresentation(entry);
        var card = new RoundedPanel
        {
            Name = "ActivityCard",
            Dock = DockStyle.Top,
            Height = entry.Error is null ? 116 : 136,
            BackColor = ClipCordTheme.Card,
            BorderColor = ClipCordTheme.CardBorder,
            CornerRadius = 14,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = $"{presentation.Label}: {entry.FileName}"
        };
        var layout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = ClipCordTheme.Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 7));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = presentation.Accent,
            Margin = Padding.Empty
        }, 0, 0);

        var content = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = ClipCordTheme.Card,
            Padding = new Padding(18, 12, 10, 10),
            Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new Label
        {
            Text = presentation.Label,
            AutoSize = true,
            ForeColor = presentation.Accent,
            Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        var context = new Label
        {
            Text = $"{entry.GameName}  •  {entry.UpdatedUtc.ToLocalTime():g}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = ClipCordTheme.MutedText,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = Padding.Empty
        };
        content.Controls.Add(context, 1, 0);
        var fileName = new Label
        {
            Text = entry.FileName,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.Text,
            Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold),
            Margin = new Padding(0, 3, 0, 1)
        };
        _toolTip.SetToolTip(fileName, entry.FileName);
        content.Controls.Add(fileName, 0, 1);
        content.SetColumnSpan(fileName, 2);
        var detail = new Label
        {
            Text = BuildDetail(entry),
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ClipCordTheme.MutedText,
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = Padding.Empty
        };
        _toolTip.SetToolTip(detail, detail.Text);
        content.Controls.Add(detail, 0, 2);
        content.SetColumnSpan(detail, 2);
        if (entry.Error is not null)
        {
            var error = new Label
            {
                Text = entry.Error,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = ClipCordTheme.Coral,
                Font = ClipCordTheme.InterfaceFont(8.5f),
                Margin = new Padding(0, 2, 0, 0)
            };
            _toolTip.SetToolTip(error, entry.Error);
            content.Controls.Add(error, 0, 3);
            content.SetColumnSpan(error, 2);
        }
        layout.Controls.Add(content, 1, 0);

        var actionHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.Card,
            Padding = new Padding(0, 38, 14, 0),
            Margin = Padding.Empty
        };
        var openLocation = new OutlineButton
        {
            Name = "OpenFileLocationButton",
            Text = "Show in folder",
            Dock = DockStyle.Top,
            Height = 36,
            SurfaceColor = Color.White,
            HoverColor = Color.FromArgb(246, 243, 253),
            OutlineColor = ClipCordTheme.CardBorder,
            ForeColor = ClipCordTheme.Text,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Enabled = entry.CurrentPath is not null && File.Exists(entry.CurrentPath),
            AccessibleName = $"Show {entry.FileName} in its folder"
        };
        openLocation.Click += (_, _) => OpenFileLocation(entry);
        actionHost.Controls.Add(openLocation);
        layout.Controls.Add(actionHost, 2, 0);
        card.Controls.Add(layout);
        return card;
    }

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
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add($"/select,{path}");
        return start;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _subscription.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal readonly record struct ActivityPresentation(string Label, Color Accent);
