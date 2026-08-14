using System.Net;
using System.Text;
using System.Text.Json;
using ClipsToDiscord;

internal static class UpdateCheckerTests
{
    private static readonly StableVersion InstalledVersion = new(1, 11, 0);
    private const string ValidDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static async Task RunAsync(string temporaryRoot)
    {
        Assert(StableVersion.TryParse("v1.11.0", out var parsedVersion) && parsedVersion == InstalledVersion,
            "Stable versions must accept the repository's v-prefixed release tags.");
        Assert(!StableVersion.TryParse("v1.5.0-beta.1", out _),
            "Prerelease suffixes must not parse as stable versions.");
        Assert(!StableVersion.TryParse("v01.5.0", out _),
            "Non-canonical leading zeroes must be rejected.");
        Assert(StableVersion.FromAssemblyVersion(new Version(1, 11, 0, 0)) == InstalledVersion,
            "Assembly versions must compare using their major, minor, and build components.");

        var observedUris = new List<Uri>();
        var observedAuthorization = false;
        using (var checker = CreateChecker(
                   BuildReleaseJson(),
                   request =>
                   {
                       observedUris.Add(request.RequestUri!);
                       observedAuthorization |= request.Headers.Authorization is not null;
                   }))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is not null,
                "A newer verified stable release must be offered.");
            var availableRelease = result.Release ?? throw new InvalidOperationException(
                "The available update result did not include release metadata.");
            Assert(availableRelease.Version == new StableVersion(2, 0, 0),
                "The offered release version must match its stable tag.");
            Assert(availableRelease.InstallerSha256 == ValidDigest,
                "The GitHub asset digest must be normalized to a bare lowercase SHA-256 hash.");
            Assert(availableRelease.InstallerSize == 123456,
                "The verified installer size must be carried into the download handoff.");
        }
        Assert(observedUris.SequenceEqual([GitHubUpdateChecker.LatestReleaseApiUri]),
            "A digest-backed update check must query only the fixed latest-release endpoint.");
        Assert(!observedAuthorization, "Anonymous update checks must not attach authorization or application secrets.");

        await AssertStatusAsync(BuildReleaseJson(draft: true), UpdateCheckStatus.UpToDate,
            "Draft releases must be ignored.");
        await AssertStatusAsync(BuildReleaseJson(prerelease: true), UpdateCheckStatus.UpToDate,
            "Prereleases must be ignored in stable mode.");
        await AssertStatusAsync(BuildReleaseJson(tag: "v1.11.0"), UpdateCheckStatus.UpToDate,
            "The installed release must not be offered again.");
        await AssertStatusAsync(BuildReleaseJson(tag: "v1.4.0"), UpdateCheckStatus.UpToDate,
            "An older release must never be offered as a downgrade.");
        await AssertStatusAsync(BuildReleaseJson(tag: "v2.0.0-beta.1"), UpdateCheckStatus.InvalidRelease,
            "A prerelease-shaped tag with a false prerelease flag must still be rejected.");
        await AssertStatusAsync(
            BuildReleaseJson(htmlUrl: "https://github.com/attacker/clips-to-discord/releases/tag/v2.0.0"),
            UpdateCheckStatus.InvalidRelease,
            "A release page outside the expected owner must be rejected.");
        await AssertStatusAsync(
            BuildReleaseJson(htmlUrl: "http://github.com/malikpervez/clips-to-discord/releases/tag/v2.0.0"),
            UpdateCheckStatus.InvalidRelease,
            "A non-HTTPS release page must be rejected.");
        await AssertStatusAsync(BuildReleaseJson(includeInstaller: false), UpdateCheckStatus.InvalidRelease,
            "A release without the exact installer asset must be rejected.");
        await AssertStatusAsync(BuildReleaseJson(duplicateInstaller: true), UpdateCheckStatus.InvalidRelease,
            "A release with duplicate expected installer assets must be rejected.");
        await AssertStatusAsync(
            "{\"tag_name\":\"v2.0.0\",\"html_url\":\"https://github.com/malikpervez/clips-to-discord/releases/tag/v2.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":null}",
            UpdateCheckStatus.InvalidRelease,
            "A release with null asset metadata must be rejected without throwing.");
        await AssertStatusAsync(BuildReleaseJson(digest: "sha256:not-a-hash"), UpdateCheckStatus.InvalidRelease,
            "An installer without a digest or checksum fallback must be rejected.");
        await AssertStatusAsync(
            BuildReleaseJson(installerUrl: "https://example.com/ClipCord-Setup.exe"),
            UpdateCheckStatus.InvalidRelease,
            "An installer URL outside the official repository must be rejected.");
        await AssertStatusAsync(
            BuildReleaseJson(installerUrl: "http://github.com/malikpervez/clips-to-discord/releases/download/v2.0.0/ClipCord-Setup.exe"),
            UpdateCheckStatus.InvalidRelease,
            "A non-HTTPS installer URL must be rejected.");
        await AssertStatusAsync(
            BuildReleaseJson(installerSize: GitHubUpdateChecker.MaximumInstallerBytes + 1),
            UpdateCheckStatus.InvalidRelease,
            "An implausibly large installer must be rejected before a download is offered.");

        var checksumJson = BuildReleaseJson(digest: null, includeChecksum: true);
        var checksumRequests = 0;
        using (var checker = CreateChecker(
                   checksumJson,
                   _ => checksumRequests++,
                   $"{ValidDigest.ToUpperInvariant()}  ClipCord-Setup.exe\r\n"))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.UpdateAvailable &&
                   result.Release?.InstallerSha256 == ValidDigest,
                "A bounded official checksum manifest must verify an installer without an API digest.");
        }
        Assert(checksumRequests == 2, "Checksum fallback must perform exactly one additional fixed asset request.");

        using (var checker = CreateChecker(
                   checksumJson,
                   checksumContent: $"\uFEFF{ValidDigest}  ClipCord-Setup.exe\r\n"))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.UpdateAvailable &&
                   result.Release?.InstallerSha256 == ValidDigest,
                "A UTF-8 BOM at the start of a checksum manifest must be accepted.");
        }

        using (var checker = CreateChecker(
                   checksumJson,
                   checksumContent:
                       $"{ValidDigest}  ClipCord-Setup.exe\r\n{new string('f', 64)}  ClipCord-Setup.exe\r\n"))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.InvalidRelease,
                "A checksum manifest with duplicate installer entries must be rejected as ambiguous.");
        }

        using (var checker = CreateChecker(
                   checksumJson,
                   checksumContent: $"{new string('f', 64)}  Different.exe\n"))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.InvalidRelease,
                "A checksum manifest without the expected installer entry must be rejected.");
        }

        using (var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                   Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
               { Timeout = TimeSpan.FromSeconds(2) })
        using (var checker = new GitHubUpdateChecker(client))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.Failed,
                "Network and GitHub status failures must produce a non-fatal failed result.");
        }

        await AssertFailedResponseAsync(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)),
            "GitHub rate limiting must produce a non-fatal failed result.");
        await AssertFailedResponseAsync(
            (_, _) => throw new HttpRequestException("Simulated offline failure."),
            "Offline failures must produce a non-fatal failed result.");
        await AssertFailedResponseAsync(
            (_, _) => throw new TaskCanceledException("Simulated request timeout."),
            "Request timeouts must produce a non-fatal failed result.");
        using (var checker = CreateChecker("{not valid json"))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.Failed,
                "Malformed JSON must produce a non-fatal failed result.");
        }
        var oversizedReleaseJson = BuildReleaseJson(body: new string('x', 1_048_577));
        using (var checker = CreateChecker(oversizedReleaseJson))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.Failed,
                "An oversized valid release response with a declared length must be rejected before parsing.");
        }
        using (var content = new StreamContent(
                   new NonSeekableReadStream(Encoding.UTF8.GetBytes(oversizedReleaseJson))))
        using (var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                   Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })))
               { Timeout = Timeout.InfiniteTimeSpan })
        using (var checker = new GitHubUpdateChecker(client, operationTimeout: TimeSpan.FromSeconds(2)))
        {
            Assert(content.Headers.ContentLength is null,
                "The streaming size-cap test must not send a Content-Length header.");
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.Failed,
                "An oversized valid streamed release response must be rejected while reading.");
        }

        await AssertChecksumRedirectAllowListAsync();
        await AssertWholeOperationDeadlineAsync();
        await AssertCallerCancellationAsync();
        await AssertConcurrentChecksAreRejectedAsync();
        await AssertPreferencesAndThrottleAsync(temporaryRoot);
        await AssertCoordinatorConcurrencyAsync(temporaryRoot);
    }

    private static async Task AssertChecksumRedirectAllowListAsync()
    {
        var releaseJson = BuildReleaseJson(digest: null, includeChecksum: true);
        var allowedRedirect = new Uri(
            "https://release-assets.githubusercontent.com/github-production-release-asset/test?signature=value");
        var requests = new List<Uri>();
        using (var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
               {
                   requests.Add(request.RequestUri!);
                   if (request.RequestUri == GitHubUpdateChecker.LatestReleaseApiUri)
                   {
                       return Task.FromResult(JsonResponse(releaseJson));
                   }
                   if (request.RequestUri!.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                   {
                       var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                       redirect.Headers.Location = allowedRedirect;
                       return Task.FromResult(redirect);
                   }
                   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(
                           $"{ValidDigest}  ClipCord-Setup.exe\r\n",
                           Encoding.UTF8,
                           "text/plain")
                   });
               })) { Timeout = Timeout.InfiniteTimeSpan })
        using (var checker = new GitHubUpdateChecker(client, operationTimeout: TimeSpan.FromSeconds(2)))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.UpdateAvailable && requests.Count == 3,
                "Checksum fallback must follow one allow-listed GitHub asset redirect.");
        }

        using (var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
               {
                   if (request.RequestUri == GitHubUpdateChecker.LatestReleaseApiUri)
                   {
                       return Task.FromResult(JsonResponse(releaseJson));
                   }
                   var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                   redirect.Headers.Location = new Uri("https://example.com/untrusted-checksums.txt");
                   return Task.FromResult(redirect);
               })) { Timeout = Timeout.InfiniteTimeSpan })
        using (var checker = new GitHubUpdateChecker(client, operationTimeout: TimeSpan.FromSeconds(2)))
        {
            var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
            Assert(result.Status == UpdateCheckStatus.Failed,
                "Checksum fallback must reject redirects outside the GitHub asset host allow-list.");
        }
    }

    private static async Task AssertWholeOperationDeadlineAsync()
    {
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NeverEndingReadStream())
            }))) { Timeout = Timeout.InfiniteTimeSpan };
        using var checker = new GitHubUpdateChecker(
            client,
            operationTimeout: TimeSpan.FromMilliseconds(75));

        var startedAt = DateTime.UtcNow;
        var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
        Assert(result.Status == UpdateCheckStatus.Failed && DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(2),
            "The update deadline must cancel a response body that never completes.");
    }

    private static async Task AssertCallerCancellationAsync()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(BuildReleaseJson());
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var checker = new GitHubUpdateChecker(client, operationTimeout: TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var check = checker.CheckAsync(InstalledVersion, cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        try
        {
            await check;
            throw new InvalidOperationException("Caller cancellation should propagate from the update checker.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task AssertConcurrentChecksAreRejectedAsync()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await releaseRequest.Task.WaitAsync(cancellationToken);
            return JsonResponse(BuildReleaseJson());
        })) { Timeout = TimeSpan.FromSeconds(2) };
        using var checker = new GitHubUpdateChecker(client);

        var first = checker.CheckAsync(InstalledVersion, CancellationToken.None);
        await requestStarted.Task;
        var second = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
        Assert(second.Status == UpdateCheckStatus.Busy,
            "A concurrent update check must return immediately instead of starting another request.");
        releaseRequest.TrySetResult();
        Assert((await first).Status == UpdateCheckStatus.UpdateAvailable,
            "The original update check must complete normally after a concurrent attempt is rejected.");
    }

    private static async Task AssertPreferencesAndThrottleAsync(string temporaryRoot)
    {
        var preferencePath = Path.Combine(temporaryRoot, "update-preferences", "updates.json");
        var store = new UpdatePreferencesStore(preferencePath);
        var now = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var release = CreateRelease(new StableVersion(2, 0, 0));
        using var checker = new StubUpdateChecker(UpdateCheckResult.Available(release));
        using var coordinator = new UpdateCoordinator(checker, store, InstalledVersion, () => now);

        var first = await coordinator.CheckAsync(manual: false, CancellationToken.None);
        Assert(first.Status == UpdateCheckStatus.UpdateAvailable && checker.CheckCount == 1,
            "A due automatic check must query the release source.");
        var immediate = await coordinator.CheckAsync(manual: false, CancellationToken.None);
        Assert(immediate.Status == UpdateCheckStatus.NotDue && checker.CheckCount == 1,
            "Automatic checks must be limited to one per 24 hours.");
        var manual = await coordinator.CheckAsync(manual: true, CancellationToken.None);
        Assert(manual.Status == UpdateCheckStatus.UpdateAvailable && checker.CheckCount == 2,
            "An explicit manual check must bypass the automatic 24-hour throttle.");

        Assert(coordinator.Skip(release), "Skipping a version must persist successfully.");
        var saved = store.Load();
        Assert(saved.SkippedVersion == "2.0.0" && saved.LastAutomaticCheckUtc == now,
            "Skip and automatic-check preferences must survive reload.");
        Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(preferencePath)!, "*.tmp").Any(),
            "Atomic update-preference writes must not leave temporary files behind.");

        now += UpdateCoordinator.AutomaticCheckInterval + TimeSpan.FromMinutes(1);
        var skipped = await coordinator.CheckAsync(manual: false, CancellationToken.None);
        Assert(skipped.Status == UpdateCheckStatus.Suppressed,
            "A skipped version must remain hidden during later automatic checks.");

        Assert(coordinator.RemindLater(release), "Remind-me-later must persist successfully.");
        saved = store.Load();
        Assert(saved.SkippedVersion is null &&
               saved.RemindVersion == "2.0.0" &&
               saved.RemindAfterUtc == now + UpdateCoordinator.ReminderInterval,
            "Remind-me-later must replace skip state and persist its deadline atomically.");

        var olderRelease = CreateRelease(new StableVersion(1, 9, 0));
        Assert(coordinator.Skip(olderRelease), "An older release skip must persist successfully.");
        Assert(coordinator.RemindLater(release), "A newer release reminder must persist successfully.");
        saved = store.Load();
        Assert(saved.SkippedVersion == "1.9.0" && saved.RemindVersion == "2.0.0",
            "Deferring a newer release must preserve an unrelated skipped version.");

        var reminderPath = Path.Combine(temporaryRoot, "reminder-preferences", "updates.json");
        var reminderStore = new UpdatePreferencesStore(reminderPath);
        reminderStore.Save(new UpdatePreferences(
            now - UpdateCoordinator.AutomaticCheckInterval - TimeSpan.FromMinutes(1),
            RemindVersion: "2.0.0",
            RemindAfterUtc: now + TimeSpan.FromHours(1)));
        using (var reminderChecker = new StubUpdateChecker(UpdateCheckResult.Available(release)))
        using (var reminderCoordinator = new UpdateCoordinator(
                   reminderChecker,
                   reminderStore,
                   InstalledVersion,
                   () => now))
        {
            Assert((await reminderCoordinator.CheckAsync(manual: false, CancellationToken.None)).Status ==
                   UpdateCheckStatus.Suppressed,
                "An unexpired reminder must suppress the same release during automatic checks.");
        }

        reminderStore.Save(new UpdatePreferences(
            now - UpdateCoordinator.AutomaticCheckInterval - TimeSpan.FromMinutes(1),
            RemindVersion: "2.0.0",
            RemindAfterUtc: now - TimeSpan.FromMinutes(1)));
        using (var expiredChecker = new StubUpdateChecker(UpdateCheckResult.Available(release)))
        using (var expiredCoordinator = new UpdateCoordinator(
                   expiredChecker,
                   reminderStore,
                   InstalledVersion,
                   () => now))
        {
            Assert((await expiredCoordinator.CheckAsync(manual: false, CancellationToken.None)).Status ==
                   UpdateCheckStatus.UpdateAvailable,
                "An expired reminder must offer the release again.");
        }

        reminderStore.Save(new UpdatePreferences(
            now - UpdateCoordinator.AutomaticCheckInterval - TimeSpan.FromMinutes(1),
            RemindVersion: "2.0.0",
            RemindAfterUtc: now + TimeSpan.FromDays(3650)));
        using (var skewChecker = new StubUpdateChecker(UpdateCheckResult.Available(release)))
        using (var skewCoordinator = new UpdateCoordinator(
                   skewChecker,
                   reminderStore,
                   InstalledVersion,
                   () => now))
        {
            Assert((await skewCoordinator.CheckAsync(manual: false, CancellationToken.None)).Status ==
                   UpdateCheckStatus.UpdateAvailable,
                "A far-future reminder caused by clock skew must not suppress updates indefinitely.");
        }

        await File.WriteAllTextAsync(preferencePath, "{not valid json");
        var recovered = store.Load();
        Assert(recovered == new UpdatePreferences(),
            "Corrupt update preferences must migrate to a safe empty default without stopping the app.");

        await File.WriteAllTextAsync(preferencePath, "{\"Version\":0,\"SkippedVersion\":\"2.0.0\"}");
        recovered = store.Load();
        Assert(recovered == new UpdatePreferences(),
            "An older update-preference schema must migrate to safe defaults.");
    }

    private static async Task AssertCoordinatorConcurrencyAsync(string temporaryRoot)
    {
        var preferencePath = Path.Combine(temporaryRoot, "coordinator-concurrency", "updates.json");
        var release = CreateRelease(new StableVersion(2, 0, 0));
        using var checker = new BlockingUpdateChecker(UpdateCheckResult.Available(release));
        using var coordinator = new UpdateCoordinator(
            checker,
            new UpdatePreferencesStore(preferencePath),
            InstalledVersion);

        var first = coordinator.CheckAsync(manual: true, CancellationToken.None);
        await checker.Started.Task;
        var second = await coordinator.CheckAsync(manual: true, CancellationToken.None);
        Assert(second.Status == UpdateCheckStatus.Busy,
            "The coordinator must reject a concurrent manual or automatic check.");
        checker.Release.TrySetResult();
        Assert((await first).Status == UpdateCheckStatus.UpdateAvailable,
            "The coordinator's original check must complete after rejecting a concurrent attempt.");
    }

    private static async Task AssertStatusAsync(
        string releaseJson,
        UpdateCheckStatus expected,
        string message)
    {
        using var checker = CreateChecker(releaseJson);
        var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
        Assert(result.Status == expected, message);
    }

    private static async Task AssertFailedResponseAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        string message)
    {
        using var client = new HttpClient(new StubHttpMessageHandler(handler))
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        using var checker = new GitHubUpdateChecker(client);
        var result = await checker.CheckAsync(InstalledVersion, CancellationToken.None);
        Assert(result.Status == UpdateCheckStatus.Failed, message);
    }

    private static GitHubUpdateChecker CreateChecker(
        string releaseJson,
        Action<HttpRequestMessage>? observeRequest = null,
        string? checksumContent = null)
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            observeRequest?.Invoke(request);
            if (request.RequestUri == GitHubUpdateChecker.LatestReleaseApiUri)
            {
                return Task.FromResult(JsonResponse(releaseJson));
            }

            if (checksumContent is not null &&
                request.RequestUri?.AbsolutePath.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(checksumContent, Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        return new GitHubUpdateChecker(client, disposeHttpClient: true);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string BuildReleaseJson(
        string tag = "v2.0.0",
        bool draft = false,
        bool prerelease = false,
        string? htmlUrl = null,
        bool includeInstaller = true,
        string? digest = "sha256:" + ValidDigest,
        string? installerUrl = null,
        bool includeChecksum = false,
        bool duplicateInstaller = false,
        long installerSize = 123456,
        string? body = null)
    {
        htmlUrl ??= $"https://github.com/malikpervez/clips-to-discord/releases/tag/{tag}";
        installerUrl ??=
            $"https://github.com/malikpervez/clips-to-discord/releases/download/{tag}/ClipCord-Setup.exe";
        var assets = new List<Dictionary<string, object?>>();
        if (includeInstaller)
        {
            var installer = new Dictionary<string, object?>
            {
                ["name"] = "ClipCord-Setup.exe",
                ["state"] = "uploaded",
                ["size"] = installerSize,
                ["digest"] = digest,
                ["browser_download_url"] = installerUrl
            };
            assets.Add(installer);
            if (duplicateInstaller) assets.Add(new Dictionary<string, object?>(installer));
        }
        if (includeChecksum)
        {
            assets.Add(new Dictionary<string, object?>
            {
                ["name"] = "SHA256SUMS.txt",
                ["state"] = "uploaded",
                ["size"] = 186,
                ["digest"] = "sha256:" + new string('a', 64),
                ["browser_download_url"] =
                    $"https://github.com/malikpervez/clips-to-discord/releases/download/{tag}/SHA256SUMS.txt"
            });
        }

        var release = new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["html_url"] = htmlUrl,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["assets"] = assets
        };
        if (body is not null) release["body"] = body;
        return JsonSerializer.Serialize(release);
    }

    internal static UpdateRelease CreateRelease(StableVersion version)
    {
        var tag = "v" + version;
        return new UpdateRelease(
            version,
            tag,
            new Uri($"https://github.com/malikpervez/clips-to-discord/releases/tag/{tag}"),
            new Uri($"https://github.com/malikpervez/clips-to-discord/releases/download/{tag}/ClipCord-Setup.exe"),
            ValidDigest,
            123456);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class NeverEndingReadStream : Stream
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

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingUpdateChecker(UpdateCheckResult result) : IUpdateChecker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpdateCheckResult> CheckAsync(
            StableVersion currentVersion,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubUpdateChecker(UpdateCheckResult result) : IUpdateChecker
    {
        public int CheckCount { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(
            StableVersion currentVersion,
            CancellationToken cancellationToken)
        {
            CheckCount++;
            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }
}
