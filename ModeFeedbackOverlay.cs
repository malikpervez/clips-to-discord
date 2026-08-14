using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClipsToDiscord;

internal enum ModeFeedbackTone
{
    UploadsEnabled,
    LocalOnlyEnabled,
    Info,
    Warning,
    Error
}

internal readonly record struct ModeFeedbackPresentation(
    string Title,
    string Detail,
    ModeFeedbackTone Tone)
{
    internal static ModeFeedbackPresentation ForUploadMode(bool uploadToDiscord) =>
        uploadToDiscord
            ? new(
                "DISCORD UPLOADS ON",
                "New clips will be sent automatically.",
                ModeFeedbackTone.UploadsEnabled)
            : new(
                "LOCAL ONLY ON",
                "New clips will stay on this PC.",
                ModeFeedbackTone.LocalOnlyEnabled);

    internal static ModeFeedbackPresentation DialogOpen => new(
        "Mode unchanged",
        "Close the open ClipCord dialog before using the mode shortcut.",
        ModeFeedbackTone.Info);

    internal static ModeFeedbackPresentation ReconfigurationInProgress => new(
        "Mode change in progress",
        "ClipCord is still finishing the previous settings change.",
        ModeFeedbackTone.Info);

    internal static ModeFeedbackPresentation DiscordSetupRequired => new(
        "Discord setup required",
        "Add a valid Discord webhook in Settings before enabling uploads.",
        ModeFeedbackTone.Warning);

    internal static ModeFeedbackPresentation SaveFailed => new(
        "Could not change upload mode",
        "ClipCord could not save the upload-mode setting.",
        ModeFeedbackTone.Error);
}

internal sealed class ModeFeedbackOverlay : Form
{
    internal const int DisplayDurationMilliseconds = 1500;
    internal const int RequiredExtendedStyles = WsExNoActivate | WsExToolWindow | WsExTransparent;

    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly System.Windows.Forms.Timer _dismissTimer;
    private ModeFeedbackPresentation _presentation;
    private int _presentationDpi = 96;
    private bool _disposed;

    internal ModeFeedbackOverlay()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ClipCordTheme.Header;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0.97d;
        Text = "ClipCord mode changed";
        AccessibleName = "ClipCord upload mode changed";
        SetStyle(ControlStyles.Selectable, false);

        _dismissTimer = new System.Windows.Forms.Timer
        {
            Interval = DisplayDurationMilliseconds
        };
        _dismissTimer.Tick += DismissTimerTick;

