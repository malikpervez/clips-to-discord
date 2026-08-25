using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal enum CaptureViewState
{
    Off,
    Armed,
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
    private readonly IManualCaptureRecorder? _manualRecorder;
    private readonly IReplayCaptureController? _replayController;
    private readonly IReactionCameraController? _reactionCameraController;
    private readonly Action<CaptureSettings>? _saveSettings;
    private readonly BrandedScrollHost _scrollHost;
    private readonly ActivityListPanel _content;
    private readonly RoundedPanel _statusPill;
    private readonly Label _statusText;
    private readonly ToggleSwitch _instantReplayToggle;
    private readonly Label _instantReplayDescription;
    private readonly CaptureFieldDisplay _saveHotkeyText;
    private readonly OutlineButton _changeShortcutButton;
    private readonly CaptureFieldDisplay _captureTargetText;
    private readonly OutlineButton _chooseCaptureTargetButton;
    private readonly OutlineButton _manualRecordButton;
    private readonly Label _manualRecordingStatus;
    private readonly Dictionary<int, OutlineButton> _durationButtons = [];
    private readonly Dictionary<CaptureResolution, OutlineButton> _resolutionButtons = [];
    private readonly Dictionary<int, OutlineButton> _fpsButtons = [];
    private readonly ToggleSwitch _gameAudioToggle;
    private readonly ToggleSwitch _microphoneToggle;
    private readonly ToggleSwitch _voiceChatToggle;
    private readonly CaptureDeviceSelector _gameAudioDevice;
    private readonly CaptureDeviceSelector _microphoneDevice;
    private readonly CaptureDeviceSelector _voiceChatDevice;
    private readonly ToggleSwitch _cameraToggle;
    private readonly Label _cameraStatus;
    private readonly ToggleSwitch _landscapeSilhouetteToggle;
    private readonly ToggleSwitch _portraitSilhouetteToggle;
    private readonly Label _silhouetteOutputSummary;
    private readonly OutlineButton _editSilhouetteLayoutsButton;
    private RoundedPanel? _cameraPrivacyNote;
    private Control? _silhouetteOutputControls;
    private readonly CaptureFieldDisplay _libraryRootText;
    private readonly Label _estimateValue;
    private readonly Label _estimateRange;
    private readonly Label _estimateMemoryValue;
    private readonly Label _estimateBitrateValue;
    private readonly Label _estimateAudioValue;
    private readonly Label _storageStatus;
    private bool _updating;
    private bool _cameraToggleBusy;
    private bool _silhouetteOutputGuardBusy;
    private CaptureSettings _settings;
    private CaptureViewState _state;

    internal event Action<CaptureSettings>? SettingsChanged;
    internal event EventHandler? SilhouetteOutputSelectionChanged;
    internal event EventHandler? EditSilhouetteLayoutsRequested;

    internal CaptureView(
        AppSettings externalSettings,
        CaptureSettings? settings = null,
        bool engineAvailable = false,
        Action<CaptureSettings>? saveSettings = null,
        IManualCaptureRecorder? manualRecorder = null)
    {
        _externalSettings = externalSettings;
        _settings = CaptureSettings.Normalize(settings);
        _engineAvailable = engineAvailable;
        _manualRecorder = manualRecorder;
        _replayController = manualRecorder as IReplayCaptureController;
        _reactionCameraController = manualRecorder as IReactionCameraController;
        _saveSettings = saveSettings;
        _state = !_engineAvailable
            ? CaptureViewState.Unavailable
            : _settings.InstantReplayEnabled ? CaptureViewState.Armed : CaptureViewState.Off;

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
        _instantReplayToggle.AccessibleName = "Instant Replay";
        _instantReplayDescription = CreateHelper(string.Empty);
        _instantReplayDescription.Name = "InstantReplayDescription";
        _saveHotkeyText = CreateReadOnlyField("CaptureSaveHotkey", _settings.SaveHotkey);
        _saveHotkeyText.AccessibleName = "Save replay hotkey";
        _saveHotkeyText.KeyDown += CaptureSaveHotkey;
        _changeShortcutButton = CreateButton("Change shortcut", "ChangeCaptureShortcutButton", 116);
        _changeShortcutButton.Click += (_, _) =>
        {
            _saveHotkeyText.Focus();
        };
        _captureTargetText = CreateReadOnlyField(
            "CaptureTargetField",
            "No game window selected",
            FigmaIconAsset.Capture);
        _captureTargetText.AccessibleName = "Selected game window";
        _captureTargetText.TabStop = false;
        _chooseCaptureTargetButton = CreateButton(
            "Choose game",
            "ChooseCaptureTargetButton",
            122);
        _chooseCaptureTargetButton.LeadingGlyph = BrandGlyph.Capture;
        _manualRecordButton = CreateButton(
            "Start test recording",
            "ManualCaptureRecordButton",
            154);
        _manualRecordButton.LeadingGlyph = BrandGlyph.Capture;
        _manualRecordingStatus = CreateHelper(string.Empty);
        _manualRecordingStatus.Name = "ManualCaptureStatus";

        _gameAudioToggle = CreateToggle("RecordGameAudioToggle", "");
        _microphoneToggle = CreateToggle("IncludeMicrophoneToggle", "");
        _voiceChatToggle = CreateToggle("IncludeVoiceChatToggle", "");
        _gameAudioToggle.AccessibleName = "Record game audio";
        _microphoneToggle.AccessibleName = "Include microphone";
        _voiceChatToggle.AccessibleName = "Include voice chat";
        _gameAudioDevice = CreateDeviceSelector("GameAudioDeviceSelector", CaptureSettings.DefaultOutputDevice);
        _gameAudioDevice.AccessibleName = "Game audio device";
        _gameAudioDevice.LeadingIcon = FigmaIconAsset.Speaker;
        _microphoneDevice = CreateDeviceSelector("MicrophoneDeviceSelector", CaptureSettings.DefaultMicrophoneDevice);
        _microphoneDevice.AccessibleName = "Microphone device";
        _microphoneDevice.LeadingIcon = FigmaIconAsset.Mic;
        _voiceChatDevice = CreateDeviceSelector("VoiceChatDeviceSelector", CaptureSettings.DefaultVoiceChatDevice);
        _voiceChatDevice.AccessibleName = "Voice chat device";
        _voiceChatDevice.LeadingIcon = FigmaIconAsset.Headset;
        foreach (var outputDevice in CaptureAudioDeviceCatalog.GetOutputDeviceNames())
        {
            _gameAudioDevice.AddItem(outputDevice);
            _voiceChatDevice.AddItem(outputDevice);
        }
        foreach (var inputDevice in CaptureAudioDeviceCatalog.GetInputDeviceNames())
        {
            _microphoneDevice.AddItem(inputDevice);
        }
        _cameraToggle = CreateToggle("IncludeReactionCameraToggle", "");
        _cameraToggle.AccessibleName = "Include reaction camera";
        _cameraStatus = CreateHelper(string.Empty);
        _cameraStatus.Name = "ReactionCameraStatus";
        _landscapeSilhouetteToggle = CreateToggle("LandscapeSilhouetteOutputToggle", "");
        _landscapeSilhouetteToggle.AccessibleName = "Landscape 16 by 9 silhouette layout";
        _landscapeSilhouetteToggle.AccessibleDescription =
            "Selects the landscape silhouette layout. At least one layout must remain selected.";
        _portraitSilhouetteToggle = CreateToggle("PortraitSilhouetteOutputToggle", "");
        _portraitSilhouetteToggle.AccessibleName = "Portrait 9 by 16 silhouette layout";
        _portraitSilhouetteToggle.AccessibleDescription =
            "Selects the portrait silhouette layout. At least one layout must remain selected.";
        _silhouetteOutputSummary = CreateLabel(
            "SilhouetteOutputSummary",
            string.Empty,
            7.25f,
            FontStyle.Bold,
            ClipCordTheme.TextTertiary);
        _silhouetteOutputSummary.TextAlign = ContentAlignment.MiddleRight;
        _silhouetteOutputSummary.AccessibleRole = AccessibleRole.StatusBar;
        _editSilhouetteLayoutsButton = CreateButton(
            "Edit silhouette layouts",
            "EditSilhouetteLayoutsButton",
            240);
        _editSilhouetteLayoutsButton.LeadingIcon = FigmaIconAsset.Silhouette;
        _editSilhouetteLayoutsButton.Dock = DockStyle.Fill;
        _editSilhouetteLayoutsButton.AccessibleDescription =
            "Opens the reusable landscape and portrait layout editor when that editor is connected.";
        _libraryRootText = CreateReadOnlyField("CaptureLibraryRootField", _settings.LibraryRoot, FigmaIconAsset.Folder);
        _libraryRootText.AccessibleName = "Capture library folder";
        _estimateValue = CreateLabel("CaptureEstimatedSizeValue", string.Empty, 22f, FontStyle.Bold, ClipCordTheme.TextPrimary);
        _estimateRange = CreateHelper(string.Empty);
        _estimateRange.Name = "CaptureEstimatedSizeRange";
        _estimateMemoryValue = CreateHelper(string.Empty);
        _estimateMemoryValue.Name = "CaptureEstimateMemoryValue";
        _estimateBitrateValue = CreateHelper(string.Empty);
        _estimateBitrateValue.Name = "CaptureEstimateBitrateValue";
        _estimateAudioValue = CreateHelper(string.Empty);
        _estimateAudioValue.Name = "CaptureEstimateAudioValue";
        _storageStatus = CreateHelper(string.Empty);
        _storageStatus.Name = "CaptureStorageStatus";

        _content = new ActivityListPanel
        {
            Name = "CaptureContentLayout",
            Padding = ScaleUi(new Padding(20, 4, 20, 5)),
            BackColor = ClipCordTheme.Shell,
            AccessibleRole = AccessibleRole.Pane
        };
        if (_manualRecorder is not null) AddCard(BuildManualRecordingCard());
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
        if (_manualRecorder is not null)
        {
            _manualRecorder.StateChanged += ManualRecorderStateChanged;
        }
        if (_replayController is not null)
        {
            _replayController.ReplayStateChanged += ReplayControllerStateChanged;
            ApplyReplayStatus();
        }
        if (_reactionCameraController is not null)
        {
            _reactionCameraController.ReactionCameraStateChanged += ReactionCameraControllerStateChanged;
        }
        ApplySettingsToControls();
    }

    internal Control HeaderStatusPill => _statusPill;
    internal CaptureSettings CurrentSettings => _settings;
    internal CaptureViewState State => _state;
    internal bool HasOverflow => _scrollHost.HasOverflow;

    internal void ApplyExternalSettings(CaptureSettings settings)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ApplyExternalSettings(settings)));
            return;
        }
        _settings = CaptureSettings.Normalize(settings);
        SettingsChanged?.Invoke(_settings);
        ApplySettingsToControls();
    }

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
        var card = CreateCard("CaptureInstantReplayCard", 130);
        var layout = CreateTable(1, 3, card.BackColor);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(37)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(37)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(40)));
        layout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Capture,
            Green,
            "Instant Replay",
            _instantReplayDescription,
            _instantReplayToggle), 0, 0);

        var duration = CreateTable(2, 1, card.BackColor);
        duration.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
        shortcut.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(145)));
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(130)));
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

    private Control BuildManualRecordingCard()
    {
        var card = CreateCard("CaptureManualRecordingCard", 124);
        var layout = CreateTable(1, 3, card.BackColor);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(40)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(40)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Capture,
            ClipCordTheme.Coral,
            "Game capture test",
            CreateHelper("Choose one game window, then record a real MP4 with your enabled audio inputs.")), 0, 0);

        var target = CreateTable(2, 1, card.BackColor);
        target.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        target.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(132)));
        target.Controls.Add(_captureTargetText, 0, 0);
        target.Controls.Add(_chooseCaptureTargetButton, 1, 0);
        layout.Controls.Add(target, 0, 1);

        var actions = CreateTable(2, 1, card.BackColor);
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(164)));
        _manualRecordingStatus.TextAlign = ContentAlignment.MiddleLeft;
        actions.Controls.Add(_manualRecordingStatus, 0, 0);
        actions.Controls.Add(_manualRecordButton, 1, 0);
        layout.Controls.Add(actions, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildQualityAndEstimateRow()
    {
        var row = new BufferedTableLayoutPanel
        {
            Name = "CaptureQualityAndEstimateRow",
            Height = ScaleUi(189),
            ColumnCount = 2,
            RowCount = 1,
            Margin = ScaleUi(new Padding(0, 0, 0, 6)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66.7f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var quality = CreateCard("CaptureVideoQualityCard", 189);
        quality.Margin = ScaleUi(new Padding(0, 0, 7, 0));
        quality.Controls.Add(BuildQualityCardContent(quality.BackColor));
        var estimate = CreateCard("CaptureSizeEstimateCard", 189);
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
            // Keep the Recommended option wide enough for its badge while the two
            // unbadged options use the narrower Figma treatment. Together with the
            // two inter-option gaps this is 424 logical pixels, so the complete row
            // also fits when Windows constrains the 1200px window to its supported
            // 960px minimum viewport.
            var buttonWidth = resolution == CaptureResolution.FullHd1080p ? 154 : 128;
            var button = CreateButton(
                CaptureProfileCatalog.GetDisplayName(resolution),
                $"CaptureResolution{resolution}Button",
                buttonWidth);
            button.SecondaryText = $"{dimensions.Width}×{dimensions.Height}";
            button.SecondaryBadgeText = resolution == CaptureResolution.FullHd1080p ? "Recommended" : string.Empty;
            button.AccessibleName = $"{CaptureProfileCatalog.GetDisplayName(resolution)}, {dimensions.Width} by {dimensions.Height}" +
                                    (resolution == CaptureResolution.FullHd1080p ? ", recommended" : string.Empty);
            button.Height = ScaleUi(42);
            button.Margin = resolution == CaptureResolution.UltraHd4K
                ? Padding.Empty
                : ScaleUi(new Padding(0, 0, 7, 0));
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
        var details = CreateTable(2, 3, background);
        details.Name = "CaptureEstimateDetails";
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        for (var row = 0; row < 3; row++) details.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333f));
        details.Controls.Add(CreateHelper("Replay buffer memory"), 0, 0);
        details.Controls.Add(CreateHelper("Video bitrate"), 0, 1);
        details.Controls.Add(CreateHelper("Clip audio"), 0, 2);
        foreach (var value in new[] { _estimateMemoryValue, _estimateBitrateValue, _estimateAudioValue })
        {
            value.TextAlign = ContentAlignment.MiddleRight;
        }
        details.Controls.Add(_estimateMemoryValue, 1, 0);
        details.Controls.Add(_estimateBitrateValue, 1, 1);
        details.Controls.Add(_estimateAudioValue, 1, 2);
        layout.Controls.Add(details, 0, 4);
        return layout;
    }

    private Control BuildAudioAndCameraRow()
    {
        var row = new BufferedTableLayoutPanel
        {
            Name = "CaptureAudioAndCameraRow",
            Height = ScaleUi(203),
            ColumnCount = 2,
            RowCount = 1,
            Margin = ScaleUi(new Padding(0, 0, 0, 6)),
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.Shell
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62.6f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37.4f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var audio = CreateCard("CaptureAudioCard", 203);
        audio.Margin = ScaleUi(new Padding(0, 0, 7, 0));
        var audioLayout = CreateTable(1, 5, audio.BackColor);
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(38)));
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(37)));
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(37)));
        audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(37)));
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

        var camera = CreateCard("CaptureReactionCameraCard", 203);
        camera.Margin = ScaleUi(new Padding(7, 0, 0, 0));
        var cameraLayout = CreateTable(1, 4, camera.BackColor);
        cameraLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(38)));
        cameraLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(37)));
        cameraLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(82)));
        cameraLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cameraLayout.Controls.Add(BuildCardHeader(
            FigmaIconAsset.Camera,
            ClipCordTheme.Violet,
            "Reaction camera",
            _cameraStatus), 0, 0);
        var cameraToggleRow = CreateTable(2, 1, camera.BackColor);
        cameraToggleRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cameraToggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cameraToggleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(50)));
        cameraToggleRow.Controls.Add(CreateRowLabel(
            "Include reaction camera",
            "Consent required · opens only while capturing."), 0, 0);
        cameraToggleRow.Controls.Add(_cameraToggle, 1, 0);
        cameraLayout.Controls.Add(cameraToggleRow, 0, 1);
        var cameraDetailHost = new Panel
        {
            Name = "CaptureReactionCameraDetailHost",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = camera.BackColor
        };
        _cameraPrivacyNote = BuildCameraPrivacyNote();
        _silhouetteOutputControls = BuildSilhouetteOutputControls(camera.BackColor);
        _silhouetteOutputControls.Visible = false;
        cameraDetailHost.Controls.Add(_cameraPrivacyNote);
        cameraDetailHost.Controls.Add(_silhouetteOutputControls);
        cameraLayout.Controls.Add(cameraDetailHost, 0, 2);
        _editSilhouetteLayoutsButton.Margin = ScaleUi(new Padding(0, 2, 0, 0));
        _editSilhouetteLayoutsButton.Visible = false;
        cameraLayout.Controls.Add(_editSilhouetteLayoutsButton, 0, 3);
        camera.Controls.Add(cameraLayout);
        row.Controls.Add(audio, 0, 0);
        row.Controls.Add(camera, 1, 0);
        return row;
    }

    private RoundedPanel BuildCameraPrivacyNote()
    {
        var privacy = new RoundedPanel
        {
            Name = "CaptureCameraPrivacyNote",
            Dock = DockStyle.Top,
            Height = ScaleUi(64),
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = ScaleUi(8),
            Padding = ScaleUi(new Padding(12, 8, 12, 8)),
            Margin = Padding.Empty
        };
        var privacyLayout = CreateTable(2, 1, ClipCordTheme.SurfaceSunken);
        privacyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        privacyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(23)));
        privacyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        privacyLayout.Controls.Add(new FigmaIconControl
        {
            Asset = FigmaIconAsset.Shield,
            IconColor = Green,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Size = new Size(ScaleUi(14), ScaleUi(14)),
            Margin = Padding.Empty
        }, 0, 0);
        var privacyCopy = CreateLabel(
            string.Empty,
            "Replay frames stay in memory and are discarded unless saved for local silhouette processing.",
            8.25f,
            FontStyle.Regular,
            ClipCordTheme.TextSecondary);
        privacyCopy.Name = "CaptureCameraPrivacyCopy";
        privacyCopy.TextAlign = ContentAlignment.TopLeft;
        privacyLayout.Controls.Add(privacyCopy, 1, 0);
        privacy.Controls.Add(privacyLayout);
        return privacy;
    }

    private Control BuildStorageCard()
    {
        var card = CreateCard("CaptureRecordingLocationCard", 111);
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
        folder.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
        CaptureDeviceSelector selector,
        FigmaIconAsset asset)
    {
        var row = CreateTable(3, 1, ClipCordTheme.SettingsCard);
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(50)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(142)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(toggle, 0, 0);
        row.Controls.Add(CreatePlainLabel(label), 1, 0);
        selector.LeadingIcon = asset;
        row.Controls.Add(selector, 2, 0);
        return row;
    }

    private Control BuildSilhouetteOutputControls(Color background)
    {
        var layout = CreateTable(1, 3, background);
        layout.Name = "CaptureSilhouetteOutputControls";
        layout.AccessibleName = "Silhouette layout preferences";
        layout.AccessibleRole = AccessibleRole.Grouping;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(18)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(32)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleUi(32)));

        var caption = CreateTable(2, 1, background);
        caption.Name = "CaptureSilhouetteOutputCaption";
        caption.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        caption.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        caption.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        caption.Controls.Add(CreateLabel(
            "SilhouetteOutputCaption",
            "SILHOUETTE LAYOUTS",
            7.25f,
            FontStyle.Bold,
            ClipCordTheme.TextTertiary), 0, 0);
        caption.Controls.Add(_silhouetteOutputSummary, 1, 0);
        layout.Controls.Add(caption, 0, 0);
        layout.Controls.Add(BuildSilhouetteOutputRow(
            "CaptureLandscapeSilhouetteOutputRow",
            _landscapeSilhouetteToggle,
            FigmaIconAsset.Landscape,
            "Landscape · 16:9",
            "Layout preference for Discord and YouTube"), 0, 1);
        layout.Controls.Add(BuildSilhouetteOutputRow(
            "CapturePortraitSilhouetteOutputRow",
            _portraitSilhouetteToggle,
            FigmaIconAsset.Portrait,
            "Portrait · 9:16",
            "Layout preference for TikTok and Shorts"), 0, 2);
        return layout;
    }

    private Control BuildSilhouetteOutputRow(
        string name,
        ToggleSwitch toggle,
        FigmaIconAsset icon,
        string title,
        string helper)
    {
        var row = CreateTable(3, 1, ClipCordTheme.SettingsCard);
        row.Name = name;
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(50)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(25)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(toggle, 0, 0);
        row.Controls.Add(new FigmaIconControl
        {
            Asset = icon,
            IconColor = ClipCordTheme.TextSecondary,
            Size = new Size(ScaleUi(15), ScaleUi(15)),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty
        }, 1, 0);
        row.Controls.Add(CreateRowLabel(title, helper), 2, 0);
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
        header.Name = "CaptureCardHeader";
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(36)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (trailing is not null) header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleUi(64)));
        var badge = new RoundedPanel
        {
            Name = "CaptureCardBadge",
            Size = new Size(ScaleUi(26), ScaleUi(26)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
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
        copy.Name = "CaptureCardHeaderCopy";
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copy.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var titleLabel = CreateLabel("CaptureCardHeaderTitle", title, 10.5f, FontStyle.Bold, ClipCordTheme.TextPrimary);
        titleLabel.AutoSize = true;
        copy.Controls.Add(titleLabel, 0, 0);
        subtitle.Name = string.IsNullOrEmpty(subtitle.Name) ? "CaptureCardHeaderSubtitle" : subtitle.Name;
        subtitle.AutoSize = true;
        subtitle.Dock = DockStyle.Fill;
        subtitle.TextAlign = ContentAlignment.TopLeft;
        copy.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(copy, 1, 0);
        if (trailing is not null)
        {
            trailing.Dock = DockStyle.None;
            trailing.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            trailing.Margin = new Padding(0, 0, ScaleUi(2), 0);
            header.Controls.Add(trailing, 2, 0);
        }
        return header;
    }

    private void WireInteractions()
    {
        _chooseCaptureTargetButton.Click += async (_, _) => await ChooseManualCaptureTargetAsync();
        _manualRecordButton.Click += async (_, _) => await ToggleManualRecordingAsync();
        _instantReplayToggle.CheckedChanged += async (_, _) =>
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
            await ToggleInstantReplayAsync(_instantReplayToggle.Checked);
        };
        _gameAudioToggle.CheckedChanged += (_, _) => UpdateAudioConfiguration();
        _microphoneToggle.CheckedChanged += (_, _) => UpdateAudioConfiguration();
        _voiceChatToggle.CheckedChanged += (_, _) => UpdateAudioConfiguration();
        _gameAudioDevice.SelectedIndexChanged += (_, _) => UpdateAudioConfiguration();
        _microphoneDevice.SelectedIndexChanged += (_, _) => UpdateAudioConfiguration();
        _voiceChatDevice.SelectedIndexChanged += (_, _) => UpdateAudioConfiguration();
        _cameraToggle.CheckedChanged += async (_, _) =>
        {
            if (_updating || _cameraToggleBusy) return;
            await ToggleReactionCameraAsync(_cameraToggle.Checked);
        };
        _landscapeSilhouetteToggle.CheckedChanged += (_, _) =>
            UpdateSilhouetteOutputConfiguration(_landscapeSilhouetteToggle);
        _portraitSilhouetteToggle.CheckedChanged += (_, _) =>
            UpdateSilhouetteOutputConfiguration(_portraitSilhouetteToggle);
        _editSilhouetteLayoutsButton.Click += EditSilhouetteLayouts;
    }

    private void UpdateSilhouetteOutputConfiguration(ToggleSwitch changedToggle)
    {
        if (_updating || _silhouetteOutputGuardBusy) return;
        if (!_landscapeSilhouetteToggle.Checked && !_portraitSilhouetteToggle.Checked)
        {
            // The composition pipeline needs a deterministic output shape whenever Reaction
            // Camera is selected. Restore the switch the user just tried to clear instead of
            // silently choosing the other orientation for them.
            _silhouetteOutputGuardBusy = true;
            try
            {
                changedToggle.Checked = true;
            }
            finally
            {
                _silhouetteOutputGuardBusy = false;
            }
            UpdateSilhouetteOutputState();
            return;
        }

        UpdateConfiguration(_settings with
        {
            SilhouetteLandscapeEnabled = _landscapeSilhouetteToggle.Checked,
            SilhouettePortraitEnabled = _portraitSilhouetteToggle.Checked
        });
        SilhouetteOutputSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditSilhouetteLayouts(object? sender, EventArgs eventArgs)
    {
        var handler = EditSilhouetteLayoutsRequested;
        if (handler is not null)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        MessageBox.Show(
            this,
            "The Silhouette layout editor is unavailable in this window. ClipCord will use your saved layout defaults while preserving the original gameplay and Reaction Camera layers.",
            "Silhouette editor unavailable",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task ToggleReactionCameraAsync(bool enable)
    {
        _cameraToggleBusy = true;
        UpdateRuntimeState();
        try
        {
            if (!enable)
            {
                // Persist the user's privacy choice before asking the active capture worker to
                // release the camera. Releasing the live device still takes precedence if the
                // settings store is unavailable.
                Exception? persistenceFailure = null;
                Exception? stopFailure = null;
                try
                {
                    UpdateConfiguration(_settings with { IncludeReactionCamera = false });
                }
                catch (Exception exception)
                {
                    persistenceFailure = exception;
                    // Keep this view fail-closed too. A manual capture started from the still-open
                    // page must not reopen a camera after the user explicitly switched it off.
                    _settings = CaptureSettings.Normalize(_settings with
                    {
                        IncludeReactionCamera = false
                    });
                    SettingsChanged?.Invoke(_settings);
                    Log.Error("ClipCord could not save the disabled Reaction Camera preference.", exception);
                }
                try
                {
                    if (_reactionCameraController is not null)
                    {
                        await _reactionCameraController.DisableReactionCameraAsync();
                    }
                }
                catch (Exception exception)
                {
                    stopFailure = exception;
                    Log.Error("ClipCord could not confirm that Reaction Camera stopped.", exception);
                }

                var releaseNeedsAttention =
                    _reactionCameraController?.ReactionCameraStatus.State ==
                    ReactionCameraRuntimeState.ReleaseNeedsAttention;
                if (persistenceFailure is not null || stopFailure is not null || releaseNeedsAttention)
                {
                    var detail = releaseNeedsAttention
                        ? string.IsNullOrWhiteSpace(_reactionCameraController?.ReactionCameraStatus.LastError)
                            ? "ClipCord is still trying to release the camera. Exit ClipCord to guarantee camera release."
                            : $"{_reactionCameraController.ReactionCameraStatus.LastError} Exit ClipCord to guarantee camera release."
                        : (persistenceFailure, stopFailure) switch
                    {
                        (not null, not null) =>
                            "ClipCord tried to release the camera, but could not confirm the stop or save your off preference. Close ClipCord to guarantee camera access ends.",
                        (not null, null) =>
                            "The live camera was turned off, but ClipCord could not save that preference. Turn Reaction Camera off again after restarting ClipCord.",
                        _ =>
                            "Your off preference was saved, but ClipCord could not confirm that the live camera stopped. Close ClipCord to guarantee camera access ends."
                    };
                    MessageBox.Show(
                        this,
                        detail,
                        "Reaction Camera needs attention",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            if (!_engineAvailable)
            {
                ApplySettingsToControls();
                MessageBox.Show(
                    this,
                    "Reaction Camera needs the ClipCord capture engine, which is not connected in this build.",
                    "Reaction Camera unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (_settings.ReactionCameraConsentGranted &&
                !string.IsNullOrWhiteSpace(_settings.CameraDeviceId))
            {
                var devices = await ReactionCameraDeviceCatalog.FindAsync();
                var savedDevice = devices.FirstOrDefault(device => string.Equals(
                    device.Id,
                    _settings.CameraDeviceId,
                    StringComparison.Ordinal));
                if (savedDevice is not null)
                {
                    UpdateConfiguration(_settings with
                    {
                        IncludeReactionCamera = true,
                        CameraDevice = savedDevice.Name
                    });
                    return;
                }

                MessageBox.Show(
                    this,
                    "The reaction camera you previously chose is not connected. Choose an available camera to continue.",
                    "Choose a reaction camera",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            using var dialog = new CaptureCameraConsentDialog(
                _settings.CameraDevice,
                _settings.CameraDeviceId);
            if (dialog.ShowDialogWithScrim(this) != DialogResult.OK ||
                string.IsNullOrWhiteSpace(dialog.SelectedDeviceId))
            {
                ApplySettingsToControls();
                return;
            }

            UpdateConfiguration(_settings with
            {
                ReactionCameraConsentGranted = true,
                CameraDeviceId = dialog.SelectedDeviceId,
                CameraDevice = dialog.SelectedDeviceName,
                IncludeReactionCamera = true
            });
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not configure Reaction Camera.", exception);
            ApplySettingsToControls();
            var publicMessage = exception is ReactionCameraUnavailableException
                ? exception.Message
                : "ClipCord could not prepare Reaction Camera. Check Windows camera privacy settings and try again.";
            MessageBox.Show(
                this,
                publicMessage,
                "Reaction Camera could not start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _cameraToggleBusy = false;
            ApplySettingsToControls();
        }
    }

    private async Task ToggleInstantReplayAsync(bool enable)
    {
        if (_replayController is null)
        {
            UpdateConfiguration(_settings with { InstantReplayEnabled = enable });
            SetState(enable ? CaptureViewState.Buffering : CaptureViewState.Off);
            return;
        }
        try
        {
            _instantReplayToggle.Enabled = false;
            if (enable)
            {
                // Persist the armed state first. The application-level target monitor starts the
                // replay worker when a stable game appears, including when no game is open yet.
                UpdateConfiguration(_settings with { InstantReplayEnabled = true });
                _state = CaptureViewState.Armed;
            }
            else
            {
                // Disarm before stopping so a pending target-detection retry cannot restart the
                // worker while the stop command is in flight.
                UpdateConfiguration(_settings with { InstantReplayEnabled = false });
                if (_replayController.ReplayStatus.State != ReplayCaptureState.Off)
                {
                    await _replayController.StopReplayAsync();
                }
            }
            ApplyReplayStatus();
        }
        catch (Exception exception)
        {
            Log.Error(enable
                ? "ClipCord could not enable Instant Replay."
                : "ClipCord could not stop Instant Replay.", exception);
            if (enable)
            {
                if (_settings.InstantReplayEnabled)
                {
                    UpdateConfiguration(_settings with { InstantReplayEnabled = false });
                }
            }
            else if (!_settings.InstantReplayEnabled)
            {
                UpdateConfiguration(_settings with { InstantReplayEnabled = true });
            }
            ApplyReplayStatus();
            var detail = _replayController.ReplayStatus.LastError ??
                (enable
                    ? "ClipCord could not arm Instant Replay."
                    : "ClipCord could not stop the replay buffer cleanly.");
            MessageBox.Show(
                this,
                detail,
                enable ? "Instant Replay could not start" : "Instant Replay could not stop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            ApplySettingsToControls();
        }
    }

    private void ReplayControllerStateChanged(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ReplayControllerStateChanged(sender, eventArgs)));
            return;
        }
        ApplyReplayStatus();
    }

    private void ReactionCameraControllerStateChanged(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ReactionCameraControllerStateChanged(sender, eventArgs)));
            return;
        }
        UpdateRuntimeState();
    }

    private void ApplyReplayStatus()
    {
        if (_replayController is null) return;
        _state = _replayController.ReplayStatus.State switch
        {
            ReplayCaptureState.Starting => CaptureViewState.Ready,
            ReplayCaptureState.Buffering or ReplayCaptureState.Saving => CaptureViewState.Buffering,
            ReplayCaptureState.Stopping => CaptureViewState.Paused,
            ReplayCaptureState.Failed when ReplayCapturePolicy.IsWaitingForGame(
                _settings.InstantReplayEnabled,
                _replayController.ReplayStatus) =>
                CaptureViewState.Armed,
            ReplayCaptureState.Failed => CaptureViewState.Paused,
            _ => _settings.InstantReplayEnabled ? CaptureViewState.Armed : CaptureViewState.Off
        };
        UpdateManualRecorderState();
        UpdateRuntimeState();
    }

    private async Task ChooseManualCaptureTargetAsync()
    {
        if (_manualRecorder is null) return;
        try
        {
            _chooseCaptureTargetButton.Enabled = false;
            _manualRecordingStatus.Text = "Waiting for you to choose the exact game window…";
            var selected = await _manualRecorder.SelectTargetAsync(this);
            if (selected is null)
            {
                _manualRecordingStatus.Text = "Selection cancelled. No capture target changed.";
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not select a ClipCord game capture target.", exception);
            MessageBox.Show(
                this,
                "Windows could not select that game window for capture.",
                "Could not choose game",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            UpdateManualRecorderState();
            UpdateRuntimeState();
        }
    }

    private async Task ToggleManualRecordingAsync()
    {
        if (_manualRecorder is null) return;
        try
        {
            _manualRecordButton.Enabled = false;
            if (_manualRecorder.State == ManualCaptureState.Recording)
            {
                var result = await _manualRecorder.StopAsync();
                if (result is not null)
                {
                    var audioSummary = _settings.HasAudio
                        ? "Enabled audio sources were synchronized and mixed into the clip."
                        : "Audio was disabled for this recording.";
                    var cameraSummary = result.ReactionCameraLayer is not null
                        ? "Reaction Camera was saved separately. ClipCord is preparing the silhouette layout(s) selected when this recording began; your gameplay MP4 remains unchanged."
                        : _settings.IncludeReactionCamera
                            ? "Gameplay was saved, but this recording did not include a Reaction Camera layer."
                            : "Reaction Camera was off for this recording.";
                    if (!string.IsNullOrWhiteSpace(result.ReactionCameraWarning))
                    {
                        cameraSummary += $"\r\n\r\n{result.ReactionCameraWarning}";
                    }
                    MessageBox.Show(
                        this,
                        $"ClipCord saved the test recording to:\r\n\r\n{result.FilePath}\r\n\r\n{audioSummary}\r\n\r\n{cameraSummary}",
                        "Test recording saved",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            else
            {
                if (_manualRecorder.Target is null)
                {
                    _manualRecordingStatus.Text =
                        "Detecting the most recently active game window…";
                    var selected = _manualRecorder is IAutomaticCaptureTargetRecorder automaticRecorder
                        ? await automaticRecorder.DetectTargetAsync()
                        : await _manualRecorder.SelectTargetAsync(this);
                    if (selected is null) return;
                }
                await _manualRecorder.StartAsync(_settings);
            }
        }
        catch (Exception exception)
        {
            Log.Error("The ClipCord game capture test failed.", exception);
            var detail = _manualRecorder.LastError ??
                "ClipCord could not complete that test recording.";
            MessageBox.Show(
                this,
                $"{detail}\r\n\r\nThe original game and external clip folders were not changed.",
                "Capture test failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UpdateManualRecorderState();
            UpdateRuntimeState();
        }
    }

    private void ManualRecorderStateChanged(object? sender, EventArgs eventArgs)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ManualRecorderStateChanged(sender, eventArgs)));
            return;
        }
        UpdateManualRecorderState();
        UpdateRuntimeState();
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
        var normalized = CaptureSettings.Normalize(settings);
        _saveSettings?.Invoke(normalized);
        _settings = normalized;
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
            _cameraToggle.Checked = CaptureSettings.ReactionCameraAvailable &&
                (_settings.IncludeReactionCamera ||
                 _reactionCameraController?.ReactionCameraStatus.IsActive == true);
            _landscapeSilhouetteToggle.Checked = _settings.SilhouetteLandscapeEnabled;
            _portraitSilhouetteToggle.Checked = _settings.SilhouettePortraitEnabled;
            _libraryRootText.Text = _settings.LibraryRoot;
            UpdateSelectedButtons();
            UpdateEstimate();
            UpdateManualRecorderState();
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
        var manualLocked = _manualRecorder is not null &&
            ManualCaptureStatePolicy.IsPipelineBusy(_manualRecorder.State);
        var replayStatus = _replayController?.ReplayStatus;
        var replayLocked = _replayController is null
            ? _state == CaptureViewState.Buffering
            : replayStatus?.State is ReplayCaptureState.Starting or
                ReplayCaptureState.Buffering or ReplayCaptureState.Saving or ReplayCaptureState.Stopping;
        var locked = replayLocked || manualLocked;
        _instantReplayToggle.Enabled = _engineAvailable;
        _instantReplayDescription.Text = _state switch
        {
            CaptureViewState.Unavailable => "Capture engine integration is still in progress. External clip sources are unaffected.",
            CaptureViewState.Armed =>
                "Armed — ClipCord will detect and begin buffering the next supported game automatically.",
            CaptureViewState.Ready => "Starting the replay buffer for the detected game…",
            CaptureViewState.Buffering =>
                $"ClipCord keeps the last {FormatDuration(_settings.ReplaySeconds)} in memory. Nothing is written until you press the save hotkey.",
            CaptureViewState.Paused => "Paused — resume Instant Replay when you are ready.",
            _ => "Off — enable it when you want ClipCord to maintain its own replay buffer."
        };
        (_statusText.Text, _statusText.ForeColor) = replayStatus?.State switch
        {
            ReplayCaptureState.Starting => ("●  WAITING FOR GAME FRAMES", Amber),
            ReplayCaptureState.Buffering => ("●  BUFFERING", ClipCordTheme.Coral),
            ReplayCaptureState.Saving => ("●  SAVING REPLAY", Amber),
            ReplayCaptureState.Stopping => ("●  STOPPING", Amber),
            ReplayCaptureState.Off when _settings.InstantReplayEnabled =>
                ("●  WAITING FOR GAME", Green),
            ReplayCaptureState.Failed when ReplayCapturePolicy.IsWaitingForGame(
                _settings.InstantReplayEnabled,
                replayStatus) =>
                ("●  WAITING FOR GAME", Green),
            ReplayCaptureState.Failed => ("●  REPLAY NEEDS ATTENTION", Amber),
            _ => _manualRecorder?.State switch
            {
            ManualCaptureState.Starting => ("●  STARTING", Amber),
            ManualCaptureState.Recording => ("●  RECORDING", ClipCordTheme.Coral),
            ManualCaptureState.Finalizing => ("●  SAVING", Amber),
            ManualCaptureState.Ready => ("●  TEST READY", Green),
            ManualCaptureState.NoTarget => ("●  SELECT GAME", Amber),
            ManualCaptureState.Failed => ("●  NEEDS ATTENTION", Amber),
                _ => _state switch
                {
                    CaptureViewState.Armed => ("●  WAITING FOR GAME", Green),
                    CaptureViewState.Ready => ("●  READY", Green),
                    CaptureViewState.Buffering => ("●  BUFFERING", ClipCordTheme.Coral),
                    CaptureViewState.Paused => ("●  PAUSED", Amber),
                    CaptureViewState.Unavailable => ("●  UNAVAILABLE", Amber),
                    _ => ("●  OFF", ClipCordTheme.TextTertiary)
                }
            }
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
                     _gameAudioDevice, _microphoneDevice, _voiceChatDevice
                 })
        {
            control.Enabled = !locked;
        }
        _changeShortcutButton.Enabled = true;
        _saveHotkeyText.Enabled = true;
        UpdateDeviceAvailability(locked);
        var cameraRuntime = _reactionCameraController?.ReactionCameraStatus;
        var cameraReleaseNeedsAttention =
            cameraRuntime?.State == ReactionCameraRuntimeState.ReleaseNeedsAttention;
        // Pipeline configuration is locked while capture is active, but a live/enabled
        // camera must always remain independently switchable off for privacy.
        _cameraToggle.Enabled = _engineAvailable &&
            !_cameraToggleBusy &&
            !cameraReleaseNeedsAttention &&
            (!locked || _cameraToggle.Checked || cameraRuntime?.IsActive == true);
        _cameraToggle.TabStop = _cameraToggle.Enabled;
        var cameraSelected = _settings.IncludeReactionCamera ||
            _cameraToggle.Checked ||
            cameraRuntime?.IsActive == true;
        if (_cameraPrivacyNote is not null)
        {
            _cameraPrivacyNote.Visible = !cameraSelected;
            if (_cameraPrivacyNote.Visible) _cameraPrivacyNote.BringToFront();
        }
        if (_silhouetteOutputControls is not null)
        {
            _silhouetteOutputControls.Visible = cameraSelected;
            if (_silhouetteOutputControls.Visible) _silhouetteOutputControls.BringToFront();
        }
        _editSilhouetteLayoutsButton.Visible = cameraSelected;
        var silhouetteControlsEnabled = _engineAvailable &&
            _settings.IncludeReactionCamera &&
            !_cameraToggleBusy &&
            !cameraReleaseNeedsAttention &&
            !locked;
        _landscapeSilhouetteToggle.Enabled = silhouetteControlsEnabled;
        _portraitSilhouetteToggle.Enabled = silhouetteControlsEnabled;
        _editSilhouetteLayoutsButton.Enabled = silhouetteControlsEnabled;
        _landscapeSilhouetteToggle.TabStop = silhouetteControlsEnabled;
        _portraitSilhouetteToggle.TabStop = silhouetteControlsEnabled;
        _editSilhouetteLayoutsButton.TabStop = silhouetteControlsEnabled;
        UpdateSilhouetteOutputState();
        _cameraStatus.Text = cameraReleaseNeedsAttention
            ? string.IsNullOrWhiteSpace(cameraRuntime?.LastError)
                ? "Camera is stopping. Exit ClipCord to guarantee camera release."
                : $"{cameraRuntime.LastError} Camera is stopping; exit ClipCord to guarantee camera release."
            : !_engineAvailable
                ? "Unavailable — the capture engine is not connected."
            : _cameraToggleBusy
                ? "Checking camera…"
                : cameraRuntime?.State == ReactionCameraRuntimeState.Starting
                    ? "Starting — opening camera…"
                : cameraRuntime?.IsActive == true
                    ? "Active — camera is in use."
                    : _settings.IncludeReactionCamera &&
                      !string.IsNullOrWhiteSpace(cameraRuntime?.LastError)
                        ? cameraRuntime.LastError
                        : _settings.IncludeReactionCamera && locked
                            ? "Ready — applies to the next capture."
                            : _settings.IncludeReactionCamera
                                ? _settings.SilhouetteLandscapeEnabled &&
                                  _settings.SilhouettePortraitEnabled
                                    ? "On — both layouts selected."
                                    : "On — one layout selected."
                                : _settings.ReactionCameraConsentGranted
                                    ? "Off — ClipCord consent is saved."
                                    : "Off — preview before enabling.";
    }

    private void UpdateSilhouetteOutputState()
    {
        var selectionCount = (_landscapeSilhouetteToggle.Checked ? 1 : 0) +
            (_portraitSilhouetteToggle.Checked ? 1 : 0);
        _silhouetteOutputSummary.Text = selectionCount switch
        {
            2 => "BOTH SELECTED",
            1 => "1 SELECTED · REQUIRED",
            _ => "ONE REQUIRED"
        };
        _silhouetteOutputSummary.ForeColor = selectionCount == 2
            ? ClipCordTheme.Violet
            : ClipCordTheme.TextTertiary;
        _silhouetteOutputSummary.AccessibleName = selectionCount switch
        {
            2 => "Both silhouette layouts selected",
            1 => "One silhouette layout selected; at least one is required",
            _ => "Select at least one silhouette layout"
        };
    }

    private void UpdateManualRecorderState()
    {
        if (_manualRecorder is null)
        {
            _captureTargetText.Text = "Manual recorder unavailable";
            _chooseCaptureTargetButton.Enabled = false;
            _manualRecordButton.Enabled = false;
            _manualRecordingStatus.Text = string.Empty;
            return;
        }

        _captureTargetText.Text = _manualRecorder.Target is { } target
            ? $"{target.DisplayName}  ·  {target.Width}×{target.Height}"
            : _replayController?.ReplayStatus.Target is { } replayTarget
                ? $"{replayTarget.DisplayName}  ·  {replayTarget.Width}×{replayTarget.Height}"
            : "No game window selected";
        _chooseCaptureTargetButton.Enabled =
            !ManualCaptureStatePolicy.IsPipelineBusy(_manualRecorder.State) &&
            !_settings.InstantReplayEnabled &&
            _replayController?.ReplayStatus.State is null or ReplayCaptureState.Off or ReplayCaptureState.Failed;
        _manualRecordButton.Enabled =
            !_settings.InstantReplayEnabled &&
            (_replayController?.ReplayStatus.State is null or ReplayCaptureState.Off) &&
            ((_manualRecorder.State == ManualCaptureState.NoTarget &&
                _manualRecorder is IAutomaticCaptureTargetRecorder) ||
            ManualCaptureStatePolicy.CanStart(_manualRecorder.State) ||
            ManualCaptureStatePolicy.CanStop(_manualRecorder.State));
        _manualRecordButton.Text = _manualRecorder.State switch
        {
            ManualCaptureState.Starting => "Starting capture…",
            ManualCaptureState.Recording => "Stop & save recording",
            ManualCaptureState.Finalizing => "Saving MP4…",
            _ => "Start test recording"
        };
        _manualRecordingStatus.Text = _manualRecorder.State switch
        {
            ManualCaptureState.NoTarget =>
                "Play or focus a game briefly, then press Start. Choose game remains available as a fallback.",
            ManualCaptureState.Ready => _settings.HasAudio
                ? "Ready to record video with the enabled audio inputs."
                : "Ready to record video. Audio is currently disabled.",
            ManualCaptureState.Starting => "Starting the hardware encoder and enabled Windows audio inputs…",
            ManualCaptureState.Recording => _manualRecorder.LastError ??
                "Recording the selected window and enabled audio inputs. Press Stop when the test is complete.",
            ManualCaptureState.Finalizing => "Synchronizing audio and finalizing the MP4 before it appears in Gallery.",
            ManualCaptureState.Failed => _manualRecorder.LastError ?? "The last capture test failed.",
            _ => string.Empty
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _manualRecorder is not null)
        {
            _manualRecorder.StateChanged -= ManualRecorderStateChanged;
        }
        if (disposing && _replayController is not null)
        {
            _replayController.ReplayStateChanged -= ReplayControllerStateChanged;
        }
        if (disposing && _reactionCameraController is not null)
        {
            _reactionCameraController.ReactionCameraStateChanged -= ReactionCameraControllerStateChanged;
        }
        base.Dispose(disposing);
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
        _estimateMemoryValue.Text =
            $"about {FormatMegabytes(ReplayMemoryPolicy.GetEstimatedResidentBytes(_settings))} MB";
        _estimateBitrateValue.Text = $"{estimate.VideoBitrateKbps / 1000d:0.#} Mbps";
        _estimateAudioValue.Text = estimate.AudioBitrateKbps == 0 ? "Off" : "192 kbps, mixed";
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

    private ToggleSwitch CreateToggle(string name, string text) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = name,
        AutoSize = false,
        MinimumSize = Size.Empty,
        Size = new Size(ScaleUi(42), ScaleUi(23)),
        CompactTrackOnly = true,
        Dock = DockStyle.None,
        Anchor = AnchorStyles.Left,
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

    private CaptureFieldDisplay CreateReadOnlyField(
        string name,
        string text,
        FigmaIconAsset? leadingIcon = null) => new()
    {
        Name = name,
        AccessibleName = name,
        Text = text,
        LeadingIcon = leadingIcon,
        KeycapMode = name == "CaptureSaveHotkey",
        SupportingText = name == "CaptureSaveHotkey"
            ? "saves the buffered clip to your library"
            : string.Empty,
        Dock = DockStyle.Fill,
        Margin = ScaleUi(new Padding(0, 2, 8, 2)),
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9f),
        AccessibleRole = AccessibleRole.Text
    };

    private CaptureDeviceSelector CreateDeviceSelector(string name, string defaultText)
    {
        var selector = new CaptureDeviceSelector
        {
            Name = name,
            AccessibleName = name,
            Dock = DockStyle.Fill,
            Margin = ScaleUi(new Padding(0, 2, 0, 2)),
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            AccessibleRole = AccessibleRole.ComboBox
        };
        selector.AddItem(defaultText);
        selector.SelectValue(defaultText);
        return selector;
    }

    private static void SelectDevice(CaptureDeviceSelector selector, string value)
    {
        selector.AddItem(value);
        selector.SelectValue(value);
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

internal sealed class CaptureFieldDisplay : Control
{
    internal FigmaIconAsset? LeadingIcon { get; init; }
    internal bool KeycapMode { get; init; }
    internal string SupportingText { get; init; } = string.Empty;

    internal CaptureFieldDisplay()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.IBeam;
    }

    protected override void OnTextChanged(EventArgs eventArgs)
    {
        base.OnTextChanged(eventArgs);
        AccessibleDescription = Text;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        base.OnEnabledChanged(eventArgs);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, ScaleLogical(6));
        using var fill = new SolidBrush(SystemInformation.HighContrast
            ? SystemColors.Window
            : ClipCordTheme.SettingsField);
        using var border = new Pen(SystemInformation.HighContrast
            ? SystemColors.WindowText
            : Focused ? ClipCordTheme.Violet : ClipCordTheme.SettingsFieldBorder);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(border, path);

        var textColor = Enabled
            ? SystemInformation.HighContrast ? SystemColors.WindowText : ClipCordTheme.TextPrimary
            : ClipCordTheme.TextTertiary;
        var x = ScaleLogical(11);
        if (LeadingIcon is { } icon)
        {
            var side = ScaleLogical(14);
            FigmaIconRenderer.Draw(
                eventArgs.Graphics,
                new Rectangle(x, (Height - side) / 2, side, side),
                icon,
                Enabled ? ClipCordTheme.TextSecondary : ClipCordTheme.TextTertiary);
            x += side + ScaleLogical(9);
        }

        if (KeycapMode)
        {
            foreach (var key in Text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var measured = TextRenderer.MeasureText(
                    eventArgs.Graphics,
                    key,
                    Font,
                    Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                var keyWidth = measured.Width + ScaleLogical(12);
                var keyHeight = Math.Min(Height - ScaleLogical(8), measured.Height + ScaleLogical(5));
                var keyBounds = new Rectangle(x, (Height - keyHeight) / 2, keyWidth, keyHeight);
                using var keyPath = RoundedPanel.CreateRoundedPath(keyBounds, ScaleLogical(4));
                using var keyFill = new SolidBrush(ClipCordTheme.SurfaceControl);
                using var keyBorder = new Pen(ClipCordTheme.SettingsFieldBorder);
                eventArgs.Graphics.FillPath(keyFill, keyPath);
                eventArgs.Graphics.DrawPath(keyBorder, keyPath);
                TextRenderer.DrawText(
                    eventArgs.Graphics,
                    key,
                    Font,
                    keyBounds,
                    textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                x = keyBounds.Right + ScaleLogical(5);
            }
            if (!string.IsNullOrWhiteSpace(SupportingText))
            {
                TextRenderer.DrawText(
                    eventArgs.Graphics,
                    SupportingText,
                    ClipCordTheme.InterfaceFont(7.75f),
                    new Rectangle(x + ScaleLogical(3), 0, Math.Max(0, Width - x - ScaleLogical(12)), Height),
                    ClipCordTheme.TextTertiary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
        else
        {
            TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                new Rectangle(x, 0, Math.Max(0, Width - x - ScaleLogical(11)), Height),
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -ScaleLogical(3), -ScaleLogical(3)));
        }
    }

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));
}

internal sealed class CaptureDeviceSelector : Control
{
    private readonly List<string> _items = [];
    private readonly ContextMenuStrip _menu;
    private bool _hovered;

    internal FigmaIconAsset LeadingIcon { get; set; } = FigmaIconAsset.Speaker;
    internal event EventHandler? SelectedIndexChanged;

    internal CaptureDeviceSelector()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        AccessibleDefaultActionDescription = "Open device list";
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = ClipCordTheme.SettingsField,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Renderer = new ToolStripProfessionalRenderer()
        };
    }

    internal void AddItem(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || _items.Contains(value, StringComparer.Ordinal)) return;
        _items.Add(value);
        var item = new ToolStripMenuItem(value)
        {
            BackColor = ClipCordTheme.SettingsField,
            ForeColor = ClipCordTheme.TextPrimary,
            AccessibleName = value
        };
        item.Click += (_, _) => SelectValue(value);
        _menu.Items.Add(item);
    }

    internal void SelectValue(string value)
    {
        AddItem(value);
        if (string.Equals(Text, value, StringComparison.Ordinal)) return;
        Text = value;
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnTextChanged(EventArgs eventArgs)
    {
        base.OnTextChanged(eventArgs);
        AccessibleDescription = string.IsNullOrWhiteSpace(Text)
            ? "No device selected"
            : $"Selected device: {Text}";
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        base.OnMouseEnter(eventArgs);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        _hovered = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        base.OnClick(eventArgs);
        ShowMenu();
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (eventArgs.KeyCode is Keys.Enter or Keys.Space or Keys.F4 ||
            eventArgs.Alt && eventArgs.KeyCode == Keys.Down)
        {
            ShowMenu();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        base.OnEnabledChanged(eventArgs);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, ScaleLogical(6));
        using var fill = new SolidBrush(SystemInformation.HighContrast
            ? SystemColors.Window
            : _hovered && Enabled ? ClipCordTheme.SurfaceControl : ClipCordTheme.SettingsField);
        using var border = new Pen(SystemInformation.HighContrast
            ? SystemColors.WindowText
            : Focused ? ClipCordTheme.Violet : ClipCordTheme.SettingsFieldBorder);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(border, path);

        var contentColor = Enabled
            ? SystemInformation.HighContrast ? SystemColors.WindowText : ClipCordTheme.TextPrimary
            : ClipCordTheme.TextTertiary;
        var iconSide = ScaleLogical(14);
        var iconLeft = ScaleLogical(11);
        FigmaIconRenderer.Draw(
            eventArgs.Graphics,
            new Rectangle(iconLeft, (Height - iconSide) / 2, iconSide, iconSide),
            LeadingIcon,
            Enabled ? ClipCordTheme.TextSecondary : ClipCordTheme.TextTertiary);
        var chevronSide = ScaleLogical(13);
        var chevronBounds = new Rectangle(
            Math.Max(0, Width - ScaleLogical(10) - chevronSide),
            (Height - chevronSide) / 2,
            chevronSide,
            chevronSide);
        var state = eventArgs.Graphics.Save();
        eventArgs.Graphics.TranslateTransform(chevronBounds.Left + chevronBounds.Width / 2f, chevronBounds.Top + chevronBounds.Height / 2f);
        eventArgs.Graphics.RotateTransform(90f);
        eventArgs.Graphics.TranslateTransform(-chevronBounds.Width / 2f, -chevronBounds.Height / 2f);
        FigmaIconRenderer.Draw(
            eventArgs.Graphics,
            new Rectangle(0, 0, chevronBounds.Width, chevronBounds.Height),
            FigmaIconAsset.ChevronRight,
            Enabled ? ClipCordTheme.TextSecondary : ClipCordTheme.TextTertiary);
        eventArgs.Graphics.Restore(state);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            new Rectangle(
                iconLeft + iconSide + ScaleLogical(9),
                0,
                Math.Max(0, chevronBounds.Left - iconLeft - iconSide - ScaleLogical(15)),
                Height),
            contentColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -ScaleLogical(3), -ScaleLogical(3)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }

    private void ShowMenu()
    {
        if (!Enabled || _menu.Items.Count == 0) return;
        _menu.MinimumSize = new Size(Width, 0);
        _menu.Show(this, new Point(0, Height));
    }

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));
}

internal sealed class CaptureCameraConsentDialog : Form
{
    private const int DialogCornerRadius = 18;
    private const int DropShadowClassStyle = 0x00020000;
    private const string GenericPreviewError =
        "ClipCord could not preview that camera. Check Windows camera privacy settings or choose another camera.";

    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CaptureDeviceSelector _deviceSelector;
    private readonly CaptureCameraPreviewPanel _preview;
    private readonly CaptureConsentGradientButton _allow;
    private readonly BrandedScrollHost _scrollHost;
    private readonly TableLayoutPanel _contentLayout;
    private readonly Dictionary<string, ReactionCameraDevice> _devices =
        new(StringComparer.Ordinal);
    private readonly string _preferredDeviceId;
    private readonly string _preferredDeviceName;
    private ReactionCameraPreviewSession? _previewSession;
    private string? _previewDeviceId;
    private bool _loadingDevices;
    private bool _previewOperationRunning;
    private bool _closing;
    private bool _resourcesDisposed;
    private Size _lastWindowRegionSize = Size.Empty;

    internal CaptureCameraConsentDialog(string cameraDevice, string cameraDeviceId = "")
    {
        _preferredDeviceName = string.IsNullOrWhiteSpace(cameraDevice)
            ? CaptureSettings.DefaultCameraDevice
            : cameraDevice.Trim();
        _preferredDeviceId = cameraDeviceId?.Trim() ?? string.Empty;

        Text = "ClipCord — Reaction Camera";
        AccessibleName = "Reaction Camera consent and preview";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ClipCordTheme.BorderStrong;
        ForeColor = ClipCordTheme.TextPrimary;
        Font = ClipCordTheme.InterfaceFont(9f);
        ClientSize = new Size(560, 509);
        Padding = new Padding(1);

        var root = _contentLayout = new TableLayoutPanel
        {
            Name = "CaptureCameraConsentLayout",
            Dock = DockStyle.None,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(25, 23, 25, 20),
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard,
            MinimumSize = new Size(0, 506)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 204));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 14),
            BackColor = ClipCordTheme.SettingsCard
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var badge = new RoundedPanel
        {
            BackColor = ClipCordTheme.VioletMuted,
            BorderColor = ClipCordTheme.Violet,
            CornerRadius = 10,
            Size = new Size(34, 34),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            Controls =
            {
                new FigmaIconControl
                {
                    Asset = FigmaIconAsset.Camera,
                    IconColor = ClipCordTheme.Violet,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(8)
                }
            }
        };
        heading.Controls.Add(badge, 0, 0);
        heading.SetRowSpan(badge, 2);
        heading.Controls.Add(new Label
        {
            Text = "Use your camera for reaction clips?",
            Dock = DockStyle.Fill,
            Font = ClipCordTheme.DisplayFont(12.75f, FontStyle.Bold),
            ForeColor = ClipCordTheme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 1, 0);
        heading.Controls.Add(new Label
        {
            Text = "ClipCord will not open the camera until you allow it here.",
            Dock = DockStyle.Fill,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            ForeColor = ClipCordTheme.TextTertiary,
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        }, 1, 1);
        root.Controls.Add(heading, 0, 0);

        _preview = new CaptureCameraPreviewPanel
        {
            Name = "CaptureCameraConsentPreview",
            AccessibleName = "Reaction Camera preview",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14)
        };
        _preview.ShowIdle("Finding available cameras…");
        root.Controls.Add(_preview, 0, 1);

        var deviceRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 14),
            BackColor = ClipCordTheme.SettingsCard
        };
        deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        deviceRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        deviceRow.Controls.Add(new Label
        {
            Text = "Camera",
            Dock = DockStyle.Fill,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            ForeColor = ClipCordTheme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 12, 0),
            UseMnemonic = false
        }, 0, 0);
        _deviceSelector = new CaptureDeviceSelector
        {
            Name = "CaptureCameraConsentDeviceSelector",
            AccessibleName = "Reaction camera device",
            LeadingIcon = FigmaIconAsset.Camera,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Font = ClipCordTheme.InterfaceFont(9f),
            AccessibleRole = AccessibleRole.ComboBox,
            Enabled = false,
            TabStop = false,
            Text = "Finding cameras…"
        };
        _deviceSelector.SelectedIndexChanged += CameraSelectionChanged;
        deviceRow.Controls.Add(_deviceSelector, 1, 0);
        root.Controls.Add(deviceRow, 0, 2);

        var facts = new RoundedPanel
        {
            Name = "CaptureCameraConsentFacts",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceSunken,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 10,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 0, 0, 14),
            AccessibleName = "Reaction Camera privacy facts",
            AccessibleRole = AccessibleRole.Grouping
        };
        var factsLayout = new TableLayoutPanel
        {
            Name = "CaptureCameraConsentFactsLayout",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceSunken
        };
        factsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        factsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var rowIndex = 0; rowIndex < 7; rowIndex++)
        {
            factsLayout.RowStyles.Add(new RowStyle(
                SizeType.Absolute,
                rowIndex % 2 == 0 ? 17 : 8));
        }
        var consentFacts = new[]
        {
            "Unsaved replay camera frames stay in RAM and are discarded with the buffer.",
            "Saving a clip keeps the camera layer locally and prepares your selected silhouette outputs after capture.",
            "A camera indicator stays visible in the tray whenever the buffer is active.",
            "You can turn the camera off at any time without affecting gameplay capture."
        };
        for (var index = 0; index < consentFacts.Length; index++)
        {
            var rowIndex = index * 2;
            factsLayout.Controls.Add(new FigmaIconControl
            {
                Asset = FigmaIconAsset.Check,
                IconColor = Color.FromArgb(49, 177, 113),
                Size = new Size(14, 14),
                Anchor = AnchorStyles.Left,
                Margin = Padding.Empty
            }, 0, rowIndex);
            factsLayout.Controls.Add(new Label
            {
                Name = $"CaptureCameraConsentFact{index + 1}",
                Text = consentFacts[index],
                Dock = DockStyle.Fill,
                Font = ClipCordTheme.InterfaceFont(8.25f),
                ForeColor = ClipCordTheme.TextSecondary,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty,
                UseMnemonic = false
            }, 1, rowIndex);
        }
        facts.Controls.Add(factsLayout);
        root.Controls.Add(facts, 0, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SettingsCard
        };
        _allow = new CaptureConsentGradientButton
        {
            Name = "CaptureAllowCameraButton",
            Text = "Allow & preview",
            AccessibleName = "Allow and preview reaction camera",
            AutoSize = false,
            Size = new Size(132, 33),
            Font = ClipCordTheme.InterfaceFont(9f),
            Margin = Padding.Empty,
            Enabled = false,
            DialogResult = DialogResult.None,
            UseMnemonic = false
        };
        _allow.Click += async (_, _) => await PreviewOrConfirmAsync();
        var decline = CreateDialogButton("Not now", 82);
        decline.Name = "CaptureDeclineCameraButton";
        decline.DialogResult = DialogResult.Cancel;
        decline.Margin = new Padding(0, 0, 10, 0);
        actions.Controls.Add(_allow);
        actions.Controls.Add(decline);
        root.Controls.Add(actions, 0, 4);
        _scrollHost = new BrandedScrollHost
        {
            Name = "CaptureCameraConsentScrollHost",
            AccessibleName = "Reaction Camera consent details",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SettingsCard,
            Content = root
        };
        Controls.Add(_scrollHost);
        AcceptButton = _allow;
        CancelButton = decline;
        Shown += async (_, _) =>
        {
            FitToWorkingArea(Screen.FromControl(this).WorkingArea);
            await LoadDevicesAsync();
        };
        FormClosing += CameraConsentFormClosing;
        Resize += (_, _) =>
        {
            UpdateWindowRegion();
            _scrollHost.RefreshContentLayout();
            _contentLayout.PerformLayout();
        };
        var startupScale = Math.Max(1f, DeviceDpi / 96f);
        if (startupScale > 1f) Scale(new SizeF(startupScale, startupScale));
        _scrollHost.RefreshContentLayout();
        _contentLayout.PerformLayout();
        UpdateWindowRegion();
    }

    internal string SelectedDeviceId { get; private set; } = string.Empty;
    internal string SelectedDeviceName { get; private set; } = CaptureSettings.DefaultCameraDevice;

    internal DialogResult ShowDialogWithScrim(Control owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var ownerForm = owner.FindForm();
        if (ownerForm is null || !ownerForm.Visible || ownerForm.WindowState == FormWindowState.Minimized)
        {
            return ShowDialog(owner);
        }

        using var scrim = new CaptureCameraConsentScrim(ownerForm.Bounds)
        {
            TopMost = ownerForm.TopMost
        };
        scrim.Show(ownerForm);
        try
        {
            return ShowDialog(ownerForm);
        }
        finally
        {
            scrim.Close();
            if (!ownerForm.IsDisposed && ownerForm.Visible) ownerForm.Activate();
        }
    }

    internal void FitToWorkingArea(Rectangle workingArea)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0) return;
        var scale = Math.Max(96, DeviceDpi) / 96d;
        var desiredSize = new Size(
            Math.Max(Width, (int)Math.Round(560 * scale)),
            Math.Max(Height, (int)Math.Round(509 * scale)));
        var maximumSize = new Size(
            Math.Max(1, workingArea.Width - 32),
            Math.Max(1, workingArea.Height - 32));
        var fittedSize = new Size(
            Math.Min(desiredSize.Width, maximumSize.Width),
            Math.Min(desiredSize.Height, maximumSize.Height));
        MinimumSize = new Size(
            Math.Min(fittedSize.Width, Math.Max(1, (int)Math.Round(420 * scale))),
            Math.Min(fittedSize.Height, Math.Max(1, (int)Math.Round(320 * scale))));
        MaximumSize = maximumSize;
        Size = fittedSize;

        var centeringBounds = Owner is { Visible: true } owner
            ? Rectangle.Intersect(owner.Bounds, workingArea)
            : workingArea;
        if (centeringBounds.Width <= 0 || centeringBounds.Height <= 0) centeringBounds = workingArea;
        Location = new Point(
            centeringBounds.Left + Math.Max(0, (centeringBounds.Width - Width) / 2),
            centeringBounds.Top + Math.Max(0, (centeringBounds.Height - Height) / 2));
        _scrollHost.RefreshContentLayout();
        _contentLayout.PerformLayout();
        UpdateWindowRegion();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= DropShadowClassStyle;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        UpdateWindowRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        FitToWorkingArea(Screen.FromControl(this).WorkingArea);
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastWindowRegionSize == Size) return;
        _lastWindowRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(
            new Rectangle(0, 0, Width, Height),
            Math.Max(1, (int)Math.Round(DialogCornerRadius * Math.Max(96, DeviceDpi) / 96d)));
        var replacement = new Region(path);
        var previous = Region;
        Region = replacement;
        previous?.Dispose();
    }

    private async Task LoadDevicesAsync()
    {
        if (_loadingDevices || _closing || _devices.Count > 0) return;
        _loadingDevices = true;
        try
        {
            var devices = await ReactionCameraDeviceCatalog.FindAsync(_lifetimeCancellation.Token);
            if (_closing || IsDisposed) return;
            if (devices.Count == 0)
            {
                _deviceSelector.Text = "No camera detected";
                _preview.ShowError("No camera was detected. Connect a camera, then reopen this window.");
                return;
            }

            foreach (var device in devices)
            {
                var count = 1;
                var displayName = device.Name;
                while (_devices.ContainsKey(displayName))
                {
                    count++;
                    displayName = $"{device.Name} ({count})";
                }
                _devices[displayName] = device;
                _deviceSelector.AddItem(displayName);
            }

            var preferred = _devices.FirstOrDefault(pair => string.Equals(
                pair.Value.Id,
                _preferredDeviceId,
                StringComparison.Ordinal));
            if (string.IsNullOrEmpty(preferred.Key))
            {
                preferred = _devices.FirstOrDefault(pair => string.Equals(
                    pair.Value.Name,
                    _preferredDeviceName,
                    StringComparison.OrdinalIgnoreCase));
            }
            if (string.IsNullOrEmpty(preferred.Key)) preferred = _devices.First();

            _deviceSelector.SelectValue(preferred.Key);
            _deviceSelector.Enabled = true;
            _deviceSelector.TabStop = true;
            _allow.Enabled = true;
            _preview.ShowIdle("Press Allow & preview to check this camera.");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the modal cancels enumeration without surfacing a spurious error.
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not enumerate reaction cameras.", exception);
            if (_closing || IsDisposed) return;
            _deviceSelector.Text = "Camera list unavailable";
            _preview.ShowError(exception is ReactionCameraUnavailableException
                ? exception.Message
                : GenericPreviewError);
        }
        finally
        {
            _loadingDevices = false;
        }
    }

    private async Task PreviewOrConfirmAsync()
    {
        if (_previewOperationRunning || _closing) return;
        if (!_devices.TryGetValue(_deviceSelector.Text, out var device))
        {
            _preview.ShowError("Choose an available camera to continue.");
            return;
        }

        if (_previewSession?.IsActive == true &&
            string.Equals(_previewDeviceId, device.Id, StringComparison.Ordinal))
        {
            SelectedDeviceId = device.Id;
            SelectedDeviceName = device.Name;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        _previewOperationRunning = true;
        _allow.Enabled = false;
        _deviceSelector.Enabled = false;
        StopPreview(clearFrame: true);
        _preview.ShowStarting();
        try
        {
            var session = await ReactionCameraPreviewSession.StartAsync(
                device.Id,
                ReceivePreviewFrame,
                _lifetimeCancellation.Token);
            if (_closing || IsDisposed)
            {
                try { session.Dispose(); }
                catch (Exception exception)
                {
                    Log.Error("ClipCord could not close the reaction camera preview cleanly.", exception);
                }
                return;
            }

            _previewSession = session;
            _previewDeviceId = device.Id;
            session.Failed += PreviewSessionFailed;
            if (!session.IsActive)
            {
                PreviewSessionFailed(session, EventArgs.Empty);
                return;
            }
            _preview.ShowActive();
            _allow.Text = "Use this camera";
            _allow.AccessibleName = "Use this reaction camera";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the modal cancels the preview without mutating Capture settings.
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not preview the selected reaction camera.", exception);
            if (_closing || IsDisposed) return;
            _preview.ShowError(exception is ReactionCameraUnavailableException
                ? exception.Message
                : GenericPreviewError);
        }
        finally
        {
            _previewOperationRunning = false;
            if (!_closing && !IsDisposed)
            {
                _deviceSelector.Enabled = _devices.Count > 0;
                _allow.Enabled = _devices.Count > 0;
            }
        }
    }

    private void ReceivePreviewFrame(Bitmap bitmap)
    {
        if (_closing || IsDisposed)
        {
            bitmap.Dispose();
            return;
        }
        _preview.SetFrame(bitmap);
    }

    private void PreviewSessionFailed(object? sender, EventArgs eventArgs)
    {
        if (_closing || IsDisposed || !ReferenceEquals(sender, _previewSession)) return;
        var error = _previewSession?.LastError ?? GenericPreviewError;
        StopPreview(clearFrame: true);
        _preview.ShowError(error);
        _allow.Text = "Try preview again";
        _allow.AccessibleName = "Try reaction camera preview again";
        _allow.Enabled = _devices.Count > 0;
        _deviceSelector.Enabled = _devices.Count > 0;
    }

    private void CameraSelectionChanged(object? sender, EventArgs eventArgs)
    {
        if (_loadingDevices || _previewOperationRunning || _closing) return;
        if (_previewSession is null) return;
        StopPreview(clearFrame: true);
        _preview.ShowIdle("Press Allow & preview to check this camera.");
        _allow.Text = "Allow & preview";
        _allow.AccessibleName = "Allow and preview reaction camera";
    }

    private void StopPreview(bool clearFrame)
    {
        var session = _previewSession;
        _previewSession = null;
        _previewDeviceId = null;
        if (session is not null)
        {
            session.Failed -= PreviewSessionFailed;
            try { session.Dispose(); }
            catch (Exception exception)
            {
                Log.Error("ClipCord could not close the reaction camera preview cleanly.", exception);
            }
        }
        if (clearFrame) _preview.ClearFrame();
    }

    private void CameraConsentFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        _closing = true;
        _lifetimeCancellation.Cancel();
        StopPreview(clearFrame: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _closing = true;
            _lifetimeCancellation.Cancel();
            StopPreview(clearFrame: true);
            _deviceSelector.SelectedIndexChanged -= CameraSelectionChanged;
            _lifetimeCancellation.Dispose();
        }
        base.Dispose(disposing);
    }

    private static OutlineButton CreateDialogButton(string text, int width) => new()
    {
        Text = text,
        AccessibleName = text,
        AutoSize = false,
        Size = new Size(width, 33),
        SurfaceColor = ClipCordTheme.SurfaceControl,
        HoverColor = ClipCordTheme.SurfaceControlHover,
        OutlineColor = ClipCordTheme.BorderStrong,
        ForeColor = ClipCordTheme.TextPrimary,
        Font = ClipCordTheme.InterfaceFont(9f),
        Margin = Padding.Empty,
        UseMnemonic = false
    };
}

