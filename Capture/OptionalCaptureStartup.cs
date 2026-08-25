namespace ClipsToDiscord;

internal static class ReactionCameraStartPolicy
{
    internal static bool CanStart(bool requested, bool releaseNeedsAttention) =>
        CanStart(requested, releaseNeedsAttention, suppressedForCapture: false);

    internal static bool CanStart(
        bool requested,
        bool releaseNeedsAttention,
        bool suppressedForCapture) =>
        requested && !releaseNeedsAttention && !suppressedForCapture;
}

/// <summary>
/// Owns the lifetime of an optional capture input while it opens in the background. The primary
/// gameplay pipeline must never wait for an optional device, but a privacy off request must still
/// be able to cancel and join that pending open before it reports success.
/// </summary>
internal sealed class OptionalCaptureStartup : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private bool _starting;
    private bool _disposed;

    internal bool IsStarting
    {
        get { lock (_gate) return _starting; }
    }

    internal void Start(
        Func<CancellationToken, Task> operation,
        Action<Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_starting)
            {
                throw new InvalidOperationException("The optional capture input is already starting.");
            }

            cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _starting = true;
            var task = RunAsync(operation, onFailure, cancellation);
            if (ReferenceEquals(_cancellation, cancellation)) _task = task;
        }
    }

    internal void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate) cancellation = _cancellation;
        TryCancel(cancellation);
    }

    internal void Abandon()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _task = null;
            _starting = false;
        }
        TryCancel(cancellation);
    }

    internal async Task CancelAndWaitAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            task = _task;
        }
        TryCancel(cancellation);
        if (task is not null)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> operation,
        Action<Exception>? onFailure,
        CancellationTokenSource cancellation)
    {
        // Ensure Start can publish the task before an operation that completes synchronously runs
        // its cleanup path.
        await Task.Yield();
        try
        {
            await operation(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            try { onFailure?.Invoke(exception); }
            catch (Exception callbackException)
            {
                Log.Error("ClipCord could not report an optional capture startup failure.", callbackException);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                    _task = null;
                    _starting = false;
                }
            }
            cancellation.Dispose();
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Cancel();
    }
}

/// <summary>
/// Publishes one optional-input stop operation before running it, so every teardown path joins
/// the same device owner instead of starting a second operation that can falsely report release.
/// </summary>
internal sealed class OptionalCaptureStopSingleFlight<T>
{
    private readonly object _gate = new();
    private Task<T>? _current;

    internal bool TryGetCurrent(out Task<T>? task)
    {
        lock (_gate)
        {
            task = _current;
            return task is not null;
        }
    }

    internal Task<T> GetOrStart(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        TaskCompletionSource<T> completion;
        lock (_gate)
        {
            if (_current is not null) return _current;
            completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _current = completion.Task;
        }

        _ = CompleteAsync(completion, operation);
        return completion.Task;
    }

    internal bool IsCurrent(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate) return ReferenceEquals(_current, task);
    }

    internal bool Clear(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, task)) return false;
            _current = null;
            return true;
        }
    }

    internal bool ClearCompleted()
    {
        lock (_gate)
        {
            if (_current?.IsCompleted != true) return false;
            _current = null;
            return true;
        }
    }

    private static async Task CompleteAsync(
        TaskCompletionSource<T> completion,
        Func<Task<T>> operation)
    {
        try
        {
            completion.TrySetResult(await operation().ConfigureAwait(false));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}

internal static class OptionalCaptureShutdownPolicy
{
    // This remains below the isolated-host command timeout. Optional camera teardown can report a
    // warning, but it must not sacrifice an otherwise healthy gameplay recording or replay buffer.
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(4);

    internal static async Task<bool> CompletesWithinAsync(
        Task task,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

internal static class ReactionCameraPersistencePolicy
{
    // Camera files can be hundreds of megabytes on slow disks or synced folders. This budget is
    // intentionally much larger than device release, while remaining comfortably below the
    // capture-host's two-minute save-command timeout.
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan HostCommandLimit = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan HostSafetyMargin = TimeSpan.FromSeconds(10);

    internal static TimeSpan GetRemainingBudget(TimeSpan commandElapsed)
    {
        if (commandElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandElapsed));
        }
        var remaining = HostCommandLimit - HostSafetyMargin - commandElapsed;
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return remaining < MaximumWait ? remaining : MaximumWait;
    }
}

internal static class ReactionCameraPersistenceGate
{
    private static int _pending;

    internal static bool IsPending => Volatile.Read(ref _pending) != 0;

    internal static bool TryAcquire(out IDisposable? lease)
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
        {
            lease = null;
            return false;
        }
        lease = new Lease();
        return true;
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref _pending, 0);
            }
        }
    }
}
