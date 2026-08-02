namespace MomentsToDiscord;

internal static class Log
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(SettingsStore.DataDirectory, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} {exception}");

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

                File.AppendAllText(LogPath, $"{DateTime.UtcNow:u} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never stop the app.
        }
    }
}
