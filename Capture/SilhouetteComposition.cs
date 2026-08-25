using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

internal static class CompositionOrientationIds
{
    internal const string Landscape = "landscape.16x9";
    internal const string Portrait = "portrait.9x16";

    internal static IReadOnlyList<string> All { get; } =
    [
        Landscape,
        Portrait
    ];
}

internal enum SilhouettePlacementPreset
{
    BottomRight,
    BottomLeft,
    RaisedBottomCenter,
    TopRight,
    TopLeft,
    Custom
}

internal enum SilhouetteOutlineStyle
{
    None,
    Soft,
    Strong
}

internal enum PortraitGameplayLayoutMode
{
    Context,
    FocusCrop,
    Custom
}

/// <summary>
/// Placement of the complete 16:9 gameplay frame inside a normalized 9:16 canvas. Height is
/// derived from WidthFraction so gameplay is never stretched.
/// </summary>
internal sealed record PortraitGameplayTransform(
    double WidthFraction,
    double CenterX,
    double TopY);

/// <summary>
/// A fixed portrait crop. FocalCenterX is normalized to the source gameplay frame; automatic
/// action tracking and keyframes are deliberately outside schema v2.
/// </summary>
internal sealed record PortraitFocusCrop(
    double Zoom,
    double FocalCenterX);

internal sealed record PortraitCompositionSettings(
    PortraitGameplayLayoutMode Layout,
    PortraitGameplayTransform Gameplay,
    PortraitFocusCrop FocusCrop);

/// <summary>
/// A resolution-independent silhouette placement in the destination canvas. AnchorX and
/// AnchorY identify the layer's bottom-center point; HeightFraction is relative to canvas
/// height. The renderer derives width from the stabilized matte bounds, so a resize never
/// stretches the person.
/// </summary>
internal sealed record SilhouetteTransform(
    bool Visible,
    double AnchorX,
    double AnchorY,
    double HeightFraction,
    bool MirrorHorizontally,
    SilhouetteOutlineStyle Outline,
    SilhouettePlacementPreset Preset);

internal sealed record SilhouetteLayoutVariant(
    string ProfileId,
    bool Enabled,
    SilhouetteTransform Transform);

internal readonly record struct NormalizedSilhouetteBounds(
    double Left,
    double Top,
    double Width,
    double Height)
{
    internal double Right => Left + Width;
    internal double Bottom => Top + Height;
}

/// <summary>
/// Mutable, non-destructive edit state stored separately from the immutable capture manifest.
/// No media paths belong here: the containing validated capture project owns the source layer.
/// </summary>
internal sealed record CaptureCompositionDocument(
    int SchemaVersion,
    string ProjectId,
    IReadOnlyList<SilhouetteLayoutVariant> SilhouetteLayouts,
    PortraitCompositionSettings PortraitComposition,
    DateTimeOffset ModifiedUtc);

internal static class CaptureCompositionModel
{
    internal const double MinimumHeightFraction = 0.10;
    internal const double MaximumHeightFraction = 0.75;
    internal const double MinimumPortraitGameplayWidthFraction = 0.40;
    internal const double MaximumPortraitGameplayWidthFraction = 1.00;
    internal const double MinimumFocusCropZoom = 1.00;
    internal const double MaximumFocusCropZoom = 4.00;
    internal const double DefaultPortraitGameplayWidthFraction = 800d / 1080d;
    internal const double DefaultPortraitGameplayCenterX = 0.50;
    internal const double DefaultPortraitGameplayTopY = 270d / 1920d;
    private const double PortraitCanvasAspectRatio = 9d / 16d;
    private const double LandscapeGameplayAspectRatio = 16d / 9d;

