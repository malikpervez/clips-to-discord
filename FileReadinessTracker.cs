namespace ClipsToDiscord;

public sealed class FileReadinessTracker
{
    private static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);

    public FileReadinessResult Observe(FileInfo file, DateTime utcNow)
    {
        file.Refresh();
        if (!_observations.TryGetValue(file.FullName, out var observation))
        {
            observation = new Observation
            {
                Length = file.Length,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                StableSinceUtc = utcNow,
                NextCheckUtc = utcNow.Add(StableWindow)
            };
            _observations[file.FullName] = observation;
            return FileReadinessResult.Waiting(observation.NextCheckUtc, "waiting for a second stable observation");
        }

        if (observation.NextCheckUtc > utcNow)
        {
            return FileReadinessResult.Waiting(observation.NextCheckUtc, "readiness check is backed off");
        }

        if (file.Length != observation.Length || file.LastWriteTimeUtc != observation.LastWriteTimeUtc)
        {
            observation.Length = file.Length;
            observation.LastWriteTimeUtc = file.LastWriteTimeUtc;
            observation.StableSinceUtc = utcNow;
            observation.FailedOpenAttempts = 0;
            observation.NextCheckUtc = utcNow.Add(StableWindow);
            return FileReadinessResult.Waiting(observation.NextCheckUtc, "file length or timestamp is still changing");
        }

        if (file.Length <= 0 || utcNow - observation.StableSinceUtc < StableWindow)
        {
            observation.NextCheckUtc = utcNow.Add(StableWindow);
            return FileReadinessResult.Waiting(observation.NextCheckUtc, "file has not been stable long enough");
        }

        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != observation.Length)
            {
                observation.Length = stream.Length;
                observation.StableSinceUtc = utcNow;
                observation.NextCheckUtc = utcNow.Add(StableWindow);
                return FileReadinessResult.Waiting(observation.NextCheckUtc, "file length changed while opening");
            }

            _observations.Remove(file.FullName);
            return FileReadinessResult.Ready();
        }
        catch (IOException)
        {
            return BackOff(observation, utcNow, "file is still locked");
        }
        catch (UnauthorizedAccessException)
        {
            return BackOff(observation, utcNow, "file is not currently readable");
        }
    }

    public void Forget(string filePath) => _observations.Remove(filePath);

    public void RemoveMissingFiles()
    {
        foreach (var path in _observations.Keys.Where(path => !File.Exists(path)).ToArray())
        {
            _observations.Remove(path);
        }
    }

    private static FileReadinessResult BackOff(Observation observation, DateTime utcNow, string reason)
    {
        observation.FailedOpenAttempts++;
        var exponent = Math.Min(observation.FailedOpenAttempts - 1, 5);
        var delaySeconds = Math.Min(
            InitialBackoff.TotalSeconds * Math.Pow(2, exponent),
            MaximumBackoff.TotalSeconds);
        observation.NextCheckUtc = utcNow.AddSeconds(delaySeconds);
        return FileReadinessResult.Waiting(observation.NextCheckUtc, reason);
    }

    private sealed class Observation
    {
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public DateTime StableSinceUtc { get; set; }
        public DateTime NextCheckUtc { get; set; }
        public int FailedOpenAttempts { get; set; }
    }
}

public sealed record FileReadinessResult(bool IsReady, DateTime NextCheckUtc, string Reason)
{
    public static FileReadinessResult Ready() => new(true, DateTime.MinValue, string.Empty);
    public static FileReadinessResult Waiting(DateTime nextCheckUtc, string reason) =>
        new(false, nextCheckUtc, reason);
}
