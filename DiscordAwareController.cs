namespace ClipsToDiscord;

internal sealed class DiscordAwareController : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<bool> _isDiscordRunning;
    private readonly Func<AppSettings, Action<string>, CancellationToken, Task> _runWatcher;
    private readonly DiscordControllerOptions _options;
    private readonly Task _loop;
    private int _disposeStarted;

    public DiscordAwareController(AppSettings settings, Action<string> reportStatus)
        : this(
            settings,
            reportStatus,
            DiscordDetector.IsRunning,
            static (workerSettings, status, cancellationToken) =>
                new UploaderWorker(workerSettings, status).RunAsync(cancellationToken),
            DiscordControllerOptions.Default)
    {
    }

    internal DiscordAwareController(
        AppSettings settings,
        Action<string> reportStatus,
        Func<bool> isDiscordRunning,
        Func<AppSettings, Action<string>, CancellationToken, Task> runWatcher,
        DiscordControllerOptions options)
    {
        if (options.AbsentPollThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The absence threshold must be positive.");
        }

        _isDiscordRunning = isDiscordRunning;
        _runWatcher = runWatcher;
        _options = options;
        _loop = Task.Run(() => RunAsync(settings, reportStatus));
    }

    private async Task RunAsync(AppSettings currentSettings, Action<string> status)
    {
        var cancellationToken = _shutdown.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_isDiscordRunning())
                {
                    status("Discord closed — uploader paused");
                    await Task.Delay(_options.ClosedPollInterval, cancellationToken);
                    continue;
                }

                try
                {
                    await RunWatcherSessionAsync(currentSettings, status, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Log.Error("Clip watcher stopped unexpectedly and will be restarted.", exception);
                    status("Watcher error — restarting shortly");
                    await Task.Delay(_options.ErrorRetryInterval, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal controller shutdown while waiting for Discord or a retry.
        }
    }

    private async Task RunWatcherSessionAsync(
        AppSettings currentSettings,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        using var watcherCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcherTask = _runWatcher(currentSettings, status, watcherCancellation.Token);
        var discordClosed = false;

        try
        {
            var consecutiveAbsentPolls = 0;
            while (!watcherTask.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.RunningPollInterval, cancellationToken);
                if (_isDiscordRunning())
                {
                    consecutiveAbsentPolls = 0;
                    continue;
                }

                consecutiveAbsentPolls++;
                if (consecutiveAbsentPolls >= _options.AbsentPollThreshold)
                {
                    // Capture why the monitoring loop exited. Do not re-poll here: Discord
                    // may relaunch between this decision and watcher cancellation.
                    discordClosed = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The finally block still cancels and observes the watcher before disposing its CTS.
        }
        finally
        {
            if (discordClosed)
            {
                status("Discord closed — stopping clip watcher");
            }

            watcherCancellation.Cancel();
            try
            {
                await watcherTask;
            }
            catch (OperationCanceledException) when (watcherCancellation.IsCancellationRequested)
            {
                // Expected after Discord closes or the application exits.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        _shutdown.Cancel();
        var completed = false;
        try
        {
            completed = _loop.Wait(_options.DisposeWaitTimeout);
        }
        catch (AggregateException)
        {
            completed = true;
        }

        if (completed)
        {
            ObserveLoopAndDisposeToken();
            return;
        }

        Log.Error(
            $"Discord controller did not stop within {_options.DisposeWaitTimeout.TotalSeconds:F0} seconds; cleanup will continue in the background.");
        _ = _loop.ContinueWith(
            _ => ObserveLoopAndDisposeToken(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveLoopAndDisposeToken()
    {
        try
        {
            _loop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected controller shutdown.
        }
        catch (Exception exception)
        {
            Log.Error("Discord controller stopped with an error during shutdown.", exception);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}

internal sealed record DiscordControllerOptions(
    TimeSpan ClosedPollInterval,
    TimeSpan RunningPollInterval,
    TimeSpan ErrorRetryInterval,
    int AbsentPollThreshold,
    TimeSpan DisposeWaitTimeout)
{
    public static DiscordControllerOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        3,
        TimeSpan.FromSeconds(10));
}
