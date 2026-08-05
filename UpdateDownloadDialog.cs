namespace ClipsToDiscord;

internal sealed class UpdateDownloadDialog : Form
{
    private readonly UpdateRelease _release;
    private readonly IUpdateDownloadService _downloadService;
    private readonly CancellationToken _applicationCancellation;
    private Icon? _ownedApplicationIcon;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _retryButton;
    private readonly Button _cancelButton;
    private CancellationTokenSource? _attemptCancellation;
    private bool _attemptRunning;
    private bool _allowClose;
    private bool _userCancellationRequested;
    private bool _closingWithoutWait;

    public DownloadedUpdate? DownloadedUpdate { get; private set; }

    public UpdateDownloadDialog(
        UpdateRelease release,
        IUpdateDownloadService downloadService,
        Icon? applicationIcon = null,
        CancellationToken applicationCancellation = default)
    {
        _release = release;
        _downloadService = downloadService;
        _applicationCancellation = applicationCancellation;
        _ownedApplicationIcon = applicationIcon;

        Text = "ClipCord — Downloading update";
        if (_ownedApplicationIcon is not null) Icon = _ownedApplicationIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(570, 220);
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label
        {
            Text = $"Preparing ClipCord {release.Version}",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10)
        };
        _statusLabel = new Label
        {
            Text = "Connecting to GitHub…",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous,
            Height = 22,
            Margin = new Padding(0, 0, 0, 6)
        };
        _detailLabel = new Label
        {
            Text = "The installer will be verified before it is allowed to run.",
            AutoSize = true,
            MaximumSize = new Size(534, 0),
            Dock = DockStyle.Top,
            ForeColor = SystemColors.GrayText,
            Margin = Padding.Empty
        };
        _retryButton = new Button { Text = "Retry", AutoSize = true, Visible = false };
        _retryButton.Click += (_, _) => BeginDownload();
        _cancelButton = new Button { Text = "Cancel", AutoSize = true };
        _cancelButton.Click += (_, _) => RequestCancel();

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttonRow.Controls.Add(_cancelButton);
        buttonRow.Controls.Add(_retryButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_statusLabel, 0, 1);
        layout.Controls.Add(_progressBar, 0, 2);
        layout.Controls.Add(_detailLabel, 0, 3);
        layout.Controls.Add(buttonRow, 0, 4);
        Controls.Add(layout);

        CancelButton = _cancelButton;
        Shown += (_, _) => BeginDownload();
        FormClosing += HandleFormClosing;
    }

    private async void BeginDownload()
    {
        if (_attemptRunning) return;

        _attemptCancellation?.Dispose();
        _attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationCancellation);
        _attemptRunning = true;
        _userCancellationRequested = false;
        _retryButton.Visible = false;
        _cancelButton.Text = "Cancel";
        _cancelButton.Enabled = true;
        _progressBar.Value = 0;
        _statusLabel.Text = "Connecting to GitHub…";
        _detailLabel.Text = "The installer will be verified before it is allowed to run.";

        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            if (_closingWithoutWait || IsDisposed) return;
            _progressBar.Value = value.Percentage;
            _statusLabel.Text = $"Downloading ClipCord {value.Percentage}%";
            _detailLabel.Text = $"{FormatBytes(value.BytesReceived)} of {FormatBytes(value.TotalBytes)}";
        });

        try
        {
            var downloadedUpdate = await _downloadService.DownloadAsync(
                _release,
                progress,
                _attemptCancellation.Token);
            if (_userCancellationRequested ||
                _applicationCancellation.IsCancellationRequested ||
                _closingWithoutWait)
            {
                if (!_closingWithoutWait && !IsDisposed)
                {
                    _allowClose = true;
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
                return;
            }

            DownloadedUpdate = downloadedUpdate;
            _progressBar.Value = 100;
            _statusLabel.Text = "Download verified";
            _detailLabel.Text = "ClipCord will close, install the update, and reopen automatically.";
            _allowClose = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            if (_closingWithoutWait || IsDisposed) return;
            if (_applicationCancellation.IsCancellationRequested || _userCancellationRequested)
            {
                _allowClose = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
            else
            {
                ShowFailure("The update download timed out. Your current ClipCord installation is unchanged.");
            }
        }
        catch (InvalidDataException exception)
        {
            Log.Error("The update installer failed verification.", exception);
            if (_closingWithoutWait || IsDisposed) return;
            ShowFailure("The downloaded update could not be verified, so ClipCord deleted it and did not run it.");
        }
        catch (HttpRequestException exception)
        {
            Log.Error("The verified update installer could not be downloaded.", exception);
            if (_closingWithoutWait || IsDisposed) return;
            ShowFailure("GitHub could not complete the download. Your current ClipCord installation is unchanged.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("The update installer could not be staged.", exception);
            if (_closingWithoutWait || IsDisposed) return;
            ShowFailure("ClipCord could not save the update. Your current installation is unchanged.");
        }
        catch (Exception exception)
        {
            Log.Error("The update could not be prepared.", exception);
            if (_closingWithoutWait || IsDisposed) return;
            ShowFailure("ClipCord could not prepare the update. Your current installation is unchanged.");
        }
        finally
        {
            _attemptRunning = false;
        }
    }

    private void ShowFailure(string message)
    {
        _progressBar.Value = 0;
        _statusLabel.Text = "Update not installed";
        _detailLabel.Text = message;
        _retryButton.Visible = true;
        _cancelButton.Text = "Close";
        _cancelButton.Enabled = true;
    }

    private void RequestCancel()
    {
        if (!_attemptRunning)
        {
            _allowClose = true;
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        _cancelButton.Text = "Cancelling…";
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Cancelling download…";
        _userCancellationRequested = true;
        _attemptCancellation?.Cancel();
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose || !_attemptRunning) return;
        if (eventArgs.CloseReason != CloseReason.UserClosing)
        {
            _closingWithoutWait = true;
            _attemptCancellation?.Cancel();
            return;
        }
        eventArgs.Cancel = true;
        RequestCancel();
    }

    private static string FormatBytes(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        return bytes >= megabyte
            ? $"{bytes / megabyte:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var cancellation = Interlocked.Exchange(ref _attemptCancellation, null);
            if (cancellation is not null)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                cancellation.Dispose();
            }
            Interlocked.Exchange(ref _ownedApplicationIcon, null)?.Dispose();
        }
        base.Dispose(disposing);
    }
}
