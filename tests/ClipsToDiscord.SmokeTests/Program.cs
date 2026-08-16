using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using ClipsToDiscord;

try
{
    Application.SetHighDpiMode(HighDpiMode.SystemAware);
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    if (args.Length == 2 && args[0].Equals("--render-mode-feedback", StringComparison.Ordinal))
    {
        RenderModeFeedbackPreviews(args[1]);
        return;
    }

    if (args.Length == 2 && args[0].Equals("--render-settings", StringComparison.Ordinal))
    {
        RenderSettingsPreview(args[1]);
        return;
    }

    if (args.Length == 2 && args[0].Equals("--render-about", StringComparison.Ordinal))
    {
        RenderAboutPreview(args[1]);
        return;
    }

    if (args.Length == 2 && args[0].Equals("--render-gallery-editor", StringComparison.Ordinal))
    {
        RenderGalleryEditorPreview(args[1]);
        return;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
    return;
}

string? temporaryRoot = null;

try
{
    temporaryRoot = Path.Combine(Path.GetTempPath(), "ClipsToDiscordTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryRoot);
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

    var localOnlyCaseRoot = Path.Combine(temporaryRoot, "local-only-case");
    Directory.CreateDirectory(localOnlyCaseRoot);
    var capitalizedLocalOnly = Directory.CreateDirectory(Path.Combine(localOnlyCaseRoot, "Local-Only")).FullName;
    var resolvedLocalOnly = UploadedFolder.GetOrCreateLocalOnly(localOnlyCaseRoot);
    Assert(
        resolvedLocalOnly.Equals(capitalizedLocalOnly, StringComparison.Ordinal),
        "Local-only archive resolution must reuse an existing differently-cased folder.");
    Assert(
        Directory.EnumerateDirectories(localOnlyCaseRoot).Count() == 1,
        "Resolving Local-Only must not create a second folder.");

    var missingLocalOnlyRoot = Path.Combine(temporaryRoot, "missing-local-only");
    Directory.CreateDirectory(missingLocalOnlyRoot);
    var createdLocalOnlyFolder = UploadedFolder.GetOrCreateLocalOnly(missingLocalOnlyRoot);
    Assert(
        Path.GetFileName(createdLocalOnlyFolder).Equals("local-only", StringComparison.Ordinal),
        "A newly created local-only folder must use the canonical lowercase name.");

    var reparseArchiveRoot = Path.Combine(temporaryRoot, "local-only-reparse-root");
    var reparseTarget = Path.Combine(temporaryRoot, "local-only-reparse-target");
    var reparseLocalOnly = Path.Combine(reparseArchiveRoot, "local-only");
    Directory.CreateDirectory(reparseArchiveRoot);
    Directory.CreateDirectory(reparseTarget);
    var reparsePayload = Path.Combine(reparseTarget, "must-remain.txt");
    await File.WriteAllTextAsync(reparsePayload, "preserve target");
    CreateDirectoryJunction(reparseLocalOnly, reparseTarget);
    try
    {
        var rejected = false;
        try
        {
            UploadedFolder.GetOrCreateLocalOnly(reparseArchiveRoot);
        }
        catch (IOException)
        {
            rejected = true;
        }
        Assert(rejected, "A local-only archive root must reject symbolic links and junctions.");
    }
    finally
    {
        Directory.Delete(reparseLocalOnly);
    }
    Assert(File.Exists(reparsePayload), "Removing the test junction must not remove its target contents.");

    Assert(
        UploadedFolder.GetGameFolderName("Battlefield™-6__2026-08-03__13-43-46.mp4") == "Battlefield™-6",
        "SteelSeries timestamps must be removed from the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("Counter-Strike 2 2026.08.03 - 13.43.46.01.mp4") == "Counter-Strike 2",
        "Dotted recording timestamps must be removed from the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("Game_20260803_134346.mp4") == "Game",
        "Compact recording timestamps must be removed from the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("manual-highlight.mp4") == "Uncategorized",
        "A filename without a recognizable game prefix must use Uncategorized.");
    Assert(
        UploadedFolder.GetGameFolderName("Game__not-a-timestamp.mp4") == "Uncategorized",
        "A double underscore without a recognized timestamp must use Uncategorized.");
    Assert(
        UploadedFolder.GetGameFolderName("Apex Legends 2026.08.03 - 13.43.46.01.DVR.mp4") == "Apex Legends",
        "DVR filename suffixes must not become part of the game folder name.");
    Assert(
        UploadedFolder.GetGameFolderName("CON__2026-08-03__13-43-46.mp4") == "_CON",
        "Reserved Windows device names must be made safe for folders.");

    var gameArchiveRoot = Path.Combine(temporaryRoot, "game-archive");
    Directory.CreateDirectory(gameArchiveRoot);
    var gameUploadedFolder = Directory.CreateDirectory(Path.Combine(gameArchiveRoot, "Uploaded")).FullName;
    var existingGameFolder = Directory.CreateDirectory(Path.Combine(gameUploadedFolder, "Battlefield™-6")).FullName;
    var resolvedGameFolder = UploadedFolder.GetOrCreateForClip(
        gameArchiveRoot,
        "battlefield™-6__2026-08-03__13-43-46.mp4");
    Assert(
        resolvedGameFolder.Equals(existingGameFolder, StringComparison.Ordinal),
        "Game folders must be reused case-insensitively.");
    Assert(
        Directory.EnumerateDirectories(gameUploadedFolder).Count() == 1,
        "Resolving a differently-cased game name must not create a duplicate folder.");

    var rootArchiveClip = Path.Combine(gameUploadedFolder, "legacy-root.mp4");
    var nestedArchiveClip = Path.Combine(existingGameFolder, "nested.MP4");
    var ignoredGalleryFile = Path.Combine(existingGameFolder, "notes.txt");
    await File.WriteAllBytesAsync(rootArchiveClip, [1, 1, 2, 3]);
    await File.WriteAllBytesAsync(nestedArchiveClip, [5, 8, 13, 21]);
    await File.WriteAllTextAsync(ignoredGalleryFile, "not a clip");
    var archivedClips = UploadedFolder.EnumerateArchivedClips(gameUploadedFolder).ToHashSet(
        StringComparer.OrdinalIgnoreCase);
    Assert(archivedClips.Contains(rootArchiveClip), "Archived baseline enumeration must retain legacy root clips.");
    Assert(archivedClips.Contains(nestedArchiveClip), "Archived baseline enumeration must include game subfolders.");

    var gameLocalOnlyFolder = Directory.CreateDirectory(Path.Combine(gameArchiveRoot, "Local-Only")).FullName;
    var existingLocalOnlyGameFolder = Directory.CreateDirectory(Path.Combine(gameLocalOnlyFolder, "Battlefield™-6")).FullName;
    var temporaryLocalOnlyGameFolder = Path.Combine(gameLocalOnlyFolder, "case-swap");
    var lowercaseLocalOnlyGameFolder = Path.Combine(
        gameLocalOnlyFolder,
        Path.GetFileName(existingLocalOnlyGameFolder).ToLowerInvariant());
    Directory.Move(existingLocalOnlyGameFolder, temporaryLocalOnlyGameFolder);
    Directory.Move(temporaryLocalOnlyGameFolder, lowercaseLocalOnlyGameFolder);
    existingLocalOnlyGameFolder = lowercaseLocalOnlyGameFolder;
    var resolvedLocalOnlyGameFolder = UploadedFolder.GetOrCreateLocalOnlyForClip(
        gameArchiveRoot,
        "battlefield™-6__2026-08-03__13-43-46.mp4");
    Assert(
        resolvedLocalOnlyGameFolder.Equals(existingLocalOnlyGameFolder, StringComparison.Ordinal),
        "Local-only game folders must be reused case-insensitively.");
    var localOnlyRootClip = Path.Combine(gameLocalOnlyFolder, "local-root.mp4");
    var localOnlyNestedClip = Path.Combine(existingLocalOnlyGameFolder, "local-nested.mp4");
    await File.WriteAllBytesAsync(localOnlyRootClip, [34, 55, 89]);
    await File.WriteAllBytesAsync(localOnlyNestedClip, [144, 233, 121]);

    var linkedGalleryTarget = Directory.CreateDirectory(Path.Combine(temporaryRoot, "gallery-linked-target")).FullName;
    var linkedGalleryClip = Path.Combine(linkedGalleryTarget, "outside-archive.mp4");
    await File.WriteAllBytesAsync(linkedGalleryClip, [1, 2, 3, 4]);
    var linkedGalleryGame = Path.Combine(gameUploadedFolder, "Linked game");
    CreateDirectoryJunction(linkedGalleryGame, linkedGalleryTarget);
    GallerySnapshot gallerySnapshot;
    try
    {
        gallerySnapshot = GalleryCatalog.Scan(gameArchiveRoot, CancellationToken.None);
    }
    finally
    {
        Directory.Delete(linkedGalleryGame);
    }
    Assert(File.Exists(linkedGalleryClip),
        "Gallery junction cleanup must not remove content outside the archive.");
    Assert(gallerySnapshot.TotalClips == 4 && gallerySnapshot.UploadedCount == 2 && gallerySnapshot.LocalOnlyCount == 2,
        "Gallery must combine uploaded and local-only clips without changing their route.");
    Assert(gallerySnapshot.Games.SelectMany(game => game.Clips).All(clip =>
            Path.GetExtension(clip.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)),
        "Gallery must include uppercase MP4 extensions while excluding non-video archive files.");
    Assert(gallerySnapshot.Games.All(game => !game.Name.Equals("Linked game", StringComparison.OrdinalIgnoreCase)),
        "Gallery must not traverse a linked game folder outside the archive.");
    var galleryBattlefield = gallerySnapshot.Games.Single(game =>
        game.Name.StartsWith("Battlefield", StringComparison.OrdinalIgnoreCase));
    Assert(galleryBattlefield.Clips.Count == 2 &&
           galleryBattlefield.UploadedCount == 1 &&
           galleryBattlefield.LocalOnlyCount == 1,
        "Gallery must merge case-insensitive game folders across both archives.");
    Assert(gallerySnapshot.Games.Single(game => game.Name == "Uncategorized").Clips.Count == 2,
        "Legacy clips stored at an archive root must remain visible under Uncategorized.");
    Assert(GalleryCatalog.GetGradient("Battlefield 6") == new GalleryGradient(
               Color.FromArgb(73, 153, 83),
               Color.FromArgb(29, 58, 68)),
        "The deterministic Battlefield gradient mapping must remain stable.");
    Assert(GalleryCatalog.GetGradient("Caf\u00e9") == GalleryCatalog.GetGradient("Cafe\u0301"),
        "Game gradients must normalize equivalent Unicode names before hashing.");
    Assert(GalleryCatalog.GetInitials("Battlefield 6") == "B6" &&
           GalleryCatalog.GetInitials("Counter-Strike 2") == "CS2",
        "Game cards must derive concise deterministic initials.");
    var playClipStart = GalleryView.CreatePlayClipStartInfo(localOnlyNestedClip);
    Assert(playClipStart.UseShellExecute && playClipStart.FileName == localOnlyNestedClip &&
           playClipStart.ArgumentList.Count == 0,
        "Gallery playback must pass the exact clip path to Windows without a shell command string.");

    var parseableLegacyRoot = Path.Combine(temporaryRoot, "gallery-parseable-root");
    var parseableLegacyUploaded = Directory.CreateDirectory(Path.Combine(parseableLegacyRoot, "uploaded")).FullName;
    await File.WriteAllBytesAsync(
        Path.Combine(parseableLegacyUploaded, "Halo Infinite__2026-01-02__10-00-00.mp4"),
        [3, 1, 4]);
    var parseableLegacySnapshot = GalleryCatalog.Scan(parseableLegacyRoot, CancellationToken.None);
    Assert(parseableLegacySnapshot.Games.Single().Name == "Halo Infinite",
        "A parseable legacy root clip should join its inferred game instead of losing useful organization.");

    var unicodeGalleryRoot = Path.Combine(temporaryRoot, "gallery-unicode-groups");
    var composedGameFolder = Directory.CreateDirectory(
        Path.Combine(unicodeGalleryRoot, "uploaded", "Caf\u00e9")).FullName;
    var decomposedGameFolder = Directory.CreateDirectory(
        Path.Combine(unicodeGalleryRoot, "local-only", "Cafe\u0301")).FullName;
    await File.WriteAllBytesAsync(Path.Combine(composedGameFolder, "uploaded.mp4"), [1, 6, 1, 8]);
    await File.WriteAllBytesAsync(Path.Combine(decomposedGameFolder, "local.mp4"), [0, 3, 3, 9]);
    var unicodeGallerySnapshot = GalleryCatalog.Scan(unicodeGalleryRoot, CancellationToken.None);
    Assert(unicodeGallerySnapshot.Games.Count == 1 &&
           unicodeGallerySnapshot.Games.Single().Clips.Count == 2,
        "Equivalent composed and decomposed Unicode game folders must collapse into one Gallery group.");

    var linkedArchiveRoot = Path.Combine(temporaryRoot, "gallery-linked-archive-root");
    var linkedArchiveTarget = Directory.CreateDirectory(Path.Combine(temporaryRoot, "gallery-linked-archive-target")).FullName;
    var linkedArchivePayload = Path.Combine(linkedArchiveTarget, "outside.mp4");
    await File.WriteAllBytesAsync(linkedArchivePayload, [2, 7, 1, 8]);
    Directory.CreateDirectory(linkedArchiveRoot);
    var linkedUploadedArchive = Path.Combine(linkedArchiveRoot, "Uploaded");
    CreateDirectoryJunction(linkedUploadedArchive, linkedArchiveTarget);
    var healthyLocalArchive = Directory.CreateDirectory(
        Path.Combine(linkedArchiveRoot, "Local-Only", "Healthy game")).FullName;
    await File.WriteAllBytesAsync(Path.Combine(healthyLocalArchive, "healthy.mp4"), [2, 8, 1, 8]);
    try
    {
        var linkedArchiveSnapshot = GalleryCatalog.Scan(linkedArchiveRoot, CancellationToken.None);
        Assert(linkedArchiveSnapshot.TotalClips == 1 &&
               linkedArchiveSnapshot.LocalOnlyCount == 1 &&
               linkedArchiveSnapshot.Warnings.Count == 1,
            "A linked uploaded archive root must be skipped without hiding a healthy local-only archive.");
        Assert(linkedArchiveSnapshot.Warnings.All(warning =>
                !warning.Contains(linkedArchiveRoot, StringComparison.OrdinalIgnoreCase) &&
                !warning.Contains(linkedArchiveTarget, StringComparison.OrdinalIgnoreCase)),
            "Gallery warnings shown in the UI must not expose local filesystem paths.");
    }
    finally
    {
        Directory.Delete(linkedUploadedArchive);
    }
    Assert(File.Exists(linkedArchivePayload),
        "Rejecting a linked archive root must not modify its external target.");

    using (var cancelledGalleryScan = new CancellationTokenSource())
    {
        cancelledGalleryScan.Cancel();
        var cancellationPropagated = false;
        try
        {
            GalleryCatalog.Scan(gameArchiveRoot, cancelledGalleryScan.Token);
        }
        catch (OperationCanceledException) when (cancelledGalleryScan.IsCancellationRequested)
        {
            cancellationPropagated = true;
        }
        Assert(cancellationPropagated,
            "Gallery cancellation must propagate instead of becoming an unreadable-folder warning.");
    }

    var disappearingArchiveRoot = Path.Combine(temporaryRoot, "gallery-disappearing-folder");
    var disappearingUploaded = Directory.CreateDirectory(Path.Combine(disappearingArchiveRoot, "uploaded")).FullName;
    var doomedGameFolder = Directory.CreateDirectory(Path.Combine(disappearingUploaded, "AAA doomed")).FullName;
    var healthyGameFolder = Directory.CreateDirectory(Path.Combine(disappearingUploaded, "Zulu healthy")).FullName;
    await File.WriteAllBytesAsync(Path.Combine(doomedGameFolder, "doomed.mp4"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(healthyGameFolder, "healthy.mp4"), [2]);
    var disappearingSnapshot = GalleryCatalog.Scan(
        disappearingArchiveRoot,
        CancellationToken.None,
        directory =>
        {
            if (directory.Equals(doomedGameFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    Assert(disappearingSnapshot.TotalClips == 1 &&
           disappearingSnapshot.Games.Single().Name == "Zulu healthy" &&
           disappearingSnapshot.Warnings.Count == 1,
        "A game folder that disappears mid-scan must not hide later healthy game folders.");

    var gameBaselineStateDirectory = Path.Combine(temporaryRoot, "game-baseline-state");
    Directory.CreateDirectory(gameBaselineStateDirectory);
    var gameBaselineStore = new WatchStateStore(
        Path.Combine(gameBaselineStateDirectory, "state.json"),
        Path.Combine(gameBaselineStateDirectory, ".safe-baseline-required"));
    var gameBaselineState = await gameBaselineStore.LoadOrInitializeAsync(
        gameArchiveRoot,
        _ => { },
        CancellationToken.None);
    Assert(
        gameBaselineState.UploadedContentHashes.Count == 2,
        "Safe baseline state must mark root-level and game-subfolder archives as uploaded.");
    Assert(
        gameBaselineState.LocalOnlyContentHashes.Count == 2,
        "Safe baseline state must recognize root-level and game-subfolder local-only clips.");
    Assert(
        gameBaselineState.LocalOnlyContentHashes.All(hash => !gameBaselineState.UploadedContentHashes.Contains(hash)),
        "Local-only baseline clips must never be classified as uploaded.");

    var v2UpgradeStateDirectory = Path.Combine(temporaryRoot, "v2-local-only-upgrade");
    Directory.CreateDirectory(v2UpgradeStateDirectory);
    var v2UpgradeStore = new WatchStateStore(
        Path.Combine(v2UpgradeStateDirectory, "state.json"),
        Path.Combine(v2UpgradeStateDirectory, ".safe-baseline-required"));
    v2UpgradeStore.Save(new WatchState { Version = 2, ClipsFolder = gameArchiveRoot });
    var upgradedV2State = await v2UpgradeStore.LoadOrInitializeAsync(
        gameArchiveRoot,
        _ => { },
        CancellationToken.None);
    Assert(
        upgradedV2State.Version == 4 && upgradedV2State.LocalOnlyContentHashes.Count == 2,
        "A v2 state upgrade must baseline existing local-only archives without treating them as uploads.");

    var v3UpgradeStateDirectory = Path.Combine(temporaryRoot, "v3-edited-upload-upgrade");
    Directory.CreateDirectory(v3UpgradeStateDirectory);
    var v3StatePath = Path.Combine(v3UpgradeStateDirectory, "state.json");
    var v3UpgradeStore = new WatchStateStore(
        v3StatePath,
        Path.Combine(v3UpgradeStateDirectory, ".safe-baseline-required"));
    v3UpgradeStore.Save(new WatchState { Version = 3, ClipsFolder = gameArchiveRoot });
    var upgradedV3State = await v3UpgradeStore.LoadOrInitializeAsync(
        gameArchiveRoot,
        _ => { },
        CancellationToken.None);
    using (var persistedV3Upgrade = JsonDocument.Parse(File.ReadAllText(v3StatePath)))
    {
        Assert(upgradedV3State.Version == 4 &&
               upgradedV3State.LocalOnlyContentHashes.Count == 0 &&
               persistedV3Upgrade.RootElement.GetProperty("Version").GetInt32() == 4,
            "A quiet same-folder v3 migration must durably write state v4 without rerunning the v2 Local-only baseline.");
    }

    var localOnlySettings = new AppSettings(
        gameArchiveRoot,
        string.Empty,
        true,
        AppSettings.DefaultCompressionTargetMb,
        AppSettings.DefaultUploaderName,
        false);
    Assert(localOnlySettings.IsValid, "Local-only mode must not require a Discord webhook.");
    Assert(
        !(localOnlySettings with { UploadToDiscord = true }).IsValid,
        "Enabling Discord uploads must still require a valid webhook.");
    Assert((localOnlySettings with { ModeToggleHotkey = "malformed cosmetic value" }).IsValid,
        "A malformed cosmetic shortcut value must never stop otherwise-valid clip watching.");
    Assert(AppSettings.Empty.UploadToDiscord, "New installations must default to Discord uploads enabled.");
    Assert(AppSettings.Empty.ModeToggleHotkey == GlobalHotkeyBinding.DefaultDisplayText,
        "New installations must default the global mode shortcut to Ctrl + Alt + L.");
    Assert(AppSettings.NormalizeModeToggleHotkey(null) == GlobalHotkeyBinding.DefaultDisplayText &&
           AppSettings.NormalizeModeToggleHotkey(string.Empty) == string.Empty &&
           AppSettings.NormalizeModeToggleHotkey("control+alt+l") == GlobalHotkeyBinding.DefaultDisplayText,
        "Hotkey migration must default missing values, preserve an intentional disable, and normalize valid values.");
    Assert(GlobalHotkeyBinding.TryParse("Ctrl + Alt + L", out var defaultHotkey) &&
           defaultHotkey == GlobalHotkeyBinding.Default &&
           defaultHotkey.DisplayText == GlobalHotkeyBinding.DefaultDisplayText,
        "The default global mode shortcut must parse and format deterministically.");
    Assert(GlobalHotkeyBinding.TryParse("Alt+Shift+9", out var numericHotkey) &&
           numericHotkey.DisplayText == "Alt + Shift + 9" &&
           GlobalHotkeyBinding.TryParse("Ctrl + F24", out var functionHotkey) &&
           functionHotkey.DisplayText == "Ctrl + F24" &&
           GlobalHotkeyBinding.TryFromKeyData(Keys.Control | Keys.Alt | Keys.U, out var capturedHotkey) &&
           capturedHotkey.DisplayText == "Ctrl + Alt + U",
        "Supported letter, number, function-key, and captured shortcuts must normalize consistently.");
    foreach (var invalidHotkey in new[]
             {
                 "", "L", "Shift + L", "Alt + F4", "Ctrl + Alt", "Ctrl + Ctrl + L", "Ctrl ++ L",
                 "Win + L", "Ctrl + Delete", "Ctrl + Alt + Space"
             })
    {
        Assert(!GlobalHotkeyBinding.TryParse(invalidHotkey, out _),
            $"Unsafe or ambiguous global shortcut '{invalidHotkey}' must be rejected.");
    }
    AssertGlobalHotkeyLifecycle();
    AssertRealGlobalHotkeyRegistration();
    Assert(GlobalHotkeyManager.GetNativeModifiers(GlobalHotkeyBinding.Default) ==
           ((uint)GlobalHotkeyBinding.Default.Modifiers | GlobalHotkeyManager.ModNoRepeat),
        "Every native registration must include MOD_NOREPEAT with the configured modifiers.");
    AssertModeHotkeyGuardPolicy();
    AssertModeFeedbackOverlayContract();
    TraceSmokeStep("Mode feedback overlay behavior");
    AssertModeFeedbackOverlayBehavior();

    Assert(
        AppSettings.NormalizeUploaderName("  Malik   Pervez  ") == "Malik Pervez",
        "Uploader names must trim and collapse whitespace.");
    Assert(
        !string.IsNullOrWhiteSpace(AppSettings.NormalizeUploaderName(null)),
        "Existing settings without an uploader name must receive a safe default.");
    var surrogateBoundaryName = new string('a', AppSettings.MaximumUploaderNameLength - 1) + "😀";
    Assert(
        AppSettings.NormalizeUploaderName(surrogateBoundaryName) ==
        new string('a', AppSettings.MaximumUploaderNameLength - 1),
        "Uploader-name truncation must not retain a lone UTF-16 surrogate.");
    Assert(
        DiscordClipMessage.BuildDescription("Malik", "Battlefield™-6__2026-08-03__13-43-46.mp4") ==
        "Malik uploaded a clip from Battlefield™-6.",
        "Timestamped clips must identify the uploader and parsed game.");
    Assert(
        DiscordClipMessage.BuildContent("Malik", "Battlefield™-6__2026-08-03__13-43-46.mp4") ==
        "Malik uploaded a clip from Battlefield™-6.",
        "Ordinary game-name punctuation must not gain visible Markdown escape characters.");
    Assert(
        DiscordClipMessage.BuildContent("player_*one*", "manual-highlight.mp4") ==
        "player\\_\\*one\\* uploaded a clip.",
        "Uploader names must be escaped and unrecognized games must not claim a game name.");
    var longManualNote = new string('x', DiscordClipMessage.MaximumNoteLength - 1) + "😀ignored";
    var normalizedManualNote = DiscordClipMessage.NormalizeNote("  flank\r\n*push*\u0001  ");
    Assert(normalizedManualNote == "flank\n*push*" &&
           DiscordClipMessage.BuildContentForGame(
               "Malik_*",
               "Battlefield™-6",
               normalizedManualNote) ==
           "Malik\\_\\* uploaded a clip from Battlefield™-6.\nflank\n\\*push\\*" &&
           DiscordClipMessage.BuildDescriptionForGame(
               "Malik_*",
               "Battlefield™-6",
               normalizedManualNote) ==
           "Malik_* uploaded a clip from Battlefield™-6. flank\n*push*" &&
           DiscordClipMessage.NormalizeNote(longManualNote) is { Length: DiscordClipMessage.MaximumNoteLength - 1 } boundedNote &&
           !char.IsSurrogate(boundedNote[^1]),
        "Manual edit notes must remove controls, normalize line endings, escape visible Markdown only, and never split a surrogate at the bound.");
    using (var payload = JsonDocument.Parse(DiscordWebhookClient.BuildUploadPayload(
               "clip.mp4",
               "Malik uploaded a clip from Battlefield™-6.",
               "Malik uploaded a clip from Battlefield™-6.")))
    {
        var root = payload.RootElement;
        Assert(root.GetProperty("content").GetString() == "Malik uploaded a clip from Battlefield™-6.",
            "The visible Discord message must contain uploader attribution.");
        var attachment = root.GetProperty("attachments")[0];
        Assert(attachment.GetProperty("id").GetInt32() == 0 &&
               attachment.GetProperty("filename").GetString() == "clip.mp4" &&
               attachment.GetProperty("description").GetString() == "Malik uploaded a clip from Battlefield™-6.",
            "The attachment description must contain matching uploader attribution.");
        Assert(root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() == 0,
            "Uploader-controlled text must not enable Discord mentions.");
    }

    Assert(SettingsForm.TryParseCompressionTarget("95 MB", out var compression95) && compression95 == 95,
        "The compression selector must accept a value with the MB suffix.");
    Assert(SettingsForm.TryParseCompressionTarget("37", out var compression37) && compression37 == 37,
        "The compression selector must preserve arbitrary values in its supported range.");
    foreach (var invalidCompression in new[] { "", "0", "101", "-5", "7.5 MB", "5 MB extra", "1 000", "abc" })
    {
        Assert(!SettingsForm.TryParseCompressionTarget(invalidCompression, out var parsedCompression) &&
               parsedCompression == 0,
            $"The compression selector must reject ambiguous value '{invalidCompression}'.");
    }
    Assert(ReferenceEquals(ClipCordTheme.InterfaceFont(10f), ClipCordTheme.InterfaceFont(10f)),
        "ClipCord fonts must be cached instead of allocating GDI font handles for every control.");
    Assert(SettingsForm.GetDesignedOpeningSize(SettingsPage.Activity, 144) == new Size(1564, 1256),
        "Activity must preserve its approved width and add only the top-navigation height.");
    Assert(SettingsForm.GetDesignedOpeningSize(SettingsPage.Settings, 96) == new Size(1080, 886),
        "Settings must preserve its approved width and add only the top-navigation height.");
    Assert(SettingsForm.GetDesignedOpeningSize(SettingsPage.Gallery, 96) == new Size(1100, 890),
        "The Gallery page must share the expanded top-navigation design size.");
    Assert(SettingsForm.GetDesignedOpeningSize(SettingsPage.About, 96) == new Size(1100, 890) &&
           SettingsForm.GetDesignedOpeningSize(SettingsPage.About, 144) == new Size(1650, 1236) &&
           SettingsForm.GetDesignedOpeningSize(SettingsPage.About, 192) == new Size(2200, 1582),
        "About must preserve its approved opening size at 100%, 150%, and 200% DPI.");
    Assert(SettingsForm.GetScaledMinimumSize(144) == new Size(1350, 975),
        "The resize floor must scale with the active Windows DPI.");
    AssertAboutPageSupport(Path.Combine(temporaryRoot, "about-support"));
    AssertBrandedActivityScrollHost();

    AssertActivityHistory(Path.Combine(temporaryRoot, "activity-history"));
    AssertSettingsFormLayout(new AppSettings(
        gameArchiveRoot,
        "https://discord.com/api/" + "webhooks/123456/test-token",
        true,
        AppSettings.DefaultCompressionTargetMb,
        "Malik",
        true));

    TraceSmokeStep("State recovery and readiness");
    var recoveryRoot = Path.Combine(temporaryRoot, "safe-baseline-recovery");
    var recoveryClips = Path.Combine(recoveryRoot, "clips");
    var recoveryStateDirectory = Path.Combine(recoveryRoot, "state");
    Directory.CreateDirectory(recoveryClips);
    Directory.CreateDirectory(recoveryStateDirectory);
    var pendingMovePath = Path.Combine(recoveryClips, "uploaded-but-not-moved.mp4");
    var pendingLocalOnlyMovePath = Path.Combine(recoveryClips, "local-only-but-not-moved.mp4");
    await File.WriteAllBytesAsync(pendingMovePath, new byte[] { 9, 8, 7, 6 });
    await File.WriteAllBytesAsync(pendingLocalOnlyMovePath, new byte[] { 6, 7, 8, 9 });
    var recoveryStatePath = Path.Combine(recoveryStateDirectory, "state.json");
    var recoveryMarkerPath = Path.Combine(recoveryStateDirectory, ".safe-baseline-required");
    var recoveryStore = new WatchStateStore(recoveryStatePath, recoveryMarkerPath);
    var pendingEditedId = Guid.NewGuid();
    recoveryStore.Save(new WatchState
    {
        Version = 2,
        ClipsFolder = recoveryClips,
        PendingMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pendingMovePath },
        PendingLocalOnlyMoves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pendingLocalOnlyMovePath },
        PendingEditedUploads =
        [
            new PendingEditedClipDisposition
            {
                Id = pendingEditedId,
                ClipsFolder = recoveryClips,
                EditedPath = Path.Combine(recoveryClips, ".clipcord-editing", "pending", "edit.mp4"),
                DestinationPath = Path.Combine(recoveryClips, "uploaded", "Game", "edit.mp4"),
                OriginalLocalOnlyPath = Path.Combine(recoveryClips, "local-only", "Game", "source.mp4"),
                EditedContentHash = "edited-hash",
                OriginalContentHash = "original-hash"
            }
        ]
    });
    await File.WriteAllTextAsync(recoveryMarkerPath, DateTime.UtcNow.ToString("O"));
    var recoveredState = await recoveryStore.LoadOrInitializeAsync(
        recoveryClips,
        _ => { },
        CancellationToken.None);
    Assert(
        recoveredState.PendingMoves.Contains(pendingMovePath),
        "A forced safe-baseline rebuild must preserve readable pending moves.");
    Assert(
        recoveredState.PendingLocalOnlyMoves.Contains(pendingLocalOnlyMovePath),
        "A forced safe-baseline rebuild must preserve readable local-only pending moves.");
    Assert(recoveredState.PendingEditedUploads.Single().Id == pendingEditedId,
        "A forced safe-baseline rebuild must preserve a confirmed edited upload for no-repost recovery.");
    Assert(
        recoveredState.KnownContentHashes.Count == 2,
        "A forced safe-baseline rebuild must hash existing clips.");
    Assert(!File.Exists(recoveryMarkerPath), "The recovery marker must clear after the baseline is durably saved.");

    var readinessPath = Path.Combine(temporaryRoot, "readiness.mp4");
    await File.WriteAllBytesAsync(readinessPath, new byte[] { 1, 2, 3, 4 });
    File.SetLastWriteTimeUtc(readinessPath, DateTime.UtcNow.AddMinutes(-1));
    var readinessTracker = new FileReadinessTracker();
    var firstObservationAt = DateTime.UtcNow;
    var firstObservation = readinessTracker.Observe(new FileInfo(readinessPath), firstObservationAt);
    var stableObservation = readinessTracker.Observe(
        new FileInfo(readinessPath),
        firstObservationAt.AddSeconds(11));
    Assert(!firstObservation.IsReady, "A file must not be ready on its first observation.");
    Assert(stableObservation.IsReady, "An unchanged readable file must become ready after the stability window.");

    var changingPath = Path.Combine(temporaryRoot, "changing.mp4");
    await File.WriteAllBytesAsync(changingPath, new byte[] { 1, 2, 3 });
    File.SetLastWriteTimeUtc(changingPath, DateTime.UtcNow.AddMinutes(-1));
    var changingTracker = new FileReadinessTracker();
    var changingObservedAt = DateTime.UtcNow;
    changingTracker.Observe(new FileInfo(changingPath), changingObservedAt);
    await File.AppendAllTextAsync(changingPath, "more data");
    var changedObservation = changingTracker.Observe(
        new FileInfo(changingPath),
        changingObservedAt.AddSeconds(11));
    Assert(!changedObservation.IsReady, "A file whose length changed between observations must not be ready.");

    var youngPath = Path.Combine(temporaryRoot, "young.mp4");
    await File.WriteAllBytesAsync(youngPath, new byte[] { 4, 3, 2, 1 });
    var youngLastWrite = File.GetLastWriteTimeUtc(youngPath);
    var youngTracker = new FileReadinessTracker();
    youngTracker.Observe(new FileInfo(youngPath), youngLastWrite);
    var youngObservation = youngTracker.Observe(new FileInfo(youngPath), youngLastWrite.AddSeconds(11));
    var oldEnoughObservation = youngTracker.Observe(new FileInfo(youngPath), youngLastWrite.AddSeconds(21));
    Assert(!youngObservation.IsReady, "A stable file younger than 20 seconds must not be ready.");
    Assert(oldEnoughObservation.IsReady, "A stable file older than 20 seconds must become ready.");

    var sharedReaderPath = Path.Combine(temporaryRoot, "shared-reader.mp4");
    await File.WriteAllBytesAsync(sharedReaderPath, new byte[] { 8, 7, 6, 5 });
    File.SetLastWriteTimeUtc(sharedReaderPath, DateTime.UtcNow.AddMinutes(-1));
    var sharedReaderTracker = new FileReadinessTracker();
    var sharedReaderObservedAt = DateTime.UtcNow;
    sharedReaderTracker.Observe(new FileInfo(sharedReaderPath), sharedReaderObservedAt);
    using (var reader = new FileStream(sharedReaderPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        var sharedReaderResult = sharedReaderTracker.Observe(
            new FileInfo(sharedReaderPath),
            sharedReaderObservedAt.AddSeconds(11));
        Assert(sharedReaderResult.IsReady, "Another reader must not permanently block a completed clip.");
    }

    var lockedPath = Path.Combine(temporaryRoot, "locked.mp4");
    await File.WriteAllBytesAsync(lockedPath, new byte[] { 5, 6, 7, 8 });
    File.SetLastWriteTimeUtc(lockedPath, DateTime.UtcNow.AddMinutes(-1));
    var lockedTracker = new FileReadinessTracker();
    var lockedObservedAt = DateTime.UtcNow;
    lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt);
    FileReadinessResult lockedResult;
    FileReadinessResult secondLockedResult;
    FileReadinessResult thirdLockedResult;
    using (var lockedStream = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
    {
        lockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(11));
        secondLockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(22));
        thirdLockedResult = lockedTracker.Observe(new FileInfo(lockedPath), lockedObservedAt.AddSeconds(43));
    }
    Assert(!lockedResult.IsReady, "A file with an active writer must not be ready.");
    Assert(
        lockedResult.NextCheckUtc > lockedObservedAt.AddSeconds(11),
        "A locked file must receive a future retry time.");
    Assert(
        secondLockedResult.NextCheckUtc - lockedObservedAt.AddSeconds(22) >= TimeSpan.FromSeconds(20),
        "Repeated lock failures must increase the readiness backoff.");
    Assert(
        thirdLockedResult.ConsecutiveOpenFailures == FileReadinessTracker.StuckLogThreshold,
        "A repeatedly writer-locked file must reach the explicit stuck-log threshold.");

    var identityPath = Path.Combine(temporaryRoot, "identity.mp4");
    await File.WriteAllBytesAsync(identityPath, new byte[] { 10, 20, 30, 40, 50 });
    var originalHash = await ContentIdentity.ComputeSha256Async(identityPath, CancellationToken.None);
    var renamedIdentityPath = Path.Combine(temporaryRoot, "renamed-identity.mp4");
    File.Move(identityPath, renamedIdentityPath);
    File.SetLastWriteTimeUtc(renamedIdentityPath, DateTime.UtcNow.AddHours(-1));
    var renamedHash = await ContentIdentity.ComputeSha256Async(renamedIdentityPath, CancellationToken.None);
    Assert(originalHash == renamedHash, "Content identity must survive path and timestamp changes.");

    TraceSmokeStep("Webhook validation and redaction");
    var apiRoot = "https://discord.com/api/";
    var unversionedWebhook = apiRoot + "webhooks/" + "123456" + "/test-token";
    var versionedWebhook = apiRoot + "v10/webhooks/" + "123456" + "/test-token";
    Assert(WebhookValidation.IsDiscordWebhook(unversionedWebhook), "An unversioned Discord webhook must be accepted.");
    Assert(WebhookValidation.IsDiscordWebhook(versionedWebhook), "A versioned Discord webhook must be accepted.");
    Assert(
        !WebhookValidation.IsDiscordWebhook("https://example.com/api/v10/webhooks/123456/test-token"),
        "A non-Discord webhook host must be rejected.");
    Assert(
        !WebhookValidation.IsDiscordWebhook(apiRoot + "v10/channels/123456"),
        "A non-webhook Discord API path must be rejected.");
    Assert(
        !WebhookValidation.IsDiscordWebhook("https://user@discord.com/api/v10/webhooks/123456/test-token"),
        "A webhook URL with userinfo must be rejected.");
    Assert(
        !WebhookValidation.IsDiscordWebhook("https://discord.com:444/api/v10/webhooks/123456/test-token"),
        "A webhook URL with a non-default port must be rejected.");
    Assert(
        !WebhookValidation.IsDiscordWebhook(versionedWebhook + "#fragment"),
        "A webhook URL with a fragment must be rejected.");
    Assert(
        DiscordWebhookClient.WithWait(versionedWebhook) == versionedWebhook + "?wait=true",
        "Webhook requests must add wait=true as a real query parameter.");
    var webhookWithQuery = new Uri(DiscordWebhookClient.WithWait(versionedWebhook + "?thread_id=42&wait=false"));
    Assert(
        webhookWithQuery.Query == "?thread_id=42&wait=true" && string.IsNullOrEmpty(webhookWithQuery.Fragment),
        "Webhook requests must preserve existing query parameters and replace an existing wait value.");
    using (var webhookHandler = DiscordWebhookClient.CreateHandler())
    {
        Assert(!webhookHandler.AllowAutoRedirect,
            "Discord uploads must not automatically follow redirects away from the validated webhook URL.");
    }
    await AssertStartedWebhookPostCancellationAsync(temporaryRoot, versionedWebhook);
    using (var oversizedResponse = new ByteArrayContent(
               Enumerable.Repeat((byte)'x', DiscordWebhookClient.MaximumResponseBytes + 20).ToArray()))
    {
        var responseText = await DiscordWebhookClient.ReadResponseTextAsync(
            oversizedResponse,
            CancellationToken.None);
        Assert(
            responseText.Length == DiscordWebhookClient.MaximumResponseBytes + " [response truncated]".Length &&
            responseText.EndsWith(" [response truncated]", StringComparison.Ordinal),
            "Discord response bodies must be read through a bounded, visibly truncated path.");
    }

    SensitiveDataRedactor.RegisterSecret(unversionedWebhook);
    var redactedExactSecret = SensitiveDataRedactor.Redact("Request failed: " + unversionedWebhook);
    var redactedVersionedSecret = SensitiveDataRedactor.Redact("Request failed: " + versionedWebhook);
    Assert(!redactedExactSecret.Contains("test-token"), "A registered webhook must be removed from logs.");
    Assert(!redactedVersionedSecret.Contains("test-token"), "A versioned webhook must be removed from logs.");
    Assert(
        redactedVersionedSecret.Contains("[REDACTED DISCORD WEBHOOK]"),
        "Webhook redaction must leave a useful placeholder.");

    TraceSmokeStep("Compression planning");
    var compressionTargets = CompressionTargetPlanner.Build(25);
    Assert(compressionTargets[0] == 25, "Compression fallback must begin at the configured target.");
    Assert(compressionTargets.Contains(9), "Compression fallback must include the lower-limit target.");
    Assert(
        compressionTargets.Zip(compressionTargets.Skip(1)).All(pair => pair.First > pair.Second),
        "Compression fallback targets must decrease strictly.");
    Assert(
        AppSettings.Empty.CompressionTargetMb == 95,
        "New settings must default to a 95 MB compression target.");
    var defaultCompressionTargets = CompressionTargetPlanner.Build(AppSettings.DefaultCompressionTargetMb);
    Assert(defaultCompressionTargets[0] == 95, "Default compression fallback must begin at 95 MB.");
    Assert(defaultCompressionTargets.Contains(9), "Default compression fallback must still reach 9 MB.");
    var hourLongTargets = CompressionTargetPlanner.BuildAchievable(95, TimeSpan.FromMinutes(60));
    Assert(hourLongTargets.Count == 0,
        "An hour-long clip must reject every target that cannot sustain the minimum video bitrate before encoding.");
    var twentyMinuteTargets = CompressionTargetPlanner.BuildAchievable(95, TimeSpan.FromMinutes(20));
    Assert(twentyMinuteTargets.SequenceEqual([95, 47]),
        $"A twenty-minute clip must skip futile lower targets; got [{string.Join(", ", twentyMinuteTargets)}].");
    Assert(
        CompressionTargetPlanner.TryCreateBitrates(TimeSpan.FromMinutes(20), 47, out var achievableBitrates) &&
        achievableBitrates.VideoKbps >= CompressionTargetPlanner.MinimumVideoKbps &&
        !CompressionTargetPlanner.TryCreateBitrates(TimeSpan.FromMinutes(20), 23, out _),
        "Compression feasibility must distinguish the last achievable target from the first impossible one.");
    Assert(
        CompressionTargetPlanner.TryCreateBitrates(TimeSpan.FromSeconds(29.3), 95, out var shortClipBitrates),
        "A short clip must produce a valid compression bitrate plan.");
    var compressionLog = DiscordWebhookClient.BuildCompressionLogMessage(
        "Battlefield.mp4",
        183_955_215,
        26_214_400,
        95,
        shortClipBitrates);
    Assert(
        compressionLog ==
        "Compression complete for Battlefield.mp4: 175.4 MB -> 25.0 MB (85.7% smaller; 95 MB target ceiling; 6000 kbps video / 96 kbps audio).",
        $"Compression logs must report the actual before/after sizes and encoder plan; got '{compressionLog}'.");
    var untrustedFfmpegFolder = Directory.CreateDirectory(Path.Combine(temporaryRoot, "path-ffmpeg"));
    var untrustedFfmpegPath = Path.Combine(untrustedFfmpegFolder.FullName, "ffmpeg.exe");
    await File.WriteAllTextAsync(untrustedFfmpegPath, "not an executable");
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", untrustedFfmpegFolder.FullName);
        Assert(
            !string.Equals(
                FfmpegCompressor.FindExecutable(),
                untrustedFfmpegPath,
                StringComparison.OrdinalIgnoreCase),
            "FFmpeg discovery must not execute an untrusted PATH entry.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
    }

    TraceSmokeStep("Discord-aware controller lifecycle");
    var detectorResponses = new ConcurrentQueue<bool>(
        [true, false, false, true, false, false, false, true]);
    var watcherStarts = 0;
    var watcherCancellations = 0;
    var cancellationsAtDebounceReset = -1;
    var detectorCalls = 0;
    bool SimulatedDiscordDetector()
    {
        var call = Interlocked.Increment(ref detectorCalls);
        var response = detectorResponses.TryDequeue(out var queuedResponse) ? queuedResponse : true;
        if (call == 4)
        {
            cancellationsAtDebounceReset = Volatile.Read(ref watcherCancellations);
        }
        return response;
    }

    async Task SimulatedWatcher(
        AppSettings ignoredSettings,
        Action<string> ignoredStatus,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref watcherStarts);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref watcherCancellations);
            throw;
        }
    }

    var controllerOptions = new DiscordControllerOptions(
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(5),
        3,
        TimeSpan.FromSeconds(1));
    var controller = new DiscordAwareController(
        AppSettings.Empty,
        _ => { },
        SimulatedDiscordDetector,
        SimulatedWatcher,
        controllerOptions);
    await WaitUntilAsync(
        () => Volatile.Read(ref watcherCancellations) >= 1,
        TimeSpan.FromSeconds(2),
        "The watcher was not cancelled after three consecutive absent polls.");
    Assert(cancellationsAtDebounceReset == 0, "Two absent polls must not stop the watcher.");
    await WaitUntilAsync(
        () => Volatile.Read(ref watcherStarts) >= 2,
        TimeSpan.FromSeconds(2),
        "The watcher did not restart after Discord returned.");
    controller.Dispose();
    Assert(
        Volatile.Read(ref watcherCancellations) == 2,
        "Controller disposal must cancel and await the active watcher.");

    var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var delayedWatcherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task DelayedCleanupWatcher(
        AppSettings ignoredSettings,
        Action<string> ignoredStatus,
        CancellationToken cancellationToken)
    {
        delayedWatcherStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cleanupStarted.TrySetResult();
            await releaseCleanup.Task;
            throw;
        }
    }

    var delayedCleanupController = new DiscordAwareController(
        AppSettings.Empty,
        _ => { },
        () => true,
        DelayedCleanupWatcher,
        controllerOptions);
    await delayedWatcherStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var stopTask = delayedCleanupController.StopAsync();
    await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(!stopTask.IsCompleted,
        "Awaitable controller shutdown must not finish while the old watcher is still cleaning up.");
    releaseCleanup.TrySetResult();
    await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
    delayedCleanupController.Dispose();

    TraceSmokeStep("Local-only worker lifecycle");
    await AssertLocalOnlyWorkerAsync(temporaryRoot);

    TraceSmokeStep("Manual Local-only edit upload transaction");
    AssertClipEditProcessorContracts(temporaryRoot);
    await AssertEditedClipUploadTransactionsAsync(temporaryRoot);

    TraceSmokeStep("Update checker");
    await UpdateCheckerTests.RunAsync(temporaryRoot);
    TraceSmokeStep("Update download service");
    await UpdateDownloadServiceTests.RunAsync(temporaryRoot);

    Console.WriteLine("All smoke tests passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
finally
{
    try
    {
        if (temporaryRoot is not null && Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Smoke-test cleanup failed: {exception}");
        Environment.ExitCode = 1;
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void CreateDirectoryJunction(string linkPath, string targetPath)
{
    var startInfo = new ProcessStartInfo("cmd.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("mklink");
    startInfo.ArgumentList.Add("/J");
    startInfo.ArgumentList.Add(linkPath);
    startInfo.ArgumentList.Add(targetPath);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Windows did not start the junction test helper.");
    Assert(process.WaitForExit(5000), "The junction test helper exceeded its five-second deadline.");
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            "The mandatory local-only junction test could not be prepared: " +
            process.StandardError.ReadToEnd());
    }
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
{
    var deadline = DateTime.UtcNow.Add(timeout);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(failureMessage);
        await Task.Delay(TimeSpan.FromMilliseconds(10));
    }
}

static void WaitForUiCondition(Func<bool> condition, TimeSpan timeout, string failureMessage)
{
    var deadline = DateTime.UtcNow.Add(timeout);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(failureMessage);
        Application.DoEvents();
        Thread.Sleep(10);
    }
}

static async Task AssertLocalOnlyWorkerAsync(string temporaryRoot)
{
    var root = Path.Combine(temporaryRoot, "local-only-worker");
    var clipsFolder = Path.Combine(root, "clips");
    var stateFolder = Path.Combine(root, "state");
    Directory.CreateDirectory(clipsFolder);
    Directory.CreateDirectory(stateFolder);
    var store = new WatchStateStore(
        Path.Combine(stateFolder, "state.json"),
        Path.Combine(stateFolder, ".safe-baseline-required"));
    await store.LoadOrInitializeAsync(clipsFolder, _ => { }, CancellationToken.None);

    const string clipName = "Battlefield__2026-08-05__12-00-00.mp4";
    var sourcePath = Path.Combine(clipsFolder, clipName);
    await File.WriteAllBytesAsync(sourcePath, [1, 3, 3, 7]);
    File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddMinutes(-1));
    var expectedDestination = Path.Combine(clipsFolder, "local-only", "Battlefield", clipName);
    var statuses = new ConcurrentQueue<string>();
    var settings = new AppSettings(
        clipsFolder,
        string.Empty,
        false,
        AppSettings.DefaultCompressionTargetMb,
        AppSettings.DefaultUploaderName,
        false);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using var activityHistory = new ActivityHistoryStore(string.Empty);
    var worker = new UploaderWorker(
        settings,
        statuses.Enqueue,
        store,
        () => throw new InvalidOperationException(
            "Local-only mode must not construct a Discord webhook client."),
        activityHistory);
    var workerTask = worker.RunAsync(cancellation.Token);
    try
    {
        await WaitUntilAsync(
            () => File.Exists(expectedDestination),
            TimeSpan.FromSeconds(16),
            "Local-only mode did not archive a ready clip without a webhook.");
    }
    finally
    {
        cancellation.Cancel();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    Assert(!File.Exists(sourcePath), "A local-only clip must leave the watched folder after it is archived.");
    Assert(
        statuses.Any(status => status.Contains("local-only", StringComparison.OrdinalIgnoreCase)),
        "Local-only processing must publish an explicit watcher status.");
    var state = await store.LoadOrInitializeAsync(clipsFolder, _ => { }, CancellationToken.None);
    Assert(state.LocalOnlyContentHashes.Count == 1,
        "A local-only clip must receive a persisted local-only content identity.");
    Assert(state.UploadedContentHashes.Count == 0,
        "A local-only clip must never be marked as uploaded.");
    Assert(state.PendingLocalOnlyMoves.Count == 0,
        "A completed local-only archive move must clear its durable pending entry.");
    var activity = activityHistory.GetSnapshot().Entries.Single(entry => entry.FileName == clipName);
    Assert(activity.State == ClipActivityState.Archived &&
           activity.Route == ClipActivityRoute.LocalOnly &&
           activity.AttemptCount == 1 &&
           activity.CurrentPath == expectedDestination,
        "Local-only processing must publish its final route and archive location to Activity.");

    const string pendingClipName = "Apex Legends__2026-08-05__12-30-00.mp4";
    var pendingSourcePath = Path.Combine(clipsFolder, pendingClipName);
    await File.WriteAllBytesAsync(pendingSourcePath, [2, 4, 6, 8]);
    var pendingHash = await ContentIdentity.ComputeSha256Async(pendingSourcePath, CancellationToken.None);
    state.KnownContentHashes.Add(pendingHash);
    state.LocalOnlyContentHashes.Add(pendingHash);
    state.PendingLocalOnlyMoves.Add(pendingSourcePath);
    store.Save(state);

    var uploadsEnabledSettings = settings with
    {
        WebhookUrl = "https://discord.com/api/webhooks/123456/test-token",
        UploadToDiscord = true
    };
    var recoveredDestination = Path.Combine(clipsFolder, "local-only", "Apex Legends", pendingClipName);
    using var recoveryCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var recoveryWorker = new UploaderWorker(uploadsEnabledSettings, _ => { }, store);
    var recoveryTask = recoveryWorker.RunAsync(recoveryCancellation.Token);
    try
    {
        await WaitUntilAsync(
            () => File.Exists(recoveredDestination),
            TimeSpan.FromSeconds(10),
            "A persisted local-only move changed destination after Discord uploads were enabled.");
    }
    finally
    {
        recoveryCancellation.Cancel();
        try
        {
            await recoveryTask;
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
        }
    }
    Assert(
        !File.Exists(Path.Combine(clipsFolder, "uploaded", "Apex Legends", pendingClipName)),
        "A recovered local-only move must never be redirected into the uploaded archive.");
}

static void AssertActivityHistory(string root)
{
    Directory.CreateDirectory(root);
    var spacedFolder = @"C:\Users\Test User\Videos\SteelSeries Moments\uploaded";
    var openFolderStart = ActivityView.CreateOpenFolderStartInfo(spacedFolder);
    Assert(openFolderStart.FileName == "explorer.exe" &&
           openFolderStart.UseShellExecute &&
           openFolderStart.ArgumentList.SequenceEqual([spacedFolder]),
        "Opening a folder must pass a space-containing path as one Explorer argument.");
    var spacedFile = Path.Combine(spacedFolder, "Battlefield™-6", "clip, 日本語 one.mp4");
    var selectFileStart = ActivityView.CreateSelectFileStartInfo(spacedFile);
    Assert(selectFileStart.FileName == "explorer.exe" &&
           selectFileStart.UseShellExecute &&
           selectFileStart.ArgumentList.Count == 0 &&
           selectFileStart.Arguments == $"/select,\"{spacedFile}\"",
        "Showing a clip must preserve spaces, commas, and Unicode while keeping Explorer's /select switch outside the quoted path.");
    var rejectedQuotedSelectPath = false;
    try
    {
        ActivityView.CreateSelectFileStartInfo(spacedFile + "\" /root,C:\\Windows");
    }
    catch (ArgumentException)
    {
        rejectedQuotedSelectPath = true;
    }
    Assert(rejectedQuotedSelectPath,
        "The Explorer selection helper must reject an embedded quote even if a future caller omits its File.Exists gate.");

    var historyPath = Path.Combine(root, "activity.json");
    var sourcePath = Path.Combine(root, "Battlefield™-6__2026-08-06__14-37-13.mp4");
    using (var store = new ActivityHistoryStore(historyPath))
    {
        using var subscription = store.Subscribe(new SynchronizationContext(), _ => { });
        Assert(store.SubscriptionCount == 1, "Activity subscribers must be tracked while attached.");
        store.Transition(new ClipActivityUpdate(sourcePath, ClipActivityState.Discovered, OriginalBytes: 183_955_215));
        store.Transition(new ClipActivityUpdate(sourcePath, ClipActivityState.Waiting, Detail: "Waiting for a stable file."));
        store.Transition(new ClipActivityUpdate(sourcePath, ClipActivityState.Hashing));
        store.Transition(new ClipActivityUpdate(sourcePath, ClipActivityState.Queued));
        store.Transition(new ClipActivityUpdate(sourcePath, ClipActivityState.Uploading, IncrementAttempt: true));
        store.Transition(new ClipActivityUpdate(
            sourcePath,
            ClipActivityState.Compressing,
            CompressedBytes: 26_214_400,
            CompressionTargetMb: 95,
            VideoKbps: 6000,
            AudioKbps: 96));
        store.Transition(new ClipActivityUpdate(sourcePath, ClipActivityState.Completed, Route: ClipActivityRoute.Uploaded));
        var archivedPath = Path.Combine(root, "uploaded", "Battlefield™-6", Path.GetFileName(sourcePath));
        store.Transition(new ClipActivityUpdate(
            sourcePath,
            ClipActivityState.Archived,
            CurrentPath: archivedPath,
            Route: ClipActivityRoute.Uploaded,
            Detail: "Discord upload and local archive completed."));

        var entry = store.GetSnapshot().Entries.Single();
        Assert(entry.State == ClipActivityState.Archived &&
               entry.AttemptCount == 1 &&
               entry.GameName == "Battlefield™-6" &&
               entry.OriginalBytes == 183_955_215 &&
               entry.CompressedBytes == 26_214_400 &&
               entry.VideoKbps == 6000,
            "Activity transitions must preserve identity, attempts, parsed game, and compression metrics.");
        Assert(ActivityView.BuildDetail(entry).Contains("175.4 MB -> 25.0 MB (85.7% smaller)", StringComparison.Ordinal),
            "Activity details must expose the actual before/after compression result.");

        subscription.Dispose();
        Assert(store.SubscriptionCount == 0, "Closing an Activity subscriber must detach it exactly once.");
    }

    using (var reloaded = new ActivityHistoryStore(historyPath))
    {
        Assert(reloaded.GetSnapshot().Entries is [var persisted] &&
               persisted.State == ClipActivityState.Archived &&
               persisted.AttemptCount == 1,
            "Bounded Activity history must survive restart.");

        Parallel.For(0, 140, index =>
        {
            reloaded.Transition(new ClipActivityUpdate(
                Path.Combine(root, $"Concurrent Game__2026-08-06__14-37-{index % 60:00}-{index}.mp4"),
                ClipActivityState.Discovered,
                OriginalBytes: index + 1));
        });
        var snapshot = reloaded.GetSnapshot();
        Assert(snapshot.Entries.Count == ActivityHistoryStore.MaximumEntries &&
               snapshot.Entries.Select(entry => entry.Id).Distinct().Count() == ActivityHistoryStore.MaximumEntries,
            "Concurrent workers must retain a bounded set of distinct Activity entries.");

        var secretPath = Path.Combine(root, "secret.mp4");
        reloaded.Transition(new ClipActivityUpdate(
            secretPath,
            ClipActivityState.Failed,
            Error: $"Could not move {secretPath}; rejected https://discord.com/api/webhooks/123456/never-persist-this-token"));
        var redactedError = reloaded.GetSnapshot().Entries.Single(entry => entry.SourcePath == secretPath).Error;
        Assert(redactedError is not null && !redactedError.Contains(root, StringComparison.OrdinalIgnoreCase),
            "Concise Activity errors must not repeat full local clip paths in the UI.");
        var persistedJson = File.ReadAllText(historyPath);
        Assert(!persistedJson.Contains("never-persist-this-token", StringComparison.Ordinal) &&
               persistedJson.Contains("[REDACTED DISCORD WEBHOOK]", StringComparison.Ordinal),
            "Activity persistence must redact webhook credentials before writing to disk.");
        Assert(!File.Exists(historyPath + ".tmp"), "Atomic Activity writes must not leave a temporary file behind.");
    }

    foreach (var state in Enum.GetValues<ClipActivityState>())
    {
        var presentation = ActivityView.GetPresentation(new ClipActivityEntry
        {
            Id = Guid.NewGuid(),
            FileName = "clip.mp4",
            SourcePath = sourcePath,
            State = state
        });
        Assert(!string.IsNullOrWhiteSpace(presentation.Label),
            $"Activity state {state} must have a deterministic UI label.");
    }

    using (var lifecycle = new ActivityHistoryStore(string.Empty))
    {
        var retryPath = Path.Combine(root, "retry.mp4");
        lifecycle.Transition(new ClipActivityUpdate(
            retryPath,
            ClipActivityState.Retrying,
            OriginalBytes: 20_000_000,
            Error: "Discord returned HTTP 413",
            CompressedBytes: 10_000_000,
            CompressionTargetMb: 25,
            VideoKbps: 3000));
        lifecycle.Transition(new ClipActivityUpdate(
            retryPath,
            ClipActivityState.Uploading,
            IncrementAttempt: true,
            ResetCompression: true));
        var retrying = lifecycle.GetSnapshot().Entries.Single();
        Assert(retrying.Error == "Discord returned HTTP 413" &&
               retrying.CompressedBytes is null &&
               retrying.CompressionTargetMb is null,
            "A retry must preserve its useful error while explicitly clearing stale compression metrics.");
        lifecycle.Transition(new ClipActivityUpdate(
            retryPath,
            ClipActivityState.Compressing,
            CompressionTargetMb: 9,
            VideoKbps: 2200,
            AudioKbps: 96,
            ResetCompression: true));
        var recompressing = lifecycle.GetSnapshot().Entries.Single();
        Assert(recompressing.CompressedBytes is null &&
               recompressing.CompressionTargetMb == 9 &&
               recompressing.VideoKbps == 2200,
            "A new compression attempt must clear the prior output while retaining its new encoder plan.");
        lifecycle.Transition(new ClipActivityUpdate(
            retryPath,
            ClipActivityState.Completed,
            ClearError: true));
        Assert(lifecycle.GetSnapshot().Entries.Single().Error is null,
            "A terminal success must explicitly clear the previous retry error.");

        var recoveryPath = Path.Combine(root, "reused-name.mp4");
        var first = lifecycle.Transition(new ClipActivityUpdate(
            recoveryPath,
            ClipActivityState.Archived,
            OriginalBytes: 500_000_000,
            Route: ClipActivityRoute.Uploaded));
        var recovered = lifecycle.Transition(new ClipActivityUpdate(
            recoveryPath,
            ClipActivityState.Archived,
            OriginalBytes: 7_000_000,
            Route: ClipActivityRoute.LocalOnly));
        Assert(first.Id != recovered.Id &&
               recovered.OriginalBytes == 7_000_000 &&
               recovered.CreatedUtc >= first.CreatedUtc,
            "Recovery-first archive updates must not merge a reused path into an older terminal clip.");

        var redundant = lifecycle.Transition(new ClipActivityUpdate(
            recoveryPath,
            ClipActivityState.Archived,
            Route: ClipActivityRoute.LocalOnly,
            Detail: "Recovered an already-finished pending move.",
            ReuseTerminalEntry: true));
        Assert(redundant.Id == recovered.Id &&
               lifecycle.GetSnapshot().Entries.Count(entry => entry.SourcePath == recoveryPath) == 2,
            "A redundant recovery with no moved file must reuse the latest terminal row instead of creating a duplicate.");
    }

    using (var concurrent = new ActivityHistoryStore(string.Empty))
    {
        Parallel.Invoke(
            () => RecordConcurrentActivities(concurrent, root, "worker-a"),
            () => RecordConcurrentActivities(concurrent, root, "worker-b"));
        var entries = concurrent.GetSnapshot().Entries;
        Assert(entries.Count == ActivityHistoryStore.MaximumEntries &&
               entries.Select(entry => entry.Id).Distinct().Count() == ActivityHistoryStore.MaximumEntries &&
               entries.All(entry => entry.State == ClipActivityState.Queued),
            "Two workers must safely publish complete transitions while history remains bounded.");
    }
}

static void AssertBrandedActivityScrollHost()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new Form
            {
                ClientSize = new Size(320, 220),
                ShowInTaskbar = false
            };
            using var host = new BrandedScrollHost
            {
                Location = new Point(40, 20),
                Size = new Size(260, 160)
            };
            using var content = new ActivityListPanel
            {
                BackColor = ClipCordTheme.Shell
            };
            Button? lastButton = null;
            var cardLayoutCount = 0;
            for (var index = 0; index < 20; index++)
            {
                var card = new Panel
                {
                    Height = 54,
                    Margin = new Padding(0, 0, 0, 6)
                };
                card.Layout += (_, _) => cardLayoutCount++;
                if (index == 19)
                {
                    lastButton = new Button
                    {
                        Text = "Last activity action",
                        Location = new Point(10, 10),
                        Size = new Size(150, 32)
                    };
                    card.Controls.Add(lastButton);
                }
                content.Controls.Add(card);
            }
            host.Content = content;
            var outsideButton = new Button
            {
                Text = "Outside action",
                Location = new Point(0, 185),
                Size = new Size(120, 30)
            };
            form.Controls.Add(host);
            form.Controls.Add(outsideButton);
            form.Show();
            Application.DoEvents();
            host.RefreshContentLayout();

            Assert(!host.AutoScroll && host.HasOverflow,
                "Activity must use the branded scroll host instead of a native Windows scrollbar.");
            var initialThumb = host.ScrollThumbBounds;
            Assert(!initialThumb.IsEmpty && host.ClientRectangle.Contains(initialThumb),
                $"The branded scrollbar thumb must begin inside its viewport; thumb={initialThumb}, viewport={host.ClientRectangle}.");
            Assert(host.GetTrackHitBounds().Width >= 20 && host.GetThumbHitBounds().Width >= 20,
                "The branded scrollbar must keep its slim artwork while exposing a usable pointer target.");

            cardLayoutCount = 0;
            for (var index = 0; index < 20; index++) host.RefreshContentLayout();
            Assert(cardLayoutCount == 0,
                $"No-op scrollbar refreshes must not relayout every activity card; observed {cardLayoutCount} layouts.");

            host.ScrollBy(100);
            Assert(host.ScrollOffset == 100 && host.ScrollThumbBounds.Top > initialThumb.Top && content.Top == -100,
                "The branded scrollbar must move its thumb and activity content together.");
            host.RefreshContentLayout(anchorAdjustment: 54);
            Assert(host.ScrollOffset == 154,
                "A new activity inserted above the viewport must preserve the user's reading position.");

            host.ScrollBy(-int.MaxValue);
            var onMouseWheel = typeof(BrandedScrollHost).GetMethod(
                "OnMouseWheel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            onMouseWheel.Invoke(host, [new MouseEventArgs(MouseButtons.None, 0, 0, 0, -120)]);
            Assert(host.ScrollOffset > 0, "Mouse-wheel input must scroll the branded activity viewport.");

            host.ScrollBy(-int.MaxValue);
            var onKeyDown = typeof(BrandedScrollHost).GetMethod(
                "OnKeyDown",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            onKeyDown.Invoke(host, [new KeyEventArgs(Keys.End)]);
            var maximumOffset = host.ScrollOffset;
            Assert(maximumOffset > 0,
                "End must reach the bottom of the branded activity viewport.");
            onKeyDown.Invoke(host, [new KeyEventArgs(Keys.Home)]);
            Assert(host.ScrollOffset == 0, "Home must return to the top of the branded activity viewport.");

            var finalButton = lastButton ?? throw new InvalidOperationException("The final activity button was not created.");
            Assert(finalButton.Focus(),
                "The last activity action must be keyboard focusable.");
            Application.DoEvents();
            var focusedBounds = new Rectangle(
                host.PointToClient(finalButton.PointToScreen(Point.Empty)),
                finalButton.Size);
            Assert(focusedBounds.Top >= 0 && focusedBounds.Bottom <= host.ClientSize.Height,
                $"Keyboard focus must scroll the final activity action into view; bounds={focusedBounds}, viewport={host.ClientRectangle}.");

            outsideButton.Focus();
            var onMouseEnter = typeof(Control).GetMethod(
                "OnMouseEnter",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            onMouseEnter.Invoke(host, [EventArgs.Empty]);
            Assert(outsideButton.Focused,
                "Moving the pointer over Activity must not steal keyboard focus from another control.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Branded scrollbar validation failed.", failure);
}

static void RecordConcurrentActivities(ActivityHistoryStore store, string root, string workerName)
{
    for (var index = 0; index < 120; index++)
    {
        var path = Path.Combine(root, $"{workerName}-{index}.mp4");
        store.Transition(new ClipActivityUpdate(path, ClipActivityState.Discovered, OriginalBytes: index + 1));
        store.Transition(new ClipActivityUpdate(path, ClipActivityState.Hashing));
        store.Transition(new ClipActivityUpdate(path, ClipActivityState.Queued));
    }
}

static void AssertSettingsFormLayout(AppSettings settings)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            TraceSmokeStep("Settings layout: pre-handle Gallery lifecycle");
            AssertGalleryPreHandleLifecycle(settings);
            TraceSmokeStep("Settings layout: Gallery Local-only editor flow");
            AssertGalleryEditorFlow(settings);
            TraceSmokeStep("Settings layout: Gallery editor trim preview playback");
            AssertGalleryEditorPreviewPlayback(settings);
            TraceSmokeStep("Settings layout: About actions and privacy seams");
            AssertAboutViewActions(settings);
            TraceSmokeStep("Settings layout: Activity navigation lifecycle");
            using (var activityOnly = new SettingsForm(
                       settings,
                       checkForUpdatesAsync: _ => Task.CompletedTask,
                       initialPage: SettingsPage.Activity))
            {
                activityOnly.Show();
                Application.DoEvents();
                Assert(activityOnly.AcceptButton is null && activityOnly.SavedSettings is null,
                    "Opening Activity must not expose an implicit save action or create saved settings.");
                activityOnly.Close();
                Assert(activityOnly.SavedSettings is null,
                    "Closing Activity without visiting Settings must not enter the settings-save notification path.");
            }

            using (var activityThenSettings = new SettingsForm(
                       settings,
                       checkForUpdatesAsync: _ => Task.CompletedTask,
                       initialPage: SettingsPage.Activity))
            {
                activityThenSettings.Show();
                activityThenSettings.ShowPage(SettingsPage.Settings);
                Application.DoEvents();
                var save = EnumerateControls(activityThenSettings)
                    .OfType<Button>()
                    .Single(button => button.Text == "Save changes");
                Assert(ReferenceEquals(activityThenSettings.AcceptButton, save) && save.Visible,
                    "Activity-to-Settings navigation must restore the real save action.");
                save.PerformClick();
                Assert(activityThenSettings.DialogResult == DialogResult.OK &&
                       activityThenSettings.SavedSettings is not null,
                    "A legitimate save after Activity-to-Settings navigation must reach the normal confirmation path.");
            }

            using var activityHistory = new ActivityHistoryStore(string.Empty);
            var longActivityName = new string('B', 180) + "__2026-08-06__14-37-13.mp4";
            var activityPath = Path.Combine(settings.ClipsFolder, longActivityName);
            activityHistory.Transition(new ClipActivityUpdate(
                activityPath,
                ClipActivityState.Compressing,
                OriginalBytes: 183_955_215,
                CompressedBytes: 26_214_400,
                CompressionTargetMb: 95,
                VideoKbps: 6000,
                AudioKbps: 96,
                IncrementAttempt: true,
                Detail: "Compression complete; preparing the Discord upload."));
            using var form = new SettingsForm(
                settings,
                checkForUpdatesAsync: _ => Task.CompletedTask,
                watcherStatusProvider: () => "Discord open — local-only mode",
                activityHistory: activityHistory);
            TraceSmokeStep("Settings layout: primary form geometry");
            form.CreateControl();
            Assert(form.Text == "ClipCord — Settings", "The settings window must use the ClipCord brand.");
            AssertControlsFit(form);
            var designedOpeningSize = form.Size;
            AssertSettingsCardsOpenWithoutScrolling(form);
            form.Show();
            Application.DoEvents();
            AssertControlsFit(form);
            AssertSettingsFooterLayout(form, requireLogicalHeight: true);
            AssertSettingsCardsScrollOnlyWhenScreenConstrained(form, designedOpeningSize);
            AssertSettingsTextFieldsAligned(form);
            AssertSettingsCardGrid(form);
            AssertSettingsFeatureIcons(form);
            AssertCompressionTargetPickerInteraction(form);
            AssertCriticalTextFits(form);
            AssertAccessibility(form);
            AssertOpaqueCustomControlsPaintEveryPixel(form);
            form.Invalidate(true);
            form.Update();
            AssertOpaqueCustomControlsPaintEveryPixel(form);
            Assert(form.Padding.All >= SettingsForm.ResizeGrip,
                "The borderless window must leave the full resize grip exposed around docked content.");
            var rootLayout = form.Controls.Cast<Control>().Single(control => control.Name == "RootLayout");
            Assert(rootLayout.Bounds == new Rectangle(
                       form.Padding.Left,
                       form.Padding.Top,
                       form.ClientSize.Width - form.Padding.Horizontal,
                       form.ClientSize.Height - form.Padding.Vertical),
                "Docked content must not cover any part of the reserved resize frame.");
            var cornerInset = SettingsForm.ResizeGrip - 1;
            Assert(form.Region?.IsVisible(cornerInset, cornerInset) != false &&
                   form.HitTestResizeGrip(new Point(cornerInset, cornerInset)) == 13 &&
                   form.HitTestResizeGrip(new Point(form.ClientSize.Width - cornerInset, cornerInset)) == 14 &&
                   form.HitTestResizeGrip(new Point(cornerInset, form.ClientSize.Height - cornerInset)) == 16 &&
                   form.HitTestResizeGrip(new Point(form.ClientSize.Width - cornerInset, form.ClientSize.Height - cornerInset)) == 17,
                "All four diagonal resize hit targets must remain reachable.");
            var reachableDiagonalPixels = Enumerable.Range(0, SettingsForm.ResizeGrip)
                .Count(inset => form.Region?.IsVisible(inset, inset) != false &&
                                form.HitTestResizeGrip(new Point(inset, inset)) == 13);
            Assert(reachableDiagonalPixels >= 8,
                $"The diagonal resize target is too small: only {reachableDiagonalPixels} pixels are reachable.");
            AssertDpiRefit(form);
            form.ToggleMaximize();
            Application.DoEvents();
            Assert(!form.HasExplicitMaximizedBounds,
                "Custom maximize must leave MaximizedBounds empty so WM_GETMINMAXINFO remains monitor-relative.");
            form.ToggleMaximize();
            Application.DoEvents();
            form.Hide();
            form.Size = form.MinimumSize;
            form.PerformLayout();
            AssertControlsFit(form);
            form.Show();
            Application.DoEvents();
            AssertControlsFit(form);
            AssertSettingsFooterLayout(form, requireLogicalHeight: true);
            AssertSettingsCardGrid(form);
            AssertSettingsTextFieldsAligned(form);
            AssertCriticalTextFits(form);
            form.Hide();

            var buttonTexts = EnumerateControls(form)
                .OfType<Button>()
                .Select(button => button.Text)
                .ToHashSet(StringComparer.Ordinal);
            Assert(new[] { "Browse", "Test webhook", "Check for updates", "Save changes", "Cancel" }
                    .All(buttonTexts.Contains),
                "The settings form must keep all action buttons available.");
            var startupCheckbox = EnumerateControls(form)
                .OfType<CheckBox>()
                .Single(checkBox => checkBox.Text == "Start with Windows");
            Assert(startupCheckbox.Width > 0 && startupCheckbox.Height > 0,
                "The Start with Windows checkbox must occupy visible layout space.");
            var modeHotkey = EnumerateControls(form)
                .OfType<TextBox>()
                .Single(textBox => textBox.AccessibleName == "Global upload-mode shortcut");
            Assert(modeHotkey.ReadOnly && modeHotkey.Text == GlobalHotkeyBinding.DefaultDisplayText &&
                   modeHotkey.Width > 0 && modeHotkey.Height > 0,
                "The global mode shortcut must show its saved binding in a visible capture field.");
            Assert(EnumerateControls(form).OfType<Button>().Any(button => button.Text == "Disable"),
                "The global shortcut must offer a discoverable disable action.");
            var uploadToggle = EnumerateControls(form)
                .OfType<CheckBox>()
                .Single(checkBox => checkBox.Name == "UploadToDiscordToggle");
            Assert(uploadToggle.Checked && uploadToggle.Width > 0 && uploadToggle.Height > 0,
                "The Discord upload toggle must reflect the saved setting and remain visible.");
            uploadToggle.Checked = false;
            Application.DoEvents();
            var uploadModeHelper = EnumerateControls(form)
                .OfType<Label>()
                .Single(label => label.Name == "UploadModeHelperLabel");
            var privacySummary = EnumerateControls(form)
                .OfType<Label>()
                .Single(label => label.Name == "PrivacySummaryLabel");
            Assert(uploadModeHelper.Text.Contains("No Discord request", StringComparison.Ordinal) &&
                   privacySummary.Text.Contains("Local-only mode", StringComparison.Ordinal),
                "Turning uploads off must immediately explain the local-only behavior.");
            AssertControlsFit(form);
            var activityItem = EnumerateControls(form)
                .Single(control => control.Name == "ActivityNavItem");
            Assert(!activityItem.AccessibilityObject.State.HasFlag(AccessibleStates.Unavailable) &&
                   activityItem.TabStop,
                "Activity navigation must be available to pointer and keyboard users.");
            form.Show();
            form.ShowPage(SettingsPage.Activity);
            Application.DoEvents();
            Assert(form.Text == "ClipCord — Activity", "Activity navigation must activate the branded Activity page.");
            Assert(EnumerateControls(form).Single(control => control.Name == "ActivityView").Visible,
                "The Activity page must be visible after navigation.");
            var activityScrollHost = EnumerateControls(form)
                .OfType<BrandedScrollHost>()
                .Single(control => control.Name == "ActivityScrollHost");
            var activityList = EnumerateControls(form)
                .OfType<ActivityListPanel>()
                .Single(control => control.Name == "ActivityList");
            Assert(!activityScrollHost.AutoScroll && !activityList.AutoScroll,
                "Activity must not expose the native Windows scrollbar.");
            Assert(EnumerateControls(form).Count(control => control.Name == "ActivityCard" && control.Visible) == 1,
                "The Activity page must render the current bounded history.");
            Assert(EnumerateControls(form).OfType<Button>().Any(button => button.Name == "OpenUploadedFolderButton") &&
                   EnumerateControls(form).OfType<Button>().Any(button => button.Name == "OpenLogsButton") &&
                   EnumerateControls(form).OfType<Button>().Any(button => button.Name == "OpenFileLocationButton"),
                "Activity must expose uploaded-folder, log, and per-clip location actions.");
            AssertControlsFit(form);
            AssertCriticalTextFits(form);

            TraceSmokeStep("Settings layout: Gallery interaction");
            form.ShowPage(SettingsPage.Gallery);
            WaitForUiCondition(
                () => EnumerateControls(form).Count(control => control.Name == "GalleryGameCard") == 2,
                TimeSpan.FromSeconds(5),
                "Gallery did not finish its on-demand archive scan.");
            Assert(form.Text.EndsWith("Gallery", StringComparison.Ordinal) &&
                   EnumerateControls(form).Single(control => control.Name == "GalleryView").Visible,
                "Gallery navigation must activate the branded Gallery page.");
            var galleryView = EnumerateControls(form).OfType<GalleryView>().Single();
            var galleryGridForDisposal = (GalleryGridPanel)typeof(GalleryView).GetField(
                    "_gameGrid",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(galleryView)!;
            var galleryListForDisposal = (ActivityListPanel)typeof(GalleryView).GetField(
                    "_clipList",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(galleryView)!;
            var topNavigation = EnumerateControls(form).Single(control => control.Name == "TopNavigation");
            var pageHost = EnumerateControls(form).Single(control => control.Name == "PageHost");
            Assert(topNavigation.Bottom <= pageHost.Top,
                "The primary navigation must remain above the page content instead of using a sidebar.");
            Assert(EnumerateControls(form).Single(control => control.Name == "GalleryNavItem").TabStop,
                "Gallery must be keyboard reachable from the top navigation.");
            var gameCard = EnumerateControls(form)
                .OfType<GalleryGameCard>()
                .Single(card => card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true);
            gameCard.Focus();
            Assert(gameCard.Focused, "The Gallery game card must accept keyboard focus.");
            typeof(Control).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(gameCard, [new KeyEventArgs(Keys.Enter)]);
            Application.DoEvents();
            Assert(EnumerateControls(form).Count(control => control.Name == "GalleryClipCard") == 2 &&
                   EnumerateControls(form).OfType<Button>().Any(button => button.Name == "PlayGalleryClipButton") &&
                   EnumerateControls(form).OfType<Button>().Any(button => button.Name == "ShowGalleryClipButton"),
                "Enter must open a Gallery game and expose uploaded and local-only clip actions.");
            EnumerateControls(form).OfType<Button>()
                .Single(button => button.Name == "GalleryBackButton")
                .PerformClick();
            Application.DoEvents();
            gameCard = EnumerateControls(form)
                .OfType<GalleryGameCard>()
                .Single(card => card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true);
            gameCard.Focus();
            typeof(Control).GetMethod(
                    "OnKeyDown",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(gameCard, [new KeyEventArgs(Keys.Space)]);
            Application.DoEvents();
            Assert(EnumerateControls(form).Count(control => control.Name == "GalleryClipCard") == 2,
                "Space must open a focused Gallery game card through its keyboard handler.");
            AssertControlsFit(form);
            AssertSettingsFooterLayout(form, requireLogicalHeight: true);
            AssertCriticalTextFits(form);

            TraceSmokeStep("Settings layout: About navigation and geometry");
            var aboutNavigation = EnumerateControls(form).Single(control => control.Name == "AboutNavItem");
            typeof(Control).GetMethod(
                    "OnClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(aboutNavigation, [EventArgs.Empty]);
            Application.DoEvents();
            var aboutView = EnumerateControls(form).OfType<AboutView>().Single();
            var footer = EnumerateControls(form).Single(control => control.Name == "FooterLayout");
            var saveButton = EnumerateControls(form).OfType<Button>().Single(button => button.Text == "Save changes");
            Assert(form.Text == "ClipCord — About" &&
                   aboutView.Visible &&
                   !EnumerateControls(form).Single(control => control.Name == "SettingsCards").Visible &&
                   !EnumerateControls(form).Single(control => control.Name == "ActivityView").Visible &&
                   !EnumerateControls(form).Single(control => control.Name == "GalleryView").Visible,
                "About navigation must activate one real page inside the branded shell.");
            Assert(aboutNavigation.AccessibleDescription == "Current page" &&
                   new[] { "SettingsNavItem", "ActivityNavItem", "GalleryNavItem" }
                       .Select(name => EnumerateControls(form).Single(control => control.Name == name))
                       .All(control => string.IsNullOrEmpty(control.AccessibleDescription)),
                "About navigation must expose the current-page state without leaving another page selected.");
            Assert(!saveButton.Visible && form.AcceptButton is null && form.SavedSettings is null &&
                   !footer.Visible &&
                   ((TableLayoutPanel)EnumerateControls(form).Single(control => control.Name == "RootLayout"))
                       .RowStyles[3].Height == 0,
                "About must remove Settings save/footer actions rather than creating an implicit save path.");
            AssertAboutLayout(form, GetDpiScale(form));
            AssertAboutCopyAndAccessibility(form);
            var aboutDesignedSize = SettingsForm.GetDesignedOpeningSize(SettingsPage.About, form.DeviceDpi);
            if (form.Width >= aboutDesignedSize.Width && form.Height >= aboutDesignedSize.Height)
            {
                Assert(!aboutView.HasOverflow,
                    "About must open without scrolling when the full designed size fits the current screen.");
            }

            form.ShowPage(SettingsPage.Settings);
            Application.DoEvents();
            Assert(footer.Visible &&
                   ((TableLayoutPanel)EnumerateControls(form).Single(control => control.Name == "RootLayout"))
                       .RowStyles[3].Height == SettingsForm.FooterLogicalHeight &&
                   saveButton.Visible && ReferenceEquals(form.AcceptButton, saveButton),
                "Returning from About to Settings must restore the normal footer and save action.");
            AssertSettingsFooterLayout(form, requireLogicalHeight: true);
            Assert(EnumerateControls(form).OfType<Label>().Any(label => label.Text == "Local only"),
                "The branded header must present the complete local-only watcher status.");
            var headerLogo = EnumerateControls(form)
                .OfType<ClipCordLogoControl>()
                .Single(control => control.Name == "HeaderLogo");
            var productName = EnumerateControls(form)
                .OfType<Label>()
                .Single(control => control.Name == "ProductNameLabel");
            Assert(ClipCordLogoControl.EmbeddedAssetSize == new Size(1024, 1024),
                "The branded header must render the full-resolution embedded app-icon.png asset.");
            Assert(Math.Min(headerLogo.Width, headerLogo.Height) >= productName.Height,
                $"The branded header logo must be at least as tall as the wordmark; logo={headerLogo.Size}, wordmark={productName.Size}.");
            AssertVerticalCentersMatch(headerLogo, productName);
            AssertOfficialLogoArtworkPainted(headerLogo);

            using var updateDialog = new UpdateAvailableDialog(
                UpdateCheckerTests.CreateRelease(new StableVersion(2, 0, 0)));
            updateDialog.CreateControl();
            Assert(updateDialog.Text == "ClipCord — Update available",
                "The update window must use the ClipCord brand.");
            AssertControlsFit(updateDialog);
            var updateActions = EnumerateControls(updateDialog)
                .OfType<Button>()
                .Select(button => button.Text)
                .ToHashSet(StringComparer.Ordinal);
            Assert(updateActions.SetEquals([
                    "View changes",
                    "Install update",
                    "Skip this version",
                    "Remind me later"
                ]),
                "The update prompt must expose every required action.");

            using var downloadService = new NeverCalledUpdateDownloadService();
            using var downloadDialog = new UpdateDownloadDialog(
                UpdateCheckerTests.CreateRelease(new StableVersion(2, 0, 0)),
                downloadService);
            downloadDialog.CreateControl();
            Assert(downloadDialog.Text == "ClipCord — Downloading update",
                "The update download window must use the ClipCord brand.");
            AssertControlsFit(downloadDialog);
            var downloadActions = EnumerateControls(downloadDialog)
                .OfType<Button>()
                .Select(button => button.Text)
                .ToHashSet(StringComparer.Ordinal);
            Assert(downloadActions.SetEquals(["Retry", "Cancel"]),
                "The update download window must expose retry and cancellation actions.");
            AssertUpdateDownloadDialogBehavior(
                UpdateCheckerTests.CreateRelease(new StableVersion(2, 0, 0)));

            using (var ownerForm = new Form { ShowInTaskbar = false })
            {
                ownerForm.Show();
                Assert(ReferenceEquals(TrayApplicationContext.GetUsableOwner(ownerForm), ownerForm),
                    "A visible live form must remain a valid update-dialog owner.");
                ownerForm.Dispose();
                Assert(TrayApplicationContext.GetUsableOwner(ownerForm) is null,
                    "A disposed Settings form must be dropped before update UI uses its handle.");
            }

            TraceSmokeStep("Settings layout: update and round-trip dialogs");
            AssertSettingsRoundTrip(settings);
            AssertManualCheckCloseProtection(
                settings,
                SettingsPage.Settings,
                "SettingsCheckUpdatesButton");
            AssertManualCheckCloseProtection(
                settings,
                SettingsPage.About,
                "AboutCheckUpdatesButton");
            form.Dispose();
            Assert(galleryGridForDisposal.IsDisposed && galleryListForDisposal.IsDisposed,
                "Both Gallery content panels must be disposed, including the one detached from the scroll host.");
            TraceSmokeStep("Settings layout: Settings 150% scaling");
            AssertSettingsScaledLayout(settings, 1.5f);
            TraceSmokeStep("Settings layout: Settings 200% scaling");
            AssertSettingsScaledLayout(settings, 2f);
            TraceSmokeStep("Settings layout: Gallery 150% scaling");
            AssertGalleryScaledLayout(settings, 1.5f);
            TraceSmokeStep("Settings layout: Gallery 200% scaling");
            AssertGalleryScaledLayout(settings, 2f);
            TraceSmokeStep("Settings layout: Gallery editor 150% scaling");
            AssertGalleryEditorScaledLayout(settings, 1.5f);
            TraceSmokeStep("Settings layout: Gallery editor 200% scaling");
            AssertGalleryEditorScaledLayout(settings, 2f);
            TraceSmokeStep("Settings layout: About 150% scaling");
            AssertAboutScaledLayout(settings, 1.5f);
            TraceSmokeStep("Settings layout: About 200% scaling");
            AssertAboutScaledLayout(settings, 2f);
            TraceSmokeStep("Settings layout: About constrained scrolling");
            AssertAboutMinimumScroll(settings);
            using (var emptyGalleryGrid = new GalleryGridPanel { Size = new Size(1024, 400) })
            using (var emptyGalleryState = new Panel { Name = "GalleryEmptyState", Height = 160 })
            {
                emptyGalleryGrid.Controls.Add(emptyGalleryState);
                emptyGalleryGrid.Reflow();
                Assert(emptyGalleryState.Width == emptyGalleryGrid.ClientSize.Width &&
                       emptyGalleryGrid.MeasureContentHeight() == 160,
                    "The Gallery empty state must span the viewport and report its actual content height.");
            }
            Assert(activityHistory.SubscriptionCount == 0,
                "Closing the Settings/Activity window must detach its live-history subscription.");
            TraceSmokeStep("Settings layout: complete");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(60)))
    {
        throw new TimeoutException(
            "Settings form layout validation did not finish within 60 seconds. " +
            "The last emitted Settings layout checkpoint identifies the stalled operation.");
    }
    if (failure is not null) throw new InvalidOperationException("Settings form layout validation failed.", failure);
}

static void TraceSmokeStep(string message)
{
    Console.WriteLine($"[smoke] {message}");
    Console.Out.Flush();
}

static async Task AssertStartedWebhookPostCancellationAsync(string temporaryRoot, string webhookUrl)
{
    var clipPath = Path.Combine(temporaryRoot, "started-post-cancellation.mp4");
    await File.WriteAllBytesAsync(clipPath, [1, 2, 3, 4]);
    var presentation = new DiscordUploadPresentation(
        Path.GetFileName(clipPath),
        "Cancellation Test",
        null);

    using (var cancelledBeforeStart = new CancellationTokenSource())
    using (var handler = new GatedWebhookHandler(System.Net.HttpStatusCode.NoContent))
    using (var client = new DiscordWebhookClient(handler))
    {
        cancelledBeforeStart.Cancel();
        var cancelled = false;
        try
        {
            await client.UploadWithCompressionAsync(
                webhookUrl,
                clipPath,
                presentation,
                95,
                "Cancellation Tester",
                cancelledBeforeStart.Token,
                completeStartedPosts: true);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Assert(cancelled && handler.CallCount == 0,
            "Cancellation before a webhook POST starts must avoid sending any request.");
    }

    using (var cancellation = new CancellationTokenSource())
    using (var handler = new GatedWebhookHandler(System.Net.HttpStatusCode.NoContent))
    using (var client = new DiscordWebhookClient(handler))
    {
        var upload = client.UploadWithCompressionAsync(
            webhookUrl,
            clipPath,
            presentation,
            95,
            "Cancellation Tester",
            cancellation.Token,
            completeStartedPosts: true);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Task.Delay(50);
        Assert(!upload.IsCompleted,
            "A caller cancellation must not abandon an already-started webhook POST with an ambiguous result.");
        handler.Release();
        await upload;
        Assert(handler.CallCount == 1,
            "A confirmed started POST must complete exactly once after caller cancellation.");
    }

    using (var cancellation = new CancellationTokenSource())
    using (var handler = new GatedWebhookHandler(System.Net.HttpStatusCode.RequestEntityTooLarge))
    using (var client = new DiscordWebhookClient(handler))
    {
        var upload = client.UploadWithCompressionAsync(
            webhookUrl,
            clipPath,
            presentation,
            95,
            "Cancellation Tester",
            cancellation.Token,
            completeStartedPosts: true);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        handler.Release();
        var cancelled = false;
        try
        {
            await upload;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Assert(cancelled && handler.CallCount == 1,
            "After a definite size rejection, cancellation must stop before FFmpeg or another POST begins.");
    }
}

static async Task AssertEditedClipUploadTransactionsAsync(string temporaryRoot)
{
    var transactionRoot = Directory.CreateDirectory(Path.Combine(temporaryRoot, "manual-edit-transaction")).FullName;
    var localGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreateLocalOnly(transactionRoot),
        "Battlefield™-6")).FullName;
    var originalPath = Path.Combine(localGame, "Battlefield™-6 original.mp4");
    await File.WriteAllBytesAsync(originalPath, [1, 2, 3, 4, 5, 6]);
    var operationId = Guid.NewGuid();
    var operationFolder = Directory.CreateDirectory(Path.Combine(
        transactionRoot,
        ".clipcord-editing",
        operationId.ToString("N"))).FullName;
    var editedPath = Path.Combine(operationFolder, "Battlefield edited.mp4");
    await File.WriteAllBytesAsync(editedPath, [9, 8, 7, 6, 5, 4, 3]);
    var originalHash = await ContentIdentity.ComputeSha256Async(originalPath, CancellationToken.None);
    var editedHash = await ContentIdentity.ComputeSha256Async(editedPath, CancellationToken.None);
    var stateDirectory = Directory.CreateDirectory(Path.Combine(transactionRoot, "state-data")).FullName;
    var stateStore = new WatchStateStore(
        Path.Combine(stateDirectory, "state.json"),
        Path.Combine(stateDirectory, ".safe-baseline-required"));
    var recycleRoot = Directory.CreateDirectory(Path.Combine(transactionRoot, "fake-recycle-bin")).FullName;
    var recycler = new RecordingOriginalClipRecycler(recycleRoot);
    var archiveSeenBeforeRecycle = false;
    var expectedArchivePath = Path.Combine(
        transactionRoot,
        "uploaded",
        "Battlefield™-6",
        "Battlefield edited.mp4");
    recycler.BeforeRecycle = _ => archiveSeenBeforeRecycle = File.Exists(expectedArchivePath);
    using var activity = new ActivityHistoryStore(string.Empty);
    using var cancellation = new CancellationTokenSource();
    var originalSeenWhenUploadReturned = false;
    var uploader = new RecordingManualDiscordUploader
    {
        EmitCompressionProgress = true,
        AfterSuccessfulUpload = () =>
        {
            originalSeenWhenUploadReturned = File.Exists(originalPath);
            cancellation.Cancel();
        }
    };
    var service = new EditedClipUploadService(
        stateStore,
        () => uploader,
        new EditedClipDispositionProcessor(recycler));
    var settings = new AppSettings(
        transactionRoot,
        "https://discord.com/api/v10/webhooks/123456/manual-edit-token",
        true,
        95,
        "Manual Editor Tester",
        false);
    var prepared = new PreparedClipEdit(
        operationId,
        transactionRoot,
        originalPath,
        originalHash,
        editedPath,
        editedHash,
        "Battlefield edited.mp4",
        "Battlefield™-6",
        "A carefully trimmed flank.",
        KeepOriginal: false,
        UsesOriginalAsArtifact: false,
        OutputBytes: new FileInfo(editedPath).Length);

    var progressUpdates = new List<ManualClipEditProgress>();
    var result = await service.UploadAsync(
        settings,
        prepared,
        activity,
        progress: new InlineProgress<ManualClipEditProgress>(progressUpdates.Add),
        cancellation.Token);
    Assert(uploader.UploadCount == 1 &&
           uploader.LastPresentation is
           {
               DisplayFileName: "Battlefield edited.mp4",
               GameName: "Battlefield™-6",
               Note: "A carefully trimmed flank."
           },
        "A manual edit must post once with its explicit display name, game, and bounded Discord note.");
    Assert(File.Exists(result.ArchivedPath) &&
           Path.GetFileName(Path.GetDirectoryName(result.ArchivedPath)) == "Battlefield™-6" &&
           !File.Exists(originalPath) && recycler.RecycledPaths.Count == 1 &&
           !result.OriginalKept && !result.OriginalCleanupFailed,
        "After Discord success, the edited copy must archive under the selected game before the original is recycled.");
    Assert(originalSeenWhenUploadReturned && archiveSeenBeforeRecycle,
        "The Local-only original must still exist when Discord returns, and the edited archive must exist before recycling begins.");
    Assert(progressUpdates.Select(update => update.Stage).SequenceEqual([
               ManualClipEditStage.Uploading,
               ManualClipEditStage.Compressing,
               ManualClipEditStage.Uploading,
               ManualClipEditStage.Archiving,
               ManualClipEditStage.Completed
           ]),
        "Compression must be cancellable only while FFmpeg is running, then return to the non-cancellable Uploading stage before the retry POST.");
    var terminalActivity = activity.GetSnapshot().Entries.Single(entry =>
        entry.SourcePath.Equals(originalPath, StringComparison.OrdinalIgnoreCase));
    Assert(terminalActivity.State == ClipActivityState.Archived &&
           terminalActivity.Route == ClipActivityRoute.Uploaded &&
           terminalActivity.GameName == "Battlefield™-6" &&
           terminalActivity.CurrentPath == result.ArchivedPath &&
           terminalActivity.OriginalBytes == prepared.OutputBytes,
        "A manual edit must finish Activity with its selected game, uploaded route, archived path, and rendered byte count.");
    var committedState = await stateStore.LoadOrInitializeAsync(transactionRoot, _ => { }, CancellationToken.None);
    Assert(committedState.Version == 4 &&
           committedState.UploadedContentHashes.Contains(editedHash) &&
           committedState.KnownContentHashes.Contains(editedHash) &&
           committedState.PendingEditedUploads.Count == 0,
        "Cancellation requested as the POST returns must not prevent the non-cancellable uploaded-hash/disposition commit.");

    var keepGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreateLocalOnly(transactionRoot),
        "Keep Game")).FullName;
    var keepOriginal = Path.Combine(keepGame, "keep-source.mp4");
    await File.WriteAllBytesAsync(keepOriginal, [11, 12, 13, 14]);
    var keepOperation = Guid.NewGuid();
    var keepStageFolder = Directory.CreateDirectory(Path.Combine(
        transactionRoot,
        ".clipcord-editing",
        keepOperation.ToString("N"))).FullName;
    var keepEdited = Path.Combine(keepStageFolder, "keep-edit.mp4");
    await File.WriteAllBytesAsync(keepEdited, [15, 16, 17, 18, 19]);
    var keepUploader = new RecordingManualDiscordUploader();
    var keepService = new EditedClipUploadService(
        stateStore,
        () => keepUploader,
        new EditedClipDispositionProcessor(recycler));
    var keepResult = await keepService.UploadAsync(
        settings,
        new PreparedClipEdit(
            keepOperation,
            transactionRoot,
            keepOriginal,
            await ContentIdentity.ComputeSha256Async(keepOriginal, CancellationToken.None),
            keepEdited,
            await ContentIdentity.ComputeSha256Async(keepEdited, CancellationToken.None),
            "keep-edit.mp4",
            "Keep Game",
            null,
            KeepOriginal: true,
            UsesOriginalAsArtifact: false,
            OutputBytes: new FileInfo(keepEdited).Length),
        null,
        null,
        CancellationToken.None);
    Assert(keepUploader.UploadCount == 1 && File.Exists(keepResult.ArchivedPath) &&
           File.Exists(keepOriginal) && keepResult.OriginalKept && !keepResult.OriginalCleanupFailed &&
           recycler.RecycledPaths.Count == 1,
        "Keep original must archive the edited copy without sending the Local-only source to the Recycle Bin.");

    var duplicateGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreateLocalOnly(transactionRoot),
        "Duplicate Game")).FullName;
    var duplicateOriginal = Path.Combine(duplicateGame, "duplicate-source.mp4");
    await File.WriteAllBytesAsync(duplicateOriginal, [31, 41, 59]);
    var duplicateOperation = Guid.NewGuid();
    var duplicateStageFolder = Directory.CreateDirectory(Path.Combine(
        transactionRoot,
        ".clipcord-editing",
        duplicateOperation.ToString("N"))).FullName;
    var duplicateEdited = Path.Combine(duplicateStageFolder, "duplicate-edit.mp4");
    await File.WriteAllBytesAsync(duplicateEdited, [9, 8, 7, 6, 5, 4, 3]);
    var duplicateResult = await keepService.UploadAsync(
        settings,
        new PreparedClipEdit(
            duplicateOperation,
            transactionRoot,
            duplicateOriginal,
            await ContentIdentity.ComputeSha256Async(duplicateOriginal, CancellationToken.None),
            duplicateEdited,
            editedHash,
            "duplicate-edit.mp4",
            "Duplicate Game",
            null,
            KeepOriginal: false,
            UsesOriginalAsArtifact: false,
            OutputBytes: new FileInfo(duplicateEdited).Length),
        null,
        null,
        CancellationToken.None);
    Assert(duplicateResult.AlreadyUploaded && keepUploader.UploadCount == 1 &&
           File.Exists(duplicateOriginal) && !File.Exists(duplicateEdited) &&
           !Directory.Exists(duplicateStageFolder),
        "An exact edited-content duplicate must not repost, must preserve its Local-only source, and must clean the redundant stage.");

    var crashRoot = Directory.CreateDirectory(Path.Combine(temporaryRoot, "manual-edit-recovery")).FullName;
    var crashLocalGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreateLocalOnly(crashRoot),
        "Recovery Game")).FullName;
    var crashOriginal = Path.Combine(crashLocalGame, "Recovery source.mp4");
    await File.WriteAllBytesAsync(crashOriginal, [20, 21, 22, 23]);
    var crashOperation = Guid.NewGuid();
    var crashStageFolder = Directory.CreateDirectory(Path.Combine(
        crashRoot,
        ".clipcord-editing",
        crashOperation.ToString("N"))).FullName;
    var crashEdited = Path.Combine(crashStageFolder, "Recovery edit.mp4");
    await File.WriteAllBytesAsync(crashEdited, [30, 31, 32, 33, 34]);
    var crashOriginalHash = await ContentIdentity.ComputeSha256Async(crashOriginal, CancellationToken.None);
    var crashEditedHash = await ContentIdentity.ComputeSha256Async(crashEdited, CancellationToken.None);
    var crashStateDirectory = Directory.CreateDirectory(Path.Combine(crashRoot, "state-data")).FullName;
    var crashStore = new WatchStateStore(
        Path.Combine(crashStateDirectory, "state.json"),
        Path.Combine(crashStateDirectory, ".safe-baseline-required"));
    var crashRecycler = new RecordingOriginalClipRecycler(
        Directory.CreateDirectory(Path.Combine(crashRoot, "fake-recycle-bin")).FullName);
    var conflictingDestination = Path.Combine(crashRoot, "uploaded", "Recovery Game", "Recovery edit.mp4");
    var crashUploader = new RecordingManualDiscordUploader
    {
        AfterSuccessfulUpload = () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(conflictingDestination)!);
            File.WriteAllBytes(conflictingDestination, [99, 99, 99]);
        }
    };
    var crashService = new EditedClipUploadService(
        crashStore,
        () => crashUploader,
        new EditedClipDispositionProcessor(crashRecycler));
    var crashPrepared = new PreparedClipEdit(
        crashOperation,
        crashRoot,
        crashOriginal,
        crashOriginalHash,
        crashEdited,
        crashEditedHash,
        "Recovery edit.mp4",
        "Recovery Game",
        null,
        KeepOriginal: false,
        UsesOriginalAsArtifact: false,
        OutputBytes: new FileInfo(crashEdited).Length);
    var commitPending = false;
    try
    {
        await crashService.UploadAsync(
            settings with { ClipsFolder = crashRoot },
            crashPrepared,
            activityHistory: null,
            progress: null,
            CancellationToken.None);
    }
    catch (ManualClipCommitPendingException)
    {
        commitPending = true;
    }
    Assert(commitPending && crashUploader.UploadCount == 1 &&
           File.Exists(crashOriginal) && File.Exists(crashEdited),
        "An archive collision after Discord success must retain both source files and enter recovery without reposting.");
    var pendingState = await crashStore.LoadOrInitializeAsync(crashRoot, _ => { }, CancellationToken.None);
    Assert(pendingState.UploadedContentHashes.Contains(crashEditedHash) &&
           pendingState.PendingEditedUploads.Count == 1,
        "Discord confirmation and the fixed archive destination must be durable before a failing move.");
    File.Delete(conflictingDestination);
    using (var recoveryCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
    {
        var recoveryWorker = new UploaderWorker(
            settings with
            {
                ClipsFolder = crashRoot,
                WebhookUrl = string.Empty,
                UploadToDiscord = false
            },
            _ => { },
            crashStore,
            discordClientFactory: null,
            activityHistory: null,
            editedClipDispositionProcessor: new EditedClipDispositionProcessor(crashRecycler));
        var recoveryTask = recoveryWorker.RunAsync(recoveryCancellation.Token);
        try
        {
            await WaitUntilAsync(
                () =>
                {
                    if (!File.Exists(conflictingDestination) || File.Exists(crashOriginal)) return false;
                    try
                    {
                        return crashStore.LoadOrInitializeAsync(crashRoot, _ => { }, CancellationToken.None)
                            .GetAwaiter().GetResult().PendingEditedUploads.Count == 0;
                    }
                    catch
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(5),
                "The uploader worker did not durably finish its confirmed edited-clip disposition.");
        }
        finally
        {
            recoveryCancellation.Cancel();
            try { await recoveryTask; }
            catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested) { }
        }
    }
    var recoveredState = await crashStore.LoadOrInitializeAsync(crashRoot, _ => { }, CancellationToken.None);
    Assert(crashUploader.UploadCount == 1 && File.Exists(conflictingDestination) &&
           !File.Exists(crashOriginal) && crashRecycler.RecycledPaths.Count == 1 &&
           recoveredState.PendingEditedUploads.Count == 0,
        "Uploader-worker restart recovery must finish archive and Recycle Bin disposition without another webhook request.");

    var invalidRoot = Directory.CreateDirectory(Path.Combine(temporaryRoot, "manual-edit-invalid-webhook")).FullName;
    var invalidLocalGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreateLocalOnly(invalidRoot),
        "Invalid Webhook Game")).FullName;
    var invalidOriginal = Path.Combine(invalidLocalGame, "source.mp4");
    await File.WriteAllBytesAsync(invalidOriginal, [1, 3, 5]);
    var invalidOperation = Guid.NewGuid();
    var invalidStageFolder = Directory.CreateDirectory(Path.Combine(
        invalidRoot,
        ".clipcord-editing",
        invalidOperation.ToString("N"))).FullName;
    var invalidEdited = Path.Combine(invalidStageFolder, "edit.mp4");
    await File.WriteAllBytesAsync(invalidEdited, [2, 4, 6]);
    var invalidUploader = new RecordingManualDiscordUploader();
    var invalidService = new EditedClipUploadService(
        new WatchStateStore(
            Path.Combine(invalidRoot, "state-data", "state.json"),
            Path.Combine(invalidRoot, "state-data", ".marker")),
        () => invalidUploader);
    var invalidPrepared = new PreparedClipEdit(
        invalidOperation,
        invalidRoot,
        invalidOriginal,
        await ContentIdentity.ComputeSha256Async(invalidOriginal, CancellationToken.None),
        invalidEdited,
        await ContentIdentity.ComputeSha256Async(invalidEdited, CancellationToken.None),
        "edit.mp4",
        "Invalid Webhook Game",
        null,
        KeepOriginal: false,
        UsesOriginalAsArtifact: false,
        OutputBytes: 3);
    var invalidRejected = false;
    try
    {
        await invalidService.UploadAsync(
            settings with { ClipsFolder = invalidRoot, WebhookUrl = string.Empty },
            invalidPrepared,
            null,
            null,
            CancellationToken.None);
    }
    catch (InvalidOperationException)
    {
        invalidRejected = true;
    }
    Assert(invalidRejected && invalidUploader.UploadCount == 0 && File.Exists(invalidOriginal) &&
           !File.Exists(invalidEdited),
        "Manual uploads in Local-only mode must still require a valid webhook and must preserve the original on rejection.");

    var failedRoot = Directory.CreateDirectory(Path.Combine(temporaryRoot, "manual-edit-failed-post")).FullName;
    var failedLocalGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreateLocalOnly(failedRoot),
        "Failure Game")).FullName;
    var failedOriginal = Path.Combine(failedLocalGame, "source.mp4");
    await File.WriteAllBytesAsync(failedOriginal, [8, 6, 7, 5, 3, 0, 9]);
    var failedOriginalBytes = await File.ReadAllBytesAsync(failedOriginal);
    var failedOperation = Guid.NewGuid();
    var failedStageFolder = Directory.CreateDirectory(Path.Combine(
        failedRoot,
        ".clipcord-editing",
        failedOperation.ToString("N"))).FullName;
    var failedEdited = Path.Combine(failedStageFolder, "edit.mp4");
    await File.WriteAllBytesAsync(failedEdited, [4, 2, 4, 2]);
    var failedStore = new WatchStateStore(
        Path.Combine(failedRoot, "state-data", "state.json"),
        Path.Combine(failedRoot, "state-data", ".marker"));
    var failedUploader = new RecordingManualDiscordUploader
    {
        Failure = new HttpRequestException("Simulated definite webhook rejection.")
    };
    var failedService = new EditedClipUploadService(failedStore, () => failedUploader);
    var failedPrepared = new PreparedClipEdit(
        failedOperation,
        failedRoot,
        failedOriginal,
        await ContentIdentity.ComputeSha256Async(failedOriginal, CancellationToken.None),
        failedEdited,
        await ContentIdentity.ComputeSha256Async(failedEdited, CancellationToken.None),
        "edit.mp4",
        "Failure Game",
        null,
        KeepOriginal: false,
        UsesOriginalAsArtifact: false,
        OutputBytes: new FileInfo(failedEdited).Length);
    var failedPostRejected = false;
    try
    {
        await failedService.UploadAsync(
            settings with { ClipsFolder = failedRoot },
            failedPrepared,
            null,
            null,
            CancellationToken.None);
    }
    catch (HttpRequestException)
    {
        failedPostRejected = true;
    }
    var failedState = await failedStore.LoadOrInitializeAsync(failedRoot, _ => { }, CancellationToken.None);
    Assert(failedPostRejected && failedUploader.UploadCount == 1 &&
           File.Exists(failedOriginal) &&
           (await File.ReadAllBytesAsync(failedOriginal)).SequenceEqual(failedOriginalBytes) &&
           !File.Exists(failedEdited) && !Directory.Exists(failedStageFolder) &&
           !failedState.UploadedContentHashes.Contains(failedPrepared.EditedContentHash) &&
           failedState.PendingEditedUploads.Count == 0,
        "A definite pre-confirmation upload failure must preserve the original byte-for-byte and clean staging without durable uploaded state.");
}