    internal static CaptureCompositionDocument CreateDefault(
        string projectId,
        bool mirrorCamera,
        DateTimeOffset? modifiedUtc = null)
    {
        ValidateProjectId(projectId);
        return new CaptureCompositionDocument(
            CaptureCompositionStore.CurrentSchemaVersion,
            projectId,
            CompositionOrientationIds.All
                .Select(profileId => new SilhouetteLayoutVariant(
                    profileId,
                    EnabledByDefault(profileId),
                    CreateDefaultTransform(profileId, mirrorCamera)))
                .ToArray(),
            CreateDefaultPortraitComposition(),
            (modifiedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    internal static CaptureCompositionDocument Normalize(
        CaptureCompositionDocument document,
        string expectedProjectId,
        bool mirrorCamera)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateProjectId(expectedProjectId);
        if (document.SchemaVersion != CaptureCompositionStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The capture composition schema is not supported.");
        }
        if (!string.Equals(document.ProjectId, expectedProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The capture composition belongs to a different project.");
        }
        if (document.SilhouetteLayouts is null)
        {
            throw new InvalidDataException("The capture composition layout list is missing.");
        }

        var supplied = new Dictionary<string, SilhouetteLayoutVariant>(StringComparer.Ordinal);
        foreach (var layout in document.SilhouetteLayouts)
        {
            if (layout is null || string.IsNullOrWhiteSpace(layout.ProfileId))
            {
                throw new InvalidDataException("A capture composition profile is invalid.");
            }
            var profileId = layout.ProfileId.Trim();
            if (!CompositionOrientationIds.All.Contains(profileId, StringComparer.Ordinal))
            {
                throw new InvalidDataException("A capture composition profile is not supported.");
            }
            if (!supplied.TryAdd(profileId, layout))
            {
                throw new InvalidDataException("A capture composition profile is duplicated.");
            }
        }

        var normalized = new List<SilhouetteLayoutVariant>(CompositionOrientationIds.All.Count);
        foreach (var profileId in CompositionOrientationIds.All)
        {
            var fallback = CreateDefaultTransform(profileId, mirrorCamera);
            if (!supplied.TryGetValue(profileId, out var layout) || layout.Transform is null)
            {
                normalized.Add(new SilhouetteLayoutVariant(
                    profileId,
                    EnabledByDefault(profileId),
                    fallback));
                continue;
            }

            normalized.Add(new SilhouetteLayoutVariant(
                profileId,
                layout.Enabled,
                NormalizeTransform(layout.Transform, fallback)));
        }

        // A composition document exists only for a project with a Reaction Camera layer.
        // Therefore a persisted "both off" state is never meaningful: repair hostile or
        // partially-written input to the conservative current-workflow default rather than
        // allowing capture to perform camera work with no requested rendition.
        if (!normalized.Any(layout => layout.Enabled))
        {
            var landscapeIndex = normalized.FindIndex(layout =>
                layout.ProfileId.Equals(
                    CompositionOrientationIds.Landscape,
                    StringComparison.Ordinal));
            normalized[landscapeIndex] = normalized[landscapeIndex] with { Enabled = true };
        }

        var modifiedUtc = document.ModifiedUtc == default
            ? DateTimeOffset.UnixEpoch
            : document.ModifiedUtc.ToUniversalTime();
        return document with
        {
            ProjectId = expectedProjectId,
            SilhouetteLayouts = normalized,
            PortraitComposition = NormalizePortraitComposition(document.PortraitComposition),
            ModifiedUtc = modifiedUtc
        };
    }

    internal static PortraitCompositionSettings CreateDefaultPortraitComposition() => new(
        PortraitGameplayLayoutMode.Context,
        new PortraitGameplayTransform(
            DefaultPortraitGameplayWidthFraction,
            DefaultPortraitGameplayCenterX,
            DefaultPortraitGameplayTopY),
        new PortraitFocusCrop(
            Zoom: MinimumFocusCropZoom,
            FocalCenterX: 0.50));

    internal static PortraitCompositionSettings NormalizePortraitComposition(
        PortraitCompositionSettings? composition)
    {
        var fallback = CreateDefaultPortraitComposition();
        if (composition is null) return fallback;

        var mode = Enum.IsDefined(composition.Layout)
            ? composition.Layout
            : PortraitGameplayLayoutMode.Context;
        var suppliedGameplay = composition.Gameplay ?? fallback.Gameplay;
        var width = NormalizeFinite(
            suppliedGameplay.WidthFraction,
            fallback.Gameplay.WidthFraction,
            MinimumPortraitGameplayWidthFraction,
            MaximumPortraitGameplayWidthFraction);
        var height = width * PortraitCanvasAspectRatio / LandscapeGameplayAspectRatio;
        var centerX = NormalizeFinite(
            suppliedGameplay.CenterX,
            fallback.Gameplay.CenterX,
            width / 2,
            1 - width / 2);
        var topY = NormalizeFinite(
            suppliedGameplay.TopY,
            fallback.Gameplay.TopY,
            0,
            1 - height);

        var suppliedCrop = composition.FocusCrop ?? fallback.FocusCrop;
        var zoom = NormalizeFinite(
            suppliedCrop.Zoom,
            fallback.FocusCrop.Zoom,
            MinimumFocusCropZoom,
            MaximumFocusCropZoom);
        var visibleSourceWidth = PortraitCanvasAspectRatio /
                                 LandscapeGameplayAspectRatio /
                                 zoom;
        var focalCenterX = NormalizeFinite(
            suppliedCrop.FocalCenterX,
            fallback.FocusCrop.FocalCenterX,
            visibleSourceWidth / 2,
            1 - visibleSourceWidth / 2);
        return new PortraitCompositionSettings(
            mode,
            new PortraitGameplayTransform(width, centerX, topY),
            new PortraitFocusCrop(zoom, focalCenterX));
    }

    internal static SilhouetteTransform CreateDefaultTransform(
        string profileId,
        bool mirrorCamera)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return profileId switch
        {
            CompositionOrientationIds.Landscape => new SilhouetteTransform(
                    Visible: true,
                    AnchorX: 0.76,
                    AnchorY: 0.96,
                    HeightFraction: 0.40,
                    MirrorHorizontally: mirrorCamera,
                    Outline: SilhouetteOutlineStyle.Soft,
                    Preset: SilhouettePlacementPreset.BottomRight),
            CompositionOrientationIds.Portrait => new SilhouetteTransform(
                Visible: true,
                AnchorX: 0.50,
                AnchorY: 0.745,
                HeightFraction: 0.34,
                MirrorHorizontally: mirrorCamera,
                Outline: SilhouetteOutlineStyle.Soft,
                Preset: SilhouettePlacementPreset.RaisedBottomCenter),
            _ => throw new ArgumentException(
                "The capture composition profile is not supported.",
                nameof(profileId))
        };
    }

    internal static CaptureCompositionDocument SetOutputEnabled(
        CaptureCompositionDocument document,
        string profileId,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!CompositionOrientationIds.All.Contains(profileId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The capture composition profile is not supported.",
                nameof(profileId));
        }

        var layouts = document.SilhouetteLayouts?.ToArray()
            ?? throw new InvalidDataException("The capture composition layout list is missing.");
        var index = Array.FindIndex(layouts, layout =>
            layout is not null &&
            layout.ProfileId.Equals(profileId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidDataException("The capture composition profile is missing.");
        }
        if (!enabled && !layouts.Where((_, candidateIndex) => candidateIndex != index)
                .Any(layout => layout is not null && layout.Enabled))
        {
            throw new InvalidOperationException(
                "At least one silhouette output must remain enabled.");
        }

        layouts[index] = layouts[index] with { Enabled = enabled };
        return document with { SilhouetteLayouts = layouts };
    }

    /// <summary>
    /// Resolves a persisted bottom-center transform against the measured, stabilized matte
    /// aspect ratio and the selected destination canvas. This is the final containment gate
    /// for preview and export: a custom drag may touch any edge, but no rendition may place
    /// part of the silhouette outside its canvas or stretch its aspect ratio.
    /// </summary>
    internal static (SilhouetteTransform Transform, NormalizedSilhouetteBounds Bounds)
        FitToCanvas(
            SilhouetteTransform transform,
            double layerAspectRatio,
            double canvasAspectRatio)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!double.IsFinite(layerAspectRatio) || layerAspectRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layerAspectRatio));
        }
        if (!double.IsFinite(canvasAspectRatio) || canvasAspectRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasAspectRatio));
        }

        var height = double.IsFinite(transform.HeightFraction)
            ? Math.Clamp(transform.HeightFraction, MinimumHeightFraction, MaximumHeightFraction)
            : MinimumHeightFraction;
        var width = height * layerAspectRatio / canvasAspectRatio;
        if (width > 1)
        {
            height /= width;
            width = 1;
        }

        var halfWidth = width / 2;
        var anchorX = Math.Clamp(
            double.IsFinite(transform.AnchorX) ? transform.AnchorX : 0.5,
            halfWidth,
            1 - halfWidth);
        var anchorY = Math.Clamp(
            double.IsFinite(transform.AnchorY) ? transform.AnchorY : 1,
            height,
            1);
        var fitted = transform with
        {
            AnchorX = anchorX,
            AnchorY = anchorY,
            HeightFraction = height
        };
        return (
            fitted,
            new NormalizedSilhouetteBounds(
                anchorX - halfWidth,
                anchorY - height,
                width,
                height));
    }

    private static double NormalizeFinite(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static bool EnabledByDefault(string profileId) =>
        profileId.Equals(CompositionOrientationIds.Landscape, StringComparison.Ordinal);

    internal static SilhouetteTransform NormalizeTransform(
        SilhouetteTransform transform,
        SilhouetteTransform fallback)
    {
        var height = NormalizeFinite(
            transform.HeightFraction,
            fallback.HeightFraction,
            MinimumHeightFraction,
            MaximumHeightFraction);
        var anchorX = NormalizeFinite(transform.AnchorX, fallback.AnchorX, 0, 1);
        // AnchorY is the layer's bottom edge. Keeping it at least one layer-height from
        // the top prevents a persisted transform from placing the entire subject outside
        // the canvas before the renderer knows the matte's horizontal aspect.
        var anchorY = NormalizeFinite(transform.AnchorY, fallback.AnchorY, height, 1);
        var outline = Enum.IsDefined(transform.Outline)
            ? transform.Outline
            : fallback.Outline;
        var preset = Enum.IsDefined(transform.Preset)
            ? transform.Preset
            : SilhouettePlacementPreset.Custom;
        return transform with
        {
            AnchorX = anchorX,
            AnchorY = anchorY,
            HeightFraction = height,
            Outline = outline,
            Preset = preset
        };
    }

    private static void ValidateProjectId(string projectId)
    {
        // CaptureProjectStore owns the canonical project-id validation. Calling its path
        // helper avoids a second subtly different definition of the security boundary.
        _ = CaptureProjectStore.GetProjectDirectory(".", projectId);
    }
}

