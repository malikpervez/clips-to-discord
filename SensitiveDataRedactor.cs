using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

public static partial class SensitiveDataRedactor
{
    private const string Replacement = "[REDACTED DISCORD WEBHOOK]";
    private static readonly ConcurrentDictionary<string, byte> RegisteredSecrets = new(StringComparer.Ordinal);

    public static void RegisterSecret(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            RegisteredSecrets.TryAdd(value, 0);
        }
    }

    public static string Redact(string value)
    {
        var redacted = DiscordWebhookPattern().Replace(value, Replacement);
        foreach (var secret in RegisteredSecrets.Keys)
        {
            redacted = redacted.Replace(secret, Replacement, StringComparison.Ordinal);
        }
        return redacted;
    }

    [GeneratedRegex(
        """https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/\d+/[^\s"'<>]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscordWebhookPattern();
}
