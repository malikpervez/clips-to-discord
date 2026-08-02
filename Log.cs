namespace ClipsToDiscord;

internal static class Log
{
    private static readonly object Gate = new();
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
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_048_576)
                {
                    File.Move(LogPath, LogPath + ".old", true);
                }

                var safeMessage = SensitiveDataRedactor.Redact(message);
                File.AppendAllText(LogPath, $"{DateTime.UtcNow:u} [{level}] {safeMessage}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never stop the app.
        }
    }
}
