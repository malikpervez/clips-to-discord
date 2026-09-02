namespace ClipsToDiscord;

internal sealed record RoutingActiveWorkSessionStorage(string RoutingRoot)
{
    internal static RoutingActiveWorkSessionStorage Default { get; } = new(
        Path.Combine(SettingsStore.DataDirectory, "routing"));

    internal string SnapshotPath => Path.Combine(RoutingRoot, RoutingSnapshotStore.FileName);
    internal string OutboxPath => Path.Combine(RoutingRoot, RoutingOutboxStore.FileName);
    internal string MigrationMarkerPath => Path.Combine(
        RoutingRoot, LegacyRoutingMigrationMarkerStore.FileName);
    internal string WatchedJournalRoot => Path.Combine(RoutingRoot, "watched-journal", "v1");
    internal string DeliveryReceiptPath => Path.Combine(
        RoutingRoot, RoutingDeliveryReceiptStore.FileName);
    internal string DiscordConnectionPath => Path.Combine(
        RoutingRoot, DiscordConnectionCatalogStore.FileName);
    internal string InputSourcePath => Path.Combine(
        RoutingRoot, RoutingInputSourceCatalogStore.FileName);
    internal string LocalOnlyOverridePath => Path.Combine(
        RoutingRoot, RoutingLocalOnlyOverrideStore.FileName);

    internal RoutingActiveWorkSessionStorage Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RoutingRoot);
        if (!Path.IsPathFullyQualified(RoutingRoot))
        {
            throw new InvalidOperationException(
                "The active Routing storage root must be fully qualified.");
        }
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RoutingRoot));
        if (!RoutingRoot.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The active Routing storage root must already be canonical.");
        }
        return this with { RoutingRoot = canonical };
    }
}

internal sealed record RoutingActiveWorkSessionOptions(
    CaptureJournalRoutingPumpOptions CapturePump,
    RoutingWatchedFolderRuntimeHostOptions WatchedHost,
    XboxDvrRuntimeHostOptions? XboxHost = null)
{
    internal static RoutingActiveWorkSessionOptions Default { get; } = new(
        CaptureJournalRoutingPumpOptions.Default,
        RoutingWatchedFolderRuntimeHostOptions.Default,
        XboxDvrRuntimeHostOptions.Default);

    internal XboxDvrRuntimeHostOptions EffectiveXboxHost =>
        XboxHost ?? XboxDvrRuntimeHostOptions.Default;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(CapturePump);
        ArgumentNullException.ThrowIfNull(WatchedHost);
        CapturePump.Validate();
        WatchedHost.Validate();
        EffectiveXboxHost.Validate();
    }
}

internal sealed record RoutingActiveWorkSessionOperations(
    Func<CancellationToken, ValueTask> StartCaptureAsync,
    Func<CancellationToken, ValueTask> StopCaptureAsync,
    Func<Task> CaptureCompletion,
    Func<Exception?> CaptureFailure,
    Func<CancellationToken, ValueTask> StartWatchedAsync,
    Func<CancellationToken, ValueTask> StopWatchedAsync,
    Func<Task> WatchedCompletion,
    Func<Exception?> WatchedFailure,
    Func<ValueTask> DisposeOwnedAsync)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(StartCaptureAsync);
        ArgumentNullException.ThrowIfNull(StopCaptureAsync);
        ArgumentNullException.ThrowIfNull(CaptureCompletion);
        ArgumentNullException.ThrowIfNull(CaptureFailure);
        ArgumentNullException.ThrowIfNull(StartWatchedAsync);
        ArgumentNullException.ThrowIfNull(StopWatchedAsync);
        ArgumentNullException.ThrowIfNull(WatchedCompletion);
        ArgumentNullException.ThrowIfNull(WatchedFailure);
        ArgumentNullException.ThrowIfNull(DisposeOwnedAsync);
    }
}

internal enum RoutingActiveWorkSessionState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed
}

internal sealed record OptionalXboxRuntimeInstance(
    Func<CancellationToken, Task> StartAsync,
    Func<CancellationToken, ValueTask> StopAsync,
    Task Completion,
    Func<Exception?> Failure,
    Func<ValueTask> DisposeAsync)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(StartAsync);
        ArgumentNullException.ThrowIfNull(StopAsync);
        ArgumentNullException.ThrowIfNull(Completion);
        ArgumentNullException.ThrowIfNull(Failure);
        ArgumentNullException.ThrowIfNull(DisposeAsync);
    }

    internal static OptionalXboxRuntimeInstance From(XboxDvrRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new OptionalXboxRuntimeInstance(
            host.StartAsync,
            host.StopAsync,
            host.Completion,
            () => host.Failure,
            host.DisposeAsync);
    }
}

internal enum OptionalXboxRuntimeStatus
{
    Created,
    Starting,
    Running,
    RetryPending,
    Stopping,
    Stopped,
    Disposed
}

internal sealed record OptionalXboxRuntimeInspection(
    OptionalXboxRuntimeStatus Status,
    int RestartCount,
    Exception? LastFailure);