        // Establish thread ownership before any background caller can reach
        // ShowFeedback. InvokeRequired is unreliable while a control has no handle.
        _ = Handle;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= RequiredExtendedStyles;
            return parameters;
        }
    }

    internal void ShowFeedback(ModeFeedbackPresentation presentation)
    {
        if (_disposed) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke((Action)(() => ShowFeedback(presentation)));
            }
            catch (InvalidOperationException)
            {
                // Includes ObjectDisposedException if ClipCord exits while feedback is queued.
            }
            return;
        }

        _presentation = presentation;
        var foreground = GetForegroundWindow();
        var screen = foreground != IntPtr.Zero
            ? Screen.FromHandle(foreground)
            : Screen.PrimaryScreen ?? Screen.AllScreens[0];
        ApplyPresentation(presentation, screen.WorkingArea, GetPresentationDpi());

        if (!Visible) Show();
        _ = SetWindowPos(
            Handle,
            HwndTopmost,
            Left,
            Top,
            Width,
            Height,
            SwpNoActivate | SwpShowWindow);

        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    internal void ApplyPresentation(
        ModeFeedbackPresentation presentation,
        Rectangle workingArea,
        int dpi)
    {
        _presentation = presentation;
        _presentationDpi = Math.Clamp(dpi, 96, 384);
        Bounds = CalculateBounds(workingArea, _presentationDpi);
        UpdateWindowRegion();
        Invalidate();
    }

    internal static Rectangle CalculateBounds(Rectangle workingArea, int dpi)
    {
        var scale = Math.Clamp(dpi, 96, 384) / 96d;
        var edge = Math.Max(8, (int)Math.Round(18 * scale));
        var desiredWidth = (int)Math.Round(430 * scale);
        var desiredHeight = (int)Math.Round(104 * scale);
        var width = Math.Max(1, Math.Min(desiredWidth, workingArea.Width - edge * 2));
        var height = Math.Max(1, Math.Min(desiredHeight, workingArea.Height - edge * 2));
        var left = workingArea.Left + Math.Max(0, (workingArea.Width - width) / 2);
        var top = workingArea.Top + Math.Min(
            Math.Max(0, workingArea.Height - height),
            Math.Max(edge, (int)Math.Round(28 * scale)));
        return new Rectangle(left, top, width, height);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;

        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var scale = Math.Clamp(_presentationDpi, 96, 384) / 96f;
        var bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        var highContrast = SystemInformation.HighContrast;
        var accent = highContrast
            ? SystemColors.Highlight
            : GetAccentColor(_presentation.Tone);

        using (var surfacePath = RoundedPanel.CreateRoundedPath(bounds, Math.Max(12, (int)Math.Round(18 * scale))))
        using (var surface = new LinearGradientBrush(
                   bounds,
                   ClipCordTheme.Header,
                   Color.FromArgb(23, 31, 49),
                   LinearGradientMode.Horizontal))
        using (var border = new Pen(Color.FromArgb(84, accent), Math.Max(1f, scale)))
        {
            graphics.FillPath(surface, surfacePath);
            graphics.DrawPath(border, surfacePath);
        }

        var accentWidth = Math.Max(4, (int)Math.Round(5 * scale));
        using (var accentBrush = new SolidBrush(accent))
        {
            graphics.FillRectangle(accentBrush, 0, 0, accentWidth, Height);
        }

        var tileSize = Math.Max(44, (int)Math.Round(62 * scale));
        var tile = new Rectangle(
            Math.Max(14, (int)Math.Round(18 * scale)),
            Math.Max(10, (Height - tileSize) / 2),
            tileSize,
            tileSize);
        using (var tilePath = RoundedPanel.CreateRoundedPath(tile, Math.Max(10, (int)Math.Round(14 * scale))))
        using (var tileBrush = new LinearGradientBrush(
                   tile,
                   accent,
                   highContrast
                       ? accent
                       : Color.FromArgb(
                           Math.Min(255, accent.R + 22),
                           Math.Min(255, accent.G + 18),
                           Math.Min(255, accent.B + 22)),
                   45f))
        {
            graphics.FillPath(tileBrush, tilePath);
        }
        var glyph = GetGlyph(_presentation.Tone);
        var glyphColor = highContrast ? SystemColors.HighlightText : Color.White;
        if (glyph == BrandGlyph.DiscordDestination)
        {
            // Feature artwork already carries its own normalized inset. Give the
            // detailed Discord mark more of the notification tile so its eyes,
            // smile, and silhouette remain legible at 100% display scaling.
            BrandGlyphControl.DrawFeatureGlyph(
                graphics,
                Rectangle.Inflate(tile, -(int)Math.Round(8 * scale), -(int)Math.Round(8 * scale)),
                glyph,
                glyphColor);
        }
        else
        {
            BrandGlyphControl.DrawGlyph(
                graphics,
                Rectangle.Inflate(tile, -(int)Math.Round(14 * scale), -(int)Math.Round(14 * scale)),
                glyph,
                glyphColor,
                Math.Max(1.8f, 2.1f * scale));
        }

        var textLeft = tile.Right + Math.Max(14, (int)Math.Round(18 * scale));
        var textRight = Width - Math.Max(16, (int)Math.Round(20 * scale));
        var titleTop = Math.Max(10, (int)Math.Round(23 * scale));
        var titleHeight = Math.Max(24, (int)Math.Round(28 * scale));
        using var titleFont = CreatePixelFont(Math.Max(16, 18 * scale), FontStyle.Bold);
        using var detailFont = CreatePixelFont(Math.Max(12, 13 * scale), FontStyle.Regular);
        TextRenderer.DrawText(
            graphics,
            _presentation.Title,
            titleFont,
            new Rectangle(textLeft, titleTop, Math.Max(1, textRight - textLeft), titleHeight),
            ClipCordTheme.ShellText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(
            graphics,
            _presentation.Detail,
            detailFont,
            new Rectangle(
                textLeft,
                titleTop + titleHeight,
                Math.Max(1, textRight - textLeft),
                Math.Max(20, Height - titleTop - titleHeight - (int)Math.Round(13 * scale))),
            ClipCordTheme.ShellMutedText,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        UpdateWindowRegion();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = new IntPtr(HtTransparent);
            return;
        }
        if (message.Msg == WmMouseActivate)
        {
            message.Result = new IntPtr(MaNoActivate);
            return;
        }
        base.WndProc(ref message);
    }

    private void DismissTimerTick(object? sender, EventArgs eventArgs)
    {
        _dismissTimer.Stop();
        Hide();
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 1 || Height <= 1) return;
        using var path = RoundedPanel.CreateRoundedPath(
            new Rectangle(0, 0, Width - 1, Height - 1),
            Math.Max(12, (int)Math.Round(18 * Math.Clamp(_presentationDpi, 96, 384) / 96d)));
        var replacement = new Region(path);
        var previous = Region;
        Region = replacement;
        previous?.Dispose();
    }

    private static Color GetAccentColor(ModeFeedbackTone tone) => tone switch
    {
        ModeFeedbackTone.UploadsEnabled => ClipCordTheme.Violet,
        ModeFeedbackTone.LocalOnlyEnabled => ClipCordTheme.Coral,
        ModeFeedbackTone.Warning => Color.FromArgb(245, 174, 66),
        ModeFeedbackTone.Error => ClipCordTheme.Coral,
        _ => ClipCordTheme.Violet
    };

    internal static BrandGlyph GetGlyph(ModeFeedbackTone tone) => tone switch
    {
        ModeFeedbackTone.UploadsEnabled => BrandGlyph.DiscordDestination,
        ModeFeedbackTone.LocalOnlyEnabled => BrandGlyph.Shield,
        ModeFeedbackTone.Error => BrandGlyph.Close,
        ModeFeedbackTone.Warning => BrandGlyph.About,
        _ => BrandGlyph.Activity
    };

    private static Font CreatePixelFont(float pixels, FontStyle style)
    {
        var familyName = ClipCordTheme.InterfaceFont(10f).Name;
        return new Font(familyName, pixels, style, GraphicsUnit.Pixel);
    }

    private int GetPresentationDpi()
    {
        try
        {
            // ClipCord is system-DPI-aware. Measuring its own window keeps Bounds and
            // painting in the same coordinate system; Windows scales that window when
            // the foreground game is on a monitor with a different DPI.
            var dpi = GetDpiForWindow(Handle);
            if (dpi is >= 96 and <= 384) return (int)dpi;
        }
        catch (EntryPointNotFoundException)
        {
            // Windows versions supported by .NET 8 normally expose this API.
        }
        return Math.Clamp(DeviceDpi, 96, 384);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _dismissTimer.Stop();
            _dismissTimer.Tick -= DismissTimerTick;
            _dismissTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
