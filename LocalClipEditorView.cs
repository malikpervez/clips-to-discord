using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace ClipsToDiscord;

internal sealed class LocalClipEditorView : UserControl
{
    private readonly GalleryClipEntry _source;
    private readonly IManualClipEditService _service;
    private readonly ClipPreviewControl _preview;
    private readonly Label _previewStatus;
    private readonly Label _durationLabel;
    private readonly Label _estimateLabel;
    private readonly Label _progressLabel;
    private readonly TrimRangeControl _trimRange;
    private readonly TextBox _startText;
    private readonly TextBox _endText;
    private readonly TextBox _gameText;
    private readonly TextBox _outputNameText;
    private readonly TextBox _noteText;
    private readonly ToggleSwitch _muteAudio;
    private readonly ToggleSwitch _keepOriginal;
    private readonly GradientButton _uploadButton;
    private readonly OutlineButton _cancelButton;
    private readonly OutlineButton _playButton;
    private readonly RoundedPanel _previewCard;
    private readonly RoundedPanel _optionsCard;
    private readonly RoundedPanel _trimCard;
    private readonly RoundedPanel _commitCard;
    private readonly System.Windows.Forms.Timer _previewDebounce;
    private CancellationTokenSource? _lifetimeCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _operationCancellation;
    private ClipMediaInfo? _media;
    private string? _previewPath;
    private int _previewGeneration;
    private int _operationGeneration;
    private bool _startupDpiScaleApplied;
    private bool _updatingTrimText;
    private bool _operationCanCancel = true;
    private bool _busy;
    private bool _disposed;

    internal event Action<bool>? BusyChanged;
    internal event Action? Cancelled;
    internal event Action<string, GalleryClipRoute>? Completed;

    internal bool IsBusy => _busy;

