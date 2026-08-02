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
    private readonly HttpClient _client;

    public DiscordWebhookClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectionTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task TestConnectionAsync(string webhookUrl, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            content = "**Clips to Discord connected.** Future clips will appear here automatically.",
            allowed_mentions = new { parse = Array.Empty<string>() }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await PostWithDeadlineAsync(
            WithWait(webhookUrl),
            content,
            TestTimeout,
            cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Discord rejected the webhook (HTTP {(int)response.StatusCode}). {responseText}");
        }
    }

    public async Task UploadWithCompressionAsync(
        string webhookUrl,
        string filePath,
        int compressionTargetMb,
        CancellationToken cancellationToken)
    {
        try
        {
            await UploadOnceAsync(webhookUrl, filePath, Path.GetFileName(filePath), cancellationToken);
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

            DiscordUploadException lastSizeException = exception;
            foreach (var targetMb in CompressionTargetPlanner.Build(compressionTargetMb))
            {
                Log.Info($"Compressing oversized clip {Path.GetFileName(filePath)} to a {targetMb} MB target.");
                var compressedPath = await FfmpegCompressor.CompressAsync(
                    filePath,
                    ffmpegPath,
                    targetMb,
                    cancellationToken);
                try
                {
                    await UploadOnceAsync(
                        webhookUrl,
                        compressedPath,
                        Path.GetFileName(filePath),
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

            throw new InvalidOperationException(
                "Discord rejected every configured compression target. Lower the compression target in Settings and retry.",
                lastSizeException);
        }
    }

    private async Task UploadOnceAsync(
        string webhookUrl,
        string filePath,
        string originalName,
        CancellationToken cancellationToken)
    {
        using var multipart = new MultipartFormDataContent();
        var message = $"**Clip: {Path.GetFileNameWithoutExtension(originalName)}**";
        var payload = JsonSerializer.Serialize(new
        {
            content = message,
            allowed_mentions = new { parse = Array.Empty<string>() }
        });
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
        multipart.Add(streamContent, "files[0]", SanitizeFileName(originalName));

        using var response = await PostWithDeadlineAsync(
            WithWait(webhookUrl),
            multipart,
            UploadTimeout,
            cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var tooLarge = response.StatusCode == HttpStatusCode.RequestEntityTooLarge ||
                           responseText.Contains("40005", StringComparison.OrdinalIgnoreCase) ||
                           responseText.Contains("50045", StringComparison.OrdinalIgnoreCase) ||
                           responseText.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
                           responseText.Contains("maximum", StringComparison.OrdinalIgnoreCase);
            throw new DiscordUploadException(
                $"Discord returned HTTP {(int)response.StatusCode}: {responseText}",
                tooLarge);
        }
    }

    private async Task<HttpResponseMessage> PostWithDeadlineAsync(
        string requestUrl,
        HttpContent content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await _client.PostAsync(requestUrl, content, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Discord did not complete the request within {timeout.TotalMinutes:F1} minute(s).", exception);
        }
    }

    private static string WithWait(string webhookUrl) =>
        webhookUrl + (webhookUrl.Contains('?') ? "&wait=true" : "?wait=true");

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
}

internal sealed class DiscordUploadException(string message, bool isTooLarge) : Exception(message)
{
    public bool IsTooLarge { get; } = isTooLarge;
}
