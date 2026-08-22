using System.Diagnostics;

namespace ClipsToDiscord;

internal enum CaptureViewState
{
    Off,
    Ready,
    Buffering,
    Paused,
    Unavailable
}

/// <summary>
/// Production configuration surface for ClipCord's optional recorder. The existing watched-folder
/// pipeline is deliberately not referenced here: SteelSeries and NVIDIA remain independent inputs.
/// The recorder engine is enabled through an explicit availability seam once the hardware benchmark
/// and encoded ring buffer are ready to ship.
/// </summary>
internal sealed class CaptureView : UserControl
{
    private static readonly Color Green = Color.FromArgb(49, 177, 113);
    private static readonly Color Blue = Color.FromArgb(91, 147, 255);
    private static readonly Color Amber = Color.FromArgb(224, 151, 54);

    private readonly AppSettings _externalSettings;
    private readonly bool _engineAvailable;
    private readonly Action<CaptureSettings>? _saveSettings;
    private readonly BrandedScrollHost _scrollHost;
    private readonly ActivityListPanel _content;
    private readonly RoundedPanel _statusPill;
    private readonly Label _statusText;
    private readonly ToggleSwitch _instantReplayToggle;
    private readonly Label _instantReplayDescription;
    private readonly TextBox _saveHotkeyText;
    private readonly OutlineButton _changeShortcutButton;
    private readonly Dictionary<int, OutlineButton> _durationButtons = [];
    private readonly Dictionary<CaptureResolution, OutlineButton> _resolutionButtons = [];
    private readonly Dictionary<int, OutlineButton> _fpsButtons = [];
    private readonly ToggleSwitch _gameAudioToggle;
    private readonly ToggleSwitch _microphoneToggle;
    private readonly ToggleSwitch _voiceChatToggle;
    private readonly ComboBox _gameAudioDevice;
    private readonly ComboBox _microphoneDevice;
    private readonly ComboBox _voiceChatDevice;
    private readonly ToggleSwitch _cameraToggle;
    private readonly Label _cameraStatus;
    private readonly TextBox _libraryRootText;
    private readonly Label _estimateValue;
    private readonly Label _estimateRange;
    private readonly Label _estimateDetails;
    private readonly Label _storageStatus;
    private bool _updating;
    private CaptureSettings _settings;
    private CaptureViewState _state;

    internal event Action<CaptureSettings>? SettingsChanged;

