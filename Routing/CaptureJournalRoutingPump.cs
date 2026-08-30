namespace ClipsToDiscord;

internal sealed record CaptureJournalRoutingPumpOptions(
    TimeSpan PollInterval,
    int MaximumEntriesPerPage,
    TimeSpan MaximumPageDuration,
    int MaximumPagesPerPass)
{
    internal static CaptureJournalRoutingPumpOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        CaptureJournalStartupReconciler.DefaultMaximumEntries,
        CaptureJournalStartupReconciler.DefaultMaximumDuration,
        MaximumPagesPerPass: 10_000);

    internal void Validate()
    {
        if (PollInterval < TimeSpan.Zero || PollInterval > TimeSpan.FromMinutes(5) ||
            MaximumEntriesPerPage is < 1 or > 10_000 ||
            MaximumPageDuration <= TimeSpan.Zero ||
            MaximumPageDuration > TimeSpan.FromMinutes(1) ||
            MaximumPagesPerPass is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CaptureJournalRoutingPumpOptions),
                "The Capture journal Routing pump bounds are invalid.");
        }
    }
}

internal sealed class CaptureJournalRoutingPumpAuthorityException(string message) :
    InvalidOperationException(message);

/// <summary>
/// Replays Capture journals into the durability-only Routing bridge and then invokes the injected
/// outbox runner. It borrows authority from the feature gate; it owns neither the Routing lease,
/// bridge, gate, nor executor. Every poll scans from the beginning so a later generation of an
/// existing journal can settle pending renditions without creating a second plan.
/// </summary>
internal sealed class CaptureJournalRoutingPump : IAsyncDisposable, IDisposable
{
    private const int MaximumGenerationRestartsPerPass = 8;

    private readonly string _libraryRoot;
    private readonly RoutingRuntimeBridge _bridge;
    private readonly RoutingRuntimeFeatureGate _featureGate;
    private readonly RoutingCaptureLibraryPermit _captureLibraryPermit;
    private readonly Func<CancellationToken, Task<RoutingExecutorRunResult>> _runExecutorAsync;
    private readonly CaptureJournalRoutingPumpOptions _options;
    private readonly Func<string?, CancellationToken,
        Task<CaptureJournalReconciliationSummary>> _pollPageAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycle = new();
    private Task? _loop;
    private Exception? _failure;
    private int _disposed;

