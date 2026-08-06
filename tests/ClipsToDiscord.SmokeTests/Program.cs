using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using ClipsToDiscord;

Application.SetHighDpiMode(HighDpiMode.SystemAware);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

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

    var localOnlyCaseRoot = Path.Combine(temporaryRoot, "local-only-case");
    Directory.CreateDirectory(localOnlyCaseRoot);
    var capitalizedLocalOnly = Directory.CreateDirectory(Path.Combine(localOnlyCaseRoot, "Local-Only")).FullName;
    var resolvedLocalOnly = UploadedFolder.GetOrCreateLocalOnly(localOnlyCaseRoot);
    Assert(
        resolvedLocalOnly.Equals(capitalizedLocalOnly, StringComparison.Ordinal),
        "Local-only archive resolution must reuse an existing differently-cased folder.");
    Assert(
        Directory.EnumerateDirectories(localOnlyCaseRoot).Count() == 1,
        "Resolving Local-Only must not create a second folder.");

    var missingLocalOnlyRoot = Path.Combine(temporaryRoot, "missing-local-only");
    Directory.CreateDirectory(missingLocalOnlyRoot);
    var createdLocalOnlyFolder = UploadedFolder.GetOrCreateLocalOnly(missingLocalOnlyRoot);
    Assert(
        Path.GetFileName(createdLocalOnlyFolder).Equals("local-only", StringComparison.Ordinal),
        "A newly created local-only folder must use the canonical lowercase name.");

    var reparseArchiveRoot = Path.Combine(temporaryRoot, "local-only-reparse-root");
    var reparseTarget = Path.Combine(temporaryRoot, "local-only-reparse-target");
    var reparseLocalOnly = Path.Combine(reparseArchiveRoot, "local-only");
    Directory.CreateDirectory(reparseArchiveRoot);
    Directory.CreateDirectory(reparseTarget);
    var reparsePayload = Path.Combine(reparseTarget, "must-remain.txt");
    await File.WriteAllTextAsync(reparsePayload, "preserve target");
    CreateDirectoryJunction(reparseLocalOnly, reparseTarget);
    try
    {
        var rejected = false;
        try
        {
            UploadedFolder.GetOrCreateLocalOnly(reparseArchiveRoot);
        }
        catch (IOException)
        {
            rejected = true;
        }
        Assert(rejected, "A local-only archive root must reject symbolic links and junctions.");
    }
    finally
    {
        Directory.Delete(reparseLocalOnly);
    }
    Assert(File.Exists(reparsePayload), "Removing the test junction must not remove its target contents.");

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

    var gameLocalOnlyFolder = Directory.CreateDirectory(Path.Combine(gameArchiveRoot, "Local-Only")).FullName;
    var existingLocalOnlyGameFolder = Directory.CreateDirectory(Path.Combine(gameLocalOnlyFolder, "Battlefield™-6")).FullName;
    var resolvedLocalOnlyGameFolder = UploadedFolder.GetOrCreateLocalOnlyForClip(
        gameArchiveRoot,
        "battlefield™-6__2026-08-03__13-43-46.mp4");
    Assert(
        resolvedLocalOnlyGameFolder.Equals(existingLocalOnlyGameFolder, StringComparison.Ordinal),
        "Local-only game folders must be reused case-insensitively.");
    var localOnlyRootClip = Path.Combine(gameLocalOnlyFolder, "local-root.mp4");
    var localOnlyNestedClip = Path.Combine(existingLocalOnlyGameFolder, "local-nested.mp4");
    await File.WriteAllBytesAsync(localOnlyRootClip, [34, 55, 89]);
    await File.WriteAllBytesAsync(localOnlyNestedClip, [144, 233, 121]);

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
    Assert(
        gameBaselineState.LocalOnlyContentHashes.Count == 2,
        "Safe baseline state must recognize root-level and game-subfolder local-only clips.");
    Assert(
        gameBaselineState.LocalOnlyContentHashes.All(hash => !gameBaselineState.UploadedContentHashes.Contains(hash)),
        "Local-only baseline clips must never be classified as uploaded.");

    var v2UpgradeStateDirectory = Path.Combine(temporaryRoot, "v2-local-only-upgrade");
    Directory.CreateDirectory(v2UpgradeStateDirectory);
    var v2UpgradeStore = new WatchStateStore(
        Path.Combine(v2UpgradeStateDirectory, "state.json"),
        Path.Combine(v2UpgradeStateDirectory, ".safe-baseline-required"));
    v2UpgradeStore.Save(new WatchState { Version = 2, ClipsFolder = gameArchiveRoot });
    var upgradedV2State = await v2UpgradeStore.LoadOrInitializeAsync(
        gameArchiveRoot,
        _ => { },
        CancellationToken.None);
    Assert(
        upgradedV2State.Version == 3 && upgradedV2State.LocalOnlyContentHashes.Count == 2,
        "A v2 state upgrade must baseline existing local-only archives without treating them as uploads.");

    var localOnlySettings = new AppSettings(
        gameArchiveRoot,
        string.Empty,
        true,
        AppSettings.DefaultCompressionTargetMb,
        AppSettings.DefaultUploaderName,
        false);
    Assert(localOnlySettings.IsValid, "Local-only mode must not require a Discord webhook.");
    Assert(
        !(localOnlySettings with { UploadToDiscord = true }).IsValid,
        "Enabling Discord uploads must still require a valid webhook.");
    Assert(AppSettings.Empty.UploadToDiscord, "New installations must default to Discord uploads enabled.");

    Assert(
        AppSettings.NormalizeUploaderName("  Malik   Pervez  ") == "Malik Pervez",
        "Uploader names must trim and collapse whitespace.");
    Assert(
        !string.IsNullOrWhiteSpace(AppSettings.NormalizeUploaderName(null)),
        "Existing settings without an uploader name must receive a safe default.");
    var surrogateBoundaryName = new string('a', AppSettings.MaximumUploaderNameLength - 1) + "😀";
    Assert(
        AppSettings.NormalizeUploaderName(surrogateBoundaryName) ==
        new string('a', AppSettings.MaximumUploaderNameLength - 1),
        "Uploader-name truncation must not retain a lone UTF-16 surrogate.");
    Assert(
        DiscordClipMessage.BuildDescription("Malik", "Battlefield™-6__2026-08-03__13-43-46.mp4") ==
        "Malik uploaded a clip from Battlefield™-6.",
        "Timestamped clips must identify the uploader and parsed game.");
    Assert(
        DiscordClipMessage.BuildContent("Malik", "Battlefield™-6__2026-08-03__13-43-46.mp4") ==
        "Malik uploaded a clip from Battlefield™-6.",
        "Ordinary game-name punctuation must not gain visible Markdown escape characters.");
    Assert(
        DiscordClipMessage.BuildContent("player_*one*", "manual-highlight.mp4") ==
        "player\\_\\*one\\* uploaded a clip.",
        "Uploader names must be escaped and unrecognized games must not claim a game name.");
    using (var payload = JsonDocument.Parse(DiscordWebhookClient.BuildUploadPayload(
               "clip.mp4",
               "Malik uploaded a clip from Battlefield™-6.",
               "Malik uploaded a clip from Battlefield™-6.")))
    {
        var root = payload.RootElement;
        Assert(root.GetProperty("content").GetString() == "Malik uploaded a clip from Battlefield™-6.",
            "The visible Discord message must contain uploader attribution.");
        var attachment = root.GetProperty("attachments")[0];
        Assert(attachment.GetProperty("id").GetInt32() == 0 &&
               attachment.GetProperty("filename").GetString() == "clip.mp4" &&
               attachment.GetProperty("description").GetString() == "Malik uploaded a clip from Battlefield™-6.",
            "The attachment description must contain matching uploader attribution.");
        Assert(root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() == 0,
            "Uploader-controlled text must not enable Discord mentions.");
    }

    Assert(SettingsForm.TryParseCompressionTarget("95 MB", out var compression95) && compression95 == 95,
        "The compression selector must accept a value with the MB suffix.");
    Assert(SettingsForm.TryParseCompressionTarget("37", out var compression37) && compression37 == 37,
        "The compression selector must preserve arbitrary values in its supported range.");
    foreach (var invalidCompression in new[] { "", "0", "101", "-5", "7.5 MB", "5 MB extra", "1 000", "abc" })
    {
        Assert(!SettingsForm.TryParseCompressionTarget(invalidCompression, out var parsedCompression) &&
               parsedCompression == 0,
            $"The compression selector must reject ambiguous value '{invalidCompression}'.");
    }
    Assert(ReferenceEquals(ClipCordTheme.InterfaceFont(10f), ClipCordTheme.InterfaceFont(10f)),
        "ClipCord fonts must be cached instead of allocating GDI font handles for every control.");

    AssertSettingsFormLayout(new AppSettings(
        gameArchiveRoot,
        "https://discord.com/api/" + "webhooks/123456/test-token",
        true,
        AppSettings.DefaultCompressionTargetMb,
        "Malik",
        true));

    var recoveryRoot = Path.Combine(temporaryRoot, "safe-baseline-recovery");
    var recoveryClips = Path.Combine(recoveryRoot, "clips");
    var recoveryStateDirectory = Path.Combine(recoveryRoot, "state");
    Directory.CreateDirectory(recoveryClips);
    Directory.CreateDirectory(recoveryStateDirectory);
    var pendingMovePath = Path.Combine(recoveryClips, "uploaded-but-not-moved.mp4");
    var pendingLocalOnlyMovePath = Path.Combine(recoveryClips, "local-only-but-not-moved.mp4");
    await File.WriteAllBytesAsync(pendingMovePath, new byte[] { 9, 8, 7, 6 });
    await File.WriteAllBytesAsync(pendingLocalOnlyMovePath, new byte[] { 6, 7, 8, 9 });
    var recoveryStatePath = Path.Combine(recoveryStateDirectory, "state.json");
    var recoveryMarkerPath = Path.Combine(recoveryStateDirectory, ".safe-baseline-required");
    var recoveryStore = new WatchStateStore(recoveryStatePath, recoveryMarkerPath);
    recoveryStore.Save(new WatchState
    {
        Version = 2,
        ClipsFolder = recoveryClips,
        PendingMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pendingMovePath },
        PendingLocalOnlyMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pendingLocalOnlyMovePath }
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
        recoveredState.PendingLocalOnlyMoves.Contains(pendingLocalOnlyMovePath),
        "A forced safe-baseline rebuild must preserve readable local-only pending moves.");
    Assert(
        recoveredState.KnownContentHashes.Count == 2,
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

    var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var delayedWatcherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task DelayedCleanupWatcher(
        AppSettings ignoredSettings,
        Action<string> ignoredStatus,
        CancellationToken cancellationToken)
    {
        delayedWatcherStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cleanupStarted.TrySetResult();
            await releaseCleanup.Task;
            throw;
        }
    }

    var delayedCleanupController = new DiscordAwareController(
        AppSettings.Empty,
        _ => { },
        () => true,
        DelayedCleanupWatcher,
        controllerOptions);
    await delayedWatcherStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var stopTask = delayedCleanupController.StopAsync();
    await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(!stopTask.IsCompleted,
        "Awaitable controller shutdown must not finish while the old watcher is still cleaning up.");
    releaseCleanup.TrySetResult();
    await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
    delayedCleanupController.Dispose();

    await AssertLocalOnlyWorkerAsync(temporaryRoot);

    await UpdateCheckerTests.RunAsync(temporaryRoot);
    await UpdateDownloadServiceTests.RunAsync(temporaryRoot);

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

