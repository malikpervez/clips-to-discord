using System.Text.Json;

namespace ClipsToDiscord;

internal sealed record CaptureSettings(
    bool InstantReplayEnabled,
    CaptureResolution Resolution,
    int FramesPerSecond,
    int ReplaySeconds,
    string SaveHotkey,
    bool RecordGameAudio,
    string GameAudioDevice,
    bool IncludeMicrophone,
    string MicrophoneDevice,
    bool IncludeVoiceChat,
    string VoiceChatDevice,
    bool IncludeReactionCamera,
    string CameraDevice,
    string LibraryRoot)
{
    internal const bool ReactionCameraAvailable = true;
    internal const string DefaultSaveHotkey = "Ctrl + Alt + S";
    internal const string DefaultOutputDevice = "Default output device";
    internal const string DefaultMicrophoneDevice = "Default microphone";
    internal const string DefaultVoiceChatDevice = "Default communications device";
    internal const string DefaultCameraDevice = "Default camera";
    internal static IReadOnlyList<int> ReplayDurationChoices { get; } = [15, 30, 60, 120, 300];

    // These are additive JSON properties so existing 2.0 preview settings continue to load.
    // The stable device id drives capture; CameraDevice remains the human-readable label.
    public string CameraDeviceId { get; init; } = string.Empty;
    public bool ReactionCameraConsentGranted { get; init; }
    public bool SilhouetteLandscapeEnabled { get; init; } = true;
    public bool SilhouettePortraitEnabled { get; init; }
    // Resolved by the UI process at capture start and carried across the capture-host
    // boundary. The worker must never reread mutable global layout defaults while a clip
    // is recording or finalizing.
    public SilhouettePreferencesDocument? SilhouettePreferencesSnapshot { get; init; }

    internal static CaptureSettings Default => new(
        InstantReplayEnabled: false,
        CaptureResolution.FullHd1080p,
        FramesPerSecond: 60,
        ReplaySeconds: 60,
        SaveHotkey: DefaultSaveHotkey,
        RecordGameAudio: true,
        GameAudioDevice: DefaultOutputDevice,
        IncludeMicrophone: false,
        MicrophoneDevice: DefaultMicrophoneDevice,
        IncludeVoiceChat: false,
        VoiceChatDevice: DefaultVoiceChatDevice,
        IncludeReactionCamera: false,
        CameraDevice: DefaultCameraDevice,
        LibraryRoot: CaptureLibraryLayout.GetDefaultRoot());

    internal bool HasAudio => RecordGameAudio || IncludeMicrophone || IncludeVoiceChat;

    internal CaptureProfile Profile => new(
        Resolution,
        FramesPerSecond,
        TimeSpan.FromSeconds(ReplaySeconds),
        IncludeReactionCamera);

    internal bool HasHotkeyConflict(AppSettings externalSettings) =>
        !string.IsNullOrWhiteSpace(SaveHotkey) &&
        string.Equals(
            NormalizeHotkey(SaveHotkey),
            AppSettings.NormalizeModeToggleHotkey(externalSettings.ModeToggleHotkey),
            StringComparison.OrdinalIgnoreCase);

    internal bool OverlapsExternalFolder(string? clipsFolder) =>
        CapturePathPolicy.PathsOverlap(LibraryRoot, clipsFolder);

    internal static CaptureSettings Normalize(CaptureSettings? value)
    {
        var defaults = Default;
        if (value is null) return defaults;
        var resolution = Enum.IsDefined(value.Resolution)
            ? value.Resolution
            : defaults.Resolution;
        var fps = value.FramesPerSecond is 30 or 60
            ? value.FramesPerSecond
            : defaults.FramesPerSecond;
        var replaySeconds = ReplayDurationChoices.Contains(value.ReplaySeconds)
            ? value.ReplaySeconds
            : defaults.ReplaySeconds;
        var landscapeEnabled = value.SilhouetteLandscapeEnabled;
        var portraitEnabled = value.SilhouettePortraitEnabled;
        // Reaction Camera always has at least one automatic composition target. Restoring
        // Landscape is deterministic and matches the existing Local/Discord capture flow.
        if (!landscapeEnabled && !portraitEnabled) landscapeEnabled = true;
        return value with
        {
            Resolution = resolution,
            FramesPerSecond = fps,
            ReplaySeconds = replaySeconds,
            SaveHotkey = NormalizeHotkey(value.SaveHotkey),
            GameAudioDevice = NormalizeDevice(value.GameAudioDevice, DefaultOutputDevice),
            MicrophoneDevice = NormalizeDevice(value.MicrophoneDevice, DefaultMicrophoneDevice),
            VoiceChatDevice = NormalizeDevice(value.VoiceChatDevice, DefaultVoiceChatDevice),
            IncludeReactionCamera = value.ReactionCameraConsentGranted && value.IncludeReactionCamera,
            CameraDevice = NormalizeDevice(value.CameraDevice, DefaultCameraDevice),
            CameraDeviceId = value.CameraDeviceId?.Trim() ?? string.Empty,
            SilhouetteLandscapeEnabled = landscapeEnabled,
            SilhouettePortraitEnabled = portraitEnabled,
            SilhouettePreferencesSnapshot = NormalizeSilhouettePreferences(
                value.SilhouettePreferencesSnapshot),
            LibraryRoot = NormalizeLibraryRoot(value.LibraryRoot)
        };
    }

    internal static string NormalizeHotkey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultSaveHotkey;
        return GlobalHotkeyBinding.TryParse(value, out var binding)
            ? binding.DisplayText
            : DefaultSaveHotkey;
    }

    private static string NormalizeDevice(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static SilhouettePreferencesDocument? NormalizeSilhouettePreferences(
        SilhouettePreferencesDocument? value)
    {
        if (value is null) return null;
        try
        {
            // Camera preview/capture is mirrored by default today. A saved transform keeps
            // its explicit mirror flag; this value only supplies deterministic fallbacks for
            // a partially populated additive settings payload.
            return SilhouettePreferencesModel.Normalize(value, mirrorCamera: true);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string NormalizeLibraryRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CaptureLibraryLayout.GetDefaultRoot();
        try { return Path.GetFullPath(value.Trim()); }
        catch { return CaptureLibraryLayout.GetDefaultRoot(); }
    }
}

internal static class CapturePathPolicy
{
    internal static bool PathsOverlap(string? left, string? right)
    {
        if (!TryNormalize(left, out var normalizedLeft) || !TryNormalize(right, out var normalizedRight))
        {
            return false;
        }

        return IsSameOrChild(normalizedLeft, normalizedRight) ||
               IsSameOrChild(normalizedRight, normalizedLeft);
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return normalized.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        if (candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}

internal enum CaptureSettingsDocumentStatus
{
    Loaded,
    Missing,
    Invalid,
    Unavailable
}

internal sealed record CaptureSettingsDocumentInspection(
    CaptureSettingsDocumentStatus Status,
    CaptureSettings? Settings,
    Exception? Error = null)
{
    internal bool Loaded => Status == CaptureSettingsDocumentStatus.Loaded && Settings is not null;
}

internal sealed record CaptureSettingsDocumentBackup(bool Existed, byte[]? Contents);

internal static class CaptureSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal const int MaximumDocumentBytes = 256 * 1024;
    internal static string SettingsPath => Path.Combine(SettingsStore.DataDirectory, "capture-settings.json");

    internal static CaptureSettings Load()
    {
        var inspection = Inspect();
        if (inspection.Loaded) return inspection.Settings!;
        if (inspection.Error is not null)
        {
            Log.Error("Could not load ClipCord Capture settings.", inspection.Error);
        }
        return CaptureSettings.Default;
    }

    /// <summary>
    /// Strict status-bearing load for authority decisions. Unlike <see cref="Load"/>, this never
    /// turns absent, malformed, unreadable, or path-less evidence into defaults.
    /// </summary>
    internal static CaptureSettingsDocumentInspection Inspect(string? path = null)
    {
        var candidate = path ?? SettingsPath;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
            var canonical = Path.GetFullPath(candidate);
            if (Directory.Exists(canonical))
            {
                return new CaptureSettingsDocumentInspection(
                    CaptureSettingsDocumentStatus.Invalid,
                    null,
                    new InvalidDataException(
                        "A directory occupies the Capture settings document path."));
            }
            if (!File.Exists(canonical))
            {
                return new CaptureSettingsDocumentInspection(
                    CaptureSettingsDocumentStatus.Missing,
                    null);
            }
            var length = new FileInfo(canonical).Length;
            if (length <= 0 || length > MaximumDocumentBytes)
            {
                return new CaptureSettingsDocumentInspection(
                    CaptureSettingsDocumentStatus.Invalid,
                    null,
                    new InvalidDataException(
                        "The Capture settings document has an invalid size."));
            }
            var raw = JsonSerializer.Deserialize<CaptureSettings>(
                File.ReadAllText(canonical),
                JsonOptions);
            if (raw is null || string.IsNullOrWhiteSpace(raw.LibraryRoot) ||
                !Path.IsPathFullyQualified(raw.LibraryRoot))
            {
                return new CaptureSettingsDocumentInspection(
                    CaptureSettingsDocumentStatus.Invalid,
                    null,
                    new InvalidDataException(
                        "The Capture settings document has no canonical library root."));
            }
            _ = Path.GetFullPath(raw.LibraryRoot.Trim());
            return new CaptureSettingsDocumentInspection(
                CaptureSettingsDocumentStatus.Loaded,
                PrepareForPersistence(raw));
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return new CaptureSettingsDocumentInspection(
                CaptureSettingsDocumentStatus.Invalid,
                null,
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            return new CaptureSettingsDocumentInspection(
                CaptureSettingsDocumentStatus.Unavailable,
                null,
                exception);
        }
    }

    internal static void Save(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = PrepareForPersistence(settings);
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    /// <summary>
    /// Captures the exact settings bytes so a failed authority repair can restore malformed or
    /// otherwise legacy evidence byte-for-byte instead of silently replacing it with defaults.
    /// </summary>
    internal static CaptureSettingsDocumentBackup CreateBackup()
    {
        if (Directory.Exists(SettingsPath))
        {
            throw new InvalidDataException(
                "A directory occupies the Capture settings document path.");
        }
        if (!File.Exists(SettingsPath)) return new(false, null);
        var contents = File.ReadAllBytes(SettingsPath);
        if (contents.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                "The Capture settings document is too large to repair transactionally.");
        }
        return new(true, contents);
    }

    internal static void RestoreBackup(CaptureSettingsDocumentBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        if (!backup.Existed)
        {
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
            return;
        }
        var contents = backup.Contents ?? throw new InvalidDataException(
            "The Capture settings backup is incomplete.");
        var temporaryPath = SettingsPath + ".rollback.tmp";
        File.WriteAllBytes(temporaryPath, contents);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    /// <summary>
    /// A silhouette snapshot is a capture-command payload, not a durable preference. Persisting
    /// it here would let an old capture-settings file override newer atomic layout defaults at
    /// the next recording start.
    /// </summary>
    internal static CaptureSettings PrepareForPersistence(CaptureSettings settings) =>
        CaptureSettings.Normalize(settings) with { SilhouettePreferencesSnapshot = null };
}
