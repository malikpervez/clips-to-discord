using System.Collections.Concurrent;
using System.Reflection;
using ClipsToDiscord;

internal static class RoutingWatchedFolderRuntimeHostTests
{
    private static readonly DateTime Now =
        new(2026, 8, 29, 20, 0, 0, DateTimeKind.Utc);
    private static readonly RoutingWatchedFolderRuntimeHostOptions FastOptions = new(
        PollInterval: TimeSpan.FromMilliseconds(1),
        ErrorRetryInterval: TimeSpan.FromMilliseconds(1),
        MaximumErrorRetryInterval: TimeSpan.FromMilliseconds(1),
        MaximumConsecutiveLoopFailures: 2,
        MaximumCandidatesPerScan: 100);

    internal static async Task RunAsync(string testRoot)
    {
        Directory.CreateDirectory(testRoot);
        await AssertStartupRecoveryPrecedesFirstScanAndIgnoresDiscordAsync(
            Path.Combine(testRoot, "startup-order"));
        await AssertPartialFileWaitsThenAdmissionPrecedesExecutionAsync(
            Path.Combine(testRoot, "readiness-order"));
        await AssertStopCancelsAndQuiescesInFlightAdmissionAsync(
            Path.Combine(testRoot, "quiescence"));
        await AssertGateLeaseAndSettingsLossFailClosedAsync(
            Path.Combine(testRoot, "authority-loss"));
        await AssertRouteGenerationChangeRemainsActiveAsync(
            Path.Combine(testRoot, "route-generation"));
        await AssertScanCursorPreventsCandidateStarvationAsync(
            Path.Combine(testRoot, "scan-cursor"));
        AssertExactEnumeratorGeometryAndAdapterSelection(
            Path.Combine(testRoot, "source-geometry"));
    }