static void CreateDirectoryJunction(string linkPath, string targetPath)
{
    var startInfo = new ProcessStartInfo("cmd.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("mklink");
    startInfo.ArgumentList.Add("/J");
    startInfo.ArgumentList.Add(linkPath);
    startInfo.ArgumentList.Add(targetPath);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Windows did not start the junction test helper.");
    Assert(process.WaitForExit(5000), "The junction test helper exceeded its five-second deadline.");
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            "The mandatory local-only junction test could not be prepared: " +
            process.StandardError.ReadToEnd());
    }
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

static async Task AssertLocalOnlyWorkerAsync(string temporaryRoot)
{
    var root = Path.Combine(temporaryRoot, "local-only-worker");
    var clipsFolder = Path.Combine(root, "clips");
    var stateFolder = Path.Combine(root, "state");
    Directory.CreateDirectory(clipsFolder);
    Directory.CreateDirectory(stateFolder);
    var store = new WatchStateStore(
        Path.Combine(stateFolder, "state.json"),
        Path.Combine(stateFolder, ".safe-baseline-required"));
    await store.LoadOrInitializeAsync(clipsFolder, _ => { }, CancellationToken.None);

    const string clipName = "Battlefield__2026-08-05__12-00-00.mp4";
    var sourcePath = Path.Combine(clipsFolder, clipName);
    await File.WriteAllBytesAsync(sourcePath, [1, 3, 3, 7]);
    File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));
    var expectedDestination = Path.Combine(clipsFolder, "local-only", "Battlefield", clipName);
    var statuses = new ConcurrentQueue<string>();
    var settings = new AppSettings(
        clipsFolder,
        string.Empty,
        false,
        AppSettings.DefaultCompressionTargetMb,
        AppSettings.DefaultUploaderName,
        false);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var worker = new UploaderWorker(
        settings,
        statuses.Enqueue,
        store,
        () => throw new InvalidOperationException(
            "Local-only mode must not construct a Discord webhook client."));
    var workerTask = worker.RunAsync(cancellation.Token);
    try
    {
        await WaitUntilAsync(
            () => File.Exists(expectedDestination),
            TimeSpan.FromSeconds(16),
            "Local-only mode did not archive a ready clip without a webhook.");
    }
    finally
    {
        cancellation.Cancel();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    Assert(!File.Exists(sourcePath), "A local-only clip must leave the watched folder after it is archived.");
    Assert(
        statuses.Any(status => status.Contains("local-only", StringComparison.OrdinalIgnoreCase)),
        "Local-only processing must publish an explicit watcher status.");
    var state = await store.LoadOrInitializeAsync(clipsFolder, _ => { }, CancellationToken.None);
    Assert(state.LocalOnlyContentHashes.Count == 1,
        "A local-only clip must receive a persisted local-only content identity.");
    Assert(state.UploadedContentHashes.Count == 0,
        "A local-only clip must never be marked as uploaded.");
    Assert(state.PendingLocalOnlyMoves.Count == 0,
        "A completed local-only archive move must clear its durable pending entry.");

    const string pendingClipName = "Apex Legends__2026-08-05__12-30-00.mp4";
    var pendingSourcePath = Path.Combine(clipsFolder, pendingClipName);
    await File.WriteAllBytesAsync(pendingSourcePath, [2, 4, 6, 8]);
    var pendingHash = await ContentIdentity.ComputeSha256Async(pendingSourcePath, CancellationToken.None);
    state.KnownContentHashes.Add(pendingHash);
    state.LocalOnlyContentHashes.Add(pendingHash);
    state.PendingLocalOnlyMoves.Add(pendingSourcePath);
    store.Save(state);

    var uploadsEnabledSettings = settings with
    {
        WebhookUrl = "https://discord.com/api/webhooks/123456/test-token",
        UploadToDiscord = true
    };
    var recoveredDestination = Path.Combine(clipsFolder, "local-only", "Apex Legends", pendingClipName);
    using var recoveryCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var recoveryWorker = new UploaderWorker(uploadsEnabledSettings, _ => { }, store);
    var recoveryTask = recoveryWorker.RunAsync(recoveryCancellation.Token);
    try
    {
        await WaitUntilAsync(
            () => File.Exists(recoveredDestination),
            TimeSpan.FromSeconds(10),
            "A persisted local-only move changed destination after Discord uploads were enabled.");
    }
    finally
    {
        recoveryCancellation.Cancel();
        try
        {
            await recoveryTask;
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
        }
    }
    Assert(
        !File.Exists(Path.Combine(clipsFolder, "uploaded", "Apex Legends", pendingClipName)),
        "A recovered local-only move must never be redirected into the uploaded archive.");
}

