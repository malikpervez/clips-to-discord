namespace ClipsToDiscord;

internal static class DiscordClipMessage
{
    public static string BuildDescription(string uploaderName, string clipFileName)
    {
        var normalizedUploaderName = AppSettings.NormalizeUploaderName(uploaderName);
        var gameName = UploadedFolder.GetGameFolderName(clipFileName);
        // Discord treats attachment descriptions as plain accessibility text, not message Markdown.
        return gameName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)
            ? $"{normalizedUploaderName} uploaded a clip."
            : $"{normalizedUploaderName} uploaded a clip from {gameName}.";
    }

    public static string BuildContent(string uploaderName, string clipFileName)
    {
        var normalizedUploaderName = AppSettings.NormalizeUploaderName(uploaderName);
        var gameName = UploadedFolder.GetGameFolderName(clipFileName);
        return gameName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)
            ? $"{EscapeMarkdown(normalizedUploaderName)} uploaded a clip."
            : $"{EscapeMarkdown(normalizedUploaderName)} uploaded a clip from {EscapeMarkdown(gameName)}.";
    }

    public static string EscapeMarkdown(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
}