internal enum CaptureCompositionLoadStatus
{
    Missing,
    Loaded,
    Migrated,
    Corrupt,
    UnsupportedSchema,
    Invalid,
    Unavailable
}

internal sealed record CaptureCompositionLoadResult(
    CaptureCompositionDocument Document,
    CaptureCompositionLoadStatus Status)
{
    internal bool LoadedFromDisk =>
        Status is CaptureCompositionLoadStatus.Loaded or CaptureCompositionLoadStatus.Migrated;
}

internal static class CaptureCompositionStore
{
    internal const int LegacyDestinationSchemaVersion = 1;
    internal const int CurrentSchemaVersion = 2;
    internal const string FileName = "composition.json";
    internal const int MaximumDocumentBytes = 128 * 1024;
    private const string SaveMutexPrefix = @"Local\ClipCord.CaptureComposition.";
    private const string LegacyDiscordLandscape = "discord.landscape.16x9";
    private const string LegacyYouTubeLandscape = "youtube.landscape.16x9";
    private const string LegacyYouTubeShortsPortrait = "youtube-shorts.portrait.9x16";
    private const string LegacyTikTokPortrait = "tiktok.portrait.9x16";

    private static IReadOnlyList<string> LegacyDestinationProfileIds { get; } =
    [
        LegacyDiscordLandscape,
        LegacyYouTubeLandscape,
        LegacyYouTubeShortsPortrait,
        LegacyTikTokPortrait
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string GetPath(string libraryRoot, string projectId) =>
        Path.Combine(CaptureProjectStore.GetProjectDirectory(libraryRoot, projectId), FileName);

    internal static CaptureCompositionLoadResult LoadOrDefault(
        string libraryRoot,
        string projectId,
        DateTimeOffset? fallbackModifiedUtc = null)
    {
        var project = ValidateProject(libraryRoot, projectId);
        var fallback = CaptureCompositionModel.CreateDefault(
            projectId,
            project.Manifest.MirrorCamera,
            fallbackModifiedUtc);
        var path = Path.Combine(project.Directory, FileName);
        if (!File.Exists(path))
        {
            return new CaptureCompositionLoadResult(
                fallback,
                CaptureCompositionLoadStatus.Missing);
        }

        try
        {
            EnsureOrdinaryExistingPath(project.Root, path, requireDirectory: false, "capture composition file");
            var json = CaptureProjectStore.ReadBoundedUtf8File(
                path,
                MaximumDocumentBytes,
                "capture composition file");
            int schemaVersion;
            using (var parsed = JsonDocument.Parse(json))
            {
                if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                    !schemaElement.TryGetInt32(out schemaVersion))
                {
                    return new CaptureCompositionLoadResult(
                        fallback,
                        CaptureCompositionLoadStatus.Invalid);
                }
                if (schemaVersion is not CurrentSchemaVersion and
                    not LegacyDestinationSchemaVersion)
                {
                    return new CaptureCompositionLoadResult(
                        fallback,
                        CaptureCompositionLoadStatus.UnsupportedSchema);
                }
            }

            if (schemaVersion == LegacyDestinationSchemaVersion)
            {
                var legacyDocument = JsonSerializer.Deserialize<LegacyCaptureCompositionDocument>(
                    json,
                    JsonOptions);
                if (legacyDocument is null)
                {
                    return new CaptureCompositionLoadResult(
                        fallback,
                        CaptureCompositionLoadStatus.Corrupt);
                }
                var migrated = MigrateLegacyDestinationDocument(
                    legacyDocument,
                    projectId,
                    project.Manifest.MirrorCamera);
                return new CaptureCompositionLoadResult(
                    migrated,
                    CaptureCompositionLoadStatus.Migrated);
            }

            var document = JsonSerializer.Deserialize<CaptureCompositionDocument>(json, JsonOptions);
            if (document is null)
            {
                return new CaptureCompositionLoadResult(
                    fallback,
                    CaptureCompositionLoadStatus.Corrupt);
            }
            var normalized = CaptureCompositionModel.Normalize(
                document,
                projectId,
                project.Manifest.MirrorCamera);
            return new CaptureCompositionLoadResult(
                normalized,
                CaptureCompositionLoadStatus.Loaded);
        }
        catch (JsonException)
        {
            return new CaptureCompositionLoadResult(
                fallback,
                CaptureCompositionLoadStatus.Corrupt);
        }
        catch (InvalidDataException)
        {
            return new CaptureCompositionLoadResult(
                fallback,
                CaptureCompositionLoadStatus.Invalid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new CaptureCompositionLoadResult(
                fallback,
                CaptureCompositionLoadStatus.Unavailable);
        }
    }

    internal static Task<CaptureCompositionDocument> SaveAsync(
        string libraryRoot,
        string projectId,
        CaptureCompositionDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveCore(libraryRoot, projectId, document, cancellationToken),
            cancellationToken);
    }