static void AssertSettingsFormLayout(AppSettings settings)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new SettingsForm(
                settings,
                checkForUpdatesAsync: _ => Task.CompletedTask,
                watcherStatusProvider: () => "Discord open — local-only mode");
            form.CreateControl();
            Assert(form.Text == "ClipCord — Settings", "The settings window must use the ClipCord brand.");
            AssertControlsFit(form);
            var designedOpeningSize = form.Size;
            AssertSettingsCardsOpenWithoutScrolling(form);
            form.Show();
            Application.DoEvents();
            AssertControlsFit(form);
            AssertSettingsCardsScrollOnlyWhenScreenConstrained(form, designedOpeningSize);
            AssertSettingsTextFieldsAligned(form);
            AssertCriticalTextFits(form);
            AssertAccessibility(form);
            AssertOpaqueCustomControlsPaintEveryPixel(form);
            form.Invalidate(true);
            form.Update();
            AssertOpaqueCustomControlsPaintEveryPixel(form);
            Assert(form.Padding.All >= SettingsForm.ResizeGrip,
                "The borderless window must leave the full resize grip exposed around docked content.");
            var rootLayout = form.Controls.Cast<Control>().Single(control => control.Name == "RootLayout");
            Assert(rootLayout.Bounds == new Rectangle(
                       form.Padding.Left,
                       form.Padding.Top,
                       form.ClientSize.Width - form.Padding.Horizontal,
                       form.ClientSize.Height - form.Padding.Vertical),
                "Docked content must not cover any part of the reserved resize frame.");
            var cornerInset = SettingsForm.ResizeGrip - 1;
            Assert(form.Region?.IsVisible(cornerInset, cornerInset) != false &&
                   form.HitTestResizeGrip(new Point(cornerInset, cornerInset)) == 13 &&
                   form.HitTestResizeGrip(new Point(form.ClientSize.Width - cornerInset, cornerInset)) == 14 &&
                   form.HitTestResizeGrip(new Point(cornerInset, form.ClientSize.Height - cornerInset)) == 16 &&
                   form.HitTestResizeGrip(new Point(form.ClientSize.Width - cornerInset, form.ClientSize.Height - cornerInset)) == 17,
                "All four diagonal resize hit targets must remain reachable.");
            var reachableDiagonalPixels = Enumerable.Range(0, SettingsForm.ResizeGrip)
                .Count(inset => form.Region?.IsVisible(inset, inset) != false &&
                                form.HitTestResizeGrip(new Point(inset, inset)) == 13);
            Assert(reachableDiagonalPixels >= 8,
                $"The diagonal resize target is too small: only {reachableDiagonalPixels} pixels are reachable.");
            AssertDpiRefit(form);
            form.ToggleMaximize();
            Application.DoEvents();
            Assert(!form.HasExplicitMaximizedBounds,
                "Custom maximize must leave MaximizedBounds empty so WM_GETMINMAXINFO remains monitor-relative.");
            form.ToggleMaximize();
            Application.DoEvents();
            form.Hide();
            form.Size = form.MinimumSize;
            form.PerformLayout();
            AssertControlsFit(form);
            form.Show();
            Application.DoEvents();
            AssertControlsFit(form);
            AssertCriticalTextFits(form);
            form.Hide();

            var buttonTexts = EnumerateControls(form)
                .OfType<Button>()
                .Select(button => button.Text)
                .ToHashSet(StringComparer.Ordinal);
            Assert(buttonTexts.SetEquals(["Browse", "Test webhook", "Check for updates", "Save changes", "Cancel"]),
                "The settings form must keep all action buttons available.");
            var startupCheckbox = EnumerateControls(form)
                .OfType<CheckBox>()
                .Single(checkBox => checkBox.Text == "Start with Windows");
            Assert(startupCheckbox.Width > 0 && startupCheckbox.Height > 0,
                "The Start with Windows checkbox must occupy visible layout space.");
            var uploadToggle = EnumerateControls(form)
                .OfType<CheckBox>()
                .Single(checkBox => checkBox.Name == "UploadToDiscordToggle");
            Assert(uploadToggle.Checked && uploadToggle.Width > 0 && uploadToggle.Height > 0,
                "The Discord upload toggle must reflect the saved setting and remain visible.");
            uploadToggle.Checked = false;
            Application.DoEvents();
            var uploadModeHelper = EnumerateControls(form)
                .OfType<Label>()
                .Single(label => label.Name == "UploadModeHelperLabel");
            var privacySummary = EnumerateControls(form)
                .OfType<Label>()
                .Single(label => label.Name == "PrivacySummaryLabel");
            Assert(uploadModeHelper.Text.Contains("No Discord request", StringComparison.Ordinal) &&
                   privacySummary.Text.Contains("Local-only mode", StringComparison.Ordinal),
                "Turning uploads off must immediately explain the local-only behavior.");
            AssertControlsFit(form);
            var activityItem = EnumerateControls(form)
                .Single(control => control.Name == "ActivityNavItem");
            Assert(activityItem.Tag as string == SettingsForm.ActivityComingSoonText &&
                   activityItem.AccessibleDescription == SettingsForm.ActivityComingSoonText &&
                   activityItem.AccessibilityObject.State.HasFlag(AccessibleStates.Unavailable),
                "The disabled Activity navigation must explain that it belongs to a future release.");
            var activityLabel = EnumerateControls(activityItem)
                .OfType<Label>()
                .Single(label => label.Name == "ActivityNavLabel");
            Assert(!activityLabel.Enabled,
                "The Activity navigation label must remain visibly unavailable.");
            Assert(EnumerateControls(form).OfType<Label>().Any(label => label.Text == "Local only"),
                "The branded header must present the complete local-only watcher status.");
            var headerLogo = EnumerateControls(form)
                .OfType<ClipCordLogoControl>()
                .Single(control => control.Name == "HeaderLogo");
            var productName = EnumerateControls(form)
                .OfType<Label>()
                .Single(control => control.Name == "ProductNameLabel");
            Assert(ClipCordLogoControl.EmbeddedAssetSize == new Size(1024, 1024),
                "The branded header must render the full-resolution embedded app-icon.png asset.");
            Assert(Math.Min(headerLogo.Width, headerLogo.Height) >= productName.Height,
                $"The branded header logo must be at least as tall as the wordmark; logo={headerLogo.Size}, wordmark={productName.Size}.");
            AssertVerticalCentersMatch(headerLogo, productName);
            AssertOfficialLogoArtworkPainted(headerLogo);

            using var updateDialog = new UpdateAvailableDialog(
                UpdateCheckerTests.CreateRelease(new StableVersion(2, 0, 0)));
            updateDialog.CreateControl();
            Assert(updateDialog.Text == "ClipCord — Update available",
                "The update window must use the ClipCord brand.");
            AssertControlsFit(updateDialog);
            var updateActions = EnumerateControls(updateDialog)
                .OfType<Button>()
                .Select(button => button.Text)
                .ToHashSet(StringComparer.Ordinal);
            Assert(updateActions.SetEquals([
                    "View changes",
                    "Install update",
                    "Skip this version",
                    "Remind me later"
                ]),
                "The update prompt must expose every required action.");

            using var downloadService = new NeverCalledUpdateDownloadService();
            using var downloadDialog = new UpdateDownloadDialog(
                UpdateCheckerTests.CreateRelease(new StableVersion(2, 0, 0)),
                downloadService);
            downloadDialog.CreateControl();
            Assert(downloadDialog.Text == "ClipCord — Downloading update",
                "The update download window must use the ClipCord brand.");
            AssertControlsFit(downloadDialog);
            var downloadActions = EnumerateControls(downloadDialog)
                .OfType<Button>()
                .Select(button => button.Text)
                .ToHashSet(StringComparer.Ordinal);
            Assert(downloadActions.SetEquals(["Retry", "Cancel"]),
                "The update download window must expose retry and cancellation actions.");
            AssertUpdateDownloadDialogBehavior(
                UpdateCheckerTests.CreateRelease(new StableVersion(2, 0, 0)));

            using (var ownerForm = new Form { ShowInTaskbar = false })
            {
                ownerForm.Show();
                Assert(ReferenceEquals(TrayApplicationContext.GetUsableOwner(ownerForm), ownerForm),
                    "A visible live form must remain a valid update-dialog owner.");
                ownerForm.Dispose();
                Assert(TrayApplicationContext.GetUsableOwner(ownerForm) is null,
                    "A disposed Settings form must be dropped before update UI uses its handle.");
            }

            AssertSettingsRoundTrip(settings);
            AssertManualCheckCloseProtection(settings);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Settings form layout validation failed.", failure);
}

