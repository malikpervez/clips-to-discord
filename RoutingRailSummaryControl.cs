using System.Drawing.Drawing2D;

namespace ClipsToDiscord;

/// <summary>
/// Compact, privacy-safe destination summary used by the 2.0 navigation rail. It draws only
/// approved Figma assets and consumes the same bounded projection as Home and About.
/// </summary>
internal sealed class RoutingRailSummaryControl : Control
{
    private RoutingUiPresentationSnapshot _snapshot;

    internal RoutingUiPresentationSnapshot Snapshot => _snapshot;

    internal RoutingRailSummaryControl()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        Name = "RailDestinationSummary";
        AccessibleName = "Routing destinations";
        AccessibleRole = AccessibleRole.StaticText;
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        TabStop = false;
    }

    internal void Apply(RoutingUiPresentationSnapshot snapshot)
    {
        _snapshot = snapshot.Normalize();
        Text = BuildAccessibleSummary(_snapshot);
        AccessibleDescription = Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Math.Max(1d, DeviceDpi / 96d);
        // R27/R29 use four 35 x 30 destination chips with 8 px gaps. Keeping those
        // optical bounds here matters: the smaller legacy-sized marks read as stray
        // status dots beside the full-size navigation rail.
        var chipWidth = Math.Max(24, (int)Math.Round(35 * scale));
        var chipHeight = Math.Max(22, (int)Math.Round(30 * scale));
        var gap = Math.Max(5, (int)Math.Round(8 * scale));
        var top = Math.Max(0, (ClientSize.Height - chipHeight) / 2);
        var items = new[]
        {
            (RoutingUiDestinations.Library, FigmaIconAsset.Disk, ClipCordTheme.TextSecondary),
            (RoutingUiDestinations.Discord, FigmaIconAsset.Discord, Color.FromArgb(139, 61, 255)),
            (RoutingUiDestinations.YouTube, FigmaIconAsset.YouTube, Color.FromArgb(255, 62, 62)),
            (RoutingUiDestinations.TikTok, FigmaIconAsset.TikTok, ClipCordTheme.TextPrimary)
        };

        var x = 0;
        foreach (var (destination, asset, color) in items)
        {
            if (x + chipWidth > ClientSize.Width) break;
            var configured = _snapshot.Destinations.HasFlag(destination);
            var held = _snapshot.LocalOnlyModeEnabled &&
                       destination != RoutingUiDestinations.Library;
            var bounds = new Rectangle(x, top, chipWidth, chipHeight);
            using var path = RoundedPanel.CreateRoundedPath(
                bounds,
                Math.Max(6, (int)Math.Round(7 * scale)));
            using var fill = new SolidBrush(configured
                ? held
                    ? Color.FromArgb(31, 35, 43)
                    : ClipCordTheme.SurfaceControl
                : ClipCordTheme.SurfaceSunken);
            using var border = new Pen(configured && !held
                ? ClipCordTheme.BorderStrong
                : ClipCordTheme.BorderDefault);
            eventArgs.Graphics.FillPath(fill, path);
            eventArgs.Graphics.DrawPath(border, path);
            var insetX = Math.Max(8, (int)Math.Round(10 * scale));
            var insetY = Math.Max(6, (int)Math.Round(7 * scale));
            FigmaIconRenderer.Draw(
                eventArgs.Graphics,
                new Rectangle(
                    bounds.Left + insetX,
                    bounds.Top + insetY,
                    Math.Max(1, bounds.Width - insetX * 2),
                    Math.Max(1, bounds.Height - insetY * 2)),
                asset,
                configured ? color : ClipCordTheme.TextTertiary,
                configured ? held ? 0.38f : 1f : 0.28f);
            x += chipWidth + gap;
        }
    }

    private static string BuildAccessibleSummary(RoutingUiPresentationSnapshot snapshot)
    {
        var destinations = new List<string>(4);
        if (snapshot.Destinations.HasFlag(RoutingUiDestinations.Library)) destinations.Add("Library");
        if (snapshot.Destinations.HasFlag(RoutingUiDestinations.Discord)) destinations.Add("Discord");
        if (snapshot.Destinations.HasFlag(RoutingUiDestinations.YouTube)) destinations.Add("YouTube");
        if (snapshot.Destinations.HasFlag(RoutingUiDestinations.TikTok)) destinations.Add("TikTok");
        var configured = destinations.Count == 0
            ? "No configured destinations"
            : string.Join(", ", destinations);
        return snapshot.LocalOnlyModeEnabled
            ? $"{configured}. External destinations are paused by Local-only mode."
            : configured;
    }
}
