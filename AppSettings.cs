using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipsToDiscord;

internal sealed record AppSettings(string ClipsFolder, string WebhookUrl, bool StartWithWindows)
{
    public static AppSettings Empty { get; } = new(string.Empty, string.Empty, true);

    public bool IsValid =>
        Directory.Exists(ClipsFolder) &&
        WebhookValidation.IsDiscordWebhook(WebhookUrl);
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipsToDiscord");

    private static string LegacyDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MomentsToDiscord");

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            MigrateLegacyData();
            if (!File.Exists(SettingsPath))
            {
                return AppSettings.Empty;
            }

            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            if (stored is null || string.IsNullOrWhiteSpace(stored.ProtectedWebhook))
            {
                return AppSettings.Empty;
            }

            var encrypted = Convert.FromBase64String(stored.ProtectedWebhook);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return new AppSettings(
                stored.ClipsFolder ?? string.Empty,
                Encoding.UTF8.GetString(decrypted),
                stored.StartWithWindows);
        }
        catch (Exception exception)
        {
            Log.Error("Could not load settings.", exception);
            return AppSettings.Empty;
        }
    }

    private static void MigrateLegacyData()
    {
        if (!Directory.Exists(LegacyDataDirectory)) return;

        Directory.CreateDirectory(DataDirectory);
        foreach (var fileName in new[] { "settings.json", "state.json", "app.log" })
        {
            var sourcePath = Path.Combine(LegacyDataDirectory, fileName);
            var destinationPath = Path.Combine(DataDirectory, fileName);
            if (File.Exists(sourcePath) && !File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath);
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(settings.WebhookUrl),
            null,
            DataProtectionScope.CurrentUser);
        var stored = new StoredSettings
        {
            ClipsFolder = settings.ClipsFolder,
            ProtectedWebhook = Convert.ToBase64String(encrypted),
            StartWithWindows = settings.StartWithWindows
        };

        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }

    private sealed class StoredSettings
    {
        public string? ClipsFolder { get; set; }
        public string? ProtectedWebhook { get; set; }
        public bool StartWithWindows { get; set; } = true;
    }
}

internal static class WebhookValidation
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.com",
        "discordapp.com",
        "canary.discord.com",
        "ptb.discord.com"
    };

    public static bool IsDiscordWebhook(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               AllowedHosts.Contains(uri.Host) &&
               uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }
}
