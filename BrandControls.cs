using System.Collections.Concurrent;
using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

internal static class ClipCordTheme
{
    public static readonly Color Shell = Color.FromArgb(15, 23, 38);
    public static readonly Color Header = Color.FromArgb(10, 18, 32);
    public static readonly Color Sidebar = Color.FromArgb(13, 22, 38);
    public static readonly Color Card = Color.FromArgb(249, 249, 251);
    public static readonly Color CardBorder = Color.FromArgb(218, 222, 231);
    public static readonly Color Coral = Color.FromArgb(224, 67, 70);
    public static readonly Color Violet = Color.FromArgb(139, 61, 255);
    public static readonly Color VioletMuted = Color.FromArgb(48, 42, 74);
    public static readonly Color Text = Color.FromArgb(25, 30, 40);
    public static readonly Color MutedText = Color.FromArgb(101, 108, 122);
    public static readonly Color ShellText = Color.FromArgb(245, 247, 252);
    public static readonly Color ShellMutedText = Color.FromArgb(166, 175, 194);
    public static readonly Color SettingsCard = Color.FromArgb(21, 31, 49);
    public static readonly Color SettingsCardBorder = Color.FromArgb(45, 58, 80);
    public static readonly Color SettingsField = Color.FromArgb(16, 27, 45);
    public static readonly Color SettingsFieldBorder = Color.FromArgb(53, 66, 88);
    public static readonly Color SettingsButton = Color.FromArgb(24, 35, 55);
    public static readonly Color SettingsButtonHover = Color.FromArgb(34, 47, 71);
    public static readonly Color SettingsMutedText = Color.FromArgb(159, 170, 189);

    private static readonly Lazy<string> InterfaceFontName = new(() =>
    {
        var installed = FontFamily.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI" })
        {
            if (installed.Contains(candidate)) return candidate;
        }
        return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    });

    private static readonly Lazy<string> DisplayFontName = new(() =>
    {
        var installed = FontFamily.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed.Contains("Segoe UI Variable Display")
            ? "Segoe UI Variable Display"
            : InterfaceFontName.Value;
    });

    private static readonly ConcurrentDictionary<(string Family, float Size, FontStyle Style), Font> Fonts = new();

    public static Font InterfaceFont(float size, FontStyle style = FontStyle.Regular) =>
        Fonts.GetOrAdd(
            (InterfaceFontName.Value, size, style),
            key => new Font(key.Family, key.Size, key.Style, GraphicsUnit.Point));

    public static Font DisplayFont(float size, FontStyle style = FontStyle.Regular) =>
        Fonts.GetOrAdd(
            (DisplayFontName.Value, size, style),
            key => new Font(key.Family, key.Size, key.Style, GraphicsUnit.Point));
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

internal sealed class ActivityListPanel : Panel
{
    private bool _reflowing;
    private int _contentHeight = 1;

    public ActivityListPanel()
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
            var top = Padding.Top;
            var availableWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            foreach (Control control in Controls)
            {
                var margin = control.Margin;
                if (control.Dock != DockStyle.None) control.Dock = DockStyle.None;
                var bounds = new Rectangle(
                    Padding.Left + margin.Left,
                    top + margin.Top,
                    Math.Max(1, availableWidth - margin.Horizontal),
                    control.Height);
                if (control.Bounds != bounds) control.Bounds = bounds;
                top = control.Bottom + margin.Bottom;
            }
            _contentHeight = Math.Max(1, top + Padding.Bottom);
        }
        finally
        {
            _reflowing = false;
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        return new Size(Math.Max(1, proposedSize.Width), MeasureContentHeight());
    }

    internal int MeasureContentHeight()
    {
        var height = Padding.Vertical;
        foreach (Control control in Controls)
        {
            height += control.Margin.Top + control.Height + control.Margin.Bottom;
        }
        return Math.Max(1, height);
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        Reflow();
    }
}

internal sealed class BrandedScrollHost : Panel
{
    private const int ScrollbarGutter = 16;
    private const int TrackWidth = 6;
    private const int MinimumThumbHeight = 36;
    private Control? _content;
    private int _contentHeight;
    private int _scrollOffset;
    private bool _layingOut;
    private bool _draggingThumb;
    private bool _thumbHovered;
    private int _dragStartY;
    private int _dragStartOffset;
    private int _lastDpi = 96;