    internal CaptureJournalRoutingPump(
        string libraryRoot,
        RoutingRuntimeBridge bridge,
        RoutingRuntimeFeatureGate featureGate,
        Func<CancellationToken, Task<RoutingExecutorRunResult>> runExecutorAsync,
        CaptureJournalRoutingPumpOptions? options = null,
        Func<string?, CancellationToken,
            Task<CaptureJournalReconciliationSummary>>? pollPageAsync = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        RoutingCaptureLibraryPermit? captureLibraryPermit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _libraryRoot = Path.GetFullPath(libraryRoot);
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        if (!ReferenceEquals(_bridge.FeatureGate, _featureGate))
        {
            throw new ArgumentException(
                "The Capture journal pump and Routing bridge must share one feature gate.",
                nameof(featureGate));
        }
        _captureLibraryPermit = captureLibraryPermit ?? _bridge.CaptureLibraryPermit;
        if (!ReferenceEquals(_bridge.CaptureLibraryPermit, _captureLibraryPermit))
        {
            throw new ArgumentException(
                "The Capture journal pump and Routing bridge must share one Capture-library permit.",
                nameof(captureLibraryPermit));
        }
        _runExecutorAsync = runExecutorAsync ??
                            throw new ArgumentNullException(nameof(runExecutorAsync));
        _options = options ?? CaptureJournalRoutingPumpOptions.Default;
        _options.Validate();
        _pollPageAsync = pollPageAsync ?? PollPageAsync;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    internal Exception? Failure => Volatile.Read(ref _failure);

    internal Task Completion
    {
        get
        {
            lock (_lifecycle) return _loop ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Returns only after every bounded startup page has been reconciled and its planned work has
    /// been offered to the outbox executor under one unchanged activation permit.
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Task loop;
        lock (_lifecycle)
        {
            if (_shutdown.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "A stopped Capture journal Routing pump cannot be restarted.");
            }
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
        if (loop is not null) await loop.ConfigureAwait(false);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var authority = RequireAuthority();
            RequireCapturePermit("Capture journal pump startup");
            await ReconcileStablePassAsync(authority, cancellationToken).ConfigureAwait(false);
            _started.TrySetResult();

            while (!cancellationToken.IsCancellationRequested)
            {
                await _delayAsync(_options.PollInterval, cancellationToken)
                    .ConfigureAwait(false);
                await ReconcileStablePassAsync(authority, cancellationToken)
                    .ConfigureAwait(false);
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
            Log.Error("Capture journal Routing pump stopped fail-closed.", exception);
        }
        finally
        {
            if (!_started.Task.IsCompleted)
            {
                _started.TrySetException(new InvalidOperationException(
                    "The Capture journal Routing pump stopped before startup completed."));
            }
        }
    }

    private async Task ReconcileStablePassAsync(
        RoutingRuntimeGateInspection authority,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumGenerationRestartsPerPass; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var passPermit = RequireAuthority();
            RequireSameAuthority(authority, passPermit);
            try
            {
                await ReconcileAllPagesAsync(passPermit, cancellationToken)
                    .ConfigureAwait(false);
                RequireSamePermit(passPermit);
                return;
            }
            catch (CaptureJournalRoutingPumpGenerationChangedException)
            {
                // A route edit invalidates only this frozen pass. Restart from the durable
                // beginning so every new plan in the successful pass uses one generation.
                if (attempt == MaximumGenerationRestartsPerPass - 1) break;
            }
        }

        throw new InvalidDataException(
            "Capture journal reconciliation could not obtain a stable Routing generation.");
    }

    private async Task ReconcileAllPagesAsync(
        RoutingRuntimeGateInspection permit,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < _options.MaximumPagesPerPass; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSamePermit(permit);
            RequireCapturePermit("Capture journal polling");
            var page = await _pollPageAsync(cursor, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(page);
            RequireSamePermit(permit);
            RequireCapturePermit("Capture journal outbox execution");

            var execution = await _runExecutorAsync(cancellationToken).ConfigureAwait(false);
            if (!execution.Enabled)
            {
                throw LostAuthority(
                    "Routing authority was revoked before planned Capture work could execute.");
            }
            RequireSamePermit(permit);

            if (!page.EntryLimitReached) return;
            if (page.NextCursor is null ||
                (cursor is not null &&
                 string.CompareOrdinal(page.NextCursor, cursor) <= 0))
            {
                throw new InvalidDataException(
                    "Capture journal paging did not advance its durable cursor.");
            }
            cursor = page.NextCursor;
        }

        throw new InvalidDataException(
            "Capture journal reconciliation exceeded its bounded page limit.");
    }

    private Task<CaptureJournalReconciliationSummary> PollPageAsync(
        string? afterClipId,
        CancellationToken cancellationToken) =>
        CaptureJournalStartupReconciler.ReconcileAsync(
            _libraryRoot,
            _bridge,
            cancellationToken,
            _options.MaximumEntriesPerPage,
            _options.MaximumPageDuration,
            afterClipId);

    private RoutingRuntimeGateInspection RequireAuthority()
    {
        var permit = _featureGate.Inspect();
        if (!permit.Enabled || permit.ExecutionAuthorityActivationId is null ||
            permit.OwnershipEpoch is null || permit.RoutingGeneration is null or <= 0 ||
            permit.MarkerPayloadFingerprint is not { Length: 64 })
        {
            throw LostAuthority(
                "The Capture journal pump does not hold complete Routing authority.");
        }
        return permit;
    }

    private void RequireCapturePermit(string operation)
    {
        try
        {
            _ = _captureLibraryPermit.RequireCurrent(operation);
        }
        catch (RoutingCaptureLibraryPermitException exception)
        {
            throw new CaptureJournalRoutingPumpAuthorityException(
                $"The Capture-library permit was revoked ({exception.Inspection.State}).");
        }
    }

    private void RequireSamePermit(RoutingRuntimeGateInspection permit)
    {
        RoutingRuntimeGateInspection current;
        try
        {
            current = _featureGate.Inspect();
        }
        catch (Exception exception)
        {
            throw new CaptureJournalRoutingPumpAuthorityException(
                $"The Capture journal Routing permit cannot be inspected: {exception.Message}");
        }
        if (permit.SamePermit(current)) return;
        if (permit.SameAuthority(current))
        {
            throw new CaptureJournalRoutingPumpGenerationChangedException();
        }
        throw LostAuthority("The Capture journal Routing authority changed.");
    }

    private static void RequireSameAuthority(
        RoutingRuntimeGateInspection authority,
        RoutingRuntimeGateInspection passPermit)
    {
        if (!authority.SameAuthority(passPermit))
        {
            throw LostAuthority(
                "The Capture journal Routing authority or ownership lease changed.");
        }
    }

    private static CaptureJournalRoutingPumpAuthorityException LostAuthority(string message) =>
        new(message);

    private sealed class CaptureJournalRoutingPumpGenerationChangedException() :
        InvalidOperationException("The Capture journal Routing generation changed.");

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
