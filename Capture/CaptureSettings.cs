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

internal static class CaptureSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string SettingsPath => Path.Combine(SettingsStore.DataDirectory, "capture-settings.json");

    internal static CaptureSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return CaptureSettings.Default;
            return PrepareForPersistence(
                JsonSerializer.Deserialize<CaptureSettings>(
                    File.ReadAllText(SettingsPath),
                    JsonOptions) ?? CaptureSettings.Default);
        }
        catch (Exception exception)
        {
            Log.Error("Could not load ClipCord Capture settings.", exception);
            return CaptureSettings.Default;
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
    /// A silhouette snapshot is a capture-command payload, not a durable preference. Persisting
    /// it here would let an old capture-settings file override newer atomic layout defaults at
    /// the next recording start.
    /// </summary>
    internal static CaptureSettings PrepareForPersistence(CaptureSettings settings) =>
        CaptureSettings.Normalize(settings) with { SilhouettePreferencesSnapshot = null };
}
