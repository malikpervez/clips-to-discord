namespace MomentsToDiscord;

internal sealed class DiscordAwareController : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    public DiscordAwareController(AppSettings settings, Action<string> reportStatus)
    {
        _loop = Task.Run(() => RunAsync(settings, reportStatus));
    }

    private async Task RunAsync(AppSettings currentSettings, Action<string> status)
    {
        var cancellationToken = _shutdown.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!DiscordDetector.IsRunning())
            {
                status("Discord closed — uploader paused");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            using var watcherCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watcher = new UploaderWorker(currentSettings, status);
            var watcherTask = watcher.RunAsync(watcherCancellation.Token);

            try
            {
                while (!watcherTask.IsCompleted && DiscordDetector.IsRunning())
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }

                if (!DiscordDetector.IsRunning())
                {
                    status("Discord closed — stopping clip watcher");
                    watcherCancellation.Cancel();
                }

                await watcherTask;
            }
            catch (OperationCanceledException) when (watcherCancellation.IsCancellationRequested)
            {
                // Expected when Discord or the app closes.
            }
            catch (Exception exception)
            {
                Log.Error("Clip watcher stopped unexpectedly and will be restarted.", exception);
                status("Watcher error — restarting shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
