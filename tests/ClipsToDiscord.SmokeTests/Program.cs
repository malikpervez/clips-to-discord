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

    var readinessPath = Path.Combine(temporaryRoot, "readiness.mp4");
    await File.WriteAllBytesAsync(readinessPath, new byte[] { 1, 2, 3, 4 });
    var readinessTracker = new FileReadinessTracker();
    var firstObservationAt = DateTime.UtcNow;
    var firstObservation = readinessTracker.Observe(new FileInfo(readinessPath), firstObservationAt);
    var stableObservation = readinessTracker.Observe(
        new FileInfo(readinessPath),
        firstObservationAt.AddSeconds(11));
    Assert(!firstObservation.IsReady, "A file must not be ready on its first observation.");
    Assert(stableObservation.IsReady, "An unchanged readable file must become ready after the stability window.");

    var lockedPath = Path.Combine(temporaryRoot, "locked.mp4");
    await File.WriteAllBytesAsync(lockedPath, new byte[] { 5, 6, 7, 8 });
    var lockedTracker = new FileReadinessTracker();
    var lockedObservedAt = DateTime.UtcNow;
    lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt);
    FileReadinessResult lockedResult;
    FileReadinessResult secondLockedResult;
    using (var lockedStream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        lockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(11));
        secondLockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(22));
    }
    Assert(!lockedResult.IsReady, "An exclusively locked file must not be ready.");
    Assert(
        lockedResult.NextCheckUtc > lockedObservedAt.AddSeconds(11),
        "A locked file must receive a future retry time.");
    Assert(
        secondLockedResult.NextCheckUtc - lockedObservedAt.AddSeconds(22) >= TimeSpan.FromSeconds(20),
        "Repeated lock failures must increase the readiness backoff.");

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
    Assert(compressionTargets.Contains(9), "Compression fallback must include the safe default target.");
    Assert(
        compressionTargets.Zip(compressionTargets.Skip(1)).All(pair => pair.First > pair.Second),
        "Compression fallback targets must decrease strictly.");

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