    internal CaptureView(
        AppSettings externalSettings,
        CaptureSettings? settings = null,
        bool engineAvailable = false,
        Action<CaptureSettings>? saveSettings = null)
    {
        _externalSettings = externalSettings;
        _settings = CaptureSettings.Normalize(settings);
        _engineAvailable = engineAvailable;
        _saveSettings = saveSettings;
        _state = !_engineAvailable
            ? CaptureViewState.Unavailable
            : _settings.InstantReplayEnabled ? CaptureViewState.Buffering : CaptureViewState.Off;

        Name = "CaptureView";
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;

        _statusText = new Label
        {
            Name = "CaptureStatusText",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = ClipCordTheme.InterfaceFont(8.25f, FontStyle.Bold),
            ForeColor = Green,
            UseMnemonic = false
        };
        _statusPill = new RoundedPanel
        {
            Name = "CaptureStatusPill",
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 16,
            AccessibleRole = AccessibleRole.StatusBar,
            AccessibleName = "ClipCord Capture status",
            Margin = Padding.Empty
        };
        _statusPill.Controls.Add(_statusText);

        _instantReplayToggle = CreateToggle("InstantReplayToggle", "");
        _instantReplayDescription = CreateHelper(string.Empty);
        _instantReplayDescription.Name = "InstantReplayDescription";
        _saveHotkeyText = CreateReadOnlyField("CaptureSaveHotkey", _settings.SaveHotkey);
        _saveHotkeyText.KeyDown += CaptureSaveHotkey;
        _changeShortcutButton = CreateButton("Change shortcut", "ChangeCaptureShortcutButton", 116);
        _changeShortcutButton.Click += (_, _) =>
        {
            _saveHotkeyText.Focus();
            _saveHotkeyText.SelectAll();
        };

        _gameAudioToggle = CreateToggle("RecordGameAudioToggle", "");
        _microphoneToggle = CreateToggle("IncludeMicrophoneToggle", "");
        _voiceChatToggle = CreateToggle("IncludeVoiceChatToggle", "");
        _gameAudioDevice = CreateDeviceSelector("GameAudioDeviceSelector", CaptureSettings.DefaultOutputDevice);
        _microphoneDevice = CreateDeviceSelector("MicrophoneDeviceSelector", CaptureSettings.DefaultMicrophoneDevice);
        _voiceChatDevice = CreateDeviceSelector("VoiceChatDeviceSelector", CaptureSettings.DefaultVoiceChatDevice);
        _cameraToggle = CreateToggle("IncludeReactionCameraToggle", "");
        _cameraStatus = CreateHelper(string.Empty);
        _cameraStatus.Name = "ReactionCameraStatus";
        _libraryRootText = CreateReadOnlyField("CaptureLibraryRootField", _settings.LibraryRoot);
        _estimateValue = CreateLabel("CaptureEstimatedSizeValue", string.Empty, 22f, FontStyle.Bold, ClipCordTheme.TextPrimary);
        _estimateRange = CreateHelper(string.Empty);
        _estimateRange.Name = "CaptureEstimatedSizeRange";
        _estimateDetails = CreateHelper(string.Empty);
        _estimateDetails.Name = "CaptureEstimateDetails";
        _storageStatus = CreateHelper(string.Empty);
        _storageStatus.Name = "CaptureStorageStatus";

        _content = new ActivityListPanel
        {
            Name = "CaptureContentLayout",
            Padding = ScaleUi(new Padding(28, 4, 28, 24)),
            BackColor = ClipCordTheme.Shell,
            AccessibleRole = AccessibleRole.Pane
        };
        AddCard(BuildInstantReplayCard());
        AddCard(BuildQualityAndEstimateRow());
        AddCard(BuildAudioAndCameraRow());
        AddCard(BuildStorageCard());

        _scrollHost = new BrandedScrollHost
        {
            Name = "CaptureScrollHost",
            AccessibleName = "ClipCord Capture settings",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.Shell,
            Content = _content
        };
        Controls.Add(_scrollHost);
        WireInteractions();
        ApplySettingsToControls();
    }

    internal Control HeaderStatusPill => _statusPill;
    internal CaptureSettings CurrentSettings => _settings;
    internal CaptureViewState State => _state;
    internal bool HasOverflow => _scrollHost.HasOverflow;

    internal void RefreshViewport()
    {
        _content.Reflow();
        _scrollHost.RefreshContentLayout();
        UpdateStorageStatus();
    }

    internal void SetState(CaptureViewState state)
    {
        if (state != CaptureViewState.Unavailable && !_engineAvailable)
        {
            state = CaptureViewState.Unavailable;
        }
        _state = state;
        UpdateRuntimeState();
    }