/// <summary>
/// Owns the optional Xbox worker independently from the aggregate watched/Capture lifetime. A
/// terminal Xbox instance is disposed and recreated after bounded backoff; stopping the aggregate
/// cancels that retry loop without promoting an Xbox-only failure to an aggregate failure.
/// </summary>
internal sealed class OptionalXboxRuntimeSupervisor : IAsyncDisposable, IDisposable
{
    private readonly Func<OptionalXboxRuntimeInstance> _factory;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maximumRetryDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string, Exception> _reportFailure;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _firstAttempt = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private OptionalXboxRuntimeInstance? _current;
    private Task? _loop;
    private Exception? _lastFailure;
    private int _status = (int)OptionalXboxRuntimeStatus.Created;
    private int _restartCount;
    private int _disposed;

    internal OptionalXboxRuntimeSupervisor(
        Func<OptionalXboxRuntimeInstance> factory,
        TimeSpan retryDelay,
        TimeSpan? maximumRetryDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string, Exception>? reportFailure = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        var maximum = maximumRetryDelay ?? retryDelay;
        if (retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromMinutes(15) ||
            maximum < retryDelay || maximum > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        _initialRetryDelay = retryDelay;
        _maximumRetryDelay = maximum;
        _delay = delay ?? Task.Delay;
        _reportFailure = reportFailure ?? ((_, _) => { });
    }

    internal Task Completion
    {
        get
        {
            lock (_sync) return _loop ?? Task.CompletedTask;
        }
    }

    internal OptionalXboxRuntimeInspection Inspect() => new(
        (OptionalXboxRuntimeStatus)Volatile.Read(ref _status),
        Volatile.Read(ref _restartCount),
        Volatile.Read(ref _lastFailure));

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if ((OptionalXboxRuntimeStatus)Volatile.Read(ref _status) is
            OptionalXboxRuntimeStatus.Stopping or OptionalXboxRuntimeStatus.Stopped)
        {
            throw new InvalidOperationException(
                "A stopped optional Xbox runtime supervisor cannot be restarted.");
        }
        lock (_sync)
        {
            _loop ??= RunAsync(_shutdown.Token);
        }
        // Startup of this optional family is considered complete after its first attempt. A failed
        // attempt is observable through Inspect and retried without blocking watched/Capture work.
        try
        {
            await _firstAttempt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if ((OptionalXboxRuntimeStatus)Volatile.Read(ref _status) is
            OptionalXboxRuntimeStatus.Stopped or OptionalXboxRuntimeStatus.Disposed)
        {
            return;
        }
        Volatile.Write(ref _status, (int)OptionalXboxRuntimeStatus.Stopping);
        _shutdown.Cancel();
        Task? loop;
        lock (_sync) loop = _loop;
        if (loop is not null) await loop.ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) == 0)
            Volatile.Write(ref _status, (int)OptionalXboxRuntimeStatus.Stopped);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempts = 0;
        var consecutiveFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                OptionalXboxRuntimeInstance? instance = null;
                Exception? terminal = null;
                try
                {
                    Volatile.Write(ref _status, (int)OptionalXboxRuntimeStatus.Starting);
                    attempts++;
                    instance = _factory() ?? throw new InvalidOperationException(
                        "The optional Xbox runtime factory returned no instance.");
                    instance.Validate();
                    lock (_sync) _current = instance;
                    await instance.StartAsync(cancellationToken).ConfigureAwait(false);
                    if (attempts > 1) Interlocked.Increment(ref _restartCount);
                    Volatile.Write(ref _status, (int)OptionalXboxRuntimeStatus.Running);
                    _firstAttempt.TrySetResult();
                    await instance.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        terminal = instance.Failure() ?? new InvalidOperationException(
                            "The optional Xbox runtime stopped unexpectedly.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _firstAttempt.TrySetResult();
                }
                catch (Exception exception)
                {
                    terminal = instance?.Failure() ?? exception;
                    _firstAttempt.TrySetResult();
                }
                finally
                {
                    if (instance is not null)
                    {
                        await StopAndDisposeInstanceAsync(instance).ConfigureAwait(false);
                        lock (_sync)
                        {
                            if (ReferenceEquals(_current, instance)) _current = null;
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested) break;
                terminal ??= new InvalidOperationException(
                    "The optional Xbox runtime stopped without terminal evidence.");
                Volatile.Write(ref _lastFailure, terminal);
                ReportFailure(
                    "Xbox DVR routing stopped; existing watched and Capture routing remain active while ClipCord retries.",
                    terminal);
                Volatile.Write(ref _status, (int)OptionalXboxRuntimeStatus.RetryPending);
                consecutiveFailures++;
                try
                {
                    await _delay(
                            RetryDelay(consecutiveFailures), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _firstAttempt.TrySetResult();
            if (Volatile.Read(ref _disposed) == 0)
                Volatile.Write(ref _status, (int)OptionalXboxRuntimeStatus.Stopped);
        }
    }

    private async ValueTask StopAndDisposeInstanceAsync(OptionalXboxRuntimeInstance instance)
    {
        try
        {
            await instance.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportFailure("The optional Xbox runtime could not stop cleanly.", exception);
        }
        try
        {
            await instance.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportFailure("The optional Xbox runtime could not dispose cleanly.", exception);
        }
    }

    private void ReportFailure(string message, Exception exception)
    {
        try { _reportFailure(message, exception); }
        catch { }
    }

    private TimeSpan RetryDelay(int consecutiveFailures)
    {
        if (_initialRetryDelay == TimeSpan.Zero) return TimeSpan.Zero;
        var multiplier = Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        var ticks = Math.Min(
            _initialRetryDelay.Ticks * multiplier,
            _maximumRetryDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        Volatile.Write(ref _status, (int)OptionalXboxRuntimeStatus.Disposed);
    }
}

internal static class RoutingOwnedWorkGraphDisposer
{
    internal static async ValueTask DisposeAsync(
        Func<ValueTask> disposeCapture,
        Func<ValueTask> disposeOptionalXbox,
        Func<ValueTask> disposeWatched,
        Action disposeExecutor,
        Action<Exception>? reportOptionalXboxFailure = null)
    {
        ArgumentNullException.ThrowIfNull(disposeCapture);
        ArgumentNullException.ThrowIfNull(disposeOptionalXbox);
        ArgumentNullException.ThrowIfNull(disposeWatched);
        ArgumentNullException.ThrowIfNull(disposeExecutor);
        var failures = new List<Exception>();
        await AttemptAsync(disposeCapture, failures).ConfigureAwait(false);
        try
        {
            await disposeOptionalXbox().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try { reportOptionalXboxFailure?.Invoke(exception); }
            catch { }
        }
        await AttemptAsync(disposeWatched, failures).ConfigureAwait(false);
        try { disposeExecutor(); }
        catch (Exception exception) { failures.Add(exception); }

        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
        {
            throw new AggregateException(
                "The owned Routing work graph encountered multiple disposal failures.",
                failures);
        }
    }

    private static async ValueTask AttemptAsync(
        Func<ValueTask> dispose,
        ICollection<Exception> failures)
    {
        try { await dispose().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
    }
}

internal static class RoutingExternalSourceStopper
{
    internal static async ValueTask StopAsync(
        Func<ValueTask> stopOptionalXbox,
        Func<ValueTask> stopWatched)
    {
        ArgumentNullException.ThrowIfNull(stopOptionalXbox);
        ArgumentNullException.ThrowIfNull(stopWatched);
        Exception? failure = null;
        try
        {
            await stopOptionalXbox().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            await stopWatched().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        if (failure is not null) throw failure;
    }
}

internal static class RoutingRequiredExternalSourceCompletion
{
    /// <summary>
    /// Both ordinary watched-folder families are required members of the active work graph. The
    /// first unexpected exit must therefore wake the aggregate supervisor, regardless of which
    /// family stopped.
    /// </summary>
    internal static async Task ObserveAsync(Task migratedWatcher, Task namedWatchers)
    {
        ArgumentNullException.ThrowIfNull(migratedWatcher);
        ArgumentNullException.ThrowIfNull(namedWatchers);
        var completed = await Task.WhenAny(migratedWatcher, namedWatchers)
            .ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }
}

/// <summary>
/// The one active Routing work session. It starts Capture reconciliation before watched-source
/// admission, shares one executor across both producers, and borrows the coordinator's current
/// Routing lease without ever disposing, reissuing, or transferring it. Any unexpected host exit
/// quiesces both producers before the terminal failure is exposed through Completion.
/// </summary>
internal sealed class RoutingActiveWorkSession : IAsyncDisposable, IDisposable
{
    private readonly ClipProcessingOwnershipLease _ownership;
    private readonly RoutingRuntimeFeatureGate _featureGate;
    private readonly RoutingRuntimeGateInspection _initialPermit;
    private readonly RoutingActiveWorkSessionOperations _operations;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _supervisor;
    private Exception? _failure;
    private int _state = (int)RoutingActiveWorkSessionState.Created;
    private int _stopRequested;
    private int _resourcesDisposed;
    private int _disposed;

    private RoutingActiveWorkSession(
        ClipProcessingOwnershipLease ownership,
        RoutingRuntimeFeatureGate featureGate,
        RoutingRuntimeGateInspection initialPermit,
        RoutingActiveWorkSessionOperations operations)
    {
        _ownership = ownership;
        _featureGate = featureGate;
        _initialPermit = initialPermit;
        _operations = operations;
        RequireStartupPermit();
    }

    internal RoutingActiveWorkSessionState State =>
        (RoutingActiveWorkSessionState)Volatile.Read(ref _state);
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal Task Completion => _completion.Task;

    /// <summary>
    /// Assembles the production graph. Snapshot, outbox, watched journal, receipt, and connection
    /// state are single shared instances; watched, Capture, and named Xbox source families resolve
    /// and file through one mux, one receipt-guarded Discord provider, and one serialized outbox
    /// executor.
    /// </summary>
    internal static RoutingActiveWorkSession CreateProduction(
        AppSettings watchedSettings,
        Func<AppSettings> currentWatchedSettings,
        CaptureSettings captureSettings,
        Func<CaptureSettings> currentCaptureSettings,
        ClipProcessingOwnershipLease routingOwnership,
        RoutingRuntimeFeatureGate featureGate,
        RoutingActiveWorkSessionStorage? storage = null,
        RoutingActiveWorkSessionOptions? options = null,
        RoutingCaptureLibraryPermit? captureLibraryPermit = null,
        Func<DiscordWebhookClient>? discordClientFactory = null,
        ActivityHistoryStore? activityHistory = null)
    {
        ArgumentNullException.ThrowIfNull(watchedSettings);
        ArgumentNullException.ThrowIfNull(currentWatchedSettings);
        ArgumentNullException.ThrowIfNull(captureSettings);
        ArgumentNullException.ThrowIfNull(currentCaptureSettings);
        ArgumentNullException.ThrowIfNull(routingOwnership);
        ArgumentNullException.ThrowIfNull(featureGate);
        var normalizedStorage = (storage ?? RoutingActiveWorkSessionStorage.Default).Normalize();
        var effectiveOptions = options ?? RoutingActiveWorkSessionOptions.Default;
        effectiveOptions.Validate();
        var configuration = RequireProductionConfiguration(
            watchedSettings,
            captureSettings);
        var normalizedWatchedSettings = configuration.WatchedSettings;
        var normalizedCaptureSettings = configuration.CaptureSettings;
        var watchedRoot = normalizedWatchedSettings.ClipsFolder;
        var captureRoot = normalizedCaptureSettings.LibraryRoot;
        RequireCurrentConfigurations(
            normalizedWatchedSettings,
            normalizedCaptureSettings,
            currentWatchedSettings,
            currentCaptureSettings);
        var permit = RequireInitialPermit(
            routingOwnership,
            featureGate,
            normalizedWatchedSettings.CaptureSource);
        var captureLibraryBinding = RoutingCaptureLibraryBindingModel.Create(captureRoot);
        var effectiveCaptureLibraryPermit = captureLibraryPermit ??
            new RoutingCaptureLibraryPermit(
                captureLibraryBinding,
                () =>
                {
                    RequireCurrentConfigurations(
                        normalizedWatchedSettings,
                        normalizedCaptureSettings,
                        currentWatchedSettings,
                        currentCaptureSettings);
                    return RoutingCaptureLibraryBindingModel.Create(captureRoot);
                });
        if (effectiveCaptureLibraryPermit.ExpectedBinding != captureLibraryBinding)
        {
            throw new InvalidOperationException(
                "The active Routing session Capture permit does not match its configured library.");
        }
        _ = effectiveCaptureLibraryPermit.RequireCurrent(
            "active Routing session construction");

        var snapshots = new RoutingSnapshotStore(normalizedStorage.SnapshotPath);
        var outbox = new RoutingOutboxStore(normalizedStorage.OutboxPath);
        var markers = new LegacyRoutingMigrationMarkerStore(
            normalizedStorage.MigrationMarkerPath);
        RequireStoresMatchPermit(
            snapshots,
            markers,
            permit,
            captureLibraryBinding);
        var journals = new RoutingWatchedSourceJournalStore(
            normalizedStorage.WatchedJournalRoot);
        var receipts = new RoutingDeliveryReceiptStore(
            normalizedStorage.DeliveryReceiptPath);
        var catalog = new DiscordConnectionCatalog(
            new DiscordConnectionCatalogStore(normalizedStorage.DiscordConnectionPath),
            new CurrentUserDiscordWebhookProtector(),
            new DiscordConnectionReferenceProbe(snapshots, outbox));
        var inputSources = new RoutingInputSourceCatalog(
            new RoutingInputSourceCatalogStore(normalizedStorage.InputSourcePath),
            new RoutingInputSourceReferenceProbe(
                snapshots,
                normalizedStorage.RoutingRoot));
        var watchedRootResolver = new RoutingWatchedSourceRootResolver(
            watchedRoot,
            inputSources);
        if (activityHistory is not null)
        {
            var activityProjection = new RoutingActivityProjection(
                new RoutingDeliveryHistoryReader(outbox),
                captureRoot,
                watchedRoot,
                journals,
                watchedRootResolver.ResolveConfigured,
                watchedRootResolver.IsCurrent);
            activityHistory.SetExternalEntriesProvider(() => activityProjection.Read());
        }
        var localOnlyOverride = new RoutingLocalOnlyOverrideState(
            new RoutingLocalOnlyOverrideStore(normalizedStorage.LocalOnlyOverridePath));
        _ = localOnlyOverride.EnsureMigratedAsync(
                normalizedWatchedSettings.UploadToDiscord,
                normalizedWatchedSettings.ModeToggleHotkey)
            .GetAwaiter()
            .GetResult();
        var provider = new RoutingReceiptGuardedDeliveryProvider(
            new DiscordRoutingProvider(
                new DiscordRoutingConnectionResolver(currentWatchedSettings, catalog),
                discordClientFactory ?? (static () => new DiscordWebhookClient())),
            new RoutingDeliveryReceiptGuard(receipts));
        var resolver = new RoutingArtifactResolverMux(
            new CaptureJournalRoutingArtifactResolver(captureRoot),
            new WatchedFolderRoutingArtifactResolver(watchedRootResolver.Resolve, journals));
        var filer = new RoutingLibraryFilerMux(
            new CaptureJournalRoutingLibraryFiler(captureRoot),
            new WatchedFolderRoutingLibraryFiler(watchedRootResolver.Resolve, journals));
        bool CanExecute()
        {
            try
            {
                return routingOwnership.IsCurrent &&
                       routingOwnership.Owner == ClipProcessingRuntimeOwner.Routing &&
                       permit.SameAuthority(featureGate.Inspect()) &&
                        SameCurrentConfigurations(
                            normalizedWatchedSettings,
                            normalizedCaptureSettings,
                            currentWatchedSettings,
                            currentCaptureSettings) &&
                        effectiveCaptureLibraryPermit.Inspect().Allowed;
            }
            catch
            {
                return false;
            }
        }
        bool CanExecuteXbox()
        {
            if (!CanExecute()) return false;
            try
            {
                var sourceSnapshot = inputSources.Inspect();
                if (!sourceSnapshot.IsUsable) return true;
                XboxDvrRuntimeAuthorityValidator.RequireDisjointOwnedAndWatchedRoots(
                    captureRoot,
                    sourceSnapshot.Sources,
                    [
                        watchedRoot,
                        .. sourceSnapshot.Sources
                            .Where(source => source.Enabled && !source.Retired &&
                                source.Kind is RoutingInputSourceKind.SteelSeriesGg or
                                    RoutingInputSourceKind.Nvidia)
                            .Select(source => source.CanonicalRoot)
                    ]);
                return true;
            }
            catch (InvalidDataException)
            {
                // An overlapping source must never reach the content-opening boundary. The
                // optional Xbox host degrades independently; ordinary watched/Capture routing
                // remains authorized by CanExecute above.
                return false;
            }
            catch
            {
                // Catalog availability is optional-source state. PollOnce owns its retry and
                // diagnostics; it must not revoke authority from the shared Routing executor.
                return true;
            }
        }
        var executor = new RoutingOutboxExecutor(
            outbox,
            provider,
            resolver,
            filer,
            CanExecute,
            captureLibraryPermit: effectiveCaptureLibraryPermit,
            durableStateChanged: activityHistory is null
                ? null
                : activityHistory.QueueExternalRefresh);
        RoutingWatchedFolderRuntimeHost? watchedHost = null;
        RoutingNamedWatchedFolderRuntimeHost? namedWatchedHost = null;
        CaptureJournalRoutingPump? capturePump = null;
        OptionalXboxRuntimeSupervisor? xboxSupervisor = null;
        try
        {
            var ingress = new RoutingWatchedFolderIngress(
                snapshots,
                markers,
                journals,
                new RoutingWatchedJournalFactory(
                    new FfmpegRoutingWatchedFolderMediaProbe(),
                    captureLocalOnlyOverride: () =>
                        localOnlyOverride.CaptureAdmissionSnapshot()),
                new RoutingPlanCommitter(
                    outbox,
                    featureGate,
                    effectiveCaptureLibraryPermit),
                featureGate);
            var adapter = RoutingWatchedSourceAdapters.Get(
                normalizedWatchedSettings.CaptureSource);
            watchedHost = RoutingWatchedFolderRuntimeHost.Create(
                normalizedWatchedSettings,
                currentWatchedSettings,
                routingOwnership,
                adapter,
                borrowed =>
                {
                    if (!ReferenceEquals(borrowed, routingOwnership))
                    {
                        throw new InvalidOperationException(
                            "The watched host did not borrow the aggregate session lease.");
                    }
                    return new RoutingWatchedFolderRuntimeDependencies(
                        featureGate,
                        ingress,
                        executor,
                        HostOwnsExecutor: false);
                },
                effectiveOptions.WatchedHost);
            var namedIngress = new RoutingNamedWatchedFolderIngress(
                snapshots,
                journals,
                new RoutingWatchedJournalFactory(
                    new FfmpegRoutingWatchedFolderMediaProbe(),
                    captureLocalOnlyOverride: () =>
                        localOnlyOverride.CaptureAdmissionSnapshot()),
                new RoutingNamedWatchedBaselineStore(Path.Combine(
                    normalizedStorage.RoutingRoot,
                    "named-watched-baselines",
                    "v1")),
                inputSources,
                new RoutingPlanCommitter(
                    outbox,
                    featureGate,
                    effectiveCaptureLibraryPermit),
                featureGate);
            namedWatchedHost = new RoutingNamedWatchedFolderRuntimeHost(
                watchedRoot,
                captureRoot,
                routingOwnership,
                featureGate,
                inputSources,
                namedIngress,
                executor,
                effectiveOptions.WatchedHost);
            var bridge = new RoutingRuntimeBridge(
                captureRoot,
                snapshots,
                outbox,
                new RoutingEvaluatorRuntimePlanner(),
                featureGate,
                captureLibraryPermit: effectiveCaptureLibraryPermit);
            capturePump = new CaptureJournalRoutingPump(
                captureRoot,
                bridge,
                featureGate,
                executor.RunOnceAsync,
                effectiveOptions.CapturePump,
                captureLibraryPermit: effectiveCaptureLibraryPermit);
            OptionalXboxRuntimeInstance CreateXboxInstance()
            {
                var host = new XboxDvrRuntimeHost(
                    captureRoot,
                    new XboxDvrProductionRuntimeBackend(
                        normalizedStorage.RoutingRoot,
                        captureRoot,
                        inputSources,
                        snapshots,
                        CanExecuteXbox,
                        maximumCandidatesPerSource:
                            effectiveOptions.EffectiveXboxHost.MaximumCandidatesPerSource,
                        captureLocalOnlyOverride: () =>
                            localOnlyOverride.CaptureAdmissionSnapshot()),
                    effectiveOptions.EffectiveXboxHost);
                return OptionalXboxRuntimeInstance.From(host);
            }
            xboxSupervisor = new OptionalXboxRuntimeSupervisor(
                CreateXboxInstance,
                effectiveOptions.EffectiveXboxHost.ErrorRetryInterval,
                effectiveOptions.EffectiveXboxHost.MaximumErrorRetryInterval,
                reportFailure: static (message, exception) => Log.Error(message, exception));
            var ownedCapture = capturePump;
            var ownedWatched = watchedHost;
            var ownedNamedWatched = namedWatchedHost;
            var ownedXbox = xboxSupervisor;

            async ValueTask StartExternalSourcesAsync(CancellationToken cancellationToken)
            {
                await ownedWatched.StartAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ownedNamedWatched.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await ownedNamedWatched.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await ownedWatched.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception)
                {
                    Exception? cleanupFailure = null;
                    try
                    {
                        await ownedNamedWatched.StopAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception stopException)
                    {
                        cleanupFailure = stopException;
                    }
                    try
                    {
                        await ownedWatched.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception stopException)
                    {
                        cleanupFailure = cleanupFailure is null
                            ? stopException
                            : new AggregateException(cleanupFailure, stopException);
                    }
                    if (cleanupFailure is not null)
                        throw new AggregateException(exception, cleanupFailure);
                    throw;
                }
                try
                {
                    await ownedXbox.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await ownedXbox.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await ownedNamedWatched.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await ownedWatched.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception)
                {
                    if (!CanExecute())
                    {
                        await ownedWatched.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                    Log.Error(
                        "Xbox DVR routing could not start; existing watched and Capture routing remain active.",
                        exception);
                }
            }

            async ValueTask StopExternalSourcesAsync(CancellationToken cancellationToken)
            {
                _ = cancellationToken;
                async ValueTask StopWatchedSourcesAsync()
                {
                    Exception? failure = null;
                    try
                    {
                        await ownedNamedWatched.StopAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    try
                    {
                        await ownedWatched.StopAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        failure = failure is null
                            ? exception
                            : new AggregateException(failure, exception);
                    }
                    if (failure is not null) throw failure;
                }
                await RoutingExternalSourceStopper.StopAsync(
                        () => ownedXbox.StopAsync(CancellationToken.None),
                        StopWatchedSourcesAsync)
                    .ConfigureAwait(false);
            }
            async Task ExternalSourceCompletion()
            {
                await RoutingRequiredExternalSourceCompletion.ObserveAsync(
                        ownedWatched.Completion,
                        ownedNamedWatched.Completion)
                    .ConfigureAwait(false);
            }

            Exception? ExternalSourceFailure() =>
                ownedWatched.Failure ?? ownedNamedWatched.Failure;

            var operations = new RoutingActiveWorkSessionOperations(
                cancellationToken => new ValueTask(
                    ownedCapture.StartAsync(cancellationToken)),
                ownedCapture.StopAsync,
                () => ownedCapture.Completion,
                () => ownedCapture.Failure,
                StartExternalSourcesAsync,
                StopExternalSourcesAsync,
                ExternalSourceCompletion,
                ExternalSourceFailure,
                () => RoutingOwnedWorkGraphDisposer.DisposeAsync(
                    ownedCapture.DisposeAsync,
                    ownedXbox.DisposeAsync,
                    () => RoutingOwnedWorkGraphDisposer.DisposeAsync(
                        ownedNamedWatched.DisposeAsync,
                        static () => ValueTask.CompletedTask,
                        ownedWatched.DisposeAsync,
                        static () => { }),
                    executor.Dispose,
                    static exception => Log.Error(
                        "The optional Xbox runtime could not dispose cleanly; remaining Routing cleanup continued.",
                        exception)));
            return CreateForTesting(
                routingOwnership,
                featureGate,
                operations,
                expectedPermit: permit);
        }
        catch
        {
            if (capturePump is not null)
            {
                capturePump.Dispose();
            }
            if (watchedHost is not null)
            {
                watchedHost.Dispose();
            }
            if (namedWatchedHost is not null)
            {
                namedWatchedHost.Dispose();
            }
            if (xboxSupervisor is not null)
            {
                xboxSupervisor.Dispose();
            }
            executor.Dispose();
            throw;
        }
    }

    internal static RoutingActiveWorkSession CreateForTesting(
        ClipProcessingOwnershipLease routingOwnership,
        RoutingRuntimeFeatureGate featureGate,
        RoutingActiveWorkSessionOperations operations,
        RoutingRuntimeGateInspection? expectedPermit = null)
    {
        ArgumentNullException.ThrowIfNull(routingOwnership);
        ArgumentNullException.ThrowIfNull(featureGate);
        ArgumentNullException.ThrowIfNull(operations);
        operations.Validate();
        var permit = expectedPermit ?? RequireInitialPermit(
            routingOwnership,
            featureGate,
            featureGate.Inspect().RequiredLegacySource ??
            throw new InvalidOperationException(
                "The Routing feature gate has no required source."));
        return new RoutingActiveWorkSession(
            routingOwnership,
            featureGate,
            permit,
            operations);
    }

    /// <summary>
    /// Returns only after both complete startup replays: Capture first, then watched journals.
    /// This ordering prevents a new external scan from racing preexisting Capture recovery.
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RoutingActiveWorkSessionState.Running) return;
            if (State != RoutingActiveWorkSessionState.Created)
            {
                throw new InvalidOperationException(
                    "A stopped active Routing work session cannot be restarted.");
            }
            SetState(RoutingActiveWorkSessionState.Starting);
            try
            {
                RequireStartupPermit();
                await _operations.StartCaptureAsync(cancellationToken).ConfigureAwait(false);
                RequireStartupPermit();
                await _operations.StartWatchedAsync(cancellationToken).ConfigureAwait(false);
                RequireStartupPermit();
                SetState(RoutingActiveWorkSessionState.Running);
                _supervisor = SuperviseAsync();
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _stopRequested, 1);
                var terminal = await QuiesceCoreAsync(exception).ConfigureAwait(false) ??
                               exception;
                SetFailure(terminal);
                if (exception is OperationCanceledException &&
                    cancellationToken.IsCancellationRequested)
                {
                    _completion.TrySetCanceled(cancellationToken);
                }
                else
                {
                    _completion.TrySetException(terminal);
                }
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stop is intentionally non-cancellable once entered: both producers are quiesced before the
    /// shared executor is disposed. The borrowed Routing lease is never part of owned cleanup.
    /// </summary>
    internal async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Interlocked.Exchange(ref _stopRequested, 1);
        Task? supervisor;
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            supervisor = _supervisor;
            if (State is RoutingActiveWorkSessionState.Stopped or
                RoutingActiveWorkSessionState.Failed)
            {
                return;
            }
            SetState(RoutingActiveWorkSessionState.Stopping);
            var terminal = await QuiesceCoreAsync(priorFailure: null).ConfigureAwait(false);
            if (terminal is not null)
            {
                SetFailure(terminal);
                _completion.TrySetException(terminal);
                throw terminal;
            }
            SetState(RoutingActiveWorkSessionState.Stopped);
            _completion.TrySetResult();
        }
        finally
        {
            _lifecycle.Release();
        }
        if (supervisor is not null)
        {
            await supervisor.ConfigureAwait(false);
        }
    }

    private async Task SuperviseAsync()
    {
        var capture = ObserveHostAsync(
            "Capture journal Routing pump",
            _operations.CaptureCompletion,
            _operations.CaptureFailure);
        var watched = ObserveHostAsync(
            "watched-folder Routing host",
            _operations.WatchedCompletion,
            _operations.WatchedFailure);
        var failure = await await Task.WhenAny(capture, watched).ConfigureAwait(false);
        if (Volatile.Read(ref _stopRequested) != 0) return;

        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
            SetState(RoutingActiveWorkSessionState.Stopping);
            var terminal = await QuiesceCoreAsync(failure).ConfigureAwait(false) ?? failure;
            SetFailure(terminal);
            _completion.TrySetException(terminal);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static async Task<Exception> ObserveHostAsync(
        string name,
        Func<Task> completion,
        Func<Exception?> failure)
    {
        try
        {
            var task = completion() ?? throw new InvalidDataException(
                $"The {name} completion task is missing.");
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return failure() ?? exception;
        }
        return failure() ?? new InvalidOperationException(
            $"The {name} stopped unexpectedly.");
    }

    private async Task<Exception?> QuiesceCoreAsync(Exception? priorFailure)
    {
        var failures = new List<Exception>();
        if (priorFailure is not null) failures.Add(priorFailure);
        await AttemptAsync(
            () => _operations.StopCaptureAsync(CancellationToken.None),
            failures).ConfigureAwait(false);
        await AttemptAsync(
            () => _operations.StopWatchedAsync(CancellationToken.None),
            failures).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            await AttemptAsync(_operations.DisposeOwnedAsync, failures)
                .ConfigureAwait(false);
        }
        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(
                "The active Routing work session encountered multiple terminal failures.",
                failures)
        };
    }

    private static async Task AttemptAsync(
        Func<ValueTask> operation,
        ICollection<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private void RequireStartupPermit()
    {
        if (!_ownership.IsCurrent ||
            _ownership.Owner != ClipProcessingRuntimeOwner.Routing ||
            !_initialPermit.SamePermit(_featureGate.Inspect()))
        {
            throw new InvalidOperationException(
                "The active Routing work session lost its exact borrowed authority.");
        }
    }

    /// <summary>
    /// Validates the source and filesystem layout required by the production watched and Capture
    /// hosts. First activation runs this while Legacy is recoverable; construction repeats it
    /// after sticky authority so a changed filesystem always fails closed.
    /// </summary>
    internal static (AppSettings WatchedSettings, CaptureSettings CaptureSettings)
        RequireProductionConfiguration(
            AppSettings watchedSettings,
            CaptureSettings captureSettings)
    {
        ArgumentNullException.ThrowIfNull(watchedSettings);
        ArgumentNullException.ThrowIfNull(captureSettings);
        var watchedRoot = RequireWatchedConfigurationRoot(watchedSettings);
        var captureRoot = RequireCaptureConfigurationRoot(captureSettings);
        if (CapturePathPolicy.PathsOverlap(watchedRoot, captureRoot))
        {
            throw new InvalidOperationException(
                "The watched-source root and ClipCord Capture library must not overlap.");
        }

        _ = RoutingWatchedSourceAdapters.Get(watchedSettings.CaptureSource);
        var ordinaryWatchedRoot = RoutingWatchedFileSystem.OpenOrdinaryRoot(watchedRoot);
        ordinaryWatchedRoot.Handle.Dispose();
        CaptureJournalStore.EnsureOrdinaryExistingPath(
            captureRoot,
            captureRoot,
            requireDirectory: true,
            "ClipCord Capture library");
        return (
            watchedSettings with { ClipsFolder = watchedRoot },
            captureSettings with { LibraryRoot = captureRoot });
    }

    private static RoutingRuntimeGateInspection RequireInitialPermit(
        ClipProcessingOwnershipLease ownership,
        RoutingRuntimeFeatureGate featureGate,
        ClipCaptureSource expectedSource)
    {
        if (!ownership.IsCurrent || ownership.Owner != ClipProcessingRuntimeOwner.Routing)
        {
            throw new InvalidOperationException(
                "The active Routing work session requires a current borrowed Routing lease.");
        }
        var permit = featureGate.Inspect();
        if (!permit.Enabled || !permit.HasRequiredSourceCoverage ||
            permit.RequiredLegacySource != expectedSource ||
            permit.OwnershipEpoch != ownership.Epoch ||
            permit.RoutingGeneration is null or <= 0 ||
            permit.MarkerPayloadFingerprint is not { Length: 64 } ||
            permit.ExecutionAuthorityActivationId is null)
        {
            throw new InvalidOperationException(
                "The active Routing work session requires one complete exact feature-gate permit.");
        }
        return permit;
    }

    private static void RequireStoresMatchPermit(
        RoutingSnapshotStore snapshots,
        LegacyRoutingMigrationMarkerStore markers,
        RoutingRuntimeGateInspection permit,
        RoutingCaptureLibraryBinding captureLibraryBinding)
    {
        var snapshot = snapshots.Load();
        var marker = markers.Load();
        if (!snapshot.LoadedFromDisk || snapshot.Document is null ||
            snapshot.Document.Generation != permit.RoutingGeneration ||
            !marker.LoadedFromDisk || marker.Document is null ||
            marker.Document.Phase != LegacyRoutingMigrationMarkerPhase.Committed ||
            marker.Document.CaptureLibraryBinding != captureLibraryBinding ||
            !marker.Document.PayloadFingerprint.Equals(
                permit.MarkerPayloadFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The active Routing stores do not match the supplied feature-gate permit.");
        }
    }

    private static string RequireWatchedConfigurationRoot(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.CaptureSource) ||
            !Path.IsPathFullyQualified(settings.ClipsFolder))
        {
            throw new InvalidOperationException(
                "The active Routing watched configuration is invalid.");
        }
        var canonical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(settings.ClipsFolder));
        if (!Directory.Exists(canonical))
        {
            throw new InvalidOperationException(
                "The active Routing watched root must exist.");
        }
        return canonical;
    }

    private static string RequireCaptureConfigurationRoot(CaptureSettings settings)
    {
        if (!Path.IsPathFullyQualified(settings.LibraryRoot))
        {
            throw new InvalidOperationException(
                "The active Routing Capture library root must be fully qualified.");
        }
        var canonical = CaptureJournalStore.NormalizeLibraryRoot(settings.LibraryRoot);
        return canonical;
    }

    private static void RequireCurrentConfigurations(
        AppSettings expectedWatched,
        CaptureSettings expectedCapture,
        Func<AppSettings> currentWatchedSettings,
        Func<CaptureSettings> currentCaptureSettings)
    {
        if (!SameCurrentConfigurations(
                expectedWatched,
                expectedCapture,
                currentWatchedSettings,
                currentCaptureSettings))
        {
            throw new InvalidOperationException(
                "The active Routing source configuration changed before construction.");
        }
    }

    private static bool SameCurrentConfigurations(
        AppSettings expectedWatched,
        CaptureSettings expectedCapture,
        Func<AppSettings> currentWatchedSettings,
        Func<CaptureSettings> currentCaptureSettings)
    {
        var watched = currentWatchedSettings();
        var capture = currentCaptureSettings();
        if (watched is null || capture is null ||
            !Enum.IsDefined(watched.CaptureSource) ||
            watched.CaptureSource != expectedWatched.CaptureSource)
        {
            return false;
        }
        try
        {
            if (!Path.IsPathFullyQualified(watched.ClipsFolder) ||
                !Path.IsPathFullyQualified(capture.LibraryRoot))
            {
                return false;
            }
            var watchedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(watched.ClipsFolder));
            var captureRoot = CaptureJournalStore.NormalizeLibraryRoot(capture.LibraryRoot);
            return watchedRoot.Equals(
                       expectedWatched.ClipsFolder, StringComparison.OrdinalIgnoreCase) &&
                   captureRoot.Equals(
                       expectedCapture.LibraryRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void SetFailure(Exception failure)
    {
        Interlocked.CompareExchange(ref _failure, failure, null);
        SetState(RoutingActiveWorkSessionState.Failed);
    }

    private void SetState(RoutingActiveWorkSessionState state) =>
        Volatile.Write(ref _state, (int)state);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            StopAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _lifecycle.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Dispose();
        }
    }
}
