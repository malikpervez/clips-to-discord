using System.Diagnostics;

namespace ClipsToDiscord;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        UpdateLaunchRequest? pendingUpdate;
        using (var mutex = new Mutex(true, @"Local\ClipsToDiscord_Application", out var createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show(
                    "ClipCord is already running in the notification area.",
                    "ClipCord",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();
            using var context = new TrayApplicationContext();
            Application.Run(context);
            pendingUpdate = context.PendingUpdateLaunch;
        }

        if (pendingUpdate is not null) LaunchUpdateOrRecover(pendingUpdate);
    }

    private static void LaunchUpdateOrRecover(UpdateLaunchRequest request)
    {
        try
        {
            UpdateInstallerLauncher.Launch(request);
        }
        catch (Exception exception)
        {
            Log.Error("The verified update installer could not be launched.", exception);
            MessageBox.Show(
                "Windows could not start the verified update installer. Your current ClipCord installation is unchanged.",
                "Could not install update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            TryRestartCurrentApplication();
        }
    }

    private static void TryRestartCurrentApplication()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
            }
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not restart after the update installer failed to launch.", exception);
        }
    }
}
