using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

internal sealed record AppSettings(
    string ClipsFolder,
    string WebhookUrl,
    bool StartWithWindows,
    int CompressionTargetMb,
    string UploaderName)
{
    public const int DefaultCompressionTargetMb = 95;
    public const int MaximumUploaderNameLength = 80;
    public static string DefaultUploaderName => NormalizeUploaderName(null);
    public static AppSettings Empty { get; } = new(
        string.Empty,
        string.Empty,
        true,
        DefaultCompressionTargetMb,
        DefaultUploaderName);

    public bool IsValid =>
        Directory.Exists(ClipsFolder) &&
        WebhookValidation.IsDiscordWebhook(WebhookUrl) &&
        CompressionTargetMb is >= 1 and <= 100 &&
        !string.IsNullOrWhiteSpace(UploaderName) &&
        UploaderName.Length <= MaximumUploaderNameLength;

    public static string NormalizeUploaderName(string? value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = Regex.Replace(Environment.UserName, @"\s+", " ").Trim();
        }
        if (string.IsNullOrWhiteSpace(normalized)) normalized = "Someone";
        if (normalized.Length <= MaximumUploaderNameLength) return normalized;

        var safeLength = MaximumUploaderNameLength;
        if (char.IsHighSurrogate(normalized[safeLength - 1])) safeLength--;
        return normalized[..safeLength];
    }
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
    internal static string SafeBaselineMarkerPath => Path.Combine(DataDirectory, ".safe-baseline-required");

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
            var webhookUrl = Encoding.UTF8.GetString(decrypted);
            SensitiveDataRedactor.RegisterSecret(webhookUrl);
            Log.SanitizeExistingFile();
            return new AppSettings(
                stored.ClipsFolder ?? string.Empty,
                webhookUrl,
                stored.StartWithWindows,
                NormalizeCompressionTarget(stored.CompressionTargetMb),
                AppSettings.NormalizeUploaderName(stored.UploaderName));
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
        var stagedDirectory = Path.Combine(DataDirectory, ".legacy-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagedDirectory);
        var migrationCompleted = false;
        try
        {
            foreach (var fileName in new[] { "settings.json", "state.json", "app.log" })
            {
                var sourcePath = Path.Combine(LegacyDataDirectory, fileName);
                var destinationPath = Path.Combine(DataDirectory, fileName);
                if (File.Exists(sourcePath) && !File.Exists(destinationPath))
                {
                    File.Copy(sourcePath, Path.Combine(stagedDirectory, fileName));
                }
            }

            var stagedSettings = Path.Combine(stagedDirectory, "settings.json");
            var stagedState = Path.Combine(stagedDirectory, "state.json");
            if (File.Exists(stagedSettings) || File.Exists(stagedState))
            {
                File.WriteAllText(SafeBaselineMarkerPath, DateTime.UtcNow.ToString("O"));

                // State moves first. A crash before settings moves cannot start an uploader,
                // while a crash after settings moves leaves the safe-baseline marker behind.
                MoveIfPresent(stagedState, Path.Combine(DataDirectory, "state.json"));
                MoveIfPresent(stagedSettings, SettingsPath);
                migrationCompleted = true;
            }

            MoveIfPresent(
                Path.Combine(stagedDirectory, "app.log"),
                Path.Combine(DataDirectory, "app.log"));

            if (migrationCompleted && File.Exists(SafeBaselineMarkerPath))
            {
                File.Delete(SafeBaselineMarkerPath);
            }
        }
        finally
        {
            try { Directory.Delete(stagedDirectory, recursive: true); } catch { }
        }
    }

    private static void MoveIfPresent(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath) && !File.Exists(destinationPath))
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    public static void Save(AppSettings settings)
    {
        SensitiveDataRedactor.RegisterSecret(settings.WebhookUrl);
        Directory.CreateDirectory(DataDirectory);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(settings.WebhookUrl),
            null,
            DataProtectionScope.CurrentUser);
        var stored = new StoredSettings
        {
            ClipsFolder = settings.ClipsFolder,
            ProtectedWebhook = Convert.ToBase64String(encrypted),
            StartWithWindows = settings.StartWithWindows,
            CompressionTargetMb = settings.CompressionTargetMb,
            UploaderName = AppSettings.NormalizeUploaderName(settings.UploaderName)
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
        public int CompressionTargetMb { get; set; } = AppSettings.DefaultCompressionTargetMb;
        public string? UploaderName { get; set; } = AppSettings.DefaultUploaderName;
    }

    private static int NormalizeCompressionTarget(int value) =>
        value is >= 1 and <= 100 ? value : AppSettings.DefaultCompressionTargetMb;
}

public static partial class WebhookValidation
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
               WebhookPathPattern().IsMatch(uri.AbsolutePath);
    }

    [GeneratedRegex(@"^/api/(?:v\d+/)?webhooks/\d+/[^/]+/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebhookPathPattern();
}
