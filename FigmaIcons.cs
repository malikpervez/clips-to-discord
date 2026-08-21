using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace ClipsToDiscord;

/// <summary>
/// Exact icon silhouettes exported from the approved ClipCord Figma screens. The source
/// SVGs live beside the embedded masks under assets/figma-icons so the shipped pixels and
/// their design source stay reviewable together.
/// </summary>
internal enum FigmaIconAsset
{
    Home,
    Settings,
    Activity,
    Gallery,
    About,
    Folder,
    ArrowRight,
    Upload,
    Clock,
    Film,
    ChevronRight,
    External,
    Refresh,
    Shield,
    Search,
    More,
    Play,
    Trim,
    Bolt,
    Check,
    Heart,
    HeartFill
}

internal static class FigmaIconRenderer
{
    private const string ResourcePrefix = "ClipsToDiscord.Assets.FigmaIcons.";
    private static readonly Dictionary<FigmaIconAsset, Lazy<Image>> Images = new();
    private static readonly object ImageGate = new();

    internal static bool TryGetBrandAsset(BrandGlyph glyph, out FigmaIconAsset asset)
    {
        asset = glyph switch
        {
            BrandGlyph.Home => FigmaIconAsset.Home,
            BrandGlyph.Settings => FigmaIconAsset.Settings,
            BrandGlyph.Activity => FigmaIconAsset.Activity,
            BrandGlyph.Gallery => FigmaIconAsset.Gallery,
            BrandGlyph.About => FigmaIconAsset.About,
            BrandGlyph.Folder or BrandGlyph.FolderOpen => FigmaIconAsset.Folder,
            BrandGlyph.Shield => FigmaIconAsset.Shield,
            BrandGlyph.AppStatus => FigmaIconAsset.Bolt,
            BrandGlyph.Diagnostics => FigmaIconAsset.About,
            BrandGlyph.Credits => FigmaIconAsset.Gallery,
            BrandGlyph.FileText or BrandGlyph.ReportProblem => FigmaIconAsset.External,
            BrandGlyph.Upload => FigmaIconAsset.Upload,
            BrandGlyph.Clock => FigmaIconAsset.Clock,
            BrandGlyph.Film => FigmaIconAsset.Film,
            BrandGlyph.ArrowRight => FigmaIconAsset.ArrowRight,
            BrandGlyph.ChevronRight => FigmaIconAsset.ChevronRight,
            BrandGlyph.External => FigmaIconAsset.External,
            BrandGlyph.Refresh => FigmaIconAsset.Refresh,
            BrandGlyph.Play => FigmaIconAsset.Play,
            BrandGlyph.Trim => FigmaIconAsset.Trim,
            BrandGlyph.Search => FigmaIconAsset.Search,
            BrandGlyph.More => FigmaIconAsset.More,
            BrandGlyph.Check => FigmaIconAsset.Check,
            _ => default
        };
        return glyph is BrandGlyph.Home or BrandGlyph.Settings or BrandGlyph.Activity or
            BrandGlyph.Gallery or BrandGlyph.About or BrandGlyph.Folder or BrandGlyph.FolderOpen or
            BrandGlyph.Shield or BrandGlyph.AppStatus or
            BrandGlyph.Diagnostics or BrandGlyph.Credits or BrandGlyph.FileText or
            BrandGlyph.ReportProblem or BrandGlyph.Upload or BrandGlyph.Clock or BrandGlyph.Film or
            BrandGlyph.ArrowRight or BrandGlyph.ChevronRight or BrandGlyph.External or
            BrandGlyph.Refresh or BrandGlyph.Play or BrandGlyph.Trim or BrandGlyph.Search or
            BrandGlyph.More or BrandGlyph.Check;
    }

    internal static void Draw(
        Graphics graphics,
        Rectangle bounds,
        FigmaIconAsset asset,
        Color color,
        float opacity = 1f)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (bounds.Width <= 0 || bounds.Height <= 0 || opacity <= 0f) return;

        var image = GetImage(asset);
        var state = graphics.Save();
        try
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            var matrix = new ColorMatrix([
                [0f, 0f, 0f, 0f, 0f],
                [0f, 0f, 0f, 0f, 0f],
                [0f, 0f, 0f, 0f, 0f],
                [0f, 0f, 0f, Math.Clamp(opacity, 0f, 1f), 0f],
                [color.R / 255f, color.G / 255f, color.B / 255f, 0f, 1f]
            ]);
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            graphics.DrawImage(
                image,
                bounds,
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attributes);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static Image GetImage(FigmaIconAsset asset)
    {
        Lazy<Image> lazy;
        lock (ImageGate)
        {
            if (!Images.TryGetValue(asset, out lazy!))
            {
                lazy = new Lazy<Image>(() => LoadImage(asset), LazyThreadSafetyMode.ExecutionAndPublication);
                Images.Add(asset, lazy);
            }
        }
        return lazy.Value;
    }

    private static Image LoadImage(FigmaIconAsset asset)
    {
        var fileName = asset switch
        {
            FigmaIconAsset.ArrowRight => "arrow-right",
            FigmaIconAsset.ChevronRight => "chevron-right",
            _ => asset.ToString().ToLowerInvariant()
        };
        var resourceName = ResourcePrefix + fileName + ".png";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Figma icon '{resourceName}' was not found.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }
}

internal sealed class FigmaIconControl : Control
{
    private FigmaIconAsset _asset;
    private Color _iconColor = ClipCordTheme.TextSecondary;

    internal FigmaIconAsset Asset
    {
        get => _asset;
        set
        {
            if (_asset == value) return;
            _asset = value;
            Invalidate();
        }
    }

    internal Color IconColor
    {
        get => _iconColor;
        set
        {
            if (_iconColor == value) return;
            _iconColor = value;
            Invalidate();
        }
    }

    internal FigmaIconControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.None;
        AccessibleName = string.Empty;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 0 || Height <= 0) return;
        FigmaIconRenderer.Draw(eventArgs.Graphics, ClientRectangle, Asset, IconColor);
    }
}
