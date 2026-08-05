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

internal enum BrandGlyph
{
    Settings,
    Activity,
    About,
    Folder,
    Destination,
    Sliders,
    Minimize,
    Maximize,
    Restore,
    Close,
    Shield
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
    public Color SurfaceColor { get; set; } = Color.White;
    public Color OutlineColor { get; set; } = ClipCordTheme.CardBorder;
    public Color HoverColor { get; set; } = Color.FromArgb(244, 241, 252);
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
            ? Color.FromArgb(230, 231, 235)
            : _hovered ? HoverColor : SurfaceColor);
        using var outline = new Pen(OutlineColor);
        eventArgs.Graphics.FillRectangle(fill, ClientRectangle);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(outline, path);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            ClientRectangle,
            Enabled ? ForeColor : Color.FromArgb(140, 144, 153),
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
        }
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
}

internal sealed class BrandIconTile : Control
{
    public BrandGlyph Glyph { get; set; }

    public BrandIconTile()
    {
        DoubleBuffered = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(52, 52);
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (Width <= 1 || Height <= 1) return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPanel.CreateRoundedPath(bounds, 11);
        using var fill = new LinearGradientBrush(bounds, ClipCordTheme.Coral, Color.FromArgb(255, 111, 82), 45f);
        eventArgs.Graphics.FillPath(fill, path);
        BrandGlyphControl.DrawGlyph(
            eventArgs.Graphics,
            Rectangle.Inflate(bounds, -9, -9),
            Glyph,
            Color.White,
            2.1f);
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
            LinearGradientMode.Vertical);
        eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
