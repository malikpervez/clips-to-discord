using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ClipsToDiscord;

internal static class UpdateDownloadServiceTests
{
    public static async Task RunAsync(string temporaryRoot)
    {
        var testRoot = Path.Combine(temporaryRoot, "update-downloads");
        Directory.CreateDirectory(testRoot);

        await AssertVerifiedDownloadAndCacheAsync(Path.Combine(testRoot, "verified"));
        await AssertHashMismatchIsDeletedAsync(Path.Combine(testRoot, "bad-hash"));
        await AssertLengthMismatchIsRejectedAsync(Path.Combine(testRoot, "bad-length"));
        await AssertStreamedLengthLimitsAsync(Path.Combine(testRoot, "streamed-length"));
        await AssertRedirectPolicyAsync(Path.Combine(testRoot, "redirects"));
        await AssertInvalidReleaseMetadataAsync(Path.Combine(testRoot, "invalid-metadata"));
        await AssertCancellationAndConcurrencyAsync(Path.Combine(testRoot, "cancellation"));
        AssertInstallerHandoff(Path.Combine(testRoot, "handoff"));
        AssertReparsePointIsRejected(Path.Combine(testRoot, "reparse"));
    }

    private static async Task AssertVerifiedDownloadAndCacheAsync(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(384 * 1024);
        var release = CreateRelease(bytes);
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            requestCount++;
            return Task.FromResult(request.RequestUri == release.InstallerUri
                ? RedirectResponse()
                : AssetResponse(bytes));
        })) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new UpdateDownloadService(client, root);
        var progressValues = new List<UpdateDownloadProgress>();
        var progress = new InlineProgress<UpdateDownloadProgress>(progressValues.Add);
        var versionDirectory = Path.Combine(root, "v" + release.Version);
        Directory.CreateDirectory(versionDirectory);
        var stalePartial = Path.Combine(
            versionDirectory,
            GitHubUpdateChecker.InstallerFileName + ".stale.download");
        File.WriteAllText(stalePartial, "incomplete previous download");

        var downloaded = await service.DownloadAsync(release, progress, CancellationToken.None);
        Assert(File.ReadAllBytes(downloaded.InstallerPath).SequenceEqual(bytes),
            "A verified update download must be committed without changing its bytes.");
        Assert(requestCount == 2,
            "An update download must use the fixed release URL and at most one allowed asset redirect.");
        Assert(progressValues.Count > 0 && progressValues[^1].Percentage == 100,
            "A completed update download must report 100 percent progress.");
        Assert(!Directory.EnumerateFiles(root, "*.download", SearchOption.AllDirectories).Any(),
            "A download must remove both current and crash-left temporary files.");

        var cached = await service.DownloadAsync(release, null, CancellationToken.None);
        Assert(cached.InstallerPath == downloaded.InstallerPath && requestCount == 2,
            "A staged installer must be reused only after its length and digest are reverified.");

        var tamperedBytes = bytes.ToArray();
        tamperedBytes[0] ^= 0xff;
        File.WriteAllBytes(downloaded.InstallerPath, tamperedBytes);
        var repaired = await service.DownloadAsync(release, null, CancellationToken.None);
        Assert(requestCount == 4 && File.ReadAllBytes(repaired.InstallerPath).SequenceEqual(bytes),
            "A same-length tampered cached installer must be rejected and downloaded again.");
    }

    private static async Task AssertHashMismatchIsDeletedAsync(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var release = CreateRelease(bytes) with { InstallerSha256 = new string('0', 64) };
        using var service = CreateService(root, (_, _) => Task.FromResult(AssetResponse(bytes)));
        await AssertThrowsAsync<InvalidDataException>(
            () => service.DownloadAsync(release, null, CancellationToken.None),
            "A digest mismatch must prevent the installer from being staged.");
        Assert(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(),
            "A digest mismatch must delete both the partial and final installer.");
    }

    private static async Task AssertLengthMismatchIsRejectedAsync(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var release = CreateRelease(bytes) with { InstallerSize = bytes.Length + 1L };
        using var service = CreateService(root, (_, _) => Task.FromResult(AssetResponse(bytes)));
        await AssertThrowsAsync<InvalidDataException>(
            () => service.DownloadAsync(release, null, CancellationToken.None),
            "A Content-Length that differs from verified release metadata must be rejected.");
        Assert(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(),
            "A length mismatch must not leave an executable behind.");
    }

    private static async Task AssertStreamedLengthLimitsAsync(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var release = CreateRelease(bytes);
        using (var service = CreateService(
                   Path.Combine(root, "under"),
                   (_, _) => Task.FromResult(StreamedAssetResponse(bytes[..^1]))))
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => service.DownloadAsync(release, null, CancellationToken.None),
                "A streamed installer ending below its verified length must be rejected.");
        }

        using (var service = CreateService(
                   Path.Combine(root, "over"),
                   (_, _) => Task.FromResult(StreamedAssetResponse([.. bytes, 0]))))
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => service.DownloadAsync(release, null, CancellationToken.None),
                "A streamed installer exceeding its verified length must be rejected immediately.");
        }
        Assert(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(),
            "Streamed length failures must not leave executable or partial files behind.");
    }

    private static async Task AssertRedirectPolicyAsync(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(1024);
        var release = CreateRelease(bytes);
        using (var service = CreateService(
                   Path.Combine(root, "unsafe"),
                   (_, _) => Task.FromResult(RedirectResponse("https://example.com/update.exe"))))
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => service.DownloadAsync(release, null, CancellationToken.None),
                "A redirect outside GitHub's release-asset hosts must be rejected.");
        }

        using (var service = CreateService(
                   Path.Combine(root, "double"),
                   (_, _) => Task.FromResult(RedirectResponse())))
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => service.DownloadAsync(release, null, CancellationToken.None),
                "A second redirect must be rejected instead of being followed.");
        }

        var rejectedRedirects = new Uri?[]
        {
            new("http://release-assets.githubusercontent.com/update"),
            new("https://user@release-assets.githubusercontent.com/update"),
            new("https://release-assets.githubusercontent.com:444/update"),
            new("https://release-assets.githubusercontent.com.example.com/update"),
            new("https://example.com/update"),
            new("/relative/update", UriKind.Relative),
            null
        };
        foreach (var candidate in rejectedRedirects)
        {
            Assert(!GitHubUpdateChecker.TryValidateAssetRedirectUri(candidate, out _),
                $"Unsafe asset redirect was accepted: {candidate?.ToString() ?? "<null>"}");
        }
        Assert(GitHubUpdateChecker.TryValidateAssetRedirectUri(
                   new Uri("https://objects.githubusercontent.com/update?signature=value"),
                   out _),
            "A signed HTTPS URL on the alternate GitHub asset host must remain valid.");
    }

    private static async Task AssertInvalidReleaseMetadataAsync(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(1024);
        var valid = CreateRelease(bytes);
        var requestCount = 0;
        using var service = CreateService(root, (_, _) =>
        {
            requestCount++;
            return Task.FromResult(AssetResponse(bytes));
        });
        var invalidReleases = new[]
        {
            valid with { TagName = "v9.8.6" },
            valid with { InstallerUri = new Uri("https://example.com/ClipCord-Setup.exe") },
            valid with { InstallerSha256 = "not-a-digest" },
            valid with { InstallerSize = 0 },
            valid with { InstallerSize = GitHubUpdateChecker.MaximumInstallerBytes + 1 }
        };
        foreach (var release in invalidReleases)
        {
            await AssertThrowsAsync<InvalidDataException>(
                () => service.DownloadAsync(release, null, CancellationToken.None),
                "Invalid release metadata must be rejected before network access.");
        }
        Assert(requestCount == 0,
            "Release metadata validation must run before any installer request is sent.");
    }

    private static async Task AssertCancellationAndConcurrencyAsync(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(1024);
        var release = CreateRelease(bytes);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(root, (_, _) =>
        {
            requestStarted.TrySetResult();
            var content = new StreamContent(new CancellationStream());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var cancellation = new CancellationTokenSource();
        var first = service.DownloadAsync(release, null, cancellation.Token);
        await requestStarted.Task;
        await AssertThrowsAsync<InvalidOperationException>(
            () => service.DownloadAsync(release, null, CancellationToken.None),
            "A concurrent update download must be rejected instead of starting a second writer.");
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            async () => await first,
            "Cancelling an update must stop its active response stream.");
        Assert(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(),
            "Cancelling an update must remove its partial executable.");
    }

    private static void AssertInstallerHandoff(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var release = CreateRelease(bytes);
        var versionRoot = Path.Combine(root, "v" + release.Version);
        Directory.CreateDirectory(versionRoot);
        var installerPath = Path.Combine(versionRoot, GitHubUpdateChecker.InstallerFileName);
        File.WriteAllBytes(installerPath, bytes);
        var request = new UpdateLaunchRequest(release.Version, installerPath, release.InstallerSha256);

        UpdateInstallerLauncher.VerifyInstaller(request, root);
        var startInfo = UpdateInstallerLauncher.CreateStartInfo(request, root);
        Assert(startInfo.UseShellExecute && startInfo.FileName == Path.GetFullPath(installerPath),
            "The handoff must execute only the confined staged installer through Windows ShellExecute.");
        Assert(startInfo.ArgumentList.SequenceEqual([
                "/SILENT", "/NORESTART", "/CLOSEAPPLICATIONS", "/CLIPCORDRESTART=1"
            ]),
            "The in-app update must use the reviewed installer and restart arguments.");

        var handlePreventedWrite = false;
        UpdateInstallerLauncher.Launch(request, root, launchInfo =>
        {
            Assert(launchInfo.FileName == installerPath,
                "The locked file and launched installer path must remain identical.");
            try
            {
                using var writer = new FileStream(
                    installerPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                handlePreventedWrite = true;
            }
            return Process.GetCurrentProcess();
        });
        Assert(handlePreventedWrite,
            "The verified installer must remain protected from writes until Process.Start returns.");

        File.WriteAllBytes(installerPath, [1, 2, 3]);
        AssertThrows<InvalidDataException>(
            () => UpdateInstallerLauncher.VerifyInstaller(request, root),
            "The installer must be rehashed immediately before launch.");
        var outsidePath = Path.Combine(Path.GetDirectoryName(root)!, GitHubUpdateChecker.InstallerFileName);
        var outside = request with { InstallerPath = outsidePath };
        AssertThrows<InvalidDataException>(
            () => UpdateInstallerLauncher.CreateStartInfo(outside, root),
            "The installer handoff must reject executables outside ClipCord's update directory.");
    }

    private static void AssertReparsePointIsRejected(string root)
    {
        var bytes = RandomNumberGenerator.GetBytes(1024);
        var release = CreateRelease(bytes);
        var versionRoot = Path.Combine(root, "v" + release.Version);
        Directory.CreateDirectory(versionRoot);
        var actualPath = Path.Combine(root, "actual-installer.exe");
        var linkedPath = Path.Combine(versionRoot, GitHubUpdateChecker.InstallerFileName);
        File.WriteAllBytes(actualPath, bytes);
        try
        {
            File.CreateSymbolicLink(linkedPath, actualPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        try
        {
            var request = new UpdateLaunchRequest(release.Version, linkedPath, release.InstallerSha256);
            AssertThrows<InvalidDataException>(
                () => UpdateInstallerLauncher.CreateStartInfo(request, root),
                "A reparse-point installer must be rejected before execution.");
        }
        finally
        {
            File.Delete(linkedPath);
        }
    }

    private static UpdateDownloadService CreateService(
        string root,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var client = new HttpClient(new StubHandler(handler)) { Timeout = Timeout.InfiniteTimeSpan };
        return new UpdateDownloadService(client, root, disposeHttpClient: true, TimeSpan.FromSeconds(5));
    }

    private static UpdateRelease CreateRelease(byte[] bytes)
    {
        var version = new StableVersion(9, 8, 7);
        var tag = "v" + version;
        return new UpdateRelease(
            version,
            tag,
            new Uri($"https://github.com/malikpervez/clips-to-discord/releases/tag/{tag}"),
            new Uri($"https://github.com/malikpervez/clips-to-discord/releases/download/{tag}/ClipCord-Setup.exe"),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength);
    }

    private static HttpResponseMessage RedirectResponse(
        string location = "https://release-assets.githubusercontent.com/github-production-release-asset/update")
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage AssetResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static HttpResponseMessage StreamedAssetResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new NonSeekableReadStream(bytes))
    };

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CancellationStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(
                _ => 0,
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _source = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            _source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _source.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
