namespace ClipsToDiscord;

internal static class RoutingExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 18, 0, 0, TimeSpan.Zero);

    internal static void Run(string testRoot) =>
        RunAsync(testRoot).GetAwaiter().GetResult();

    private static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertConfirmedDeliveryAndFilingAsync(Path.Combine(testRoot, "confirmed"));
        await AssertDefiniteAndUnknownResultsAsync(Path.Combine(testRoot, "outcomes"));
        await AssertStartupNeverResendsInterruptedAttemptAsync(Path.Combine(testRoot, "restart"));
        await AssertAmbiguousFileRecoveryAsync(Path.Combine(testRoot, "file-recovery"));
        await AssertStaleRecoveryResultsAreAttemptFencedAsync(
            Path.Combine(testRoot, "file-recovery-fence"));
        await AssertArtifactResolutionFailureDoesNotBlockAsync(
            Path.Combine(testRoot, "artifact-resolution"));
        await AssertBoundedAndSerializedAsync(Path.Combine(testRoot, "bounded"));
        await AssertRecoveryBacklogDoesNotStarveDeliveriesAsync(
            Path.Combine(testRoot, "recovery-fairness"));
        await AssertGateRevocationStopsBeforeProviderAsync(Path.Combine(testRoot, "gate"));
        await AssertCaptureJournalImplementationsAsync(Path.Combine(testRoot, "capture"));
        await AssertDerivedCaptureJournalArtifactResolutionAsync(
            Path.Combine(testRoot, "capture-derived"));
        AssertAttemptFences();
    }

    private static async Task AssertConfirmedDeliveryAndFilingAsync(string root)
    {
        var seeded = await SeedAsync(root, includeDelivery: true, includeDisposition: true);
        var provider = new RecordingProvider(
            (_, _, token) =>
            {
                var disk = seeded.Store.Load().Document!;
                var persisted = disk.Deliveries.Single(item =>
                    item.DeliveryId == seeded.Deliveries[0].DeliveryId);
                Assert(persisted.State == PlannedDeliveryState.Sending &&
                       persisted.CurrentAttemptId is not null,
                    "The delivery attempt must be durable before the provider is called.");
                Assert(!token.CanBeCanceled,
                    "A started provider request must finish under its own bounded deadline.");
                return RoutingDeliveryAttemptResult.Confirmed("discord:message-100");
            });
        var filer = new RecordingFiler(
            (disposition, token) =>
            {
                var disk = seeded.Store.Load().Document!;
                var persisted = disk.FileDispositions.Single(item =>
                    item.DispositionId == disposition.DispositionId);
                Assert(persisted.State == PlannedFileDispositionState.Moving &&
                       persisted.CurrentAttemptId is not null,
                    "The file attempt must be durable before the filer is called.");
                Assert(!token.CanBeCanceled,
                    "A started library operation must finish its bounded reconciliation step.");
                return RoutingFileAttemptResult.Confirmed("capture:clip-confirmed:original");
            });
        using var executor = Executor(seeded.Store, provider, new RecordingResolver(), filer);
        var result = await executor.RunOnceAsync();
        var final = seeded.Store.Load().Document!;
        Assert(result.Enabled && result.ProviderAttempts == 1 && result.FileAttempts == 1 &&
               result.RecoveryInspections == 0 && provider.Calls == 1 &&
               filer.FileCalls == 1 && filer.RecoveryCalls == 0,
            "One confirmed plan must perform exactly one provider and one terminal filing attempt.");
        Assert(final.Deliveries.Single().State == PlannedDeliveryState.Delivered &&
               final.Deliveries.Single().RemoteReceiptReference == "discord:message-100" &&
               final.Deliveries.Single().ErrorCode is null &&
               final.FileDispositions.Single().State == PlannedFileDispositionState.Completed &&
               final.FileDispositions.Single().FinalLibraryItemReference ==
               "capture:clip-confirmed:original",
            "Confirmed side effects must retain their durable receipts.");

        _ = await executor.RunOnceAsync();
        Assert(provider.Calls == 1 && filer.FileCalls == 1,
            "Re-running a completed executor must not repeat either side effect.");
    }

    private static async Task AssertDefiniteAndUnknownResultsAsync(string root)
    {
        var failed = await SeedAsync(Path.Combine(root, "failed"), includeDelivery: true);
        var failedProvider = new RecordingProvider((_, _, _) =>
            RoutingDeliveryAttemptResult.Failed("discord-rejected", "discord:target-50"));
        using (var executor = Executor(
                   failed.Store, failedProvider, new RecordingResolver(), new RecordingFiler()))
        {
            _ = await executor.RunOnceAsync();
            _ = await executor.RunOnceAsync();
        }
        var failedDelivery = failed.Store.Load().Document!.Deliveries.Single();
        Assert(failedProvider.Calls == 1 &&
               failedDelivery.State == PlannedDeliveryState.Failed &&
               failedDelivery.ErrorCode == "discord-rejected" &&
               failedDelivery.ProviderResumeReference == "discord:target-50",
            "A definite provider rejection may be retained as safely retryable failure evidence.");

        var unknown = await SeedAsync(Path.Combine(root, "unknown"), includeDelivery: true);
        var unknownProvider = new RecordingProvider((_, _, _) =>
            RoutingDeliveryAttemptResult.Unknown("discord-timeout"));
        using (var executor = Executor(
                   unknown.Store, unknownProvider, new RecordingResolver(), new RecordingFiler()))
        {
            _ = await executor.RunOnceAsync();
            _ = await executor.RunOnceAsync();
        }
        var unknownDelivery = unknown.Store.Load().Document!.Deliveries.Single();
        Assert(unknownProvider.Calls == 1 &&
               unknownDelivery.State == PlannedDeliveryState.DeliveryUnknown &&
               unknownDelivery.ErrorCode == "discord-timeout",
            "An ambiguous provider result must stop in DeliveryUnknown without auto-resend.");

        var malformed = await SeedAsync(Path.Combine(root, "malformed"), includeDelivery: true);
        var malformedProvider = new RecordingProvider((_, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed("not a valid receipt"));
        using (var executor = Executor(
                   malformed.Store, malformedProvider, new RecordingResolver(), new RecordingFiler()))
        {
            _ = await executor.RunOnceAsync();
        }
        Assert(malformed.Store.Load().Document!.Deliveries.Single().State ==
               PlannedDeliveryState.DeliveryUnknown,
            "A malformed success response must be conservative rather than invented as delivery proof.");

        var throwing = await SeedAsync(Path.Combine(root, "throwing"), includeDelivery: true);
        var throwingProvider = new RecordingProvider((_, _, _) =>
            throw new TimeoutException("synthetic transport ambiguity"));
        using (var executor = Executor(
                   throwing.Store, throwingProvider, new RecordingResolver(), new RecordingFiler()))
        {
            _ = await executor.RunOnceAsync();
        }
        Assert(throwing.Store.Load().Document!.Deliveries.Single().State ==
               PlannedDeliveryState.DeliveryUnknown,
            "An unexpected provider exception after StartDelivery must be treated as ambiguous.");
    }

    private static async Task AssertStartupNeverResendsInterruptedAttemptAsync(string root)
    {
        var seeded = await SeedAsync(root, includeDelivery: true);
        var current = seeded.Store.Load().Document!;
        var attemptId = Guid.NewGuid();
        var sending = RoutingOutboxModel.StartDelivery(
            current, seeded.Deliveries[0].DeliveryId, attemptId, At(5));
        _ = await seeded.Store.SaveAsync(sending, current.Generation);

        var provider = new RecordingProvider((_, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed("discord:should-not-send"));
        using var executor = Executor(
            seeded.Store, provider, new RecordingResolver(), new RecordingFiler());
        _ = await executor.RunOnceAsync();
        var recovered = seeded.Store.Load().Document!.Deliveries.Single();
        Assert(provider.Calls == 0 &&
               recovered.State == PlannedDeliveryState.DeliveryUnknown &&
               recovered.ErrorCode == "interrupted-after-send-started",
            "Startup recovery must persist DeliveryUnknown and never repeat an interrupted send.");
    }

    private static async Task AssertAmbiguousFileRecoveryAsync(string root)
    {
        var seeded = await SeedAsync(root, includeDisposition: true);
        var firstFiler = new RecordingFiler(
            (_, _) => RoutingFileAttemptResult.Unknown("move-result-unknown"),
            (_, _) => RoutingFileRecoveryResult.Unresolved);
        using (var executor = Executor(
                   seeded.Store, new RecordingProvider(), new RecordingResolver(), firstFiler))
        {
            _ = await executor.RunOnceAsync();
        }
        var pending = seeded.Store.Load().Document!.FileDispositions.Single();
        Assert(firstFiler.FileCalls == 1 &&
               pending.State == PlannedFileDispositionState.RecoveryPending,
            "An ambiguous library operation must enter hash-reconciliation state.");

        var recoveringFiler = new RecordingFiler(
            recovery: (_, token) =>
            {
                Assert(!token.CanBeCanceled,
                    "Startup library reconciliation must finish its bounded inspection.");
                return RoutingFileRecoveryResult.Completed("capture:clip-confirmed:original");
            });
        using (var restarted = Executor(
                   seeded.Store, new RecordingProvider(), new RecordingResolver(), recoveringFiler))
        {
            _ = await restarted.RunOnceAsync();
        }
        var completed = seeded.Store.Load().Document!.FileDispositions.Single();
        Assert(recoveringFiler.RecoveryCalls == 1 && recoveringFiler.FileCalls == 0 &&
               completed.State == PlannedFileDispositionState.Completed,
            "Exact recovery proof must complete an ambiguous move without moving a second time.");

        var unresolved = await SeedAsync(Path.Combine(root, "unresolved"), includeDisposition: true);
        var unresolvedCurrent = unresolved.Store.Load().Document!;
        var moveAttempt = Guid.NewGuid();
        var moving = RoutingOutboxModel.StartFileDisposition(
            unresolvedCurrent,
            unresolved.Disposition!.DispositionId,
            moveAttempt,
            At(5));
        _ = await unresolved.Store.SaveAsync(moving, unresolvedCurrent.Generation);
        var unresolvedFiler = new RecordingFiler(
            recovery: (_, _) => RoutingFileRecoveryResult.Unresolved);
        using (var restarted = Executor(
                   unresolved.Store,
                   new RecordingProvider(),
                   new RecordingResolver(),
                   unresolvedFiler))
        {
            _ = await restarted.RunOnceAsync();
        }
        Assert(unresolvedFiler.FileCalls == 0 &&
               unresolved.Store.Load().Document!.FileDispositions.Single().State ==
               PlannedFileDispositionState.RecoveryPending,
            "Unresolved recovery evidence must remain pending and must never guess at a retry.");
    }

    private static async Task AssertStaleRecoveryResultsAreAttemptFencedAsync(string root)
    {
        foreach (var retrySafe in new[] { false, true })
        {
            var caseRoot = Path.Combine(root, retrySafe ? "retry" : "complete");
            var seeded = await SeedAsync(caseRoot, includeDisposition: true);
            var dispositionId = seeded.Disposition!.DispositionId;
            var current = seeded.Store.Load().Document!;
            var attemptA = Guid.NewGuid();
            var movingA = RoutingOutboxModel.StartFileDisposition(
                current, dispositionId, attemptA, At(2));
            current = await seeded.Store.SaveAsync(movingA, current.Generation);
            var pendingA = RoutingOutboxModel.MarkFileDispositionRecoveryPending(
                current, dispositionId, attemptA, "attempt-a-unknown", At(3));
            _ = await seeded.Store.SaveAsync(pendingA, current.Generation);

            var filer = new BlockingRecoveryFiler();
            using var executor = Executor(
                seeded.Store, new RecordingProvider(), new RecordingResolver(), filer);
            var running = executor.RunOnceAsync();
            await filer.Entered.WaitAsync(TimeSpan.FromSeconds(5));

            current = seeded.Store.Load().Document!;
            var retryA = RoutingOutboxModel.RetryFileDisposition(
                current, dispositionId, At(4));
            current = await seeded.Store.SaveAsync(retryA, current.Generation);
            var attemptB = Guid.NewGuid();
            var movingB = RoutingOutboxModel.StartFileDisposition(
                current, dispositionId, attemptB, At(5));
            current = await seeded.Store.SaveAsync(movingB, current.Generation);
            var pendingB = RoutingOutboxModel.MarkFileDispositionRecoveryPending(
                current, dispositionId, attemptB, "attempt-b-unknown", At(6));
            _ = await seeded.Store.SaveAsync(pendingB, current.Generation);

            filer.Release(retrySafe
                ? RoutingFileRecoveryResult.RetrySafe
                : RoutingFileRecoveryResult.Completed("capture:stale-attempt:original"));
            _ = await running;

            var final = seeded.Store.Load().Document!.FileDispositions.Single();
            Assert(final.State == PlannedFileDispositionState.RecoveryPending &&
                   final.CurrentAttemptId == attemptB &&
                   final.ErrorCode == "attempt-b-unknown",
                "A stale recovery result from attempt A must not complete or retry later attempt B.");
        }
    }

    private static async Task AssertArtifactResolutionFailureDoesNotBlockAsync(string root)
    {
        var seeded = await SeedAsync(root, includeDelivery: true, deliveryCount: 2);
        var blockedId = seeded.Deliveries[0].DeliveryId;
        var resolver = new RecordingResolver(delivery =>
        {
            if (delivery.DeliveryId == blockedId)
                throw new InvalidDataException("synthetic missing artifact");
            return CreateResolvedArtifact(delivery);
        });
        var provider = new RecordingProvider((delivery, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed($"discord:{delivery.DeliveryId:N}"));
        using var executor = Executor(
            seeded.Store, provider, resolver, new RecordingFiler());

        var run = await executor.RunOnceAsync();
        var final = seeded.Store.Load().Document!;
        var failed = final.Deliveries.Single(item => item.DeliveryId == blockedId);
        var delivered = final.Deliveries.Single(item => item.DeliveryId != blockedId);
        Assert(failed.State == PlannedDeliveryState.Failed &&
               failed.ErrorCode == "artifact-resolution-failed" &&
               failed.Attempts == 0 && failed.CurrentAttemptId is null &&
               failed.AttemptStartedUtc is null,
            "A definite artifact failure before provider entry must be durable without inventing a send attempt.");
        Assert(delivered.State == PlannedDeliveryState.Delivered && provider.Calls == 1 &&
               run.ProviderAttempts == 1,
            "One broken artifact must not head-of-line block a later valid delivery.");
    }

    private static async Task AssertBoundedAndSerializedAsync(string root)
    {
        var seeded = await SeedAsync(root, includeDelivery: true, deliveryCount: 3);
        var provider = new RecordingProvider((delivery, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed($"discord:{delivery.DeliveryId:N}"));
        using var executor = Executor(
            seeded.Store,
            provider,
            new RecordingResolver(),
            new RecordingFiler(),
            maximumSideEffects: 2);
        var first = await executor.RunOnceAsync();
        Assert(first.ProviderAttempts == 2 && provider.Calls == 2 &&
               seeded.Store.Load().Document!.Deliveries.Count(item =>
                   item.State == PlannedDeliveryState.Delivered) == 2,
            "One executor pass must obey its external-side-effect bound.");

        var runs = await Task.WhenAll(executor.RunOnceAsync(), executor.RunOnceAsync());
        Assert(provider.Calls == 3 &&
               seeded.Store.Load().Document!.Deliveries.All(item =>
                   item.State == PlannedDeliveryState.Delivered),
            "Concurrent calls on one executor must serialize and cannot double-send ready work.");
        Assert(runs.Sum(item => item.ProviderAttempts) == 1,
            "Only the remaining delivery may be attempted by serialized concurrent runs.");
    }

    private static async Task AssertRecoveryBacklogDoesNotStarveDeliveriesAsync(string root)
    {
        const int maximumSideEffects = 4;
        const int recoveryCount = maximumSideEffects * 2;
        var seeded = await SeedRecoveryBacklogAsync(
            root, recoveryCount, maximumSideEffects);
        var provider = new RecordingProvider((delivery, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed($"discord:{delivery.DeliveryId:N}"));
        var filer = new BlockingInterleavingFiler();
        using var executor = Executor(
            seeded.Store,
            provider,
            new RecordingResolver(),
            filer,
            maximumSideEffects);

        var running = executor.RunOnceAsync();
        await filer.FirstRecoveryEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(provider.Calls == 1,
            "The first ready delivery must run before any potentially expensive recovery inspection.");
        filer.ReleaseFirstRecovery();
        await filer.SecondRecoveryEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(provider.Calls == 2,
            "The second ready delivery must run before the executor starts its second recovery inspection.");

        var first = await running;
        Assert(first.RecoveryInspections == maximumSideEffects &&
               first.ProviderAttempts == maximumSideEffects &&
               provider.Calls == maximumSideEffects &&
               seeded.Store.Load().Document!.Deliveries.All(item =>
                   item.State == PlannedDeliveryState.Delivered),
            "Recovery must advance under a steady upload backlog without consuming its independent delivery budget.");
        Assert(filer.FileCalls == 0 &&
               seeded.Store.Load().Document!.FileDispositions.All(item =>
                   item.State == PlannedFileDispositionState.RecoveryPending),
            "Unresolved recovery inspections must never guess that an ambiguous move is retry-safe.");

        var firstInspections = filer.RecoveryDispositionIds.ToHashSet();
        var second = await executor.RunOnceAsync();
        Assert(second.RecoveryInspections == maximumSideEffects &&
               filer.RecoveryDispositionIds.Count == recoveryCount &&
               filer.RecoveryDispositionIds.Skip(maximumSideEffects).All(id =>
                   !firstInspections.Contains(id)),
            "Bounded recovery inspection must rotate deterministically instead of polling only the oldest items.");

        _ = await executor.RunOnceAsync();
        Assert(provider.Calls == maximumSideEffects && filer.FileCalls == 0 &&
               seeded.Store.Load().Document!.FileDispositions.All(item =>
                   item.State == PlannedFileDispositionState.RecoveryPending),
            "Repeated unresolved inspections must neither resend a delivery nor auto-retry a file move.");
    }

    private static async Task AssertGateRevocationStopsBeforeProviderAsync(string root)
    {
        var seeded = await SeedAsync(root, includeDelivery: true);
        var inspections = 0;
        bool Gate() => Interlocked.Increment(ref inspections) <= 2;
        var provider = new RecordingProvider((_, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed("discord:must-not-run"));
        using var executor = new RoutingOutboxExecutor(
            seeded.Store,
            provider,
            new RecordingResolver(),
            new RecordingFiler(),
            Gate,
            utcNow: () => At(10));
        _ = await executor.RunOnceAsync();
        var delivery = seeded.Store.Load().Document!.Deliveries.Single();
        Assert(provider.Calls == 0 && delivery.State == PlannedDeliveryState.Failed &&
               delivery.ErrorCode == "routing-disabled-before-provider",
            "A revoked ownership/feature gate after StartDelivery must stop before the provider call.");
    }

    private static async Task AssertCaptureJournalImplementationsAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var gameDirectory = CaptureLibraryLayout.GetRecordingDirectory(library, "Capture Test");
        Directory.CreateDirectory(gameDirectory);
        var originalPath = Path.Combine(
            gameDirectory,
            "Capture Test__2026-08-27__18-00-00.mp4");
        await File.WriteAllBytesAsync(originalPath, [1, 3, 3, 7, 9, 11]);
        var journal = await CaptureJournalStore.CommitOriginalAsync(
            library,
            originalPath,
            CaptureJournalSourceKind.ManualCapture,
            "Capture Test",
            At(0),
            TimeSpan.FromSeconds(5),
            1920,
            1080,
            reactionCameraRequested: false,
            requestedRenditions: [],
            now: At(0));

        var seeded = await SeedAsync(
            Path.Combine(root, "outbox"),
            includeDelivery: true,
            includeDisposition: true,
            sourceClipId: journal.Clip.ClipId,
            sourceHash: journal.Clip.Original.Fingerprint.Sha256);
        var provider = new RecordingProvider((_, artifact, _) =>
        {
            Assert(artifact.Path.Equals(originalPath, StringComparison.OrdinalIgnoreCase) &&
                   artifact.Sha256 == journal.Clip.Original.Fingerprint.Sha256 &&
                   artifact.ByteLength == journal.Clip.Original.Fingerprint.ByteLength &&
                   artifact.GameName == "Capture Test",
                "The capture resolver must bind the exact journal artifact and immutable metadata.");
            var writeBlocked = false;
            try
            {
                using var writer = new FileStream(
                    originalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                writeBlocked = true;
            }
            Assert(writeBlocked,
                "The resolved capture artifact must retain a read lease through provider completion.");
            return RoutingDeliveryAttemptResult.Confirmed("discord:capture-message");
        });
        using var executor = Executor(
            seeded.Store,
            provider,
            new CaptureJournalRoutingArtifactResolver(library),
            new CaptureJournalRoutingLibraryFiler(library));
        _ = await executor.RunOnceAsync();
        var final = seeded.Store.Load().Document!;
        Assert(final.Deliveries.Single().State == PlannedDeliveryState.Delivered &&
               final.FileDispositions.Single().State == PlannedFileDispositionState.Completed &&
               final.FileDispositions.Single().FinalLibraryItemReference ==
               $"capture:{journal.Clip.ClipId}:original" &&
               File.Exists(originalPath),
            "Capture filing must be a hash-verified canonical no-op, not an external archive move.");
    }

    private static async Task AssertDerivedCaptureJournalArtifactResolutionAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var gameDirectory = CaptureLibraryLayout.GetRecordingDirectory(library, "Derived Test");
        Directory.CreateDirectory(gameDirectory);
        var originalPath = Path.Combine(
            gameDirectory,
            "Derived Test__2026-08-27__18-00-00.mp4");
        await File.WriteAllBytesAsync(originalPath, [3, 1, 4, 1, 5, 9]);
        var journal = await CaptureJournalStore.CommitOriginalAsync(
            library,
            originalPath,
            CaptureJournalSourceKind.ManualCapture,
            "Derived Test",
            At(0),
            TimeSpan.FromSeconds(5),
            1920,
            1080,
            reactionCameraRequested: true,
            requestedRenditions: [CaptureJournalArtifactKinds.Landscape],
            now: At(0));
        journal = await CaptureJournalStore.BeginCameraAsync(
            library, journal.Clip.ClipId, journal.Generation, now: At(1));

        var cameraPath = CaptureJournalStore.GetCanonicalArtifactPath(
            library, journal.Clip, CaptureJournalArtifactKinds.ReactionCamera);
        Directory.CreateDirectory(Path.GetDirectoryName(cameraPath)!);
        await File.WriteAllBytesAsync(cameraPath, [2, 7, 1, 8]);
        var camera = await CaptureJournalStore.CreateArtifactAsync(
            library, journal.Clip.ClipId, CaptureJournalArtifactKinds.ReactionCamera, cameraPath);
        journal = await CaptureJournalStore.AttachCameraAsync(
            library, journal.Clip.ClipId, journal.Generation, camera, now: At(2));

        var landscapePath = CaptureJournalStore.GetCanonicalArtifactPath(
            library, journal.Clip, CaptureJournalArtifactKinds.Landscape);
        await File.WriteAllBytesAsync(landscapePath, [1, 6, 1, 8, 0, 3]);
        var landscape = await CaptureJournalStore.CreateArtifactAsync(
            library, journal.Clip.ClipId, CaptureJournalArtifactKinds.Landscape, landscapePath);
        journal = await CaptureJournalStore.CompleteRenditionsAsync(
            library, journal.Clip.ClipId, journal.Generation, [landscape], now: At(3));

        var logicalOutput = RoutingEvaluator.CreateLogicalOutputReference(
            journal.Clip.ClipId,
            journal.Clip.Original.Fingerprint.Sha256,
            RoutingOutputKind.Landscape);
        Assert(logicalOutput.Revision != landscape.Fingerprint.Sha256,
            "A derived output's logical revision must not be mistaken for its physical file hash.");
        var planId = Guid.NewGuid();
        var delivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(),
            planId,
            journal.Clip.ClipId,
            RouteReference(Guid.NewGuid(), Guid.NewGuid(), 0),
            RoutingDestinationKind.Discord,
            "discord.derived",
            logicalOutput,
            RoutingEvaluator.CreateLogicalOutputReference(
                journal.Clip.ClipId,
                journal.Clip.Original.Fingerprint.Sha256,
                RoutingOutputKind.Original),
            RoutingMissingOutputBehavior.NeedsAttention,
            RoutingDeliveryMode.Automatic,
            Settings(),
            artifactReady: true,
            intentionalDuplicate: null,
            At(4));

        var resolver = new CaptureJournalRoutingArtifactResolver(library);
        await using var resolved = await resolver.ResolveAsync(delivery, CancellationToken.None);
        Assert(resolved.Path.Equals(landscapePath, StringComparison.OrdinalIgnoreCase) &&
               resolved.Sha256 == landscape.Fingerprint.Sha256 &&
               resolved.ByteLength == landscape.Fingerprint.ByteLength,
            "A derived logical output must resolve to its independently validated physical rendition.");
    }

    private static void AssertAttemptFences()
    {
        var planId = Guid.NewGuid();
        var route = RouteReference(Guid.NewGuid(), Guid.NewGuid(), 0);
        var output = new RoutingOutputReference("clip-fence", RoutingOutputKind.Original, Hash('f'));
        var delivery = RoutingOutboxModel.CreateDelivery(
            Guid.NewGuid(), planId, "clip-fence", route,
            RoutingDestinationKind.Discord, "discord.fence", output, output,
            RoutingMissingOutputBehavior.UseOriginal, RoutingDeliveryMode.Automatic,
            Settings(), artifactReady: true, intentionalDuplicate: null, At(0));
        var disposition = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), planId, "clip-fence", Hash('f'),
            route with { ActionId = Guid.NewGuid(), Order = 1 },
            RoutingLibraryArea.Uploaded, [delivery.DeliveryId], At(0));
        var current = RoutingOutboxModel.AppendPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), planId, [delivery], [disposition], At(1));
        var sendAttempt = Guid.NewGuid();
        current = RoutingOutboxModel.StartDelivery(current, delivery.DeliveryId, sendAttempt, At(2));
        AssertThrows<InvalidDataException>(() => RoutingOutboxModel.MarkDeliveryUnknown(
                current, delivery.DeliveryId, Guid.NewGuid(), "unknown", At(3)),
            "A stale provider attempt must not mark the current delivery unknown.");
        current = RoutingOutboxModel.MarkDeliveryUnknown(
            current, delivery.DeliveryId, sendAttempt, "unknown", At(3));
        Assert(current.Deliveries.Single().State == PlannedDeliveryState.DeliveryUnknown,
            "The current provider attempt must be able to persist an ambiguous result.");

        var fileOnlyPlan = Guid.NewGuid();
        var fileDisposition = RoutingOutboxModel.CreateFileDisposition(
            Guid.NewGuid(), fileOnlyPlan, "clip-file-fence", Hash('e'),
            RouteReference(Guid.NewGuid(), Guid.NewGuid(), 0),
            RoutingLibraryArea.LocalOnly, [], At(0));
        var fileCurrent = RoutingOutboxModel.AppendPlan(
            RoutingOutboxModel.CreateEmpty(At(0)), fileOnlyPlan, [], [fileDisposition], At(1));
        var fileAttempt = Guid.NewGuid();
        fileCurrent = RoutingOutboxModel.StartFileDisposition(
            fileCurrent, fileDisposition.DispositionId, fileAttempt, At(2));
        AssertThrows<InvalidDataException>(() =>
                RoutingOutboxModel.MarkFileDispositionRecoveryPending(
                    fileCurrent, fileDisposition.DispositionId, Guid.NewGuid(), "unknown", At(3)),
            "A stale file attempt must not claim an ambiguous move.");
        fileCurrent = RoutingOutboxModel.MarkFileDispositionRecoveryPending(
            fileCurrent, fileDisposition.DispositionId, fileAttempt, "unknown", At(3));
        Assert(fileCurrent.FileDispositions.Single().State ==
               PlannedFileDispositionState.RecoveryPending,
            "The current file attempt must be able to request exact recovery.");
    }

    private static RoutingOutboxExecutor Executor(
        RoutingOutboxStore store,
        IRoutingDeliveryProvider provider,
        IRoutingArtifactResolver resolver,
        IRoutingLibraryFiler filer,
        int maximumSideEffects = 32) =>
        new(
            store,
            provider,
            resolver,
            filer,
            () => true,
            maximumSideEffects,
            Guid.NewGuid,
            () => At(10));

    private static async Task<SeededPlan> SeedAsync(
        string root,
        bool includeDelivery = false,
        bool includeDisposition = false,
        int deliveryCount = 1,
        string sourceClipId = "clip-confirmed",
        string? sourceHash = null)
    {
        Directory.CreateDirectory(root);
        sourceHash ??= Hash('a');
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var empty = await store.LoadOrCreateAsync(At(0));
        var planId = Guid.NewGuid();
        var deliveries = new List<PlannedDelivery>();
        if (includeDelivery)
        {
            for (var index = 0; index < deliveryCount; index++)
            {
                var route = RouteReference(Guid.NewGuid(), Guid.NewGuid(), index);
                var output = new RoutingOutputReference(
                    sourceClipId, RoutingOutputKind.Original, sourceHash);
                deliveries.Add(RoutingOutboxModel.CreateDelivery(
                    Guid.NewGuid(),
                    planId,
                    sourceClipId,
                    route,
                    RoutingDestinationKind.Discord,
                    $"discord.connection-{index}",
                    output,
                    output,
                    RoutingMissingOutputBehavior.UseOriginal,
                    RoutingDeliveryMode.Automatic,
                    Settings(),
                    artifactReady: true,
                    intentionalDuplicate: null,
                    At(0)));
            }
        }
        PlannedFileDisposition? disposition = null;
        if (includeDisposition)
        {
            disposition = RoutingOutboxModel.CreateFileDisposition(
                Guid.NewGuid(),
                planId,
                sourceClipId,
                sourceHash,
                RouteReference(Guid.NewGuid(), Guid.NewGuid(), deliveryCount),
                includeDelivery ? RoutingLibraryArea.Uploaded : RoutingLibraryArea.LocalOnly,
                deliveries.Select(item => item.DeliveryId).ToArray(),
                At(0));
        }
        var planned = RoutingOutboxModel.AppendPlan(
            empty,
            planId,
            deliveries,
            disposition is null ? [] : [disposition],
            At(1));
        _ = await store.SaveAsync(planned, empty.Generation);
        return new SeededPlan(store, deliveries, disposition);
    }

    private static async Task<SeededPlan> SeedRecoveryBacklogAsync(
        string root,
        int recoveryCount,
        int deliveryCount)
    {
        Directory.CreateDirectory(root);
        var store = new RoutingOutboxStore(Path.Combine(root, RoutingOutboxStore.FileName));
        var current = await store.LoadOrCreateAsync(At(0));
        var tick = 0;

        for (var index = 0; index < recoveryCount; index++)
        {
            var planId = Guid.NewGuid();
            var sourceClipId = $"clip-recovery-{index}";
            var disposition = RoutingOutboxModel.CreateFileDisposition(
                Guid.NewGuid(),
                planId,
                sourceClipId,
                Hash('e'),
                RouteReference(Guid.NewGuid(), Guid.NewGuid(), index),
                RoutingLibraryArea.LocalOnly,
                [],
                At(++tick));
            var appended = RoutingOutboxModel.AppendPlan(
                current, planId, [], [disposition], At(++tick));
            current = await store.SaveAsync(appended, current.Generation);

            var attemptId = Guid.NewGuid();
            var moving = RoutingOutboxModel.StartFileDisposition(
                current, disposition.DispositionId, attemptId, At(++tick));
            current = await store.SaveAsync(moving, current.Generation);
            var pending = RoutingOutboxModel.MarkFileDispositionRecoveryPending(
                current,
                disposition.DispositionId,
                attemptId,
                "move-result-unknown",
                At(++tick));
            current = await store.SaveAsync(pending, current.Generation);
        }

        var deliveryPlanId = Guid.NewGuid();
        var deliveries = Enumerable.Range(0, deliveryCount).Select(index =>
        {
            var deliveryOutput = new RoutingOutputReference(
                "clip-ready-after-recovery", RoutingOutputKind.Original, Hash('d'));
            return RoutingOutboxModel.CreateDelivery(
                Guid.NewGuid(),
                deliveryPlanId,
                deliveryOutput.ClipId,
                RouteReference(
                    Guid.NewGuid(), Guid.NewGuid(), recoveryCount + index),
                RoutingDestinationKind.Discord,
                $"discord.recovery-fairness-{index}",
                deliveryOutput,
                deliveryOutput,
                RoutingMissingOutputBehavior.UseOriginal,
                RoutingDeliveryMode.Automatic,
                Settings(),
                artifactReady: true,
                intentionalDuplicate: null,
                At(++tick));
        }).ToArray();
        var withDelivery = RoutingOutboxModel.AppendPlan(
            current, deliveryPlanId, deliveries, [], At(++tick));
        _ = await store.SaveAsync(withDelivery, current.Generation);
        return new SeededPlan(store, deliveries, null);
    }

    private static RoutingRouteSnapshotReference RouteReference(
        Guid routeId,
        Guid actionId,
        int order) =>
        new(1, routeId, 1, $"Executor route {order}", actionId, order, order);

    private static RoutingDeliverySettings Settings() =>
        new(null, null, null, RoutingVisibility.Unspecified, false);

    private static string Hash(char value) => new(value, 64);

    private static DateTimeOffset At(int seconds) => Now.AddSeconds(seconds);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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

    private sealed record SeededPlan(
        RoutingOutboxStore Store,
        IReadOnlyList<PlannedDelivery> Deliveries,
        PlannedFileDisposition? Disposition);

    private sealed class RecordingProvider(
        Func<PlannedDelivery, RoutingResolvedArtifact, CancellationToken,
            RoutingDeliveryAttemptResult>? send = null) : IRoutingDeliveryProvider
    {
        private readonly Func<PlannedDelivery, RoutingResolvedArtifact, CancellationToken,
            RoutingDeliveryAttemptResult> _send = send ?? ((_, _, _) =>
            RoutingDeliveryAttemptResult.Confirmed("discord:default"));

        internal int Calls { get; private set; }

        public bool Supports(RoutingDestinationKind destination) =>
            destination == RoutingDestinationKind.Discord;

        public Task<RoutingDeliveryAttemptResult> SendAsync(
            PlannedDelivery delivery,
            RoutingResolvedArtifact artifact,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_send(delivery, artifact, cancellationToken));
        }
    }

    private sealed class RecordingResolver(
        Func<PlannedDelivery, RoutingResolvedArtifact>? resolve = null) : IRoutingArtifactResolver
    {
        private readonly Func<PlannedDelivery, RoutingResolvedArtifact> _resolve =
            resolve ?? CreateResolvedArtifact;

        public Task<RoutingResolvedArtifact> ResolveAsync(
            PlannedDelivery delivery,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_resolve(delivery));
        }
    }

    private static RoutingResolvedArtifact CreateResolvedArtifact(PlannedDelivery delivery) =>
        new(
            Path.Combine(Path.GetTempPath(), $"{delivery.DeliveryId:N}.mp4"),
            "clip.mp4",
            "Test Game",
            1,
            delivery.Output.Revision);

    private sealed class BlockingRecoveryFiler : IRoutingLibraryFiler
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RoutingFileRecoveryResult> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release(RoutingFileRecoveryResult result) =>
            _release.TrySetResult(result);

        public Task<RoutingFileAttemptResult> FileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The stale-recovery test must not start a file move.");

        public Task<RoutingFileRecoveryResult> ReconcileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult(true);
            return _release.Task;
        }
    }

    private sealed class BlockingInterleavingFiler : IRoutingLibraryFiler
    {
        private readonly TaskCompletionSource<bool> _firstRecoveryEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstRecovery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondRecoveryEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task FirstRecoveryEntered => _firstRecoveryEntered.Task;
        internal Task SecondRecoveryEntered => _secondRecoveryEntered.Task;
        internal int FileCalls { get; private set; }
        internal List<Guid> RecoveryDispositionIds { get; } = [];

        internal void ReleaseFirstRecovery() =>
            _releaseFirstRecovery.TrySetResult(true);

        public Task<RoutingFileAttemptResult> FileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken)
        {
            FileCalls++;
            throw new InvalidOperationException(
                "An unresolved ambiguous move must not enter the ordinary filing path.");
        }

        public async Task<RoutingFileRecoveryResult> ReconcileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken)
        {
            RecoveryDispositionIds.Add(disposition.DispositionId);
            if (RecoveryDispositionIds.Count == 1)
            {
                _firstRecoveryEntered.TrySetResult(true);
                await _releaseFirstRecovery.Task.ConfigureAwait(false);
            }
            else if (RecoveryDispositionIds.Count == 2)
            {
                _secondRecoveryEntered.TrySetResult(true);
            }
            return RoutingFileRecoveryResult.Unresolved;
        }
    }

    private sealed class RecordingFiler(
        Func<PlannedFileDisposition, CancellationToken, RoutingFileAttemptResult>? file = null,
        Func<PlannedFileDisposition, CancellationToken, RoutingFileRecoveryResult>? recovery = null)
        : IRoutingLibraryFiler
    {
        private readonly Func<PlannedFileDisposition, CancellationToken, RoutingFileAttemptResult>
            _file = file ?? ((disposition, _) => RoutingFileAttemptResult.Confirmed(
                $"capture:{disposition.SourceClipId}:original"));
        private readonly Func<PlannedFileDisposition, CancellationToken, RoutingFileRecoveryResult>
            _recovery = recovery ?? ((disposition, _) => RoutingFileRecoveryResult.Completed(
                $"capture:{disposition.SourceClipId}:original"));

        internal int FileCalls { get; private set; }
        internal int RecoveryCalls { get; private set; }

        public Task<RoutingFileAttemptResult> FileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken)
        {
            FileCalls++;
            return Task.FromResult(_file(disposition, cancellationToken));
        }

        public Task<RoutingFileRecoveryResult> ReconcileAsync(
            PlannedFileDisposition disposition,
            CancellationToken cancellationToken)
        {
            RecoveryCalls++;
            return Task.FromResult(_recovery(disposition, cancellationToken));
        }
    }
}
