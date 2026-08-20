using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Globalization;

namespace ClipsToDiscord;

internal interface IGalleryThumbnailProvider
{
    /// <summary>
    /// Returns a caller-owned bitmap for the clip, or <see langword="null"/> when the
    /// bundled FFmpeg executable is unavailable. Callers must dispose the returned image.
    /// </summary>
    Task<Bitmap?> GetThumbnailAsync(
        string clipsFolder,
        GalleryClipEntry clip,
        CancellationToken cancellationToken);
}

/// <summary>
/// Produces a small, near-start frame for Gallery cards without holding the source or
/// cached PNG open after the request completes. Frames are generated only on demand and
/// cached by the source file's path, write time, and length.
/// </summary>
internal sealed class GalleryThumbnailProvider : IGalleryThumbnailProvider
{
    private const string CacheFolderName = "gallery-thumbnails";
    private const long CacheByteBudget = 128L * 1024 * 1024;
    private const int MaximumConcurrentRenders = 2;
    private static readonly TimeSpan PartialLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ThumbnailLifetime = TimeSpan.FromDays(7);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RenderLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim RenderSlots =
        new(MaximumConcurrentRenders, MaximumConcurrentRenders);
    private static readonly SemaphoreSlim InitialPruneGate = new(1, 1);
    private static readonly object CacheBudgetGate = new();
    private static int _pruned;

    internal static string CacheFolder =>
        Path.Combine(Path.GetTempPath(), "ClipsToDiscord", CacheFolderName);

    public async Task<Bitmap?> GetThumbnailAsync(
        string clipsFolder,
        GalleryClipEntry clip,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsFolder);
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();

        await PruneCacheOnceAsync(cancellationToken).ConfigureAwait(false);
        // Revalidate the cache roots on every request. Pruning is intentionally once per
        // process, but a directory can be replaced by a junction after that first pass.
        EnsureSafeCacheFolder();
        var source = ResolveSafeSource(clipsFolder, clip);
        var expectedLength = source.Length;
        var expectedWriteTimeUtc = source.LastWriteTimeUtc;
        var cacheKey = BuildCacheKey(source);
        var thumbnailPath = Path.Combine(CacheFolder, cacheKey + ".png");

        var gate = RenderLocks.GetOrAdd(thumbnailPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = TryLoadCachedBitmap(thumbnailPath);
            if (cached is not null) return cached;

            var ffmpeg = FfmpegCompressor.FindExecutable();
            if (ffmpeg is null) return null;

            EnsureSafeCacheFolder();
            var partialPath = string.Create(
                CultureInfo.InvariantCulture,
                $"{thumbnailPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.partial");
            try
            {
                await RenderSlots.WaitAsync(cancellationToken);
                try
                {
                    try
                    {
                        await FfmpegCompressor.RunAsync(
                            ffmpeg,
                            BuildThumbnailArguments(source.FullName, partialPath),
                            cancellationToken);
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or IOException &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        // Very short clips can end before the preferred 0.25-second
                        // sampling point. A frame at time zero is still the truthful
                        // beginning thumbnail the Gallery promises.
                        TryDelete(partialPath);
                        await FfmpegCompressor.RunAsync(
                            ffmpeg,
                            BuildThumbnailArguments(source.FullName, partialPath, seekSeconds: 0d),
                            cancellationToken);
                    }
                }
                finally
                {
                    RenderSlots.Release();
                }

                cancellationToken.ThrowIfCancellationRequested();
                var partial = new FileInfo(partialPath);
                if (!partial.Exists || partial.Length <= 0)
                {
                    throw new InvalidOperationException("FFmpeg did not create a Gallery thumbnail.");
                }

                // Decode the file before adopting it so a successful FFmpeg exit cannot
                // leave a corrupt cache entry. The clone owns no handle to the partial.
                using (LoadBitmapClone(partialPath)) { }

                // A clip can be replaced while FFmpeg is reading it. Never publish that
                // frame under the old identity's key; repeat the bounded archive and
                // reparse-point checks, then compare the exact catalog identity.
                var current = ResolveSafeSource(clipsFolder, clip);
                if (!HasIdentity(current, source.FullName, expectedLength, expectedWriteTimeUtc))
                {
                    throw new IOException("The clip changed while its Gallery thumbnail was being prepared.");
                }

                return PublishThumbnail(partialPath, thumbnailPath);
            }
            catch
            {
                TryDelete(partialPath);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Seeks just past the container's opening frame, extracts one video frame, and
    /// constrains only its width. The escaped comma is FFmpeg filter syntax rather than
    /// command-shell escaping; arguments are passed through ProcessStartInfo.ArgumentList.
    /// </summary>
    internal static IReadOnlyList<string> BuildThumbnailArguments(
        string sourcePath,
        string outputPath,
        double seekSeconds = 0.25d)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!double.IsFinite(seekSeconds) || seekSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(seekSeconds));
        }

