using System.Text;
using System.Text.RegularExpressions;

namespace ClipsToDiscord;

public static class UploadedFolder
{
    private const string FolderName = "uploaded";
    private const string LocalOnlyFolderName = "local-only";
    private const string UncategorizedFolderName = "Uncategorized";
    private const int MaximumGameFolderLength = 80;
    private static readonly HashSet<char> InvalidFolderCharacters = [.. Path.GetInvalidFileNameChars()];
    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReservedWindowsNamePattern = new(
        @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex[] TimestampSuffixPatterns =
    [
        new Regex(
            @"^(?<game>.+?)__\d{4}-\d{2}-\d{2}__\d{2}-\d{2}-\d{2}(?:[-_.]\d+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new Regex(
            @"^(?<game>.+?)[ _-]+\d{4}[-.]\d{2}[-.]\d{2}(?:[ _-]+(?:at[ _-]+)?\d{2}[-.]\d{2}[-.]\d{2}(?:[-.]\d+)?)?(?:[ ._-]*DVR)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new Regex(
            @"^(?<game>.+?)[ _-]+\d{8}[ _-]+\d{6}(?:[-_.]\d+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant)
    ];

    public static string GetOrCreate(string clipsFolder) =>
        GetOrCreateCaseInsensitiveChild(clipsFolder, FolderName);

    internal static string? FindExistingUploaded(string clipsFolder) =>
        FindCaseInsensitiveChild(clipsFolder, FolderName);

    public static string GetOrCreateForClip(string clipsFolder, string clipFileName)
    {
        var uploadedFolder = GetOrCreate(clipsFolder);
        return GetOrCreateCaseInsensitiveChild(uploadedFolder, GetGameFolderName(clipFileName));
    }

    public static string GetOrCreateLocalOnly(string clipsFolder) =>
        GetOrCreateCaseInsensitiveChild(clipsFolder, LocalOnlyFolderName);

    public static string GetOrCreateLocalOnlyForClip(string clipsFolder, string clipFileName)
    {
        var localOnlyFolder = GetOrCreateLocalOnly(clipsFolder);
        return GetOrCreateCaseInsensitiveChild(localOnlyFolder, GetGameFolderName(clipFileName));
    }

    internal static string? FindExistingLocalOnly(string clipsFolder) =>
        FindCaseInsensitiveChild(clipsFolder, LocalOnlyFolderName);

    public static string GetGameFolderName(string clipFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(clipFileName).Normalize(NormalizationForm.FormC);
        string? gameName = null;
        foreach (var pattern in TimestampSuffixPatterns)
        {
            var match = pattern.Match(baseName);
            if (match.Success)
            {
                gameName = match.Groups["game"].Value;
                break;
            }
        }

        return SanitizeGameFolderName(gameName);
    }

    public static IEnumerable<string> EnumerateArchivedClips(string uploadedFolder)
    {
        foreach (var path in Directory.EnumerateFiles(uploadedFolder, "*.mp4", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var directory in Directory.EnumerateDirectories(uploadedFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null) continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.mp4", SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
    }

    private static string GetOrCreateCaseInsensitiveChild(string parentFolder, string requestedName)
    {
        var existing = FindCaseInsensitiveChild(parentFolder, requestedName);
        return existing ?? Directory.CreateDirectory(Path.Combine(parentFolder, requestedName)).FullName;
    }

    private static string? FindCaseInsensitiveChild(string parentFolder, string requestedName)
    {
        string? caseInsensitiveMatch = null;
        foreach (var directory in Directory.EnumerateDirectories(parentFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(directory);
            var name = info.Name.Normalize(NormalizationForm.FormC);
            if (name.Equals(requestedName, StringComparison.Ordinal))
            {
                if (info.LinkTarget is not null)
                {
                    throw new IOException($"Archive folder cannot be a symbolic link or junction: {directory}");
                }
                return directory;
            }

            if (caseInsensitiveMatch is null &&
                name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
            {
                if (info.LinkTarget is not null)
                {
                    throw new IOException($"Archive folder cannot be a symbolic link or junction: {directory}");
                }
                caseInsensitiveMatch = directory;
            }
        }

        if (caseInsensitiveMatch is not null)
        {
            return caseInsensitiveMatch;
        }

        return null;
    }

    private static string SanitizeGameFolderName(string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return UncategorizedFolderName;

        var normalized = gameName.Normalize(NormalizationForm.FormC);
        var sanitized = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            sanitized.Append(InvalidFolderCharacters.Contains(character) ? '_' : character);
        }

        var candidate = WhitespacePattern.Replace(sanitized.ToString(), " ").Trim(' ', '.');
        if (candidate.Length > MaximumGameFolderLength)
        {
            candidate = candidate[..MaximumGameFolderLength].TrimEnd(' ', '.');
        }
        if (string.IsNullOrWhiteSpace(candidate)) return UncategorizedFolderName;
        if (ReservedWindowsNamePattern.IsMatch(candidate)) candidate = "_" + candidate;
        return candidate;
    }
}
