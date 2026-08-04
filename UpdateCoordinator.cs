namespace ClipsToDiscord;

internal sealed class UpdateCoordinator : IDisposable
{
    internal static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(24);

    private readonly IUpdateChecker _checker;
    private readonly IUpdatePreferencesStore _preferencesStore;
    private readonly StableVersion _currentVersion;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdatePreferences _preferences;

    public UpdateCoordinator(
        IUpdateChecker checker,
        IUpdatePreferencesStore preferencesStore,
        StableVersion currentVersion,
        Func<DateTimeOffset>? utcNow = null)
    {
        _checker = checker;
        _preferencesStore = preferencesStore;
        _currentVersion = currentVersion;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _preferences = preferencesStore.Load();
    }

    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Busy, Message: "An update check is already running.");
        }

        try
        {
            var now = _utcNow().ToUniversalTime();
            if (!manual)
            {
                var lastCheck = _preferences.LastAutomaticCheckUtc;
                if (lastCheck is not null &&
                    Math.Abs((now - lastCheck.Value).TotalHours) < AutomaticCheckInterval.TotalHours)
                {
                    return new UpdateCheckResult(UpdateCheckStatus.NotDue);
                }

                if (!TrySave(_preferences with { LastAutomaticCheckUtc = now }))
                {
                    return new UpdateCheckResult(
                        UpdateCheckStatus.Failed,
                        Message: "The automatic update-check timestamp could not be saved.");
                }
            }

            var result = await _checker.CheckAsync(_currentVersion, cancellationToken);
            if (manual || result.Status != UpdateCheckStatus.UpdateAvailable || result.Release is null)
            {
                return result;
            }

            var availableVersion = result.Release.Version.ToString();
            if (_preferences.SkippedVersion?.Equals(availableVersion, StringComparison.Ordinal) == true)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Suppressed);
            }

            if (_preferences.RemindVersion?.Equals(availableVersion, StringComparison.Ordinal) == true &&
                _preferences.RemindAfterUtc is not null &&
                now < _preferences.RemindAfterUtc)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Suppressed);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool Skip(UpdateRelease release) => TrySave(_preferences with
    {
        SkippedVersion = release.Version.ToString(),
        RemindVersion = null,
        RemindAfterUtc = null
    });

    public bool RemindLater(UpdateRelease release) => TrySave(_preferences with
    {
        SkippedVersion = null,
        RemindVersion = release.Version.ToString(),
        RemindAfterUtc = _utcNow().ToUniversalTime() + ReminderInterval
    });

    private bool TrySave(UpdatePreferences preferences)
    {
        try
        {
            _preferencesStore.Save(preferences);
            _preferences = preferences;
            return true;
        }
        catch (Exception exception)
        {
            Log.Error("Could not save update preferences.", exception);
            return false;
        }
    }

    public void Dispose()
    {
        _checker.Dispose();
    }
}
