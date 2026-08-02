using System.Diagnostics;

namespace MomentsToDiscord;

internal static class DiscordDetector
{
    private static readonly string[] ProcessNames =
        ["Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment"];

    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0) return true;
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
            catch
            {
                // Try the remaining Discord variants.
            }
        }

        return false;
    }
}