static void AssertManualCheckCloseProtection(AppSettings settings)
{
    var releaseCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var checkedBusyState = false;
    using var form = new SettingsForm(
        settings,
        checkForUpdatesAsync: async _ => await releaseCheck.Task);
    form.Shown += (_, _) =>
    {
        var buttons = EnumerateControls(form).OfType<Button>().ToArray();
        var checkButton = buttons.Single(button => button.Text == "Check for updates");
        var cancelButton = buttons.Single(button => button.Text == "Cancel");
        var titleButtons = EnumerateControls(form).OfType<TitleBarButton>().ToArray();
        checkButton.PerformClick();

        var inspectTimer = new System.Windows.Forms.Timer { Interval = 20 };
        inspectTimer.Tick += (_, _) =>
        {
            inspectTimer.Stop();
            inspectTimer.Dispose();
            Assert(!cancelButton.Enabled && titleButtons.All(button => !button.Enabled),
                "Cancel, Esc, and every custom title-bar action must be disabled during a manual update check.");
            form.Close();
            Assert(form.Visible,
                "A user close request must not dispose Settings while its update callback is in flight.");
            checkedBusyState = true;
            releaseCheck.TrySetResult();

            var closeTimer = new System.Windows.Forms.Timer { Interval = 20 };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                closeTimer.Dispose();
                form.Close();
            };
            closeTimer.Start();
        };
        inspectTimer.Start();
    };

    form.ShowDialog();
    Assert(checkedBusyState, "The manual update-check close-protection test did not run.");
}