    private static CaptureCompositionDocument SaveCore(
        string libraryRoot,
        string projectId,
        CaptureCompositionDocument document,
        CancellationToken cancellationToken)
    {
        var project = ValidateProject(libraryRoot, projectId);
        var normalized = CaptureCompositionModel.Normalize(
            document,
            projectId,
            project.Manifest.MirrorCamera);
        cancellationToken.ThrowIfCancellationRequested();

        using var saveMutex = new Mutex(
            initiallyOwned: false,
            GetSaveMutexName(projectId));
        var lockTaken = false;
        try
        {
            while (!lockTaken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lockTaken = saveMutex.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
            }

            var path = Path.Combine(project.Directory, FileName);
            var replacingExisting = File.Exists(path);
            if (replacingExisting)
            {
                EnsureOrdinaryExistingPath(
                    project.Root,
                    path,
                    requireDirectory: false,
                    "capture composition file");
                EnsureExistingDocumentCanBeReplaced(project, path, projectId);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
            if (bytes.Length <= 0 || bytes.Length > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The capture composition document is too large.");
            }

            var temporaryPath = Path.Combine(
                project.Directory,
                $".{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                // If the destination was absent when this lock was acquired, never replace a
                // file that appeared unexpectedly. Existing files have already passed the
                // current-schema and project-identity checks while holding the same mutex.
                File.Move(temporaryPath, path, overwrite: replacingExisting);
                return normalized;
            }
            finally
            {
                TryDeleteOwnedTemporaryFile(project.Directory, temporaryPath);
            }
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    internal static string GetSaveMutexName(string projectId) => SaveMutexPrefix + projectId;

    private static ValidatedCaptureProject ValidateProject(string libraryRoot, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot.Trim()));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The ClipCord library folder does not exist.");
        }
        EnsureOrdinaryExistingPath(root, root, requireDirectory: true, "ClipCord library folder");

        var projectDirectory = Path.GetFullPath(
            CaptureProjectStore.GetProjectDirectory(root, projectId));
        EnsurePathIsInside(root, projectDirectory, "capture project folder");
        EnsureOrdinaryExistingPath(root, projectDirectory, requireDirectory: true, "capture project folder");

        var manifestPath = Path.Combine(projectDirectory, CaptureProjectStore.ManifestFileName);
        EnsureOrdinaryExistingPath(root, manifestPath, requireDirectory: false, "capture project manifest");
        CaptureProjectManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CaptureProjectManifest>(
                           CaptureProjectStore.ReadBoundedManifestFile(manifestPath),
                           ManifestJsonOptions)
                       ?? throw new InvalidDataException("The capture project manifest is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The capture project manifest is invalid.", exception);
        }
        // Project ids are path-derived, so validating paths alone would let bytes replaced at
        // the same location inherit an older camera layer. Every composition read/write goes
        // through the same schema-v2 SHA-256 identity gate used by Gallery.
        _ = CaptureProjectStore.ValidateCommittedMediaIdentity(root, projectId, manifest);
        return new ValidatedCaptureProject(root, projectDirectory, manifest);
    }

    private static void EnsureExistingDocumentCanBeReplaced(
        ValidatedCaptureProject project,
        string path,
        string projectId)
    {
        try
        {
            var json = CaptureProjectStore.ReadBoundedUtf8File(
                path,
                MaximumDocumentBytes,
                "capture composition file");
            int schemaVersion;
            using (var parsed = JsonDocument.Parse(json))
            {
                if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                    !schemaElement.TryGetInt32(out schemaVersion) ||
                    schemaVersion is not CurrentSchemaVersion and
                        not LegacyDestinationSchemaVersion)
                {
                    throw new InvalidDataException(
                        "The existing capture composition cannot be replaced by this version of ClipCord.");
                }
            }

            if (schemaVersion == LegacyDestinationSchemaVersion)
            {
                var legacy = JsonSerializer.Deserialize<LegacyCaptureCompositionDocument>(
                                 json,
                                 JsonOptions)
                             ?? throw new InvalidDataException(
                                 "The existing capture composition is invalid.");
                _ = MigrateLegacyDestinationDocument(
                    legacy,
                    projectId,
                    project.Manifest.MirrorCamera);
            }
            else
            {
                var existing = JsonSerializer.Deserialize<CaptureCompositionDocument>(
                                   json,
                                   JsonOptions)
                               ?? throw new InvalidDataException(
                                   "The existing capture composition is invalid.");
                _ = CaptureCompositionModel.Normalize(
                    existing,
                    projectId,
                    project.Manifest.MirrorCamera);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The existing capture composition cannot be safely replaced.",
                exception);
        }
    }

    /// <summary>
    /// Migrates the original four destination-owned layouts to the two canvas orientations.
    /// Migration is deliberately in-memory only; LoadOrDefault never rewrites evidence. The
    /// next explicit save may atomically replace a validated v1 document with schema v2.
    /// If exactly one legacy destination was customized, that edit wins. If both destinations
    /// for an orientation were customized differently, the currently supported Discord flow
    /// wins for landscape and TikTok wins for portrait. Untouched legacy defaults adopt the
    /// approved v2 orientation default instead of carrying destination-specific drift forward.
    /// </summary>
    private static CaptureCompositionDocument MigrateLegacyDestinationDocument(
        LegacyCaptureCompositionDocument legacy,
        string expectedProjectId,
        bool mirrorCamera)
    {
        if (legacy.SchemaVersion != LegacyDestinationSchemaVersion)
        {
            throw new InvalidDataException("The legacy capture composition schema is invalid.");
        }
        if (!string.Equals(legacy.ProjectId, expectedProjectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The capture composition belongs to a different project.");
        }
        if (legacy.SilhouetteLayouts is null)
        {
            throw new InvalidDataException("The capture composition layout list is missing.");
        }

        var supplied = new Dictionary<string, LegacySilhouetteLayoutVariant>(StringComparer.Ordinal);
        foreach (var layout in legacy.SilhouetteLayouts)
        {
            if (layout is null || string.IsNullOrWhiteSpace(layout.ProfileId))
            {
                throw new InvalidDataException("A legacy capture composition profile is invalid.");
            }
            var profileId = layout.ProfileId.Trim();
            if (!LegacyDestinationProfileIds.Contains(profileId, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "A legacy capture composition profile is not supported.");
            }
            if (!supplied.TryAdd(profileId, layout))
            {
                throw new InvalidDataException("A legacy capture composition profile is duplicated.");
            }
        }

        var landscape = SelectLegacyTransform(
            supplied,
            preferredProfileId: LegacyDiscordLandscape,
            alternateProfileId: LegacyYouTubeLandscape,
            approvedDefault: CaptureCompositionModel.CreateDefaultTransform(
                CompositionOrientationIds.Landscape,
                mirrorCamera),
            mirrorCamera);
        var portrait = SelectLegacyTransform(
            supplied,
            preferredProfileId: LegacyTikTokPortrait,
            alternateProfileId: LegacyYouTubeShortsPortrait,
            approvedDefault: CaptureCompositionModel.CreateDefaultTransform(
                CompositionOrientationIds.Portrait,
                mirrorCamera),
            mirrorCamera);

        return CaptureCompositionModel.Normalize(
            new CaptureCompositionDocument(
                CurrentSchemaVersion,
                expectedProjectId,
                [
                    new SilhouetteLayoutVariant(
                        CompositionOrientationIds.Landscape,
                        Enabled: true,
                        Transform: landscape),
                    new SilhouetteLayoutVariant(
                        CompositionOrientationIds.Portrait,
                        Enabled: false,
                        Transform: portrait)
                ],
                CaptureCompositionModel.CreateDefaultPortraitComposition(),
                legacy.ModifiedUtc),
            expectedProjectId,
            mirrorCamera);
    }

    private static SilhouetteTransform SelectLegacyTransform(
        IReadOnlyDictionary<string, LegacySilhouetteLayoutVariant> supplied,
        string preferredProfileId,
        string alternateProfileId,
        SilhouetteTransform approvedDefault,
        bool mirrorCamera)
    {
        var preferred = ReadLegacyTransform(
            supplied,
            preferredProfileId,
            mirrorCamera);
        var alternate = ReadLegacyTransform(
            supplied,
            alternateProfileId,
            mirrorCamera);

        if (preferred.WasCustomized && !alternate.WasCustomized)
        {
            return preferred.Transform;
        }
        if (alternate.WasCustomized && !preferred.WasCustomized)
        {
            return alternate.Transform;
        }
        if (preferred.WasCustomized && alternate.WasCustomized)
        {
            return preferred.Transform;
        }
        return approvedDefault;
    }

    private static LegacyTransformCandidate ReadLegacyTransform(
        IReadOnlyDictionary<string, LegacySilhouetteLayoutVariant> supplied,
        string profileId,
        bool mirrorCamera)
    {
        var legacyDefault = CreateLegacyDefaultTransform(profileId, mirrorCamera);
        if (!supplied.TryGetValue(profileId, out var layout) || layout.Transform is null)
        {
            return new LegacyTransformCandidate(legacyDefault, WasCustomized: false);
        }
        var normalized = CaptureCompositionModel.NormalizeTransform(
            layout.Transform,
            legacyDefault);
        return new LegacyTransformCandidate(
            normalized,
            WasCustomized: normalized != legacyDefault);
    }

    private static SilhouetteTransform CreateLegacyDefaultTransform(
        string profileId,
        bool mirrorCamera) => profileId switch
        {
            LegacyDiscordLandscape or LegacyYouTubeLandscape => new SilhouetteTransform(
                Visible: true,
                AnchorX: 0.76,
                AnchorY: 0.96,
                HeightFraction: 0.40,
                MirrorHorizontally: mirrorCamera,
                Outline: SilhouetteOutlineStyle.Soft,
                Preset: SilhouettePlacementPreset.BottomRight),
            LegacyYouTubeShortsPortrait => new SilhouetteTransform(
                Visible: true,
                AnchorX: 0.50,
                AnchorY: 0.82,
                HeightFraction: 0.34,
                MirrorHorizontally: mirrorCamera,
                Outline: SilhouetteOutlineStyle.Soft,
                Preset: SilhouettePlacementPreset.RaisedBottomCenter),
            LegacyTikTokPortrait => new SilhouetteTransform(
                Visible: true,
                AnchorX: 0.50,
                AnchorY: 0.80,
                HeightFraction: 0.34,
                MirrorHorizontally: mirrorCamera,
                Outline: SilhouetteOutlineStyle.Soft,
                Preset: SilhouettePlacementPreset.RaisedBottomCenter),
            _ => throw new InvalidDataException(
                "A legacy capture composition profile is not supported.")
        };

    private static void EnsureOrdinaryExistingPath(
        string allowedRoot,
        string candidate,
        bool requireDirectory,
        string description)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            EnsurePathIsInside(normalizedRoot, normalizedCandidate, description);
        }
        EnsureNotReparsePoint(normalizedRoot, description);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        var current = normalizedRoot;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                throw new IOException($"The {description} does not exist.");
            }
            EnsureNotReparsePoint(current, description);
        }
        if (requireDirectory ? !Directory.Exists(normalizedCandidate) : !File.Exists(normalizedCandidate))
        {
            throw new IOException($"The {description} has an unexpected file-system type.");
        }
    }

    private static void EnsurePathIsInside(string allowedRoot, string candidate, string description)
    {
        var relative = Path.GetRelativePath(allowedRoot, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} must remain inside ClipCord's library.");
        }
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {description} cannot be a symbolic link or junction.");
        }
    }

    private static void TryDeleteOwnedTemporaryFile(string projectDirectory, string temporaryPath)
    {
        try
        {
            var expectedPrefix = $".{FileName}.";
            var file = new FileInfo(temporaryPath);
            if (!file.Exists ||
                file.Directory is null ||
                !file.Directory.FullName.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase) ||
                !file.Name.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                !file.Name.EndsWith(".tmp", StringComparison.Ordinal) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }
            file.Delete();
        }
        catch
        {
            // Best-effort cleanup is restricted to the exact owned temporary filename.
        }
    }

    private sealed record LegacyCaptureCompositionDocument(
        int SchemaVersion,
        string ProjectId,
        IReadOnlyList<LegacySilhouetteLayoutVariant?>? SilhouetteLayouts,
        DateTimeOffset ModifiedUtc);

    private sealed record LegacySilhouetteLayoutVariant(
        string ProfileId,
        SilhouetteTransform? Transform);

    private readonly record struct LegacyTransformCandidate(
        SilhouetteTransform Transform,
        bool WasCustomized);

    private sealed record ValidatedCaptureProject(
        string Root,
        string Directory,
        CaptureProjectManifest Manifest);
}
