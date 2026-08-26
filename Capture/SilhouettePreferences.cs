using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipsToDiscord;

/// <summary>
/// Reusable placement defaults live outside capture projects. They contain no source paths
/// and no project identity; capture snapshots them into a new CaptureCompositionDocument so
/// later preference changes never move the silhouette in an existing clip.
/// </summary>
internal sealed record SilhouettePreferencesDocument(
    int SchemaVersion,
    IReadOnlyList<SilhouetteLayoutPreference> Layouts,
    PortraitCompositionSettings PortraitComposition,
    DateTimeOffset ModifiedUtc);

internal sealed record SilhouetteLayoutPreference(
    string OrientationId,
    SilhouetteTransform Transform);

internal static class SilhouettePreferencesModel
{
    internal static SilhouettePreferencesDocument CreateDefault(
        bool mirrorCamera,
        DateTimeOffset? modifiedUtc = null) => new(
        SilhouettePreferencesStore.CurrentSchemaVersion,
        CompositionOrientationIds.All
            .Select(orientationId => new SilhouetteLayoutPreference(
                orientationId,
                CaptureCompositionModel.CreateDefaultTransform(orientationId, mirrorCamera)))
            .ToArray(),
        CaptureCompositionModel.CreateDefaultPortraitComposition(),
        (modifiedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());

    internal static SilhouettePreferencesDocument Normalize(
        SilhouettePreferencesDocument document,
        bool mirrorCamera)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SilhouettePreferencesStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The silhouette preferences schema is not supported.");
        }
        if (document.Layouts is null)
        {
            throw new InvalidDataException("The silhouette preference layout list is missing.");
        }

        var supplied = new Dictionary<string, SilhouetteLayoutPreference>(StringComparer.Ordinal);
        foreach (var layout in document.Layouts)
        {
            if (layout is null || string.IsNullOrWhiteSpace(layout.OrientationId))
            {
                throw new InvalidDataException("A silhouette preference is invalid.");
            }
            var orientationId = layout.OrientationId.Trim();
            if (!CompositionOrientationIds.All.Contains(orientationId, StringComparer.Ordinal))
            {
                throw new InvalidDataException("A silhouette preference orientation is not supported.");
            }
            if (!supplied.TryAdd(orientationId, layout))
            {
                throw new InvalidDataException("A silhouette preference orientation is duplicated.");
            }
        }

        var normalized = CompositionOrientationIds.All
            .Select(orientationId =>
            {
                var fallback = CaptureCompositionModel.CreateDefaultTransform(
                    orientationId,
                    mirrorCamera);
                return new SilhouetteLayoutPreference(
                    orientationId,
                    supplied.TryGetValue(orientationId, out var layout) && layout.Transform is not null
                        ? CaptureCompositionModel.NormalizeTransform(layout.Transform, fallback)
                        : fallback);
            })
            .ToArray();
        return document with
        {
            Layouts = normalized,
            PortraitComposition = CaptureCompositionModel.NormalizePortraitComposition(
                document.PortraitComposition),
            ModifiedUtc = document.ModifiedUtc == default
                ? DateTimeOffset.UnixEpoch
                : document.ModifiedUtc.ToUniversalTime()
        };
    }

    internal static CaptureCompositionDocument CreateProjectSnapshot(
        string projectId,
        SilhouettePreferencesDocument preferences,
        bool landscapeEnabled,
        bool portraitEnabled,
        bool mirrorCamera,
        DateTimeOffset? modifiedUtc = null)
    {
        var normalized = Normalize(preferences, mirrorCamera);
        if (!landscapeEnabled && !portraitEnabled) landscapeEnabled = true;
        var layouts = normalized.Layouts
            .Select(layout => new SilhouetteLayoutVariant(
                layout.OrientationId,
                layout.OrientationId.Equals(
                    CompositionOrientationIds.Landscape,
                    StringComparison.Ordinal)
                    ? landscapeEnabled
                    : portraitEnabled,
                layout.Transform))
            .ToArray();
        return CaptureCompositionModel.Normalize(
            new CaptureCompositionDocument(
                CaptureCompositionStore.CurrentSchemaVersion,
                projectId,
                layouts,
                normalized.PortraitComposition,
                (modifiedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()),
            projectId,
            mirrorCamera);
    }
}

internal enum SilhouettePreferencesLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
    UnsupportedSchema,
    Invalid,
    Unavailable
}

internal sealed record SilhouettePreferencesLoadResult(
    SilhouettePreferencesDocument Document,
    SilhouettePreferencesLoadStatus Status)
{
    internal bool LoadedFromDisk => Status == SilhouettePreferencesLoadStatus.Loaded;
}