static void AssertControlsFit(Form form)
{
    form.PerformLayout();
    foreach (var control in EnumerateControls(form).Where(control => control.Visible))
    {
        control.PerformLayout();
        if (control.Parent is not null && !IsAutoScrollViewport(control.Parent))
        {
            var parentBounds = control.Parent.ClientRectangle;
            Assert(control.Left >= -1 && control.Top >= -1 &&
                   control.Right <= parentBounds.Right + 1 &&
                   control.Bottom <= parentBounds.Bottom + 1,
                $"Control {control.GetType().Name} '{control.Name}' ('{control.Text}') is clipped by parent " +
                $"{control.Parent.GetType().Name} '{control.Parent.Name}': child={control.Bounds}, parent={parentBounds}, " +
                $"grandparent={control.Parent.Parent?.GetType().Name} '{control.Parent.Parent?.Name}' bounds={control.Parent.Parent?.Bounds}.");
        }
        var bounds = control.Bounds;
        for (var parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
        {
            bounds.Offset(parent.Left, parent.Top);
        }

        if (!HasAutoScrollAncestor(control))
        {
            Assert(bounds.Left >= 0 && bounds.Top >= 0 &&
                   bounds.Right <= form.ClientSize.Width + 1 &&
                   bounds.Bottom <= form.ClientSize.Height + 1,
                $"Control {control.GetType().Name} '{control.Name}' ('{control.Text}') is clipped at {bounds} inside {form.ClientSize}; parent={control.Parent?.GetType().Name} '{control.Parent?.Name}' bounds={control.Parent?.Bounds}.");
        }
    }
}

static void AssertSettingsCardsOpenWithoutScrolling(SettingsForm form)
{
    var cards = EnumerateControls(form)
        .OfType<ScrollableControl>()
        .Single(control => control.AutoScroll);
    cards.PerformLayout();
    Assert(!cards.VerticalScroll.Visible && cards.AutoScrollPosition.Y == 0,
        $"Settings must open with every card visible without scrolling; viewport={cards.ClientSize}, display={cards.DisplayRectangle}.");
}

static void AssertSettingsCardsScrollOnlyWhenScreenConstrained(SettingsForm form, Size designedOpeningSize)
{
    var cards = EnumerateControls(form)
        .OfType<ScrollableControl>()
        .Single(control => control.AutoScroll);
    if (!cards.VerticalScroll.Visible) return;
    Assert(form.Height < designedOpeningSize.Height,
        $"Settings may scroll only when the screen reduced its designed opening height; designed={designedOpeningSize}, actual={form.Size}.");
}

static void AssertSettingsTextFieldsAligned(SettingsForm form)
{
    var fields = EnumerateControls(form)
        .OfType<TextBox>()
        .Where(control => control.AccessibleName is "Clips folder" or "Uploader name" or "Discord webhook URL")
        .OrderBy(control => control.AccessibleName, StringComparer.Ordinal)
        .ToArray();
    Assert(fields.Length == 3, "The three primary Settings text fields must remain discoverable.");
    var screenBounds = fields
        .Select(control => new Rectangle(control.PointToScreen(Point.Empty), control.Size))
        .ToArray();
    Assert(screenBounds.Select(bounds => bounds.Left).Distinct().Count() == 1 &&
           screenBounds.Select(bounds => bounds.Height).Distinct().Count() == 1,
        $"Clip source and Discord destination fields must share one left edge and height: {string.Join(", ", screenBounds)}.");
}

static void AssertSettingsRoundTrip(AppSettings original)
{
    using var form = new SettingsForm(original, checkForUpdatesAsync: _ => Task.CompletedTask);
    form.Show();
    Application.DoEvents();
    var controls = EnumerateControls(form).ToArray();
    var changedFolder = Directory.CreateDirectory(Path.Combine(original.ClipsFolder, "round-trip-clips")).FullName;
    const string changedWebhook = "https://discord.com/api/v10/webhooks/987654/round-trip-token";
    ((TextBox)controls.Single(control => control.AccessibleName == "Clips folder")).Text = changedFolder;
    ((TextBox)controls.Single(control => control.AccessibleName == "Discord webhook URL")).Text = changedWebhook;
    ((TextBox)controls.Single(control => control.AccessibleName == "Uploader name")).Text = "Round Trip User";
    ((ComboBox)controls.Single(control => control.AccessibleName == "Compression target in megabytes")).Text = "37 MB";
    var startup = controls.OfType<CheckBox>().Single(control => control.Text == "Start with Windows");
    startup.Checked = !original.StartWithWindows;
    var uploadToDiscord = controls.OfType<CheckBox>()
        .Single(control => control.Name == "UploadToDiscordToggle");
    uploadToDiscord.Checked = !original.UploadToDiscord;
    controls.OfType<Button>().Single(control => control.Text == "Save changes").PerformClick();

    Assert(form.SavedSettings is not null &&
           form.SavedSettings.ClipsFolder == changedFolder &&
           form.SavedSettings.WebhookUrl == changedWebhook &&
           form.SavedSettings.UploaderName == "Round Trip User" &&
           form.SavedSettings.StartWithWindows == !original.StartWithWindows &&
           form.SavedSettings.CompressionTargetMb == 37 &&
           form.SavedSettings.UploadToDiscord == !original.UploadToDiscord,
        "Every settings value must survive the branded form save round trip.");
}

static void AssertCriticalTextFits(Form form)
{
    var criticalText = new HashSet<string>(StringComparer.Ordinal)
    {
        "Clip source",
        "Clips folder",
        "Discord destination",
        "Uploader name",
        "Webhook URL",
        "Upload preferences",
        "Compression target",
        "Upload new clips to Discord",
        "Local only",
        "Start with Windows",
        "Save changes",
        "Cancel"
    };
    foreach (var control in EnumerateControls(form).Where(control => control.Visible && criticalText.Contains(control.Text)))
    {
        var measured = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine);
        Assert(measured.Width <= control.ClientSize.Width + 4 && measured.Height <= control.ClientSize.Height + 4,
            $"Text '{control.Text}' does not fit {control.GetType().Name}: measured={measured}, client={control.ClientSize}.");
    }

    foreach (var toggle in EnumerateControls(form).OfType<ToggleSwitch>())
    {
        var toggleText = TextRenderer.MeasureText(toggle.Text, toggle.Font, Size.Empty, TextFormatFlags.SingleLine);
        Assert(toggleText.Width <= toggle.GetTextBounds().Width + 4 &&
               toggleText.Height <= toggle.GetTextBounds().Height + 4,
            $"Toggle text '{toggle.Text}' does not fit its painted text area: measured={toggleText}, paintBounds={toggle.GetTextBounds()}.");
    }

    foreach (var layout in EnumerateControls(form).OfType<TableLayoutPanel>())
    {
        var children = layout.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
        for (var first = 0; first < children.Length; first++)
        {
            for (var second = first + 1; second < children.Length; second++)
            {
                Assert(!children[first].Bounds.IntersectsWith(children[second].Bounds),
                    $"Sibling controls '{children[first].Text}' and '{children[second].Text}' overlap in {layout.Name}.");
            }
        }
    }
}