    private Control BuildInstantReplayCard()
    {
        var card = CreateCard("CaptureInstantReplayCard", 112);
        var layout = CreateTable(1, 3, card.BackColor);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(36)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(34)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Capture,
            Green,
            "Instant Replay",
            _instantReplayDescription,
            _instantReplayToggle), 0, 0);

        var duration = CreateTable(2, 1, card.BackColor);
        duration.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(145)));
        duration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        duration.Controls.Add(CreateRowLabel("Replay length", "Change it before you start"), 0, 0);
        var choices = new FlowLayoutPanel
        {
            Name = "CaptureReplayLengthChoices",
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceSunken
        };
        foreach (var seconds in CaptureSettings.ReplayDurationChoices)
        {
            var button = CreateButton(FormatDuration(seconds), $"CaptureDuration{seconds}Button", 89);
            button.Margin = Padding.Empty;
            button.Click += (_, _) => UpdateConfiguration(_settings with { ReplaySeconds = seconds });
            _durationButtons.Add(seconds, button);
            choices.Controls.Add(button);
        }
        duration.Controls.Add(choices, 1, 0);
        layout.Controls.Add(duration, 0, 1);

        var shortcut = CreateTable(3, 1, card.BackColor);
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(145)));
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(205)));
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shortcut.Controls.Add(CreatePlainLabel("Save hotkey"), 0, 0);
        shortcut.Controls.Add(_saveHotkeyText, 1, 0);
        var shortcutActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = card.BackColor
        };
        shortcutActions.Controls.Add(_changeShortcutButton);
        shortcut.Controls.Add(shortcutActions, 2, 0);
        layout.Controls.Add(shortcut, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildQualityAndEstimateRow()
    {
        var row = new BufferedTableLayoutPanel
        {
            Name = "CaptureQualityAndEstimateRow",
            Height = ScaleUi(162),
            ColumnCount = 2,
            RowCount = 1,
            Margin = ScaleUi(new Padding(0, 0, 0, 6)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var quality = CreateCard("CaptureVideoQualityCard", 162);
        quality.Margin = ScaleUi(new Padding(0, 0, 7, 0));
        quality.Controls.Add(BuildQualityCardContent(quality.BackColor));
        var estimate = CreateCard("CaptureSizeEstimateCard", 162);
        estimate.Margin = ScaleUi(new Padding(7, 0, 0, 0));
        estimate.Controls.Add(BuildEstimateContent(estimate.BackColor));
        row.Controls.Add(quality, 0, 0);
        row.Controls.Add(estimate, 1, 0);
        return row;
    }

    private Control BuildQualityCardContent(Color background)
    {
        var layout = CreateTable(1, 4, background);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(38)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(48)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(34)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Film,
            ClipCordTheme.Violet,
            "Video quality",
            CreateHelper("Applies to every clip you save.")), 0, 0);

        var resolutions = new FlowLayoutPanel
        {
            Name = "CaptureResolutionChoices",
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = background
        };
        foreach (var resolution in Enum.GetValues<CaptureResolution>())
        {
            var dimensions = CaptureProfileCatalog.GetDimensions(resolution);
            var button = CreateButton(
                $"{CaptureProfileCatalog.GetDisplayName(resolution)}  ·  {dimensions.Width}×{dimensions.Height}",
                $"CaptureResolution{resolution}Button",
                154);
            button.Height = ScaleUi(42);
            button.Margin = ScaleUi(new Padding(0, 0, 7, 0));
            button.Click += (_, _) => UpdateConfiguration(_settings with { Resolution = resolution });
            _resolutionButtons.Add(resolution, button);
            resolutions.Controls.Add(button);
        }
        layout.Controls.Add(resolutions, 0, 1);

        var fps = new FlowLayoutPanel
        {
            Name = "CaptureFrameRateChoices",
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = background
        };
        fps.Controls.Add(CreatePlainLabel("Frame rate", ScaleUi(82)));
        foreach (var rate in new[] { 30, 60 })
        {
            var button = CreateButton($"{rate} FPS", $"CaptureFps{rate}Button", 76);
            button.Margin = ScaleUi(new Padding(0, 0, 6, 0));
            button.Click += (_, _) => UpdateConfiguration(_settings with { FramesPerSecond = rate });
            _fpsButtons.Add(rate, button);
            fps.Controls.Add(button);
        }
        layout.Controls.Add(fps, 0, 2);
        var encoder = CreateHelper("Hardware encoding: Automatic  ·  encoder benchmark pending");
        encoder.Name = "CaptureEncoderStatus";
        encoder.BackColor = ClipCordTheme.SurfaceSunken;
        encoder.BorderStyle = BorderStyle.FixedSingle;
        encoder.Dock = DockStyle.Fill;
        encoder.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(encoder, 0, 3);
        return layout;
    }

    private Control BuildEstimateContent(Color background)
    {
        var layout = CreateTable(1, 5, background);
        layout.Padding = ScaleUi(new Padding(2, 0, 2, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(20)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(35)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(38)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(1)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var eyebrow = CreateLabel("CaptureEstimateEyebrow", "ESTIMATED CLIP SIZE", 7.5f, FontStyle.Bold, ClipCordTheme.TextTertiary);
        layout.Controls.Add(eyebrow, 0, 0);
        layout.Controls.Add(_estimateValue, 0, 1);
        layout.Controls.Add(_estimateRange, 0, 2);
        layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = ClipCordTheme.BorderDefault }, 0, 3);
        layout.Controls.Add(_estimateDetails, 0, 4);
        return layout;
    }

    private Control BuildAudioAndCameraRow()
    {
        var row = new BufferedTableLayoutPanel
        {
            Name = "CaptureAudioAndCameraRow",
            Height = ScaleUi(184),
            ColumnCount = 2,
            RowCount = 1,
            Margin = ScaleUi(new Padding(0, 0, 0, 6)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var audio = CreateCard("CaptureAudioCard", 184);
        audio.Margin = ScaleUi(new Padding(0, 0, 7, 0));
        var audioLayout = CreateTable(1, 5, audio.BackColor);
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(38)));
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(31)));
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(31)));
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(31)));
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        audioLayout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Mic,
            Blue,
            "Audio",
            CreateHelper("Every enabled input is mixed into one clip audio stream.")), 0, 0);
        audioLayout.Controls.Add(BuildAudioInputRow(
            _gameAudioToggle,
            "Record game audio",
            _gameAudioDevice,
            FigmaIconAsset.Speaker), 0, 1);
        audioLayout.Controls.Add(BuildAudioInputRow(
            _microphoneToggle,
            "Include microphone",
            _microphoneDevice,
            FigmaIconAsset.Mic), 0, 2);
        audioLayout.Controls.Add(BuildAudioInputRow(
            _voiceChatToggle,
            "Include voice chat",
            _voiceChatDevice,
            FigmaIconAsset.Headset), 0, 3);
        var voiceHelper = CreateHelper(
            "Voice chat is already included when it uses the game output. Select a communications device when chat is routed elsewhere.");
        voiceHelper.Name = "CaptureVoiceChatHelper";
        audioLayout.Controls.Add(voiceHelper, 0, 4);
        audio.Controls.Add(audioLayout);

        var camera = CreateCard("CaptureReactionCameraCard", 184);
        camera.Margin = ScaleUi(new Padding(7, 0, 0, 0));
        var cameraLayout = CreateTable(1, 3, camera.BackColor);
        cameraLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(45)));
        cameraLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(42)));
        cameraLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cameraLayout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Camera,
            ClipCordTheme.TextTertiary,
            "Reaction camera",
            _cameraStatus), 0, 0);
        var cameraToggleRow = CreateTable(2, 1, camera.BackColor);
        cameraToggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cameraToggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(50)));
        cameraToggleRow.Controls.Add(CreateRowLabel(
            "Include reaction camera",
            "Consent is required before capture."), 0, 0);
        cameraToggleRow.Controls.Add(_cameraToggle, 1, 0);
        cameraLayout.Controls.Add(cameraToggleRow, 0, 1);
        var privacy = CreateHelper(
            "Webcam frames stay in memory and are discarded. Saved clips keep the camera as a local editable layer.");
        privacy.Name = "CaptureCameraPrivacyNote";
        privacy.BackColor = ClipCordTheme.SurfaceSunken;
        privacy.BorderStyle = BorderStyle.FixedSingle;
        privacy.Padding = ScaleUi(new Padding(9, 7, 9, 7));
        privacy.Dock = DockStyle.Fill;
        cameraLayout.Controls.Add(privacy, 0, 2);
        camera.Controls.Add(cameraLayout);
        row.Controls.Add(audio, 0, 0);
        row.Controls.Add(camera, 1, 0);
        return row;
    }

    private Control BuildStorageCard()
    {
        var card = CreateCard("CaptureRecordingLocationCard", 112);
        var layout = CreateTable(1, 3, card.BackColor);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(38)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(36)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Disk,
            Amber,
            "Recording location",
            CreateHelper("Originals are organized by game. Uploads never move or replace the original.")), 0, 0);
        var folder = CreateTable(3, 1, card.BackColor);
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(112)));
        folder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(108)));
        folder.Controls.Add(_libraryRootText, 0, 0);
        var change = CreateButton("Change folder", "ChangeCaptureFolderButton", 104);
        change.Click += ChangeLibraryRoot;
        folder.Controls.Add(change, 1, 0);
        var open = CreateButton("Open folder", "OpenCaptureFolderButton", 100);
        open.LeadingGlyph = BrandGlyph.External;
        open.Click += OpenLibraryRoot;
        folder.Controls.Add(open, 2, 0);
        layout.Controls.Add(folder, 0, 1);
        layout.Controls.Add(_storageStatus, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildAudioInputRow(
        ToggleSwitch toggle,
        string label,
        ComboBox selector,
        FigmaIconAsset asset)
    {
        var row = CreateTable(3, 1, ClipCordTheme.SettingsCard);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(50)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(142)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(toggle, 0, 0);
        row.Controls.Add(CreatePlainLabel(label), 1, 0);
        selector.Tag = asset;
        row.Controls.Add(selector, 2, 0);
        return row;
    }

    private Control BuildCardHeader(
        FigmaIconAsset asset,
        Color accent,
        string title,
        Label subtitle,
        Control? trailing = null)
    {
        var header = CreateTable(trailing is null ? 2 : 3, 1, ClipCordTheme.SettingsCard);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(36)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (trailing is not null) header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(54)));
        var badge = new RoundedPanel
        {
            Size = new Size(ScaleUi(28), ScaleUi(28)),
            Anchor = AnchorStyles.Left,
            BackColor = Color.FromArgb(24, accent),
            BorderColor = accent,
            CornerRadius = ScaleUi(8),
            Margin = Padding.Empty
        };
        badge.Controls.Add(new FigmaIconControl
        {
            Asset = asset,
            IconColor = accent,
            Dock = DockStyle.Fill,
            Padding = ScaleUi(new Padding(6))
        });
        header.Controls.Add(badge, 0, 0);
        var copy = CreateTable(1, 2, ClipCordTheme.SettingsCard);
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        copy.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        copy.Controls.Add(CreateLabel(string.Empty, title, 10.5f, FontStyle.Bold, ClipCordTheme.TextPrimary), 0, 0);
        subtitle.Dock = DockStyle.Fill;
        subtitle.TextAlign = ContentAlignment.TopLeft;
        copy.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(copy, 1, 0);
        if (trailing is not null) header.Controls.Add(trailing, 2, 0);
        return header;
    }

    private void WireInteractions()
    {
        _instantReplayToggle.CheckedChanged += (_, _) =>
        {
            if (_updating) return;
            if (!_engineAvailable)
            {
                ApplySettingsToControls();
                MessageBox.Show(
                    this,
                    "The ClipCord recorder engine is not connected in this preview yet. SteelSeries and NVIDIA clips continue working normally.",
                    "Instant Replay unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            UpdateConfiguration(_settings with { InstantReplayEnabled = _instantReplayToggle.Checked });
            SetState(_instantReplayToggle.Checked ? CaptureViewState.Buffering : CaptureViewState.Off);
        };
        _gameAudioToggle.CheckedChanged += (_, _) => UpdateAudioConfiguration();
        _microphoneToggle.CheckedChanged += (_, _) => UpdateAudioConfiguration();
        _voiceChatToggle.CheckedChanged += (_, _) => UpdateAudioConfiguration();
        _gameAudioDevice.SelectedIndexChanged += (_, _) => UpdateAudioConfiguration();
        _microphoneDevice.SelectedIndexChanged += (_, _) => UpdateAudioConfiguration();
        _voiceChatDevice.SelectedIndexChanged += (_, _) => UpdateAudioConfiguration();
        _cameraToggle.CheckedChanged += (_, _) =>
        {
            if (_updating) return;
            if (_cameraToggle.Checked)
            {
                var consent = MessageBox.Show(
                    this,
                    "Include the selected camera when Instant Replay is active? ClipCord will always show a camera-active indicator. Unsaved frames remain in memory and are discarded.",
                    "Allow reaction camera",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (consent != DialogResult.Yes)
                {
                    _updating = true;
                    _cameraToggle.Checked = false;
                    _updating = false;
                    return;
                }
            }
            UpdateConfiguration(_settings with { IncludeReactionCamera = _cameraToggle.Checked });
        };
    }

    private void UpdateAudioConfiguration()
    {
        if (_updating) return;
        UpdateConfiguration(_settings with
        {
            RecordGameAudio = _gameAudioToggle.Checked,
            GameAudioDevice = _gameAudioDevice.Text,
            IncludeMicrophone = _microphoneToggle.Checked,
            MicrophoneDevice = _microphoneDevice.Text,
            IncludeVoiceChat = _voiceChatToggle.Checked,
            VoiceChatDevice = _voiceChatDevice.Text
        });
    }

    private void CaptureSaveHotkey(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Tab) return;
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        if (!GlobalHotkeyBinding.TryFromKeyData(eventArgs.KeyData, out var binding)) return;
        if (string.Equals(
                binding.DisplayText,
                AppSettings.NormalizeModeToggleHotkey(_externalSettings.ModeToggleHotkey),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "That shortcut already switches ClipCord's upload mode. Choose a separate Capture shortcut so one key press cannot trigger two actions.",
                "Shortcut conflict",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        UpdateConfiguration(_settings with { SaveHotkey = binding.DisplayText });
    }

    private void ChangeLibraryRoot(object? sender, EventArgs eventArgs)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where ClipCord stores its Library, Exports, and project data.",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_settings.LibraryRoot)
                ? _settings.LibraryRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var candidate = Path.GetFullPath(dialog.SelectedPath);
        if (CapturePathPolicy.PathsOverlap(candidate, _externalSettings.ClipsFolder))
        {
            MessageBox.Show(
                this,
                "Choose a folder outside the SteelSeries/NVIDIA watched folder. Keeping them separate prevents ClipCord from importing its own recordings twice.",
                "Folders must stay separate",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        UpdateConfiguration(_settings with { LibraryRoot = candidate });
    }

    private void OpenLibraryRoot(object? sender, EventArgs eventArgs)
    {
        try
        {
            Directory.CreateDirectory(_settings.LibraryRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", _settings.LibraryRoot)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Log.Error("Could not open the ClipCord Capture library.", exception);
            MessageBox.Show(
                this,
                "Windows could not open that recording folder.",
                "Could not open folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void UpdateConfiguration(CaptureSettings settings)
    {
        if (_updating) return;
        _settings = CaptureSettings.Normalize(settings);
        _saveSettings?.Invoke(_settings);
        SettingsChanged?.Invoke(_settings);
        ApplySettingsToControls();
    }

    private void ApplySettingsToControls()
    {
        _updating = true;
        try
        {
            _instantReplayToggle.Checked = _settings.InstantReplayEnabled && _engineAvailable;
            _saveHotkeyText.Text = _settings.SaveHotkey;
            _gameAudioToggle.Checked = _settings.RecordGameAudio;
            _microphoneToggle.Checked = _settings.IncludeMicrophone;
            _voiceChatToggle.Checked = _settings.IncludeVoiceChat;
            SelectDevice(_gameAudioDevice, _settings.GameAudioDevice);
            SelectDevice(_microphoneDevice, _settings.MicrophoneDevice);
            SelectDevice(_voiceChatDevice, _settings.VoiceChatDevice);
            _cameraToggle.Checked = _settings.IncludeReactionCamera;
            _libraryRootText.Text = _settings.LibraryRoot;
            UpdateSelectedButtons();
            UpdateEstimate();
            UpdateRuntimeState();
            UpdateStorageStatus();
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateRuntimeState()
    {
        var locked = _state == CaptureViewState.Buffering;
        _instantReplayToggle.Enabled = _engineAvailable;
        _instantReplayDescription.Text = _state switch
        {
            CaptureViewState.Unavailable => "Capture engine integration is still in progress. External clip sources are unaffected.",
            CaptureViewState.Buffering => $"ClipCord keeps the last {FormatDuration(_settings.ReplaySeconds)} in memory. Nothing is written until you press the save hotkey.",
            CaptureViewState.Paused => "Paused — resume Instant Replay when you are ready.",
            _ => "Off — enable it when you want ClipCord to maintain its own replay buffer."
        };
        (_statusText.Text, _statusText.ForeColor) = _state switch
        {
            CaptureViewState.Ready => ("●  READY", Green),
            CaptureViewState.Buffering => ("●  BUFFERING", Green),
            CaptureViewState.Paused => ("●  PAUSED", Amber),
            CaptureViewState.Unavailable => ("●  UNAVAILABLE", Amber),
            _ => ("●  OFF", ClipCordTheme.TextTertiary)
        };
        _statusPill.AccessibleDescription = _statusText.Text.Replace("●", string.Empty).Trim();
        _statusPill.Invalidate(true);
        foreach (var button in _durationButtons.Values.Concat(_resolutionButtons.Values).Concat(_fpsButtons.Values))
        {
            button.Enabled = !locked;
        }
        foreach (var control in new Control[]
                 {
                     _gameAudioToggle, _microphoneToggle, _voiceChatToggle,
                     _gameAudioDevice, _microphoneDevice, _voiceChatDevice, _cameraToggle
                 })
        {
            control.Enabled = !locked;
        }
        _changeShortcutButton.Enabled = true;
        _saveHotkeyText.Enabled = true;
        UpdateDeviceAvailability(locked);
        _cameraStatus.Text = _settings.IncludeReactionCamera
            ? _state == CaptureViewState.Buffering ? "Camera active — indicator shown" : "Configured — camera is not currently active"
            : "Off — the webcam is not in use.";
    }

    private void UpdateDeviceAvailability(bool pipelineLocked)
    {
        _gameAudioDevice.Enabled = !pipelineLocked && _gameAudioToggle.Checked;
        _microphoneDevice.Enabled = !pipelineLocked && _microphoneToggle.Checked;
        _voiceChatDevice.Enabled = !pipelineLocked && _voiceChatToggle.Checked;
        _gameAudioDevice.TabStop = _gameAudioDevice.Enabled;
        _microphoneDevice.TabStop = _microphoneDevice.Enabled;
        _voiceChatDevice.TabStop = _voiceChatDevice.Enabled;
    }

    private void UpdateSelectedButtons()
    {
        foreach (var (seconds, button) in _durationButtons) SetButtonSelected(button, seconds == _settings.ReplaySeconds);
        foreach (var (resolution, button) in _resolutionButtons) SetButtonSelected(button, resolution == _settings.Resolution);
        foreach (var (fps, button) in _fpsButtons) SetButtonSelected(button, fps == _settings.FramesPerSecond);
    }

    private void UpdateEstimate()
    {
        var estimate = CaptureProfileCatalog.Estimate(_settings.Profile, _settings.HasAudio);
        _estimateValue.Text = $"about {FormatMegabytes(estimate.ExpectedBytes)} MB";
        _estimateRange.Text =
            $"Usually {FormatMegabytes(estimate.LowerBoundBytes)}–{FormatMegabytes(estimate.UpperBoundBytes)} MB for a {FormatDuration(_settings.ReplaySeconds)} clip. Motion and detail affect the final size.";
        _estimateDetails.Text =
            $"Replay buffer memory     about {FormatMegabytes(estimate.ExpectedBytes)} MB\r\n" +
            $"Video bitrate                 {estimate.VideoBitrateKbps / 1000d:0.#} Mbps\r\n" +
            $"Clip audio                    {(estimate.AudioBitrateKbps == 0 ? "Off" : "192 kbps, mixed")}";
    }

    private void UpdateStorageStatus()
    {
        if (_settings.OverlapsExternalFolder(_externalSettings.ClipsFolder))
        {
            _storageStatus.ForeColor = Amber;
            _storageStatus.Text = "Choose a separate folder so the external watcher cannot re-import ClipCord recordings.";
            return;
        }
        try
        {
            var root = Path.GetPathRoot(_settings.LibraryRoot);
            var drive = string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
            if (drive is null || !drive.IsReady)
            {
                _storageStatus.ForeColor = Amber;
                _storageStatus.Text = "The selected recording disk is not currently available.";
                return;
            }
            var estimate = Math.Max(1, CaptureProfileCatalog.Estimate(_settings.Profile, _settings.HasAudio).ExpectedBytes);
            var clips = drive.AvailableFreeSpace / estimate;
            _storageStatus.ForeColor = ClipCordTheme.TextSecondary;
            _storageStatus.Text =
                $"{FormatGigabytes(drive.AvailableFreeSpace)} GB free on this disk  ·  room for roughly {clips:N0} clips at " +
                $"{CaptureProfileCatalog.GetDisplayName(_settings.Resolution)} / {_settings.FramesPerSecond} FPS / {FormatDuration(_settings.ReplaySeconds)}";
        }
        catch
        {
            _storageStatus.ForeColor = Amber;
            _storageStatus.Text = "ClipCord could not read available space for this recording folder.";
        }
    }

    private void AddCard(Control card) => _content.Controls.Add(card);

    private RoundedPanel CreateCard(string name, int height) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        Height = ScaleUi(height),
        BackColor = ClipCordTheme.SettingsCard,
        BorderColor = ClipCordTheme.SettingsCardBorder,
        CornerRadius = ScaleUi(16),
        Padding = ScaleUi(new Padding(18, 8, 18, 8)),
        Margin = ScaleUi(new Padding(0, 0, 0, 6)),
        AccessibleRole = AccessibleRole.Grouping
    };

    private static BufferedTableLayoutPanel CreateTable(int columns, int rows, Color background) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = columns,
        RowCount = rows,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        BackColor = background
    };

    private static ToggleSwitch CreateToggle(string name, string text) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = name,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        BackColor = ClipCordTheme.SettingsCard,
        ForeColor = ClipCordTheme.TextPrimary,
        AccessibleRole = AccessibleRole.CheckButton
    };

    private OutlineButton CreateButton(string text, string name, int width) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = text,
        AccessibleRole = AccessibleRole.PushButton,
        AutoSize = false,
        Size = new Size(ScaleUi(width), ScaleUi(30)),
        Margin = Padding.Empty,
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.75f)
    };

    private TextBox CreateReadOnlyField(string name, string text) => new()
    {
        Name = name,
        AccessibleName = name,
        Text = text,
        ReadOnly = true,
        ShortcutsEnabled = false,
        Dock = DockStyle.Fill,
        Margin = ScaleUi(new Padding(0, 2, 8, 2)),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = ClipCordTheme.SurfaceSunken,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9f),
        AccessibleRole = AccessibleRole.Text
    };

    private ComboBox CreateDeviceSelector(string name, string defaultText)
    {
        var selector = new ComboBox
        {
            Name = name,
            AccessibleName = name,
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Margin = ScaleUi(new Padding(0, 2, 0, 2)),
            BackColor = ClipCordTheme.SurfaceSunken,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            IntegralHeight = true,
            AccessibleRole = AccessibleRole.ComboBox
        };
        selector.Items.Add(defaultText);
        selector.SelectedIndex = 0;
        return selector;
    }

    private static void SelectDevice(ComboBox selector, string value)
    {
        if (!selector.Items.Cast<object>().Any(item => string.Equals(item.ToString(), value, StringComparison.Ordinal)))
        {
            selector.Items.Add(value);
        }
        selector.SelectedItem = selector.Items.Cast<object>()
            .First(item => string.Equals(item.ToString(), value, StringComparison.Ordinal));
    }

    private static Label CreatePlainLabel(string text, int width = 0) => new()
    {
        Text = text,
        AutoSize = width == 0,
        Width = width,
        Dock = width == 0 ? DockStyle.Fill : DockStyle.None,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(8.75f),
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private static Control CreateRowLabel(string title, string helper)
    {
        var layout = CreateTable(1, 2, ClipCordTheme.SettingsCard);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        layout.Controls.Add(CreateLabel(string.Empty, title, 8.75f, FontStyle.Regular, ClipCordTheme.TextPrimary), 0, 0);
        layout.Controls.Add(CreateLabel(string.Empty, helper, 7.5f, FontStyle.Regular, ClipCordTheme.TextTertiary), 0, 1);
        return layout;
    }

    private static Label CreateHelper(string text) =>
        CreateLabel(string.Empty, text, 7.75f, FontStyle.Regular, ClipCordTheme.TextTertiary);

    private static Label CreateLabel(string name, string text, float size, FontStyle style, Color color) => new()
    {
        Name = name,
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        ForeColor = color,
        Font = ClipCordTheme.InterfaceFont(size, style),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
        UseMnemonic = false
    };

    private static void SetButtonSelected(OutlineButton button, bool selected)
    {
        button.AccessibilitySelected = selected;
        button.SurfaceColor = selected ? ClipCordTheme.VioletMuted : ClipCordTheme.SurfaceSunken;
        button.OutlineColor = selected ? ClipCordTheme.Violet : ClipCordTheme.BorderStrong;
        button.ForeColor = selected ? ClipCordTheme.TextPrimary : ClipCordTheme.TextSecondary;
        button.Font = ClipCordTheme.InterfaceFont(8.75f, selected ? FontStyle.Bold : FontStyle.Regular);
        button.Invalidate();
    }

    private int ScaleUi(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));

    private Padding ScaleUi(Padding value) => new(
        value.Left == 0 ? 0 : ScaleUi(value.Left),
        value.Top == 0 ? 0 : ScaleUi(value.Top),
        value.Right == 0 ? 0 : ScaleUi(value.Right),
        value.Bottom == 0 ? 0 : ScaleUi(value.Bottom));

    private static string FormatDuration(int seconds) => seconds switch
    {
        < 60 => $"{seconds} sec",
        60 => "60 sec",
        _ => $"{seconds / 60} min"
    };

    private static long FormatMegabytes(long bytes) =>
        Math.Max(0, (long)Math.Round(bytes / 1024d / 1024d, MidpointRounding.AwayFromZero));

    private static long FormatGigabytes(long bytes) =>
        Math.Max(0, (long)Math.Round(bytes / 1024d / 1024d / 1024d, MidpointRounding.AwayFromZero));
}