static void AssertClipEditProcessorContracts(string temporaryRoot)
{
    var root = Directory.CreateDirectory(Path.Combine(temporaryRoot, "clip-edit-processor-contracts")).FullName;
    var localGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreateLocalOnly(root),
        "Battlefield™-6")).FullName;
    var localPath = Path.Combine(localGame, "Local source.mp4");
    File.WriteAllBytes(localPath, [1, 3, 3, 7]);
    var local = ClipEditProcessor.ValidateLocalOnlySource(root, localPath);
    Assert(local.FullName == Path.GetFullPath(localPath),
        "The edit processor must accept a regular MP4 at the supported Local-only game depth.");

    var uploadedGame = Directory.CreateDirectory(Path.Combine(
        UploadedFolder.GetOrCreate(root),
        "Battlefield™-6")).FullName;
    var uploadedPath = Path.Combine(uploadedGame, "Already uploaded.mp4");
    File.WriteAllBytes(uploadedPath, [2, 4, 6, 8]);
    foreach (var rejectedPath in new[]
             {
                 uploadedPath,
                 Path.Combine(temporaryRoot, "outside-local-only.mp4"),
                 Path.Combine(localGame, "not-a-video.txt")
             })
    {
        if (!File.Exists(rejectedPath)) File.WriteAllBytes(rejectedPath, [9]);
        var rejected = false;
        try { ClipEditProcessor.ValidateLocalOnlySource(root, rejectedPath); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected,
            $"The edit processor must reject a source outside its regular Local-only MP4 layout: {Path.GetFileName(rejectedPath)}.");
    }

    var source = new GalleryClipEntry(
        localPath,
        Path.GetFileName(localPath),
        "Battlefield™-6",
        GalleryClipRoute.LocalOnly,
        1_000_000,
        DateTime.UtcNow);
    var request = new ManualClipEditRequest(
        source,
        TimeSpan.FromSeconds(2.345),
        TimeSpan.FromSeconds(5.555),
        MuteAudio: false,
        "Edited output.mp4",
        "Battlefield™-6",
        "Trimmed flank",
        KeepOriginal: false);
    var stagedPath = Path.Combine(root, ".clipcord-editing", "operation", "Edited output.mp4");
    var arguments = ClipEditProcessor.BuildRenderArguments(localPath, stagedPath, request).ToArray();
    static bool HasPair(IReadOnlyList<string> values, string option, string value)
    {
        for (var index = 0; index + 1 < values.Count; index++)
        {
            if (values[index] == option && values[index + 1] == value) return true;
        }
        return false;
    }
    Assert(HasPair(arguments, "-ss", "2.345000") &&
           HasPair(arguments, "-t", "3.210000") &&
           HasPair(arguments, "-map", "0:v:0") &&
           HasPair(arguments, "-map", "0:a:0?") &&
           HasPair(arguments, "-vf", "setpts=PTS-STARTPTS") &&
           HasPair(arguments, "-af", "asetpts=PTS-STARTPTS") &&
           HasPair(arguments, "-c:v", "libx264") &&
           HasPair(arguments, "-c:a", "aac") &&
           HasPair(arguments, "-map_metadata", "-1") &&
           HasPair(arguments, "-map_chapters", "-1") &&
           arguments.Contains("-sn") && arguments.Contains("-dn") &&
           arguments[^3] == "-f" && arguments[^2] == "mp4" && arguments[^1] == stagedPath,
        "An accurate unmuted edit must preserve the selected trim, rebuild timestamps/audio, strip metadata, and target MP4 without a shell command.");

    var muted = ClipEditProcessor.BuildRenderArguments(
        localPath,
        stagedPath,
        request with { MuteAudio = true }).ToArray();
    Assert(muted.Contains("-an") &&
           !muted.Contains("0:a:0?") &&
           !muted.Contains("-af") &&
           !muted.Contains("-c:a"),
        "A muted edit must explicitly remove audio and omit every audio map/filter/codec option.");

    var playbackPath = Path.Combine(root, ".clipcord-editing", "playback", "preview.mp4");
    var playback = ClipEditProcessor.BuildPlaybackArguments(
        localPath,
        playbackPath,
        TimeSpan.FromSeconds(4.5),
        TimeSpan.FromSeconds(9.75),
        muteAudio: false).ToArray();
    Assert(HasPair(playback, "-ss", "4.500000") &&
           HasPair(playback, "-t", "5.250000") &&
           HasPair(playback, "-map", "0:v:0") &&
           HasPair(playback, "-map", "0:a:0?") &&
           HasPair(playback, "-af", "asetpts=PTS-STARTPTS") &&
           playback[^3] == "-f" && playback[^2] == "mp4" && playback[^1] == playbackPath,
        "Trim preview playback must render exactly the selected range, not the whole source.");
    var mutedPlayback = ClipEditProcessor.BuildPlaybackArguments(
        localPath,
        playbackPath,
        TimeSpan.FromSeconds(4.5),
        TimeSpan.FromSeconds(9.75),
        muteAudio: true).ToArray();
    Assert(mutedPlayback.Contains("-an") &&
           !mutedPlayback.Contains("0:a:0?") &&
           !mutedPlayback.Contains("-af") &&
           !mutedPlayback.Contains("-c:a"),
        "A muted trim preview must drop audio and omit every audio map/filter/codec option.");
    var playbackRangeRejected = false;
    try
    {
        ClipEditProcessor.BuildPlaybackArguments(
            localPath,
            playbackPath,
            TimeSpan.FromSeconds(9.75),
            TimeSpan.FromSeconds(4.5),
            muteAudio: false);
    }
    catch (ArgumentOutOfRangeException) { playbackRangeRejected = true; }
    Assert(playbackRangeRejected,
        "Trim preview playback must reject an inverted selection instead of rendering the whole clip.");

    var processor = new ClipEditProcessor();
    var sourceBytes = 200_000_000L;
    var sourceDuration = TimeSpan.FromSeconds(100);
    var estimateFull = processor.EstimateOutputBytes(sourceBytes, sourceDuration, sourceDuration);
    var estimateHalf = processor.EstimateOutputBytes(sourceBytes, sourceDuration, TimeSpan.FromSeconds(50));
    var estimateQuarter = processor.EstimateOutputBytes(sourceBytes, sourceDuration, TimeSpan.FromSeconds(25));
    var estimateTenSeconds = processor.EstimateOutputBytes(sourceBytes, sourceDuration, TimeSpan.FromSeconds(10));
    var estimateOneSecond = processor.EstimateOutputBytes(sourceBytes, sourceDuration, TimeSpan.FromSeconds(1));
    var estimateTiny = processor.EstimateOutputBytes(sourceBytes, sourceDuration, TimeSpan.FromSeconds(0.25));
    var smallSourceBytes = 5_000_000L;
    var estimateTinyWithSlack = processor.EstimateOutputBytes(
        smallSourceBytes,
        sourceDuration,
        TimeSpan.FromSeconds(0.25));
    Assert(estimateFull > estimateHalf &&
           estimateHalf > estimateQuarter &&
           estimateQuarter > estimateTenSeconds &&
           estimateTenSeconds > estimateOneSecond &&
           estimateOneSecond > estimateTiny &&
           estimateTiny > 0,
        "The editor size estimate should strictly increase as the selected trim grows.");
    Assert(estimateQuarter < sourceBytes,
        "A quarter-length trim should reduce the estimate instead of pinning to the source size.");
    Assert(estimateHalf < sourceBytes * 4 && estimateFull < sourceBytes * 4.0,
        "The estimate should remain conservative for full clips but not explode to an unusable upper bound.");
    Assert(estimateTiny < estimateQuarter / 4 &&
           estimateFull >= sourceBytes,
        "The estimate should reflect short trims while still honoring conservative growth for full clips.");
    Assert(estimateHalf > sourceBytes * 0.5 * 2.0,
        "A half-length selection must stay meaningfully above linear scaling to cover CRF re-encode growth.");
    Assert(estimateTinyWithSlack > 1_000_000,
        "Small sources should retain the small-file safety slack so short trims aren't grossly under-estimated.");

    var outsideIdentity = Path.Combine(temporaryRoot, "outside-identity.mp4");
    File.WriteAllBytes(outsideIdentity, [1, 9, 8, 4]);
    var unsafeIdentityRejected = false;
    try
    {
        EditedClipDispositionProcessor.ValidatePendingPaths(new PendingEditedClipDisposition
        {
            Id = Guid.NewGuid(),
            ClipsFolder = root,
            EditedPath = outsideIdentity,
            OriginalLocalOnlyPath = outsideIdentity,
            DestinationPath = Path.Combine(uploadedGame, "identity.mp4"),
            EditedContentHash = "identity-hash",
            OriginalContentHash = "identity-hash"
        });
    }
    catch (Exception exception) when (exception is InvalidOperationException or IOException)
    {
        unsafeIdentityRejected = true;
    }
    Assert(unsafeIdentityRejected,
        "A recovered identity edit must still be confined to this clips folder's Local-only archive.");

    var movedIdentitySource = Path.Combine(localGame, "Identity source.mp4");
    File.WriteAllBytes(movedIdentitySource, [4, 8, 15, 16, 23, 42]);
    var movedIdentityHash = ContentIdentity.ComputeSha256Async(
        movedIdentitySource,
        CancellationToken.None).GetAwaiter().GetResult();
    var movedIdentityDestination = Path.Combine(uploadedGame, "Identity source.mp4");
    File.Move(movedIdentitySource, movedIdentityDestination);
    var identityRecycler = new RecordingOriginalClipRecycler(
        Directory.CreateDirectory(Path.Combine(root, "identity-recycle")).FullName);
    var identityResult = new EditedClipDispositionProcessor(identityRecycler).CompleteAsync(
        new PendingEditedClipDisposition
        {
            Id = Guid.NewGuid(),
            ClipsFolder = root,
            EditedPath = movedIdentitySource,
            OriginalLocalOnlyPath = movedIdentitySource,
            DestinationPath = movedIdentityDestination,
            EditedContentHash = movedIdentityHash,
            OriginalContentHash = movedIdentityHash,
            KeepOriginal = false,
            OutputBytes = new FileInfo(movedIdentityDestination).Length
        },
        CancellationToken.None).GetAwaiter().GetResult();
    Assert(identityResult.ArchivedPath == movedIdentityDestination &&
           !identityResult.OriginalKept && !identityResult.OriginalCleanupFailed &&
           identityRecycler.RecycledPaths.Count == 0,
        "Identity-edit recovery after the archive move must accept the matching destination and clear without reposting or recycling twice.");

    var stagingTarget = Directory.CreateDirectory(Path.Combine(temporaryRoot, "linked-edit-staging-target")).FullName;
    var linkedOperation = Directory.CreateDirectory(Path.Combine(stagingTarget, Guid.NewGuid().ToString("N"))).FullName;
    var linkedEdit = Path.Combine(linkedOperation, "linked-edit.mp4");
    File.WriteAllBytes(linkedEdit, [7, 7, 7]);
    var stagingLink = Path.Combine(root, ".clipcord-editing");
    CreateDirectoryJunction(stagingLink, stagingTarget);
    try
    {
        var stagingJunctionRejected = false;
        try
        {
            EditedClipDispositionProcessor.ValidatePendingPaths(new PendingEditedClipDisposition
            {
                Id = Guid.NewGuid(),
                ClipsFolder = root,
                EditedPath = Path.Combine(stagingLink, Path.GetFileName(linkedOperation), "linked-edit.mp4"),
                OriginalLocalOnlyPath = localPath,
                DestinationPath = Path.Combine(uploadedGame, "linked-edit.mp4"),
                EditedContentHash = "linked-edit-hash",
                OriginalContentHash = "local-hash"
            });
        }
        catch (IOException)
        {
            stagingJunctionRejected = true;
        }
        Assert(stagingJunctionRejected,
            "Confirmed-edit recovery must reject a junction at the app-owned staging root.");
    }
    finally
    {
        Directory.Delete(stagingLink);
    }
    Assert(File.Exists(linkedEdit),
        "Rejecting a linked edit-staging root must never modify its external target.");
}