static void AssertAccessibility(Form form)
{
    foreach (var input in EnumerateControls(form).Where(control => control is TextBox or ComboBox))
    {
        Assert(!string.IsNullOrWhiteSpace(input.AccessibleName),
            $"Input {input.GetType().Name} must have an accessible name.");
    }

    foreach (var decorative in EnumerateControls(form).Where(control =>
                 control is BrandGlyphControl or BrandIconTile or ClipCordLogoControl or GradientStrip))
    {
        Assert(!decorative.TabStop,
            $"Decorative control {decorative.GetType().Name} must not be a keyboard tab stop.");
    }

    Assert(EnumerateControls(form).Single(control => control.Name == "ActivityNavItem").TabStop,
        "Activity must be keyboard reachable so its future-release description can be announced.");
    Assert(EnumerateControls(form).Single(control => control.Name == "AboutNavItem").TabStop,
        "About must be keyboard reachable.");
    Assert(EnumerateControls(form).Single(control => control.Name == "SettingsNavItem").TabStop,
        "The current Settings navigation item must participate in the sidebar keyboard order.");
}

static void AssertOpaqueCustomControlsPaintEveryPixel(Form form)
{
    var getStyle = typeof(Control).GetMethod(
        "GetStyle",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var customTypes = new HashSet<Type>
    {
        typeof(ToggleSwitch),
        typeof(GradientButton),
        typeof(OutlineButton),
        typeof(TitleBarButton),
        typeof(BrandIconTile),
        typeof(ClipCordLogoControl),
        typeof(GradientStrip)
    };
    var sentinel = Color.FromArgb(255, 1, 254, 1);
    foreach (var control in EnumerateControls(form).Where(control =>
                 control.Visible &&
                 control.Width > 0 &&
                 control.Height > 0 &&
                 customTypes.Contains(control.GetType()) &&
                 (bool)getStyle.Invoke(control, [ControlStyles.Opaque])!))
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(sentinel);
        using var paint = new PaintEventArgs(graphics, control.ClientRectangle);
        var onPaint = control.GetType().GetMethod(
            "OnPaint",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        onPaint.Invoke(control, [paint]);

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (control.Region?.IsVisible(x, y) == false) continue;
                Assert(bitmap.GetPixel(x, y).ToArgb() != sentinel.ToArgb(),
                    $"Opaque {control.GetType().Name} left pixel ({x}, {y}) unpainted.");
            }
        }
    }
}

