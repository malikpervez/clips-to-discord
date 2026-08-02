using ClipsToDiscord;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "ClipsToDiscordTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);

try
{
    var existingCaseRoot = Path.Combine(temporaryRoot, "existing-case");
    Directory.CreateDirectory(existingCaseRoot);
    var capitalizedFolder = Directory.CreateDirectory(Path.Combine(existingCaseRoot, "Uploaded")).FullName;
    var resolvedCapitalizedFolder = UploadedFolder.GetOrCreate(existingCaseRoot);

    Assert(
        resolvedCapitalizedFolder.Equals(capitalizedFolder, StringComparison.Ordinal),
        $"Expected existing folder '{capitalizedFolder}', got '{resolvedCapitalizedFolder}'.");
    Assert(
        Directory.EnumerateDirectories(existingCaseRoot).Count() == 1,
        "Resolving Uploaded must not create a second folder.");

    var missingRoot = Path.Combine(temporaryRoot, "missing");
    Directory.CreateDirectory(missingRoot);
    var createdFolder = UploadedFolder.GetOrCreate(missingRoot);
    Assert(Directory.Exists(createdFolder), "The archive folder was not created.");
    Assert(
        Path.GetFileName(createdFolder).Equals("uploaded", StringComparison.Ordinal),
        "A newly created archive folder must use the canonical lowercase name.");

    Console.WriteLine("Archive-folder smoke tests passed.");
}
finally
{
    Directory.Delete(temporaryRoot, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