static void AssertAboutPageSupport(string testRoot)
{
    Directory.CreateDirectory(testRoot);

    var watcherCases = new (string? Raw, bool DiscordRunning, AboutWatcherState State, string Label, string Detail)[]
    {
        ("Watcher error — restarting SecretClip.mp4", true, AboutWatcherState.NeedsAttention, "Needs attention", "Clip processing needs attention"),
        ("Setup required", false, AboutWatcherState.SetupRequired, "Setup required", "Finish setup to start watching"),
        ("Discord closed — uploader paused", false, AboutWatcherState.Paused, "Paused", "Discord is closed"),
        ("Saving SecretClip.mp4 locally", true, AboutWatcherState.LocalOnly, "Local only", "Saving new clips on this PC"),
        ("Uploaded SecretClip.mp4 — archive move pending", true, AboutWatcherState.Archiving, "Archiving", "Organizing a completed clip"),
        ("Compressing SecretClip.mp4", true, AboutWatcherState.Compressing, "Compressing", "Reducing a clip for Discord"),
        ("Hashing SecretClip.mp4", true, AboutWatcherState.Preparing, "Preparing clip", "Preparing a completed clip"),
        ("Applying settings — stopping current watcher", true, AboutWatcherState.Starting, "Starting", "Starting clip monitoring"),
        ("Discord open — watching for clips", true, AboutWatcherState.Watching, "Watching", "Discord is open"),
        ("Uploading SecretClip.mp4", true, AboutWatcherState.Uploading, "Uploading", "Sending a clip to Discord"),
        ("Uploading Setup Simulator.mp4", true, AboutWatcherState.Uploading, "Uploading", "Sending a clip to Discord"),
        ("Uploading Failed Mission.mp4", true, AboutWatcherState.Uploading, "Uploading", "Sending a clip to Discord"),
        ("Uploading Discord Closed Beta.mp4", true, AboutWatcherState.Uploading, "Uploading", "Sending a clip to Discord"),
        ("Hashing Upload Failed.mp4", true, AboutWatcherState.Preparing, "Preparing clip", "Preparing a completed clip"),
        ("unrecognized SecretClip.mp4 state", true, AboutWatcherState.Unavailable, "Status unavailable", "Status is temporarily unavailable"),
        (null, false, AboutWatcherState.Starting, "Starting", "Starting clip monitoring")
    };
    foreach (var testCase in watcherCases)
    {
        var presentation = AboutPageSupport.NormalizeWatcherStatus(testCase.Raw, testCase.DiscordRunning);
        Assert(presentation == new AboutWatcherPresentation(testCase.State, testCase.Label, testCase.Detail),
            $"About watcher status '{testCase.Raw}' normalized incorrectly: {presentation}.");
        Assert(!presentation.Label.Contains("SecretClip", StringComparison.Ordinal) &&
               !presentation.Detail.Contains("SecretClip", StringComparison.Ordinal),
            "About watcher presentation must never copy a clip filename from the live status string.");
    }

    var localAppData = Path.Combine(testRoot, "Local AppData");
    var installedExecutable = Path.Combine(localAppData, "Programs", "ClipsToDiscord", "ClipsToDiscord.exe");
    Assert(AboutPageSupport.ClassifyInstallation(installedExecutable, localAppData) == AboutInstallationType.Installed &&
           AboutPageSupport.ClassifyInstallation(installedExecutable.ToUpperInvariant(), localAppData) == AboutInstallationType.Installed,
        "About must recognize only the exact per-user installation path, case-insensitively.");
    Assert(AboutPageSupport.ClassifyInstallation(
               Path.Combine(localAppData, "Programs", "ClipsToDiscord-portable", "ClipsToDiscord.exe"),
               localAppData) == AboutInstallationType.Portable &&
           AboutPageSupport.ClassifyInstallation(Path.Combine(testRoot, "portable", "ClipsToDiscord.exe"), localAppData) ==
           AboutInstallationType.Portable,
        "A portable or near-prefix executable path must not be reported as an installed copy.");

    const string secretWebhook = "https://discord.com/api/v10/webhooks/123456/about-secret-token";
    const string secretUploader = "Private Uploader";
    var secretClipsFolder = Path.Combine(testRoot, "Private Profile", "Videos", "Secret Battlefield Clip");
    SensitiveDataRedactor.RegisterSecret(secretWebhook);
    var settings = new AppSettings(
        secretClipsFolder,
        secretWebhook,
        true,
        AppSettings.DefaultCompressionTargetMb,
        secretUploader,
        true);
    var capturedAt = new DateTimeOffset(2026, 8, 14, 18, 30, 0, TimeSpan.Zero);
    var facts = new AboutRuntimeFacts(
        new Version(9, 8, 7, 6),
        new Version(10, 0, 26100, 1),
        System.Runtime.InteropServices.Architecture.X64,
        System.Runtime.InteropServices.Architecture.X64,
        new Version(8, 0, 19),
        installedExecutable,
        localAppData,
        true,
        true,
        capturedAt);
    var snapshot = AboutStatusSnapshot.Create(
        settings,
        $"Uploading SecretClip.mp4 through {secretWebhook}",
        facts);
    var diagnostics = AboutPageSupport.BuildDiagnostics(snapshot);
    Assert(snapshot.Version == "9.8.7" &&
           snapshot.WatcherState == AboutWatcherState.Uploading &&
           snapshot.Watcher == "Uploading" &&
           snapshot.Routing == "Discord uploads" &&
           snapshot.Installation == "Installed copy",
        "About status snapshots must contain only the expected normalized product state.");
    Assert(diagnostics.Contains("Version: 9.8.7", StringComparison.Ordinal) &&
           diagnostics.Contains("Watcher: Uploading", StringComparison.Ordinal) &&
           diagnostics.Contains("Route: Discord uploads", StringComparison.Ordinal) &&
           diagnostics.Contains("Install type: Installed copy", StringComparison.Ordinal) &&
           diagnostics.Contains("FFmpeg: Available", StringComparison.Ordinal) &&
           diagnostics.Contains($"Captured (UTC): {capturedAt:O}", StringComparison.Ordinal),
        $"The safe diagnostic summary omitted its allow-listed facts:{Environment.NewLine}{diagnostics}");
    var expectedDiagnosticLines = new[]
    {
        "ClipCord diagnostics",
        $"Version: {snapshot.Version}",
        $"Operating system: {snapshot.OperatingSystem}",
        $"Architecture: {snapshot.Architecture}",
        $"Runtime: {snapshot.Runtime}",
        $"Install type: {snapshot.Installation}",
        $"Watcher: {snapshot.Watcher}",
        $"Route: {snapshot.Routing}",
        $"Discord: {snapshot.Discord}",
        $"Start with Windows: {snapshot.StartupDetail}",
        $"FFmpeg: {snapshot.Ffmpeg}",
        $"Captured (UTC): {snapshot.CapturedAtUtc:O}"
    };
    Assert(diagnostics.Split(Environment.NewLine, StringSplitOptions.None)
               .SequenceEqual(expectedDiagnosticLines, StringComparer.Ordinal),
        $"Copied diagnostics must contain only the fixed allow-listed lines:{Environment.NewLine}{diagnostics}");
    foreach (var privateValue in new[]
             {
                 secretWebhook,
                 "about-secret-token",
                 "/api/v10/webhooks/",
                 secretUploader,
                 secretClipsFolder,
                 "Private Profile",
                 "Secret Battlefield Clip",
                 "SecretClip.mp4"
             })
    {
        Assert(!diagnostics.Contains(privateValue, StringComparison.OrdinalIgnoreCase),
            $"The copied About diagnostics exposed private value '{privateValue}'.");
    }
    Assert(diagnostics.Length < 4096,
        "The copied About diagnostics must remain a concise bounded support summary.");

    var expectedLinks = new Dictionary<AboutLink, string>
    {
        [AboutLink.Repository] = "https://github.com/malikpervez/clips-to-discord",
        [AboutLink.ReportProblem] = "https://github.com/malikpervez/clips-to-discord/issues/new/choose",
        [AboutLink.ReleaseNotes] = "https://github.com/malikpervez/clips-to-discord/blob/main/CHANGELOG.md",
        [AboutLink.Roadmap] = "https://github.com/malikpervez/clips-to-discord/blob/main/ROADMAP.md",
        [AboutLink.Privacy] = "https://github.com/malikpervez/clips-to-discord/blob/main/docs/PRIVACY.md",
        [AboutLink.SecurityDesign] = "https://github.com/malikpervez/clips-to-discord/blob/main/docs/ARCHITECTURE.md",
        [AboutLink.ProjectLicense] = "https://github.com/malikpervez/clips-to-discord/blob/main/LICENSE",
        [AboutLink.ThirdPartyNotices] = "https://github.com/malikpervez/clips-to-discord/blob/main/THIRD_PARTY_NOTICES.md",
        [AboutLink.Troubleshooting] = "https://github.com/malikpervez/clips-to-discord/blob/main/docs/TROUBLESHOOTING.md"
    };
    Assert(Enum.GetValues<AboutLink>().Length == expectedLinks.Count,
        "Every About project link must be covered by the exact-target allow-list test.");
    foreach (var (link, expectedTarget) in expectedLinks)
    {
        var start = AboutPageSupport.CreateTrustedLinkStartInfo(link);
        var uri = new Uri(start.FileName, UriKind.Absolute);
        Assert(start.UseShellExecute && start.ArgumentList.Count == 0 &&
               uri.AbsoluteUri == new Uri(expectedTarget, UriKind.Absolute).AbsoluteUri &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
               uri.AbsolutePath.StartsWith("/malikpervez/clips-to-discord", StringComparison.Ordinal),
            $"About link {link} escaped its exact trusted GitHub target: {start.FileName}.");
    }
    foreach (var trustedTarget in new[]
             {
                 "https://github.com/malikpervez/clips-to-discord",
                 "https://github.com/malikpervez/clips-to-discord/",
                 "https://GITHUB.COM/MALIKPERVEZ/CLIPS-TO-DISCORD/blob/main/README.md"
             })
    {
        Assert(AboutPageSupport.IsTrustedProjectUri(new Uri(trustedTarget, UriKind.Absolute)),
            $"The About project allow-list rejected its own repository target: {trustedTarget}.");
    }
    foreach (var untrustedTarget in new[]
             {
                 "http://github.com/malikpervez/clips-to-discord",
                 "https://github.com.evil.example/malikpervez/clips-to-discord",
                 "https://evil.github.com/malikpervez/clips-to-discord",
                 "https://user@github.com/malikpervez/clips-to-discord",
                 "https://github.com:444/malikpervez/clips-to-discord",
                 "https://github.com/malikpervez/other",
                 "https://github.com/malikpervez/clips-to-discord.evil",
                 "https://github.com/malikpervez/clips-to-discord-archive",
                 "https://github.com/malikpervez/clips-to-discord/../other",
                 "https://github.com/malikpervez/clips-to-discord?redirect=https://evil.example",
                 "https://github.com/malikpervez/clips-to-discord#https://evil.example",
                 "https://github.com/%2Fmalikpervez/clips-to-discord",
                 "https://github.com/malikpervez%2Fclips-to-discord"
             })
    {
        Assert(!AboutPageSupport.IsTrustedProjectUri(new Uri(untrustedTarget, UriKind.Absolute)),
            $"The About project allow-list accepted a malicious or out-of-scope URI: {untrustedTarget}.");
    }
    Assert(!AboutPageSupport.IsTrustedProjectUri(null),
        "The About project allow-list must reject a missing URI.");
    var unknownLinkRejected = false;
    try
    {
        _ = AboutPageSupport.CreateTrustedLinkStartInfo((AboutLink)int.MaxValue);
    }
    catch (ArgumentOutOfRangeException)
    {
        unknownLinkRejected = true;
    }
    Assert(unknownLinkRejected, "An unknown About link must not be handed to the Windows shell.");

    var shellDataDirectory = Path.Combine(testRoot, "Data folder, 日本語");
    var openData = AboutPageSupport.CreateOpenDataFolderStartInfo(shellDataDirectory);
    var openMissingLogs = AboutPageSupport.CreateOpenLogsStartInfo(shellDataDirectory, logFileExists: false);
    var selectExistingLog = AboutPageSupport.CreateOpenLogsStartInfo(shellDataDirectory, logFileExists: true);
    Assert(openData.UseShellExecute && openData.FileName == "explorer.exe" &&
           openData.ArgumentList.SequenceEqual([shellDataDirectory]) &&
           openMissingLogs.UseShellExecute && openMissingLogs.FileName == "explorer.exe" &&
           openMissingLogs.ArgumentList.SequenceEqual([shellDataDirectory]),
        "About data/log folder actions must pass the exact folder as one Explorer argument.");
    var expectedLogPath = Path.Combine(shellDataDirectory, "app.log");
    Assert(selectExistingLog.UseShellExecute &&
           selectExistingLog.FileName == "explorer.exe" &&
           selectExistingLog.ArgumentList.Count == 0 &&
           selectExistingLog.Arguments == $"/select,\"{expectedLogPath}\"",
        "About must select an existing log with Explorer's exact safe /select quoting shape.");
}

