namespace ClipsToDiscord;

internal enum UpdateDialogAction
{
    RemindLater,
    ViewChanges,
    DownloadUpdate,
    SkipVersion
}

internal sealed class UpdateAvailableDialog : Form
{
    private readonly Icon? _ownedApplicationIcon;

    public UpdateDialogAction SelectedAction { get; private set; } = UpdateDialogAction.RemindLater;

    public UpdateAvailableDialog(UpdateRelease release, Icon? applicationIcon = null)
    {
        Text = "Clips to Discord — Update available";
        _ownedApplicationIcon = applicationIcon;
        if (_ownedApplicationIcon is not null) Icon = _ownedApplicationIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(690, 220);
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label
        {
            Text = $"Clips to Discord {release.Version} is available",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10)
        };
        var explanation = new Label
        {
            Text = "The release includes the expected installer and SHA-256 verification information. " +
                   "For safety, this app never downloads or installs updates silently. " +
                   "View changes and Download update both open the official GitHub release page.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        var viewChangesButton = CreateActionButton("View changes", UpdateDialogAction.ViewChanges);
        var downloadButton = CreateActionButton("Download update", UpdateDialogAction.DownloadUpdate);
        var skipButton = CreateActionButton("Skip this version", UpdateDialogAction.SkipVersion);
        var remindButton = CreateActionButton("Remind me later", UpdateDialogAction.RemindLater);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttonRow.Controls.Add(downloadButton);
        buttonRow.Controls.Add(viewChangesButton);
        buttonRow.Controls.Add(remindButton);
        buttonRow.Controls.Add(skipButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(explanation, 0, 1);
        layout.Controls.Add(buttonRow, 0, 2);
        Controls.Add(layout);

        AcceptButton = downloadButton;
        CancelButton = remindButton;
    }

    private Button CreateActionButton(string text, UpdateDialogAction action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) =>
        {
            SelectedAction = action;
            DialogResult = DialogResult.OK;
            Close();
        };
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _ownedApplicationIcon?.Dispose();
    }
}
