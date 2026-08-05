using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

internal readonly record struct StableVersion(int Major, int Minor, int Patch) : IComparable<StableVersion>
{
    public static bool TryParse(string? value, out StableVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim();
        if (normalized.StartsWith('v')) normalized = normalized[1..];
        var parts = normalized.Split('.');
        if (parts.Length != 3 || parts.Any(part =>
                part.Length == 0 ||
                (part.Length > 1 && part[0] == '0') ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new StableVersion(major, minor, patch);
        return true;
    }

    public static StableVersion FromAssemblyVersion(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build));

    public int CompareTo(StableVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0) return majorComparison;
        var minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0 ? minorComparison : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

internal sealed record UpdateRelease(
    StableVersion Version,
    string TagName,
    Uri ReleasePageUri,
    Uri InstallerUri,
    string InstallerSha256,
    long InstallerSize);

internal enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    InvalidRelease,
    Failed,
    Busy,
    NotDue,
    Suppressed
}

internal sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateRelease? Release = null,
    string? Message = null)
{
    public static UpdateCheckResult Available(UpdateRelease release) =>
        new(UpdateCheckStatus.UpdateAvailable, release);
}

internal interface IUpdateChecker : IDisposable
{
    Task<UpdateCheckResult> CheckAsync(StableVersion currentVersion, CancellationToken cancellationToken);
}

internal sealed partial class GitHubUpdateChecker : IUpdateChecker
{
    internal const string Owner = "malikpervez";
    internal const string Repository = "clips-to-discord";
    internal const string InstallerFileName = "ClipCord-Setup.exe";
    internal const string ChecksumsFileName = "SHA256SUMS.txt";
    internal const long MaximumInstallerBytes = 536_870_912;
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(12);
    internal static readonly Uri LatestReleaseApiUri = new(
        $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    private const int MaximumReleaseResponseBytes = 1_048_576;
    private const int MaximumChecksumResponseBytes = 65_536;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly TimeSpan _operationTimeout;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private static readonly HashSet<string> AllowedAssetRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    public static GitHubUpdateChecker Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        return new GitHubUpdateChecker(
            client,
            disposeHttpClient: true,
            operationTimeout: DefaultOperationTimeout);
    }