static void AssertAboutViewActions(AppSettings settings)
{
    var dataDirectory = Path.Combine(settings.ClipsFolder, "about-action-data, 日本語");
    if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    var starts = new List<ProcessStartInfo>();
    string? copiedDiagnostics = null;
    var checkRequests = 0;
    using var view = new AboutView(
        settings,
        watcherStatusProvider: () => "Uploading SecretActionClip.mp4",
        discordRunningProvider: () => true,
        ffmpegExecutableProvider: () => @"C:\Program Files\ClipCord\ffmpeg.exe",
        processStarter: starts.Add,
        clipboardWriter: value => copiedDiagnostics = value,
        dataDirectory: dataDirectory);
    view.CheckUpdatesRequested += (_, _) => checkRequests++;
    using var host = new Form
    {
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-2000, -2000),
        ClientSize = new Size(1100, 720)
    };
    host.Controls.Add(view);
    host.Show();
    Application.DoEvents();
    view.RefreshViewport();

    Assert(view.CurrentSnapshot is
           {
               WatcherState: AboutWatcherState.Uploading,
               Watcher: "Uploading",
               Discord: "Open",
               Ffmpeg: "Available"
           },
        "The live About view must display only normalized runtime facts from its injected providers.");

    var buttons = EnumerateControls(view).OfType<Button>().ToDictionary(button => button.Name, StringComparer.Ordinal);
    buttons["AboutOpenLogsButton"].PerformClick();
    Assert(Directory.Exists(dataDirectory) && starts.Count == 1 &&
           starts[0].FileName == "explorer.exe" && starts[0].ArgumentList.SequenceEqual([dataDirectory]),
        "Open logs must open the injected data folder when app.log does not yet exist.");

    var logPath = Path.Combine(dataDirectory, "app.log");
    File.WriteAllText(logPath, "test log");
    buttons["AboutOpenLogsButton"].PerformClick();
    Assert(starts.Count == 2 && starts[1].FileName == "explorer.exe" &&
           starts[1].Arguments == $"/select,\"{logPath}\"",
        "Open logs must select the exact existing app.log path without launching a shell command.");

    buttons["AboutOpenDataFolderButton"].PerformClick();
    Assert(starts.Count == 3 && starts[2].FileName == "explorer.exe" &&
           starts[2].ArgumentList.SequenceEqual([dataDirectory]),
        "Open data folder must use only the injected safe directory.");

    buttons["AboutCopyDiagnosticsButton"].PerformClick();
    Assert(!string.IsNullOrWhiteSpace(copiedDiagnostics) &&
           copiedDiagnostics.Contains("ClipCord diagnostics", StringComparison.Ordinal) &&
           copiedDiagnostics.Contains("Watcher: Uploading", StringComparison.Ordinal) &&
           !copiedDiagnostics.Contains("SecretActionClip.mp4", StringComparison.Ordinal) &&
           !copiedDiagnostics.Contains(settings.WebhookUrl, StringComparison.Ordinal) &&
           !copiedDiagnostics.Contains(settings.UploaderName, StringComparison.Ordinal) &&
           !copiedDiagnostics.Contains(settings.ClipsFolder, StringComparison.OrdinalIgnoreCase),
        "Copy diagnostics must use the injected clipboard seam and exclude webhook, uploader, clip, and path values.");
    var actionStatus = EnumerateControls(view).OfType<Label>()
        .Single(label => label.Name == "AboutActionStatusLabel");
    Assert(actionStatus.Text == "Safe diagnostics copied — webhook and clip names excluded." &&
           actionStatus.AccessibleDescription == actionStatus.Text,
        "Copy diagnostics must announce its safe completion to visual and assistive-technology users.");

    var linkButtons = new[]
    {
        "AboutReportProblemButton",
        "AboutPrivacyButton",
        "AboutSecurityButton",
        "AboutGitHubButton",
        "AboutReleaseNotesButton",
        "AboutRoadmapButton",
        "AboutLicensesButton"
    };
    foreach (var buttonName in linkButtons) buttons[buttonName].PerformClick();
    Assert(starts.Count == 3 + linkButtons.Length,
        "Every About project action must route through the injected process-start seam exactly once.");
    foreach (var start in starts.Skip(3))
    {
        var uri = new Uri(start.FileName, UriKind.Absolute);
        Assert(start.UseShellExecute && start.ArgumentList.Count == 0 &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith("/malikpervez/clips-to-discord", StringComparison.Ordinal),
            $"An About project button attempted to open an untrusted target: {start.FileName}.");
    }

    var checkButton = buttons["AboutCheckUpdatesButton"];
    checkButton.PerformClick();
    Assert(checkRequests == 1,
        "The About update button must raise exactly one request through its owner-provided lifecycle.");
    view.SetBusy(true, updateChecksAvailable: true);
    Assert(buttons.Values.All(button => !button.Enabled),
        "A busy About update must disable update, diagnostics, and project actions together.");
    checkButton.PerformClick();
    Assert(checkRequests == 1,
        "A disabled About update button must not start a second request.");
    view.SetBusy(false, updateChecksAvailable: true);
    Assert(buttons.Values.All(button => button.Enabled),
        "About actions must be restored after the shared update lifecycle completes.");

    host.Hide();
}

