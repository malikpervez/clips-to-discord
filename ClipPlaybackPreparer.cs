using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

/// <summary>
/// Where in-app playback should read a clip from. <paramref name="IsMixedRendition"/> is
/// true when the source carried more than one audio track and a mixed copy had to be
/// produced first.
/// </summary>
internal sealed record ClipPlaybackSource(string Path, bool IsMixedRendition, int AudioTrackCount);

internal interface IClipPlaybackPreparer
{
    Task<ClipPlaybackSource> PrepareAsync(string sourcePath, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the file that in-app playback should open.
///
/// WPF's MediaElement renders only the first audio stream of a file, but a clip recorded
/// with the microphone on its own track carries several — and every editing path already
/// mixes them together (see <see cref="ClipEditProcessor.BuildAudioArguments"/>). Playing
/// the raw source would therefore preview something quieter than the file that actually
/// reaches Discord: game audio without the voice that the upload contains.
///
/// A single-track clip is handed back untouched. Anything with more tracks is rendered
/// once into a mixed copy that reuses the same FFmpeg audio graph as the edit, so what a
/// viewer hears in the app cannot disagree with what gets uploaded. The video stream is
/// copied rather than re-encoded, which keeps the render close to I/O bound.
/// </summary>
internal sealed class ClipPlaybackPreparer : IClipPlaybackPreparer
{
    private const string CacheFolderName = "playback-mix";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _renderLocks = new(StringComparer.OrdinalIgnoreCase);
    private int _pruned;

    internal static string CacheFolder =>
        Path.Combine(Path.GetTempPath(), "ClipsToDiscord", CacheFolderName);

    public async Task<ClipPlaybackSource> PrepareAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists)
        {
            throw new FileNotFoundException("The clip is no longer on disk.", source.FullName);
        }

        var ffmpeg = FfmpegCompressor.FindExecutable();
        if (ffmpeg is null)
        {
            // Without FFmpeg the only option is the source itself. A multi-track clip will
            // preview with its first track only, so callers surface that as a warning
            // rather than silently implying the mix was applied.
            return new ClipPlaybackSource(source.FullName, IsMixedRendition: false, AudioTrackCount: 1);
        }

        var probe = await FfmpegCompressor.ProbeMediaAsync(source.FullName, ffmpeg, cancellationToken);
        if (probe.AudioStreamCount <= 1)
        {
            return new ClipPlaybackSource(source.FullName, IsMixedRendition: false, probe.AudioStreamCount);
        }

        var renditionPath = Path.Combine(CacheFolder, BuildCacheKey(source) + ".mp4");
        PruneCacheOnce();

        var gate = _renderLocks.GetOrAdd(renditionPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have produced it while this one waited.
            if (File.Exists(renditionPath) && new FileInfo(renditionPath).Length > 0)
            {
                return new ClipPlaybackSource(renditionPath, IsMixedRendition: true, probe.AudioStreamCount);
            }

            Directory.CreateDirectory(CacheFolder);
            var partialPath = renditionPath + ".partial";
            TryDelete(partialPath);
            try
            {
                await FfmpegCompressor.RunAsync(
                    ffmpeg,
                    BuildMixedRenditionArguments(source.FullName, partialPath, probe.AudioStreamCount),
                    cancellationToken);
                if (!File.Exists(partialPath))
                {
                    throw new InvalidOperationException("FFmpeg did not create the mixed playback copy.");
                }

                // Prove the mix landed rather than assuming it. A rendition that somehow
                // came back with no audio, or with the tracks still separate, would put
                // playback right back where it started, so it is never handed to a player.
                var renditionProbe = await FfmpegCompressor.ProbeMediaAsync(partialPath, ffmpeg, cancellationToken);
                if (renditionProbe.AudioStreamCount != 1)
                {
                    throw new InvalidOperationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The mixed playback copy carries {renditionProbe.AudioStreamCount} audio streams instead of one."));
                }

                File.Move(partialPath, renditionPath, overwrite: true);
                return new ClipPlaybackSource(renditionPath, IsMixedRendition: true, probe.AudioStreamCount);
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
    /// Sums every audio stream into one track while copying the video untouched. The mix
    /// filter is shared with the edit path so the two cannot drift apart, and no PTS reset
    /// is applied: the copied video keeps its original timestamps, so shifting audio alone
    /// would desynchronise the two.
    /// </summary>
    internal static IReadOnlyList<string> BuildMixedRenditionArguments(
        string sourcePath,
        string renditionPath,
        int audioTrackCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(renditionPath);
        if (audioTrackCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioTrackCount),
                "A mixed rendition is only required when the source carries more than one audio track.");
        }

        return [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", sourcePath,
            "-filter_complex", FfmpegCompressor.BuildAudioMixFilter(audioTrackCount),
            "-map", "0:v:0", "-map", FfmpegCompressor.MixedAudioLabel,
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn",
            "-f", "mp4", renditionPath
        ];
    }

    /// <summary>
    /// Identifies a rendition by the source it came from. Length and write time are part
    /// of the key so a re-recorded clip at the same path never replays a stale mix.
    /// </summary>
    internal static string BuildCacheKey(FileInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"{source.FullName.ToUpperInvariant()}|{source.LastWriteTimeUtc.Ticks}|{source.Length}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// Mixed renditions are whole clips rather than the small PNG frames the preview
    /// folder holds, so the cache is swept once per session instead of growing forever.
    /// </summary>
    private void PruneCacheOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) != 0) return;
        try
        {
            if (!Directory.Exists(CacheFolder)) return;
            var cutoff = DateTime.UtcNow - CacheLifetime;
            foreach (var file in Directory.EnumerateFiles(CacheFolder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception exception)
                {
                    Log.Error($"Could not prune the cached playback copy {file}.", exception);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not prune the playback copy cache.", exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            Log.Error($"Could not remove the temporary playback copy {path}.", exception);
        }
    }
}