    internal GitHubUpdateChecker(
        HttpClient httpClient,
        bool disposeHttpClient = false,
        TimeSpan? operationTimeout = null)
    {
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        StableVersion currentVersion,
        CancellationToken cancellationToken)
    {
        if (!await _checkGate.WaitAsync(0, cancellationToken))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Busy, Message: "An update check is already running.");
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_operationTimeout);
            var operationToken = deadline.Token;
            using var request = CreateRequest(LatestReleaseApiUri, acceptJson: true);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operationToken);
            response.EnsureSuccessStatusCode();
            var payload = await ReadBoundedAsync(response, MaximumReleaseResponseBytes, operationToken);
            var release = JsonSerializer.Deserialize<GitHubReleaseDto>(payload);
            if (release is null)
            {
                return Invalid("GitHub returned an empty release document.");
            }

            return await ValidateReleaseAsync(release, currentVersion, operationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException)
        {
            Log.Error("Update check failed.", exception);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                Message: "The update service could not be reached or returned an unreadable response.");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<UpdateCheckResult> ValidateReleaseAsync(
        GitHubReleaseDto release,
        StableVersion currentVersion,
        CancellationToken cancellationToken)
    {
        if (release.Draft || release.Prerelease)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }

        if (!StableVersion.TryParse(release.TagName, out var releaseVersion))
        {
            return Invalid("The latest release tag is not a stable semantic version.");
        }

        if (!TryValidateReleasePage(release.HtmlUrl, release.TagName!, out var releasePageUri))
        {
            return Invalid("The release page did not match the official repository.");
        }

        if (releaseVersion.CompareTo(currentVersion) <= 0)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }

        var installerAssets = (release.Assets ?? [])
            .Where(asset => string.Equals(asset?.Name, InstallerFileName, StringComparison.Ordinal))
            .OfType<GitHubAssetDto>()
            .ToArray();
        if (installerAssets.Length != 1)
        {
            return Invalid("The release did not contain exactly one expected installer.");
        }

        var installer = installerAssets[0];
        if (!string.Equals(installer.State, "uploaded", StringComparison.Ordinal) ||
            installer.Size is <= 0 or > MaximumInstallerBytes ||
            !TryValidateAssetUri(installer.BrowserDownloadUrl, release.TagName!, InstallerFileName, out var installerUri))
        {
            return Invalid("The installer asset metadata was invalid.");
        }

        var digest = NormalizeSha256Digest(installer.Digest);
        if (digest is null)
        {
            digest = await GetManifestDigestAsync(release, release.TagName!, cancellationToken);
        }

        if (digest is null)
        {
            return Invalid("The installer did not have a verifiable SHA-256 digest.");
        }

        return UpdateCheckResult.Available(new UpdateRelease(
            releaseVersion,
            release.TagName!,
            releasePageUri,
            installerUri,
            digest,
            installer.Size));
    }

    private async Task<string?> GetManifestDigestAsync(
        GitHubReleaseDto release,
        string tagName,
        CancellationToken cancellationToken)
    {
        var manifests = (release.Assets ?? [])
            .Where(asset => string.Equals(asset?.Name, ChecksumsFileName, StringComparison.Ordinal))
            .OfType<GitHubAssetDto>()
            .ToArray();
        if (manifests.Length != 1) return null;

        var manifest = manifests[0];
        if (!string.Equals(manifest.State, "uploaded", StringComparison.Ordinal) ||
            manifest.Size is <= 0 or > MaximumChecksumResponseBytes ||
            !TryValidateAssetUri(manifest.BrowserDownloadUrl, tagName, ChecksumsFileName, out var manifestUri))
        {
            return null;
        }

        using var response = await SendChecksumRequestAsync(manifestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await ReadBoundedAsync(response, MaximumChecksumResponseBytes, cancellationToken);
        var manifestText = Encoding.UTF8.GetString(payload);
        var matches = InstallerChecksumPattern().Matches(manifestText);
        return matches.Count == 1 ? matches[0].Groups["hash"].Value.ToLowerInvariant() : null;
    }

    private async Task<HttpResponseMessage> SendChecksumRequestAsync(
        Uri manifestUri,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(manifestUri, acceptJson: false);
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!IsRedirect(response.StatusCode)) return response;

        var location = response.Headers.Location;
        response.Dispose();
        if (!TryValidateAssetRedirectUri(location, out var redirectUri))
        {
            throw new InvalidDataException("The checksum asset redirect was not an allowed GitHub host.");
        }

        using var redirectRequest = CreateRequest(redirectUri, acceptJson: false);
        var redirectedResponse = await _httpClient.SendAsync(
            redirectRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (IsRedirect(redirectedResponse.StatusCode))
        {
            redirectedResponse.Dispose();
            throw new InvalidDataException("The checksum asset exceeded its redirect limit.");
        }
        return redirectedResponse;
    }

    private static HttpRequestMessage CreateRequest(Uri uri, bool acceptJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ClipCord-UpdateChecker");
        if (acceptJson)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("The update response exceeded its size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The update response exceeded its size limit.");
            }
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static bool TryValidateReleasePage(string? value, string tagName, out Uri uri)
    {
        var expectedPath = $"/{Owner}/{Repository}/releases/tag/{tagName}";
        return TryValidateGitHubUri(value, expectedPath, out uri);
    }

    private static bool TryValidateAssetUri(string? value, string tagName, string fileName, out Uri uri)
    {
        var expectedPath = $"/{Owner}/{Repository}/releases/download/{tagName}/{fileName}";
        return TryValidateGitHubUri(value, expectedPath, out uri);
    }

    internal static bool IsExpectedInstallerUri(Uri candidate, string tagName) =>
        TryValidateAssetUri(candidate.AbsoluteUri, tagName, InstallerFileName, out _);

    private static bool TryValidateGitHubUri(string? value, string expectedPath, out Uri uri)
    {
        var parsed = Uri.TryCreate(value, UriKind.Absolute, out var candidate) ? candidate : null;
        if (parsed is not null &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            parsed.IsDefaultPort &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) &&
            string.IsNullOrEmpty(parsed.Fragment) &&
            parsed.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    internal static bool TryValidateAssetRedirectUri(Uri? candidate, out Uri uri)
    {
        if (candidate is not null &&
            candidate.IsAbsoluteUri &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            candidate.IsDefaultPort &&
            string.IsNullOrEmpty(candidate.UserInfo) &&
            AllowedAssetRedirectHosts.Contains(candidate.Host))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static string? NormalizeSha256Digest(string? digest)
    {
        var match = Sha256DigestPattern().Match(digest ?? string.Empty);
        return match.Success ? match.Groups["hash"].Value.ToLowerInvariant() : null;
    }

    private static UpdateCheckResult Invalid(string reason)
    {
        Log.Info($"Ignored an unverifiable update release. {reason}");
        return new UpdateCheckResult(UpdateCheckStatus.InvalidRelease, Message: reason);
    }

    public void Dispose()
    {
        if (_disposeHttpClient) _httpClient.Dispose();
    }

    [GeneratedRegex(@"^sha256:(?<hash>[a-f0-9]{64})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256DigestPattern();

    [GeneratedRegex(@"^\uFEFF?(?<hash>[a-f0-9]{64})[ \t]+\*?ClipCord-Setup\.exe[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerChecksumPattern();

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto?>? Assets { get; set; } = [];
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