    public BrandedScrollHost()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        BackColor = ClipCordTheme.Shell;
        AccessibleName = "Recent activity list";
        AccessibleRole = AccessibleRole.Pane;
        SetStyle(ControlStyles.Selectable, true);
        Resize += (_, _) => RefreshContentLayout();
    }

    public Control? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value)) return;
            if (_content is not null)
            {
                UnwireFocusTracking(_content);
                Controls.Remove(_content);
            }

            _content = value;
            _scrollOffset = 0;
            if (_content is not null)
            {
                _content.Dock = DockStyle.None;
                _content.Margin = Padding.Empty;
                WireFocusTracking(_content);
                Controls.Add(_content);
            }
            RefreshContentLayout();
        }
    }

    internal bool HasOverflow => MaximumOffset > 0;
    internal int ScrollOffset => _scrollOffset;
    internal Rectangle ScrollThumbBounds => GetThumbBounds();

    internal void RefreshContentLayout(bool preservePosition = true, int anchorAdjustment = 0)
    {
        if (_layingOut || _content is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        _layingOut = true;
        try
        {
            var measuredHeight = MeasureContentHeight();
            var contentWidth = measuredHeight > ClientSize.Height
                ? Math.Max(1, ClientSize.Width - ScaleLogical(ScrollbarGutter))
                : ClientSize.Width;

            _contentHeight = measuredHeight;
            if (!preservePosition) _scrollOffset = 0;
            else if (_scrollOffset > 0 && anchorAdjustment != 0)
            {
                _scrollOffset = (int)Math.Clamp(
                    (long)_scrollOffset + anchorAdjustment,
                    0,
                    int.MaxValue);
            }
            _scrollOffset = Math.Clamp(_scrollOffset, 0, MaximumOffset);
            var contentBounds = new Rectangle(0, -_scrollOffset, contentWidth, _contentHeight);
            if (_content.Bounds != contentBounds) _content.Bounds = contentBounds;
        }
        finally
        {
            _layingOut = false;
        }
        Invalidate();
    }

    internal void ScrollBy(int pixels)
    {
        SetScrollOffset((int)Math.Clamp((long)_scrollOffset + pixels, 0, int.MaxValue));
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        _lastDpi = DeviceDpi;
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        var previousDpi = Math.Max(1, _lastDpi);
        base.OnDpiChangedAfterParent(eventArgs);
        var currentDpi = Math.Max(1, DeviceDpi);
        if (_scrollOffset > 0 && currentDpi != previousDpi)
        {
            _scrollOffset = (int)Math.Clamp(
                (long)Math.Round(_scrollOffset * currentDpi / (double)previousDpi),
                0,
                int.MaxValue);
        }
        _lastDpi = currentDpi;
        RefreshContentLayout();
    }

    protected override void OnMouseWheel(MouseEventArgs eventArgs)
    {
        if (HasOverflow && eventArgs.Delta != 0)
        {
            var step = ScaleLogical(72);
            ScrollBy(eventArgs.Delta > 0 ? -step : step);
        }
        base.OnMouseWheel(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        var step = ScaleLogical(48);
        switch (eventArgs.KeyCode)
        {
            case Keys.Up:
                ScrollBy(-step);
                break;
            case Keys.Down:
                ScrollBy(step);
                break;
            case Keys.PageUp:
                ScrollBy(-Math.Max(step, ClientSize.Height - step));
                break;
            case Keys.PageDown:
                ScrollBy(Math.Max(step, ClientSize.Height - step));
                break;
            case Keys.Home:
                SetScrollOffset(0);
                break;
            case Keys.End:
                SetScrollOffset(MaximumOffset);
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
        Focus();
        if (eventArgs.Button == MouseButtons.Left && HasOverflow)
        {
            var thumb = GetThumbBounds();
            if (GetThumbHitBounds().Contains(eventArgs.Location))
            {
                _draggingThumb = true;
                _dragStartY = eventArgs.Y;
                _dragStartOffset = _scrollOffset;
                Capture = true;
                Invalidate();
            }
            else if (GetTrackHitBounds().Contains(eventArgs.Location))
            {
                ScrollBy(eventArgs.Y < thumb.Top ? -ClientSize.Height : ClientSize.Height);
            }
        }
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_draggingThumb)
        {
            var track = GetTrackBounds();
            var thumb = GetThumbBounds();
            var travel = Math.Max(1, track.Height - thumb.Height);
            var offset = _dragStartOffset +
                         (int)Math.Round((eventArgs.Y - _dragStartY) * MaximumOffset / (double)travel);
            SetScrollOffset(offset);
        }
        else
        {
            var hovered = GetThumbHitBounds().Contains(eventArgs.Location);
            if (hovered != _thumbHovered)
            {
                _thumbHovered = hovered;
                Cursor = hovered ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }
        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left && _draggingThumb)
        {
            _draggingThumb = false;
            Capture = false;
            Invalidate();
        }
        base.OnMouseUp(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        if (!_draggingThumb && _thumbHovered)
        {
            _thumbHovered = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (!HasOverflow) return;

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = GetTrackBounds();
        var thumb = GetThumbBounds();
        using var trackPath = RoundedPanel.CreateRoundedPath(track, track.Width / 2);
        using var trackBrush = new SolidBrush(SystemInformation.HighContrast
            ? SystemColors.ScrollBar
            : Color.FromArgb(28, 39, 57));
        eventArgs.Graphics.FillPath(trackBrush, trackPath);
        using var thumbPath = RoundedPanel.CreateRoundedPath(thumb, thumb.Width / 2);
        using var thumbBrush = new SolidBrush(SystemInformation.HighContrast
            ? SystemColors.ControlDarkDark
            : _draggingThumb || _thumbHovered
                ? ClipCordTheme.Violet
                : Color.FromArgb(103, 77, 154));
        eventArgs.Graphics.FillPath(thumbBrush, thumbPath);
    }

    private int MeasureContentHeight()
    {
        if (_content is null) return 0;
        if (_content is ActivityListPanel activityList)
        {
            return activityList.MeasureContentHeight();
        }
        if (_content is GalleryGridPanel galleryGrid)
        {
            return galleryGrid.MeasureContentHeight();
        }
        _content.PerformLayout();
        var preferred = _content.GetPreferredSize(new Size(Math.Max(1, ClientSize.Width), int.MaxValue));
        return Math.Max(_content.MinimumSize.Height, preferred.Height);
    }

    private void WireFocusTracking(Control control)
    {
        control.GotFocus += DescendantGotFocus;
        control.ControlAdded += DescendantControlAdded;
        foreach (Control child in control.Controls) WireFocusTracking(child);
    }

    private void UnwireFocusTracking(Control control)
    {
        control.GotFocus -= DescendantGotFocus;
        control.ControlAdded -= DescendantControlAdded;
        foreach (Control child in control.Controls) UnwireFocusTracking(child);
    }

    private void DescendantControlAdded(object? sender, ControlEventArgs eventArgs)
    {
        if (eventArgs.Control is not null) WireFocusTracking(eventArgs.Control);
    }

    private void DescendantGotFocus(object? sender, EventArgs eventArgs)
    {
        if (sender is Control control) EnsureControlVisible(control);
    }

    internal void EnsureControlVisible(Control control)
    {
        if (_content is null || !HasOverflow ||
            (!ReferenceEquals(control, _content) && !_content.Contains(control))) return;

        var top = PointToClient(control.PointToScreen(Point.Empty)).Y;
        var margin = ScaleLogical(4);
        if (top < margin)
        {
            ScrollBy(top - margin);
            return;
        }

        var bottom = top + control.Height;
        var visibleBottom = ClientSize.Height - margin;
        if (bottom > visibleBottom) ScrollBy(bottom - visibleBottom);
    }

    private int MaximumOffset => Math.Max(0, _contentHeight - ClientSize.Height);

    private void SetScrollOffset(int value)
    {
        var clamped = Math.Clamp(value, 0, MaximumOffset);
        if (clamped == _scrollOffset) return;
        _scrollOffset = clamped;
        if (_content is not null) _content.Top = -_scrollOffset;
        Invalidate();
    }

    private Rectangle GetTrackBounds()
    {
        var width = ScaleLogical(TrackWidth);
        var inset = ScaleLogical(4);
        return new Rectangle(
            Math.Max(0, ClientSize.Width - width - inset),
            inset,
            width,
            Math.Max(1, ClientSize.Height - inset * 2));
    }

    internal Rectangle GetTrackHitBounds()
    {
        var hitWidth = ScaleLogical(20);
        return new Rectangle(
            Math.Max(0, ClientSize.Width - hitWidth),
            0,
            Math.Min(hitWidth, ClientSize.Width),
            ClientSize.Height);
    }

    private Rectangle GetThumbBounds()
    {
        if (!HasOverflow) return Rectangle.Empty;
        var track = GetTrackBounds();
        var proportionalHeight = (int)Math.Round(track.Height * ClientSize.Height / (double)_contentHeight);
        var height = Math.Clamp(proportionalHeight, ScaleLogical(MinimumThumbHeight), track.Height);
        var travel = Math.Max(0, track.Height - height);
        var top = track.Top + (MaximumOffset == 0
            ? 0
            : (int)Math.Round(travel * _scrollOffset / (double)MaximumOffset));
        return new Rectangle(track.Left, top, track.Width, height);
    }

    internal Rectangle GetThumbHitBounds()
    {
        var thumb = GetThumbBounds();
        if (thumb.IsEmpty) return Rectangle.Empty;
        var trackHit = GetTrackHitBounds();
        return new Rectangle(trackHit.Left, thumb.Top, trackHit.Width, thumb.Height);
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96d));
}

internal enum BrandGlyph
{
    Settings,
    Activity,
    Gallery,
    About,
    Folder,
    Destination,
    Sliders,
    ClipSource,
    DiscordDestination,
    UploadBehavior,
    AppPreferences,
    Minimize,
    Maximize,
    Restore,
    Close,
    Shield,
    AppStatus,
    Diagnostics,
    Credits,
    FileText,
    Copy,
    FolderOpen,
    ReportProblem
}

internal sealed class RoundedPanel : Panel
{
    private int _cornerRadius = 14;
    private Size _lastRegionSize = Size.Empty;
    private Action? _keyboardAction;
    private bool _keyboardFocusable;
    public bool AccessibilityUnavailable { get; set; }

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(0, value);
            _lastRegionSize = Size.Empty;
            UpdateRegion();
            Invalidate();
        }
    }

    public Color BorderColor { get; set; } = Color.Transparent;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        Resize += (_, _) => UpdateRegion();
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
    }

    public void EnableKeyboardAccess(Action? action = null)
    {
        _keyboardAction = action;
        _keyboardFocusable = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (BorderColor != Color.Transparent && Width > 1 && Height > 1)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
            using var pen = new Pen(BorderColor);
            eventArgs.Graphics.DrawPath(pen, path);
        }
        if (_keyboardFocusable && Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (_keyboardFocusable) Focus();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (_keyboardFocusable && eventArgs.KeyCode is Keys.Enter or Keys.Space)
        {
            _keyboardAction?.Invoke();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new RoundedPanelAccessibleObject(this);

    private sealed class RoundedPanelAccessibleObject(RoundedPanel owner) : Control.ControlAccessibleObject(owner)
    {
        public override AccessibleStates State => base.State |
            (owner.AccessibilityUnavailable ? AccessibleStates.Unavailable : AccessibleStates.None);
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }

    internal static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(0, radius * 2));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class GradientButton : Button
{
    private Size _lastRegionSize = Size.Empty;
    public Color StartColor { get; set; } = ClipCordTheme.Coral;
    public Color EndColor { get; set; } = ClipCordTheme.Violet;

    public GradientButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        Font = ClipCordTheme.InterfaceFont(10.5f, FontStyle.Bold);
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
        var start = Enabled ? StartColor : Color.FromArgb(105, 110, 123);
        var end = Enabled ? EndColor : Color.FromArgb(105, 110, 123);
        using var background = new SolidBrush(start);
        eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        using var path = RoundedPanel.CreateRoundedPath(
            new Rectangle(0, 0, Width, Height), 10);
        using var brush = new LinearGradientBrush(bounds, start, end, LinearGradientMode.Horizontal);
        eventArgs.Graphics.FillPath(brush, path);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            bounds,
            Enabled ? ForeColor : Color.FromArgb(225, 225, 230),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width, Height), 10);
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class OutlineButton : Button
{
    public BrandGlyph? LeadingGlyph { get; set; }
    public Color SurfaceColor { get; set; } = Color.White;
    public Color OutlineColor { get; set; } = ClipCordTheme.CardBorder;
    public Color HoverColor { get; set; } = Color.FromArgb(244, 241, 252);
    public Color DisabledSurfaceColor { get; set; } = Color.FromArgb(230, 231, 235);
    public Color DisabledTextColor { get; set; } = Color.FromArgb(140, 144, 153);
    private bool _hovered;
    private Size _lastRegionSize = Size.Empty;

    public OutlineButton()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = ClipCordTheme.Text;
        Font = ClipCordTheme.InterfaceFont(9.5f);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        DoubleBuffered = true;
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        Resize += (_, _) => UpdateRegion();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 0 || Height <= 0) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = RoundedPanel.CreateRoundedPath(bounds, 8);
        using var fill = new SolidBrush(!Enabled
            ? DisabledSurfaceColor
            : _hovered ? HoverColor : SurfaceColor);
        using var outline = new Pen(OutlineColor);
        eventArgs.Graphics.FillRectangle(fill, ClientRectangle);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(outline, path);
        var textColor = Enabled ? ForeColor : DisabledTextColor;
        if (LeadingGlyph is { } glyph)
        {
            var glyphSize = Math.Max(16, Font.Height - 2);
            var gap = Math.Max(5, (int)Math.Round(6 * DeviceDpi / 96d));
            var measured = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var combinedWidth = glyphSize + gap + measured.Width;
            var left = Math.Max(6, (Width - combinedWidth) / 2);
            var glyphBounds = new Rectangle(left, (Height - glyphSize) / 2, glyphSize, glyphSize);
            BrandGlyphControl.DrawGlyph(eventArgs.Graphics, glyphBounds, glyph, textColor, Math.Max(1.3f, glyphSize / 12f));
            var textBounds = new Rectangle(
                glyphBounds.Right + gap,
                0,
                Math.Max(0, Width - glyphBounds.Right - gap - 4),
                Height);
            TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                textBounds,
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        else
        {
            TextRenderer.DrawText(
                eventArgs.Graphics,
                Text,
                Font,
                ClientRectangle,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0 || _lastRegionSize == Size) return;
        _lastRegionSize = Size;
        using var path = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width, Height), 8);
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        AutoSize = true;
        MinimumSize = new Size(0, 30);
        Font = ClipCordTheme.InterfaceFont(10f);
        ForeColor = ClipCordTheme.Text;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        CheckedChanged += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 0 || Height <= 0) return;
        using (var background = new SolidBrush(BackColor))
        {
            eventArgs.Graphics.FillRectangle(background, ClientRectangle);
        }
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = GetTrackBounds();
        var trackHeight = track.Height;
        using var trackPath = RoundedPanel.CreateRoundedPath(track, trackHeight / 2);
        using var trackBrush = new SolidBrush(Checked && Enabled
            ? ClipCordTheme.Violet
            : Color.FromArgb(167, 172, 184));
        eventArgs.Graphics.FillPath(trackBrush, trackPath);
        var thumbInset = Math.Max(3, trackHeight / 8);
        var thumbSize = trackHeight - thumbInset * 2;
        var thumb = new Rectangle(
            Checked ? track.Right - thumbInset - thumbSize : track.Left + thumbInset,
            track.Top + thumbInset,
            thumbSize,
            thumbSize);
        using var thumbBrush = new SolidBrush(Color.White);
        eventArgs.Graphics.FillEllipse(thumbBrush, thumb);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            GetTextBounds(),
            Enabled ? ForeColor : Color.FromArgb(135, 139, 147),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var trackHeight = Math.Max(24, Font.Height + 4);
        var trackWidth = trackHeight * 2;
        var textSize = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(trackWidth + Math.Max(12, trackHeight / 2) + textSize.Width + 4,
            Math.Max(trackHeight, textSize.Height) + 6);
    }

    internal Rectangle GetTrackBounds()
    {
        var trackHeight = Math.Max(24, Font.Height + 4);
        return new Rectangle(0, Math.Max(0, (Height - trackHeight) / 2), trackHeight * 2, trackHeight);
    }

    internal Rectangle GetTextBounds()
    {
        var track = GetTrackBounds();
        var textLeft = track.Right + Math.Max(12, track.Height / 2);
        return new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft), Height);
    }
}

