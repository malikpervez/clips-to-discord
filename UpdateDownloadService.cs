using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace ClipsToDiscord;

internal readonly record struct UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public int Percentage => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(BytesReceived * 100L / TotalBytes, 0, 100);
}

internal sealed record DownloadedUpdate(UpdateRelease Release, string InstallerPath);

internal interface IUpdateDownloadService : IDisposable
{
    Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

internal sealed class UpdateDownloadService : IUpdateDownloadService
{
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(30);
    internal static string DefaultDownloadRoot => Path.Combine(SettingsStore.DataDirectory, "updates");

    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly string _downloadRoot;
    private readonly TimeSpan _operationTimeout;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _lifecycleGate = new();
    private int _activeOperations;
    private bool _disposeRequested;
    private bool _resourcesDisposed;

    public static UpdateDownloadService Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new UpdateDownloadService(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            DefaultDownloadRoot,
            disposeHttpClient: true);
    }

    internal UpdateDownloadService(
        HttpClient httpClient,
        string downloadRoot,
        bool disposeHttpClient = false,
        TimeSpan? operationTimeout = null)
    {
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _downloadRoot = Path.GetFullPath(downloadRoot);
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    public async Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateRelease(release);
        BeginOperation();
        var gateEntered = false;

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);
            deadline.CancelAfter(_operationTimeout);
            var operationToken = deadline.Token;
            if (!await _downloadGate.WaitAsync(0, operationToken))
            {
                throw new InvalidOperationException("An update download is already running.");
            }
            gateEntered = true;
            var versionDirectory = PrepareVersionDirectory(release.Version);
            RemoveStalePartials(versionDirectory);
            var installerPath = Path.Combine(versionDirectory, GitHubUpdateChecker.InstallerFileName);

            if (await IsVerifiedInstallerAsync(installerPath, release, operationToken))
            {
                progress?.Report(new UpdateDownloadProgress(release.InstallerSize, release.InstallerSize));
                return new DownloadedUpdate(release, installerPath);
            }

            if (File.Exists(installerPath)) File.Delete(installerPath);
            var temporaryPath = installerPath + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using var response = await SendInstallerRequestAsync(release, operationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength &&
                    contentLength != release.InstallerSize)
                {
                    throw new InvalidDataException("The installer size did not match the verified release metadata.");
                }

                await DownloadAndVerifyAsync(
                    response,
                    temporaryPath,
                    release,
                    progress,
                    operationToken);
                File.Move(temporaryPath, installerPath, overwrite: true);
                return new DownloadedUpdate(release, installerPath);
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
        finally
        {
            if (gateEntered) _downloadGate.Release();
            EndOperation();
        }
    }

    private async Task<HttpResponseMessage> SendInstallerRequestAsync(
        UpdateRelease release,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(release.InstallerUri);
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!IsRedirect(response.StatusCode)) return response;

        var location = response.Headers.Location;
        response.Dispose();
        if (!GitHubUpdateChecker.TryValidateAssetRedirectUri(location, out var redirectUri))
        {
            throw new InvalidDataException("The installer redirect was not an allowed GitHub asset host.");
        }

        using var redirectRequest = CreateRequest(redirectUri);
        var redirectedResponse = await _httpClient.SendAsync(
            redirectRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (IsRedirect(redirectedResponse.StatusCode))
        {
            redirectedResponse.Dispose();
            throw new InvalidDataException("The installer download exceeded its redirect limit.");
        }
        return redirectedResponse;
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ClipCord-Updater");
        return request;
    }

    private static async Task DownloadAndVerifyAsync(
        HttpResponseMessage response,
        string temporaryPath,
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long received = 0;
        var progressClock = Stopwatch.StartNew();
        progress?.Report(new UpdateDownloadProgress(0, release.InstallerSize));

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            received += read;
            if (received > release.InstallerSize)
            {
                throw new InvalidDataException("The installer exceeded its verified size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (progressClock.Elapsed >= ProgressInterval)
            {
                progress?.Report(new UpdateDownloadProgress(received, release.InstallerSize));
                progressClock.Restart();
            }
        }

        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        if (received != release.InstallerSize)
        {
            throw new InvalidDataException("The installer was incomplete.");
        }

        var actualHash = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(release.InstallerSha256)))
        {
            throw new InvalidDataException("The installer failed SHA-256 verification.");
        }
        progress?.Report(new UpdateDownloadProgress(received, release.InstallerSize));
    }

    private static async Task<bool> IsVerifiedInstallerAsync(
        string path,
        UpdateRelease release,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var file = new FileInfo(path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != release.InstallerSize)
        {
            return false;
        }
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(
            actualHash,
            Convert.FromHexString(release.InstallerSha256));
    }

    private string PrepareVersionDirectory(StableVersion version)
    {
        EnsureSafeDirectory(_downloadRoot);
        var versionDirectory = Path.Combine(_downloadRoot, "v" + version);
        EnsureSafeDirectory(versionDirectory);
        return versionDirectory;
    }

    private static void EnsureSafeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The update directory cannot be a symbolic link or junction.");
        }
    }

    private static void RemoveStalePartials(string versionDirectory)
    {
        var pattern = GitHubUpdateChecker.InstallerFileName + ".*.download";
        foreach (var path in Directory.EnumerateFiles(versionDirectory, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Error("A stale partial update could not be removed; a new unique download will be used.", exception);
            }
        }
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        if (!string.Equals(release.TagName, "v" + release.Version, StringComparison.Ordinal) ||
            !GitHubUpdateChecker.IsExpectedInstallerUri(release.InstallerUri, release.TagName) ||
            release.InstallerSize is <= 0 or > GitHubUpdateChecker.MaximumInstallerBytes)
        {
            throw new InvalidDataException("The update release metadata was invalid.");
        }

        try
        {
            if (string.IsNullOrEmpty(release.InstallerSha256) ||
                release.InstallerSha256.Length != 64 ||
                Convert.FromHexString(release.InstallerSha256).Length != 32)
            {
                throw new InvalidDataException("The installer digest was invalid.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The installer digest was invalid.", exception);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private void BeginOperation()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        lock (_lifecycleGate)
        {
            _activeOperations--;
            if (_disposeRequested && _activeOperations == 0) DisposeResources();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposeRequested) return;
            _disposeRequested = true;
        }

        _disposeCancellation.Cancel();
        lock (_lifecycleGate)
        {
            if (_activeOperations == 0) DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        _downloadGate.Dispose();
        _disposeCancellation.Dispose();
        if (_disposeHttpClient) _httpClient.Dispose();
    }
}
