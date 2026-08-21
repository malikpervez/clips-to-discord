using Microsoft.Win32;
using Windows.ApplicationModel;

namespace ClipsToDiscord;

internal enum PackagedStartupAction
{
    None,
    Disable,
    RequestEnable,
    BlockedByUser,
    BlockedByPolicy
}

internal static class StartupManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipsToDiscord";
    private const string LegacyValueName = "MomentsToDiscord";

    private const string PackagedTaskId = "ClipCordStartup";

    public static async Task ApplyAsync(bool enabled)
    {
        if (AppDistribution.IsPackaged)
        {
            DeleteUnpackagedStartupValues();
            await ApplyPackagedAsync(enabled);
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);

        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static void DeleteUnpackagedStartupValues()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private static async Task ApplyPackagedAsync(bool enabled)
    {
        var startupTask = await StartupTask.GetAsync(PackagedTaskId);
        switch (GetPackagedAction(enabled, startupTask.State))
        {
            case PackagedStartupAction.None:
                return;
            case PackagedStartupAction.Disable:
                startupTask.Disable();
                return;
            case PackagedStartupAction.BlockedByUser:
                throw new InvalidOperationException(
                    "Windows has disabled ClipCord at startup. Enable ClipCord under Settings > Apps > Startup, then save again.");
            case PackagedStartupAction.BlockedByPolicy:
                throw new InvalidOperationException(
                    "Your Windows policy does not allow ClipCord to start automatically.");
            case PackagedStartupAction.RequestEnable:
                break;
            default:
                throw new InvalidOperationException("Unknown packaged startup action.");
        }

        var newState = await startupTask.RequestEnableAsync();
        if (newState != StartupTaskState.Enabled)
        {
            throw new InvalidOperationException(
                "Windows did not enable ClipCord at startup. Review Settings > Apps > Startup and try again.");
        }
    }

    internal static PackagedStartupAction GetPackagedAction(bool enabled, StartupTaskState state)
    {
        if (!enabled)
        {
            return state == StartupTaskState.Enabled
                ? PackagedStartupAction.Disable
                : PackagedStartupAction.None;
        }

        return state switch
        {
            StartupTaskState.Enabled => PackagedStartupAction.None,
            StartupTaskState.Disabled => PackagedStartupAction.RequestEnable,
            StartupTaskState.DisabledByUser => PackagedStartupAction.BlockedByUser,
            StartupTaskState.DisabledByPolicy => PackagedStartupAction.BlockedByPolicy,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown startup task state.")
        };
    }
}