        return [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", seekSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", sourcePath,
            "-map", "0:v:0", "-vf", "scale=min(640\\,iw):-2",
            "-frames:v", "1", "-an", "-sn", "-dn",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:v", "png", "-f", "image2", "-update", "1", outputPath
        ];
    }

    /// <summary>
    /// Uses the playback cache's reviewed identity function so every media cache agrees
    /// on what constitutes a changed source.
    /// </summary>
    internal static string BuildCacheKey(FileInfo source) =>
        ClipPlaybackPreparer.BuildCacheKey(source);

    /// <summary>
    /// Resolves a catalog entry back to the matching uploaded/local-only archive. Only a
    /// clip directly in that route root or one game folder below it is accepted, and no
    /// component that can redirect traversal may be a reparse point.
    /// </summary>
    internal static FileInfo ResolveSafeSource(string clipsFolder, GalleryClipEntry clip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsFolder);
        ArgumentNullException.ThrowIfNull(clip);

        var clipsRoot = new DirectoryInfo(Path.GetFullPath(clipsFolder));
        if (!clipsRoot.Exists)
        {
            throw new DirectoryNotFoundException("The configured clips folder is not available.");
        }
        RejectReparsePoint(clipsRoot, "The configured clips folder cannot be a symbolic link or junction.");

        var routePath = clip.Route switch
        {
            GalleryClipRoute.Uploaded => UploadedFolder.FindExistingUploaded(clipsRoot.FullName),
            GalleryClipRoute.LocalOnly => UploadedFolder.FindExistingLocalOnly(clipsRoot.FullName),
            _ => throw new ArgumentOutOfRangeException(nameof(clip), "The Gallery route is not supported.")
        };
        if (routePath is null)
        {
            throw new DirectoryNotFoundException("The Gallery route folder is not available.");
        }

        var routeRoot = new DirectoryInfo(Path.GetFullPath(routePath));
        if (!routeRoot.Exists)
        {
            throw new DirectoryNotFoundException("The Gallery route folder is not available.");
        }
        RejectReparsePoint(routeRoot, "The Gallery route folder cannot be a symbolic link or junction.");

        var sourcePath = Path.GetFullPath(clip.Path);
        var relative = Path.GetRelativePath(routeRoot.FullName, sourcePath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            segments.Length is < 1 or > 2 ||
            segments.Any(segment => segment is "." or ".."))
        {
            throw new IOException("The clip is outside its bounded Gallery route.");
        }

        var recombined = Path.GetFullPath(Path.Combine(routeRoot.FullName, Path.Combine(segments)));
        if (!recombined.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The clip path is not a canonical Gallery route path.");
        }

        if (segments.Length == 2)
        {
            var gameFolder = new DirectoryInfo(Path.Combine(routeRoot.FullName, segments[0]));
            if (!gameFolder.Exists)
            {
                throw new DirectoryNotFoundException("The Gallery game folder is not available.");
            }
            RejectReparsePoint(gameFolder, "A Gallery game folder cannot be a symbolic link or junction.");
        }

        var source = new FileInfo(sourcePath);
        source.Refresh();
        if (!source.Exists)
        {
            throw new FileNotFoundException("The Gallery clip is no longer on disk.");
        }
        RejectReparsePoint(source, "A Gallery clip cannot be a symbolic link or junction.");
        if (!source.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            !source.Name.Equals(clip.FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The Gallery clip identity is invalid.");
        }
        if (!HasIdentity(source, sourcePath, clip.Length, clip.LastWriteTimeUtc))
        {
            throw new IOException("The Gallery clip changed after it was cataloged.");
        }

        return source;
    }

    internal static bool HasIdentity(
        FileInfo source,
        string expectedPath,
        long expectedLength,
        DateTime expectedWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Refresh();
        return source.Exists &&
               source.FullName.Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase) &&
               source.Length == expectedLength &&
               source.LastWriteTimeUtc.Ticks == expectedWriteTimeUtc.ToUniversalTime().Ticks;
    }

    private static Bitmap? TryLoadCachedBitmap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var file = new FileInfo(path);
            if (file.Length <= 0)
            {
                TryDelete(path);
                return null;
            }
            return LoadBitmapClone(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            TryDelete(path);
            return null;
        }
    }

    /// <summary>
    /// Fully decodes into a new bitmap while the stream is open. Neither the stream nor
    /// the Image created from it escapes this method, so Gallery cards can dispose their
    /// bitmap without ever pinning a cache file.
    /// </summary>
    private static Bitmap LoadBitmapClone(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.DrawImage(
                image,
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void EnsureSafeCacheFolder()
    {
        var cacheRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ClipsToDiscord"));
        RejectReparsePoint(cacheRoot, "The ClipCord cache folder cannot be a symbolic link or junction.");
        var thumbnailRoot = Directory.CreateDirectory(CacheFolder);
        RejectReparsePoint(thumbnailRoot, "The Gallery thumbnail cache cannot be a symbolic link or junction.");
    }

    private static async Task PruneCacheOnceAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _pruned) != 0) return;
        await InitialPruneGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _pruned) != 0) return;
            PruneCache();
            Volatile.Write(ref _pruned, 1);
        }
        finally
        {
            InitialPruneGate.Release();
        }
    }

    private static void PruneCache()
    {
        lock (CacheBudgetGate)
        {
            try
            {
                EnsureSafeCacheFolder();
                var now = DateTime.UtcNow;
                var partialCutoff = now - PartialLifetime;
                var thumbnailCutoff = now - ThumbnailLifetime;
                var surviving = new List<FileInfo>();
                foreach (var path in Directory.EnumerateFiles(CacheFolder, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        if (file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                        {
                            if (file.LastWriteTimeUtc < partialCutoff) file.Delete();
                            continue;
                        }
                        if (!file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
                        if (file.LastWriteTimeUtc < thumbnailCutoff) file.Delete();
                        else surviving.Add(file);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Log.Error($"Could not prune cached Gallery thumbnail {Path.GetFileName(path)}.", exception);
                    }
                }

                var total = 0L;
                foreach (var file in surviving)
                {
                    total = file.Length > long.MaxValue - total ? long.MaxValue : total + file.Length;
                }
                if (total <= CacheByteBudget) return;

                foreach (var file in surviving.OrderBy(file => file.LastWriteTimeUtc))
                {
                    if (total <= CacheByteBudget) break;
                    try
                    {
                        var length = file.Length;
                        file.Delete();
                        total = Math.Max(0, total - length);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Log.Error($"Could not trim cached Gallery thumbnail {file.Name}.", exception);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not prune the Gallery thumbnail cache.", exception);
            }
        }
    }

    private static Bitmap PublishThumbnail(string partialPath, string thumbnailPath)
    {
        lock (CacheBudgetGate)
        {
            File.Move(partialPath, thumbnailPath, overwrite: true);
            var bitmap = LoadBitmapClone(thumbnailPath);
            EnforceCacheBudgetCore(thumbnailPath);
            return bitmap;
        }
    }

    private static void EnforceCacheBudgetCore(string protectedPath)
    {
        try
        {
            var thumbnails = Directory
                .EnumerateFiles(CacheFolder, "*.png", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToArray();
            var total = thumbnails.Aggregate(
                0L,
                (sum, file) => file.Length > long.MaxValue - sum ? long.MaxValue : sum + file.Length);
            foreach (var file in thumbnails)
            {
                if (total <= CacheByteBudget) break;
                if (file.FullName.Equals(protectedPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var length = file.Length;
                    file.Delete();
                    total = Math.Max(0, total - length);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log.Error($"Could not trim cached Gallery thumbnail {file.Name}.", exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not enforce the Gallery thumbnail cache budget.", exception);
        }
    }

    private static void RejectReparsePoint(FileSystemInfo entry, string message)
    {
        if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not remove temporary Gallery thumbnail {Path.GetFileName(path)}.", exception);
        }
    }
}