    internal LocalClipEditorView(
        GalleryClipEntry source,
        IManualClipEditService service)
    {
        if (source.Route != GalleryClipRoute.LocalOnly)
        {
            throw new ArgumentException("The Gallery editor accepts only Local-only clips.", nameof(source));
        }
        _source = source;
        _service = service;
        Name = "LocalClipEditorView";
        AccessibleName = $"Edit and upload {source.FileName}";
        BackColor = ClipCordTheme.Shell;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        AutoSize = false;
        Padding = Padding.Empty;

        var root = new BufferedTableLayoutPanel
        {
            Name = "LocalClipEditorLayout",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _previewCard = CreateCard("EditorPreviewCard", new Padding(18));
        _previewCard.Height = 470;
        _previewCard.Margin = new Padding(0, 0, 7, 12);
        var previewLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.Controls.Add(CreateSectionHeading("Preview", "A still frame from the selected trim point."), 0, 0);
        var previewSurface = new RoundedPanel
        {
            Name = "EditorPreviewSurface",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(8, 15, 26),
            BorderColor = Color.FromArgb(55, 68, 90),
            CornerRadius = 12,
            Margin = new Padding(0, 12, 0, 10),
            Padding = new Padding(2)
        };
        _preview = new ClipPreviewControl
        {
            Name = "EditorPreviewImage",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(8, 15, 26),
            TabStop = false,
            AccessibleRole = AccessibleRole.Graphic,
            AccessibleName = "Selected clip preview frame"
        };
        _previewStatus = new Label
        {
            Text = "Preparing preview…",
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ForeColor = ClipCordTheme.ShellMutedText,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = ClipCordTheme.InterfaceFont(10f)
        };
        previewSurface.Controls.Add(_previewStatus);
        previewSurface.Controls.Add(_preview);
        _preview.SendToBack();
        previewLayout.Controls.Add(previewSurface, 0, 1);
        var previewFooter = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        previewFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _durationLabel = CreateMutedLabel("Duration: —");
        _durationLabel.Anchor = AnchorStyles.Left;
        _playButton = CreateOutlineButton("Play source", 112);
        _playButton.Name = "PlayEditorSourceButton";
        _playButton.Click += PlaySource;
        previewFooter.Controls.Add(_durationLabel, 0, 0);
        previewFooter.Controls.Add(_playButton, 1, 0);
        previewLayout.Controls.Add(previewFooter, 0, 2);
        _previewCard.Controls.Add(previewLayout);
        root.Controls.Add(_previewCard, 0, 0);

        _optionsCard = CreateCard("EditorOptionsCard", new Padding(18));
        _optionsCard.Height = 470;
        _optionsCard.Margin = new Padding(7, 0, 0, 12);
        var options = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 9; index++) options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // ToggleSwitch includes a logical 30px track plus the 12px top gap. Giving
        // that row an explicit logical height keeps late-created high-DPI editors
        // from letting the following helper label overlap the scaled switch.
        options.RowStyles[7] = new RowStyle(SizeType.Absolute, 42);
        options.Controls.Add(CreateSectionHeading("Clip details", "Choose how the edited copy appears in Discord."), 0, 0);
        options.Controls.Add(CreateFieldLabel("Game"), 0, 1);
        _gameText = CreateEditorTextBox("Edited clip game", source.GameName);
        options.Controls.Add(CreateFieldHost(_gameText), 0, 2);
        options.Controls.Add(CreateFieldLabel("Output file name"), 0, 3);
        _outputNameText = CreateEditorTextBox("Edited clip file name", BuildDefaultOutputName(source.FileName));
        options.Controls.Add(CreateFieldHost(_outputNameText), 0, 4);
        options.Controls.Add(CreateFieldLabel("Discord description (optional)"), 0, 5);
        _noteText = CreateEditorTextBox("Discord description", string.Empty);
        _noteText.Multiline = true;
        _noteText.MaxLength = DiscordClipMessage.MaximumNoteLength;
        _noteText.ScrollBars = ScrollBars.None;
        var noteHost = CreateFieldHost(_noteText);
        noteHost.Height = 72;
        options.Controls.Add(noteHost, 0, 6);
        _muteAudio = new ToggleSwitch
        {
            Name = "MuteEditedClipToggle",
            Text = "Mute audio in edited clip",
            AccessibleName = "Mute audio in edited clip",
            AutoSize = true,
            BackColor = ClipCordTheme.Card,
            ForeColor = ClipCordTheme.Text,
            Margin = new Padding(0, 12, 0, 0)
        };
        options.Controls.Add(_muteAudio, 0, 7);
        options.Controls.Add(CreateMutedLabel(
            "Discord still applies the configured size limit and compression fallback."), 0, 8);
        _optionsCard.Controls.Add(options);
        root.Controls.Add(_optionsCard, 1, 0);

        _trimCard = CreateCard("EditorTrimCard", new Padding(18));
        _trimCard.Height = 300;
        _trimCard.Margin = Padding.Empty;
        var trimLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        trimLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        trimLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        trimLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        trimLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        trimLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        trimLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        trimLayout.Controls.Add(CreateSectionHeading("Trim", "Drag either handle, or enter exact times."), 0, 0);
        _trimRange = new TrimRangeControl
        {
            Name = "EditorTrimRange",
            Dock = DockStyle.Top,
            Height = 46,
            Margin = new Padding(0, 10, 0, 4),
            Enabled = false
        };
        _trimRange.RangeChanged += TrimRangeChanged;
        trimLayout.Controls.Add(_trimRange, 0, 1);
        var timeFields = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        timeFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timeFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        timeFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timeFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var startLabel = CreateFieldLabel("Start");
        startLabel.Margin = new Padding(0, 7, 8, 0);
        _startText = CreateEditorTextBox("Trim start time", "0:00.0");
        _startText.Leave += TrimTextChanged;
        _startText.KeyDown += TrimTextKeyDown;
        var endLabel = CreateFieldLabel("End");
        endLabel.Margin = new Padding(16, 7, 8, 0);
        _endText = CreateEditorTextBox("Trim end time", "—");
        _endText.Leave += TrimTextChanged;
        _endText.KeyDown += TrimTextKeyDown;
        timeFields.Controls.Add(startLabel, 0, 0);
        timeFields.Controls.Add(CreateFieldHost(_startText), 1, 0);
        timeFields.Controls.Add(endLabel, 2, 0);
        timeFields.Controls.Add(CreateFieldHost(_endText), 3, 0);
        trimLayout.Controls.Add(timeFields, 0, 2);
        _estimateLabel = CreateMutedLabel("Approximate edited size: —");
        _estimateLabel.Margin = new Padding(0, 10, 0, 0);
        trimLayout.Controls.Add(_estimateLabel, 0, 3);
        _keepOriginal = new ToggleSwitch
        {
            Name = "KeepLocalOriginalToggle",
            Text = "Keep original in Local only",
            AccessibleName = "Keep original in Local only",
            AutoSize = true,
            BackColor = ClipCordTheme.Card,
            ForeColor = ClipCordTheme.Text,
            Margin = new Padding(0, 9, 0, 0)
        };
        trimLayout.Controls.Add(_keepOriginal, 0, 4);
        _trimCard.Controls.Add(trimLayout);
        root.Controls.Add(_trimCard, 0, 1);

        _commitCard = CreateCard("EditorCommitCard", new Padding(18));
        _commitCard.Height = 300;
        _commitCard.Margin = new Padding(14, 0, 0, 0);
        var commitLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        commitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        commitLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commitLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commitLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        commitLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commitLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commitLayout.Controls.Add(CreateSectionHeading("Upload safely", "The Local-only original stays untouched until Discord confirms success."), 0, 0);
        var cleanup = CreateMutedLabel(
            "Default: archive the edited copy under uploaded, then send the original to the Windows Recycle Bin. Enable Keep original if you want both files.");
        cleanup.AutoSize = true;
        cleanup.MaximumSize = new Size(460, 0);
        cleanup.Margin = new Padding(0, 12, 0, 0);
        commitLayout.Controls.Add(cleanup, 0, 1);
        _progressLabel = new Label
        {
            Name = "EditorProgressLabel",
            Text = "Ready after ClipCord finishes inspecting the clip.",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.ShellMutedText,
            Font = ClipCordTheme.InterfaceFont(9f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 10, 0, 8)
        };
        commitLayout.Controls.Add(_progressLabel, 0, 2);
        _uploadButton = new GradientButton
        {
            Name = "UploadEditedClipButton",
            Text = "Upload edited clip",
            AccessibleName = "Upload edited clip to Discord",
            Width = 180,
            Height = 42,
            Enabled = false,
            Margin = Padding.Empty
        };
        _uploadButton.AutoSize = false;
        _uploadButton.Click += UploadClicked;
        _cancelButton = CreateOutlineButton("Cancel editing", 122);
        _cancelButton.Name = "CancelClipEditorButton";
        _cancelButton.Margin = new Padding(8, 0, 0, 0);
        _cancelButton.Click += CancelClicked;
        var commitActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = ClipCordTheme.Card,
            Margin = Padding.Empty
        };
        commitActions.Controls.Add(_uploadButton);
        commitActions.Controls.Add(_cancelButton);
        commitLayout.Controls.Add(commitActions, 0, 3);
        commitLayout.Controls.Add(new Label
        {
            Text = "No permanent duplicate is created unless Keep original is enabled.",
            AutoSize = true,
            ForeColor = ClipCordTheme.ShellMutedText,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = new Padding(0, 10, 0, 0)
        }, 0, 4);
        _commitCard.Controls.Add(commitLayout);
        root.Controls.Add(_commitCard, 1, 1);
        Controls.Add(root);