static void AssertAboutLayout(SettingsForm form, float expectedScale)
{
    form.PerformLayout();
    var view = EnumerateControls(form).OfType<AboutView>().Single();
    view.RefreshViewport();
    var content = EnumerateControls(view).OfType<AboutContentLayout>().Single();
    content.PerformLayout();

    var expectedGap = (int)Math.Round(14 * expectedScale);
    Assert(content.ScaledGap == expectedGap,
        $"About card spacing did not scale from the approved 14px gap at {expectedScale:F1}x: {content.ScaledGap}px.");
    var bounds = new[]
    {
        "AboutHero",
        "AboutStatusCard",
        "AboutDiagnosticsCard",
        "AboutPrivacyCard",
        "AboutCreditsCard"
    }.ToDictionary(
        name => name,
        name =>
        {
            var control = EnumerateControls(view).Single(candidate => candidate.Name == name);
            return new Rectangle(control.PointToScreen(Point.Empty), control.Size);
        },
        StringComparer.Ordinal);
    var hero = bounds["AboutHero"];
    var status = bounds["AboutStatusCard"];
    var diagnostics = bounds["AboutDiagnosticsCard"];
    var privacy = bounds["AboutPrivacyCard"];
    var credits = bounds["AboutCreditsCard"];
    Assert(status.Top == diagnostics.Top && status.Bottom == diagnostics.Bottom &&
           Math.Abs(status.Width - diagnostics.Width) <= 1 &&
           diagnostics.Left - status.Right == expectedGap,
        $"The upper About cards must form an equal row with a {expectedGap}px gap: status={status}, diagnostics={diagnostics}.");
    Assert(privacy.Top == credits.Top && privacy.Bottom == credits.Bottom &&
           Math.Abs(privacy.Width - credits.Width) <= 1 &&
           credits.Left - privacy.Right == expectedGap,
        $"The lower About cards must form an equal row with a {expectedGap}px gap: privacy={privacy}, credits={credits}.");
    Assert(status.Top - hero.Bottom == expectedGap &&
           privacy.Top - status.Bottom == expectedGap &&
           hero.Left == status.Left && hero.Left == privacy.Left &&
           Math.Abs(hero.Right - diagnostics.Right) <= 1 &&
           Math.Abs(hero.Right - credits.Right) <= 1,
        $"The About hero and card grid must share one aligned content width and uniform vertical gaps: hero={hero}, status={status}, privacy={privacy}.");

    var labels = EnumerateControls(view).OfType<Label>().ToArray();
    var version = typeof(AboutView).Assembly.GetName().Version ?? new Version(0, 0, 0);
    var releaseVersion = labels.Single(label => label.Name == "AboutReleaseVersionLabel").Text;
    Assert(releaseVersion == $"Stable · v{AboutPageSupport.FormatApplicationVersion(version)}",
        $"About must display the three-part assembly product version, not a hard-coded or four-part value; got '{releaseVersion}'.");
    Assert(labels.Count(label => label.Text == "Dixon Yamada") == 1 &&
           labels.Count(label => label.Text == "Certified Looter") == 1 &&
           labels.Count(label => label.Text == "Papi Jawn") == 1 &&
           labels.Count(label => label.Text == "Certified Shooter · LeBron’s Legacy") == 1 &&
           !labels.Any(label => label.Text.Contains("Local-First Companion", StringComparison.OrdinalIgnoreCase)),
        "About must preserve the approved two-person credit copy and remove Local-First Companion.");
    Assert(EnumerateControls(view).Count(control => control.Name is "AboutDixonCredit" or "AboutPapiCredit") == 2 &&
           EnumerateControls(view).Single(control => control.Name == "AboutDixonCredit").AccessibleName ==
           "Dixon Yamada, Certified Looter" &&
           EnumerateControls(view).Single(control => control.Name == "AboutPapiCredit").AccessibleName ==
           "Papi Jawn, Certified Shooter · LeBron’s Legacy",
        "The two credit cards must expose the exact approved names and joined Papi Jawn title to assistive technology.");
    Assert(!EnumerateControls(view).OfType<ScrollableControl>().Any(control => control.AutoScroll),
        "About must use ClipCord's branded viewport instead of a native Windows scrollbar.");
}

static float GetDpiScale(Control control) => Math.Max(96, control.DeviceDpi) / 96f;

static void AssertAboutCopyAndAccessibility(SettingsForm form, bool requireVisible = true)
{
    var view = EnumerateControls(form).OfType<AboutView>().Single();
    var requiredButtons = new[]
    {
        "AboutCheckUpdatesButton",
        "AboutOpenLogsButton",
        "AboutCopyDiagnosticsButton",
        "AboutOpenDataFolderButton",
        "AboutReportProblemButton",
        "AboutPrivacyButton",
        "AboutSecurityButton",
        "AboutGitHubButton",
        "AboutReleaseNotesButton",
        "AboutRoadmapButton",
        "AboutLicensesButton"
    };
    var buttons = EnumerateControls(view).OfType<Button>()
        .Where(button => requiredButtons.Contains(button.Name, StringComparer.Ordinal))
        .ToArray();
    Assert(buttons.Length == requiredButtons.Length &&
           buttons.All(button => (!requireVisible || button.Visible) && button.TabStop &&
                                 button.AccessibleRole == AccessibleRole.PushButton &&
                                 !string.IsNullOrWhiteSpace(button.AccessibleName)),
        "Every About action must remain visible, keyboard reachable, and named as a push button.");

    var requiredCopy = new HashSet<string>(StringComparer.Ordinal)
    {
        "Your clips. Your choice. Your Discord.",
        "App status",
        "Help & diagnostics",
        "Privacy & security",
        "Credits & project",
        "Dixon Yamada",
        "Certified Looter",
        "Papi Jawn",
        "Certified Shooter · LeBron’s Legacy",
        "Check for updates",
        "Open logs",
        "Copy diagnostics",
        "Open data folder",
        "Report a problem",
        "Privacy details",
        "Security design",
        "GitHub",
        "Release notes",
        "Roadmap",
        "Licenses"
    };
    var textControls = EnumerateControls(view)
        .Where(control => (!requireVisible || control.Visible) && requiredCopy.Contains(control.Text))
        .ToArray();
    Assert(textControls.Select(control => control.Text).ToHashSet(StringComparer.Ordinal).SetEquals(requiredCopy),
        "The live About page is missing approved mockup copy or actions.");
    foreach (var control in textControls)
    {
        var measured = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine);
        Assert(measured.Width <= control.ClientSize.Width + 4 && measured.Height <= control.ClientSize.Height + 4,
            $"About text '{control.Text}' is ellipsized or clipped: measured={measured}, client={control.ClientSize}.");
    }

    var multilineLabels = EnumerateControls(view).OfType<Label>()
        .Where(label => (!requireVisible || label.Visible) &&
                        (label.Name is "AboutDescriptionLabel" or "AboutDisclaimerLabel" ||
                         label.Parent?.Name.StartsWith("AboutPrivacyStatement", StringComparison.Ordinal) == true))
        .ToArray();
    foreach (var label in multilineLabels)
    {
        var available = new Size(
            Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal),
            int.MaxValue);
        var measured = TextRenderer.MeasureText(
            label.Text,
            label.Font,
            available,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        Assert(measured.Height <= label.ClientSize.Height - label.Padding.Vertical + 4,
            $"About explanatory text '{label.Text}' is clipped: measured={measured}, client={label.ClientSize}, padding={label.Padding}, " +
            $"parent={label.Parent?.Name}:{label.Parent?.ClientSize}, container={label.Parent?.Parent?.Name}:{label.Parent?.Parent?.ClientSize}.");
    }

    foreach (var heading in new[] { "Help & diagnostics", "Privacy & security", "Credits & project" })
    {
        var label = EnumerateControls(view).OfType<Label>().Single(candidate => candidate.Text == heading);
        Assert(!label.UseMnemonic,
            $"About heading '{heading}' must paint its ampersand instead of treating it as a mnemonic marker.");
    }

    var expectedActionGlyphs = new Dictionary<string, BrandGlyph>
    {
        ["AboutOpenLogsButton"] = BrandGlyph.FileText,
        ["AboutCopyDiagnosticsButton"] = BrandGlyph.Copy,
        ["AboutOpenDataFolderButton"] = BrandGlyph.FolderOpen,
        ["AboutReportProblemButton"] = BrandGlyph.ReportProblem
    };
    foreach (var (name, glyph) in expectedActionGlyphs)
    {
        var button = EnumerateControls(view).OfType<OutlineButton>().Single(candidate => candidate.Name == name);
        Assert(button.LeadingGlyph == glyph,
            $"About action '{name}' must retain its approved leading icon ({glyph}).");
    }

    var sectionIcons = EnumerateControls(view).OfType<AboutSectionIcon>().ToArray();
    var sectionGlyphs = sectionIcons
        .Select(icon => icon.Glyph)
        .OrderBy(glyph => glyph)
        .ToArray();
    var expectedSectionGlyphs = new[]
    {
        BrandGlyph.Shield,
        BrandGlyph.AppStatus,
        BrandGlyph.Diagnostics,
        BrandGlyph.Credits,
        BrandGlyph.Shield
    }.OrderBy(glyph => glyph).ToArray();
    Assert(sectionGlyphs.SequenceEqual(expectedSectionGlyphs),
        $"About section artwork must remain distinct and mockup-aligned: {string.Join(", ", sectionGlyphs)}.");
    Assert(sectionIcons.All(icon => icon.AccessibleRole == AccessibleRole.None &&
                                    string.IsNullOrWhiteSpace(icon.AccessibleName) &&
                                    !icon.TabStop),
        "Decorative About section icons must stay silent and out of the accessibility focus order.");

    var content = EnumerateControls(view).OfType<AboutContentLayout>().Single();
    var effectiveScale = content.ScaledGap / 14f;
    var settingsIconSides = EnumerateControls(form).OfType<BrandIconTile>()
        .Select(icon => icon.Width)
        .Distinct()
        .ToArray();
    var featureIcons = sectionIcons.Where(icon => icon.Name == "AboutFeatureIcon").ToArray();
    var featureIconSides = featureIcons.Select(icon => icon.Width).Distinct().ToArray();
    var releaseIcon = sectionIcons.Single(icon => icon.Name == "AboutReleaseIcon");
    Assert(AboutView.ReleaseIconLogicalSize == 44 && AboutView.FeatureIconLogicalSize == 48,
        "The approved About icon hierarchy must remain 44px for the compact release badge and 48px for feature cards.");
    Assert(featureIcons.Length == 4 &&
           settingsIconSides.Length == 1 && featureIconSides.Length == 1 &&
           featureIcons.All(icon => Math.Abs(icon.Width - icon.Height) <= 1) &&
           featureIconSides[0] >= AboutView.FeatureIconLogicalSize &&
           featureIconSides[0] >= (int)Math.Floor(settingsIconSides[0] * 0.72f) &&
           featureIconSides[0] <= (int)Math.Ceiling(settingsIconSides[0] * 1.05f),
        $"The four About feature-card icons must stay uniformly enlarged and visually comparable to Settings: " +
        $"About={string.Join(", ", featureIcons.Select(icon => $"{icon.Glyph}={icon.Size}"))}; " +
        $"Settings side={string.Join(", ", settingsIconSides)}.");
    var expectedReleaseIconSide = (int)Math.Round(
        featureIconSides[0] * AboutView.ReleaseIconLogicalSize / (double)AboutView.FeatureIconLogicalSize);
    Assert(Math.Abs(releaseIcon.Width - releaseIcon.Height) <= 1 &&
           Math.Abs(releaseIcon.Width - expectedReleaseIconSide) <= 1 &&
           releaseIcon.Width < featureIconSides[0],
        $"The compact About release icon must retain its {AboutView.ReleaseIconLogicalSize}:" +
        $"{AboutView.FeatureIconLogicalSize} scale ratio as a square " +
        $"without competing with the feature-card icons: release={releaseIcon.Size}, " +
        $"features={string.Join(", ", featureIconSides)}, expected side={expectedReleaseIconSide}px.");
    var expectedAvatarSide = (int)Math.Round(34 * effectiveScale);
    foreach (var avatarName in new[] { "AboutDixonCreditAvatar", "AboutPapiCreditAvatar" })
    {
        var avatar = EnumerateControls(view).OfType<AboutAvatarControl>().Single(control => control.Name == avatarName);
        var preferredSize = avatar.GetPreferredSize(Size.Empty);
        Assert(Math.Abs(avatar.Width - avatar.Height) <= 1 &&
               Math.Abs(avatar.Width - expectedAvatarSide) <= 2 &&
               preferredSize.Width == preferredSize.Height &&
               Math.Abs(preferredSize.Width - expectedAvatarSide) <= 2,
            $"About credit avatar '{avatarName}' must remain a DPI-scaled square: actual={avatar.Bounds}, " +
            $"preferred={preferredSize}, expected side {expectedAvatarSide}px.");
    }

    var maximumLinkHeight = (int)Math.Ceiling(42 * effectiveScale);
    foreach (var name in new[]
             {
                 "AboutPrivacyButton", "AboutSecurityButton", "AboutGitHubButton",
                 "AboutReleaseNotesButton", "AboutRoadmapButton", "AboutLicensesButton"
             })
    {
        var button = EnumerateControls(view).OfType<Button>().Single(candidate => candidate.Name == name);
        Assert(button.Height <= maximumLinkHeight,
            $"About link '{name}' expanded into an oversized action bar: {button.Height}px at {effectiveScale:F2}x.");
    }
}

