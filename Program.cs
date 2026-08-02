namespace MomentsToDiscord;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\MomentsToDiscord_Application", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Moments to Discord is already running in the notification area.",
                "Moments to Discord",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
