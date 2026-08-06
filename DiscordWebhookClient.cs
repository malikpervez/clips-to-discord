using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClipsToDiscord;

internal sealed class DiscordWebhookClient : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    internal const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _client;

    public DiscordWebhookClient()
    {
        var handler = CreateHandler();
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = ConnectionTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    public async Task TestConnectionAsync(
        string webhookUrl,
        string uploaderName,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            content = $"**ClipCord connected.** Future clips from {DiscordClipMessage.EscapeMarkdown(AppSettings.NormalizeUploaderName(uploaderName))} will appear here automatically.",
            allowed_mentions = new { parse = Array.Empty<string>() }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await PostWithDeadlineAsync(
            WithWait(webhookUrl),
            content,
            TestTimeout,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Discord rejected the webhook (HTTP {(int)response.StatusCode}). {response.ResponseText}");
        }
    }

    public async Task UploadWithCompressionAsync(
        string webhookUrl,
        string filePath,
        int compressionTargetMb,
        string uploaderName,
        CancellationToken cancellationToken)
    {
        var message = DiscordClipMessage.BuildContent(uploaderName, Path.GetFileName(filePath));
        var description = DiscordClipMessage.BuildDescription(uploaderName, Path.GetFileName(filePath));
        try
        {
            await UploadOnceAsync(
                webhookUrl,
                filePath,
                Path.GetFileName(filePath),
                message,
                description,
                cancellationToken);
        }
        catch (DiscordUploadException exception) when (exception.IsTooLarge)
        {
            var ffmpegPath = FfmpegCompressor.FindExecutable();
            if (ffmpegPath is null)
            {
                throw new InvalidOperationException(
                    "The clip is larger than this Discord server accepts and ffmpeg.exe is not bundled with the app.",
                    exception);
            }

            var plannedTargets = CompressionTargetPlanner.Build(compressionTargetMb);
            var duration = await FfmpegCompressor.ProbeDurationAsync(filePath, ffmpegPath, cancellationToken);
            var achievableTargets = CompressionTargetPlanner.BuildAchievable(compressionTargetMb, duration);
            if (achievableTargets.Count == 0)
            {
                throw new CompressionTargetUnachievableException(
                    $"This {duration.TotalMinutes:F1}-minute clip is too long to reach any configured upload target without falling below ClipCord's minimum video bitrate.",
                    exception);
            }

            DiscordUploadException lastSizeException = exception;
            foreach (var targetMb in achievableTargets)
            {
                Log.Info($"Compressing oversized clip {Path.GetFileName(filePath)} to a {targetMb} MB target.");
                var compressedPath = await FfmpegCompressor.CompressAsync(
                    filePath,
                    ffmpegPath,
                    targetMb,
                    duration,
                    cancellationToken);
                try
                {
                    var originalBytes = new FileInfo(filePath).Length;
                    var compressedBytes = new FileInfo(compressedPath).Length;
                    CompressionTargetPlanner.TryCreateBitrates(duration, targetMb, out var bitrates);
                    Log.Info(BuildCompressionLogMessage(
                        Path.GetFileName(filePath),
                        originalBytes,
                        compressedBytes,
                        targetMb,
                        bitrates));

                    await UploadOnceAsync(
                        webhookUrl,
                        compressedPath,
                        Path.GetFileName(filePath),
                        message,
                        description,
                        cancellationToken);
                    return;
                }
                catch (DiscordUploadException compressedException) when (compressedException.IsTooLarge)
                {
                    lastSizeException = compressedException;
                    Log.Info($"Discord rejected the {targetMb} MB target; trying a smaller target.");
                }
                finally
                {
                    TryDelete(compressedPath);
                }
            }

            if (achievableTargets.Count < plannedTargets.Count)
            {
                throw new CompressionTargetUnachievableException(
                    "Discord rejected every achievable compression target. Smaller targets would fall below ClipCord's minimum video bitrate for this clip.",
                    lastSizeException);
            }

            throw new InvalidOperationException(
                "Discord rejected every configured compression target. Lower the compression target in Settings and retry.",
                lastSizeException);
        }
    }

    internal static string BuildCompressionLogMessage(
        string fileName,
        long originalBytes,
        long compressedBytes,
        int targetMb,
        CompressionTargetPlanner.CompressionBitrates bitrates)
    {
        var originalMb = originalBytes / 1024d / 1024d;
        var compressedMb = compressedBytes / 1024d / 1024d;
        var reductionPercent = originalBytes > 0
            ? Math.Max(0, (1d - compressedBytes / (double)originalBytes) * 100d)
            : 0;

        FormattableString message =
            $"Compression complete for {fileName}: {originalMb:F1} MB -> {compressedMb:F1} MB ({reductionPercent:F1}% smaller; {targetMb} MB target ceiling; {bitrates.VideoKbps} kbps video / {bitrates.AudioKbps} kbps audio).";
        return FormattableString.Invariant(message);
    }

    private async Task UploadOnceAsync(
        string webhookUrl,
        string filePath,
        string originalName,
        string message,
        string description,
        CancellationToken cancellationToken)
    {
        using var multipart = new MultipartFormDataContent();
        var safeFileName = SanitizeFileName(originalName);
        var payload = BuildUploadPayload(safeFileName, message, description);
        multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            useAsync: true);
        using var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        multipart.Add(streamContent, "files[0]", safeFileName);

        var response = await PostWithDeadlineAsync(
            WithWait(webhookUrl),
            multipart,
            UploadTimeout,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var tooLarge = response.StatusCode == HttpStatusCode.RequestEntityTooLarge ||
                           response.ResponseText.Contains("40005", StringComparison.OrdinalIgnoreCase) ||
                           response.ResponseText.Contains("50045", StringComparison.OrdinalIgnoreCase) ||
                           response.ResponseText.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
                           response.ResponseText.Contains("maximum", StringComparison.OrdinalIgnoreCase);
            throw new DiscordUploadException(
                $"Discord returned HTTP {(int)response.StatusCode}: {response.ResponseText}",
                tooLarge);
        }
    }

    internal static string BuildUploadPayload(string safeFileName, string message, string description) =>
        JsonSerializer.Serialize(new
        {
            content = message,
            attachments = new[]
            {
                new { id = 0, filename = safeFileName, description }
            },
            allowed_mentions = new { parse = Array.Empty<string>() }
        });

    private async Task<WebhookResponse> PostWithDeadlineAsync(
        string requestUrl,
        HttpContent content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = content
            };
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);
            var responseText = await ReadResponseTextAsync(response.Content, deadline.Token);
            return new WebhookResponse(response.StatusCode, response.IsSuccessStatusCode, responseText);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Discord did not complete the request within {timeout.TotalMinutes:F1} minute(s).", exception);
        }
    }

    internal static string WithWait(string webhookUrl)
    {
        var builder = new UriBuilder(webhookUrl) { Fragment = string.Empty };
        var queryParts = builder.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !IsWaitParameter(part))
            .Append("wait=true");
        builder.Query = string.Join('&', queryParts);
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsWaitParameter(string queryPart)
    {
        var separator = queryPart.IndexOf('=');
        var name = separator < 0 ? queryPart : queryPart[..separator];
        try
        {
            return Uri.UnescapeDataString(name).Equals("wait", StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    internal static async Task<string> ReadResponseTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaximumResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }

        var length = Math.Min(total, MaximumResponseBytes);
        var responseText = Encoding.UTF8.GetString(buffer, 0, length);
        return total > MaximumResponseBytes
            ? responseText + " [response truncated]"
            : responseText;
    }

    private static string SanitizeFileName(string fileName)
    {
        var safe = new string(Path.GetFileName(fileName)
            .Select(character => character is >= ' ' and <= '~' ? character : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "clip.mp4" : safe;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose() => _client.Dispose();

    private readonly record struct WebhookResponse(
        HttpStatusCode StatusCode,
        bool IsSuccessStatusCode,
        string ResponseText);
}

internal sealed class DiscordUploadException(string message, bool isTooLarge) : Exception(message)
{
    public bool IsTooLarge { get; } = isTooLarge;
}