static void AssertManualCheckCloseProtection(
    AppSettings settings,
    SettingsPage openingPage,
    string initiatingButtonName)
{
    var releaseCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var checkedBusyState = false;
    var invocationCount = 0;
    IWin32Window? callbackOwner = null;
    using var form = new SettingsForm(
        settings,
        checkForUpdatesAsync: async owner =>
        {
            invocationCount++;
            callbackOwner = owner;
            await releaseCheck.Task;
        },
        initialPage: openingPage);
    form.Shown += (_, _) =>
    {
        var buttons = EnumerateControls(form).OfType<Button>().ToArray();
        var checkButton = buttons.Single(button => button.Name == initiatingButtonName);
        var cancelButton = buttons.Single(button => button.Text == (openingPage == SettingsPage.Settings ? "Cancel" : "Close"));
        var titleButtons = EnumerateControls(form).OfType<TitleBarButton>().ToArray();
        checkButton.PerformClick();

        var inspectTimer = new System.Windows.Forms.Timer { Interval = 20 };
        inspectTimer.Tick += (_, _) =>
        {
            inspectTimer.Stop();
            inspectTimer.Dispose();
            var updateButtons = buttons.Where(button =>
                button.Name is "SettingsCheckUpdatesButton" or "AboutCheckUpdatesButton").ToArray();
            var aboutActions = buttons.Where(button => button.Name.StartsWith("About", StringComparison.Ordinal)).ToArray();
            Assert(invocationCount == 1 && ReferenceEquals(callbackOwner, form) &&
                   updateButtons.All(button => !button.Enabled) &&
                   aboutActions.All(button => !button.Enabled) &&
                   !cancelButton.Enabled && titleButtons.All(button => !button.Enabled),
                $"{initiatingButtonName} must use the shared single-flight update lifecycle and disable every conflicting action.");
            foreach (var updateButton in updateButtons) updateButton.PerformClick();
            Assert(invocationCount == 1,
                "Disabled Settings/About update buttons must not start a concurrent update check.");
            form.Close();
            Assert(form.Visible,
                $"A user close request must not dispose {openingPage} while its update callback is in flight.");
            checkedBusyState = true;
            releaseCheck.TrySetResult();

            var closeTimer = new System.Windows.Forms.Timer { Interval = 20 };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                closeTimer.Dispose();
                form.Close();
            };
            closeTimer.Start();
        };
        inspectTimer.Start();
    };

    form.ShowDialog();
    Assert(checkedBusyState && invocationCount == 1 && ReferenceEquals(callbackOwner, form),
        $"The {initiatingButtonName} close-protection test did not complete through the shared callback.");
}

static void AssertGalleryScaledLayout(AppSettings settings, float scale)
{
    var scaledFonts = new Dictionary<(string Family, float Size, FontStyle Style), Font>();
    try
    {
        using var form = new SettingsForm(
            settings,
            checkForUpdatesAsync: _ => Task.CompletedTask);
        form.Show();
        form.ShowPage(SettingsPage.Gallery);
        WaitForUiCondition(
            () => EnumerateControls(form).Any(control => control.Name == "GalleryGameCard"),
            TimeSpan.FromSeconds(5),
            $"Gallery did not populate before the {scale:F1}x layout check.");
        TraceSmokeStep($"Gallery {scale:F1}x: archive populated");
        var gallery = EnumerateControls(form).OfType<GalleryView>().Single();
        var topNavigation = EnumerateControls(form)
            .Single(control => control.Name == "TopNavigation");
        // Synthetic scaling should model Windows' startup-DPI pass, which occurs
        // before the window is displayed. Scaling an already-visible oversized
        // form can deadlock WinForms layout on a small headless CI desktop.
        form.Hide();
        TraceSmokeStep($"Gallery {scale:F1}x: applying synthetic startup scale");
        form.Scale(new SizeF(scale, scale));
        TraceSmokeStep($"Gallery {scale:F1}x: geometry scaled");
        var featureControls = new[] { (Control)gallery, topNavigation }
            .SelectMany(root => new[] { root }.Concat(EnumerateControls(root)))
            .Distinct();
        foreach (var control in featureControls)
        {
            var source = control.Font;
            var key = (source.FontFamily.Name, source.Size * scale, source.Style);
            if (!scaledFonts.TryGetValue(key, out var scaledFont))
            {
                scaledFont = new Font(source.FontFamily, key.Item2, source.Style, GraphicsUnit.Point);
                scaledFonts.Add(key, scaledFont);
            }
            control.Font = scaledFont;
        }
        TraceSmokeStep($"Gallery {scale:F1}x: fonts scaled");
        form.PerformLayout();
        Application.DoEvents();
        TraceSmokeStep($"Gallery {scale:F1}x: layout completed");
        AssertControlsFit(form);
        AssertCriticalTextFits(form);
        TraceSmokeStep($"Gallery {scale:F1}x: assertions passed");
    }
    finally
    {
        foreach (var font in scaledFonts.Values) font.Dispose();
    }
}

static void AssertGalleryPreHandleLifecycle(AppSettings settings)
{
    using var gallery = new GalleryView(settings.ClipsFolder);
    Assert(!gallery.IsHandleCreated && gallery.Parent is null,
        "The pre-handle Gallery lifecycle test must begin before the view is parented or shown.");
    gallery.Activate(settings.ClipsFolder);
    gallery.Deactivate();
    Thread.Sleep(50);
    Application.DoEvents();
    var refresh = EnumerateControls(gallery)
        .OfType<Button>()
        .Single(button => button.Name == "RefreshGalleryButton");
    Assert(refresh.Enabled &&
           EnumerateControls(gallery).All(control => control.Name != "GalleryGameCard"),
        "Deactivate must cancel a pre-handle scan, restore Refresh, and reject its late completion.");

    gallery.Activate(settings.ClipsFolder);
    WaitForUiCondition(
        () => EnumerateControls(gallery).Any(control => control.Name == "GalleryGameCard"),
        TimeSpan.FromSeconds(5),
        "Activate must populate Gallery even before its control handle is created.");
    Assert(!gallery.IsHandleCreated,
        "On-demand Gallery scanning must not require an invisible native window handle.");
    gallery.Deactivate();
    var cardsBeforeInactiveRefresh = EnumerateControls(gallery)
        .Count(control => control.Name == "GalleryGameCard");
    gallery.RefreshCatalog(settings.ClipsFolder);
    Application.DoEvents();
    Assert(refresh.Enabled &&
           EnumerateControls(gallery).Count(control => control.Name == "GalleryGameCard") == cardsBeforeInactiveRefresh,
        "RefreshCatalog must remain a no-op while Gallery is inactive.");
}

static void AssertControlsFit(Form form)
{
    form.PerformLayout();
    foreach (var control in EnumerateControls(form).Where(control => control.Visible))
    {
        control.PerformLayout();
        if (control.Parent is not null && !IsAutoScrollViewport(control.Parent))
        {
            var parentBounds = control.Parent.ClientRectangle;
            Assert(control.Left >= -1 && control.Top >= -1 &&
                   control.Right <= parentBounds.Right + 1 &&
                   control.Bottom <= parentBounds.Bottom + 1,
                $"Control {control.GetType().Name} '{control.Name}' ('{control.Text}') is clipped by parent " +
                $"{control.Parent.GetType().Name} '{control.Parent.Name}': child={control.Bounds}, parent={parentBounds}, " +
                $"grandparent={control.Parent.Parent?.GetType().Name} '{control.Parent.Parent?.Name}' bounds={control.Parent.Parent?.Bounds}.");
        }
        var bounds = control.Bounds;
        for (var parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
        {
            bounds.Offset(parent.Left, parent.Top);
        }

        if (!HasAutoScrollAncestor(control))
        {
            Assert(bounds.Left >= 0 && bounds.Top >= 0 &&
                   bounds.Right <= form.ClientSize.Width + 1 &&
                   bounds.Bottom <= form.ClientSize.Height + 1,
                $"Control {control.GetType().Name} '{control.Name}' ('{control.Text}') is clipped at {bounds} inside {form.ClientSize}; parent={control.Parent?.GetType().Name} '{control.Parent?.Name}' bounds={control.Parent?.Bounds}.");
        }
    }
}

static void AssertSettingsCardsOpenWithoutScrolling(SettingsForm form)
{
    var cards = EnumerateControls(form)
        .OfType<ScrollableControl>()
        .Single(control => control.Name == "SettingsCards");
    cards.PerformLayout();
    Assert(!cards.VerticalScroll.Visible && cards.AutoScrollPosition.Y == 0,
        $"Settings must open with every card visible without scrolling; viewport={cards.ClientSize}, display={cards.DisplayRectangle}.");
}

static void AssertSettingsCardsScrollOnlyWhenScreenConstrained(SettingsForm form, Size designedOpeningSize)
{
    var cards = EnumerateControls(form)
        .OfType<ScrollableControl>()
        .Single(control => control.Name == "SettingsCards");
    if (!cards.VerticalScroll.Visible) return;
    Assert(form.Height < designedOpeningSize.Height,
        $"Settings may scroll only when the screen reduced its designed opening height; designed={designedOpeningSize}, actual={form.Size}.");
}

static void AssertSettingsTextFieldsAligned(SettingsForm form)
{
    var fields = EnumerateControls(form)
        .OfType<TextBox>()
        .Where(control => control.AccessibleName is "Clips folder" or "Uploader name" or "Discord webhook URL")
        .OrderBy(control => control.AccessibleName, StringComparer.Ordinal)
        .ToArray();
    Assert(fields.Length == 3, "The three primary Settings text fields must remain discoverable.");
    var screenBounds = fields
        .Select(control => new Rectangle(control.PointToScreen(Point.Empty), control.Size))
        .ToArray();
    Assert(screenBounds.Select(bounds => bounds.Left).Distinct().Count() == 1 &&
           screenBounds.Select(bounds => bounds.Height).Distinct().Count() == 1,
        $"Clip source and Discord destination fields must share one left edge and height: {string.Join(", ", screenBounds)}.");
    Assert(screenBounds.All(bounds => bounds.Width >= 240),
        $"Every primary Settings field must remain usable rather than merely unclipped: {string.Join(", ", screenBounds)}.");
}

static void AssertSettingsCardGrid(SettingsForm form)
{
    var cards = new[]
    {
        "ClipSourceCard",
        "DiscordDestinationCard",
        "UploadBehaviorCard",
        "AppPreferencesCard"
    }.ToDictionary(
        name => name,
        name => EnumerateControls(form).Single(control => control.Name == name),
        StringComparer.Ordinal);

    var bounds = cards.ToDictionary(
        pair => pair.Key,
        pair => new Rectangle(pair.Value.PointToScreen(Point.Empty), pair.Value.Size),
        StringComparer.Ordinal);
    var source = bounds["ClipSourceCard"];
    var discord = bounds["DiscordDestinationCard"];
    var upload = bounds["UploadBehaviorCard"];
    var preferences = bounds["AppPreferencesCard"];

    Assert(source.Left == discord.Left && source.Right == discord.Right && source.Width == discord.Width,
        $"Clip source and Discord destination cards must have identical full-width bounds: source={source}, discord={discord}.");
    Assert(Math.Abs(upload.Width - preferences.Width) <= 1 &&
           Math.Abs(upload.Top - preferences.Top) <= 1 &&
           Math.Abs(upload.Bottom - preferences.Bottom) <= 1,
        $"Upload behavior and App preferences must form one equal-height, equal-width row: upload={upload}, preferences={preferences}.");
    Assert(upload.Right < preferences.Left &&
           upload.Left == source.Left &&
           Math.Abs(preferences.Right - source.Right) <= 1,
        $"The lower Settings cards must divide the same width as the full cards with a positive gap: source={source}, upload={upload}, preferences={preferences}.");

    var uploadTail = GetUnpaintedCardTail(cards["UploadBehaviorCard"]);
    var preferencesTail = GetUnpaintedCardTail(cards["AppPreferencesCard"]);
    var uploadTailLimit = GetMaximumCardTail(cards["UploadBehaviorCard"]);
    var preferencesTailLimit = GetMaximumCardTail(cards["AppPreferencesCard"]);
    Assert(uploadTail <= uploadTailLimit && uploadTail <= (int)Math.Ceiling(upload.Height * .20) &&
           preferencesTail <= preferencesTailLimit && preferencesTail <= (int)Math.Ceiling(preferences.Height * .20),
        $"The lower Settings cards must hug their painted content instead of stretching into empty panels: uploadTail={uploadTail}/{upload.Height}, preferencesTail={preferencesTail}/{preferences.Height}.");

    var compression = EnumerateControls(form)
        .OfType<TextBox>()
        .Single(control => control.AccessibleName == "Compression target in megabytes");
    var compressionHost = EnumerateControls(form)
        .OfType<RoundedPanel>()
        .Single(control => ReferenceEquals(control.Tag, compression));
    var presetButton = EnumerateControls(form)
        .OfType<OutlineButton>()
        .Single(control => control.Name == "CompressionTargetPresetButton");
    var expectedInputBackColor = SystemInformation.HighContrast ? SystemColors.Window : ClipCordTheme.SettingsField;
    var expectedInputForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : ClipCordTheme.ShellText;
    var expectedButtonOutline = SystemInformation.HighContrast ? SystemColors.WindowText : Color.Transparent;
    Assert(compression.BorderStyle == BorderStyle.None &&
           compression.BackColor == expectedInputBackColor &&
           compression.ForeColor == expectedInputForeColor &&
           presetButton.SurfaceColor == expectedInputBackColor &&
           presetButton.OutlineColor == expectedButtonOutline &&
           presetButton.AccessibleRole == AccessibleRole.PushButton &&
           !string.IsNullOrWhiteSpace(presetButton.AccessibleName),
        "The compression selector must use a borderless dark editor and branded accessible preset button rather than native ComboBox chrome.");
    Assert(!EnumerateControls(form).OfType<ComboBox>()
               .Any(control => control.AccessibleName == "Compression target in megabytes"),
        "The compression selector must not regress to native ComboBox chrome.");
    Assert(SettingsForm.CompressionTargetPresets.SequenceEqual([5, 10, 25, 50, 75, 95, 100]),
        "The branded compression picker must preserve the seven established preset choices.");
    if (!SystemInformation.HighContrast)
    {
        using var bitmap = new Bitmap(compressionHost.Width, compressionHost.Height);
        compressionHost.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var darkPixels = 0;
        var sampledPixels = 0;
        for (var y = 2; y < bitmap.Height - 2; y++)
        {
            for (var x = 2; x < bitmap.Width - 2; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var luminance = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                if (luminance <= 100) darkPixels++;
                sampledPixels++;
            }
        }
        Assert(sampledPixels > 0 && darkPixels >= sampledPixels * .70,
            $"The branded compression picker rendered an unexpected light surface: darkPixels={darkPixels}/{sampledPixels}.");
    }
}

static int GetUnpaintedCardTail(Control card)
{
    var paintedBottom = EnumerateControls(card)
        .Where(control => control.Width > 0 && control.Height > 0 &&
                          control is Label or Button or TextBox or CheckBox or BrandIconTile)
        .Select(control => GetLocationRelativeToAncestor(control, card).Y + control.Height)
        .DefaultIfEmpty(0)
        .Max();
    return Math.Max(0, card.ClientSize.Height - paintedBottom);
}

static Point GetLocationRelativeToAncestor(Control control, Control ancestor)
{
    var x = 0;
    var y = 0;
    for (Control? current = control; current is not null && current != ancestor; current = current.Parent)
    {
        x += current.Left;
        y += current.Top;
    }
    return new Point(x, y);
}

static int GetMaximumCardTail(Control card)
{
    var tile = EnumerateControls(card).OfType<BrandIconTile>().Single();
    var scale = tile.Width / 64d;
    return Math.Max(12, (int)Math.Ceiling(32 * scale));
}

static void AssertCompressionTargetPickerInteraction(SettingsForm form)
{
    var input = EnumerateControls(form)
        .OfType<TextBox>()
        .Single(control => control.AccessibleName == "Compression target in megabytes");
    var button = EnumerateControls(form)
        .OfType<OutlineButton>()
        .Single(control => control.Name == "CompressionTargetPresetButton");
    var menuField = typeof(SettingsForm).GetField(
        "_compressionTargetMenu",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var menu = (ContextMenuStrip)menuField.GetValue(form)!;
    var expectedItems = SettingsForm.CompressionTargetPresets.Select(value => $"{value} MB").ToArray();
    Assert(menu.Items.Cast<ToolStripItem>().Select(item => item.Text).SequenceEqual(expectedItems),
        "The compression preset popup must expose the seven established choices in order.");

    button.PerformClick();
    Application.DoEvents();
    Assert(menu.Visible, "The compression preset button must open its branded popup.");
    menu.Close();

    var originalText = input.Text;
    menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "25 MB").PerformClick();
    Assert(input.Text == "25 MB", "Choosing a compression preset must update the editable target.");
    input.Text = "37 MB";
    Assert(SettingsForm.TryParseCompressionTarget(input.Text, out var arbitraryTarget) && arbitraryTarget == 37,
        "The branded picker must preserve direct arbitrary 1-100 MB entry.");

    var keyArgs = new KeyEventArgs(Keys.F4);
    typeof(Control).GetMethod(
            "OnKeyDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(input, [keyArgs]);
    Application.DoEvents();
    Assert(keyArgs.Handled && keyArgs.SuppressKeyPress && menu.Visible,
        "F4 must open the compression preset popup without inserting text.");
    menu.Close();
    input.Text = originalText;
}

static void AssertSettingsFeatureIcons(SettingsForm form, int minimumSide = 64)
{
    var expected = new Dictionary<string, BrandGlyph>(StringComparer.Ordinal)
    {
        ["ClipSourceCardIcon"] = BrandGlyph.ClipSource,
        ["DiscordDestinationCardIcon"] = BrandGlyph.DiscordDestination,
        ["UploadBehaviorCardIcon"] = BrandGlyph.UploadBehavior,
        ["AppPreferencesCardIcon"] = BrandGlyph.AppPreferences
    };
    var tiles = EnumerateControls(form).OfType<BrandIconTile>().ToArray();
    Assert(tiles.Length == expected.Count,
        $"Settings must expose exactly four feature icon tiles; found {tiles.Length}.");
    Assert(tiles.Select(tile => tile.Size).Distinct().Count() == 1 &&
           tiles[0].Width >= minimumSide &&
           tiles[0].Height >= minimumSide,
        $"All feature icons must share the approved enlarged size of at least {minimumSide}px: {string.Join(", ", tiles.Select(tile => $"{tile.Name}={tile.Size}"))}.");

    var signatures = new HashSet<ulong>();
    foreach (var tile in tiles)
    {
        Assert(expected.TryGetValue(tile.Name, out var expectedGlyph) && tile.Glyph == expectedGlyph,
            $"Feature icon '{tile.Name}' uses {tile.Glyph} instead of its dedicated {expectedGlyph} artwork.");
        Assert(tile.AccessibleRole == AccessibleRole.Graphic && !string.IsNullOrWhiteSpace(tile.AccessibleName),
            $"Feature icon '{tile.Name}' must remain a named, noninteractive graphic.");

        using var bitmap = new Bitmap(tile.Width, tile.Height);
        tile.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var brightCount = 0;
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        ulong signature = 1469598103934665603;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var bright = pixel.R >= 210 && pixel.G >= 210 && pixel.B >= 210;
                signature ^= bright ? (byte)1 : (byte)0;
                signature *= 1099511628211;
                if (!bright) continue;
                brightCount++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert(brightCount >= bitmap.Width * bitmap.Height / 100 && right > left && bottom > top,
            $"Feature icon '{tile.Name}' did not paint enough high-contrast detail ({brightCount} pixels).");
        Assert(right - left + 1 >= bitmap.Width * .45 && bottom - top + 1 >= bitmap.Height * .45,
            $"Feature icon '{tile.Name}' is still visually undersized: detailBounds={Rectangle.FromLTRB(left, top, right + 1, bottom + 1)}, tile={bitmap.Size}.");
        signatures.Add(signature);
    }

    Assert(signatures.Count == expected.Count,
        "Each Settings section must use distinct feature artwork; duplicate rendered icon masks were found.");
}

static void AssertSettingsRoundTrip(AppSettings original)
{
    using var form = new SettingsForm(original, checkForUpdatesAsync: _ => Task.CompletedTask);
    form.Show();
    Application.DoEvents();
    var controls = EnumerateControls(form).ToArray();
    var changedFolder = Directory.CreateDirectory(Path.Combine(original.ClipsFolder, "round-trip-clips")).FullName;
    const string changedWebhook = "https://discord.com/api/v10/webhooks/987654/round-trip-token";
    ((TextBox)controls.Single(control => control.AccessibleName == "Clips folder")).Text = changedFolder;
    ((TextBox)controls.Single(control => control.AccessibleName == "Discord webhook URL")).Text = changedWebhook;
    ((TextBox)controls.Single(control => control.AccessibleName == "Uploader name")).Text = "Round Trip User";
    ((TextBox)controls.Single(control => control.AccessibleName == "Compression target in megabytes")).Text = "37 MB";
    ((TextBox)controls.Single(control => control.AccessibleName == "Global upload-mode shortcut")).Text =
        "Ctrl + Shift + U";
    var startup = controls.OfType<CheckBox>().Single(control => control.Text == "Start with Windows");
    startup.Checked = !original.StartWithWindows;
    var uploadToDiscord = controls.OfType<CheckBox>()
        .Single(control => control.Name == "UploadToDiscordToggle");
    uploadToDiscord.Checked = !original.UploadToDiscord;
    controls.OfType<Button>().Single(control => control.Text == "Save changes").PerformClick();

    Assert(form.SavedSettings is not null &&
           form.SavedSettings.ClipsFolder == changedFolder &&
           form.SavedSettings.WebhookUrl == changedWebhook &&
           form.SavedSettings.UploaderName == "Round Trip User" &&
           form.SavedSettings.StartWithWindows == !original.StartWithWindows &&
           form.SavedSettings.CompressionTargetMb == 37 &&
           form.SavedSettings.UploadToDiscord == !original.UploadToDiscord &&
           form.SavedSettings.ModeToggleHotkey == "Ctrl + Shift + U",
        "Every settings value must survive the branded form save round trip.");
}

static void AssertCriticalTextFits(Form form)
{
    var criticalText = new HashSet<string>(StringComparer.Ordinal)
    {
        "Clip source",
        "Clips folder",
        "Discord destination",
        "Uploader name",
        "Webhook URL",
        "Upload behavior",
        "App preferences",
        "Compression target",
        "Mode shortcut",
        "Upload new clips to Discord",
        "Local only",
        "Start with Windows",
        "Save changes",
        "Cancel",
        "Recent activity",
        "Open uploaded folder",
        "Open logs",
        "Show in folder",
        "Settings",
        "Activity",
        "Gallery",
        "About",
        "Refresh",
        "Open clips folder",
        "All games",
        "Play clip",
        "Close"
    };
    foreach (var control in EnumerateControls(form).Where(control => control.Visible && criticalText.Contains(control.Text)))
    {
        var measured = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine);
        Assert(measured.Width <= control.ClientSize.Width + 4 && measured.Height <= control.ClientSize.Height + 4,
            $"Text '{control.Text}' does not fit {control.GetType().Name}: measured={measured}, client={control.ClientSize}.");
    }

    foreach (var toggle in EnumerateControls(form).OfType<ToggleSwitch>())
    {
        var toggleText = TextRenderer.MeasureText(toggle.Text, toggle.Font, Size.Empty, TextFormatFlags.SingleLine);
        Assert(toggleText.Width <= toggle.GetTextBounds().Width + 4 &&
               toggleText.Height <= toggle.GetTextBounds().Height + 4,
            $"Toggle text '{toggle.Text}' does not fit its painted text area: measured={toggleText}, paintBounds={toggle.GetTextBounds()}.");
    }

    foreach (var layout in EnumerateControls(form).OfType<TableLayoutPanel>())
    {
        var children = layout.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
        for (var first = 0; first < children.Length; first++)
        {
            for (var second = first + 1; second < children.Length; second++)
            {
                Assert(!children[first].Bounds.IntersectsWith(children[second].Bounds),
                    $"Sibling controls '{children[first].Text}' {children[first].Bounds} and " +
                    $"'{children[second].Text}' {children[second].Bounds} overlap in {layout.Name} {layout.ClientRectangle}.");
            }
        }
    }
}

static void AssertAccessibility(Form form)
{
    foreach (var input in EnumerateControls(form).Where(control => control is TextBox or ComboBox))
    {
        Assert(!string.IsNullOrWhiteSpace(input.AccessibleName),
            $"Input {input.GetType().Name} must have an accessible name.");
    }

    foreach (var decorative in EnumerateControls(form).Where(control =>
                 control is BrandGlyphControl or BrandIconTile or ClipCordLogoControl or GradientStrip))
    {
        Assert(!decorative.TabStop,
            $"Decorative control {decorative.GetType().Name} must not be a keyboard tab stop.");
    }

    Assert(EnumerateControls(form).Single(control => control.Name == "ActivityNavItem").TabStop,
        "Activity must be keyboard reachable so its future-release description can be announced.");
    Assert(EnumerateControls(form).Single(control => control.Name == "AboutNavItem").TabStop,
        "About must be keyboard reachable.");
    Assert(EnumerateControls(form).Single(control => control.Name == "SettingsNavItem").TabStop,
        "The current Settings navigation item must participate in the top navigation keyboard order.");
    Assert(EnumerateControls(form).Single(control => control.Name == "GalleryNavItem").TabStop,
        "Gallery must be keyboard reachable from the top navigation.");
}

static void AssertOpaqueCustomControlsPaintEveryPixel(Form form)
{
    var getStyle = typeof(Control).GetMethod(
        "GetStyle",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var customTypes = new HashSet<Type>
    {
        typeof(ToggleSwitch),
        typeof(GradientButton),
        typeof(OutlineButton),
        typeof(TitleBarButton),
        typeof(BrandIconTile),
        typeof(ClipCordLogoControl),
        typeof(GradientStrip)
    };
    var sentinel = Color.FromArgb(255, 1, 254, 1);
    foreach (var control in EnumerateControls(form).Where(control =>
                 control.Visible &&
                 control.Width > 0 &&
                 control.Height > 0 &&
                 customTypes.Contains(control.GetType()) &&
                 (bool)getStyle.Invoke(control, [ControlStyles.Opaque])!))
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(sentinel);
        using var paint = new PaintEventArgs(graphics, control.ClientRectangle);
        var onPaint = control.GetType().GetMethod(
            "OnPaint",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        onPaint.Invoke(control, [paint]);

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (control.Region?.IsVisible(x, y) == false) continue;
                Assert(bitmap.GetPixel(x, y).ToArgb() != sentinel.ToArgb(),
                    $"Opaque {control.GetType().Name} left pixel ({x}, {y}) unpainted.");
            }
        }
    }
}

static void AssertDpiRefit(SettingsForm form)
{
    var compression = EnumerateControls(form)
        .OfType<TextBox>()
        .Single(control => control.AccessibleName == "Compression target in megabytes");
    var host = EnumerateControls(form)
        .OfType<RoundedPanel>()
        .Single(control => ReferenceEquals(control.Tag, compression));
    var originalFont = compression.Font;
    compression.Font = ClipCordTheme.InterfaceFont(18f);
    host.MaximumSize = Size.Empty;
    host.MinimumSize = Size.Empty;
    host.Height = 1;
    form.RefitDpiSensitiveControls();
    Assert(host.Height >= compression.PreferredHeight + host.Padding.Vertical,
        "DPI refitting must recompute the compression host from the editable compression field's preferred height.");
    compression.Font = originalFont;
    form.RefitDpiSensitiveControls();
}

static void AssertVerticalCentersMatch(Control first, Control second)
{
    var firstCenter = first.PointToScreen(new Point(first.Width / 2, first.Height / 2));
    var secondCenter = second.PointToScreen(new Point(second.Width / 2, second.Height / 2));
    Assert(Math.Abs(firstCenter.Y - secondCenter.Y) <= 1,
        $"The logo and wordmark must remain vertically centered; centers were {firstCenter.Y} and {secondCenter.Y}.");
}

static void AssertOfficialLogoArtworkPainted(ClipCordLogoControl logo)
{
    using var bitmap = new Bitmap(logo.Width, logo.Height);
    logo.DrawToBitmap(bitmap, new Rectangle(Point.Empty, logo.Size));

    var officialCoralSeen = false;
    var officialVioletSeen = false;
    for (var y = 0; y < bitmap.Height && !(officialCoralSeen && officialVioletSeen); y++)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            officialCoralSeen |= color.R >= 238 && color.G is >= 55 and <= 115 && color.B is >= 45 and <= 105;
            officialVioletSeen |= color.R is >= 115 and <= 180 && color.G is >= 40 and <= 105 && color.B >= 210;
        }
    }

    Assert(officialCoralSeen && officialVioletSeen,
        "The header logo must paint the official PNG's bright coral and violet artwork.");
}

static bool IsAutoScrollViewport(Control control) =>
    control is BrandedScrollHost { HasOverflow: true } ||
    control is ScrollableControl { AutoScroll: true };