internal static class SilhouettePreferencesStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const string FileName = "silhouette-preferences.json";
    internal const int MaximumDocumentBytes = 64 * 1024;
    private const string SaveMutexName = @"Local\ClipCord.SilhouettePreferences";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static string GetPath(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        return Path.Combine(Path.GetFullPath(settingsDirectory.Trim()), FileName);
    }

    internal static SilhouettePreferencesLoadResult LoadOrDefault(
        string settingsDirectory,
        bool mirrorCamera,
        DateTimeOffset? fallbackModifiedUtc = null)
    {
        var fallback = SilhouettePreferencesModel.CreateDefault(
            mirrorCamera,
            fallbackModifiedUtc);
        try
        {
            var path = GetPath(settingsDirectory);
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidDataException("The silhouette preference folder is invalid.");
            if (!Directory.Exists(directory) || !File.Exists(path))
            {
                return new SilhouettePreferencesLoadResult(
                    fallback,
                    SilhouettePreferencesLoadStatus.Missing);
            }
            EnsureOrdinaryPath(directory, requireFile: false);
            EnsureOrdinaryPath(path, requireFile: true);
            var json = CaptureProjectStore.ReadBoundedUtf8File(
                path,
                MaximumDocumentBytes,
                "silhouette preferences file");
            using (var parsed = JsonDocument.Parse(json))
            {
                if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                    !schemaElement.TryGetInt32(out var schemaVersion))
                {
                    return new SilhouettePreferencesLoadResult(
                        fallback,
                        SilhouettePreferencesLoadStatus.Invalid);
                }
                if (schemaVersion != CurrentSchemaVersion)
                {
                    return new SilhouettePreferencesLoadResult(
                        fallback,
                        SilhouettePreferencesLoadStatus.UnsupportedSchema);
                }
            }

            var document = JsonSerializer.Deserialize<SilhouettePreferencesDocument>(json, JsonOptions)
                ?? throw new JsonException("The silhouette preferences document is empty.");
            return new SilhouettePreferencesLoadResult(
                SilhouettePreferencesModel.Normalize(document, mirrorCamera),
                SilhouettePreferencesLoadStatus.Loaded);
        }
        catch (JsonException)
        {
            return new SilhouettePreferencesLoadResult(
                fallback,
                SilhouettePreferencesLoadStatus.Corrupt);
        }
        catch (InvalidDataException)
        {
            return new SilhouettePreferencesLoadResult(
                fallback,
                SilhouettePreferencesLoadStatus.Invalid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new SilhouettePreferencesLoadResult(
                fallback,
                SilhouettePreferencesLoadStatus.Unavailable);
        }
    }

    internal static Task<SilhouettePreferencesDocument> SaveAsync(
        string settingsDirectory,
        SilhouettePreferencesDocument document,
        bool mirrorCamera,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => SaveCore(settingsDirectory, document, mirrorCamera, cancellationToken),
            cancellationToken);
    }

    private static SilhouettePreferencesDocument SaveCore(
        string settingsDirectory,
        SilhouettePreferencesDocument document,
        bool mirrorCamera,
        CancellationToken cancellationToken)
    {
        var normalized = SilhouettePreferencesModel.Normalize(document, mirrorCamera);
        var path = GetPath(settingsDirectory);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The silhouette preference folder is invalid.");
        Directory.CreateDirectory(directory);
        EnsureOrdinaryPath(directory, requireFile: false);

        using var saveMutex = new Mutex(initiallyOwned: false, SaveMutexName);
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

            var replacingExisting = File.Exists(path);
            if (replacingExisting)
            {
                EnsureOrdinaryPath(path, requireFile: true);
                EnsureExistingDocumentCanBeReplaced(path, mirrorCamera);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
            if (bytes.Length <= 0 || bytes.Length > MaximumDocumentBytes)
            {
                throw new InvalidDataException("The silhouette preferences document is too large.");
            }

            var temporaryPath = Path.Combine(
                directory,
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
                File.Move(temporaryPath, path, overwrite: replacingExisting);
                return normalized;
            }
            finally
            {
                TryDeleteOwnedTemporaryFile(directory, temporaryPath);
            }
        }
        finally
        {
            if (lockTaken) saveMutex.ReleaseMutex();
        }
    }

    private static void EnsureExistingDocumentCanBeReplaced(
        string path,
        bool mirrorCamera)
    {
        try
        {
            var json = CaptureProjectStore.ReadBoundedUtf8File(
                path,
                MaximumDocumentBytes,
                "silhouette preferences file");
            using (var parsed = JsonDocument.Parse(json))
            {
                if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                    !schemaElement.TryGetInt32(out var schemaVersion) ||
                    schemaVersion != CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        "The existing silhouette preferences cannot be replaced by this version of ClipCord.");
                }
            }
            var existing = JsonSerializer.Deserialize<SilhouettePreferencesDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("The existing silhouette preferences are invalid.");
            _ = SilhouettePreferencesModel.Normalize(existing, mirrorCamera);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The existing silhouette preferences cannot be safely replaced.",
                exception);
        }
    }

    private static void EnsureOrdinaryPath(string path, bool requireFile)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The silhouette preference path cannot be a symbolic link or junction.");
        }
        if (requireFile == attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidDataException(
                "The silhouette preference path has an unexpected file-system type.");
        }
    }

    private static void TryDeleteOwnedTemporaryFile(
        string settingsDirectory,
        string temporaryPath)
    {
        try
        {
            var file = new FileInfo(temporaryPath);
            if (!file.Exists ||
                file.Directory is null ||
                !file.Directory.FullName.Equals(
                    settingsDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !file.Name.StartsWith($".{FileName}.", StringComparison.Ordinal) ||
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
}
