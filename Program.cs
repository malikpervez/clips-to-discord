namespace ClipsToDiscord;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ClipsToDiscord_Application", out var createdNew);
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
        Application.Run(new TrayApplicationContext());
    }
}