static void AssertGlobalHotkeyLifecycle()
{
    var registrar = new FakeGlobalHotkeyRegistrar();
    var manager = new GlobalHotkeyManager(registrar);
    try
    {
        Assert(manager.TrySetBinding(GlobalHotkeyBinding.Default, out var initialError) &&
               initialError == 0 &&
               manager.RegisteredBinding == GlobalHotkeyBinding.Default &&
               registrar.RegisterCalls.Count == 1,
            "The global shortcut manager must register the initial binding exactly once.");

        Assert(manager.TrySetBinding(GlobalHotkeyBinding.Default, out _) &&
               registrar.RegisterCalls.Count == 1,
            "Reapplying the active global shortcut must not churn its Windows registration.");

        var presses = 0;
        manager.Pressed += (_, _) => presses++;
        Assert(!manager.HandleHotkeyMessage(GlobalHotkeyManager.HotkeyIdentifier + 1) && presses == 0 &&
               manager.HandleHotkeyMessage(GlobalHotkeyManager.HotkeyIdentifier) && presses == 1,
            "Only the registered ClipCord hotkey identifier may dispatch a mode toggle.");

        Assert(GlobalHotkeyBinding.TryParse("Ctrl + Shift + U", out var replacement),
            "The lifecycle test replacement shortcut must be valid.");
        registrar.RegisterResults.Enqueue(false);
        registrar.RegisterResults.Enqueue(true);
        Assert(!manager.TrySetBinding(replacement, out var conflictError) &&
               conflictError == FakeGlobalHotkeyRegistrar.ConflictError &&
               manager.RegisteredBinding == GlobalHotkeyBinding.Default &&
               registrar.UnregisterCount == 1,
            "A conflicting replacement must atomically restore the previous working shortcut.");

        registrar.UnregisterResults.Enqueue(false);
        Assert(!manager.TrySetBinding(replacement, out var unregisterError) &&
               unregisterError == FakeGlobalHotkeyRegistrar.ConflictError &&
               manager.RegisteredBinding == GlobalHotkeyBinding.Default,
            "An unregister failure must preserve the manager's previous binding instead of reporting it inactive.");

        Assert(manager.TrySetBinding(null, out _) &&
               manager.RegisteredBinding is null &&
               registrar.UnregisterCount == 3 &&
               !manager.HandleHotkeyMessage(GlobalHotkeyManager.HotkeyIdentifier),
            "Disabling the shortcut must unregister it and reject stale hotkey messages.");
    }
    finally
    {
        manager.Dispose();
        manager.Dispose();
    }

    var disposeRegistrar = new FakeGlobalHotkeyRegistrar();
    var disposableManager = new GlobalHotkeyManager(disposeRegistrar);
    Assert(disposableManager.TrySetBinding(GlobalHotkeyBinding.Default, out _),
        "The explicit-disposal probe must begin with an active registration.");
    disposableManager.Dispose();
    Assert(disposeRegistrar.UnregisterCount == 1,
        "Disposing an active manager must explicitly unregister its shortcut before destroying the handle.");
    disposableManager.Dispose();
    Assert(disposeRegistrar.UnregisterCount == 1,
        "Repeated disposal must not attempt to unregister the shortcut again.");
}

static void AssertModeHotkeyGuardPolicy()
{
    Assert(TrayApplicationContext.GetModeHotkeyBlockReason(false, false, false, false) ==
           ModeHotkeyBlockReason.None,
        "An idle ClipCord instance must allow the global mode shortcut.");
    Assert(TrayApplicationContext.GetModeHotkeyBlockReason(false, true, false, false) ==
           ModeHotkeyBlockReason.DialogOpen &&
           TrayApplicationContext.GetModeHotkeyBlockReason(false, false, true, false) ==
           ModeHotkeyBlockReason.DialogOpen,
        "Settings and update dialogs must both block global mode changes.");
    Assert(TrayApplicationContext.GetModeHotkeyBlockReason(false, false, false, true) ==
           ModeHotkeyBlockReason.ReconfigurationInProgress,
        "An active watcher reconfiguration must block another global mode change.");
    Assert(TrayApplicationContext.GetModeHotkeyBlockReason(true, true, true, true) ==
           ModeHotkeyBlockReason.ShuttingDown,
        "Shutdown must dominate every other global-shortcut guard state.");

    var exitRequested = false;
    using (var idleOperation = new CancellationTokenSource())
    {
        Assert(!TrayApplicationContext.TryDeferExitAndCancelManualOperation(
                   false,
                   ref exitRequested,
                   idleOperation) &&
               !exitRequested &&
               !idleOperation.IsCancellationRequested,
            "An ordinary exit must not be deferred or cancel a nonexistent manual transaction.");
    }
    using (var activeOperation = new CancellationTokenSource())
    {
        var exitMarkedBeforeCancellation = false;
        using var registration = activeOperation.Token.Register(() =>
            exitMarkedBeforeCancellation = exitRequested);
        Assert(TrayApplicationContext.TryDeferExitAndCancelManualOperation(
                   true,
                   ref exitRequested,
                   activeOperation) &&
               exitRequested &&
               activeOperation.IsCancellationRequested &&
               exitMarkedBeforeCancellation,
            "Exit during a manual edit must request deferred shutdown before cancelling safe pre-POST work.");
    }
    var disposedOperation = new CancellationTokenSource();
    disposedOperation.Dispose();
    exitRequested = false;
    Assert(TrayApplicationContext.TryDeferExitAndCancelManualOperation(
               true,
               ref exitRequested,
               disposedOperation) && exitRequested,
        "Deferred exit must tolerate a manual-operation cancellation source that completed concurrently.");
}

static void AssertModeFeedbackOverlayContract()
{
    var uploads = ModeFeedbackPresentation.ForUploadMode(true);
    var localOnly = ModeFeedbackPresentation.ForUploadMode(false);
    Assert(uploads == new ModeFeedbackPresentation(
               "DISCORD UPLOADS ON",
               "New clips will be sent automatically.",
               ModeFeedbackTone.UploadsEnabled) &&
           localOnly == new ModeFeedbackPresentation(
               "LOCAL ONLY ON",
               "New clips will stay on this PC.",
               ModeFeedbackTone.LocalOnlyEnabled),
        "Shortcut feedback must identify the confirmed route unambiguously.");
    Assert(ModeFeedbackOverlay.GetGlyph(ModeFeedbackTone.UploadsEnabled) == BrandGlyph.DiscordDestination &&
           ModeFeedbackOverlay.GetGlyph(ModeFeedbackTone.LocalOnlyEnabled) == BrandGlyph.Shield,
        "In-game route feedback must use the corrected Discord destination artwork and retain the local-only shield.");

    Assert(ModeFeedbackOverlay.RequiredExtendedStyles == 0x080000A0,
        "The in-game mode indicator must retain the non-activating, tool-window, and click-through styles.");
    var displayDuration = ModeFeedbackOverlay.DisplayDurationMilliseconds;
    Assert(displayDuration == 1500,
        "The in-game indicator must dismiss after the approved 1.5-second duration.");

    var primary = new Rectangle(0, 0, 1920, 1080);
    var secondary = new Rectangle(-2560, -240, 2560, 1440);
    var compact = new Rectangle(3000, 500, 360, 240);
    var normalBounds = ModeFeedbackOverlay.CalculateBounds(primary, 96);
    var oneHundredFiftyPercentBounds = ModeFeedbackOverlay.CalculateBounds(primary, 144);
    var scaledBounds = ModeFeedbackOverlay.CalculateBounds(secondary, 192);
    var compactBounds = ModeFeedbackOverlay.CalculateBounds(compact, 192);
    Assert(primary.Contains(normalBounds) &&
           primary.Contains(oneHundredFiftyPercentBounds) &&
           secondary.Contains(scaledBounds) &&
           compact.Contains(compactBounds),
        "The in-game indicator must stay inside primary, negative-coordinate, and compact monitor work areas.");
    Assert(Math.Abs(normalBounds.Left + normalBounds.Width / 2 - (primary.Left + primary.Width / 2)) <= 1 &&
           Math.Abs(oneHundredFiftyPercentBounds.Left + oneHundredFiftyPercentBounds.Width / 2 -
                    (primary.Left + primary.Width / 2)) <= 1 &&
           Math.Abs(scaledBounds.Left + scaledBounds.Width / 2 -
                    (secondary.Left + secondary.Width / 2)) <= 1,
        "The in-game indicator must be centered on the monitor containing the active game.");
    Assert(scaledBounds.Width > normalBounds.Width && scaledBounds.Height > normalBounds.Height,
        "The in-game indicator must scale for high-DPI displays.");
}

static void AssertModeFeedbackOverlayBehavior()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var overlay = new ModeFeedbackOverlay();
            Assert(overlay.IsHandleCreated,
                "The mode indicator must establish UI-thread ownership before background feedback can be queued.");
            overlay.ApplyPresentation(
                ModeFeedbackPresentation.ForUploadMode(true),
                new Rectangle(0, 0, 1920, 1080),
                96);

            var extendedStyle = ModeFeedbackNativeProbe.GetExtendedStyle(overlay.Handle);
            Assert((extendedStyle & ModeFeedbackOverlay.RequiredExtendedStyles) ==
                   ModeFeedbackOverlay.RequiredExtendedStyles,
                $"The real mode-indicator HWND is missing required extended styles: 0x{extendedStyle:X}.");
            Assert(ModeFeedbackNativeProbe.SendWindowMessage(overlay.Handle, 0x0084).ToInt64() == -1,
                "WM_NCHITTEST must return HTTRANSPARENT so the indicator cannot consume game input.");
            Assert(ModeFeedbackNativeProbe.SendWindowMessage(overlay.Handle, 0x0021).ToInt64() == 3,
                "WM_MOUSEACTIVATE must return MA_NOACTIVATE so the indicator cannot take focus.");
            var showWithoutActivation = typeof(ModeFeedbackOverlay).GetProperty(
                "ShowWithoutActivation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert(showWithoutActivation?.GetValue(overlay) is true &&
                   !overlay.ShowInTaskbar && overlay.TopMost,
                "The mode indicator must be topmost and shown outside taskbar/activation flows.");

            var timerField = typeof(ModeFeedbackOverlay).GetField(
                "_dismissTimer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var dismissTimer = timerField?.GetValue(overlay) as System.Windows.Forms.Timer;
            Assert(dismissTimer?.Interval == ModeFeedbackOverlay.DisplayDurationMilliseconds,
                "The real dismissal timer must use the approved 1.5-second interval.");

            var initialHandle = overlay.Handle;
            var backgroundShow = Task.Run(() =>
                overlay.ShowFeedback(ModeFeedbackPresentation.ForUploadMode(true)));
            PumpWindowsMessagesUntil(
                () => backgroundShow.IsCompleted && overlay.Visible,
                "Background feedback was not marshaled to the overlay's UI thread.");
            backgroundShow.GetAwaiter().GetResult();
            Assert(overlay.Handle == initialHandle,
                "Showing feedback must reuse the overlay's original HWND.");

            PumpWindowsMessagesFor(TimeSpan.FromMilliseconds(900));
            var resetWatch = Stopwatch.StartNew();
            overlay.ShowFeedback(ModeFeedbackPresentation.ForUploadMode(false));
            PumpWindowsMessagesFor(TimeSpan.FromMilliseconds(900));
            Assert(overlay.Visible,
                "A repeated shortcut press must restart, not inherit, the previous dismissal countdown.");
            PumpWindowsMessagesUntil(
                () => !overlay.Visible,
                "The mode indicator did not dismiss after the latest shortcut feedback.");
            resetWatch.Stop();
            Assert(resetWatch.Elapsed >= TimeSpan.FromMilliseconds(1200) &&
                   resetWatch.Elapsed <= TimeSpan.FromMilliseconds(2600),
                $"The mode indicator dismissed {resetWatch.Elapsed.TotalMilliseconds:F0} ms after the latest feedback.");

            overlay.ShowFeedback(ModeFeedbackPresentation.ForUploadMode(true));
            Application.DoEvents();
            Assert(overlay.Visible && overlay.Handle == initialHandle,
                "A dismissed mode indicator must be reusable without creating another HWND.");
            overlay.Dispose();
            overlay.Dispose();
            Task.Run(() => overlay.ShowFeedback(ModeFeedbackPresentation.ForUploadMode(false)))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(12)))
    {
        throw new TimeoutException("Mode feedback overlay behavior validation did not finish within 12 seconds.");
    }
    if (failure is not null)
    {
        throw new InvalidOperationException("Mode feedback overlay behavior validation failed.", failure);
    }
}

static void RenderModeFeedbackPreviews(string outputDirectory)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            using var overlay = new ModeFeedbackOverlay();
            var presentations = new[]
            {
                ("discord-uploads-on.png", ModeFeedbackPresentation.ForUploadMode(true)),
                ("local-only-on.png", ModeFeedbackPresentation.ForUploadMode(false)),
                ("dialog-open.png", ModeFeedbackPresentation.DialogOpen),
                ("mode-change-in-progress.png", ModeFeedbackPresentation.ReconfigurationInProgress),
                ("discord-setup-required.png", ModeFeedbackPresentation.DiscordSetupRequired),
                ("mode-change-failed.png", ModeFeedbackPresentation.SaveFailed)
            };
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                var dpiDirectory = Path.Combine(outputDirectory, $"dpi-{dpi}");
                Directory.CreateDirectory(dpiDirectory);
                foreach (var (name, presentation) in presentations)
                {
                    overlay.ApplyPresentation(presentation, new Rectangle(0, 0, 1920, 1080), dpi);
                    overlay.PerformLayout();
                    using var bitmap = new Bitmap(overlay.Width, overlay.Height);
                    overlay.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(dpiDirectory, name), System.Drawing.Imaging.ImageFormat.Png);
                    if (dpi == 96)
                    {
                        bitmap.Save(Path.Combine(outputDirectory, name), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(15)))
    {
        throw new TimeoutException("Mode feedback preview rendering did not finish within 15 seconds.");
    }
    if (failure is not null) throw new InvalidOperationException("Mode feedback preview rendering failed.", failure);
}

static void RenderSettingsPreview(string outputPath)
{
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);
    using var form = new SettingsForm(
        new AppSettings(
            @"C:\Users\Player\Videos\Game Clips",
            "https://discord.com/api/webhooks/123456/preview-token",
            true,
            AppSettings.DefaultCompressionTargetMb,
            "PlayerOne",
            true),
        checkForUpdatesAsync: _ => Task.CompletedTask,
        watcherStatusProvider: () => "Watching");
    form.Show();
    Application.DoEvents();
    AssertSettingsCardGrid(form);
    AssertSettingsFeatureIcons(form);
    AssertSettingsFooterLayout(form, requireLogicalHeight: true);
    using var bitmap = new Bitmap(form.Width, form.Height);
    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    form.Hide();
}

