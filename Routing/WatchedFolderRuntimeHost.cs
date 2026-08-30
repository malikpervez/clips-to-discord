namespace ClipsToDiscord;

internal sealed record RoutingWatchedFolderRuntimeHostOptions(
    TimeSpan PollInterval,
    TimeSpan ErrorRetryInterval,
    TimeSpan MaximumErrorRetryInterval,
    int MaximumConsecutiveLoopFailures,
    int MaximumCandidatesPerScan)
{
    internal static RoutingWatchedFolderRuntimeHostOptions Default { get; } = new(
        PollInterval: TimeSpan.FromSeconds(5),
        ErrorRetryInterval: TimeSpan.FromSeconds(2),
        MaximumErrorRetryInterval: TimeSpan.FromSeconds(30),
        MaximumConsecutiveLoopFailures: 5,
        MaximumCandidatesPerScan: 10_000);

    internal void Validate()
    {
        if (PollInterval < TimeSpan.Zero || PollInterval > TimeSpan.FromMinutes(5) ||
            ErrorRetryInterval < TimeSpan.Zero ||
            ErrorRetryInterval > TimeSpan.FromMinutes(5) ||
            MaximumErrorRetryInterval < ErrorRetryInterval ||
            MaximumErrorRetryInterval > TimeSpan.FromMinutes(15) ||
            MaximumConsecutiveLoopFailures is < 1 or > 100 ||
            MaximumCandidatesPerScan is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RoutingWatchedFolderRuntimeHostOptions),
                "The watched-folder runtime polling bounds are invalid.");
        }
    }
}

/// <summary>
/// Concrete components are built against the coordinator's current Routing lease. The host only
/// borrows that lease; it never reissues, transfers, or disposes coordinator ownership.
/// </summary>
internal sealed record RoutingWatchedFolderRuntimeDependencies(
    RoutingRuntimeFeatureGate FeatureGate,
    RoutingWatchedFolderIngress Ingress,
    RoutingOutboxExecutor Executor,
    bool HostOwnsExecutor = true);

internal sealed record RoutingWatchedFolderRuntimeOperations(
    Func<RoutingRuntimeGateInspection> InspectPermit,
    Func<CancellationToken, Task<IReadOnlyList<RoutingWatchedIngressResult>>> ReconcileAllAsync,
    Func<string, CancellationToken, Task<RoutingWatchedIngressResult>> AdmitPathAsync,
    Func<CancellationToken, Task<RoutingExecutorRunResult>> RunExecutorAsync,
    IDisposable? OwnedExecutor = null)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(InspectPermit);
        ArgumentNullException.ThrowIfNull(ReconcileAllAsync);
        ArgumentNullException.ThrowIfNull(AdmitPathAsync);
        ArgumentNullException.ThrowIfNull(RunExecutorAsync);
    }
}

internal sealed class RoutingWatchedFolderRuntimeAuthorityException(string message) :
    InvalidOperationException(message);

/// <summary>
/// Owns the active watched-folder Routing loop. Startup replays durable journals and recovers the
/// outbox before external files can be scanned. Every scan is fenced by the same sticky execution
/// authority and borrowed Routing ownership lease; losing either stops the host instead of falling
/// back to Legacy. The host has no Discord-process dependency.
/// </summary>
internal sealed class RoutingWatchedFolderRuntimeHost : IAsyncDisposable, IDisposable
{
    private readonly Func<AppSettings> _currentSettings;
    private readonly ClipCaptureSource _captureSource;
    private readonly string _canonicalRoot;
    private readonly uint _rootVolumeSerial;
    private readonly string _rootFileId;
    private readonly ClipProcessingOwnershipLease _ownership;
    private readonly RoutingRuntimeGateInspection _initialPermit;
    private readonly RoutingWatchedFolderRuntimeOperations _operations;
    private readonly RoutingWatchedFolderRuntimeHostOptions _options;
    private readonly FileReadinessTracker _readiness;
    private readonly Func<string, ClipCaptureSource, CancellationToken, IEnumerable<string>>
        _enumerateCandidates;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycle = new();
    private Task? _loop;
    private Exception? _failure;
    private string? _scanCursor;
    private int _resourcesReleased;
    private int _disposed;

