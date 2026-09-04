namespace ClipsToDiscord;

internal static class Log
{
    private static readonly object Gate = new();
    private static Action<string>? _testSink;
    private static Action<string>? _testObserver;
    private static string LogPath => Path.Combine(SettingsStore.DataDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} {exception}");

    public static void SanitizeExistingFile()
    {
        try
        {
            lock (Gate)
            {
                if (_testSink is not null) return;
                if (!File.Exists(LogPath)) return;
                var original = File.ReadAllText(LogPath);
                var sanitized = SensitiveDataRedactor.Redact(original);
                if (sanitized.Equals(original, StringComparison.Ordinal)) return;

                var temporaryPath = LogPath + ".sanitize.tmp";
                File.WriteAllText(temporaryPath, sanitized);
                File.Move(temporaryPath, LogPath, true);
            }
        }
        catch
        {
            // Sanitization must never stop startup.
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var safeMessage = SensitiveDataRedactor.Redact(message);
                var line = $"{DateTime.UtcNow:u} [{level}] {safeMessage}{Environment.NewLine}";
                if (_testSink is not null)
                {
                    _testSink(line);
                    _testObserver?.Invoke(line);
                    return;
                }

                Directory.CreateDirectory(SettingsStore.DataDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_048_576)
                {
                    File.Move(LogPath, LogPath + ".old", true);
                }

                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging must never stop the app.
        }
    }

    /// <summary>
    /// Keeps smoke-test diagnostics process-local so executing product failure paths cannot append
    /// to the signed-in user's real ClipCord log. Production never calls this internal seam.
    /// </summary>
    internal static IDisposable RedirectForTests(Action<string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            if (_testSink is not null)
            {
                throw new InvalidOperationException("A test log sink is already active.");
            }
            _testSink = sink;
        }
        return new TestSinkScope(sink);
    }

    /// <summary>
    /// Lets a focused smoke test inspect lines after production sanitization while the suite's
    /// process-local sink remains active. The observer never receives unsanitized input.
    /// </summary>
    internal static IDisposable ObserveForTests(Action<string> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (Gate)
        {
            _testObserver += observer;
        }
        return new TestObserverScope(observer);
    }

    private sealed class TestSinkScope(Action<string> sink) : IDisposable
    {
        private Action<string>? _sink = sink;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _sink, null);
            if (current is null) return;
            lock (Gate)
            {
                if (ReferenceEquals(_testSink, current)) _testSink = null;
            }
        }
    }

    private sealed class TestObserverScope(Action<string> observer) : IDisposable
    {
        private Action<string>? _observer = observer;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _observer, null);
            if (current is null) return;
            lock (Gate)
            {
                _testObserver -= current;
            }
        }
    }
}