static void AssertGalleryEditorScaledLayout(AppSettings settings, float scale)
{
    var scaledFonts = new Dictionary<(string Family, float Size, FontStyle Style), Font>();
    try
    {
        using var form = new SettingsForm(
            settings,
            checkForUpdatesAsync: _ => Task.CompletedTask,
            initialPage: SettingsPage.Gallery,
            manualClipEditService: new FakeManualClipEditService());
        form.Show();
        WaitForUiCondition(
            () => EnumerateControls(form).OfType<GalleryGameCard>().Any(card =>
                card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true),
            TimeSpan.FromSeconds(5),
            $"Gallery editor did not populate before the {scale:F1}x layout check.");
        var gameCard = EnumerateControls(form).OfType<GalleryGameCard>().Single(card =>
            card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true);
        typeof(Control).GetMethod(
                "OnClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(gameCard, [EventArgs.Empty]);
        EnumerateControls(form).OfType<Button>()
            .Single(button => button.Name == "GalleryLocalOnlyFilterButton")
            .PerformClick();
        EnumerateControls(form).OfType<Button>()
            .Single(button => button.Name == "EditGalleryClipButton")
            .PerformClick();
        WaitForUiCondition(
            () => EnumerateControls(form).OfType<Button>().Any(button =>
                button.Name == "UploadEditedClipButton" && button.Enabled),
            TimeSpan.FromSeconds(5),
            $"Gallery editor did not initialize before the {scale:F1}x layout check.");

        var gallery = EnumerateControls(form).OfType<GalleryView>().Single();
        var topNavigation = EnumerateControls(form).Single(control => control.Name == "TopNavigation");
        form.Hide();
        form.Scale(new SizeF(scale, scale));
        var featureControls = new[] { (Control)gallery, topNavigation }
            .SelectMany(root => new[] { root }.Concat(EnumerateControls(root)))
            .Distinct();
        foreach (var control in featureControls)
        {
            var source = control.Font;
            var key = (source.FontFamily.Name, source.Size * scale, source.Style);
            if (!scaledFonts.TryGetValue(key, out var scaledFont))
            {
                scaledFont = new Font(source.FontFamily, key.Item2, source.Style, GraphicsUnit.Point);
                scaledFonts.Add(key, scaledFont);
            }
            control.Font = scaledFont;
        }
        form.PerformLayout();
        gallery.RefreshViewport();
        Application.DoEvents();
        AssertLocalClipEditorLayout(form, scale);
    }
    finally
    {
        foreach (var font in scaledFonts.Values) font.Dispose();
    }
}

static void AssertLocalClipEditorLayout(SettingsForm form, float scale)
{
    var editor = EnumerateControls(form).OfType<LocalClipEditorView>().Single();
    var root = EnumerateControls(editor).Single(control => control.Name == "LocalClipEditorLayout");
    var host = EnumerateControls(form).OfType<BrandedScrollHost>()
        .Single(control => control.Name == "GalleryScrollHost");
    host.RefreshContentLayout();
    form.PerformLayout();
    Application.DoEvents();

    var preferred = editor.GetPreferredSize(new Size(host.ClientSize.Width, int.MaxValue));
    Assert(editor.Width > 0 && editor.Width <= host.ClientSize.Width && editor.Width < 10_000 &&
           root.Width == editor.ClientSize.Width && root.Height == editor.ClientSize.Height &&
           editor.Height >= preferred.Height - 1,
        $"Gallery editor {scale:F1}x must be width-constrained by its branded viewport and report its full content height: " +
        $"host={host.ClientSize}, editor={editor.Bounds}, root={root.Bounds}, preferred={preferred}.");
    Assert(host.HasOverflow == (editor.Height > host.ClientSize.Height),
        $"Gallery editor {scale:F1}x overflow must match its actual content: host={host.ClientSize}, editor={editor.Size}, overflow={host.HasOverflow}.");

    foreach (var control in EnumerateControls(editor).Where(control => control.Visible))
    {
        if (control.Parent is null || ReferenceEquals(control.Parent, host)) continue;
        var parentBounds = control.Parent.ClientRectangle;
        Assert(control.Left >= -1 && control.Top >= -1 &&
               control.Right <= parentBounds.Right + 1 && control.Bottom <= parentBounds.Bottom + 1,
            $"Gallery editor {scale:F1}x clips {control.GetType().Name} '{control.Name}' ('{control.Text}') " +
            $"inside {control.Parent.GetType().Name} '{control.Parent.Name}': child={control.Bounds}, parent={parentBounds}.");
    }

    var cards = new[] { "EditorPreviewCard", "EditorOptionsCard", "EditorTrimCard", "EditorCommitCard" }
        .Select(name => EnumerateControls(editor).Single(control => control.Name == name))
        .ToArray();
    var (preview, options, trim, commit) = (cards[0], cards[1], cards[2], cards[3]);
    Assert(preview.Top == options.Top && preview.Bottom == options.Bottom &&
           trim.Top == commit.Top && trim.Bottom == commit.Bottom &&
           trim.Top > preview.Bottom &&
           preview.Right < options.Left && trim.Right < commit.Left,
        $"Gallery editor {scale:F1}x must keep two aligned, non-overlapping card rows: " +
        string.Join(", ", cards.Select(card => $"{card.Name}={card.Bounds}")));
}

static void RenderGalleryEditorPreview(string outputPath)
{
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);
    var fixtureRoot = Path.Combine(
        Path.GetTempPath(),
        "ClipsToDiscordTests",
        "gallery-editor-render-" + Guid.NewGuid().ToString("N"));
    try
    {
        var uploadedGame = Directory.CreateDirectory(Path.Combine(
            fixtureRoot,
            "uploaded",
            "Battlefield™-6")).FullName;
        var localGame = Directory.CreateDirectory(Path.Combine(
            fixtureRoot,
            "local-only",
            "Battlefield™-6")).FullName;
        File.WriteAllBytes(Path.Combine(uploadedGame, "Battlefield uploaded match.mp4"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(localGame, "Battlefield local match.mp4"), new byte[175 * 1024]);
        using var form = new SettingsForm(
            new AppSettings(
                fixtureRoot,
                "https://discord.com/api/webhooks/123456/preview-token",
                true,
                95,
                "PlayerOne",
                true),
            checkForUpdatesAsync: _ => Task.CompletedTask,
            initialPage: SettingsPage.Gallery,
            manualClipEditService: new FakeManualClipEditService());
        form.Show();
        WaitForUiCondition(
            () => EnumerateControls(form).OfType<GalleryGameCard>().Any(),
            TimeSpan.FromSeconds(5),
            "Gallery editor preview did not populate its game card.");
        var gameCard = EnumerateControls(form).OfType<GalleryGameCard>().Single();
        typeof(Control).GetMethod(
                "OnClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(gameCard, [EventArgs.Empty]);
        EnumerateControls(form).OfType<Button>()
            .Single(button => button.Name == "GalleryLocalOnlyFilterButton")
            .PerformClick();
        EnumerateControls(form).OfType<Button>()
            .Single(button => button.Name == "EditGalleryClipButton")
            .PerformClick();
        WaitForUiCondition(
            () => EnumerateControls(form).OfType<Button>().Any(button =>
                      button.Name == "UploadEditedClipButton" && button.Enabled) &&
                  EnumerateControls(form).OfType<ClipPreviewControl>().Any(preview =>
                      preview.Name == "EditorPreviewImage" && preview.Image is not null),
            TimeSpan.FromSeconds(5),
            "Gallery editor preview did not initialize its media controls.");
        form.PerformLayout();
        EnumerateControls(form).OfType<GalleryView>().Single().RefreshViewport();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
    }
    finally
    {
        try { Directory.Delete(fixtureRoot, recursive: true); } catch { }
    }
}

static void AssertGalleryEditorFlow(AppSettings settings)
{
    var service = new FakeManualClipEditService();
    using var form = new SettingsForm(
        settings,
        checkForUpdatesAsync: _ => Task.CompletedTask,
        initialPage: SettingsPage.Gallery,
        manualClipEditService: service);
    form.Show();
    WaitForUiCondition(
        () => EnumerateControls(form).OfType<GalleryGameCard>().Any(card =>
            card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true),
        TimeSpan.FromSeconds(5),
        "The Local-only editor flow did not populate its game-first Gallery entry.");
    var gameCard = EnumerateControls(form)
        .OfType<GalleryGameCard>()
        .Single(card => card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true);
    typeof(Control).GetMethod(
            "OnClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(gameCard, [EventArgs.Empty]);
    Application.DoEvents();
    Assert(EnumerateControls(form).Count(control => control.Name == "GalleryClipCard") == 2 &&
           EnumerateControls(form).OfType<Button>().Count(button => button.Name == "EditGalleryClipButton") == 1,
        "A selected game must show both routes while exposing Edit & upload only on its Local-only clip.");
    var filters = EnumerateControls(form)
        .OfType<Button>()
        .Where(button => button.Name is
            "GalleryAllFilterButton" or
            "GalleryLocalOnlyFilterButton" or
            "GalleryUploadedFilterButton")
        .ToDictionary(button => button.Name, StringComparer.Ordinal);
    filters["GalleryLocalOnlyFilterButton"].PerformClick();
    Application.DoEvents();
    Assert(EnumerateControls(form).Count(control => control.Name == "GalleryClipCard") == 1 &&
           EnumerateControls(form).OfType<Button>().Count(button => button.Name == "EditGalleryClipButton") == 1 &&
           filters["GalleryLocalOnlyFilterButton"].AccessibleDescription == "Selected filter",
        "The Local-only route chip must preserve the game-first view and hide uploaded clips.");
    EnumerateControls(form).OfType<Button>()
        .Single(button => button.Name == "EditGalleryClipButton")
        .PerformClick();
    WaitForUiCondition(
        () => EnumerateControls(form).OfType<Button>().Any(button =>
            button.Name == "UploadEditedClipButton" && button.Enabled),
        TimeSpan.FromSeconds(5),
        "The Local-only editor did not finish its on-demand media inspection.");
    var editor = EnumerateControls(form).OfType<LocalClipEditorView>().Single();
    var keepOriginal = EnumerateControls(editor)
        .OfType<CheckBox>()
        .Single(control => control.Name == "KeepLocalOriginalToggle");
    var gameField = EnumerateControls(editor)
        .OfType<TextBox>()
        .Single(control => control.AccessibleName == "Edited clip game");
    var noteField = EnumerateControls(editor)
        .OfType<TextBox>()
        .Single(control => control.AccessibleName == "Discord description");
    Assert(!keepOriginal.Checked && gameField.Text == "Battlefield™-6" &&
           noteField.MaxLength == DiscordClipMessage.MaximumNoteLength &&
           EnumerateControls(editor).OfType<TrimRangeControl>().Single().Enabled,
        "The editor must default to no duplicate, preserve game attribution, bound Discord text, and expose trim controls.");
    Assert(EnumerateControls(form).OfType<Button>()
               .Single(button => button.Name == "GalleryBackButton").Text == "Back to clips" &&
           EnumerateControls(editor).OfType<Label>().Any(label =>
               label.Text.Contains("Recycle Bin", StringComparison.OrdinalIgnoreCase)),
        "The editor must explain its safe original-file disposition and provide a return path to the selected game.");
    AssertControlsFit(form);
    AssertCriticalTextFits(form);
    var designedEditorSize = form.Size;
    form.Size = form.MinimumSize;
    form.PerformLayout();
    EnumerateControls(form).OfType<GalleryView>().Single().RefreshViewport();
    Application.DoEvents();
    AssertLocalClipEditorLayout(form, form.DeviceDpi / 96f);
    form.Size = designedEditorSize;
    form.PerformLayout();
    EnumerateControls(form).OfType<GalleryView>().Single().RefreshViewport();
    Application.DoEvents();
    var tryBuildRequest = typeof(LocalClipEditorView).GetMethod(
        "TryBuildRequest",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var requestArguments = new object?[] { null, null };
    Assert((bool)tryBuildRequest.Invoke(editor, requestArguments)!,
        $"The editor's current values must be uploadable before click; error={requestArguments[1]}.");
    EnumerateControls(editor).OfType<Button>()
        .Single(button => button.Name == "UploadEditedClipButton")
        .PerformClick();
    WaitForUiCondition(
        () => service.EditCalls == 1 && editor.IsBusy,
        TimeSpan.FromSeconds(3),
        "The editor did not enter its single-flight busy lifecycle.");
    Assert(!EnumerateControls(form).Single(control => control.Name == "SettingsNavItem").Enabled &&
           !EnumerateControls(form).Single(control => control.Name == "ActivityNavItem").Enabled &&
           !EnumerateControls(form).Single(control => control.Name == "AboutNavItem").Enabled &&
           !EnumerateControls(form).Single(control => control.Name == "CloseButton").Enabled,
        "A manual edit/upload must block conflicting navigation and user-close actions while its transaction is active.");
    var cancelEditorButton = EnumerateControls(editor).OfType<Button>()
        .Single(button => button.Name == "CancelClipEditorButton");
    service.ReportStage(ManualClipEditStage.Compressing, "Compressing test edit…");
    WaitForUiCondition(
        () => cancelEditorButton.Enabled && cancelEditorButton.Text == "Cancel upload",
        TimeSpan.FromSeconds(3),
        "FFmpeg compression must remain user-cancellable because no Discord POST is in flight.");
    service.ReportStage(ManualClipEditStage.Uploading, "Uploading test edit…");
    WaitForUiCondition(
        () => !cancelEditorButton.Enabled && cancelEditorButton.Text == "Finishing safely",
        TimeSpan.FromSeconds(3),
        "An in-flight Discord upload must disable cancellation until its bounded result is known.");
    service.ReportStage(ManualClipEditStage.Rendering, "Rendering another test edit…");
    WaitForUiCondition(
        () => cancelEditorButton.Enabled && cancelEditorButton.Text == "Cancel upload",
        TimeSpan.FromSeconds(3),
        "Pre-upload rendering must remain cancellable.");
    service.ReportStage(ManualClipEditStage.Archiving, "Archiving test edit…");
    WaitForUiCondition(
        () => !cancelEditorButton.Enabled && cancelEditorButton.Text == "Finishing safely",
        TimeSpan.FromSeconds(3),
        "Post-confirmation archive work must remain non-cancellable so durable disposition can finish.");
    service.ReportStage(ManualClipEditStage.Rendering, "Rendering test edit…");
    WaitForUiCondition(
        () => cancelEditorButton.Enabled && cancelEditorButton.Text == "Cancel upload",
        TimeSpan.FromSeconds(3),
        "The cancellation-only test must return to a safe cancellable stage before unwinding.");
    cancelEditorButton.PerformClick();
    WaitForUiCondition(
        () => !editor.IsBusy,
        TimeSpan.FromSeconds(3),
        "Cancelling a pre-confirmation editor operation did not unwind its busy lifecycle.");
    Assert(service.LastRequest is { Source.Route: GalleryClipRoute.LocalOnly, KeepOriginal: false } &&
           File.Exists(service.LastRequest.Source.Path) &&
           EnumerateControls(form).Single(control => control.Name == "SettingsNavItem").Enabled,
        "Editor cancellation must preserve the Local-only source and restore navigation.");

    EnumerateControls(form).OfType<Button>()
        .Single(button => button.Name == "GalleryBackButton")
        .PerformClick();
    Application.DoEvents();
    Assert(EnumerateControls(form).Count(control => control.Name == "GalleryClipCard") == 1 &&
           EnumerateControls(form).OfType<Button>()
               .Single(button => button.Name == "GalleryLocalOnlyFilterButton")
               .AccessibleDescription == "Selected filter",
        "Leaving the editor must return to the same game and Local-only filter.");
    EnumerateControls(form).OfType<Button>()
        .Single(button => button.Name == "GalleryUploadedFilterButton")
        .PerformClick();
    Application.DoEvents();
    Assert(EnumerateControls(form).Count(control => control.Name == "GalleryClipCard") == 1 &&
           !EnumerateControls(form).OfType<Button>().Any(button => button.Name == "EditGalleryClipButton"),
        "Uploaded clips must never expose the Local-only editing action.");
    form.Close();
}

static void AssertGalleryEditorPreviewPlayback(AppSettings settings)
{
    var service = new FakeManualClipEditService();
    var launchedPaths = new List<string>();
    using var form = new SettingsForm(
        settings,
        checkForUpdatesAsync: _ => Task.CompletedTask,
        initialPage: SettingsPage.Gallery,
        manualClipEditService: service,
        launchMediaFile: path => { launchedPaths.Add(path); return true; });
    form.Show();
    var editor = OpenLocalOnlyEditor(form);
    var trim = EnumerateControls(editor).OfType<TrimRangeControl>().Single();
    var duration = TimeSpan.FromSeconds(FakeManualClipEditService.ProbeDurationSeconds);
    var playSelection = typeof(LocalClipEditorView).GetMethod(
        "TryPlaySelectionInTempMediaAsync",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    Task<bool> PlaySelection() => (Task<bool>)playSelection.Invoke(editor, [])!;

    // --- Trim-range replay: the render must use the handles' current values. ---
    var replayStart = TimeSpan.FromSeconds(12.5);
    var replayEnd = TimeSpan.FromSeconds(31.25);
    trim.SetRange(replayStart, replayEnd, duration);
    Application.DoEvents();
    var replayTask = PlaySelection();
    WaitForUiCondition(
        () => replayTask.IsCompleted,
        TimeSpan.FromSeconds(5),
        "Trim preview playback did not complete for the selected range.");
    Assert(replayTask.Result && service.PlaybackCalls.Count == 1,
        "Playing a selection must run exactly one trimmed-playback render.");
    var replayed = service.PlaybackCalls[0];
    Assert(replayed.Start == replayStart && replayed.End == replayEnd && !replayed.MuteAudio,
        $"Trim preview must replay the current handle range; got {replayed.Start}-{replayed.End}.");
    Assert(launchedPaths.Count == 1 && File.Exists(launchedPaths[0]),
        "A completed trim preview must hand exactly one existing rendered clip to the media launcher.");

    // --- Moving the handles mid-render must cancel it and never launch the stale range. ---
    service.GatePlayback();
    var staleTask = PlaySelection();
    WaitForUiCondition(
        () => service.PlaybackCalls.Count == 2,
        TimeSpan.FromSeconds(5),
        "The second trim preview render did not start.");
    trim.SetRange(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(55), duration);
    Application.DoEvents();
    service.ReleasePlayback();
    WaitForUiCondition(
        () => staleTask.IsCompleted,
        TimeSpan.FromSeconds(5),
        "The superseded trim preview render never unwound.");
    Assert(!staleTask.Result && service.PlaybackCancellations == 1,
        "Moving a trim handle must cancel the in-flight trim preview render.");
    Assert(launchedPaths.Count == 1,
        "A cancelled trim preview must never launch a player for the stale range.");

    // --- A still-frame refresh must not paint over playback that started after it. ---
    var playbackRunning = typeof(LocalClipEditorView).GetField(
        "_isPlaybackRunning",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var previewControl = EnumerateControls(editor).OfType<ClipPreviewControl>().Single();
    var refreshPreview = typeof(LocalClipEditorView).GetMethod(
        "RefreshPreviewRequestAsync",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var adoptedBefore = previewControl.Image;
    playbackRunning.SetValue(editor, true);
    var refreshTask = (Task)refreshPreview.Invoke(editor, [TimeSpan.FromSeconds(5), CancellationToken.None])!;
    WaitForUiCondition(
        () => refreshTask.IsCompleted,
        TimeSpan.FromSeconds(5),
        "The still-frame preview refresh never completed while playback was active.");
    Assert(ReferenceEquals(previewControl.Image, adoptedBefore),
        "A still-frame refresh must not replace the preview image while playback is running.");
    playbackRunning.SetValue(editor, false);

    // With playback stopped the same refresh must adopt normally, proving the guard is
    // conditional rather than a permanent disable.
    var resumedTask = (Task)refreshPreview.Invoke(editor, [TimeSpan.FromSeconds(5), CancellationToken.None])!;
    WaitForUiCondition(
        () => resumedTask.IsCompleted,
        TimeSpan.FromSeconds(5),
        "The still-frame preview refresh never completed after playback stopped.");
    Assert(!ReferenceEquals(previewControl.Image, adoptedBefore) && previewControl.Image is not null,
        "A still-frame refresh must adopt its frame once playback is no longer running.");
    form.Close();
}

static LocalClipEditorView OpenLocalOnlyEditor(SettingsForm form)
{
    WaitForUiCondition(
        () => EnumerateControls(form).OfType<GalleryGameCard>().Any(card =>
            card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true),
        TimeSpan.FromSeconds(5),
        "The Local-only editor flow did not populate its game-first Gallery entry.");
    var gameCard = EnumerateControls(form)
        .OfType<GalleryGameCard>()
        .Single(card => card.AccessibleName?.Contains("Battlefield", StringComparison.OrdinalIgnoreCase) == true);
    typeof(Control).GetMethod(
            "OnClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(gameCard, [EventArgs.Empty]);
    Application.DoEvents();
    EnumerateControls(form).OfType<Button>()
        .Single(button => button.Name == "EditGalleryClipButton")
        .PerformClick();
    WaitForUiCondition(
        () => EnumerateControls(form).OfType<Button>().Any(button =>
            button.Name == "UploadEditedClipButton" && button.Enabled),
        TimeSpan.FromSeconds(5),
        "The Local-only editor did not finish its on-demand media inspection.");
    return EnumerateControls(form).OfType<LocalClipEditorView>().Single();
}

static void AssertSettingsFooterLayout(SettingsForm form, bool requireLogicalHeight)
{
    var root = EnumerateControls(form)
        .OfType<TableLayoutPanel>()
        .Single(control => control.Name == "RootLayout");
    var footer = EnumerateControls(form).Single(control => control.Name == "FooterLayout");
    var glyph = EnumerateControls(footer)
        .OfType<BrandGlyphControl>()
        .Single(control => control.Name == "FooterPrivacyGlyph");
    var messageStack = EnumerateControls(footer)
        .Single(control => control.Name == "FooterMessageStack");
    var actions = EnumerateControls(footer)
        .Single(control => control.Name == "FooterActions");
    var privacy = EnumerateControls(footer)
        .OfType<Label>()
        .Single(control => control.Name == "PrivacySummaryLabel");
    var status = EnumerateControls(footer)
        .OfType<Label>()
        .Single(control => control.Name == "FooterStatusLabel");

    if (requireLogicalHeight)
    {
        Assert(root.RowStyles[3].SizeType == SizeType.Absolute &&
               Math.Abs(root.RowStyles[3].Height - SettingsForm.FooterLogicalHeight) < .1f,
            $"The Settings footer must remain {SettingsForm.FooterLogicalHeight}px instead of being scaled independently: " +
            $"actual={root.RowStyles[3].Height}px, dpi={form.DeviceDpi}.");
    }

    Assert(Math.Abs(glyph.Width - glyph.Height) <= 1 && glyph.Width >= 20,
        $"The footer privacy glyph must retain a square drawing surface: {glyph.Bounds}.");

    var contentCenterTwice = footer.Padding.Top * 2 +
                             footer.ClientSize.Height - footer.Padding.Vertical;
    static int CenterTwice(Control control, Control ancestor)
    {
        var location = GetLocationRelativeToAncestor(control, ancestor);
        return location.Y * 2 + control.Height;
    }

    foreach (var (name, control) in new[]
             {
                 ("privacy glyph", (Control)glyph),
                 ("message stack", messageStack),
                 ("footer actions", actions)
             })
    {
        Assert(Math.Abs(CenterTwice(control, footer) - contentCenterTwice) <= 2,
            $"The {name} must be vertically centered in the footer: control={control.Bounds}, " +
            $"footer={footer.ClientRectangle}, padding={footer.Padding}.");
    }

    if (string.IsNullOrWhiteSpace(status.Text))
    {
        Assert(!status.Visible && messageStack.Height <= privacy.Height + 1,
            $"An empty footer status must collapse completely instead of reserving a blank row: " +
            $"statusVisible={status.Visible}, status={status.Size}, stack={messageStack.Size}, privacy={privacy.Size}.");
        Assert(Math.Abs(CenterTwice(privacy, footer) - CenterTwice(glyph, footer)) <= 2,
            $"The privacy copy must align with its glyph when no transient status is shown: " +
            $"privacy={GetLocationRelativeToAncestor(privacy, footer)} {privacy.Size}, glyph={glyph.Bounds}.");
    }
    else if (form.Visible)
    {
        Assert(status.Visible && messageStack.Height >= status.Height + privacy.Height - 1,
            $"A nonempty footer status must expand into the centered two-line message stack: " +
            $"statusVisible={status.Visible}, status={status.Size}, stack={messageStack.Size}, privacy={privacy.Size}.");
    }

    var buttons = EnumerateControls(actions)
        .OfType<Button>()
        .Where(button => !form.Visible || button.Visible)
        .ToArray();
    Assert(buttons.Length is >= 1 and <= 2 &&
           buttons.All(button => Math.Abs(CenterTwice(button, footer) - contentCenterTwice) <= 2),
        $"Save and Cancel must share the footer's vertical center: " +
        string.Join(", ", buttons.Select(button => $"{button.Text}={GetLocationRelativeToAncestor(button, footer)} {button.Size}")));
}

static void RenderAboutPreview(string outputPath)
{
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);
    using var form = new SettingsForm(
        new AppSettings(
            @"C:\Users\Player\Videos\Game Clips",
            "https://discord.com/api/webhooks/123456/preview-token",
            true,
            AppSettings.DefaultCompressionTargetMb,
            "PlayerOne",
            true),
        checkForUpdatesAsync: _ => Task.CompletedTask,
        watcherStatusProvider: () => "Discord open — watching for clips",
        initialPage: SettingsPage.About);
    form.Show();
    Application.DoEvents();
    form.Size = SettingsForm.GetDesignedOpeningSize(SettingsPage.About, form.DeviceDpi);
    form.PerformLayout();
    Application.DoEvents();
    AssertAboutLayout(form, GetDpiScale(form));
    AssertAboutCopyAndAccessibility(form);
    Assert(!EnumerateControls(form).OfType<AboutView>().Single().HasOverflow,
        "The rendered production About page unexpectedly requires scrolling at its designed opening size.");
    using var bitmap = new Bitmap(form.Width, form.Height);
    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    form.Hide();
}

static void AssertSettingsScaledLayout(AppSettings settings, float scale)
{
    var scaledFonts = new Dictionary<(string Family, float Size, FontStyle Style), Font>();
    try
    {
        using var form = new SettingsForm(
            settings,
            checkForUpdatesAsync: _ => Task.CompletedTask);
        var designedOpeningSize = SettingsForm.GetDesignedOpeningSize(SettingsPage.Settings, 96);
        var scaledDesignedOpeningSize = new Size(
            (int)Math.Round(designedOpeningSize.Width * scale),
            (int)Math.Round(designedOpeningSize.Height * scale));
        form.CreateControl();
        form.Scale(new SizeF(scale, scale));
        foreach (var control in new[] { (Control)form }.Concat(EnumerateControls(form)))
        {
            var source = control.Font;
            var key = (source.FontFamily.Name, source.Size * scale, source.Style);
            if (!scaledFonts.TryGetValue(key, out var scaledFont))
            {
                scaledFont = new Font(source.FontFamily, key.Item2, source.Style, GraphicsUnit.Point);
                scaledFonts.Add(key, scaledFont);
            }
            control.Font = scaledFont;
        }
        // Keep this synthetic DPI test independent of the CI runner's desktop size.
        // OnShown separately verifies that real constrained screens are fitted and scroll safely.
        form.AutoScroll = true;
        var rootLayout = form.Controls.Cast<Control>().Single(control => control.Name == "RootLayout");
        rootLayout.Dock = DockStyle.None;
        rootLayout.Size = scaledDesignedOpeningSize;
        rootLayout.PerformLayout();
        form.PerformLayout();
        Application.DoEvents();
        AssertControlsFit(form);
        AssertSettingsFooterLayout(form, requireLogicalHeight: false);
        if (scale <= 1.5f) AssertSettingsCardsOpenWithoutScrolling(form);
        AssertSettingsCardGrid(form);
        AssertSettingsTextFieldsAligned(form);
        AssertSettingsFeatureIcons(form, (int)Math.Round(64 * scale));
        AssertCriticalTextFits(form);

        var hotkeyField = EnumerateControls(form)
            .OfType<TextBox>()
            .Single(control => control.AccessibleName == "Global upload-mode shortcut");
        var disable = EnumerateControls(form).OfType<Button>().Single(control => control.Text == "Disable");
        var fieldBounds = new Rectangle(hotkeyField.PointToScreen(Point.Empty), hotkeyField.Size);
        var actionBounds = new Rectangle(disable.PointToScreen(Point.Empty), disable.Size);
        var minimumUsableFieldWidth = (int)Math.Round(140 * scale);
        Assert(hotkeyField.Width >= minimumUsableFieldWidth &&
               disable.Width > 0 &&
               fieldBounds.Right <= actionBounds.Left,
            $"The shortcut editor overlaps at {scale:F1}x: field={fieldBounds}, action={actionBounds}.");
    }
    finally
    {
        foreach (var font in scaledFonts.Values) font.Dispose();
    }
}

static void AssertAboutScaledLayout(AppSettings settings, float scale)
{
    var scaledFonts = new Dictionary<(string Family, float Size, FontStyle Style), Font>();
    try
    {
        using var form = new SettingsForm(
            settings,
            checkForUpdatesAsync: _ => Task.CompletedTask,
            watcherStatusProvider: () => "Discord open — watching for clips",
            initialPage: SettingsPage.About);
        var logicalDesignedOpeningSize = SettingsForm.GetDesignedOpeningSize(SettingsPage.About, 96);
        var scaledDesignedOpeningSize = new Size(
            (int)Math.Round(logicalDesignedOpeningSize.Width * scale),
            (int)Math.Round(logicalDesignedOpeningSize.Height * scale));
        form.CreateControl();
        var relativeScale = scale / GetDpiScale(form);
        form.Scale(new SizeF(relativeScale, relativeScale));
        foreach (var control in new[] { (Control)form }.Concat(EnumerateControls(form)))
        {
            var source = control.Font;
            var key = (source.FontFamily.Name, source.Size * relativeScale, source.Style);
            if (!scaledFonts.TryGetValue(key, out var scaledFont))
            {
                scaledFont = new Font(source.FontFamily, key.Item2, source.Style, GraphicsUnit.Point);
                scaledFonts.Add(key, scaledFont);
            }
            control.Font = scaledFont;
        }

        form.AutoScroll = true;
        var rootLayout = form.Controls.Cast<Control>().Single(control => control.Name == "RootLayout");
        rootLayout.Dock = DockStyle.None;
        rootLayout.Size = scaledDesignedOpeningSize;
        rootLayout.PerformLayout();
        form.PerformLayout();
        var about = EnumerateControls(form).OfType<AboutView>().Single();
        about.RefreshViewport();
        Application.DoEvents();
        AssertControlsFit(form);
        // At a non-96-DPI runner, Control.Scale receives a relative factor while
        // DeviceDpi remains the live desktop DPI. CI runs at 96 DPI and therefore
        // exercises the exact requested 150%/200% target; local high-DPI runs
        // assert the effective scale that WinForms can represent synthetically.
        var deviceScale = GetDpiScale(form);
        var effectiveLayoutScale = Math.Max(deviceScale, deviceScale * relativeScale);
        AssertAboutLayout(form, effectiveLayoutScale);
        AssertAboutCopyAndAccessibility(form, requireVisible: false);
        Assert(!about.HasOverflow,
            $"About must not scroll at its designed {scale:F1}x opening size.");
    }
    finally
    {
        foreach (var font in scaledFonts.Values) font.Dispose();
    }
}

static void AssertAboutMinimumScroll(AppSettings settings)
{
    using var view = new AboutView(
        settings,
        watcherStatusProvider: () => "Discord open — watching for clips",
        discordRunningProvider: () => true,
        ffmpegExecutableProvider: () => null,
        processStarter: _ => { },
        clipboardWriter: _ => { },
        dataDirectory: Path.Combine(settings.ClipsFolder, "about-minimum-data"));
    using var host = new Form
    {
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-2000, -2000),
        ClientSize = new Size(900, 476)
    };
    host.Controls.Add(view);
    host.Show();
    Application.DoEvents();
    view.RefreshViewport();
    var scrollHost = EnumerateControls(view).OfType<BrandedScrollHost>()
        .Single(control => control.Name == "AboutScrollHost");
    Assert(view.HasOverflow && scrollHost.HasOverflow &&
           !EnumerateControls(view).OfType<ScrollableControl>().Any(control => control.AutoScroll),
        "A height-constrained About page must use the branded scrollbar instead of clipping or exposing native chrome.");
    var lastAction = EnumerateControls(view).OfType<Button>()
        .Single(button => button.Name == "AboutLicensesButton");
    Assert(lastAction.Focus(),
        "The final About project action must be keyboard focusable.");
    Application.DoEvents();
    var focusedBounds = new Rectangle(
        scrollHost.PointToClient(lastAction.PointToScreen(Point.Empty)),
        lastAction.Size);
    Assert(focusedBounds.Top >= 0 && focusedBounds.Bottom <= scrollHost.ClientSize.Height,
        $"Keyboard focus must scroll the final About action into view: bounds={focusedBounds}, viewport={scrollHost.ClientRectangle}.");
    host.Hide();
}

static void AssertRealGlobalHotkeyRegistration()
{
    Assert(GlobalHotkeyBinding.TryParse("Ctrl + Alt + Shift + F24", out var probeBinding),
        "The native registration probe must use a valid shortcut.");
    var first = new GlobalHotkeyManager();
    using var second = new GlobalHotkeyManager();
    try
    {
        Assert(first.TrySetBinding(probeBinding, out var firstError),
            $"Windows rejected the isolated global-shortcut probe with error {firstError}.");
        Assert(!second.TrySetBinding(probeBinding, out var conflictError) && conflictError != 0,
            "Windows must prevent two live ClipCord windows from owning the same global shortcut.");
        first.Dispose();
        Assert(second.TrySetBinding(probeBinding, out var replacementError),
            $"Disposal must release the Windows shortcut registration; retry error {replacementError}.");
    }
    finally
    {
        first.Dispose();
    }
}

static bool HasAutoScrollAncestor(Control control)
{
    for (var parent = control.Parent; parent is not null; parent = parent.Parent)
    {
        if (IsAutoScrollViewport(parent)) return true;
    }
    return false;
}

static IEnumerable<Control> EnumerateControls(Control parent)
{
    foreach (Control control in parent.Controls)
    {
        yield return control;
        foreach (var descendant in EnumerateControls(control)) yield return descendant;
    }
}

static void AssertUpdateDownloadDialogBehavior(UpdateRelease release)
{
    using (var service = new CompletingUpdateDownloadService())
    using (var form = new UpdateDownloadDialog(release, service))
    {
        form.Show();
        PumpWindowsMessagesUntil(() => service.Started, "The update download did not start.");
        var cancel = EnumerateControls(form).OfType<Button>().Single(button => button.Text == "Cancel");
        cancel.PerformClick();
        service.Complete(new DownloadedUpdate(release, "unused-after-cancel.exe"));
        PumpWindowsMessagesUntil(() => !form.Visible, "The cancelled update dialog did not close.");
        Assert(form.DialogResult == DialogResult.Cancel && form.DownloadedUpdate is null,
            "Cancellation must remain authoritative when the download completes at the same moment.");
    }

    using (var service = new FailingUpdateDownloadService())
    using (var form = new UpdateDownloadDialog(release, service))
    {
        form.Show();
        PumpWindowsMessagesUntil(
            () => EnumerateControls(form).OfType<Button>().Any(button => button.Text == "Retry" && button.Visible),
            "The verification-failure state was not shown.");
        form.PerformLayout();
        AssertControlsFit(form);
        var detail = EnumerateControls(form)
            .OfType<Label>()
            .Single(label => label.Text.StartsWith("The downloaded update could not be verified", StringComparison.Ordinal));
        var measured = TextRenderer.MeasureText(
            detail.Text,
            detail.Font,
            new Size(detail.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        Assert(detail.Height >= measured.Height,
            $"The update failure explanation is clipped: actual {detail.Height}px, required {measured.Height}px.");
        form.Close();
    }

    var closeHandler = typeof(UpdateDownloadDialog).GetMethod(
        "HandleFormClosing",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    foreach (var reason in new[]
             {
                 CloseReason.UserClosing,
                 CloseReason.WindowsShutDown,
                 CloseReason.ApplicationExitCall,
                 CloseReason.TaskManagerClosing
             })
    {
        using var service = new CompletingUpdateDownloadService();
        using var form = new UpdateDownloadDialog(release, service);
        form.Show();
        PumpWindowsMessagesUntil(() => service.Started, $"The {reason} close test did not start.");
        var eventArgs = new FormClosingEventArgs(reason, cancel: false);
        closeHandler.Invoke(form, [form, eventArgs]);
        Assert(eventArgs.Cancel == (reason == CloseReason.UserClosing),
            $"The update dialog handled {reason} incorrectly; cancelled={eventArgs.Cancel}.");
        service.Complete(new DownloadedUpdate(release, "unused-after-close.exe"));
        PumpWindowsMessagesUntil(
            () => !service.IsPending,
            $"The {reason} close test did not release its download.");
        Application.DoEvents();
        if (!form.IsDisposed) form.Close();
    }

    var disposableService = new NeverCalledUpdateDownloadService();
    var disposableForm = new UpdateDownloadDialog(release, disposableService);
    disposableForm.Dispose();
    disposableForm.Dispose();
    disposableService.Dispose();

    using var activeService = new CompletingUpdateDownloadService();
    var activeForm = new UpdateDownloadDialog(release, activeService);
    activeForm.Show();
    PumpWindowsMessagesUntil(() => activeService.Started, "The active-disposal test did not start.");
    activeForm.Dispose();
    activeForm.Dispose();
    activeForm.Dispose();
    activeService.Complete(new DownloadedUpdate(release, "unused-after-dispose.exe"));
    PumpWindowsMessagesUntil(() => !activeService.IsPending, "The active-disposal test did not complete.");
    Application.DoEvents();
    Assert(activeForm.DownloadedUpdate is null,
        "A completion arriving after active dialog disposal must not become installable.");
}

static void PumpWindowsMessagesUntil(Func<bool> condition, string failureMessage)
{
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(failureMessage);
        Application.DoEvents();
        Thread.Sleep(5);
    }
    Application.DoEvents();
}

static void PumpWindowsMessagesFor(TimeSpan duration)
{
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < duration)
    {
        Application.DoEvents();
        Thread.Sleep(5);
    }
    Application.DoEvents();
}

internal static class ModeFeedbackNativeProbe
{
    private const int ExtendedStyleIndex = -20;

    internal static int GetExtendedStyle(IntPtr windowHandle) =>
        GetWindowLong(windowHandle, ExtendedStyleIndex);

    internal static IntPtr SendWindowMessage(IntPtr windowHandle, int message) =>
        SendMessage(windowHandle, message, IntPtr.Zero, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);
}

internal sealed class NeverCalledUpdateDownloadService : IUpdateDownloadService
{
    public Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The layout test must not start a download.");

    public void Dispose()
    {
    }
}

internal sealed class CompletingUpdateDownloadService : IUpdateDownloadService
{
    private readonly TaskCompletionSource<DownloadedUpdate> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Started { get; private set; }
    public bool IsPending => !_completion.Task.IsCompleted;

    public Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Started = true;
        return _completion.Task;
    }

    public void Complete(DownloadedUpdate update) => _completion.TrySetResult(update);

    public void Dispose()
    {
    }
}

internal sealed class FailingUpdateDownloadService : IUpdateDownloadService
{
    public Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromException<DownloadedUpdate>(new InvalidDataException("Simulated verification failure."));

    public void Dispose()
    {
    }
}

internal sealed class FakeGlobalHotkeyRegistrar : IGlobalHotkeyRegistrar
{
    public const int ConflictError = 1409;
    public Queue<bool> RegisterResults { get; } = new();
    public Queue<bool> UnregisterResults { get; } = new();
    public List<GlobalHotkeyBinding> RegisterCalls { get; } = [];
    public int UnregisterCount { get; private set; }

    public bool Register(IntPtr windowHandle, int identifier, GlobalHotkeyBinding binding)
    {
        RegisterCalls.Add(binding);
        return RegisterResults.Count == 0 || RegisterResults.Dequeue();
    }

    public bool Unregister(IntPtr windowHandle, int identifier)
    {
        UnregisterCount++;
        return UnregisterResults.Count == 0 || UnregisterResults.Dequeue();
    }

    public int GetLastError() => ConflictError;
}

internal sealed class RecordingManualDiscordUploader : IManualDiscordUploader
{
    public int UploadCount { get; private set; }
    public DiscordUploadPresentation? LastPresentation { get; private set; }
    public Action? AfterSuccessfulUpload { get; init; }
    public Exception? Failure { get; init; }
    public bool EmitCompressionProgress { get; init; }

    public Task UploadAsync(
        string webhookUrl,
        string physicalPath,
        DiscordUploadPresentation presentation,
        int compressionTargetMb,
        string uploaderName,
        CancellationToken cancellationToken,
        Action<CompressionProgress>? reportCompression)
    {
        UploadCount++;
        LastPresentation = presentation;
        AssertForFake(WebhookValidation.IsDiscordWebhook(webhookUrl),
            "The manual uploader fake received an invalid webhook.");
        AssertForFake(File.Exists(physicalPath),
            "The manual uploader fake must receive an existing physical artifact.");
        if (Failure is not null) throw Failure;
        cancellationToken.ThrowIfCancellationRequested();
        if (EmitCompressionProgress)
        {
            var originalBytes = new FileInfo(physicalPath).Length;
            reportCompression?.Invoke(new CompressionProgress(
                compressionTargetMb,
                6000,
                96,
                originalBytes,
                null));
            reportCompression?.Invoke(new CompressionProgress(
                compressionTargetMb,
                6000,
                96,
                originalBytes,
                Math.Max(1, originalBytes / 2)));
        }
        AfterSuccessfulUpload?.Invoke();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    private static void AssertForFake(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class GatedWebhookHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public Task Started => _started.Task;
    public int CallCount => Volatile.Read(ref _callCount);

    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        _started.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent([])
        };
    }
}

internal sealed class RecordingOriginalClipRecycler(string recycleRoot) : IOriginalClipRecycler
{
    public List<string> RecycledPaths { get; } = [];
    public Action<string>? BeforeRecycle { get; set; }

    public void Recycle(string path)
    {
        BeforeRecycle?.Invoke(path);
        var destination = Path.Combine(
            recycleRoot,
            $"{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        File.Move(path, destination, overwrite: false);
        RecycledPaths.Add(path);
    }
}

internal sealed record TrimmedPlaybackCall(TimeSpan Start, TimeSpan End, bool MuteAudio);

internal sealed class FakeManualClipEditService : IManualClipEditService
{
    internal const double ProbeDurationSeconds = 74.8;

    private IProgress<ManualClipEditProgress>? _activeProgress;
    private TaskCompletionSource? _playbackGate;

    public int EditCalls { get; private set; }
    public ManualClipEditRequest? LastRequest { get; private set; }
    public List<TrimmedPlaybackCall> PlaybackCalls { get; } = [];
    public int PlaybackCancellations { get; private set; }

    /// <summary>Holds the next trimmed-playback render open until <see cref="ReleasePlayback"/>.</summary>
    public void GatePlayback() =>
        _playbackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleasePlayback()
    {
        var gate = _playbackGate;
        _playbackGate = null;
        gate?.TrySetResult();
    }

    public void ReportStage(ManualClipEditStage stage, string message) =>
        _activeProgress?.Report(new ManualClipEditProgress(stage, message));

    public Task<ClipMediaInfo> ProbeAsync(
        GalleryClipEntry source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ClipMediaInfo(TimeSpan.FromSeconds(ProbeDurationSeconds), source.Length));
    }

    public Task<string> CreatePreviewFrameAsync(
        GalleryClipEntry source,
        TimeSpan position,
        CancellationToken cancellationToken,
        int maxDimension = 960)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            "ClipsToDiscordTests",
            "editor-previews")).FullName;
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
        using var bitmap = new Bitmap(960, 540);
        using var graphics = Graphics.FromImage(bitmap);
        using var gradient = new System.Drawing.Drawing2D.LinearGradientBrush(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Color.FromArgb(30, 52, 88),
            Color.FromArgb(124, 57, 176),
            28f);
        graphics.FillRectangle(gradient, 0, 0, bitmap.Width, bitmap.Height);
        using var titleFont = new Font("Segoe UI", 38, FontStyle.Bold, GraphicsUnit.Pixel);
        using var detailFont = new Font("Segoe UI", 20, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(
            graphics,
            source.GameName,
            titleFont,
            new Rectangle(44, 370, 870, 60),
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            graphics,
            $"Preview at {LocalClipEditorView.FormatTime(position)}",
            detailFont,
            new Rectangle(46, 430, 850, 40),
            Color.FromArgb(220, 230, 245),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return Task.FromResult(path);
    }

    public async Task<string> CreateTrimmedPlaybackAsync(
        GalleryClipEntry source,
        TimeSpan start,
        TimeSpan end,
        bool muteAudio,
        CancellationToken cancellationToken)
    {
        // Record the exact range the editor asked for so a stale-range regression is visible.
        PlaybackCalls.Add(new TrimmedPlaybackCall(start, end, muteAudio));
        cancellationToken.ThrowIfCancellationRequested();
        var gate = _playbackGate;
        if (gate is not null)
        {
            // Observe cancellation for the whole render, not only at entry, so a trim change
            // mid-render can abandon this attempt exactly like the real FFmpeg run.
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlaybackCancellations++;
                throw;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            "ClipsToDiscordTests",
            "editor-playback")).FullName;
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".mp4");
        // Honor the requested selection instead of copying the whole source, so the produced
        // artifact reflects the trim the editor requested.
        var sourceBytes = await File.ReadAllBytesAsync(source.Path, cancellationToken);
        var totalSeconds = Math.Max(0.001, ProbeDurationSeconds);
        var ratio = Math.Clamp((end - start).TotalSeconds / totalSeconds, 0, 1);
        var offset = (int)Math.Clamp(
            Math.Round(sourceBytes.Length * (start.TotalSeconds / totalSeconds)),
            0,
            Math.Max(0, sourceBytes.Length - 1));
        var length = (int)Math.Clamp(
            Math.Round(sourceBytes.Length * ratio),
            1,
            Math.Max(1, sourceBytes.Length - offset));
        var slice = new byte[muteAudio ? length : length + 1];
        Array.Copy(sourceBytes, offset, slice, 0, length);
        await File.WriteAllBytesAsync(path, slice, cancellationToken);
        return path;
    }

    public long EstimateOutputBytes(
        long sourceBytes,
        TimeSpan sourceDuration,
        TimeSpan selectionDuration)
    {
        if (sourceDuration <= TimeSpan.Zero) return 0;
        return (long)Math.Round(sourceBytes * Math.Clamp(
            selectionDuration.TotalSeconds / sourceDuration.TotalSeconds,
            0,
            1));
    }

    public async Task<ManualClipEditResult> EditAndUploadAsync(
        ManualClipEditRequest request,
        IProgress<ManualClipEditProgress>? progress,
        CancellationToken cancellationToken)
    {
        EditCalls++;
        LastRequest = request;
        _activeProgress = progress;
        progress?.Report(new ManualClipEditProgress(
            ManualClipEditStage.Rendering,
            "Rendering test edit…"));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-only fake should never complete normally.");
        }
        finally
        {
            _activeProgress = null;
        }
    }
}
