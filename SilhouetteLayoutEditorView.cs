using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal sealed class SilhouetteLayoutSavedEventArgs(
    SilhouettePreferencesDocument preferences) : EventArgs
{
    internal SilhouettePreferencesDocument Preferences { get; } = preferences;
}

/// <summary>
/// Edits reusable, normalized layout defaults for future Reaction Camera captures. This view
/// deliberately previews geometry only: background removal and video rendering happen after a
/// clip is saved and are not simulated here.
/// </summary>
internal sealed class SilhouetteLayoutEditorView : UserControl
{
    private const string LandscapeId = CompositionOrientationIds.Landscape;
    private const string PortraitId = CompositionOrientationIds.Portrait;

    private readonly string _settingsDirectory;
    private readonly bool _mirrorCameraDefault;
    private readonly Dictionary<string, SilhouetteTransform> _transforms;
    private readonly BrandedScrollHost _scrollHost;
    private BrandedScrollHost _inspectorScrollHost = null!;
    private readonly SilhouetteEditorContentLayout _content;
    private readonly SilhouetteBackButton _headerBackButton;
    private readonly SilhouetteLayoutGuide _guide;
    private readonly OutlineButton _landscapeTab;
    private readonly OutlineButton _portraitTab;
    private readonly Dictionary<SilhouettePlacementPreset, OutlineButton> _presetButtons = [];
    private readonly Dictionary<PortraitGameplayLayoutMode, OutlineButton> _portraitModeButtons = [];
    private readonly SilhouetteValueSlider _sizeSlider;
    private readonly SilhouetteValueSlider _horizontalSlider;
    private readonly SilhouetteValueSlider _verticalSlider;
    private readonly SilhouetteValueSlider _gameplayWidthSlider;
    private readonly SilhouetteValueSlider _gameplayCenterSlider;
    private readonly SilhouetteValueSlider _gameplayTopSlider;
    private readonly SilhouetteValueSlider _focusZoomSlider;
    private readonly SilhouetteValueSlider _focusCenterSlider;
    private readonly ToggleSwitch _mirrorToggle;
    private readonly ToggleSwitch _safeAreasToggle;
    private readonly Dictionary<SilhouetteOutlineStyle, OutlineButton> _outlineButtons = [];
    private readonly Panel _portraitModeSection;
    private readonly Panel _portraitGameplaySection;
    private readonly Panel _portraitFocusSection;
    private Label _orientationDescription = null!;
    private readonly Label _saveStatus;
    private readonly GradientButton _saveButton;
    private SilhouettePreferencesDocument _loadedDocument;
    private PortraitCompositionSettings _portraitComposition;
    private string _selectedOrientationId = LandscapeId;
    private bool _syncingControls;
    private bool _saveRunning;
    private bool _startupDpiScaleApplied;

    internal event EventHandler? BackRequested;
    internal event EventHandler<SilhouetteLayoutSavedEventArgs>? Saved;

