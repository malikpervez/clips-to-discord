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
                "Clips to Discord is already running in the notification area.",
                "Clips to Discord",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