internal class BrandGlyphControl : Control
{
    public BrandGlyph Glyph { get; set; }
    public Color GlyphColor { get; set; } = ClipCordTheme.ShellText;
    public float StrokeWidth { get; set; } = 1.8f;

    public BrandGlyphControl()
    {
        DoubleBuffered = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 0 || Height <= 0) return;
        DrawGlyph(eventArgs.Graphics, ClientRectangle, Glyph, GlyphColor, StrokeWidth);
    }

    internal static void DrawGlyph(Graphics graphics, Rectangle bounds, BrandGlyph glyph, Color color, float width)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var inset = Math.Max(3, Math.Min(bounds.Width, bounds.Height) / 5);
        var box = Rectangle.Inflate(bounds, -inset, -inset);
        using var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (glyph)
        {
            case BrandGlyph.Settings:
                DrawGear(graphics, box, pen);
                break;
            case BrandGlyph.Activity:
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left, box.Top + box.Height * .58f),
                    new PointF(box.Left + box.Width * .22f, box.Top + box.Height * .58f),
                    new PointF(box.Left + box.Width * .34f, box.Top + box.Height * .2f),
                    new PointF(box.Left + box.Width * .52f, box.Bottom - box.Height * .12f),
                    new PointF(box.Left + box.Width * .66f, box.Top + box.Height * .42f),
                    new PointF(box.Right, box.Top + box.Height * .42f)
                ]);
                break;
            case BrandGlyph.Gallery:
                var cellWidth = Math.Max(2, (box.Width - 3) / 2);
                var cellHeight = Math.Max(2, (box.Height - 3) / 2);
                graphics.DrawRectangle(pen, box.Left, box.Top, cellWidth, cellHeight);
                graphics.DrawRectangle(pen, box.Right - cellWidth, box.Top, cellWidth, cellHeight);
                graphics.DrawRectangle(pen, box.Left, box.Bottom - cellHeight, cellWidth, cellHeight);
                graphics.DrawRectangle(pen, box.Right - cellWidth, box.Bottom - cellHeight, cellWidth, cellHeight);
                break;
            case BrandGlyph.About:
                graphics.DrawEllipse(pen, box);
                graphics.DrawLine(pen, box.Left + box.Width / 2f, box.Top + box.Height * .45f,
                    box.Left + box.Width / 2f, box.Bottom - box.Height * .2f);
                using (var infoDotBrush = new SolidBrush(color))
                {
                    graphics.FillEllipse(infoDotBrush,
                        box.Left + box.Width / 2f - 1.3f, box.Top + box.Height * .2f, 2.6f, 2.6f);
                }
                break;
            case BrandGlyph.Folder:
                using (var folderPath = RoundedPanel.CreateRoundedPath(
                           new Rectangle(box.Left, box.Top + box.Height / 4, box.Width, box.Height * 3 / 4), 4))
                {
                    graphics.DrawPath(pen, folderPath);
                }
                graphics.DrawLines(pen,
                new Point[]
                {
                    new Point(box.Left + 2, box.Top + box.Height / 4),
                    new Point(box.Left + box.Width / 3, box.Top + box.Height / 4),
                    new Point(box.Left + box.Width * 2 / 5, box.Top + 1),
                    new Point(box.Left + box.Width * 3 / 5, box.Top + 1),
                    new Point(box.Left + box.Width * 2 / 3, box.Top + box.Height / 4)
                });
                break;
            case BrandGlyph.Destination:
                using (var discord = new GraphicsPath())
                {
                    discord.AddBezier(
                        box.Left + box.Width * .12f, box.Top + box.Height * .7f,
                        box.Left + box.Width * .18f, box.Top + box.Height * .25f,
                        box.Left + box.Width * .34f, box.Top + box.Height * .18f,
                        box.Left + box.Width * .5f, box.Top + box.Height * .23f);
                    discord.AddBezier(
                        box.Left + box.Width * .5f, box.Top + box.Height * .23f,
                        box.Left + box.Width * .66f, box.Top + box.Height * .18f,
                        box.Left + box.Width * .82f, box.Top + box.Height * .25f,
                        box.Left + box.Width * .88f, box.Top + box.Height * .7f);
                    discord.AddBezier(
                        box.Left + box.Width * .88f, box.Top + box.Height * .7f,
                        box.Left + box.Width * .76f, box.Top + box.Height * .83f,
                        box.Left + box.Width * .65f, box.Top + box.Height * .86f,
                        box.Left + box.Width * .58f, box.Top + box.Height * .7f);
                    discord.AddBezier(
                        box.Left + box.Width * .58f, box.Top + box.Height * .7f,
                        box.Left + box.Width * .52f, box.Top + box.Height * .77f,
                        box.Left + box.Width * .48f, box.Top + box.Height * .77f,
                        box.Left + box.Width * .42f, box.Top + box.Height * .7f);
                    discord.AddBezier(
                        box.Left + box.Width * .42f, box.Top + box.Height * .7f,
                        box.Left + box.Width * .35f, box.Top + box.Height * .86f,
                        box.Left + box.Width * .24f, box.Top + box.Height * .83f,
                        box.Left + box.Width * .12f, box.Top + box.Height * .7f);
                    graphics.DrawPath(pen, discord);
                }
                using (var destinationDotBrush = new SolidBrush(color))
                {
                    var eyeSize = Math.Max(3f, box.Width * .12f);
                    graphics.FillEllipse(
                        destinationDotBrush,
                        box.Left + box.Width * .3f,
                        box.Top + box.Height * .45f,
                        eyeSize,
                        eyeSize);
                    graphics.FillEllipse(
                        destinationDotBrush,
                        box.Left + box.Width * .58f,
                        box.Top + box.Height * .45f,
                        eyeSize,
                        eyeSize);
                }
                break;
            case BrandGlyph.Sliders:
                DrawSliders(graphics, box, pen, color);
                break;
            case BrandGlyph.ClipSource:
            case BrandGlyph.DiscordDestination:
            case BrandGlyph.UploadBehavior:
            case BrandGlyph.AppPreferences:
                DrawFeatureGlyph(graphics, bounds, glyph, color);
                break;
            case BrandGlyph.Minimize:
                graphics.DrawLine(pen, box.Left, box.Bottom - 2, box.Right, box.Bottom - 2);
                break;
            case BrandGlyph.Maximize:
                graphics.DrawRectangle(pen, box.Left, box.Top, box.Width - 1, box.Height - 1);
                break;
            case BrandGlyph.Restore:
                graphics.DrawRectangle(pen, box.Left + 3, box.Top, box.Width - 4, box.Height - 4);
                graphics.DrawRectangle(pen, box.Left, box.Top + 3, box.Width - 4, box.Height - 4);
                break;
            case BrandGlyph.Close:
                graphics.DrawLine(pen, box.Left, box.Top, box.Right, box.Bottom);
                graphics.DrawLine(pen, box.Right, box.Top, box.Left, box.Bottom);
                break;
            case BrandGlyph.Shield:
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left + box.Width / 2f, box.Top),
                    new PointF(box.Right, box.Top + box.Height * .2f),
                    new PointF(box.Right - box.Width * .08f, box.Top + box.Height * .7f),
                    new PointF(box.Left + box.Width / 2f, box.Bottom),
                    new PointF(box.Left + box.Width * .08f, box.Top + box.Height * .7f),
                    new PointF(box.Left, box.Top + box.Height * .2f),
                    new PointF(box.Left + box.Width / 2f, box.Top)
                ]);
                break;
            case BrandGlyph.AppStatus:
                graphics.DrawRectangle(pen, box.Left, box.Top, box.Width - 1, box.Height * .68f);
                graphics.DrawLine(pen, box.Left + box.Width * .34f, box.Bottom, box.Right - box.Width * .34f, box.Bottom);
                graphics.DrawLine(pen, box.Left + box.Width * .5f, box.Top + box.Height * .68f, box.Left + box.Width * .5f, box.Bottom);
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left + box.Width * .62f, box.Top + box.Height * .36f),
                    new PointF(box.Left + box.Width * .72f, box.Top + box.Height * .47f),
                    new PointF(box.Left + box.Width * .9f, box.Top + box.Height * .22f)
                ]);
                break;
            case BrandGlyph.Diagnostics:
                graphics.DrawEllipse(pen, box);
                graphics.DrawEllipse(
                    pen,
                    box.Left + box.Width * .32f,
                    box.Top + box.Height * .32f,
                    box.Width * .36f,
                    box.Height * .36f);
                graphics.DrawLine(pen, box.Left + box.Width * .18f, box.Top + box.Height * .18f,
                    box.Left + box.Width * .36f, box.Top + box.Height * .36f);
                graphics.DrawLine(pen, box.Right - box.Width * .18f, box.Top + box.Height * .18f,
                    box.Right - box.Width * .36f, box.Top + box.Height * .36f);
                graphics.DrawLine(pen, box.Left + box.Width * .18f, box.Bottom - box.Height * .18f,
                    box.Left + box.Width * .36f, box.Bottom - box.Height * .36f);
                graphics.DrawLine(pen, box.Right - box.Width * .18f, box.Bottom - box.Height * .18f,
                    box.Right - box.Width * .36f, box.Bottom - box.Height * .36f);
                break;
            case BrandGlyph.Credits:
                DrawSparkle(graphics, pen, box.Left + box.Width * .48f, box.Top + box.Height * .45f, box.Width * .28f);
                DrawSparkle(graphics, pen, box.Left + box.Width * .8f, box.Top + box.Height * .2f, box.Width * .12f);
                DrawSparkle(graphics, pen, box.Left + box.Width * .18f, box.Top + box.Height * .78f, box.Width * .1f);
                break;
            case BrandGlyph.FileText:
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left + box.Width * .15f, box.Top),
                    new PointF(box.Left + box.Width * .68f, box.Top),
                    new PointF(box.Right - box.Width * .05f, box.Top + box.Height * .25f),
                    new PointF(box.Right - box.Width * .05f, box.Bottom),
                    new PointF(box.Left + box.Width * .15f, box.Bottom),
                    new PointF(box.Left + box.Width * .15f, box.Top)
                ]);
                graphics.DrawLine(pen, box.Left + box.Width * .32f, box.Top + box.Height * .5f, box.Right - box.Width * .2f, box.Top + box.Height * .5f);
                graphics.DrawLine(pen, box.Left + box.Width * .32f, box.Top + box.Height * .7f, box.Right - box.Width * .3f, box.Top + box.Height * .7f);
                break;
            case BrandGlyph.Copy:
                graphics.DrawRectangle(pen, box.Left + box.Width * .27f, box.Top + box.Height * .25f, box.Width * .65f, box.Height * .68f);
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left + box.Width * .72f, box.Top + box.Height * .08f),
                    new PointF(box.Left + box.Width * .08f, box.Top + box.Height * .08f),
                    new PointF(box.Left + box.Width * .08f, box.Top + box.Height * .72f)
                ]);
                break;
            case BrandGlyph.FolderOpen:
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left, box.Top + box.Height * .38f),
                    new PointF(box.Left + box.Width * .12f, box.Top + box.Height * .15f),
                    new PointF(box.Left + box.Width * .42f, box.Top + box.Height * .15f),
                    new PointF(box.Left + box.Width * .52f, box.Top + box.Height * .3f),
                    new PointF(box.Right, box.Top + box.Height * .3f)
                ]);
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left, box.Top + box.Height * .38f),
                    new PointF(box.Left + box.Width * .13f, box.Bottom),
                    new PointF(box.Right - box.Width * .08f, box.Bottom),
                    new PointF(box.Right, box.Top + box.Height * .38f),
                    new PointF(box.Left, box.Top + box.Height * .38f)
                ]);
                break;
            case BrandGlyph.ReportProblem:
                using (var reportPath = RoundedPanel.CreateRoundedPath(
                           new Rectangle(box.Left, box.Top, box.Width, (int)(box.Height * .72f)), 4))
                {
                    graphics.DrawPath(pen, reportPath);
                }
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left + box.Width * .25f, box.Top + box.Height * .72f),
                    new PointF(box.Left + box.Width * .18f, box.Bottom),
                    new PointF(box.Left + box.Width * .48f, box.Top + box.Height * .72f)
                ]);
                graphics.DrawLine(pen, box.Left + box.Width * .5f, box.Top + box.Height * .2f, box.Left + box.Width * .5f, box.Top + box.Height * .43f);
                using (var reportDot = new SolidBrush(color))
                {
                    graphics.FillEllipse(reportDot, box.Left + box.Width * .46f, box.Top + box.Height * .53f, box.Width * .08f, box.Width * .08f);
                }
                break;
        }
    }

    private static void DrawSparkle(Graphics graphics, Pen pen, float centerX, float centerY, float radius)
    {
        graphics.DrawLines(pen,
        [
            new PointF(centerX, centerY - radius),
            new PointF(centerX + radius * .28f, centerY - radius * .28f),
            new PointF(centerX + radius, centerY),
            new PointF(centerX + radius * .28f, centerY + radius * .28f),
            new PointF(centerX, centerY + radius),
            new PointF(centerX - radius * .28f, centerY + radius * .28f),
            new PointF(centerX - radius, centerY),
            new PointF(centerX - radius * .28f, centerY - radius * .28f),
            new PointF(centerX, centerY - radius)
        ]);
    }

    private static void DrawGear(Graphics graphics, Rectangle box, Pen pen)
    {
        var center = new PointF(box.Left + box.Width / 2f, box.Top + box.Height / 2f);
        var outer = Math.Min(box.Width, box.Height) * .4f;
        var inner = outer * .38f;
        graphics.DrawEllipse(pen, center.X - outer * .72f, center.Y - outer * .72f, outer * 1.44f, outer * 1.44f);
        graphics.DrawEllipse(pen, center.X - inner, center.Y - inner, inner * 2, inner * 2);
        for (var index = 0; index < 8; index++)
        {
            var angle = index * Math.PI / 4;
            graphics.DrawLine(pen,
                center.X + (float)Math.Cos(angle) * outer * .75f,
                center.Y + (float)Math.Sin(angle) * outer * .75f,
                center.X + (float)Math.Cos(angle) * outer,
                center.Y + (float)Math.Sin(angle) * outer);
        }
    }

    private static void DrawSliders(Graphics graphics, Rectangle box, Pen pen, Color color)
    {
        var positions = new[] { .25f, .55f, .78f };
        var knobs = new[] { .65f, .32f, .72f };
        for (var index = 0; index < positions.Length; index++)
        {
            var y = box.Top + box.Height * positions[index];
            graphics.DrawLine(pen, box.Left, y, box.Right, y);
            var x = box.Left + box.Width * knobs[index];
            using var fill = new SolidBrush(color);
            graphics.FillEllipse(fill, x - 3, y - 3, 6, 6);
        }
    }

    internal static void DrawFeatureGlyph(
        Graphics graphics,
        Rectangle bounds,
        BrandGlyph glyph,
        Color color)
    {
        var side = Math.Min(bounds.Width, bounds.Height);
        if (side <= 0) return;

        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(
                bounds.Left + (bounds.Width - side) / 2f,
                bounds.Top + (bounds.Height - side) / 2f);
            graphics.ScaleTransform(side / 64f, side / 64f);
            graphics.TranslateTransform(8f, 8f);

            using var fill = new SolidBrush(Color.FromArgb(34, color));
            using var stroke = new Pen(color, 2.15f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var detail = new Pen(color, 1.55f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var solid = new SolidBrush(color);

            switch (glyph)
            {
                case BrandGlyph.ClipSource:
                    DrawClipSourceFeature(graphics, fill, stroke, detail, solid);
                    break;
                case BrandGlyph.DiscordDestination:
                    DrawDiscordDestinationFeature(graphics, fill, stroke, detail, solid);
                    break;
                case BrandGlyph.UploadBehavior:
                    DrawUploadBehaviorFeature(graphics, fill, stroke, detail, solid);
                    break;
                case BrandGlyph.AppPreferences:
                    DrawAppPreferencesFeature(graphics, fill, stroke, detail, solid);
                    break;
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawClipSourceFeature(
        Graphics graphics,
        Brush fill,
        Pen stroke,
        Pen detail,
        Brush solid)
    {
        using var folder = RoundedPanel.CreateRoundedPath(new Rectangle(2, 11, 44, 31), 5);
        graphics.FillPath(fill, folder);
        graphics.DrawPath(stroke, folder);
        graphics.DrawLines(stroke,
        [
            new PointF(3, 14),
            new PointF(3, 8.5f),
            new PointF(15, 8.5f),
            new PointF(19, 13),
            new PointF(40, 13)
        ]);

        using var film = RoundedPanel.CreateRoundedPath(new Rectangle(10, 18, 28, 18), 3);
        graphics.DrawPath(detail, film);
        graphics.DrawLine(detail, 14, 18, 14, 36);
        graphics.DrawLine(detail, 34, 18, 34, 36);
        graphics.DrawLine(detail, 10.5f, 23, 14, 23);
        graphics.DrawLine(detail, 10.5f, 31, 14, 31);
        graphics.DrawLine(detail, 34, 23, 37.5f, 23);
        graphics.DrawLine(detail, 34, 31, 37.5f, 31);
        graphics.FillPolygon(solid,
        [
            new PointF(21, 22.5f),
            new PointF(30, 27),
            new PointF(21, 31.5f)
        ]);
    }

    private static void DrawDiscordDestinationFeature(
        Graphics graphics,
        Brush fill,
        Pen stroke,
        Pen detail,
        Brush solid)
    {
        using var discord = new GraphicsPath();
        discord.StartFigure();
        discord.AddBezier(12.7f, 13.8f, 20.2f, 10.4f, 27.8f, 10.4f, 35.3f, 13.8f);
        discord.AddBezier(35.3f, 13.8f, 39.5f, 19.7f, 41.8f, 26.4f, 42.3f, 33.9f);
        discord.AddBezier(42.3f, 33.9f, 38.5f, 37.3f, 34.9f, 39.4f, 31.3f, 40.5f);
        discord.AddLine(31.3f, 40.5f, 28.6f, 36.4f);
        discord.AddBezier(28.6f, 36.4f, 30.6f, 35.8f, 32.4f, 35f, 34.1f, 33.9f);
        discord.AddBezier(34.1f, 33.9f, 27.4f, 37f, 20.6f, 37f, 13.9f, 33.9f);
        discord.AddBezier(13.9f, 33.9f, 15.6f, 35f, 17.4f, 35.8f, 19.4f, 36.4f);
        discord.AddLine(19.4f, 36.4f, 16.7f, 40.5f);
        discord.AddBezier(16.7f, 40.5f, 13.1f, 39.4f, 9.5f, 37.3f, 5.7f, 33.9f);
        discord.AddBezier(5.7f, 33.9f, 6.2f, 26.4f, 8.5f, 19.7f, 12.7f, 13.8f);
        discord.CloseFigure();
        graphics.FillPath(fill, discord);

        using var outline = new GraphicsPath();
        outline.StartFigure();
        outline.AddBezier(13.1f, 14.4f, 20.3f, 11.2f, 27.7f, 11.2f, 34.9f, 14.4f);
        outline.AddBezier(34.9f, 14.4f, 38.8f, 20.1f, 41f, 26.4f, 41.5f, 33.6f);
        outline.AddBezier(41.5f, 33.6f, 37.9f, 36.6f, 34.5f, 38.6f, 31.4f, 39.6f);
        outline.AddLine(31.4f, 39.6f, 28.7f, 35.6f);
        outline.AddBezier(28.7f, 35.6f, 30.6f, 35.1f, 32.4f, 34.3f, 34.1f, 33.3f);
        outline.AddBezier(34.1f, 33.3f, 27.4f, 36.3f, 20.6f, 36.3f, 13.9f, 33.3f);
        outline.AddBezier(13.9f, 33.3f, 15.6f, 34.3f, 17.4f, 35.1f, 19.3f, 35.6f);
        outline.AddLine(19.3f, 35.6f, 16.6f, 39.6f);
        outline.AddBezier(16.6f, 39.6f, 13.5f, 38.6f, 10.1f, 36.6f, 6.5f, 33.6f);
        outline.AddBezier(6.5f, 33.6f, 7f, 26.4f, 9.2f, 20.1f, 13.1f, 14.4f);
        outline.CloseFigure();
        graphics.DrawPath(stroke, outline);

        graphics.DrawBezier(detail, 16.4f, 17.3f, 18.5f, 16.5f, 20.6f, 16f, 22.7f, 15.9f);
        graphics.DrawBezier(detail, 25.3f, 15.9f, 27.4f, 16f, 29.5f, 16.5f, 31.6f, 17.3f);
        graphics.FillEllipse(solid, 16f, 23.3f, 4.8f, 4.8f);
        graphics.FillEllipse(solid, 27.2f, 23.3f, 4.8f, 4.8f);
        graphics.DrawBezier(detail, 18.2f, 30.6f, 22f, 32.6f, 26f, 32.6f, 29.8f, 30.6f);
    }

    private static void DrawUploadBehaviorFeature(
        Graphics graphics,
        Brush fill,
        Pen stroke,
        Pen detail,
        Brush solid)
    {
        using var cloud = new GraphicsPath();
        cloud.StartFigure();
        cloud.AddBezier(11, 35, 4, 35, 3, 28, 6, 23);
        cloud.AddBezier(6, 23, 8, 19, 11, 18, 15, 18);
        cloud.AddBezier(15, 18, 18, 9, 26, 6, 33, 11);
        cloud.AddBezier(33, 11, 36, 13, 38, 16, 38.5f, 20);
        cloud.AddBezier(38.5f, 20, 44, 21, 46, 25, 45, 29.5f);
        cloud.AddBezier(45, 29.5f, 44, 33, 41, 35, 37, 35);
        cloud.CloseFigure();
        graphics.FillPath(fill, cloud);
        graphics.DrawPath(stroke, cloud);
        graphics.DrawLine(stroke, 24, 34, 24, 16);
        graphics.DrawLines(stroke,
        [
            new PointF(17.5f, 22.5f),
            new PointF(24, 16),
            new PointF(30.5f, 22.5f)
        ]);
        graphics.DrawLine(detail, 6, 40, 42, 40);
        graphics.DrawLine(detail, 11, 44, 37, 44);
        graphics.FillEllipse(solid, 5, 24, 3, 3);
    }

    private static void DrawAppPreferencesFeature(
        Graphics graphics,
        Brush fill,
        Pen stroke,
        Pen detail,
        Brush solid)
    {
        using var window = RoundedPanel.CreateRoundedPath(new Rectangle(4, 6, 40, 33), 5);
        graphics.FillPath(fill, window);
        graphics.DrawPath(stroke, window);
        graphics.DrawLine(detail, 4, 16, 44, 16);
        graphics.FillEllipse(solid, 9, 10, 2.5f, 2.5f);
        graphics.FillEllipse(solid, 14, 10, 2.5f, 2.5f);
        graphics.DrawLine(detail, 10, 23, 38, 23);
        graphics.DrawLine(detail, 10, 31, 38, 31);
        graphics.DrawLine(detail, 19, 19, 19, 27);
        graphics.DrawLine(detail, 30, 27, 30, 35);
        graphics.FillEllipse(solid, 16.2f, 20.2f, 5.6f, 5.6f);
        graphics.FillEllipse(solid, 27.2f, 28.2f, 5.6f, 5.6f);
        graphics.DrawLine(detail, 18, 44, 30, 44);
        graphics.DrawLine(detail, 24, 39, 24, 44);
    }
}

internal sealed class BrandIconTile : Control
{
    private BrandGlyph _glyph;

    public BrandGlyph Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph == value) return;
            _glyph = value;
            Invalidate();
        }
    }

    public BrandIconTile()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(64, 64);
        BackColor = ClipCordTheme.SettingsCard;
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.Clear(BackColor);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var cornerRadius = Math.Max(8, Math.Min(14, Math.Min(Width, Height) / 4));
        using var path = RoundedPanel.CreateRoundedPath(bounds, cornerRadius);
        var (start, end) = GetPalette(Glyph);
        using var fill = new LinearGradientBrush(bounds, start, end, 45f);
        eventArgs.Graphics.FillPath(fill, path);
        using var sheen = new LinearGradientBrush(
            bounds,
            Color.FromArgb(50, Color.White),
            Color.FromArgb(0, Color.White),
            120f);
        eventArgs.Graphics.FillPath(sheen, path);
        using var border = new Pen(Color.FromArgb(72, Color.White), Math.Max(1f, Math.Min(Width, Height) / 64f));
        eventArgs.Graphics.DrawPath(border, path);
        BrandGlyphControl.DrawFeatureGlyph(
            eventArgs.Graphics,
            bounds,
            Glyph,
            SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White);
    }

    private static (Color Start, Color End) GetPalette(BrandGlyph glyph)
    {
        if (SystemInformation.HighContrast)
        {
            return (SystemColors.Highlight, SystemColors.Highlight);
        }

        return glyph switch
        {
            BrandGlyph.ClipSource => (Color.FromArgb(243, 96, 84), Color.FromArgb(220, 69, 105)),
            BrandGlyph.DiscordDestination => (Color.FromArgb(88, 101, 242), Color.FromArgb(143, 66, 245)),
            BrandGlyph.UploadBehavior => (Color.FromArgb(76, 115, 241), Color.FromArgb(153, 71, 236)),
            BrandGlyph.AppPreferences => (Color.FromArgb(239, 91, 84), Color.FromArgb(148, 68, 238)),
            _ => (ClipCordTheme.Coral, Color.FromArgb(255, 111, 82))
        };
    }
}