    private RoutingWatchedFolderRuntimeHost(
        Func<AppSettings> currentSettings,
        ClipCaptureSource captureSource,
        string canonicalRoot,
        RoutingWatchedNativeFileIdentity rootIdentity,
        ClipProcessingOwnershipLease ownership,
        RoutingRuntimeGateInspection initialPermit,
        RoutingWatchedFolderRuntimeOperations operations,
        RoutingWatchedFolderRuntimeHostOptions options,
        FileReadinessTracker readiness,
        Func<string, ClipCaptureSource, CancellationToken, IEnumerable<string>>
            enumerateCandidates,
        Func<DateTime> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _currentSettings = currentSettings;
        _captureSource = captureSource;
        _canonicalRoot = canonicalRoot;
        _rootVolumeSerial = rootIdentity.VolumeSerialNumber;
        _rootFileId = rootIdentity.FileIdHex;
        _ownership = ownership;
        _initialPermit = initialPermit;
        _operations = operations;
        _options = options;
        _readiness = readiness;
        _enumerateCandidates = enumerateCandidates;
        _utcNow = utcNow;
        _delay = delay;
    }

    internal Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>
    /// Completes after the background loop is fully quiescent and faults with its terminal
    /// failure. StopAsync deliberately awaits the private loop instead so shutdown can always
    /// finish cleanup without rethrowing a previously surfaced runtime failure.
    /// </summary>
    internal Task Completion => _completion.Task;

    /// <summary>
    /// Production construction requires the catalog's exact adapter. The component factory sees
    /// the same borrowed lease so its gate, ingress, and executor can share the coordinator fence.
    /// </summary>
    internal static RoutingWatchedFolderRuntimeHost Create(
        AppSettings settings,
        Func<AppSettings> currentSettings,
        ClipProcessingOwnershipLease routingOwnership,
        IRoutingWatchedSourceAdapter adapter,
        Func<ClipProcessingOwnershipLease, RoutingWatchedFolderRuntimeDependencies>
            createDependencies,
        RoutingWatchedFolderRuntimeHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(createDependencies);
        ValidateCaptureConfiguration(settings, adapter.Source, requireExactAdapter: adapter);
        RoutingWatchedFolderRuntimeDependencies? dependencies = null;
        return CreateCore(
            settings,
            currentSettings,
            routingOwnership,
            adapter.Source,
            borrowed =>
            {
                dependencies = createDependencies(borrowed) ??
                               throw new InvalidOperationException(
                                   "The watched-folder runtime dependencies are missing.");
                ArgumentNullException.ThrowIfNull(dependencies.FeatureGate);
                ArgumentNullException.ThrowIfNull(dependencies.Ingress);
                ArgumentNullException.ThrowIfNull(dependencies.Executor);
                return new RoutingWatchedFolderRuntimeOperations(
                    dependencies.FeatureGate.Inspect,
                    dependencies.Ingress.ReconcileAllAsync,
                    (path, cancellationToken) => dependencies.Ingress.AdmitPathAsync(
                        adapter,
                        CanonicalRoot(settings.ClipsFolder),
                        path,
                        cancellationToken),
                    dependencies.Executor.RunOnceAsync,
                    dependencies.HostOwnsExecutor ? dependencies.Executor : null);
            },
            options ?? RoutingWatchedFolderRuntimeHostOptions.Default,
            new FileReadinessTracker(),
            DefaultEnumerateCandidates,
            () => DateTime.UtcNow,
            Task.Delay);
    }