static void AssertDpiRefit(SettingsForm form)
{
    var compression = EnumerateControls(form)
        .OfType<ComboBox>()
        .Single(control => control.AccessibleName == "Compression target in megabytes");
    var host = (RoundedPanel)compression.Parent!;
    var originalFont = compression.Font;
    compression.Font = ClipCordTheme.InterfaceFont(18f);
    host.MaximumSize = Size.Empty;
    host.MinimumSize = Size.Empty;
    host.Height = 1;
    form.RefitDpiSensitiveControls();
    Assert(host.Height >= compression.PreferredHeight + host.Padding.Vertical,
        "DPI refitting must recompute the compression host from the ComboBox preferred height.");
    compression.Font = originalFont;
    form.RefitDpiSensitiveControls();
}

static void AssertVerticalCentersMatch(Control first, Control second)
{
    var firstCenter = first.PointToScreen(new Point(first.Width / 2, first.Height / 2));
    var secondCenter = second.PointToScreen(new Point(second.Width / 2, second.Height / 2));
    Assert(Math.Abs(firstCenter.Y - secondCenter.Y) <= 1,
        $"The logo and wordmark must remain vertically centered; centers were {firstCenter.Y} and {secondCenter.Y}.");
}

static void AssertOfficialLogoArtworkPainted(ClipCordLogoControl logo)
{
    using var bitmap = new Bitmap(logo.Width, logo.Height);
    logo.DrawToBitmap(bitmap, new Rectangle(Point.Empty, logo.Size));

    var officialCoralSeen = false;
    var officialVioletSeen = false;
    for (var y = 0; y < bitmap.Height && !(officialCoralSeen && officialVioletSeen); y++)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            officialCoralSeen |= color.R >= 238 && color.G is >= 55 and <= 115 && color.B is >= 45 and <= 105;
            officialVioletSeen |= color.R is >= 115 and <= 180 && color.G is >= 40 and <= 105 && color.B >= 210;
        }
    }

    Assert(officialCoralSeen && officialVioletSeen,
        "The header logo must paint the official PNG's bright coral and violet artwork.");
}