        _previewDebounce = new System.Windows.Forms.Timer { Interval = 350 };
        _previewDebounce.Tick += PreviewDebounceTick;
        HandleCreated += EditorHandleCreated;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var topHeight = Math.Max(
            _previewCard.Height + _previewCard.Margin.Vertical,
            _optionsCard.Height + _optionsCard.Margin.Vertical);
        var bottomHeight = Math.Max(
            _trimCard.Height + _trimCard.Margin.Vertical,
            _commitCard.Height + _commitCard.Margin.Vertical);
        return new Size(Math.Max(1, proposedSize.Width), Math.Max(1, topHeight + bottomHeight));
    }

    internal void CancelActiveOperation()
    {
        TryCancel(_operationCancellation);
        TryCancel(_previewCancellation);
    }

    private void EditorHandleCreated(object? sender, EventArgs eventArgs)
    {
        HandleCreated -= EditorHandleCreated;
        ApplyStartupDpiScale();
        _lifetimeCancellation = new CancellationTokenSource();
        _ = InitializeAsync(_lifetimeCancellation.Token);
    }

    private void ApplyStartupDpiScale()
    {
        if (_startupDpiScaleApplied) return;
        _startupDpiScaleApplied = true;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        if (scale > 1.001f)
        {
            Scale(new SizeF(scale, scale));
        }
        if (Parent is BrandedScrollHost scrollHost)
        {
            scrollHost.RefreshContentLayout(preservePosition: false);
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var media = await _service.ProbeAsync(_source, cancellationToken);
            if (!CanUpdate()) return;
            _media = media;
            _updatingTrimText = true;
            try { _trimRange.SetRange(TimeSpan.Zero, media.Duration, media.Duration); }
            finally { _updatingTrimText = false; }
            UpdateTrimTextFromRange();
            _durationLabel.Text = $"Duration: {FormatTime(media.Duration)} · {GalleryView.FormatBytes(media.SourceBytes)}";
            _trimRange.Enabled = true;
            _uploadButton.Enabled = true;
            _progressLabel.Text = "Ready to edit and upload.";
            UpdateEstimate();
            try
            {
                await RefreshPreviewRequestAsync(_trimRange.Start, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A newer trim position superseded the initial frame. Media inspection is
                // still complete, so the editor remains ready while the newer frame wins.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!CanUpdate()) return;
            Log.Error($"Could not initialize the clip editor for {_source.FileName}.", exception);
            _previewStatus.Text = "Preview unavailable";
            _previewStatus.Visible = true;
            _previewStatus.BringToFront();
            _progressLabel.Text = exception.Message;
            _uploadButton.Enabled = false;
        }
    }

    private void UploadClicked(object? sender, EventArgs eventArgs)
    {
        if (_busy) return;
        if (_media is null)
        {
            MessageBox.Show(this, "ClipCord is still inspecting the clip.", "Check the edit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!TryBuildRequest(out var request, out var validationError))
        {
            MessageBox.Show(this, validationError, "Check the edit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true);
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation?.Token ?? CancellationToken.None);
        _operationCancellation = operationCancellation;
        var operationGeneration = ++_operationGeneration;
        var progress = new ControlProgress<ManualClipEditProgress>(this, operationGeneration, update =>
        {
            if (!CanUpdate() || operationGeneration != _operationGeneration) return;
            _progressLabel.Text = update.Message;
            SetOperationCancellationAllowed(update.Stage is
                ManualClipEditStage.Inspecting or
                ManualClipEditStage.Rendering or
                ManualClipEditStage.Compressing);
        });
        _ = RunUploadAsync(request!, progress, operationCancellation, operationGeneration);
    }

    private async Task RunUploadAsync(
        ManualClipEditRequest request,
        IProgress<ManualClipEditProgress> progress,
        CancellationTokenSource operationCancellation,
        int operationGeneration)
    {
        ManualClipEditResult? result = null;
        Exception? failure = null;
        var cancelled = false;
        try
        {
            result = await _service.EditAndUploadAsync(
                    request,
                    progress,
                    operationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        ReleaseOperationCancellation(operationCancellation);
        TryPostToUi(() => FinishUploadOnUi(
            request,
            result,
            failure,
            cancelled,
            operationGeneration));
    }

    private void FinishUploadOnUi(
        ManualClipEditRequest request,
        ManualClipEditResult? result,
        Exception? failure,
        bool cancelled,
        int operationGeneration)
    {
        if (!CanUpdate() || operationGeneration != _operationGeneration) return;
        _operationGeneration++;
        try
        {
            if (cancelled)
            {
                _progressLabel.Text = "Edit cancelled. The Local-only original was not changed.";
                return;
            }
            if (failure is ManualClipCommitPendingException pendingException)
            {
                MessageBox.Show(this, pendingException.Message, "Upload accepted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Completed?.Invoke(request.GameName, GalleryClipRoute.Uploaded);
                return;
            }
            if (failure is not null)
            {
                Log.Error($"Could not upload edited Gallery clip {_source.FileName}.", failure);
                MessageBox.Show(
                    this,
                    failure is TimeoutException
                        ? "Discord did not confirm the result. Check the channel before trying again. The Local-only original was not changed."
                        : $"ClipCord could not upload this edit. The Local-only original was not changed.\n\n{failure.Message}",
                    "Could not upload edit",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _progressLabel.Text = "Upload failed. The Local-only original is still safe.";
                return;
            }
            if (result is null) return;
            var message = result.AlreadyUploaded
                ? "This exact edited content was already uploaded, so ClipCord did not post it again. The Local-only original was kept."
                : result.OriginalCleanupFailed
                    ? "The edited clip was uploaded and archived, but Windows could not move the original to the Recycle Bin. The original was kept in Local only."
                    : result.OriginalKept
                        ? "The edited clip was uploaded and archived. The original remains in Local only."
                        : "The edited clip was uploaded and archived. The original was moved out of Local only safely.";
            MessageBox.Show(this, message, "Edited clip uploaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Completed?.Invoke(
                result.AlreadyUploaded ? _source.GameName : request!.GameName,
                result.AlreadyUploaded ? GalleryClipRoute.LocalOnly : GalleryClipRoute.Uploaded);
        }
        finally
        {
            if (CanUpdate()) SetBusy(false);
        }
    }

    private bool TryBuildRequest(
        out ManualClipEditRequest? request,
        out string? validationError)
    {
        request = null;
        validationError = null;
        if (_media is null)
        {
            validationError = "ClipCord is still inspecting the clip.";
            return false;
        }
        ApplyTrimText();
        if (_trimRange.End <= _trimRange.Start ||
            (_trimRange.End - _trimRange.Start).TotalSeconds < 0.25)
        {
            validationError = "Choose at least 0.25 seconds of the clip.";
            return false;
        }
        var outputName = _outputNameText.Text.Trim();
        try
        {
            ClipEditProcessor.ValidateOutputFileName(outputName);
        }
        catch (ArgumentException)
        {
            validationError = "Enter a valid MP4 output file name without any folders.";
            return false;
        }
        var game = UploadedFolder.SanitizeGameFolderName(_gameText.Text);
        request = new ManualClipEditRequest(
            _source,
            _trimRange.Start,
            _trimRange.End,
            _muteAudio.Checked,
            outputName,
            game,
            _noteText.Text,
            _keepOriginal.Checked);
        return true;
    }

    private void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        _uploadButton.Enabled = !busy && _media is not null;
        _trimRange.Enabled = !busy && _media is not null;
        _startText.Enabled = !busy;
        _endText.Enabled = !busy;
        _gameText.Enabled = !busy;
        _outputNameText.Enabled = !busy;
        _noteText.Enabled = !busy;
        _muteAudio.Enabled = !busy;
        _keepOriginal.Enabled = !busy;
        _playButton.Enabled = !busy;
        if (busy)
        {
            _operationCanCancel = true;
            _cancelButton.Text = "Cancel upload";
            _cancelButton.Enabled = true;
        }
        else
        {
            _operationCanCancel = true;
            _cancelButton.Text = "Cancel editing";
            _cancelButton.Enabled = true;
        }
        BusyChanged?.Invoke(busy);
    }

    private void SetOperationCancellationAllowed(bool allowed)
    {
        if (!_busy || _operationCanCancel == allowed) return;
        _operationCanCancel = allowed;
        _cancelButton.Enabled = allowed;
        _cancelButton.Text = allowed ? "Cancel upload" : "Finishing safely";
    }

    private void CancelClicked(object? sender, EventArgs eventArgs)
    {
        if (_busy)
        {
            if (!_operationCanCancel) return;
            TryCancel(_operationCancellation);
            _progressLabel.Text = "Cancelling safely…";
            return;
        }
        Cancelled?.Invoke();
    }

    private void PlaySource(object? sender, EventArgs eventArgs)
    {
        if (!File.Exists(_source.Path)) return;
        try { Process.Start(GalleryView.CreatePlayClipStartInfo(_source.Path)); }
        catch (Exception exception)
        {
            Log.Error($"Could not play Gallery clip {_source.FileName}.", exception);
            MessageBox.Show(this, "Windows could not open this clip.", "Could not play clip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void TrimRangeChanged(object? sender, EventArgs eventArgs)
    {
        if (_updatingTrimText) return;
        UpdateTrimTextFromRange();
        UpdateEstimate();
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void TrimTextChanged(object? sender, EventArgs eventArgs) => ApplyTrimText();

    private void TrimTextKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode != Keys.Enter) return;
        ApplyTrimText();
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }

    private void ApplyTrimText()
    {
        if (_updatingTrimText || _media is null) return;
        if (!TryParseTime(_startText.Text, out var start) ||
            !TryParseTime(_endText.Text, out var end))
        {
            UpdateTrimTextFromRange();
            return;
        }
        start = TimeSpan.FromSeconds(Math.Clamp(start.TotalSeconds, 0, _media.Duration.TotalSeconds));
        end = TimeSpan.FromSeconds(Math.Clamp(end.TotalSeconds, 0, _media.Duration.TotalSeconds));
        if (end - start < TimeSpan.FromSeconds(0.25))
        {
            UpdateTrimTextFromRange();
            return;
        }
        _trimRange.SetRange(start, end, _media.Duration);
        UpdateTrimTextFromRange();
        UpdateEstimate();
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void UpdateTrimTextFromRange()
    {
        _updatingTrimText = true;
        try
        {
            _startText.Text = FormatTime(_trimRange.Start);
            _endText.Text = FormatTime(_trimRange.End);
        }
        finally { _updatingTrimText = false; }
    }

    private void UpdateEstimate()
    {
        if (_media is null) return;
        var estimate = _service.EstimateOutputBytes(
            _media.SourceBytes,
            _media.Duration,
            _trimRange.End - _trimRange.Start);
        var selectedDuration = (_trimRange.End - _trimRange.Start)
            .ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
        _estimateLabel.Text = $"Approximate edited size: {GalleryView.FormatBytes(estimate)} · {selectedDuration}";
    }

    private async void PreviewDebounceTick(object? sender, EventArgs eventArgs)
    {
        _previewDebounce.Stop();
        if (_busy) return;
        try
        {
            await RefreshPreviewRequestAsync(
                _trimRange.Start,
                _lifetimeCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log.Error("Could not refresh the clip-editor preview frame.", exception);
            if (CanUpdate())
            {
                _previewStatus.Text = "Preview unavailable";
                _previewStatus.Visible = true;
                _previewStatus.BringToFront();
            }
        }
    }

    private async Task RefreshPreviewRequestAsync(TimeSpan position, CancellationToken parentToken)
    {
        TryCancel(_previewCancellation);
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _previewCancellation = requestCancellation;
        var generation = ++_previewGeneration;
        string? returnedPath = null;
        var adopted = false;
        try
        {
            if (CanUpdate())
            {
                _previewStatus.Text = "Refreshing preview…";
                _previewStatus.Visible = true;
                _previewStatus.BringToFront();
            }
            returnedPath = await _service.CreatePreviewFrameAsync(
                _source,
                position,
                requestCancellation.Token);
            requestCancellation.Token.ThrowIfCancellationRequested();
            using var stream = new FileStream(returnedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var loaded = Image.FromStream(stream);
            var clone = new Bitmap(loaded);
            if (!CanUpdate() || generation != _previewGeneration ||
                !ReferenceEquals(_previewCancellation, requestCancellation))
            {
                clone.Dispose();
                return;
            }
            var previousImage = _preview.Image;
            var previousPath = _previewPath;
            _preview.Image = clone;
            _previewPath = returnedPath;
            adopted = true;
            _previewStatus.Text = string.Empty;
            _previewStatus.Visible = false;
            _preview.BringToFront();
            previousImage?.Dispose();
            if (previousPath is not null) TryDelete(previousPath);
        }
        finally
        {
            if (!adopted && returnedPath is not null) TryDelete(returnedPath);
            if (ReferenceEquals(_previewCancellation, requestCancellation))
            {
                _previewCancellation = null;
            }
            requestCancellation.Dispose();
        }
    }

    private bool CanUpdate() => !_disposed && !IsDisposed && !Disposing;

    private bool TryPostToUi(Action action)
    {
        if (!CanUpdate() || !IsHandleCreated) return false;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (CanUpdate()) action();
            }));
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ReleaseOperationCancellation(CancellationTokenSource operationCancellation)
    {
        Interlocked.CompareExchange(ref _operationCancellation, null, operationCancellation);
        operationCancellation.Dispose();
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        var formats = new[] { @"m\:ss\.f", @"m\:ss", @"h\:mm\:ss\.f", @"h\:mm\:ss" };
        return TimeSpan.TryParseExact(
                   value.Trim(),
                   formats,
                   CultureInfo.InvariantCulture,
                   out time) ||
               TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out time);
    }

    internal static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
        : value.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);

    private static string BuildDefaultOutputName(string sourceFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
        var suffix = "-edited";
        var maximumBaseLength = Math.Max(1, 180 - suffix.Length - 4);
        if (baseName.Length > maximumBaseLength) baseName = baseName[..maximumBaseLength].TrimEnd(' ', '.');
        return baseName + suffix + ".mp4";
    }

    private static RoundedPanel CreateCard(string name, Padding padding) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        BackColor = ClipCordTheme.Card,
        BorderColor = ClipCordTheme.CardBorder,
        CornerRadius = 15,
        Padding = padding
    };

    private static Control CreateSectionHeading(string title, string subtitle)
    {
        var panel = new BufferedTableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.Card
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = ClipCordTheme.Text,
            Font = ClipCordTheme.DisplayFont(13f, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);
        panel.Controls.Add(new Label
        {
            Text = subtitle,
            AutoSize = true,
            ForeColor = ClipCordTheme.MutedText,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = new Padding(0, 2, 0, 0)
        }, 0, 1);
        return panel;
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = ClipCordTheme.Text,
        Font = ClipCordTheme.InterfaceFont(8.5f, FontStyle.Bold),
        Margin = new Padding(0, 9, 0, 4)
    };

    private static Label CreateMutedLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = ClipCordTheme.MutedText,
        Font = ClipCordTheme.InterfaceFont(8.5f),
        Margin = Padding.Empty
    };

    private static TextBox CreateEditorTextBox(string accessibleName, string text) => new()
    {
        Text = text,
        AccessibleName = accessibleName,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(244, 246, 249),
        ForeColor = ClipCordTheme.Text,
        Font = ClipCordTheme.InterfaceFont(9.5f),
        Dock = DockStyle.Fill,
        Margin = Padding.Empty
    };

    private static RoundedPanel CreateFieldHost(TextBox textBox) => new()
    {
        Height = 36,
        Dock = DockStyle.Top,
        BackColor = Color.FromArgb(244, 246, 249),
        BorderColor = ClipCordTheme.CardBorder,
        CornerRadius = 8,
        Padding = new Padding(10, 8, 10, 5),
        Margin = Padding.Empty,
        Controls = { textBox }
    };

    private static OutlineButton CreateOutlineButton(string text, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Height = 38,
        SurfaceColor = Color.FromArgb(25, 35, 52),
        HoverColor = Color.FromArgb(35, 46, 65),
        OutlineColor = Color.FromArgb(65, 76, 96),
        ForeColor = ClipCordTheme.ShellText,
        Font = ClipCordTheme.InterfaceFont(9f),
        Margin = Padding.Empty
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _operationGeneration++;
            _previewDebounce.Stop();
            _previewDebounce.Dispose();
            _lifetimeCancellation?.Cancel();
            TryCancel(_previewCancellation);
            TryCancel(_operationCancellation);
            _lifetimeCancellation?.Dispose();
            // Preview/upload continuations own and dispose their CTS instances. Disposing
            // them here can race a continuation that is unwinding after cancellation.
            _preview.Image?.Dispose();
            if (_previewPath is not null) TryDelete(_previewPath);
        }
        base.Dispose(disposing);
    }

    private sealed class ControlProgress<T>(
        LocalClipEditorView owner,
        int generation,
        Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => owner.TryPostToUi(() =>
        {
            if (generation == owner._operationGeneration) handler(value);
        });
    }
}

internal sealed class ClipPreviewControl : Control
{
    private Image? _image;

    internal Image? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            Invalidate();
        }
    }

    internal ClipPreviewControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(BackColor);
        if (_image is null || Width <= 0 || Height <= 0) return;
        eventArgs.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var scale = Math.Min(
            ClientSize.Width / (double)Math.Max(1, _image.Width),
            ClientSize.Height / (double)Math.Max(1, _image.Height));
        var width = Math.Max(1, (int)Math.Round(_image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(_image.Height * scale));
        var destination = new Rectangle(
            (ClientSize.Width - width) / 2,
            (ClientSize.Height - height) / 2,
            width,
            height);
        eventArgs.Graphics.DrawImage(_image, destination);
    }
}

internal sealed class TrimRangeControl : Control
{
    private const double MinimumGapSeconds = 0.25;
    private TimeSpan _duration = TimeSpan.FromSeconds(1);
    private TimeSpan _start;
    private TimeSpan _end = TimeSpan.FromSeconds(1);
    private TrimHandle _dragging;

    internal event EventHandler? RangeChanged;
    internal TimeSpan Start => _start;
    internal TimeSpan End => _end;

    internal TrimRangeControl()
    {
        DoubleBuffered = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "Clip trim range";
        SetStyle(ControlStyles.Selectable, true);
    }

    internal void SetRange(TimeSpan start, TimeSpan end, TimeSpan duration)
    {
        _duration = duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : duration;
        _start = TimeSpan.FromSeconds(Math.Clamp(start.TotalSeconds, 0, _duration.TotalSeconds));
        _end = TimeSpan.FromSeconds(Math.Clamp(end.TotalSeconds, _start.TotalSeconds, _duration.TotalSeconds));
        if ((_end - _start).TotalSeconds < MinimumGapSeconds)
        {
            _end = TimeSpan.FromSeconds(Math.Min(_duration.TotalSeconds, _start.TotalSeconds + MinimumGapSeconds));
            _start = TimeSpan.FromSeconds(Math.Max(0, _end.TotalSeconds - MinimumGapSeconds));
        }
        UpdateAccessibleRange();
        Invalidate();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = GetTrack();
        using var trackPen = new Pen(Color.FromArgb(76, 88, 108), 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        eventArgs.Graphics.DrawLine(trackPen, track.Left, track.Top, track.Right, track.Top);
        var startX = TimeToX(_start);
        var endX = TimeToX(_end);
        using var selectedPen = new Pen(ClipCordTheme.Violet, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        eventArgs.Graphics.DrawLine(selectedPen, startX, track.Top, endX, track.Top);
        DrawHandle(eventArgs.Graphics, startX, track.Top, _dragging == TrimHandle.Start);
        DrawHandle(eventArgs.Graphics, endX, track.Top, _dragging == TrimHandle.End);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        Focus();
        if (eventArgs.Button == MouseButtons.Left)
        {
            _dragging = Math.Abs(eventArgs.X - TimeToX(_start)) <= Math.Abs(eventArgs.X - TimeToX(_end))
                ? TrimHandle.Start
                : TrimHandle.End;
            Capture = true;
            MoveHandle(eventArgs.X);
        }
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_dragging != TrimHandle.None) MoveHandle(eventArgs.X);
        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _dragging = TrimHandle.None;
            Capture = false;
            Invalidate();
        }
        base.OnMouseUp(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        var step = eventArgs.Shift ? 1.0 : 0.1;
        if (eventArgs.KeyCode is Keys.Left or Keys.Right)
        {
            var delta = eventArgs.KeyCode == Keys.Left ? -step : step;
            var target = _dragging == TrimHandle.End ? TrimHandle.End : TrimHandle.Start;
            _dragging = target;
            MoveHandle(TimeToX(target == TrimHandle.Start
                ? _start + TimeSpan.FromSeconds(delta)
                : _end + TimeSpan.FromSeconds(delta)));
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        else if (eventArgs.KeyCode == Keys.Tab && eventArgs.Control)
        {
            _dragging = _dragging == TrimHandle.End ? TrimHandle.Start : TrimHandle.End;
            Invalidate();
            eventArgs.Handled = true;
        }
        base.OnKeyDown(eventArgs);
    }

    private void MoveHandle(int x)
    {
        var value = XToTime(x);
        if (_dragging == TrimHandle.Start)
        {
            _start = TimeSpan.FromSeconds(Math.Clamp(
                value.TotalSeconds,
                0,
                Math.Max(0, _end.TotalSeconds - MinimumGapSeconds)));
        }
        else if (_dragging == TrimHandle.End)
        {
            _end = TimeSpan.FromSeconds(Math.Clamp(
                value.TotalSeconds,
                Math.Min(_duration.TotalSeconds, _start.TotalSeconds + MinimumGapSeconds),
                _duration.TotalSeconds));
        }
        UpdateAccessibleRange();
        Invalidate();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle GetTrack() => new(14, Height / 2, Math.Max(1, Width - 28), 1);

    private void UpdateAccessibleRange()
    {
        AccessibleDescription =
            $"Selected {LocalClipEditorView.FormatTime(_start)} through {LocalClipEditorView.FormatTime(_end)} " +
            $"of {LocalClipEditorView.FormatTime(_duration)}.";
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    private int TimeToX(TimeSpan value)
    {
        var track = GetTrack();
        return track.Left + (int)Math.Round(
            Math.Clamp(value.TotalSeconds / Math.Max(0.001, _duration.TotalSeconds), 0, 1) * track.Width);
    }
    private TimeSpan XToTime(int x)
    {
        var track = GetTrack();
        var ratio = Math.Clamp((x - track.Left) / (double)Math.Max(1, track.Width), 0, 1);
        return TimeSpan.FromSeconds(_duration.TotalSeconds * ratio);
    }

    private static void DrawHandle(Graphics graphics, int x, int y, bool active)
    {
        var bounds = new Rectangle(x - 8, y - 8, 16, 16);
        using var brush = new SolidBrush(active ? ClipCordTheme.Coral : Color.White);
        graphics.FillEllipse(brush, bounds);
        using var outline = new Pen(ClipCordTheme.Violet, 2f);
        graphics.DrawEllipse(outline, bounds);
    }

    private enum TrimHandle
    {
        None,
        Start,
        End
    }
}
