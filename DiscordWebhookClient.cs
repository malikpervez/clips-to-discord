using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MomentsToDiscord;

internal sealed class DiscordWebhookClient : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(20) };

    public async Task TestConnectionAsync(string webhookUrl, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            content = "**Moments to Discord connected.** Future clips will appear here automatically.",
            allowed_mentions = new { parse = Array.Empty<string>() }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(WithWait(webhookUrl), content, cancellationToken);
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

            Log.Info($"Compressing oversized clip: {Path.GetFileName(filePath)}");
            var compressedPath = await FfmpegCompressor.CompressAsync(filePath, ffmpegPath, cancellationToken);
            try
            {
                await UploadOnceAsync(
                    webhookUrl,
                    compressedPath,
                    Path.GetFileName(filePath),
                    cancellationToken);
            }
            finally
            {
                TryDelete(compressedPath);
            }
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

        using var response = await _client.PostAsync(WithWait(webhookUrl), multipart, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var tooLarge = response.StatusCode == HttpStatusCode.RequestEntityTooLarge ||
                           responseText.Contains("40005", StringComparison.OrdinalIgnoreCase) ||
                           responseText.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
                           responseText.Contains("maximum", StringComparison.OrdinalIgnoreCase);
            throw new DiscordUploadException(
                $"Discord returned HTTP {(int)response.StatusCode}: {responseText}",
                tooLarge);
        }
    }

    private static string WithWait(string webhookUrl) =>
        webhookUrl + (webhookUrl.Contains('?') ? "&wait=true" : "?wait=true");

    private static string SanitizeFileName(string fileName)
    {
        var safe = new string(Path.GetFileName(fileName)
            .Select(character => character is >= ' ' and <= '~' ? character : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "SteelSeries-clip.mp4" : safe;
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
