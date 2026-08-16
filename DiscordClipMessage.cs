namespace ClipsToDiscord;

internal static class DiscordClipMessage
{
    internal const int MaximumNoteLength = 600;

    public static string BuildDescription(string uploaderName, string clipFileName)
        => BuildDescriptionForGame(
            uploaderName,
            UploadedFolder.GetGameFolderName(clipFileName),
            note: null);

    internal static string BuildDescriptionForGame(
        string uploaderName,
        string gameName,
        string? note)
    {
        var normalizedUploaderName = AppSettings.NormalizeUploaderName(uploaderName);
        var normalizedGameName = UploadedFolder.SanitizeGameFolderName(gameName);
        // Discord treats attachment descriptions as plain accessibility text, not message Markdown.
        var attribution = normalizedGameName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)
            ? $"{normalizedUploaderName} uploaded a clip."
            : $"{normalizedUploaderName} uploaded a clip from {normalizedGameName}.";
        var normalizedNote = NormalizeNote(note);
        return normalizedNote is null ? attribution : $"{attribution} {normalizedNote}";
    }

    public static string BuildContent(string uploaderName, string clipFileName)
        => BuildContentForGame(
            uploaderName,
            UploadedFolder.GetGameFolderName(clipFileName),
            note: null);

    internal static string BuildContentForGame(
        string uploaderName,
        string gameName,
        string? note)
    {
        var normalizedUploaderName = AppSettings.NormalizeUploaderName(uploaderName);
        var normalizedGameName = UploadedFolder.SanitizeGameFolderName(gameName);
        var attribution = normalizedGameName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)
            ? $"{EscapeMarkdown(normalizedUploaderName)} uploaded a clip."
            : $"{EscapeMarkdown(normalizedUploaderName)} uploaded a clip from {EscapeMarkdown(normalizedGameName)}.";
        var normalizedNote = NormalizeNote(note);
        return normalizedNote is null ? attribution : $"{attribution}\n{EscapeMarkdown(normalizedNote)}";
    }

    internal static string? NormalizeNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = new string(value
            .Where(character => character is '\n' or '\t' || !char.IsControl(character))
            .ToArray())
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (sanitized.Length <= MaximumNoteLength) return sanitized;
        var safeLength = MaximumNoteLength;
        if (char.IsHighSurrogate(sanitized[safeLength - 1])) safeLength--;
        return sanitized[..safeLength];
    }

    public static string EscapeMarkdown(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
}