    internal SilhouetteLayoutEditorView(string settingsDirectory, bool mirrorCameraDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        _settingsDirectory = Path.GetFullPath(settingsDirectory.Trim());
        _mirrorCameraDefault = mirrorCameraDefault;

        var loadResult = SilhouettePreferencesStore.LoadOrDefault(
            _settingsDirectory,
            mirrorCameraDefault);
        _loadedDocument = loadResult.Document;
        _portraitComposition = _loadedDocument.PortraitComposition;
        _transforms = _loadedDocument.Layouts.ToDictionary(
            layout => layout.OrientationId,
            layout => layout.Transform,
            StringComparer.Ordinal);

        Name = "SilhouetteLayoutEditorView";
        AccessibleName = "Silhouette layouts editor";
        AccessibleDescription =
            "Adjust reusable Landscape and Portrait Reaction Camera layout guides for future captures.";
        AccessibleRole = AccessibleRole.Pane;
        Dock = DockStyle.Fill;
        BackColor = ClipCordTheme.Shell;
        ForeColor = ClipCordTheme.TextPrimary;
        Font = ClipCordTheme.InterfaceFont(9.5f);

        var header = BuildHeader(out _headerBackButton);
        var orientationBar = BuildOrientationBar(out _landscapeTab, out _portraitTab);

        _guide = new SilhouetteLayoutGuide
        {
            Name = "SilhouetteLayoutGuide",
            AccessibleName = "Silhouette layout guide",
            AccessibleDescription =
                "A geometry guide, not processed live video. Drag the silhouette or use arrow keys to reposition it; use plus and minus to resize it.",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        _guide.TransformChanged += GuideTransformChanged;
        var previewCard = BuildPreviewCard(_guide, out _safeAreasToggle);

        _sizeSlider = CreateSlider(
            "SilhouetteSizeSlider",
            "Silhouette size",
            CaptureCompositionModel.MinimumHeightFraction,
            CaptureCompositionModel.MaximumHeightFraction,
            0.01,
            value => $"{value:P0}");
        _horizontalSlider = CreateSlider(
            "SilhouetteHorizontalSlider",
            "Horizontal position",
            0,
            1,
            0.01,
            value => $"{value:P0}");
        _verticalSlider = CreateSlider(
            "SilhouetteVerticalSlider",
            "Vertical position",
            CaptureCompositionModel.MinimumHeightFraction,
            1,
            0.01,
            value => $"{value:P0}");
        _gameplayWidthSlider = CreateSlider(
            "PortraitGameplayWidthSlider",
            "Gameplay width",
            CaptureCompositionModel.MinimumPortraitGameplayWidthFraction,
            CaptureCompositionModel.MaximumPortraitGameplayWidthFraction,
            0.01,
            value => $"{value:P0}");
        _gameplayCenterSlider = CreateSlider(
            "PortraitGameplayCenterSlider",
            "Gameplay center",
            0,
            1,
            0.01,
            value => $"{value:P0}");
        _gameplayTopSlider = CreateSlider(
            "PortraitGameplayTopSlider",
            "Gameplay top",
            0,
            1,
            0.01,
            value => $"{value:P0}");
        _focusZoomSlider = CreateSlider(
            "PortraitFocusZoomSlider",
            "Crop zoom",
            CaptureCompositionModel.MinimumFocusCropZoom,
            CaptureCompositionModel.MaximumFocusCropZoom,
            0.05,
            value => $"{value:0.00}×");
        _focusCenterSlider = CreateSlider(
            "PortraitFocusCenterSlider",
            "Crop focal point",
            0,
            1,
            0.01,
            value => $"{value:P0}");

        foreach (var slider in new[] { _sizeSlider, _horizontalSlider, _verticalSlider })
        {
            slider.ValueChanged += (_, _) => TransformControlChanged();
        }
        foreach (var slider in new[]
                 {
                     _gameplayWidthSlider,
                     _gameplayCenterSlider,
                     _gameplayTopSlider,
                     _focusZoomSlider,
                     _focusCenterSlider
                 })
        {
            slider.ValueChanged += (_, _) => PortraitCompositionControlChanged();
        }

        _mirrorToggle = new ToggleSwitch
        {
            Name = "SilhouetteMirrorToggle",
            Text = "Mirror camera",
            AccessibleName = "Mirror Reaction Camera horizontally",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(9f)
        };
        _mirrorToggle.CheckedChanged += (_, _) => TransformControlChanged();

        _portraitModeSection = BuildPortraitModeSection();
        _portraitGameplaySection = BuildPortraitGameplaySection();
        _portraitFocusSection = BuildPortraitFocusSection();
        var inspector = BuildInspector();
        var footer = BuildFooter(loadResult.Status, out _saveStatus, out _saveButton);

        _content = new SilhouetteEditorContentLayout(
            header,
            orientationBar,
            previewCard,
            inspector,
            footer)
        {
            Name = "SilhouetteLayoutEditorContent",
            AccessibleName = "Silhouette layout settings",
            AccessibleRole = AccessibleRole.Pane,
            BackColor = ClipCordTheme.Shell
        };
        _scrollHost = new BrandedScrollHost
        {
            Name = "SilhouetteLayoutEditorScrollHost",
            AccessibleName = "Silhouette layout editor content",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.Shell,
            Content = _content
        };
        Controls.Add(_scrollHost);
        HandleCreated += EditorHandleCreated;

        _landscapeTab.Click += (_, _) => SelectOrientation(LandscapeId);
        _portraitTab.Click += (_, _) => SelectOrientation(PortraitId);
        _safeAreasToggle.CheckedChanged += (_, _) =>
        {
            _guide.ShowSafeAreas = _safeAreasToggle.Checked;
            _guide.Invalidate();
        };
        _saveButton.Click += async (_, _) => await SavePreferencesAsync();
        SelectOrientation(LandscapeId);
    }

    internal string SelectedOrientationId => _selectedOrientationId;
    internal bool HasOverflow => _scrollHost.HasOverflow;
    internal SilhouettePreferencesDocument CurrentPreferences => CreateCurrentDocument(DateTimeOffset.UtcNow);
    internal Control HeaderBackButton => _headerBackButton;

    internal void RefreshViewport() => _scrollHost.RefreshContentLayout();

    internal void SetEmbeddedHeaderVisible(bool visible)
    {
        _content.EmbeddedHeaderVisible = visible;
        _scrollHost.RefreshContentLayout();
    }

    private void EditorHandleCreated(object? sender, EventArgs eventArgs)
    {
        HandleCreated -= EditorHandleCreated;
        if (_startupDpiScaleApplied) return;
        _startupDpiScaleApplied = true;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        if (scale > 1.001f) Scale(new SizeF(scale, scale));
        _scrollHost.RefreshContentLayout(preservePosition: false);
    }

    internal void SelectOrientation(string orientationId)
    {
        if (!CompositionOrientationIds.All.Contains(orientationId, StringComparer.Ordinal))
        {
            throw new ArgumentException("The silhouette orientation is not supported.", nameof(orientationId));
        }

        _selectedOrientationId = orientationId;
        var portrait = orientationId.Equals(PortraitId, StringComparison.Ordinal);
        if (!portrait) _safeAreasToggle.Checked = false;
        _safeAreasToggle.Enabled = portrait;
        _safeAreasToggle.TabStop = portrait;
        _safeAreasToggle.AccessibleDescription = portrait
            ? "Shows the combined TikTok and YouTube Shorts safe region."
            : "Safe-area overlays apply only to Portrait output.";
        _landscapeTab.AccessibilitySelected = !portrait;
        _portraitTab.AccessibilitySelected = portrait;
        ApplySelectedButtonStyle(_landscapeTab, !portrait);
        ApplySelectedButtonStyle(_portraitTab, portrait);
        _portraitModeSection.Visible = portrait;
        UpdatePortraitControlVisibility();
        _orientationDescription.Text = portrait
            ? "Keep the full play in view by default, then place your silhouette for vertical video."
            : "Place your silhouette once for Discord and standard widescreen video.";
        SyncControlsFromModel();
        _content.PerformLayout();
        _scrollHost.RefreshContentLayout();
    }

    internal async Task<bool> SavePreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (_saveRunning || IsDisposed || Disposing) return false;
        _saveRunning = true;
        _saveButton.Enabled = false;
        _saveStatus.ForeColor = ClipCordTheme.TextSecondary;
        _saveStatus.Text = "Saving layout defaults…";
        try
        {
            var saved = await SilhouettePreferencesStore.SaveAsync(
                _settingsDirectory,
                CreateCurrentDocument(DateTimeOffset.UtcNow),
                _mirrorCameraDefault,
                cancellationToken);
            if (IsDisposed || Disposing) return false;
            _loadedDocument = saved;
            _saveStatus.ForeColor = Color.FromArgb(126, 218, 175);
            _saveStatus.Text = "Saved. New Reaction Camera clips will use these layouts.";
            Saved?.Invoke(this, new SilhouetteLayoutSavedEventArgs(saved));
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                _saveStatus.ForeColor = ClipCordTheme.TextSecondary;
                _saveStatus.Text = "Save cancelled. Your previous defaults are unchanged.";
            }
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("Could not save silhouette layout defaults.", exception);
            if (!IsDisposed && !Disposing)
            {
                _saveStatus.ForeColor = Color.FromArgb(244, 178, 84);
                _saveStatus.Text = "Could not save. Your previous defaults are unchanged.";
            }
            return false;
        }
        finally
        {
            _saveRunning = false;
            if (!IsDisposed && !Disposing) _saveButton.Enabled = true;
        }
    }

    private Control BuildHeader(out SilhouetteBackButton back)
    {
        var header = new Panel
        {
            Name = "SilhouetteLayoutEditorHeader",
            BackColor = ClipCordTheme.Shell,
            Margin = Padding.Empty
        };
        var backButton = new SilhouetteBackButton
        {
            Name = "SilhouetteLayoutBackButton",
            Text = "Back to Capture",
            AccessibleName = "Back to Capture",
            Size = new Size(136, 34),
            Location = new Point(620, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TabIndex = 0
        };
        backButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        header.Controls.Add(backButton);
        header.Resize += (_, _) =>
        {
            if (ReferenceEquals(backButton.Parent, header))
            {
                backButton.Left = Math.Max(0, header.ClientSize.Width - backButton.Width);
            }
        };
        header.Controls.Add(new Label
        {
            Name = "SilhouetteLayoutEditorTitle",
            Text = "Silhouette layouts",
            AutoSize = true,
            Location = new Point(0, 3),
            Font = ClipCordTheme.DisplayFont(19f, FontStyle.Bold),
            ForeColor = ClipCordTheme.TextPrimary,
            AccessibleRole = AccessibleRole.StaticText
        });
        header.Controls.Add(new Label
        {
            Name = "SilhouetteLayoutEditorSubtitle",
            Text = "Reusable composition defaults for future Reaction Camera clips",
            AutoSize = true,
            Location = new Point(2, 34),
            Font = ClipCordTheme.InterfaceFont(8.75f),
            ForeColor = ClipCordTheme.TextSecondary,
            AccessibleRole = AccessibleRole.StaticText
        });
        back = backButton;
        return header;
    }

    private Control BuildOrientationBar(out OutlineButton landscape, out OutlineButton portrait)
    {
        var bar = new RoundedPanel
        {
            Name = "SilhouetteOrientationBar",
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 11,
            Padding = new Padding(8, 6, 8, 6),
            Margin = Padding.Empty,
            AccessibleName = "Output orientation",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        landscape = CreateSegmentButton(
            "SilhouetteLandscapeTab",
            "Landscape 16:9",
            "Edit Landscape 16 by 9 layout",
            FigmaIconAsset.Landscape);
        portrait = CreateSegmentButton(
            "SilhouettePortraitTab",
            "Portrait 9:16",
            "Edit Portrait 9 by 16 layout",
            FigmaIconAsset.Portrait);
        landscape.Margin = new Padding(0, 0, 6, 0);
        portrait.Margin = Padding.Empty;
        layout.Controls.Add(landscape, 0, 0);
        layout.Controls.Add(portrait, 1, 0);
        var hint = new Label
        {
            Name = "SilhouetteOrientationHint",
            Text = "Each orientation keeps its own placement.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = new Padding(12, 0, 4, 0),
            AutoEllipsis = true,
            AccessibleRole = AccessibleRole.StaticText
        };
        layout.Controls.Add(hint, 2, 0);
        bar.Controls.Add(layout);
        return bar;
    }

    private Control BuildPreviewCard(SilhouetteLayoutGuide guide, out ToggleSwitch safeAreas)
    {
        var card = CreateCard("SilhouetteLayoutPreviewCard");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
        heading.Controls.Add(new Label
        {
            Text = "RESULT LAYOUT GUIDE",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(7.75f, FontStyle.Bold),
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        safeAreas = new ToggleSwitch
        {
            Name = "SilhouetteSafeAreasToggle",
            Text = "Safe areas",
            AccessibleName = "Show output safe areas",
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ClipCordTheme.SurfaceRaised,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Checked = false
        };
        heading.Controls.Add(safeAreas, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        var guideFrame = new RoundedPanel
        {
            Name = "SilhouetteLayoutGuideFrame",
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceChrome,
            BorderColor = ClipCordTheme.BorderStrong,
            CornerRadius = 12,
            Padding = new Padding(8),
            Margin = Padding.Empty
        };
        guideFrame.Controls.Add(guide);
        layout.Controls.Add(guideFrame, 0, 1);

        layout.Controls.Add(new Label
        {
            Name = "SilhouetteLayoutGuideDisclaimer",
            Text = "Layout guide only — the processed silhouette appears after a clip is captured.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(8f),
            Margin = Padding.Empty,
            AutoEllipsis = true,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildInspector()
    {
        var card = CreateCard("SilhouetteLayoutInspectorCard");
        var layout = new SilhouetteInspectorLayout
        {
            Name = "SilhouetteLayoutInspector",
            Dock = DockStyle.None,
            AutoSize = false,
            MinimumSize = new Size(0, 430),
            AutoScroll = false,
            ColumnCount = 1,
            RowCount = 12,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(CreateSectionLabel("PLACEMENT"), 0, 0);
        _orientationDescription = new Label
        {
            Name = "SilhouetteOrientationDescription",
            Dock = DockStyle.Fill,
            ForeColor = ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty,
            AutoEllipsis = true,
            AccessibleRole = AccessibleRole.StaticText
        };
        layout.Controls.Add(_orientationDescription, 0, 1);
        layout.Controls.Add(BuildPresetGrid(), 0, 2);
        layout.Controls.Add(CreateSectionLabel("POSITION & SIZE"), 0, 3);
        layout.Controls.Add(_sizeSlider, 0, 4);
        layout.Controls.Add(_horizontalSlider, 0, 5);
        layout.Controls.Add(_verticalSlider, 0, 6);
        layout.Controls.Add(BuildMirrorRow(), 0, 7);
        layout.Controls.Add(BuildOutlineRow(), 0, 8);
        layout.Controls.Add(_portraitModeSection, 0, 9);
        layout.Controls.Add(_portraitGameplaySection, 0, 10);
        layout.Controls.Add(_portraitFocusSection, 0, 11);
        _inspectorScrollHost = new BrandedScrollHost
        {
            Name = "SilhouetteInspectorScrollHost",
            AccessibleName = "Silhouette placement controls",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            BackColor = ClipCordTheme.SurfaceRaised,
            Content = layout
        };
        card.Controls.Add(_inspectorScrollHost);
        return card;
    }

    private Panel BuildPortraitModeSection()
    {
        var section = new Panel
        {
            Name = "PortraitLayoutModeSection",
            Height = 88,
            Dock = DockStyle.Top,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            AccessibleName = "Portrait gameplay layout",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var column = 0; column < 3; column++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new Label
        {
            Text = "PORTRAIT GAMEPLAY",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextTertiary,
            Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 3);
        var labels = new[]
        {
            (PortraitGameplayLayoutMode.Context, "Context", "Context layout, recommended"),
            (PortraitGameplayLayoutMode.FocusCrop, "Focus crop", "Focus crop layout"),
            (PortraitGameplayLayoutMode.Custom, "Custom", "Custom portrait layout")
        };
        for (var index = 0; index < labels.Length; index++)
        {
            var item = labels[index];
            var button = new OutlineButton
            {
                Name = $"Portrait{item.Item1}ModeButton",
                Text = item.Item2,
                AccessibleName = item.Item3,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = ClipCordTheme.InterfaceFont(8.25f),
                LeadingIcon = item.Item1 switch
                {
                    PortraitGameplayLayoutMode.Context => FigmaIconAsset.Landscape,
                    PortraitGameplayLayoutMode.FocusCrop => FigmaIconAsset.Crop,
                    _ => FigmaIconAsset.Move
                },
                SurfaceColor = ClipCordTheme.SurfaceControl,
                OutlineColor = ClipCordTheme.BorderDefault,
                Margin = new Padding(0, 1, index == 2 ? 0 : 6, 1)
            };
            var mode = item.Item1;
            button.Click += (_, _) => SetPortraitMode(mode);
            _portraitModeButtons.Add(mode, button);
            layout.Controls.Add(button, index, 1);
        }
        var recommendation = new Label
        {
            Name = "PortraitContextRecommendationLabel",
            Text = "Context keeps the complete 16:9 play visible.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(126, 218, 175),
            Font = ClipCordTheme.InterfaceFont(7.75f),
            Margin = Padding.Empty,
            AutoEllipsis = true,
            AccessibleRole = AccessibleRole.StaticText
        };
        layout.Controls.Add(recommendation, 0, 2);
        layout.SetColumnSpan(recommendation, 3);
        section.Controls.Add(layout);
        return section;
    }

    private Control BuildMirrorRow()
    {
        var row = new TableLayoutPanel
        {
            Name = "SilhouetteMirrorRow",
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AccessibleName = "Mirror camera setting",
            AccessibleRole = AccessibleRole.Grouping
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.Controls.Add(new FigmaIconControl
        {
            Name = "SilhouetteMirrorIcon",
            Asset = FigmaIconAsset.Mirror,
            IconColor = ClipCordTheme.TextSecondary,
            Size = new Size(18, 18),
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty
        }, 0, 0);
        row.Controls.Add(_mirrorToggle, 1, 0);
        return row;
    }

    private Panel BuildPortraitGameplaySection()
    {
        var section = new Panel
        {
            Name = "PortraitGameplayTransformSection",
            Height = 157,
            Dock = DockStyle.Top,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            AccessibleName = "Portrait gameplay frame settings",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 3; index++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(_gameplayWidthSlider, 0, 0);
        layout.Controls.Add(_gameplayCenterSlider, 0, 1);
        layout.Controls.Add(_gameplayTopSlider, 0, 2);
        section.Controls.Add(layout);
        return section;
    }

    private Panel BuildPortraitFocusSection()
    {
        var section = new Panel
        {
            Name = "PortraitFocusCropSection",
            Height = 108,
            Dock = DockStyle.Top,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            AccessibleName = "Portrait focus crop settings",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(_focusZoomSlider, 0, 0);
        layout.Controls.Add(_focusCenterSlider, 0, 1);
        section.Controls.Add(layout);
        return section;
    }

    private Control BuildPresetGrid()
    {
        var grid = new TableLayoutPanel
        {
            Name = "SilhouettePlacementPresetGrid",
            Dock = DockStyle.Top,
            Height = 78,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = new Padding(0, 0, 0, 8),
            Padding = Padding.Empty,
            AccessibleName = "Silhouette placement presets",
            AccessibleRole = AccessibleRole.Grouping
        };
        for (var index = 0; index < 3; index++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var presets = new[]
        {
            (SilhouettePlacementPreset.BottomLeft, "Bottom left"),
            (SilhouettePlacementPreset.RaisedBottomCenter, "Bottom center"),
            (SilhouettePlacementPreset.BottomRight, "Bottom right"),
            (SilhouettePlacementPreset.TopLeft, "Top left"),
            (SilhouettePlacementPreset.TopRight, "Top right")
        };
        for (var index = 0; index < presets.Length; index++)
        {
            var (preset, label) = presets[index];
            var button = new OutlineButton
            {
                Name = $"Silhouette{preset}PresetButton",
                Text = label,
                AccessibleName = $"Place silhouette {label.ToLowerInvariant()}",
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = ClipCordTheme.InterfaceFont(7.75f),
                SurfaceColor = ClipCordTheme.SurfaceControl,
                OutlineColor = ClipCordTheme.BorderDefault,
                Margin = new Padding(0, 0, index % 3 == 2 ? 0 : 5, 5)
            };
            button.Click += (_, _) => ApplyPlacementPreset(preset);
            _presetButtons.Add(preset, button);
            grid.Controls.Add(button, index % 3, index / 3);
        }
        return grid;
    }

    private Control BuildOutlineRow()
    {
        var row = new TableLayoutPanel
        {
            Name = "SilhouetteOutlineRow",
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = ClipCordTheme.SurfaceRaised,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AccessibleName = "Silhouette outline",
            AccessibleRole = AccessibleRole.Grouping
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        row.Controls.Add(new Label
        {
            Text = "Outline",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipCordTheme.TextPrimary,
            Font = ClipCordTheme.InterfaceFont(8.5f),
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        }, 0, 0);
        foreach (var (style, label, column) in new[]
                 {
                     (SilhouetteOutlineStyle.None, "None", 1),
                     (SilhouetteOutlineStyle.Soft, "Soft", 2),
                     (SilhouetteOutlineStyle.Strong, "Strong", 3)
                 })
        {
            var button = new OutlineButton
            {
                Name = $"Silhouette{style}OutlineButton",
                Text = label,
                AccessibleName = $"{label} silhouette outline",
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = ClipCordTheme.InterfaceFont(7.75f),
                SurfaceColor = ClipCordTheme.SurfaceControl,
                OutlineColor = ClipCordTheme.BorderDefault,
                Margin = new Padding(column == 1 ? 0 : 4, 4, 0, 4)
            };
            button.Click += (_, _) => SetOutline(style);
            _outlineButtons.Add(style, button);
            row.Controls.Add(button, column, 0);
        }
        return row;
    }

    private Control BuildFooter(
        SilhouettePreferencesLoadStatus loadStatus,
        out Label saveStatus,
        out GradientButton saveButton)
    {
        var footer = new RoundedPanel
        {
            Name = "SilhouetteLayoutFooter",
            BackColor = ClipCordTheme.SurfaceRaised,
            BorderColor = ClipCordTheme.BorderDefault,
            CornerRadius = 11,
            Padding = new Padding(14, 10, 14, 10),
            Margin = Padding.Empty,
            AccessibleName = "Save silhouette layout defaults",
            AccessibleRole = AccessibleRole.Grouping
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206));
        saveStatus = new Label
        {
            Name = "SilhouetteLayoutSaveStatusLabel",
            Text = GetLoadStatusCopy(loadStatus),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = loadStatus is SilhouettePreferencesLoadStatus.Corrupt or
                SilhouettePreferencesLoadStatus.Invalid or
                SilhouettePreferencesLoadStatus.UnsupportedSchema or
                SilhouettePreferencesLoadStatus.Unavailable
                ? Color.FromArgb(244, 178, 84)
                : ClipCordTheme.TextSecondary,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = Padding.Empty,
            AccessibleRole = AccessibleRole.StaticText
        };
        saveButton = new GradientButton
        {
            Name = "SilhouetteSaveDefaultLayoutButton",
            Text = "Save as my default layout",
            AccessibleName = "Save as my default silhouette layout",
            LeadingIcon = FigmaIconAsset.Disk,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Margin = Padding.Empty,
            Font = ClipCordTheme.InterfaceFont(9f, FontStyle.Bold),
            TabIndex = 40
        };
        var resetButton = new OutlineButton
        {
            Name = "SilhouetteResetCurrentLayoutButton",
            Text = "Reset current",
            AccessibleName = "Reset current orientation layout",
            LeadingIcon = FigmaIconAsset.Refresh,
            Dock = DockStyle.Fill,
            AutoSize = false,
            SurfaceColor = ClipCordTheme.SurfaceControl,
            OutlineColor = ClipCordTheme.BorderDefault,
            Font = ClipCordTheme.InterfaceFont(8.25f),
            Margin = new Padding(0, 0, 10, 0),
            TabIndex = 39
        };
        resetButton.Click += (_, _) => ResetCurrentOrientation();
        layout.Controls.Add(saveStatus, 0, 0);
        layout.Controls.Add(resetButton, 1, 0);
        layout.Controls.Add(saveButton, 2, 0);
        footer.Controls.Add(layout);
        return footer;
    }

    private void SyncControlsFromModel()
    {
        _syncingControls = true;
        try
        {
            var transform = _transforms[_selectedOrientationId];
            _sizeSlider.Value = transform.HeightFraction;
            _horizontalSlider.Value = transform.AnchorX;
            _verticalSlider.Minimum = transform.HeightFraction;
            _verticalSlider.Value = transform.AnchorY;
            _mirrorToggle.Checked = transform.MirrorHorizontally;
            foreach (var (preset, button) in _presetButtons)
            {
                var selected = preset == transform.Preset;
                button.AccessibilitySelected = selected;
                ApplySelectedButtonStyle(button, selected);
            }
            foreach (var (style, button) in _outlineButtons)
            {
                var selected = style == transform.Outline;
                button.AccessibilitySelected = selected;
                ApplySelectedButtonStyle(button, selected);
            }

            _gameplayWidthSlider.Value = _portraitComposition.Gameplay.WidthFraction;
            _gameplayCenterSlider.Minimum = _portraitComposition.Gameplay.WidthFraction / 2;
            _gameplayCenterSlider.Maximum = 1 - _portraitComposition.Gameplay.WidthFraction / 2;
            _gameplayCenterSlider.Value = _portraitComposition.Gameplay.CenterX;
            // WidthFraction is relative to the portrait canvas width. Convert the 16:9
            // frame height back to portrait-canvas height before deriving the legal top.
            var gameplayHeight = _portraitComposition.Gameplay.WidthFraction * 81d / 256d;
            _gameplayTopSlider.Maximum = Math.Max(0, 1 - gameplayHeight);
            _gameplayTopSlider.Value = _portraitComposition.Gameplay.TopY;
            _focusZoomSlider.Value = _portraitComposition.FocusCrop.Zoom;
            _focusCenterSlider.Value = _portraitComposition.FocusCrop.FocalCenterX;
            foreach (var (mode, button) in _portraitModeButtons)
            {
                var selected = mode == _portraitComposition.Layout;
                button.AccessibilitySelected = selected;
                ApplySelectedButtonStyle(button, selected);
            }
            _guide.OrientationId = _selectedOrientationId;
            _guide.Transform = transform;
            _guide.PortraitComposition = _portraitComposition;
            _guide.ShowSafeAreas = _safeAreasToggle.Checked;
        }
        finally
        {
            _syncingControls = false;
        }
        _guide.Invalidate();
    }

    private void TransformControlChanged()
    {
        if (_syncingControls) return;
        var current = _transforms[_selectedOrientationId];
        var updated = current with
        {
            AnchorX = _horizontalSlider.Value,
            AnchorY = _verticalSlider.Value,
            HeightFraction = _sizeSlider.Value,
            MirrorHorizontally = _mirrorToggle.Checked,
            Preset = SilhouettePlacementPreset.Custom
        };
        UpdateSelectedTransform(updated, markCustom: true);
        SyncControlsFromModel();
    }

    private void GuideTransformChanged(object? sender, SilhouetteTransformChangedEventArgs eventArgs)
    {
        if (_syncingControls) return;
        UpdateSelectedTransform(eventArgs.Transform, markCustom: true);
        SyncControlsFromModel();
    }

    private void UpdateSelectedTransform(SilhouetteTransform updated, bool markCustom)
    {
        var fallback = CaptureCompositionModel.CreateDefaultTransform(
            _selectedOrientationId,
            _mirrorCameraDefault);
        var normalized = CaptureCompositionModel.NormalizeTransform(
            markCustom ? updated with { Preset = SilhouettePlacementPreset.Custom } : updated,
            fallback);
        _transforms[_selectedOrientationId] = normalized;
        _guide.Transform = normalized;
        _guide.Invalidate();
        if (markCustom)
        {
            foreach (var button in _presetButtons.Values)
            {
                button.AccessibilitySelected = false;
                ApplySelectedButtonStyle(button, selected: false);
            }
        }
    }

    private void PortraitCompositionControlChanged()
    {
        if (_syncingControls) return;
        _portraitComposition = CaptureCompositionModel.NormalizePortraitComposition(
            new PortraitCompositionSettings(
                _portraitComposition.Layout,
                new PortraitGameplayTransform(
                    _gameplayWidthSlider.Value,
                    _gameplayCenterSlider.Value,
                    _gameplayTopSlider.Value),
                new PortraitFocusCrop(
                    _focusZoomSlider.Value,
                    _focusCenterSlider.Value)));
        SyncControlsFromModel();
    }

    private void SetPortraitMode(PortraitGameplayLayoutMode mode)
    {
        _portraitComposition = _portraitComposition with { Layout = mode };
        _portraitComposition = CaptureCompositionModel.NormalizePortraitComposition(_portraitComposition);
        UpdatePortraitControlVisibility();
        SyncControlsFromModel();
        _content.PerformLayout();
        _scrollHost.RefreshContentLayout();
    }

    private void UpdatePortraitControlVisibility()
    {
        var portrait = _selectedOrientationId.Equals(PortraitId, StringComparison.Ordinal);
        _portraitGameplaySection.Visible = portrait &&
            _portraitComposition.Layout is PortraitGameplayLayoutMode.Context or PortraitGameplayLayoutMode.Custom;
        _portraitFocusSection.Visible = portrait &&
            _portraitComposition.Layout == PortraitGameplayLayoutMode.FocusCrop;
        _inspectorScrollHost?.RefreshContentLayout();
    }

    private void ApplyPlacementPreset(SilhouettePlacementPreset preset)
    {
        var current = _transforms[_selectedOrientationId];
        var portrait = _selectedOrientationId.Equals(PortraitId, StringComparison.Ordinal);
        var (x, y) = preset switch
        {
            SilhouettePlacementPreset.BottomLeft => (portrait ? 0.26 : 0.24, portrait ? 0.92 : 0.96),
            SilhouettePlacementPreset.BottomRight => (portrait ? 0.74 : 0.76, portrait ? 0.92 : 0.96),
            SilhouettePlacementPreset.RaisedBottomCenter => (0.50, portrait ? 0.745 : 0.88),
            SilhouettePlacementPreset.TopLeft => (portrait ? 0.28 : 0.24, 0.48),
            SilhouettePlacementPreset.TopRight => (portrait ? 0.72 : 0.76, 0.48),
            _ => (current.AnchorX, current.AnchorY)
        };
        var updated = current with { AnchorX = x, AnchorY = y, Preset = preset };
        UpdateSelectedTransform(updated, markCustom: false);
        SyncControlsFromModel();
    }

    private void SetOutline(SilhouetteOutlineStyle style)
    {
        var current = _transforms[_selectedOrientationId];
        UpdateSelectedTransform(current with { Outline = style }, markCustom: false);
        SyncControlsFromModel();
    }

    private void ResetCurrentOrientation()
    {
        _transforms[_selectedOrientationId] = CaptureCompositionModel.CreateDefaultTransform(
            _selectedOrientationId,
            _mirrorCameraDefault);
        if (_selectedOrientationId.Equals(PortraitId, StringComparison.Ordinal))
        {
            _portraitComposition = CaptureCompositionModel.CreateDefaultPortraitComposition();
            UpdatePortraitControlVisibility();
        }
        SyncControlsFromModel();
    }

    private SilhouettePreferencesDocument CreateCurrentDocument(DateTimeOffset modifiedUtc)
    {
        var document = new SilhouettePreferencesDocument(
            SilhouettePreferencesStore.CurrentSchemaVersion,
            CompositionOrientationIds.All
                .Select(orientationId => new SilhouetteLayoutPreference(
                    orientationId,
                    _transforms[orientationId]))
                .ToArray(),
            _portraitComposition,
            modifiedUtc.ToUniversalTime());
        return SilhouettePreferencesModel.Normalize(document, _mirrorCameraDefault);
    }

    private static RoundedPanel CreateCard(string name) => new()
    {
        Name = name,
        BackColor = ClipCordTheme.SurfaceRaised,
        BorderColor = ClipCordTheme.BorderDefault,
        CornerRadius = 14,
        Padding = new Padding(16),
        Margin = Padding.Empty,
        AccessibleRole = AccessibleRole.Grouping
    };

    private static Label CreateSectionLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = ClipCordTheme.TextTertiary,
        Font = ClipCordTheme.InterfaceFont(7.5f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty,
        UseMnemonic = false,
        AccessibleRole = AccessibleRole.StaticText
    };

    private static OutlineButton CreateSegmentButton(
        string name,
        string text,
        string accessibleName,
        FigmaIconAsset icon) => new()
    {
        Name = name,
        Text = text,
        AccessibleName = accessibleName,
        Dock = DockStyle.Fill,
        AutoSize = false,
        Font = ClipCordTheme.InterfaceFont(8.5f, FontStyle.Bold),
        SurfaceColor = ClipCordTheme.SurfaceControl,
        OutlineColor = ClipCordTheme.BorderDefault,
        LeadingIcon = icon,
        Margin = Padding.Empty
    };

    private static SilhouetteValueSlider CreateSlider(
        string name,
        string label,
        double minimum,
        double maximum,
        double step,
        Func<double, string> formatter) => new()
    {
        Name = name,
        Label = label,
        AccessibleName = label,
        Minimum = minimum,
        Maximum = maximum,
        Step = step,
        ValueFormatter = formatter,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        BackColor = ClipCordTheme.SurfaceRaised
    };

    private static void ApplySelectedButtonStyle(OutlineButton button, bool selected)
    {
        button.SurfaceColor = selected ? ClipCordTheme.VioletMuted : ClipCordTheme.SurfaceControl;
        button.OutlineColor = selected ? ClipCordTheme.Violet : ClipCordTheme.BorderDefault;
        button.ForeColor = selected ? Color.FromArgb(226, 215, 255) : ClipCordTheme.TextPrimary;
        button.Invalidate();
    }

    private static string GetLoadStatusCopy(SilhouettePreferencesLoadStatus status) => status switch
    {
        SilhouettePreferencesLoadStatus.Loaded =>
            "Changes affect future captures; existing projects remain unchanged.",
        SilhouettePreferencesLoadStatus.Missing =>
            "Using ClipCord defaults. Save when this placement feels right.",
        SilhouettePreferencesLoadStatus.Corrupt =>
            "Saved layout could not be read; safe defaults are shown.",
        SilhouettePreferencesLoadStatus.UnsupportedSchema =>
            "Saved layout belongs to another ClipCord version; safe defaults are shown.",
        SilhouettePreferencesLoadStatus.Invalid =>
            "Saved layout was invalid; safe defaults are shown.",
        _ => "Saved layout is unavailable; safe defaults are shown."
    };
}

internal sealed class SilhouetteTransformChangedEventArgs(SilhouetteTransform transform) : EventArgs
{
    internal SilhouetteTransform Transform { get; } = transform;
}

/// <summary>
/// Draws a synthetic composition guide and edits only normalized geometry. It never opens a
/// camera, decodes a clip, runs background removal, or implies that a rendered preview exists.
/// </summary>
internal sealed class SilhouetteLayoutGuide : Control
{
    private const double LayerAspectRatio = 0.55;
    private string _orientationId = CompositionOrientationIds.Landscape;
    private SilhouetteTransform _transform = CaptureCompositionModel.CreateDefaultTransform(
        CompositionOrientationIds.Landscape,
        mirrorCamera: false);
    private PortraitCompositionSettings _portraitComposition =
        CaptureCompositionModel.CreateDefaultPortraitComposition();
    private bool _showSafeAreas;
    private bool _dragging;
    private bool _resizing;
    private Point _dragStart;
    private SilhouetteTransform? _dragStartTransform;

    internal event EventHandler<SilhouetteTransformChangedEventArgs>? TransformChanged;

    internal string OrientationId
    {
        get => _orientationId;
        set
        {
            if (_orientationId.Equals(value, StringComparison.Ordinal)) return;
            _orientationId = value;
            Invalidate();
        }
    }

    internal SilhouetteTransform Transform
    {
        get => _transform;
        set
        {
            _transform = value ?? throw new ArgumentNullException(nameof(value));
            Invalidate();
        }
    }

    internal PortraitCompositionSettings PortraitComposition
    {
        get => _portraitComposition;
        set
        {
            _portraitComposition = value ?? throw new ArgumentNullException(nameof(value));
            Invalidate();
        }
    }

    internal bool ShowSafeAreas
    {
        get => _showSafeAreas;
        set
        {
            if (_showSafeAreas == value) return;
            _showSafeAreas = value;
            Invalidate();
        }
    }

    internal SilhouetteLayoutGuide()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        Cursor = Cursors.SizeAll;
        BackColor = ClipCordTheme.SurfaceChrome;
        ForeColor = ClipCordTheme.TextPrimary;
        AccessibleRole = AccessibleRole.Graphic;
        SetStyle(ControlStyles.Selectable, true);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or
            Keys.Add or Keys.Subtract or Keys.Oemplus or Keys.OemMinus ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        var movement = eventArgs.Shift ? 0.05 : 0.01;
        var updated = _transform;
        switch (eventArgs.KeyCode)
        {
            case Keys.Left:
                updated = updated with { AnchorX = updated.AnchorX - movement, Preset = SilhouettePlacementPreset.Custom };
                break;
            case Keys.Right:
                updated = updated with { AnchorX = updated.AnchorX + movement, Preset = SilhouettePlacementPreset.Custom };
                break;
            case Keys.Up:
                updated = updated with { AnchorY = updated.AnchorY - movement, Preset = SilhouettePlacementPreset.Custom };
                break;
            case Keys.Down:
                updated = updated with { AnchorY = updated.AnchorY + movement, Preset = SilhouettePlacementPreset.Custom };
                break;
            case Keys.Add:
            case Keys.Oemplus:
                updated = updated with { HeightFraction = updated.HeightFraction + movement, Preset = SilhouettePlacementPreset.Custom };
                break;
            case Keys.Subtract:
            case Keys.OemMinus:
                updated = updated with { HeightFraction = updated.HeightFraction - movement, Preset = SilhouettePlacementPreset.Custom };
                break;
            default:
                base.OnKeyDown(eventArgs);
                return;
        }
        CommitTransform(updated);
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        base.OnKeyDown(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            Focus();
            var canvas = GetCanvasBounds();
            var silhouette = GetSilhouetteBounds(canvas);
            var handle = GetResizeHandle(silhouette);
            if (handle.Contains(eventArgs.Location))
            {
                _resizing = true;
            }
            else if (silhouette.Contains(eventArgs.Location))
            {
                _dragging = true;
            }
            if (_dragging || _resizing)
            {
                _dragStart = eventArgs.Location;
                _dragStartTransform = _transform;
                Capture = true;
            }
        }
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if ((_dragging || _resizing) && _dragStartTransform is not null)
        {
            var canvas = GetCanvasBounds();
            if (canvas.Width > 0 && canvas.Height > 0)
            {
                var dx = (eventArgs.X - _dragStart.X) / (double)canvas.Width;
                var dy = (eventArgs.Y - _dragStart.Y) / (double)canvas.Height;
                var updated = _resizing
                    ? _dragStartTransform with
                    {
                        HeightFraction = _dragStartTransform.HeightFraction - dy,
                        Preset = SilhouettePlacementPreset.Custom
                    }
                    : _dragStartTransform with
                    {
                        AnchorX = _dragStartTransform.AnchorX + dx,
                        AnchorY = _dragStartTransform.AnchorY + dy,
                        Preset = SilhouettePlacementPreset.Custom
                    };
                CommitTransform(updated);
            }
        }
        else
        {
            var silhouette = GetSilhouetteBounds(GetCanvasBounds());
            Cursor = GetResizeHandle(silhouette).Contains(eventArgs.Location)
                ? Cursors.SizeNESW
                : silhouette.Contains(eventArgs.Location) ? Cursors.SizeAll : Cursors.Default;
        }
        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left && (_dragging || _resizing))
        {
            _dragging = false;
            _resizing = false;
            _dragStartTransform = null;
            Capture = false;
        }
        base.OnMouseUp(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(ClipCordTheme.SurfaceChrome);
        var canvas = GetCanvasBounds();
        if (canvas.Width <= 2 || canvas.Height <= 2) return;

        using (var path = RoundedPanel.CreateRoundedPath(canvas, Math.Max(8, ScaleLogical(10))))
        {
            graphics.SetClip(path);
            if (_orientationId.Equals(CompositionOrientationIds.Portrait, StringComparison.Ordinal))
            {
                DrawPortraitCanvas(graphics, canvas);
            }
            else
            {
                DrawGameplay(graphics, canvas, dimmed: false);
            }
            graphics.ResetClip();
            using var border = new Pen(ClipCordTheme.BorderStrong, Math.Max(1f, DeviceDpi / 96f));
            graphics.DrawPath(border, path);
        }

        if (_showSafeAreas) DrawSafeAreas(graphics, canvas);
        DrawSilhouette(graphics, GetSilhouetteBounds(canvas));
        DrawGuideBadge(graphics, canvas);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(canvas, -3, -3));
        }
    }

    private void DrawPortraitCanvas(Graphics graphics, Rectangle canvas)
    {
        DrawGameplay(graphics, canvas, dimmed: true);
        using (var veil = new SolidBrush(Color.FromArgb(92, 6, 11, 22))) graphics.FillRectangle(veil, canvas);

        if (_portraitComposition.Layout == PortraitGameplayLayoutMode.FocusCrop)
        {
            // Fill the 9:16 result with a fixed focal crop from the 16:9 source. Zoom
            // enlarges the source around the saved horizontal focal point; this remains a
            // deterministic guide and intentionally performs no action tracking.
            var zoom = _portraitComposition.FocusCrop.Zoom;
            var sourceHeight = Math.Max(1, (int)Math.Round(canvas.Height * zoom));
            var sourceWidth = Math.Max(1, (int)Math.Round(sourceHeight * 16d / 9d));
            var sourceLeft = canvas.Left + canvas.Width / 2 -
                             (int)Math.Round(sourceWidth * _portraitComposition.FocusCrop.FocalCenterX);
            var sourceTop = canvas.Top + (canvas.Height - sourceHeight) / 2;
            DrawGameplay(
                graphics,
                new Rectangle(sourceLeft, sourceTop, sourceWidth, sourceHeight),
                dimmed: false);
            using var focusPen = new Pen(Color.FromArgb(185, 205, 190, 255), Math.Max(1f, ScaleLogical(1)))
            {
                DashStyle = DashStyle.Dash
            };
            graphics.DrawLine(
                focusPen,
                canvas.Left + canvas.Width / 2,
                canvas.Top,
                canvas.Left + canvas.Width / 2,
                canvas.Bottom);
            return;
        }

        var gameplay = GetPortraitGameplayBounds(canvas);
        DrawGameplay(graphics, gameplay, dimmed: false);
        using var gameplayBorder = new Pen(Color.FromArgb(124, 151, 180), Math.Max(1f, DeviceDpi / 96f));
        graphics.DrawRectangle(gameplayBorder, gameplay);
    }

    private static void DrawGameplay(Graphics graphics, Rectangle bounds, bool dimmed)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var top = dimmed ? Color.FromArgb(25, 33, 58) : Color.FromArgb(35, 56, 83);
        var bottom = dimmed ? Color.FromArgb(21, 18, 45) : Color.FromArgb(41, 25, 65);
        using var fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
        graphics.FillRectangle(fill, bounds);

        using var horizon = new SolidBrush(dimmed
            ? Color.FromArgb(48, 52, 82)
            : Color.FromArgb(75, 88, 118));
        var horizonY = bounds.Top + bounds.Height * 43 / 100;
        graphics.FillPolygon(horizon,
        new Point[]
        {
            new Point(bounds.Left, horizonY),
            new Point(bounds.Left + bounds.Width * 22 / 100, horizonY - bounds.Height * 18 / 100),
            new Point(bounds.Left + bounds.Width * 44 / 100, horizonY + bounds.Height * 2 / 100),
            new Point(bounds.Left + bounds.Width * 72 / 100, horizonY - bounds.Height * 23 / 100),
            new Point(bounds.Right, horizonY - bounds.Height * 4 / 100),
            new Point(bounds.Right, bounds.Bottom),
            new Point(bounds.Left, bounds.Bottom)
        });
        using var ground = new SolidBrush(dimmed
            ? Color.FromArgb(37, 28, 56)
            : Color.FromArgb(58, 43, 73));
        graphics.FillRectangle(ground, bounds.Left, horizonY, bounds.Width, bounds.Bottom - horizonY);

        using var hud = new Pen(dimmed
            ? Color.FromArgb(82, 104, 125)
            : Color.FromArgb(147, 209, 212),
            Math.Max(1f, bounds.Width / 420f));
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var tick = Math.Max(4, bounds.Width / 55);
        graphics.DrawLine(hud, center.X - tick, center.Y, center.X - 2, center.Y);
        graphics.DrawLine(hud, center.X + 2, center.Y, center.X + tick, center.Y);
        graphics.DrawLine(hud, center.X, center.Y - tick, center.X, center.Y - 2);
        graphics.DrawLine(hud, center.X, center.Y + 2, center.X, center.Y + tick);
        graphics.DrawRectangle(
            hud,
            bounds.Left + bounds.Width / 25,
            bounds.Bottom - Math.Max(10, bounds.Height / 9),
            Math.Max(18, bounds.Width / 6),
            Math.Max(5, bounds.Height / 28));
    }

    private void DrawSilhouette(Graphics graphics, Rectangle bounds)
    {
        if (bounds.Width <= 2 || bounds.Height <= 2) return;
        var color = Color.FromArgb(223, 165, 139, 255);
        var outlineWidth = _transform.Outline switch
        {
            SilhouetteOutlineStyle.None => 0f,
            SilhouetteOutlineStyle.Soft => Math.Max(1.5f, bounds.Height / 55f),
            _ => Math.Max(2.5f, bounds.Height / 36f)
        };
        using var bodyPath = new GraphicsPath();
        var headSize = Math.Max(6, bounds.Width * 42 / 100);
        var head = new Rectangle(
            bounds.Left + (bounds.Width - headSize) / 2,
            bounds.Top,
            headSize,
            headSize);
        bodyPath.AddEllipse(head);
        bodyPath.AddBezier(
            new Point(bounds.Left + bounds.Width * 47 / 100, head.Bottom - 2),
            new Point(bounds.Left + bounds.Width * 12 / 100, bounds.Top + bounds.Height * 39 / 100),
            new Point(bounds.Left + bounds.Width * 4 / 100, bounds.Bottom - bounds.Height * 10 / 100),
            new Point(bounds.Left, bounds.Bottom));
        bodyPath.AddLine(bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        bodyPath.AddBezier(
            new Point(bounds.Right, bounds.Bottom),
            new Point(bounds.Right - bounds.Width * 4 / 100, bounds.Bottom - bounds.Height * 10 / 100),
            new Point(bounds.Right - bounds.Width * 12 / 100, bounds.Top + bounds.Height * 39 / 100),
            new Point(bounds.Left + bounds.Width * 53 / 100, head.Bottom - 2));
        bodyPath.CloseFigure();
        if (outlineWidth > 0)
        {
            using var outline = new Pen(
                _transform.Outline == SilhouetteOutlineStyle.Strong
                    ? Color.FromArgb(230, 240, 233, 255)
                    : Color.FromArgb(185, 212, 198, 255),
                outlineWidth)
            {
                LineJoin = LineJoin.Round
            };
            graphics.DrawPath(outline, bodyPath);
        }
        using var body = new SolidBrush(color);
        graphics.FillPath(body, bodyPath);
        using var inner = new Pen(Color.FromArgb(155, 231, 221, 255), Math.Max(1f, bounds.Height / 100f));
        graphics.DrawPath(inner, bodyPath);
        var faceDot = Math.Max(2, headSize / 10);
        var faceX = _transform.MirrorHorizontally
            ? head.Left + head.Width * 31 / 100
            : head.Right - head.Width * 31 / 100 - faceDot;
        using var faceBrush = new SolidBrush(Color.FromArgb(225, 241, 235, 255));
        graphics.FillEllipse(
            faceBrush,
            faceX,
            head.Top + head.Height * 42 / 100,
            faceDot,
            faceDot);

        var handle = GetResizeHandle(bounds);
        using var handleFill = new SolidBrush(ClipCordTheme.Violet);
        using var handleBorder = new Pen(Color.White, Math.Max(1f, DeviceDpi / 96f));
        graphics.FillEllipse(handleFill, handle);
        graphics.DrawEllipse(handleBorder, handle);
    }

    private void DrawSafeAreas(Graphics graphics, Rectangle canvas)
    {
        if (!_orientationId.Equals(CompositionOrientationIds.Portrait, StringComparison.Ordinal))
        {
            return;
        }
        var safe = GetPortraitSafeArea(canvas);
        using var pen = new Pen(Color.FromArgb(205, 223, 206, 255), Math.Max(1f, DeviceDpi / 96f))
        {
            DashStyle = DashStyle.Dash
        };
        graphics.DrawRectangle(pen, safe);
        using var labelFont = ClipCordTheme.InterfaceFont(6.75f, FontStyle.Bold);
        TextRenderer.DrawText(
            graphics,
            "SAFE AREA",
            labelFont,
            new Point(safe.Left + 4, safe.Top + 3),
            Color.FromArgb(218, 223, 206, 255),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    internal static Rectangle GetPortraitSafeArea(Rectangle canvas) => Rectangle.FromLTRB(
        canvas.Left + (int)Math.Round(canvas.Width * (60d / 1080d)),
        canvas.Top + (int)Math.Round(canvas.Height * (180d / 1920d)),
        canvas.Right - (int)Math.Round(canvas.Width * (140d / 1080d)),
        canvas.Bottom - (int)Math.Round(canvas.Height * (484d / 1920d)));

    private void DrawGuideBadge(Graphics graphics, Rectangle canvas)
    {
        var badge = new Rectangle(
            canvas.Left + ScaleLogical(10),
            canvas.Top + ScaleLogical(10),
            ScaleLogical(94),
            ScaleLogical(23));
        using var path = RoundedPanel.CreateRoundedPath(badge, ScaleLogical(8));
        using var fill = new SolidBrush(Color.FromArgb(205, 12, 20, 36));
        graphics.FillPath(fill, path);
        using var font = ClipCordTheme.InterfaceFont(6.75f, FontStyle.Bold);
        TextRenderer.DrawText(
            graphics,
            "LAYOUT GUIDE",
            font,
            badge,
            ClipCordTheme.TextSecondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private void CommitTransform(SilhouetteTransform transform)
    {
        var canvasAspect = _orientationId.Equals(CompositionOrientationIds.Portrait, StringComparison.Ordinal)
            ? 9d / 16d
            : 16d / 9d;
        var fitted = CaptureCompositionModel.FitToCanvas(
            CaptureCompositionModel.NormalizeTransform(
                transform,
                CaptureCompositionModel.CreateDefaultTransform(_orientationId, transform.MirrorHorizontally)),
            LayerAspectRatio,
            canvasAspect).Transform;
        _transform = fitted with { Preset = SilhouettePlacementPreset.Custom };
        Invalidate();
        TransformChanged?.Invoke(this, new SilhouetteTransformChangedEventArgs(_transform));
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    private Rectangle GetCanvasBounds()
    {
        var available = Rectangle.Inflate(ClientRectangle, -ScaleLogical(12), -ScaleLogical(12));
        if (available.Width <= 0 || available.Height <= 0) return Rectangle.Empty;
        var aspect = _orientationId.Equals(CompositionOrientationIds.Portrait, StringComparison.Ordinal)
            ? 9d / 16d
            : 16d / 9d;
        var width = available.Width;
        var height = (int)Math.Round(width / aspect);
        if (height > available.Height)
        {
            height = available.Height;
            width = (int)Math.Round(height * aspect);
        }
        return new Rectangle(
            available.Left + (available.Width - width) / 2,
            available.Top + (available.Height - height) / 2,
            Math.Max(1, width),
            Math.Max(1, height));
    }

    private Rectangle GetSilhouetteBounds(Rectangle canvas)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0) return Rectangle.Empty;
        var canvasAspect = canvas.Width / (double)canvas.Height;
        var fit = CaptureCompositionModel.FitToCanvas(_transform, LayerAspectRatio, canvasAspect).Bounds;
        return new Rectangle(
            canvas.Left + (int)Math.Round(fit.Left * canvas.Width),
            canvas.Top + (int)Math.Round(fit.Top * canvas.Height),
            Math.Max(1, (int)Math.Round(fit.Width * canvas.Width)),
            Math.Max(1, (int)Math.Round(fit.Height * canvas.Height)));
    }

    private Rectangle GetPortraitGameplayBounds(Rectangle canvas)
    {
        var gameplay = _portraitComposition.Gameplay;
        var width = (int)Math.Round(canvas.Width * gameplay.WidthFraction);
        var height = (int)Math.Round(width * 9d / 16d);
        var centerX = canvas.Left + (int)Math.Round(canvas.Width * gameplay.CenterX);
        return new Rectangle(
            centerX - width / 2,
            canvas.Top + (int)Math.Round(canvas.Height * gameplay.TopY),
            Math.Max(1, width),
            Math.Max(1, height));
    }

    private Rectangle GetResizeHandle(Rectangle silhouette)
    {
        var size = ScaleLogical(12);
        return new Rectangle(
            silhouette.Right - size / 2,
            silhouette.Top - size / 2,
            size,
            size);
    }

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));
}

internal sealed class SilhouetteValueSlider : Control
{
    private double _minimum;
    private double _maximum = 1;
    private double _value;
    private double _step = 0.01;
    private bool _dragging;
    private bool _hovered;

    internal event EventHandler? ValueChanged;

    internal string Label { get; set; } = string.Empty;
    internal Func<double, string> ValueFormatter { get; set; } = value => value.ToString("0.00");

    internal double Minimum
    {
        get => _minimum;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _minimum = value;
            if (_maximum < _minimum) _maximum = _minimum;
            Value = _value;
            Invalidate();
        }
    }

    internal double Maximum
    {
        get => _maximum;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _maximum = Math.Max(value, _minimum);
            Value = _value;
            Invalidate();
        }
    }

    internal double Step
    {
        get => _step;
        set => _step = double.IsFinite(value) && value > 0 ? value : 0.01;
    }

    internal double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(double.IsFinite(value) ? value : _minimum, _minimum, _maximum);
            if (Math.Abs(_value - clamped) < 0.0000001) return;
            _value = clamped;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal SilhouetteValueSlider()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        Height = 48;
        Font = ClipCordTheme.InterfaceFont(8.25f);
        ForeColor = ClipCordTheme.TextPrimary;
        BackColor = ClipCordTheme.SurfaceRaised;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        SetStyle(ControlStyles.Selectable, true);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End ||
        base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        switch (eventArgs.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                Value -= eventArgs.Shift ? Step * 5 : Step;
                break;
            case Keys.Right:
            case Keys.Up:
                Value += eventArgs.Shift ? Step * 5 : Step;
                break;
            case Keys.Home:
                Value = Minimum;
                break;
            case Keys.End:
                Value = Maximum;
                break;
            default:
                base.OnKeyDown(eventArgs);
                return;
        }
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
        base.OnKeyDown(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            Focus();
            _dragging = true;
            Capture = true;
            SetValueFromX(eventArgs.X);
        }
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_dragging) SetValueFromX(eventArgs.X);
        else if (!_hovered)
        {
            _hovered = true;
            Invalidate();
        }
        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left && _dragging)
        {
            _dragging = false;
            Capture = false;
        }
        base.OnMouseUp(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        if (!_dragging && _hovered)
        {
            _hovered = false;
            Invalidate();
        }
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(BackColor);
        graphics.FillRectangle(background, ClientRectangle);
        var valueText = ValueFormatter(Value);
        TextRenderer.DrawText(
            graphics,
            Label,
            Font,
            new Rectangle(0, 0, Math.Max(1, Width - 66), 20),
            ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(
            graphics,
            valueText,
            ClipCordTheme.InterfaceFont(8f, FontStyle.Bold),
            new Rectangle(Math.Max(0, Width - 64), 0, 64, 20),
            ClipCordTheme.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix);
        var track = GetTrackBounds();
        using var trackPath = RoundedPanel.CreateRoundedPath(track, track.Height / 2);
        using var trackFill = new SolidBrush(ClipCordTheme.BorderStrong);
        graphics.FillPath(trackFill, trackPath);
        var fraction = Maximum <= Minimum ? 0 : (Value - Minimum) / (Maximum - Minimum);
        var activeWidth = Math.Max(track.Height, (int)Math.Round(track.Width * fraction));
        var active = new Rectangle(track.Left, track.Top, Math.Min(track.Width, activeWidth), track.Height);
        using var activePath = RoundedPanel.CreateRoundedPath(active, active.Height / 2);
        using var activeFill = new SolidBrush(ClipCordTheme.Violet);
        graphics.FillPath(activeFill, activePath);
        var thumbSize = ScaleLogical(_dragging || _hovered || Focused ? 14 : 12);
        var thumbCenterX = track.Left + (int)Math.Round(track.Width * fraction);
        var thumb = new Rectangle(
            thumbCenterX - thumbSize / 2,
            track.Top + track.Height / 2 - thumbSize / 2,
            thumbSize,
            thumbSize);
        using var thumbFill = new SolidBrush(Color.White);
        using var thumbBorder = new Pen(ClipCordTheme.Violet, Math.Max(1f, DeviceDpi / 96f));
        graphics.FillEllipse(thumbFill, thumb);
        graphics.DrawEllipse(thumbBorder, thumb);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() =>
        new SliderAccessibleObject(this);

    private void SetValueFromX(int x)
    {
        var track = GetTrackBounds();
        var fraction = Math.Clamp((x - track.Left) / (double)Math.Max(1, track.Width), 0, 1);
        var raw = Minimum + fraction * (Maximum - Minimum);
        var stepped = Minimum + Math.Round((raw - Minimum) / Step) * Step;
        Value = stepped;
    }

    private Rectangle GetTrackBounds() => new(
        ScaleLogical(3),
        Height - ScaleLogical(14),
        Math.Max(1, Width - ScaleLogical(6)),
        ScaleLogical(5));

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));

    private sealed class SliderAccessibleObject(SilhouetteValueSlider owner)
        : Control.ControlAccessibleObject(owner)
    {
        public override string? Value => owner.ValueFormatter(owner.Value);
        public override string? Description =>
            $"Minimum {owner.ValueFormatter(owner.Minimum)}, maximum {owner.ValueFormatter(owner.Maximum)}.";
    }
}

internal sealed class SilhouetteBackButton : Button
{
    private bool _hovered;
    private Size _lastRegionSize = Size.Empty;

    internal SilhouetteBackButton()
    {
        AutoSize = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = ClipCordTheme.SurfaceControl;
        ForeColor = ClipCordTheme.TextPrimary;
        Font = ClipCordTheme.InterfaceFont(8.75f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        DoubleBuffered = true;
        MouseEnter += (_, _) =>
        {
            _hovered = true;
            Invalidate();
        };
        MouseLeave += (_, _) =>
        {
            _hovered = false;
            Invalidate();
        };
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
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, ScaleLogical(8));
        using var fill = new SolidBrush(_hovered
            ? ClipCordTheme.SurfaceControlHover
            : ClipCordTheme.SurfaceControl);
        using var border = new Pen(ClipCordTheme.BorderDefault, Math.Max(1f, DeviceDpi / 96f));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        var iconSize = ScaleLogical(16);
        var icon = new Rectangle(
            ScaleLogical(12),
            (Height - iconSize) / 2,
            iconSize,
            iconSize);
        FigmaIconRenderer.Draw(graphics, icon, FigmaIconAsset.ChevronLeft, ForeColor);
        var textBounds = new Rectangle(
            icon.Right + ScaleLogical(7),
            0,
            Math.Max(1, Width - icon.Right - ScaleLogical(13)),
            Height);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textBounds,
            Enabled ? ForeColor : ClipCordTheme.TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(ClientRectangle, ScaleLogical(8));
        Region?.Dispose();
        Region = new Region(path);
    }

    private int ScaleLogical(int value) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(96, DeviceDpi) / 96d));
}

internal sealed class SilhouetteInspectorLayout : TableLayoutPanel
{
    internal SilhouetteInspectorLayout()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(1, proposedSize.Width);
        var height = Padding.Vertical;
        for (var row = 0; row < RowCount; row++)
        {
            var style = row < RowStyles.Count ? RowStyles[row] : null;
            if (style is { SizeType: SizeType.Absolute })
            {
                height += Math.Max(0, (int)Math.Ceiling(style.Height));
                continue;
            }

            var rowHeight = 0;
            foreach (Control control in Controls)
            {
                if (!control.Visible || GetRow(control) != row) continue;
                rowHeight = Math.Max(
                    rowHeight,
                    Math.Max(control.MinimumSize.Height, control.Height) + control.Margin.Vertical);
            }
            height += rowHeight;
        }
        return new Size(width, Math.Max(MinimumSize.Height, height));
    }
}

internal sealed class SilhouetteEditorContentLayout : Panel
{
    private const int LogicalHorizontalPadding = 20;
    private const int LogicalTopPadding = 18;
    private const int LogicalBottomPadding = 18;
    private const int LogicalGap = 12;
    private readonly Control _header;
    private readonly Control _orientationBar;
    private readonly Control _preview;
    private readonly Control _inspector;
    private readonly Control _footer;
    private float _syntheticScale = 1f;
    private bool _embeddedHeaderVisible = true;

    internal bool EmbeddedHeaderVisible
    {
        get => _embeddedHeaderVisible;
        set
        {
            if (_embeddedHeaderVisible == value) return;
            _embeddedHeaderVisible = value;
            _header.Visible = value;
            PerformLayout();
            if (Parent is BrandedScrollHost scrollHost) scrollHost.RefreshContentLayout();
        }
    }

    internal SilhouetteEditorContentLayout(
        Control header,
        Control orientationBar,
        Control preview,
        Control inspector,
        Control footer)
    {
        _header = header;
        _orientationBar = orientationBar;
        _preview = preview;
        _inspector = inspector;
        _footer = footer;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        Controls.AddRange([header, orientationBar, preview, inspector, footer]);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(1, proposedSize.Width);
        return new Size(width, MeasureHeight(width));
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        var padding = ScaleLogical(LogicalHorizontalPadding);
        var top = ScaleLogical(LogicalTopPadding);
        var gap = ScaleLogical(LogicalGap);
        var innerWidth = Math.Max(1, ClientSize.Width - padding * 2);
        if (_embeddedHeaderVisible)
        {
            _header.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(57));
            top = _header.Bottom + gap;
        }
        _orientationBar.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(48));
        top = _orientationBar.Bottom + gap;

        if (innerWidth >= ScaleLogical(820))
        {
            var inspectorWidth = Math.Clamp(
                (int)Math.Round(innerWidth * 0.36),
                ScaleLogical(320),
                ScaleLogical(390));
            var previewWidth = Math.Max(1, innerWidth - inspectorWidth - gap);
            var bodyHeight = ScaleLogical(530);
            _preview.Bounds = new Rectangle(padding, top, previewWidth, bodyHeight);
            _inspector.Bounds = new Rectangle(padding + previewWidth + gap, top, inspectorWidth, bodyHeight);
            top += bodyHeight + gap;
        }
        else
        {
            var previewHeight = ScaleLogical(innerWidth >= ScaleLogical(560) ? 470 : 390);
            _preview.Bounds = new Rectangle(padding, top, innerWidth, previewHeight);
            top = _preview.Bottom + gap;
            _inspector.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(610));
            top = _inspector.Bottom + gap;
        }
        _footer.Bounds = new Rectangle(padding, top, innerWidth, ScaleLogical(58));
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        _syntheticScale = 1f;
        PerformLayout();
        if (Parent is BrandedScrollHost scrollHost) scrollHost.RefreshContentLayout();
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        if (factor.Height > 0 && float.IsFinite(factor.Height))
        {
            _syntheticScale = Math.Max(1f, factor.Height);
        }
        base.ScaleControl(factor, specified);
        PerformLayout();
    }

    private int MeasureHeight(int width)
    {
        var padding = ScaleLogical(LogicalHorizontalPadding);
        var gap = ScaleLogical(LogicalGap);
        var innerWidth = Math.Max(1, width - padding * 2);
        var headerHeight = _embeddedHeaderVisible ? ScaleLogical(57) + gap : 0;
        var common = ScaleLogical(LogicalTopPadding + LogicalBottomPadding + 48 + 58) +
                     gap * 2 + headerHeight;
        return innerWidth >= ScaleLogical(820)
            ? common + ScaleLogical(530)
            : common + ScaleLogical(innerWidth >= ScaleLogical(560) ? 470 + 610 : 390 + 610) + gap;
    }

    private int ScaleLogical(int value)
    {
        var scale = Math.Max(Math.Max(1f, DeviceDpi / 96f), _syntheticScale);
        return Math.Max(1, (int)Math.Round(value * scale));
    }
}
