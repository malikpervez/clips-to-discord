using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClipsToDiscord;

internal enum ModeFeedbackTone
{
    UploadsEnabled,
    LocalOnlyEnabled,
    CaptureRecording,
    CaptureSaved,
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
                "Discord uploads on",
                "New clips upload automatically.",
                ModeFeedbackTone.UploadsEnabled)
            : new(
                "Local only on",
                "New clips stay on this PC.",
                ModeFeedbackTone.LocalOnlyEnabled);

    internal static ModeFeedbackPresentation ForRoutingLocalOnlyMode(bool enabled) =>
        enabled
            ? new(
                "Local-only mode on",
                "Future clips will stay on this PC. Existing deliveries are unchanged.",
                ModeFeedbackTone.LocalOnlyEnabled)
            : new(
                "Normal routing restored",
                "Future clips will follow your active Routes.",
                ModeFeedbackTone.UploadsEnabled);

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
        "Add a webhook in Settings first.",
        ModeFeedbackTone.Warning);

    internal static ModeFeedbackPresentation SaveFailed => new(
        "Could not change upload mode",
        "ClipCord could not save the upload-mode setting.",
        ModeFeedbackTone.Error);

    internal static ModeFeedbackPresentation CaptureStarted => new(
        "Recording",
        "Capturing your game window.",
        ModeFeedbackTone.CaptureRecording);

    internal static ModeFeedbackPresentation ForCapturedClip(
        string? gameName,
        TimeSpan duration)
    {
        var safeGameName = string.IsNullOrWhiteSpace(gameName)
            ? "ClipCord"
            : gameName.Trim();
        var totalSeconds = Math.Max(0, (int)Math.Round(duration.TotalSeconds));
        var formattedDuration = $"{totalSeconds / 60}:{totalSeconds % 60:00}";
        return new(
            "Clip captured",
            $"{safeGameName} · {formattedDuration}",
            ModeFeedbackTone.CaptureSaved);
    }

    internal static ModeFeedbackPresentation CaptureFailed => new(
        "Recording stopped",
        "That recording could not be finished.",
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
    private readonly System.Windows.Forms.Timer _progressTimer;
    private ModeFeedbackPresentation _presentation;
    private int _presentationDpi = 96;
    private long _shownAtTimestamp;
    private float _remainingFraction = 1f;
    private bool _disposed;

    internal ModeFeedbackOverlay()
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ClipCordTheme.SurfaceRaised;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 1d;
        Text = "ClipCord notification";
        AccessibleName = "ClipCord notification";
        SetStyle(ControlStyles.Selectable, false);

        _dismissTimer = new System.Windows.Forms.Timer
        {
            Interval = DisplayDurationMilliseconds
        };
        _dismissTimer.Tick += DismissTimerTick;
        _progressTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _progressTimer.Tick += ProgressTimerTick;

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
        AccessibleName = $"{presentation.Title}. {presentation.Detail}";
        _shownAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _remainingFraction = 1f;
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
        _progressTimer.Stop();
        _progressTimer.Start();
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
        var desiredWidth = (int)Math.Round(384 * scale);
        var desiredHeight = (int)Math.Round(74 * scale);
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

        using (var surfacePath = RoundedPanel.CreateRoundedPath(bounds, ScalePixels(14)))
        using (var surface = new SolidBrush(highContrast
                   ? SystemColors.Window
                   : ClipCordTheme.SurfaceRaised))
        using (var border = new Pen(highContrast
                   ? SystemColors.WindowText
                   : ClipCordTheme.BorderDefault, Math.Max(1f, scale)))
        {
            graphics.FillPath(surface, surfacePath);
            graphics.DrawPath(border, surfacePath);
        }

        int textLeft;
        if (_presentation.Tone == ModeFeedbackTone.CaptureSaved)
        {
            var frameGrab = new Rectangle(
                ScalePixels(14),
                ScalePixels(15),
                ScalePixels(80),
                ScalePixels(45));
            using (var framePath = RoundedPanel.CreateRoundedPath(frameGrab, ScalePixels(8)))
            using (var frameBrush = new LinearGradientBrush(
                       frameGrab,
                       Color.FromArgb(36, 52, 83),
                       Color.FromArgb(17, 28, 46),
                       LinearGradientMode.Vertical))
            using (var frameBorder = new Pen(ClipCordTheme.BorderDefault, Math.Max(1f, scale)))
            {
                graphics.FillPath(frameBrush, framePath);
                graphics.DrawPath(frameBorder, framePath);
            }
            var filmSize = ScalePixels(18);
            FigmaIconRenderer.Draw(
                graphics,
                new Rectangle(
                    frameGrab.Left + (frameGrab.Width - filmSize) / 2,
                    frameGrab.Top + (frameGrab.Height - filmSize) / 2,
                    filmSize,
                    filmSize),
                FigmaIconAsset.Film,
                ClipCordTheme.TextTertiary,
                opacity: .75f);

            var badgeSize = ScalePixels(18);
            var badge = new Rectangle(
                frameGrab.Left + ScalePixels(65),
                frameGrab.Top + ScalePixels(30),
                badgeSize,
                badgeSize);
            using (var badgeBrush = new SolidBrush(accent))
            using (var badgeBorder = new Pen(ClipCordTheme.SurfaceRaised, Math.Max(2f, 2f * scale)))
            {
                graphics.FillEllipse(badgeBrush, badge);
                graphics.DrawEllipse(badgeBorder, badge);
            }
            FigmaIconRenderer.Draw(
                graphics,
                Rectangle.Inflate(badge, -ScalePixels(3), -ScalePixels(3)),
                FigmaIconAsset.Check,
                Color.FromArgb(10, 18, 32));
            textLeft = frameGrab.Right + ScalePixels(14);
        }
        else
        {
            var tile = new Rectangle(
                ScalePixels(14),
                ScalePixels(15),
                ScalePixels(44),
                ScalePixels(44));
            var (tileSurface, tileBorder) = GetTileColors(_presentation.Tone, highContrast);
            using (var tilePath = RoundedPanel.CreateRoundedPath(tile, ScalePixels(12)))
            using (var tileBrush = new SolidBrush(tileSurface))
            using (var tilePen = new Pen(tileBorder, Math.Max(1f, scale)))
            {
                graphics.FillPath(tileBrush, tilePath);
                graphics.DrawPath(tilePen, tilePath);
            }

            var iconBounds = Rectangle.Inflate(tile, -ScalePixels(10), -ScalePixels(10));
            var glyphColor = highContrast ? SystemColors.HighlightText : accent;
            FigmaIconRenderer.Draw(
                graphics,
                iconBounds,
                GetIconAsset(_presentation.Tone),
                glyphColor);
            textLeft = tile.Right + ScalePixels(14);
        }

        var textRight = Width - ScalePixels(16);
        using var titleFont = CreatePixelFont(16 * scale, FontStyle.Bold);
        using var detailFont = CreatePixelFont(13 * scale, FontStyle.Regular);
        TextRenderer.DrawText(
            graphics,
            _presentation.Title,
            titleFont,
            new Rectangle(textLeft, ScalePixels(15), Math.Max(1, textRight - textLeft), ScalePixels(21)),
            ClipCordTheme.ShellText,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(
            graphics,
            _presentation.Detail,
            detailFont,
            new Rectangle(textLeft, ScalePixels(39), Math.Max(1, textRight - textLeft), ScalePixels(18)),
            ClipCordTheme.ShellMutedText,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        var timerHeight = ScalePixels(3);
        var timerTop = Height - timerHeight;
        using (var trackBrush = new SolidBrush(Color.FromArgb(140, ClipCordTheme.BorderDefault)))
        using (var remainingBrush = new SolidBrush(accent))
        {
            graphics.FillRectangle(trackBrush, 0, timerTop, Width, timerHeight);
            graphics.FillRectangle(
                remainingBrush,
                0,
                timerTop,
                Math.Clamp((int)Math.Round(Width * _remainingFraction), 0, Width),
                timerHeight);
        }
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
        _progressTimer.Stop();
        _remainingFraction = 0f;
        Hide();
    }

    private void ProgressTimerTick(object? sender, EventArgs eventArgs)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_shownAtTimestamp);
        _remainingFraction = Math.Clamp(
            1f - (float)(elapsed.TotalMilliseconds / DisplayDurationMilliseconds),
            0f,
            1f);
        Invalidate(new Rectangle(0, Math.Max(0, Height - ScalePixels(4)), Width, ScalePixels(4)));
        if (_remainingFraction <= 0f) _progressTimer.Stop();
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 1 || Height <= 1) return;
        using var path = RoundedPanel.CreateRoundedPath(
            new Rectangle(0, 0, Width - 1, Height - 1),
            ScalePixels(14));
        var replacement = new Region(path);
        var previous = Region;
        Region = replacement;
        previous?.Dispose();
    }

    private static Color GetAccentColor(ModeFeedbackTone tone) => tone switch
    {
        ModeFeedbackTone.UploadsEnabled => ClipCordTheme.Violet,
        ModeFeedbackTone.LocalOnlyEnabled => ClipCordTheme.Coral,
        ModeFeedbackTone.CaptureRecording => ClipCordTheme.Coral,
        ModeFeedbackTone.CaptureSaved => Color.FromArgb(49, 177, 113),
        ModeFeedbackTone.Warning => Color.FromArgb(224, 151, 54),
        ModeFeedbackTone.Error => ClipCordTheme.Coral,
        _ => ClipCordTheme.Violet
    };

    internal static FigmaIconAsset GetIconAsset(ModeFeedbackTone tone) => tone switch
    {
        ModeFeedbackTone.UploadsEnabled => FigmaIconAsset.Discord,
        ModeFeedbackTone.LocalOnlyEnabled => FigmaIconAsset.Shield,
        ModeFeedbackTone.CaptureRecording => FigmaIconAsset.Capture,
        ModeFeedbackTone.CaptureSaved => FigmaIconAsset.Film,
        ModeFeedbackTone.Error or ModeFeedbackTone.Warning => FigmaIconAsset.Alert,
        _ => FigmaIconAsset.Activity
    };

    private static (Color Surface, Color Border) GetTileColors(
        ModeFeedbackTone tone,
        bool highContrast)
    {
        if (highContrast) return (SystemColors.Highlight, SystemColors.HighlightText);
        return tone switch
        {
            ModeFeedbackTone.UploadsEnabled or ModeFeedbackTone.Info =>
                (Color.FromArgb(48, 42, 74), Color.FromArgb(62, 42, 121)),
            ModeFeedbackTone.Warning =>
                (Color.FromArgb(58, 53, 50), Color.FromArgb(92, 73, 51)),
            _ => (Color.FromArgb(57, 37, 53), Color.FromArgb(92, 44, 56))
        };
    }

    private int ScalePixels(int logicalPixels) => Math.Max(
        1,
        (int)Math.Round(logicalPixels * Math.Clamp(_presentationDpi, 96, 384) / 96d));

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
            _progressTimer.Stop();
            _progressTimer.Tick -= ProgressTimerTick;
            _progressTimer.Dispose();
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