internal sealed class TitleBarButton : Control
{
    private bool _hovered;
    public BrandGlyph Glyph { get; set; }

    public TitleBarButton()
    {
        Size = new Size(48, 46);
        Margin = Padding.Empty;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        DoubleBuffered = true;
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        };
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 0 || Height <= 0) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hovered)
        {
            using var hover = new SolidBrush(Glyph == BrandGlyph.Close
                ? Color.FromArgb(196, 43, 52)
                : Color.FromArgb(34, 45, 64));
            eventArgs.Graphics.FillRectangle(hover, ClientRectangle);
        }
        BrandGlyphControl.DrawGlyph(
            eventArgs.Graphics,
            new Rectangle((Width - 22) / 2, (Height - 22) / 2, 22, 22),
            Glyph,
            Enabled ? ClipCordTheme.ShellText : Color.FromArgb(91, 101, 119),
            1.5f);
        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}

internal sealed class ClipCordLogoControl : Control
{
    private const string LogoResourceName = "ClipsToDiscord.Assets.AppIcon.png";
    private static readonly Lazy<Bitmap> LogoImage = new(LoadLogoImage);

    internal static Size EmbeddedAssetSize => LogoImage.Value.Size;

    public ClipCordLogoControl()
    {
        DoubleBuffered = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleName = "ClipCord logo";
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 0 || Height <= 0) return;

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        eventArgs.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        eventArgs.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        eventArgs.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var side = Math.Min(Width, Height);
        var destination = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);
        eventArgs.Graphics.DrawImage(LogoImage.Value, destination);
    }

    private static Bitmap LoadLogoImage()
    {
        using var stream = typeof(ClipCordLogoControl).Assembly.GetManifestResourceStream(LogoResourceName)
            ?? throw new InvalidOperationException($"Embedded ClipCord logo '{LogoResourceName}' was not found.");
        using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
        return new Bitmap(decoded);
    }
}

internal sealed class GradientStrip : Control
{
    public bool Horizontal { get; set; }

    public GradientStrip()
    {
        DoubleBuffered = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        Width = 4;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 0 || Height <= 0) return;
        using var brush = new LinearGradientBrush(
            ClientRectangle,
            ClipCordTheme.Coral,
            ClipCordTheme.Violet,
            Horizontal ? LinearGradientMode.Horizontal : LinearGradientMode.Vertical);
        eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