static bool IsAutoScrollViewport(Control control) =>
    control is ScrollableControl scrollable && scrollable.AutoScroll;

static bool HasAutoScrollAncestor(Control control)
{
    for (var parent = control.Parent; parent is not null; parent = parent.Parent)
    {
        if (IsAutoScrollViewport(parent)) return true;
    }
    return false;
}

static IEnumerable<Control> EnumerateControls(Control parent)
{
    foreach (Control control in parent.Controls)
    {
        yield return control;
        foreach (var descendant in EnumerateControls(control)) yield return descendant;
    }
}

static void AssertUpdateDownloadDialogBehavior(UpdateRelease release)
{
    using (var service = new CompletingUpdateDownloadService())
    using (var form = new UpdateDownloadDialog(release, service))
    {
        form.Show();
        PumpWindowsMessagesUntil(() => service.Started, "The update download did not start.");
        var cancel = EnumerateControls(form).OfType<Button>().Single(button => button.Text == "Cancel");
        cancel.PerformClick();
        service.Complete(new DownloadedUpdate(release, "unused-after-cancel.exe"));
        PumpWindowsMessagesUntil(() => !form.Visible, "The cancelled update dialog did not close.");
        Assert(form.DialogResult == DialogResult.Cancel && form.DownloadedUpdate is null,
            "Cancellation must remain authoritative when the download completes at the same moment.");
    }

    using (var service = new FailingUpdateDownloadService())
    using (var form = new UpdateDownloadDialog(release, service))
    {
        form.Show();
        PumpWindowsMessagesUntil(
            () => EnumerateControls(form).OfType<Button>().Any(button => button.Text == "Retry" && button.Visible),
            "The verification-failure state was not shown.");
        form.PerformLayout();
        AssertControlsFit(form);
        var detail = EnumerateControls(form)
            .OfType<Label>()
            .Single(label => label.Text.StartsWith("The downloaded update could not be verified", StringComparison.Ordinal));
        var measured = TextRenderer.MeasureText(
            detail.Text,
            detail.Font,
            new Size(detail.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        Assert(detail.Height >= measured.Height,
            $"The update failure explanation is clipped: actual {detail.Height}px, required {measured.Height}px.");
        form.Close();
    }

    var closeHandler = typeof(UpdateDownloadDialog).GetMethod(
        "HandleFormClosing",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    foreach (var reason in new[]
             {
                 CloseReason.UserClosing,
                 CloseReason.WindowsShutDown,
                 CloseReason.ApplicationExitCall,
                 CloseReason.TaskManagerClosing
             })
    {
        using var service = new CompletingUpdateDownloadService();
        using var form = new UpdateDownloadDialog(release, service);
        form.Show();
        PumpWindowsMessagesUntil(() => service.Started, $"The {reason} close test did not start.");
        var eventArgs = new FormClosingEventArgs(reason, cancel: false);
        closeHandler.Invoke(form, [form, eventArgs]);
        Assert(eventArgs.Cancel == (reason == CloseReason.UserClosing),
            $"The update dialog handled {reason} incorrectly; cancelled={eventArgs.Cancel}.");
        service.Complete(new DownloadedUpdate(release, "unused-after-close.exe"));
        PumpWindowsMessagesUntil(
            () => !service.IsPending,
            $"The {reason} close test did not release its download.");
        Application.DoEvents();
        if (!form.IsDisposed) form.Close();
    }

    var disposableService = new NeverCalledUpdateDownloadService();
    var disposableForm = new UpdateDownloadDialog(release, disposableService);
    disposableForm.Dispose();
    disposableForm.Dispose();
    disposableService.Dispose();

    using var activeService = new CompletingUpdateDownloadService();
    var activeForm = new UpdateDownloadDialog(release, activeService);
    activeForm.Show();
    PumpWindowsMessagesUntil(() => activeService.Started, "The active-disposal test did not start.");
    activeForm.Dispose();
    activeForm.Dispose();
    activeForm.Dispose();
    activeService.Complete(new DownloadedUpdate(release, "unused-after-dispose.exe"));
    PumpWindowsMessagesUntil(() => !activeService.IsPending, "The active-disposal test did not complete.");
    Application.DoEvents();
    Assert(activeForm.DownloadedUpdate is null,
        "A completion arriving after active dialog disposal must not become installable.");
}

static void PumpWindowsMessagesUntil(Func<bool> condition, string failureMessage)
{
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(failureMessage);
        Application.DoEvents();
        Thread.Sleep(5);
    }
    Application.DoEvents();
}

internal sealed class NeverCalledUpdateDownloadService : IUpdateDownloadService
{
    public Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The layout test must not start a download.");

    public void Dispose()
    {
    }
}

internal sealed class CompletingUpdateDownloadService : IUpdateDownloadService
{
    private readonly TaskCompletionSource<DownloadedUpdate> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Started { get; private set; }
    public bool IsPending => !_completion.Task.IsCompleted;

    public Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Started = true;
        return _completion.Task;
    }

    public void Complete(DownloadedUpdate update) => _completion.TrySetResult(update);

    public void Dispose()
    {
    }
}

internal sealed class FailingUpdateDownloadService : IUpdateDownloadService
{
    public Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromException<DownloadedUpdate>(new InvalidDataException("Simulated verification failure."));

    public void Dispose()
    {
    }
}
