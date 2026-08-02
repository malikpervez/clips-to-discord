namespace ClipsToDiscord;

public static class UploadedFolder
{
    private const string FolderName = "uploaded";

    public static string GetOrCreate(string clipsFolder)
    {
        string? caseInsensitiveMatch = null;
        foreach (var directory in Directory.EnumerateDirectories(clipsFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals(FolderName, StringComparison.Ordinal))
            {
                return directory;
            }

            if (caseInsensitiveMatch is null &&
                name.Equals(FolderName, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitiveMatch = directory;
            }
        }

        if (caseInsensitiveMatch is not null)
        {
            return caseInsensitiveMatch;
        }

        return Directory.CreateDirectory(Path.Combine(clipsFolder, FolderName)).FullName;
    }
}
