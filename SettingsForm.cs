namespace ClipsToDiscord;

internal sealed class SettingsForm : Form
{
    private readonly Icon? _ownedApplicationIcon;
    private readonly TextBox _folderText = new() { Dock = DockStyle.Fill };
    private readonly TextBox _webhookText = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _uploaderNameText = new()
    {
        Dock = DockStyle.Fill,
        MaxLength = AppSettings.MaximumUploaderNameLength
    };
    private readonly NumericUpDown _compressionTarget = new()
    {
        Minimum = 1,
        Maximum = 100,
        DecimalPlaces = 0,
        Width = 85
    };
    private readonly CheckBox _startWithWindows = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Button _testButton = new() { Text = "Test webhook", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "Save", AutoSize = true };
    private readonly Label _statusLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    public AppSettings? SavedSettings { get; private set; }

    public SettingsForm(AppSettings settings, Icon? applicationIcon = null)
    {
        Text = "Clips to Discord — Settings";
        _ownedApplicationIcon = applicationIcon;
        if (_ownedApplicationIcon is not null) Icon = _ownedApplicationIcon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(760, 455);
        MinimumSize = new Size(680, 455);
        SizeGripStyle = SizeGripStyle.Show;
        AutoScaleMode = AutoScaleMode.Dpi;

        _folderText.Text = settings.ClipsFolder;
        _webhookText.Text = settings.WebhookUrl;
        _uploaderNameText.Text = AppSettings.NormalizeUploaderName(settings.UploaderName);
        _compressionTarget.Value = Math.Clamp(
            settings.CompressionTargetMb,
            (int)_compressionTarget.Minimum,
            (int)_compressionTarget.Maximum);
        _startWithWindows.Checked = settings.StartWithWindows;

        var browseButton = new Button { Text = "Browse…", AutoSize = true };
        browseButton.Click += BrowseClicked;
        _testButton.Click += TestClicked;
        _saveButton.Click += SaveClicked;
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var folderRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = Padding.Empty
        };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(_folderText, 0, 0);
        folderRow.Controls.Add(browseButton, 1, 0);

        var webhookRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = Padding.Empty
        };
        webhookRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        webhookRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        webhookRow.Controls.Add(_webhookText, 0, 0);
        webhookRow.Controls.Add(_testButton, 1, 0);

        var compressionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = Padding.Empty
        };
        compressionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        compressionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        compressionRow.Controls.Add(_compressionTarget, 0, 0);
        compressionRow.Controls.Add(new Label
        {
            Text = "MB target; smaller retries are automatic",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(8, 0, 0, 0)
        }, 1, 0);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttonRow.Controls.Add(_saveButton);
        buttonRow.Controls.Add(cancelButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 13,
            AutoSize = false,
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 11; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "Clips folder",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 5)
        }, 0, 0);
        layout.Controls.Add(folderRow, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "Discord webhook URL",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 14, 0, 5)
        }, 0, 2);
        layout.Controls.Add(webhookRow, 0, 3);
        layout.Controls.Add(new Label
        {
            Text = "Keep this URL private. It is encrypted for your Windows account when saved.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 5, 0, 10)
        }, 0, 4);
        layout.Controls.Add(new Label
        {
            Text = "Uploader name",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 5, 0, 5)
        }, 0, 5);
        layout.Controls.Add(_uploaderNameText, 0, 6);
        layout.Controls.Add(new Label
        {
            Text = "Shown with each clip. Use your Discord display name when sharing a webhook.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 5, 0, 10)
        }, 0, 7);
        layout.Controls.Add(new Label
        {
            Text = "Compression target",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 5, 0, 5)
        }, 0, 8);
        layout.Controls.Add(compressionRow, 0, 9);
        layout.Controls.Add(_startWithWindows, 0, 10);
        layout.Controls.Add(_statusLabel, 0, 11);
        layout.Controls.Add(buttonRow, 0, 12);
        Controls.Add(layout);

        AcceptButton = _saveButton;
        CancelButton = cancelButton;
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
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderText.Text = dialog.SelectedPath;
        }
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
            _statusLabel.ForeColor = Color.DarkGreen;
            _statusLabel.Text = "Connection successful — check the Discord channel.";
        }
        catch (Exception exception)
        {
            _statusLabel.ForeColor = Color.DarkRed;
            _statusLabel.Text = "Connection failed.";
            MessageBox.Show(this, exception.Message, "Webhook test failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveClicked(object? sender, EventArgs eventArgs)
    {
        if (!TryValidate(out var settings)) return;
        SavedSettings = settings;
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryValidate(out AppSettings settings)
    {
        settings = new AppSettings(
            _folderText.Text.Trim(),
            _webhookText.Text.Trim(),
            _startWithWindows.Checked,
            Decimal.ToInt32(_compressionTarget.Value),
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

    private void SetBusy(bool busy, string? status = null)
    {
        _testButton.Enabled = !busy;
        _saveButton.Enabled = !busy;
        if (status is not null)
        {
            _statusLabel.ForeColor = SystemColors.GrayText;
            _statusLabel.Text = status;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _ownedApplicationIcon?.Dispose();
    }
}