    /// <summary>Focused seam for deterministic lifecycle tests; production uses <see cref="Create"/>.</summary>
    internal static RoutingWatchedFolderRuntimeHost CreateForTesting(
        AppSettings settings,
        Func<AppSettings> currentSettings,
        ClipProcessingOwnershipLease routingOwnership,
        ClipCaptureSource adapterSource,
        Func<ClipProcessingOwnershipLease, RoutingWatchedFolderRuntimeOperations>
            createOperations,
        RoutingWatchedFolderRuntimeHostOptions options,
        FileReadinessTracker readiness,
        Func<string, ClipCaptureSource, CancellationToken, IEnumerable<string>>
            enumerateCandidates,
        Func<DateTime> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ValidateCaptureConfiguration(settings, adapterSource, requireExactAdapter: null);
        return CreateCore(
            settings,
            currentSettings,
            routingOwnership,
            adapterSource,
            createOperations,
            options,
            readiness,
            enumerateCandidates,
            utcNow,
            delay);
    }

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Task loop;
        lock (_lifecycle)
        {
            _loop ??= RunCoreAsync(_shutdown.Token);
            loop = _loop;
        }

        using var registration = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            _shutdown);
        try
        {
            await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _shutdown.Cancel();
            await loop.ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _shutdown.Cancel();
        Task? loop;
        lock (_lifecycle) loop = _loop;
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }
        else
        {
            ReleaseOwnedResources();
        }
    }

    private static RoutingWatchedFolderRuntimeHost CreateCore(
        AppSettings settings,
        Func<AppSettings> currentSettings,
        ClipProcessingOwnershipLease routingOwnership,
        ClipCaptureSource adapterSource,
        Func<ClipProcessingOwnershipLease, RoutingWatchedFolderRuntimeOperations>
            createOperations,
        RoutingWatchedFolderRuntimeHostOptions options,
        FileReadinessTracker readiness,
        Func<string, ClipCaptureSource, CancellationToken, IEnumerable<string>>
            enumerateCandidates,
        Func<DateTime> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentNullException.ThrowIfNull(routingOwnership);
        ArgumentNullException.ThrowIfNull(createOperations);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(enumerateCandidates);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(delay);
        options.Validate();
        ValidateCaptureConfiguration(settings, adapterSource, requireExactAdapter: null);
        var canonicalRoot = CanonicalRoot(settings.ClipsFolder);
        var root = RoutingWatchedFileSystem.OpenOrdinaryRoot(canonicalRoot);
        RoutingWatchedNativeFileIdentity rootIdentity;
        using (root.Handle)
        {
            rootIdentity = root.Identity;
        }

        if (routingOwnership.Owner != ClipProcessingRuntimeOwner.Routing ||
            !routingOwnership.IsCurrent)
        {
            throw new InvalidOperationException(
                "The watched-folder runtime cannot borrow the current Routing pipeline lease.");
        }

        RoutingWatchedFolderRuntimeOperations? operations = null;
        try
        {
            operations = createOperations(routingOwnership) ??
                         throw new InvalidOperationException(
                             "The watched-folder runtime operations are missing.");
            operations.Validate();
            var initialPermit = operations.InspectPermit() ??
                                throw new InvalidDataException(
                                    "The watched-folder Routing permit is missing.");
            var host = new RoutingWatchedFolderRuntimeHost(
                currentSettings,
                adapterSource,
                canonicalRoot,
                rootIdentity,
                routingOwnership,
                initialPermit,
                operations,
                options,
                readiness,
                enumerateCandidates,
                utcNow,
                delay);
            host.RequireCurrentAuthority();
            return host;
        }
        catch
        {
            operations?.OwnedExecutor?.Dispose();
            throw;
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            RequireCurrentAuthority();
            var reconciled = await _operations.ReconcileAllAsync(cancellationToken)
                .ConfigureAwait(false);
            if (reconciled.Any(result => result.Status == RoutingWatchedIngressStatus.Disabled))
            {
                throw LostAuthority(
                    "Routing authority was revoked during watched-journal reconciliation.");
            }
            RequireCurrentAuthority();
            var recovered = await _operations.RunExecutorAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!recovered.Enabled)
            {
                throw LostAuthority(
                    "Routing authority was revoked during outbox startup recovery.");
            }
            RequireCurrentAuthority();
            _started.TrySetResult();

            var consecutiveFailures = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    RequireCurrentAuthority();
                    await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
                    RequireCurrentAuthority();
                    var execution = await _operations.RunExecutorAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!execution.Enabled)
                    {
                        throw LostAuthority(
                            "Routing authority was revoked before watched work could execute.");
                    }
                    RequireCurrentAuthority();
                    consecutiveFailures = 0;
                    await _delay(_options.PollInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (RoutingWatchedFolderRuntimeAuthorityException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= _options.MaximumConsecutiveLoopFailures)
                    {
                        throw new InvalidOperationException(
                            "The watched-folder Routing loop exceeded its bounded retry budget.",
                            exception);
                    }
                    var retry = ErrorRetryDelay(consecutiveFailures);
                    Log.Error(
                        $"Watched-folder Routing pass failed; retrying in {retry.TotalSeconds:0.#} seconds.",
                        exception);
                    await _delay(retry, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _started.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            _started.TrySetException(exception);
            _completion.TrySetException(exception);
            Log.Error("Watched-folder Routing runtime stopped fail-closed.", exception);
        }
        finally
        {
            try
            {
                ReleaseOwnedResources();
            }
            catch (Exception exception)
            {
                if (Interlocked.CompareExchange(ref _failure, exception, null) is null)
                {
                    _completion.TrySetException(exception);
                    Log.Error(
                        "Watched-folder Routing runtime cleanup failed.",
                        exception);
                }
                else
                {
                    Log.Error(
                        "Watched-folder Routing runtime cleanup also failed.",
                        exception);
                }
            }
            if (!_started.Task.IsCompleted)
            {
                _started.TrySetException(new InvalidOperationException(
                    "The watched-folder Routing runtime stopped before startup completed."));
            }
            if (Failure is null)
            {
                _completion.TrySetResult();
            }
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        _readiness.RemoveMissingFiles();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = _enumerateCandidates(
                _canonicalRoot,
                _captureSource,
                cancellationToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            _scanCursor = null;
            return;
        }
        var start = 0;
        if (_scanCursor is not null)
        {
            while (start < candidates.Length &&
                   StringComparer.OrdinalIgnoreCase.Compare(
                       candidates[start], _scanCursor) <= 0)
            {
                start++;
            }
            if (start == candidates.Length) start = 0;
        }
        var budget = Math.Min(_options.MaximumCandidatesPerScan, candidates.Length);
        for (var offset = 0; offset < budget; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[(start + offset) % candidates.Length];
            _scanCursor = candidate;
            string path;
            try
            {
                path = Path.GetFullPath(candidate);
                if (!seen.Add(path)) continue;
                RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
                    _canonicalRoot,
                    path,
                    requireDirectory: false,
                    "watched runtime candidate");
                var readiness = _readiness.Observe(new FileInfo(path), _utcNow());
                if (!readiness.IsReady) continue;

                RequireCurrentAuthority();
                var result = await _operations.AdmitPathAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status == RoutingWatchedIngressStatus.Disabled)
                {
                    // Admission pins one current generation. A legitimate route edit can
                    // invalidate that candidate without revoking the sticky authority; retry
                    // it on a later scan under the fresh generation.
                    RequireCurrentAuthority();
                    continue;
                }
                RequireCurrentAuthority();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RoutingWatchedFolderRuntimeAuthorityException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    InvalidDataException or ArgumentException or NotSupportedException)
            {
                Log.Error(
                    $"Watched-folder Routing skipped {Path.GetFileName(candidate)} for this pass.",
                    exception);
            }
        }
    }

    private void RequireCurrentAuthority()
    {
        if (!_ownership.IsCurrent ||
            _ownership.Owner != ClipProcessingRuntimeOwner.Routing)
        {
            throw LostAuthority("The watched-folder runtime lost Routing ownership.");
        }

        AppSettings current;
        try
        {
            current = _currentSettings() ??
                      throw new InvalidDataException(
                          "The current watched-folder settings are missing.");
        }
        catch (Exception exception)
        {
            throw new RoutingWatchedFolderRuntimeAuthorityException(
                $"The watched-folder settings cannot be revalidated: {exception.Message}");
        }
        if (!Enum.IsDefined(current.CaptureSource) ||
            current.CaptureSource != _captureSource ||
            !CanonicalRoot(current.ClipsFolder).Equals(
                _canonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw LostAuthority(
                "The watched-folder capture source or canonical root changed.");
        }

        try
        {
            var root = RoutingWatchedFileSystem.OpenOrdinaryRoot(_canonicalRoot);
            using (root.Handle)
            {
                if (root.Identity.VolumeSerialNumber != _rootVolumeSerial ||
                    !root.Identity.FileIdHex.Equals(_rootFileId, StringComparison.Ordinal))
                {
                    throw LostAuthority(
                        "The watched-folder root was replaced after activation.");
                }
            }
        }
        catch (RoutingWatchedFolderRuntimeAuthorityException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RoutingWatchedFolderRuntimeAuthorityException(
                $"The watched-folder root cannot be revalidated: {exception.Message}");
        }

        RoutingRuntimeGateInspection permit;
        try
        {
            permit = _operations.InspectPermit() ??
                     throw new InvalidDataException(
                         "The watched-folder Routing permit is missing.");
        }
        catch (Exception exception)
        {
            throw new RoutingWatchedFolderRuntimeAuthorityException(
                $"The watched-folder Routing permit cannot be inspected: {exception.Message}");
        }
        if (!_initialPermit.SameAuthority(permit) ||
            !permit.Enabled || !permit.HasRequiredSourceCoverage ||
            permit.ExecutionAuthorityActivationId is null ||
            permit.OwnershipEpoch != _ownership.Epoch ||
            permit.RequiredLegacySource != _captureSource ||
            permit.RoutingGeneration is null or <= 0 ||
            permit.MarkerPayloadFingerprint is not { Length: 64 })
        {
            throw LostAuthority(
                "The watched-folder runtime does not hold complete sticky Routing authority.");
        }
    }

    private TimeSpan ErrorRetryDelay(int consecutiveFailures)
    {
        if (_options.ErrorRetryInterval == TimeSpan.Zero) return TimeSpan.Zero;
        var multiplier = Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        var ticks = Math.Min(
            _options.ErrorRetryInterval.Ticks * multiplier,
            _options.MaximumErrorRetryInterval.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private void ReleaseOwnedResources()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0) return;
        _operations.OwnedExecutor?.Dispose();
    }

    internal static IEnumerable<string> DefaultEnumerateCandidates(
        string canonicalRoot,
        ClipCaptureSource captureSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var path in WatchStateStore.EnumerateClips(canonicalRoot, captureSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    private static void ValidateCaptureConfiguration(
        AppSettings settings,
        ClipCaptureSource adapterSource,
        IRoutingWatchedSourceAdapter? requireExactAdapter)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.CaptureSource) ||
            settings.CaptureSource != adapterSource ||
            (requireExactAdapter is not null &&
             !ReferenceEquals(
                 requireExactAdapter,
                 RoutingWatchedSourceAdapters.Get(settings.CaptureSource))))
        {
            throw new InvalidOperationException(
                "The watched-folder runtime requires the exact configured capture-source adapter.");
        }
        var canonical = CanonicalRoot(settings.ClipsFolder);
        if (!settings.ClipsFolder.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The watched-folder runtime requires one canonical absolute capture root.");
        }
    }

    private static string CanonicalRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException(
                "The watched-folder capture root must be fully qualified.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static RoutingWatchedFolderRuntimeAuthorityException LostAuthority(
        string message) => new(message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAsync().AsTask().GetAwaiter().GetResult();
        _shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