internal sealed class CaptureCameraConsentScrim : Form
{
    internal CaptureCameraConsentScrim(Rectangle ownerBounds)
    {
        Name = "CaptureCameraConsentScrim";
        AccessibleName = "ClipCord modal background";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ControlBox = false;
        BackColor = Color.FromArgb(5, 10, 20);
        Opacity = 0.72d;
        Bounds = ownerBounds;
        TabStop = false;
    }

    protected override bool ShowWithoutActivation => true;
}

internal sealed class CaptureConsentGradientButton : Button
{
    private Size _lastRegionSize = Size.Empty;

    internal CaptureConsentGradientButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        DoubleBuffered = true;
        Resize += (_, _) => UpdateRegion();
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
        var bounds = new Rectangle(0, 0, Width, Height);
        var start = Enabled ? ClipCordTheme.Coral : Color.FromArgb(105, 110, 123);
        var end = Enabled ? ClipCordTheme.Violet : Color.FromArgb(105, 110, 123);
        using var background = new SolidBrush(start);
        eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        using var path = RoundedPanel.CreateRoundedPath(bounds, ScaleLogical(8));
        using var brush = new LinearGradientBrush(bounds, start, end, LinearGradientMode.Horizontal);
        eventArgs.Graphics.FillPath(brush, path);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            bounds,
            Enabled ? ForeColor : Color.FromArgb(225, 225, 230),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(
                eventArgs.Graphics,
                Rectangle.Inflate(ClientRectangle, -ScaleLogical(4), -ScaleLogical(4)));
        }
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(
            new Rectangle(0, 0, Width, Height),
            ScaleLogical(8));
        var replacement = new Region(path);
        var previous = Region;
        Region = replacement;
        previous?.Dispose();
    }

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));
}

