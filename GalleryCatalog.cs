using System.Text;

namespace ClipsToDiscord;

internal enum GalleryClipRoute
{
    Uploaded,
    LocalOnly
}

internal sealed record GalleryClipEntry(
    string Path,
    string FileName,
    string GameName,
    GalleryClipRoute Route,
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed record GalleryGameEntry(
    string Name,
    IReadOnlyList<GalleryClipEntry> Clips)
{
    internal int UploadedCount => Clips.Count(clip => clip.Route == GalleryClipRoute.Uploaded);
    internal int LocalOnlyCount => Clips.Count(clip => clip.Route == GalleryClipRoute.LocalOnly);
    internal long TotalBytes => Clips.Sum(clip => clip.Length);
}

internal sealed record GallerySnapshot(
    IReadOnlyList<GalleryGameEntry> Games,
    IReadOnlyList<string> Warnings)
{
    internal int TotalClips => Games.Sum(game => game.Clips.Count);
    internal int UploadedCount => Games.Sum(game => game.UploadedCount);
    internal int LocalOnlyCount => Games.Sum(game => game.LocalOnlyCount);
}

internal readonly record struct GalleryGradient(Color Start, Color End);

internal static class GalleryCatalog
{
    private static readonly GalleryGradient[] Gradients =
    [
        new(Color.FromArgb(224, 75, 69), Color.FromArgb(32, 54, 92)),
        new(Color.FromArgb(139, 61, 255), Color.FromArgb(44, 105, 189)),
        new(Color.FromArgb(213, 159, 49), Color.FromArgb(43, 50, 46)),
        new(Color.FromArgb(34, 158, 150), Color.FromArgb(33, 59, 96)),
        new(Color.FromArgb(207, 69, 126), Color.FromArgb(76, 51, 133)),
        new(Color.FromArgb(42, 133, 198), Color.FromArgb(28, 41, 72)),
        new(Color.FromArgb(219, 105, 51), Color.FromArgb(88, 44, 81)),
        new(Color.FromArgb(73, 153, 83), Color.FromArgb(29, 58, 68))
    ];

    internal static GallerySnapshot Scan(
        string clipsFolder,
        CancellationToken cancellationToken,
        Action<string>? beforeGameDirectoryScan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsFolder);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(clipsFolder))
        {
            return new GallerySnapshot([], ["The clips folder is not available."]);
        }

        var clips = new List<GalleryClipEntry>();
        var warnings = new List<string>();
        ScanArchive(
            ResolveArchive(clipsFolder, GalleryClipRoute.Uploaded, warnings),
            GalleryClipRoute.Uploaded,
            clips,
            warnings,
            cancellationToken,
            beforeGameDirectoryScan);
        ScanArchive(
            ResolveArchive(clipsFolder, GalleryClipRoute.LocalOnly, warnings),
            GalleryClipRoute.LocalOnly,
            clips,
            warnings,
            cancellationToken,
            beforeGameDirectoryScan);

        var games = clips
            .GroupBy(clip => clip.GameName.Normalize(NormalizationForm.FormC), StringComparer.OrdinalIgnoreCase)
            .Select(group => new GalleryGameEntry(
                group.Key,
                group.OrderByDescending(clip => clip.LastWriteTimeUtc)
                    .ThenBy(clip => clip.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderByDescending(game => game.Clips.Max(clip => clip.LastWriteTimeUtc))
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new GallerySnapshot(games, warnings);
    }

    internal static GalleryGradient GetGradient(string gameName)
    {
        var normalized = (gameName ?? string.Empty).Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var hash = 2166136261u;
        foreach (var character in normalized)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return Gradients[(int)(hash % (uint)Gradients.Length)];
    }

    internal static string GetInitials(string gameName)
    {
        var words = (gameName ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
        {
            return new string(words[0].Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant() switch
            {
                "" => "?",
                var value => value
            };
        }

        var initials = words
            .Select(word => word.FirstOrDefault(char.IsLetterOrDigit))
            .Where(character => character != default)
            .Take(3)
            .ToArray();
        return initials.Length == 0 ? "?" : new string(initials).ToUpperInvariant();
    }

    private static void ScanArchive(
        string? archiveFolder,
        GalleryClipRoute route,
        ICollection<GalleryClipEntry> clips,
        ICollection<string> warnings,
        CancellationToken cancellationToken,
        Action<string>? beforeGameDirectoryScan)
    {
        if (archiveFolder is null) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(archiveFolder, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddClip(path, UploadedFolder.GetGameFolderName(Path.GetFileName(path)), route, clips);
            }

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddArchiveWarning(route, warnings);
            Log.Error($"Could not scan root-level clips in the {route} Gallery archive.", exception);
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(archiveFolder, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddArchiveWarning(route, warnings);
            Log.Error($"Could not enumerate game folders in the {route} Gallery archive.", exception);
            return;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The optional callback is a deterministic fault-injection seam for
                // testing a folder that disappears after archive enumeration.
                beforeGameDirectoryScan?.Invoke(directory);
                var directoryInfo = new DirectoryInfo(directory);
                if (directoryInfo.LinkTarget is not null) continue;
                var gameName = string.IsNullOrWhiteSpace(directoryInfo.Name)
                    ? "Uncategorized"
                    : directoryInfo.Name.Normalize(NormalizationForm.FormC);
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddClip(path, gameName, route, clips);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddArchiveWarning(route, warnings);
                Log.Error($"Could not scan a game folder in the {route} Gallery archive.", exception);
            }
        }
    }

    private static string? ResolveArchive(
        string clipsFolder,
        GalleryClipRoute route,
        ICollection<string> warnings)
    {
        try
        {
            return route == GalleryClipRoute.Uploaded
                ? UploadedFolder.FindExistingUploaded(clipsFolder)
                : UploadedFolder.FindExistingLocalOnly(clipsFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddArchiveWarning(route, warnings);
            Log.Error($"Could not resolve the {route} Gallery archive.", exception);
            return null;
        }
    }

    private static void AddArchiveWarning(GalleryClipRoute route, ICollection<string> warnings)
    {
        var warning = route == GalleryClipRoute.Uploaded
            ? "Some uploaded clips could not be read."
            : "Some local-only clips could not be read.";
        if (!warnings.Contains(warning)) warnings.Add(warning);
    }

    private static void AddClip(
        string path,
        string gameName,
        GalleryClipRoute route,
        ICollection<GalleryClipEntry> clips)
    {
        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null) return;
            clips.Add(new GalleryClipEntry(
                file.FullName,
                file.Name,
                string.IsNullOrWhiteSpace(gameName) ? "Uncategorized" : gameName,
                route,
                Math.Max(0, file.Length),
                file.LastWriteTimeUtc));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not inspect Gallery clip {Path.GetFileName(path)}.", exception);
        }
    }
}
