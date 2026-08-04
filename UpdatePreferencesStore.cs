using System.Text;
using System.Text.Json;

namespace ClipsToDiscord;

internal sealed record UpdatePreferences(
    DateTimeOffset? LastAutomaticCheckUtc = null,
    string? SkippedVersion = null,
    string? RemindVersion = null,
    DateTimeOffset? RemindAfterUtc = null);

internal interface IUpdatePreferencesStore
{
    UpdatePreferences Load();
    void Save(UpdatePreferences preferences);
}

internal sealed class UpdatePreferencesStore : IUpdatePreferencesStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public UpdatePreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(SettingsStore.DataDirectory, "updates.json");
    }

    public UpdatePreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UpdatePreferences();
            var stored = JsonSerializer.Deserialize<StoredUpdatePreferences>(File.ReadAllText(_path), JsonOptions);
            if (stored is null || stored.Version != CurrentVersion) return new UpdatePreferences();

            return new UpdatePreferences(
                stored.LastAutomaticCheckUtc?.ToUniversalTime(),
                NormalizeVersion(stored.SkippedVersion),
                NormalizeVersion(stored.RemindVersion),
                stored.RemindAfterUtc?.ToUniversalTime());
        }
        catch (Exception exception)
        {
            Log.Error("Could not load update preferences.", exception);
            return new UpdatePreferences();
        }
    }

    public void Save(UpdatePreferences preferences)
    {
        var stored = new StoredUpdatePreferences
        {
            Version = CurrentVersion,
            LastAutomaticCheckUtc = preferences.LastAutomaticCheckUtc?.ToUniversalTime(),
            SkippedVersion = NormalizeVersion(preferences.SkippedVersion),
            RemindVersion = NormalizeVersion(preferences.RemindVersion),
            RemindAfterUtc = preferences.RemindAfterUtc?.ToUniversalTime()
        };
        var serialized = JsonSerializer.Serialize(stored, JsonOptions);

        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Update preference path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(serialized);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static string? NormalizeVersion(string? value) =>
        StableVersion.TryParse(value, out var version) ? version.ToString() : null;

    private sealed class StoredUpdatePreferences
    {
        public int Version { get; set; } = CurrentVersion;
        public DateTimeOffset? LastAutomaticCheckUtc { get; set; }
        public string? SkippedVersion { get; set; }
        public string? RemindVersion { get; set; }
        public DateTimeOffset? RemindAfterUtc { get; set; }
    }
}