internal sealed class CaptureCameraPreviewPanel : Panel
{
    private static readonly Color PreviewRed = Color.FromArgb(240, 90, 84);
    private Bitmap? _frame;
    private string _indicator = "CAMERA OFF";
    private string _message = string.Empty;
    private string _footer = "Nothing is being recorded";

    internal CaptureCameraPreviewPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(20, 28, 46);
    }

    internal void SetFrame(Bitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var previous = _frame;
        _frame = frame;
        previous?.Dispose();
        _message = string.Empty;
        ShowActive();
    }

    internal void ClearFrame()
    {
        var previous = _frame;
        _frame = null;
        previous?.Dispose();
        Invalidate();
    }

    internal void ShowIdle(string message)
    {
        _indicator = "CAMERA OFF";
        _message = message;
        _footer = "Nothing is being recorded";
        AccessibleDescription = $"Camera off. {message}";
        Invalidate();
    }

    internal void ShowStarting()
    {
        _indicator = "OPENING PREVIEW";
        _message = "Waiting for the selected camera…";
        _footer = "Preview only — nothing is being recorded";
        AccessibleDescription = "Opening camera preview. Nothing is being recorded.";
        Invalidate();
    }

    internal void ShowActive()
    {
        _indicator = "CAMERA PREVIEW";
        _footer = "Preview only — nothing is being recorded";
        AccessibleDescription = "Camera preview active. Nothing is being recorded.";
        Invalidate();
    }

    internal void ShowError(string message)
    {
        ClearFrame();
        _indicator = "PREVIEW UNAVAILABLE";
        _message = message;
        _footer = "Nothing is being recorded";
        AccessibleDescription = $"Camera preview unavailable. {message}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = ScaleLogical(12);
        using var panelPath = RoundedPanel.CreateRoundedPath(bounds, radius);
        var graphicsState = eventArgs.Graphics.Save();
        eventArgs.Graphics.SetClip(panelPath);
        using (var background = new LinearGradientBrush(
                   bounds,
                   Color.FromArgb(46, 58, 91),
                   Color.FromArgb(20, 28, 46),
                   25f))
        {
            eventArgs.Graphics.FillRectangle(background, bounds);
        }

        // The approved consent surface uses the full wide preview well. Crop a live
        // camera frame to that viewport instead of pillar-boxing it into a smaller
        // 16:9 rectangle, which made the idle and live states look visually broken.
        var videoBounds = bounds;
        using (var videoBackground = new SolidBrush(Color.FromArgb(10, 18, 32)))
        {
            eventArgs.Graphics.FillRectangle(videoBackground, videoBounds);
        }
        if (_frame is not null)
        {
            var source = GetAspectFillSource(
                _frame.Size,
                videoBounds.Width / (float)Math.Max(1, videoBounds.Height));
            var previewState = eventArgs.Graphics.Save();
            eventArgs.Graphics.TranslateTransform(videoBounds.Right, videoBounds.Top);
            eventArgs.Graphics.ScaleTransform(-1f, 1f);
            eventArgs.Graphics.DrawImage(
                _frame,
                new Rectangle(0, 0, videoBounds.Width, videoBounds.Height),
                source,
                GraphicsUnit.Pixel);
            eventArgs.Graphics.Restore(previewState);
        }
        else if (!string.IsNullOrWhiteSpace(_message))
        {
            TextRenderer.DrawText(
                eventArgs.Graphics,
                _message,
                ClipCordTheme.InterfaceFont(8.5f),
                Rectangle.Inflate(videoBounds, -ScaleLogical(28), -ScaleLogical(28)),
                ClipCordTheme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        DrawIndicator(eventArgs.Graphics);
        using (var footerBackdrop = new SolidBrush(Color.FromArgb(145, 10, 18, 32)))
        {
            eventArgs.Graphics.FillRectangle(
                footerBackdrop,
                0,
                Math.Max(0, Height - ScaleLogical(30)),
                Width,
                ScaleLogical(30));
        }
        TextRenderer.DrawText(
            eventArgs.Graphics,
            _footer,
            ClipCordTheme.InterfaceFont(8f),
            new Rectangle(
                ScaleLogical(14),
                Math.Max(0, Height - ScaleLogical(28)),
                Math.Max(0, Width - ScaleLogical(28)),
                ScaleLogical(22)),
            ClipCordTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        eventArgs.Graphics.Restore(graphicsState);
        using var border = new Pen(ClipCordTheme.BorderDefault);
        eventArgs.Graphics.DrawPath(border, panelPath);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var frame = _frame;
            _frame = null;
            frame?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void DrawIndicator(Graphics graphics)
    {
        var font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold);
        var textSize = TextRenderer.MeasureText(
            graphics,
            _indicator,
            font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        var pill = new Rectangle(
            ScaleLogical(12),
            ScaleLogical(10),
            textSize.Width + ScaleLogical(32),
            ScaleLogical(25));
        using var path = RoundedPanel.CreateRoundedPath(pill, pill.Height / 2);
        using var fill = new SolidBrush(Color.FromArgb(235, 10, 18, 32));
        using var outline = new Pen(PreviewRed);
        graphics.FillPath(fill, path);
        graphics.DrawPath(outline, path);
        using var dot = new SolidBrush(PreviewRed);
        var dotSide = ScaleLogical(7);
        graphics.FillEllipse(
            dot,
            pill.Left + ScaleLogical(9),
            pill.Top + (pill.Height - dotSide) / 2,
            dotSide,
            dotSide);
        TextRenderer.DrawText(
            graphics,
            _indicator,
            font,
            new Rectangle(
                pill.Left + ScaleLogical(21),
                pill.Top,
                pill.Width - ScaleLogical(25),
                pill.Height),
            PreviewRed,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private static Rectangle GetAspectFillSource(Size source, float targetAspect)
    {
        if (source.Width <= 0 || source.Height <= 0) return Rectangle.Empty;
        var sourceAspect = source.Width / (float)source.Height;
        if (sourceAspect > targetAspect)
        {
            var width = Math.Max(1, (int)Math.Round(source.Height * targetAspect));
            return new Rectangle((source.Width - width) / 2, 0, width, source.Height);
        }
        var height = Math.Max(1, (int)Math.Round(source.Width / targetAspect));
        return new Rectangle(0, (source.Height - height) / 2, source.Width, height);
    }

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));
}
