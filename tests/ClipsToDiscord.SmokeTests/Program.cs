using System.Collections.Concurrent;
using ClipsToDiscord;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "ClipsToDiscordTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);

try
{
    var existingCaseRoot = Path.Combine(temporaryRoot, "existing-case");
    Directory.CreateDirectory(existingCaseRoot);
    var capitalizedFolder = Directory.CreateDirectory(Path.Combine(existingCaseRoot, "Uploaded")).FullName;
    var resolvedCapitalizedFolder = UploadedFolder.GetOrCreate(existingCaseRoot);

    Assert(
        resolvedCapitalizedFolder.Equals(capitalizedFolder, StringComparison.Ordinal),
        $"Expected existing folder '{capitalizedFolder}', got '{resolvedCapitalizedFolder}'.");
    Assert(
        Directory.EnumerateDirectories(existingCaseRoot).Count() == 1,
        "Resolving Uploaded must not create a second folder.");

    var missingRoot = Path.Combine(temporaryRoot, "missing");
    Directory.CreateDirectory(missingRoot);
    var createdFolder = UploadedFolder.GetOrCreate(missingRoot);
    Assert(Directory.Exists(createdFolder), "The archive folder was not created.");
    Assert(
        Path.GetFileName(createdFolder).Equals("uploaded", StringComparison.Ordinal),
        "A newly created archive folder must use the canonical lowercase name.");

    Assert(
        UploadedFolder.GetGameFolderName("Battlefield™-6__2026-08-03__13-43-46.mp4") == "Battlefield™-6",
        "SteelSeries timestamps must be removed from the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("Counter-Strike 2 2026.08.03 - 13.43.46.01.mp4") == "Counter-Strike 2",
        "Dotted recording timestamps must be removed from the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("Game_20260803_134346.mp4") == "Game",
        "Compact recording timestamps must be removed from the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("manual-highlight.mp4") == "Uncategorized",
        "A filename without a recognizable game prefix must use Uncategorized.");
    Assert(
        UploadedFolder.GetGameFolderName("Game__not-a-timestamp.mp4") == "Uncategorized",
        "A double underscore without a recognized timestamp must use Uncategorized.");
    Assert(
        UploadedFolder.GetGameFolderName("Apex Legends 2026.08.03 - 13.43.46.01.DVR.mp4") == "Apex Legends",
        "DVR filename suffixes must not become part of the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("CON__2026-08-03__13-43-46.mp4") == "_CON",
        "Reserved Windows device names must be made safe for folders.");

    var gameArchiveRoot = Path.Combine(temporaryRoot, "game-archive");
    Directory.CreateDirectory(gameArchiveRoot);
    var gameUploadedFolder = Directory.CreateDirectory(Path.Combine(gameArchiveRoot, "Uploaded")).FullName;
    var existingGameFolder = Directory.CreateDirectory(Path.Combine(gameUploadedFolder, "Battlefield™-6")).FullName;
    var resolvedGameFolder = UploadedFolder.GetOrCreateForClip(
        gameArchiveRoot,
        "battlefield™-6__2026-08-03__13-43-46.mp4");
    Assert(
        resolvedGameFolder.Equals(existingGameFolder, StringComparison.Ordinal),
        "Game folders must be reused case-insensitively.");
    Assert(
        Directory.EnumerateDirectories(gameUploadedFolder).Count() == 1,
        "Resolving a differently-cased game name must not create a duplicate folder.");

    var rootArchiveClip = Path.Combine(gameUploadedFolder, "legacy-root.mp4");
    var nestedArchiveClip = Path.Combine(existingGameFolder, "nested.mp4");
    await File.WriteAllBytesAsync(rootArchiveClip, [1, 1, 2, 3]);
    await File.WriteAllBytesAsync(nestedArchiveClip, [5, 8, 13, 21]);
    var archivedClips = UploadedFolder.EnumerateArchivedClips(gameUploadedFolder).ToHashSet(
        StringComparer.OrdinalIgnoreCase);
    Assert(archivedClips.Contains(rootArchiveClip), "Archived baseline enumeration must retain legacy root clips.");
    Assert(archivedClips.Contains(nestedArchiveClip), "Archived baseline enumeration must include game subfolders.");

    var gameBaselineStateDirectory = Path.Combine(temporaryRoot, "game-baseline-state");
    Directory.CreateDirectory(gameBaselineStateDirectory);
    var gameBaselineStore = new WatchStateStore(
        Path.Combine(gameBaselineStateDirectory, "state.json"),
        Path.Combine(gameBaselineStateDirectory, ".safe-baseline-required"));
    var gameBaselineState = await gameBaselineStore.LoadOrInitializeAsync(
        gameArchiveRoot,
        _ => { },
        CancellationToken.None);
    Assert(
        gameBaselineState.UploadedContentHashes.Count == 2,
        "Safe baseline state must mark root-level and game-subfolder archives as uploaded.");

    var recoveryRoot = Path.Combine(temporaryRoot, "safe-baseline-recovery");
    var recoveryClips = Path.Combine(recoveryRoot, "clips");
    var recoveryStateDirectory = Path.Combine(recoveryRoot, "state");
    Directory.CreateDirectory(recoveryClips);
    Directory.CreateDirectory(recoveryStateDirectory);
    var pendingMovePath = Path.Combine(recoveryClips, "uploaded-but-not-moved.mp4");
    await File.WriteAllBytesAsync(pendingMovePath, new byte[] { 9, 8, 7, 6 });
    var recoveryStatePath = Path.Combine(recoveryStateDirectory, "state.json");
    var recoveryMarkerPath = Path.Combine(recoveryStateDirectory, ".safe-baseline-required");
    var recoveryStore = new WatchStateStore(recoveryStatePath, recoveryMarkerPath);
    recoveryStore.Save(new WatchState
    {
        Version = 2,
        ClipsFolder = recoveryClips,
        PendingMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pendingMovePath }
    });
    await File.WriteAllTextAsync(recoveryMarkerPath, DateTime.UtcNow.ToString("O"));
    var recoveredState = await recoveryStore.LoadOrInitializeAsync(
        recoveryClips,
        _ => { },
        CancellationToken.None);
    Assert(
        recoveredState.PendingMoves.Contains(pendingMovePath),
        "A forced safe-baseline rebuild must preserve readable pending moves.");
    Assert(
        recoveredState.KnownContentHashes.Count == 1,
        "A forced safe-baseline rebuild must hash existing clips.");
    Assert(!File.Exists(recoveryMarkerPath), "The recovery marker must clear after the baseline is durably saved.");

    var readinessPath = Path.Combine(temporaryRoot, "readiness.mp4");
    await File.WriteAllBytesAsync(readinessPath, new byte[] { 1, 2, 3, 4 });
    File.SetLastWriteTimeUtc(readinessPath, DateTime.UtcNow.AddMinutes(-1));
    var readinessTracker = new FileReadinessTracker();
    var firstObservationAt = DateTime.UtcNow;
    var firstObservation = readinessTracker.Observe(new FileInfo(readinessPath), firstObservationAt);
    var stableObservation = readinessTracker.Observe(
        new FileInfo(readinessPath),
        firstObservationAt.AddSeconds(11));
    Assert(!firstObservation.IsReady, "A file must not be ready on its first observation.");
    Assert(stableObservation.IsReady, "An unchanged readable file must become ready after the stability window.");

    var changingPath = Path.Combine(temporaryRoot, "changing.mp4");
    await File.WriteAllBytesAsync(changingPath, new byte[] { 1, 2, 3 });
    File.SetLastWriteTimeUtc(changingPath, DateTime.UtcNow.AddMinutes(-1));
    var changingTracker = new FileReadinessTracker();
    var changingObservedAt = DateTime.UtcNow;
    changingTracker.Observe(new FileInfo(changingPath), changingObservedAt);
    await File.AppendAllTextAsync(changingPath, "more data");
    var changedObservation = changingTracker.Observe(
        new FileInfo(changingPath),
        changingObservedAt.AddSeconds(11));
    Assert(!changedObservation.IsReady, "A file whose length changed between observations must not be ready.");

    var youngPath = Path.Combine(temporaryRoot, "young.mp4");
    await File.WriteAllBytesAsync(youngPath, new byte[] { 4, 3, 2, 1 });
    var youngLastWrite = File.GetLastWriteTimeUtc(youngPath);
    var youngTracker = new FileReadinessTracker();
    youngTracker.Observe(new FileInfo(youngPath), youngLastWrite);
    var youngObservation = youngTracker.Observe(new FileInfo(youngPath), youngLastWrite.AddSeconds(11));
    var oldEnoughObservation = youngTracker.Observe(new FileInfo(youngPath), youngLastWrite.AddSeconds(21));
    Assert(!youngObservation.IsReady, "A stable file younger than 20 seconds must not be ready.");
    Assert(oldEnoughObservation.IsReady, "A stable file older than 20 seconds must become ready.");

    var sharedReaderPath = Path.Combine(temporaryRoot, "shared-reader.mp4");
    await File.WriteAllBytesAsync(sharedReaderPath, new byte[] { 8, 7, 6, 5 });
    File.SetLastWriteTimeUtc(sharedReaderPath, DateTime.UtcNow.AddMinutes(-1));
    var sharedReaderTracker = new FileReadinessTracker();
    var sharedReaderObservedAt = DateTime.UtcNow;
    sharedReaderTracker.Observe(new FileInfo(sharedReaderPath), sharedReaderObservedAt);
    using (var reader = new FileStream(sharedReaderPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        var sharedReaderResult = sharedReaderTracker.Observe(
            new FileInfo(sharedReaderPath),
            sharedReaderObservedAt.AddSeconds(11));
        Assert(sharedReaderResult.IsReady, "Another reader must not permanently block a completed clip.");
    }

    var lockedPath = Path.Combine(temporaryRoot, "locked.mp4");
    await File.WriteAllBytesAsync(lockedPath, new byte[] { 5, 6, 7, 8 });
    File.SetLastWriteTimeUtc(lockedPath, DateTime.UtcNow.AddMinutes(-1));
    var lockedTracker = new FileReadinessTracker();
    var lockedObservedAt = DateTime.UtcNow;
    lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt);
    FileReadinessResult lockedResult;
    FileReadinessResult secondLockedResult;
    FileReadinessResult thirdLockedResult;
    using (var lockedStream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
    {
        lockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(11));
        secondLockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(22));
        thirdLockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(43));
    }
    Assert(!lockedResult.IsReady, "A file with an active writer must not be ready.");
    Assert(
        lockedResult.NextCheckUtc > lockedObservedAt.AddSeconds(11),
        "A locked file must receive a future retry time.");
    Assert(
        secondLockedResult.NextCheckUtc - lockedObservedAt.AddSeconds(22) >= TimeSpan.FromSeconds(20),
        "Repeated lock failures must increase the readiness backoff.");
    Assert(
        thirdLockedResult.ConsecutiveOpenFailures == FileReadinessTracker.StuckLogThreshold,
        "A repeatedly writer-locked file must reach the explicit stuck-log threshold.");

    var identityPath = Path.Combine(temporaryRoot, "identity.mp4");
    await File.WriteAllBytesAsync(identityPath, new byte[] { 10, 20, 30, 40, 50 });
    var originalHash = await ContentIdentity.ComputeSha256Async(identityPath, CancellationToken.None);
    var renamedIdentityPath = Path.Combine(temporaryRoot, "renamed-identity.mp4");
    File.Move(identityPath, renamedIdentityPath);
    File.SetLastWriteTimeUtc(renamedIdentityPath, DateTime.UtcNow.AddHours(-1));
    var renamedHash = await ContentIdentity.ComputeSha256Async(renamedIdentityPath, CancellationToken.None);
    Assert(originalHash == renamedHash, "Content identity must survive path and timestamp changes.");

    var apiRoot = "https://discord.com/api/";
    var unversionedWebhook = apiRoot + "webhooks/" + "123456" + "/test-token";
    var versionedWebhook = apiRoot + "v10/webhooks/" + "123456" + "/test-token";
    Assert(WebhookValidation.IsDiscordWebhook(unversionedWebhook), "An unversioned Discord webhook must be accepted.");
    Assert(WebhookValidation.IsDiscordWebhook(versionedWebhook), "A versioned Discord webhook must be accepted.");
    Assert(
        !WebhookValidation.IsDiscordWebhook("https://example.com/api/v10/webhooks/123456/test-token"),
        "A non-Discord webhook host must be rejected.");
    Assert(
        !WebhookValidation.IsDiscordWebhook(apiRoot + "v10/channels/123456"),
        "A non-webhook Discord API path must be rejected.");

    SensitiveDataRedactor.RegisterSecret(unversionedWebhook);
    var redactedExactSecret = SensitiveDataRedactor.Redact("Request failed: " + unversionedWebhook);
    var redactedVersionedSecret = SensitiveDataRedactor.Redact("Request failed: " + versionedWebhook);
    Assert(!redactedExactSecret.Contains("test-token"), "A registered webhook must be removed from logs.");
    Assert(!redactedVersionedSecret.Contains("test-token"), "A versioned webhook must be removed from logs.");
    Assert(
        redactedVersionedSecret.Contains("[REDACTED DISCORD WEBHOOK]"),
        "Webhook redaction must leave a useful placeholder.");

    var compressionTargets = CompressionTargetPlanner.Build(25);
    Assert(compressionTargets[0] == 25, "Compression fallback must begin at the configured target.");
    Assert(compressionTargets.Contains(9), "Compression fallback must include the lower-limit target.");
    Assert(
        compressionTargets.Zip(compressionTargets.Skip(1)).All(pair => pair.First > pair.Second),
        "Compression fallback targets must decrease strictly.");
    Assert(
        AppSettings.Empty.CompressionTargetMb == 95,
        "New settings must default to a 95 MB compression target.");
    var defaultCompressionTargets = CompressionTargetPlanner.Build(AppSettings.DefaultCompressionTargetMb);
    Assert(defaultCompressionTargets[0] == 95, "Default compression fallback must begin at 95 MB.");
    Assert(defaultCompressionTargets.Contains(9), "Default compression fallback must still reach 9 MB.");

    var detectorResponses = new ConcurrentQueue<bool>(
        [true, false, false, true, false, false, false, true]);
    var watcherStarts = 0;
    var watcherCancellations = 0;
    var cancellationsAtDebounceReset = -1;
    var detectorCalls = 0;
    bool SimulatedDiscordDetector()
    {
        var call = Interlocked.Increment(ref detectorCalls);
        var response = detectorResponses.TryDequeue(out var queuedResponse) ? queuedResponse : true;
        if (call == 4)
        {
            cancellationsAtDebounceReset = Volatile.Read(ref watcherCancellations);
        }
        return response;
    }

    async Task SimulatedWatcher(
        AppSettings ignoredSettings,
        Action<string> ignoredStatus,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref watcherStarts);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref watcherCancellations);
            throw;
        }
    }

    var controllerOptions = new DiscordControllerOptions(
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(5),
        3,
        TimeSpan.FromSeconds(1));
    var controller = new DiscordAwareController(
        AppSettings.Empty,
        _ => { },
        SimulatedDiscordDetector,
        SimulatedWatcher,
        controllerOptions);
    await WaitUntilAsync(
        () => Volatile.Read(ref watcherCancellations) >= 1,
        TimeSpan.FromSeconds(2),
        "The watcher was not cancelled after three consecutive absent polls.");
    Assert(cancellationsAtDebounceReset == 0, "Two absent polls must not stop the watcher.");
    await WaitUntilAsync(
        () => Volatile.Read(ref watcherStarts) >= 2,
        TimeSpan.FromSeconds(2),
        "The watcher did not restart after Discord returned.");
    controller.Dispose();
    Assert(
        Volatile.Read(ref watcherCancellations) == 2,
        "Controller disposal must cancel and await the active watcher.");

    Console.WriteLine("All smoke tests passed.");
}
finally
{
    Directory.Delete(temporaryRoot, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
{
    var deadline = DateTime.UtcNow.Add(timeout);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(failureMessage);
        await Task.Delay(TimeSpan.FromMilliseconds(10));
    }
}