    private static async Task AssertStartupRecoveryPrecedesFirstScanAndIgnoresDiscordAsync(
        string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg, upload: true);
        using var ownership = RoutingLease.Create();
        var events = new ConcurrentQueue<string>();
        var scanned = Signal();
        var executorLifetime = new RecordingDisposable();
        TestAuthority? authority = null;
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            settings,
            () => settings,
            ownership.Lease,
            ClipCaptureSource.SteelSeriesGg,
            borrowed =>
            {
                Assert(ReferenceEquals(borrowed, ownership.Lease),
                    "The runtime host must borrow the coordinator's exact Routing lease.");
                authority = new TestAuthority(borrowed, settings.CaptureSource);
                return Operations(
                    authority,
                    reconcile: _ =>
                    {
                        events.Enqueue("reconcile");
                        return Task.FromResult<IReadOnlyList<RoutingWatchedIngressResult>>([]);
                    },
                    execute: _ =>
                    {
                        events.Enqueue("execute-startup");
                        return Task.FromResult(EnabledExecution());
                    },
                    ownedExecutor: executorLifetime);
            },
            FastOptions,
            new FileReadinessTracker(),
            (candidateRoot, source, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert(candidateRoot == clips && source == ClipCaptureSource.SteelSeriesGg,
                    "The host scan must retain its one canonical root and configured source.");
                events.Enqueue("scan");
                scanned.TrySetResult(true);
                return [];
            },
            () => Now,
            static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        await host.StartAsync();
        await scanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();
        var order = events.ToArray();
        Assert(IndexOf(order, "reconcile") < IndexOf(order, "execute-startup") &&
               IndexOf(order, "execute-startup") < IndexOf(order, "scan"),
            "Startup must reconcile journals, recover/run the outbox, and only then scan external clips.");
        Assert(host.Failure is null && executorLifetime.Disposed &&
               ownership.Lease.IsCurrent &&
               ownership.Coordinator.Owner == ClipProcessingRuntimeOwner.Routing,
            "Stopping the host must dispose its executor but leave the borrowed Routing lease to the coordinator.");
        Assert(!typeof(RoutingWatchedFolderRuntimeHost)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Any(field => field.Name.Contains("discord", StringComparison.OrdinalIgnoreCase)),
            "The active watched-folder host must not carry a Discord-process dependency.");
        Assert(authority is not null,
            "The startup fixture did not construct authority against the borrowed lease.");
    }

    private static async Task AssertPartialFileWaitsThenAdmissionPrecedesExecutionAsync(
        string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var path = Path.Combine(clips, "Ingress Game 2026.08.29.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(-1));
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        var clock = new TestClock(Now);
        using var ownership = RoutingLease.Create();
        var events = new ConcurrentQueue<string>();
        var executionAfterAdmission = Signal();
        var scan = 0;
        var executions = 0;
        var admissions = 0;
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            settings,
            () => settings,
            ownership.Lease,
            settings.CaptureSource,
            borrowed =>
            {
                var authority = new TestAuthority(borrowed, settings.CaptureSource);
                return Operations(
                    authority,
                    admit: (_, _) =>
                    {
                        Interlocked.Increment(ref admissions);
                        events.Enqueue("admit");
                        return Task.FromResult(Planned());
                    },
                    execute: _ =>
                    {
                        var call = Interlocked.Increment(ref executions);
                        events.Enqueue($"execute:{call}");
                        if (Volatile.Read(ref admissions) > 0)
                        {
                            executionAfterAdmission.TrySetResult(true);
                        }
                        return Task.FromResult(EnabledExecution());
                    });
            },
            FastOptions,
            new FileReadinessTracker(),
            (_, source, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert(source == ClipCaptureSource.SteelSeriesGg,
                    "The readiness scan used the wrong source geometry.");
                var call = Interlocked.Increment(ref scan);
                events.Enqueue($"scan:{call}");
                return [path];
            },
            clock.UtcNow,
            async (_, cancellationToken) =>
            {
                clock.Advance(TimeSpan.FromSeconds(11));
                await Task.Delay(1, cancellationToken);
            });

        await host.StartAsync();
        await executionAfterAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();
        var order = events.ToArray();
        Assert(Volatile.Read(ref scan) >= 2 && Volatile.Read(ref admissions) == 1 &&
               IndexOf(order, "scan:1") < IndexOf(order, "execute:2") &&
               IndexOf(order, "execute:2") < IndexOf(order, "scan:2") &&
               IndexOf(order, "scan:2") < IndexOf(order, "admit") &&
               IndexOf(order, "admit") < IndexOf(order, "execute:3"),
            "A partial file must wait for a second stable observation, then admission must finish before that pass executes the outbox.");
        Assert(host.Failure is null && ownership.Lease.IsCurrent,
            "The stable-file pass must finish without disturbing coordinator ownership.");
    }

    private static async Task AssertStopCancelsAndQuiescesInFlightAdmissionAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var path = Path.Combine(clips, "Ingress Game blocking.mp4");
        await File.WriteAllBytesAsync(path, [5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(-1));
        var tracker = new FileReadinessTracker();
        _ = tracker.Observe(new FileInfo(path), Now);
        var clock = new TestClock(Now.AddSeconds(11));
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        using var ownership = RoutingLease.Create();
        var entered = Signal();
        var exited = Signal();
        var callbacks = 0;
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            settings,
            () => settings,
            ownership.Lease,
            settings.CaptureSource,
            borrowed =>
            {
                var authority = new TestAuthority(borrowed, settings.CaptureSource);
                return Operations(
                    authority,
                    admit: async (_, cancellationToken) =>
                    {
                        Interlocked.Increment(ref callbacks);
                        entered.TrySetResult(true);
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                            throw new InvalidOperationException(
                                "The blocking admission unexpectedly resumed.");
                        }
                        finally
                        {
                            exited.TrySetResult(true);
                        }
                    });
            },
            FastOptions,
            tracker,
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return [path];
            },
            clock.UtcNow,
            static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        await host.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var callsAtStop = Volatile.Read(ref callbacks);
        await Task.Delay(20);
        Assert(callsAtStop == 1 && Volatile.Read(ref callbacks) == callsAtStop &&
               host.Completion.IsCompleted && host.Failure is null &&
               ownership.Lease.IsCurrent &&
               ownership.Coordinator.Owner == ClipProcessingRuntimeOwner.Routing,
            "Stop must cancel and await every host callback while leaving the borrowed Routing lease current.");
    }

    private static async Task AssertScanCursorPreventsCandidateStarvationAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var candidates = new[]
        {
            Path.Combine(clips, "A Game clip.mp4"),
            Path.Combine(clips, "B Game clip.mp4"),
            Path.Combine(clips, "Z New clip.mp4")
        };
        foreach (var path in candidates)
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(path, Now.AddMinutes(-1));
        }
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        var clock = new TestClock(Now);
        using var ownership = RoutingLease.Create();
        var admittedPaths = new ConcurrentDictionary<string, byte>(
            StringComparer.OrdinalIgnoreCase);
        var allAdmitted = Signal();
        var cappedOptions = FastOptions with { MaximumCandidatesPerScan = 2 };
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            settings,
            () => settings,
            ownership.Lease,
            settings.CaptureSource,
            borrowed =>
            {
                var authority = new TestAuthority(borrowed, settings.CaptureSource);
                return Operations(
                    authority,
                    admit: (path, _) =>
                    {
                        admittedPaths.TryAdd(path, 0);
                        if (admittedPaths.Count == candidates.Length)
                        {
                            allAdmitted.TrySetResult(true);
                        }
                        return Task.FromResult(Planned());
                    });
            },
            cappedOptions,
            new FileReadinessTracker(),
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return candidates;
            },
            clock.UtcNow,
            async (_, cancellationToken) =>
            {
                clock.Advance(TimeSpan.FromSeconds(11));
                await Task.Delay(1, cancellationToken);
            });

        await host.StartAsync();
        await allAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();
        Assert(admittedPaths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(candidates) &&
               host.Failure is null,
            "A bounded scan must rotate beyond earlier candidates so a new clip after the cap cannot be starved forever.");
    }

    private static async Task AssertGateLeaseAndSettingsLossFailClosedAsync(string root)
    {
        await AssertGateLossAsync(Path.Combine(root, "gate"));
        await AssertLeaseLossAsync(Path.Combine(root, "lease"));
        await AssertSourceChangeAsync(Path.Combine(root, "source"));
    }

    private static async Task AssertGateLossAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        using var ownership = RoutingLease.Create();
        TestAuthority? authority = null;
        var scan = Signal();
        var executions = 0;
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            settings,
            () => settings,
            ownership.Lease,
            settings.CaptureSource,
            borrowed =>
            {
                authority = new TestAuthority(borrowed, settings.CaptureSource);
                return Operations(authority, execute: _ =>
                {
                    Interlocked.Increment(ref executions);
                    return Task.FromResult(EnabledExecution());
                });
            },
            FastOptions,
            new FileReadinessTracker(),
            (_, _, _) =>
            {
                authority!.Enabled = false;
                scan.TrySetResult(true);
                return [];
            },
            () => Now,
            static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        await host.StartAsync();
        await scan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertFaultedCompletionAsync(host);
        Assert(host.Failure is RoutingWatchedFolderRuntimeAuthorityException &&
               Volatile.Read(ref executions) == 1 && ownership.Lease.IsCurrent,
            "Losing the authority-backed feature gate must stop before another executor pass without releasing coordinator ownership.");
    }

    private static async Task AssertLeaseLossAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        using var ownership = RoutingLease.Create();
        var delayEntered = Signal();
        var releaseDelay = Signal();
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            settings,
            () => settings,
            ownership.Lease,
            settings.CaptureSource,
            borrowed => Operations(new TestAuthority(borrowed, settings.CaptureSource)),
            FastOptions,
            new FileReadinessTracker(),
            static (_, _, _) => [],
            () => Now,
            async (_, cancellationToken) =>
            {
                delayEntered.TrySetResult(true);
                await releaseDelay.Task.WaitAsync(cancellationToken);
            });

        await host.StartAsync();
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ownership.Lease.Dispose();
        releaseDelay.TrySetResult(true);
        await AssertFaultedCompletionAsync(host);
        Assert(host.Failure is RoutingWatchedFolderRuntimeAuthorityException &&
               ownership.Coordinator.Owner is null,
            "A stale borrowed lease must stop the host fail-closed; the host must not acquire or start Legacy.");
    }

    private static async Task AssertSourceChangeAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var initial = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        var current = initial;
        using var ownership = RoutingLease.Create();
        var delayEntered = Signal();
        var releaseDelay = Signal();
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            initial,
            () => current,
            ownership.Lease,
            initial.CaptureSource,
            borrowed => Operations(new TestAuthority(borrowed, initial.CaptureSource)),
            FastOptions,
            new FileReadinessTracker(),
            static (_, _, _) => [],
            () => Now,
            async (_, cancellationToken) =>
            {
                delayEntered.TrySetResult(true);
                await releaseDelay.Task.WaitAsync(cancellationToken);
            });

        await host.StartAsync();
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        current = current with { CaptureSource = ClipCaptureSource.Nvidia };
        releaseDelay.TrySetResult(true);
        await AssertFaultedCompletionAsync(host);
        Assert(host.Failure is RoutingWatchedFolderRuntimeAuthorityException &&
               ownership.Lease.IsCurrent,
            "Changing the configured capture source must stop the old host without transferring its borrowed lease.");
    }

    private static async Task AssertRouteGenerationChangeRemainsActiveAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var path = Path.Combine(clips, "Generation Edit 2026.08.29.mp4");
        await File.WriteAllBytesAsync(path, [2, 4, 6, 8]);
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(-1));
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        using var ownership = RoutingLease.Create();
        var enteredDelay = new SemaphoreSlim(0);
        var releaseDelay = new SemaphoreSlim(0);
        var admitted = Signal();
        var scanEnabled = 0;
        TestAuthority? authority = null;
        var executions = 0;
        var admissions = 0;
        var tracker = new FileReadinessTracker();
        _ = tracker.Observe(new FileInfo(path), Now);
        var clock = new TestClock(Now.AddSeconds(11));
        await using var host = RoutingWatchedFolderRuntimeHost.CreateForTesting(
            settings,
            () => settings,
            ownership.Lease,
            settings.CaptureSource,
            borrowed =>
            {
                authority = new TestAuthority(borrowed, settings.CaptureSource);
                return Operations(
                    authority,
                    admit: (_, _) =>
                    {
                        var call = Interlocked.Increment(ref admissions);
                        if (call == 1)
                        {
                            authority.RoutingGeneration = 2;
                            return Task.FromResult(new RoutingWatchedIngressResult(
                                RoutingWatchedIngressStatus.Disabled,
                                null,
                                null,
                                null));
                        }
                        admitted.TrySetResult(true);
                        return Task.FromResult(Planned());
                    },
                    execute: _ =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(EnabledExecution());
                    });
            },
            FastOptions,
            tracker,
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Volatile.Read(ref scanEnabled) == 0 ? [] : [path];
            },
            clock.UtcNow,
            async (_, cancellationToken) =>
            {
                clock.Advance(TimeSpan.FromSeconds(11));
                enteredDelay.Release();
                await releaseDelay.WaitAsync(cancellationToken);
            });

        await host.StartAsync();
        Assert(await enteredDelay.WaitAsync(TimeSpan.FromSeconds(5)),
            "The watched host did not enter its first poll delay.");
        Volatile.Write(ref scanEnabled, 1);
        releaseDelay.Release();
        Assert(await enteredDelay.WaitAsync(TimeSpan.FromSeconds(5)),
            "The watched host did not continue after a generation-invalidated admission.");
        releaseDelay.Release();
        Assert(await enteredDelay.WaitAsync(TimeSpan.FromSeconds(5)),
            "The watched host did not retain the candidate for a fresh stability observation.");
        releaseDelay.Release();
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(host.Failure is null && !host.Completion.IsCompleted &&
               Volatile.Read(ref admissions) == 2 &&
               Volatile.Read(ref executions) >= 3 &&
               authority!.RoutingGeneration == 2 && ownership.Lease.IsCurrent,
            "A legitimate route edit must skip only its stale admission, retry under the new generation, and keep the active executor and lease.");
        await host.StopAsync();
    }

    private static void AssertExactEnumeratorGeometryAndAdapterSelection(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var flat = Write(Path.Combine(clips, "flat.mp4"));
        var nested = Write(Path.Combine(clips, "Game", "nested.mp4"));
        _ = Write(Path.Combine(clips, "Game", "Deep", "too-deep.mp4"));
        _ = Write(Path.Combine(clips, "uploaded", "managed.mp4"));
        var steel = RoutingWatchedFolderRuntimeHost.DefaultEnumerateCandidates(
                clips,
                ClipCaptureSource.SteelSeriesGg,
                CancellationToken.None)
            .ToArray();
        var nvidia = RoutingWatchedFolderRuntimeHost.DefaultEnumerateCandidates(
                clips,
                ClipCaptureSource.Nvidia,
                CancellationToken.None)
            .ToArray();
        Assert(steel.Count(path => path.Equals(flat, StringComparison.OrdinalIgnoreCase)) == 1 &&
               steel.Length == 1 &&
               nvidia.Count(path => path.Equals(nested, StringComparison.OrdinalIgnoreCase)) == 1 &&
               nvidia.Length == 1,
            "The host must select WatchStateStore's exact SteelSeries root-leaf or NVIDIA game/leaf geometry.");

        using var ownership = RoutingLease.Create();
        var settings = Settings(clips, ClipCaptureSource.SteelSeriesGg);
        AssertThrows<InvalidOperationException>(() =>
            _ = RoutingWatchedFolderRuntimeHost.CreateForTesting(
                settings,
                () => settings,
                ownership.Lease,
                ClipCaptureSource.Nvidia,
                _ => throw new InvalidOperationException("must not build"),
                FastOptions,
                new FileReadinessTracker(),
                static (_, _, _) => [],
                () => Now,
                static (_, _) => Task.CompletedTask));
        Assert(ownership.Lease.IsCurrent,
            "Rejecting a mismatched adapter must not mutate the coordinator's Routing lease.");
    }

    private static RoutingWatchedFolderRuntimeOperations Operations(
        TestAuthority authority,
        Func<CancellationToken, Task<IReadOnlyList<RoutingWatchedIngressResult>>>? reconcile = null,
        Func<string, CancellationToken, Task<RoutingWatchedIngressResult>>? admit = null,
        Func<CancellationToken, Task<RoutingExecutorRunResult>>? execute = null,
        IDisposable? ownedExecutor = null) => new(
        authority.Inspect,
        reconcile ?? (_ =>
            Task.FromResult<IReadOnlyList<RoutingWatchedIngressResult>>([])),
        admit ?? ((_, _) => Task.FromResult(Planned())),
        execute ?? (_ => Task.FromResult(EnabledExecution())),
        ownedExecutor);

    private static RoutingWatchedIngressResult Planned() => new(
        RoutingWatchedIngressStatus.Planned,
        "watched:" + new string('a', 64),
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        RoutingWatchedJournalAdmissionKind.PreparedPlan);

    private static RoutingExecutorRunResult EnabledExecution() =>
        new(true, 0, 0, 0, 0);

    private static AppSettings Settings(
        string clipsRoot,
        ClipCaptureSource source,
        bool upload = false) => new(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(clipsRoot)),
        upload
            ? "https://discord.com/api/webhooks/123456789012345678/test-token"
            : string.Empty,
        StartWithWindows: false,
        AppSettings.DefaultCompressionTargetMb,
        "Runtime Host Test",
        upload,
        ModeToggleHotkey: string.Empty,
        source);

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return Path.GetFullPath(path);
    }

    private static int IndexOf(IReadOnlyList<string> values, string expected)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].Equals(expected, StringComparison.Ordinal)) return index;
        }
        return int.MaxValue;
    }

    private static TaskCompletionSource<bool> Signal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertFaultedCompletionAsync(
        RoutingWatchedFolderRuntimeHost host)
    {
        try
        {
            await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (RoutingWatchedFolderRuntimeAuthorityException exception)
        {
            Assert(ReferenceEquals(exception, host.Failure),
                "The faulted completion must surface the host's exact terminal failure.");
            return;
        }
        throw new InvalidOperationException(
            "The watched-folder host completion did not fault after authority loss.");
    }

    private sealed class TestClock(DateTime initial)
    {
        private long _ticks = initial.Ticks;
        internal DateTime UtcNow() =>
            new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);
        internal void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class TestAuthority(
        ClipProcessingOwnershipLease ownership,
        ClipCaptureSource source)
    {
        private int _enabled = 1;
        private long _routingGeneration = 1;

        internal bool Enabled
        {
            get => Volatile.Read(ref _enabled) != 0;
            set => Volatile.Write(ref _enabled, value ? 1 : 0);
        }

        internal long RoutingGeneration
        {
            get => Interlocked.Read(ref _routingGeneration);
            set => Interlocked.Exchange(ref _routingGeneration, value);
        }

        internal RoutingRuntimeGateInspection Inspect()
        {
            if (!Enabled || !ownership.IsCurrent)
            {
                return new RoutingRuntimeGateInspection(
                    RoutingRuntimeGateState.OwnershipUnavailable,
                    0, 0, 0, 0,
                    RoutingGeneration: null,
                    MarkerPayloadFingerprint: null,
                    OwnershipEpoch: ownership.Epoch,
                    RequiredLegacySource: source,
                    CoveredLegacySources: new HashSet<ClipCaptureSource> { source },
                    ExecutionAuthorityActivationId: null);
            }
            return new RoutingRuntimeGateInspection(
                RoutingRuntimeGateState.Enabled,
                0, 0, 0, 0,
                RoutingGeneration,
                MarkerPayloadFingerprint: new string('b', 64),
                OwnershipEpoch: ownership.Epoch,
                RequiredLegacySource: source,
                CoveredLegacySources: new HashSet<ClipCaptureSource> { source },
                ExecutionAuthorityActivationId:
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        }
    }

    private sealed class RoutingLease : IDisposable
    {
        private RoutingLease(
            ClipProcessingOwnershipCoordinator coordinator,
            ClipProcessingOwnershipLease lease)
        {
            Coordinator = coordinator;
            Lease = lease;
        }

        internal ClipProcessingOwnershipCoordinator Coordinator { get; }
        internal ClipProcessingOwnershipLease Lease { get; }

        internal static RoutingLease Create()
        {
            var coordinator = new ClipProcessingOwnershipCoordinator();
            if (!coordinator.TryAcquire(
                    ClipProcessingRuntimeOwner.Routing,
                    out var lease) || lease is null)
            {
                throw new InvalidOperationException(
                    "The runtime-host test could not acquire Routing ownership.");
            }
            return new RoutingLease(coordinator, lease);
        }

        public void Dispose() => Lease.Dispose();
    }

    private sealed class RecordingDisposable : IDisposable
    {
        private int _disposed;
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private static void AssertThrows<TException>(Action action)
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
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
