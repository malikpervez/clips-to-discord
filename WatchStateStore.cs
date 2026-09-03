using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace ClipsToDiscord;

internal enum WatchStateRoutingProbeStatus
{
    Missing,
    Loaded,
    ReleasedUnversioned,
    Corrupt,
    UnsupportedVersion,
    Invalid,
    Unavailable
}

internal sealed record WatchStateRoutingProbe(
    WatchStateRoutingProbeStatus Status,
    int PendingMoves,
    int PendingLocalOnlyMoves,
    int PendingEditedUploads,
    int IgnoredFileKeys,
    WatchState? State)
{
    internal bool Loaded => Status == WatchStateRoutingProbeStatus.Loaded;
}

internal sealed class WatchStateStore
{
    internal const int CurrentVersion = 4;
    internal const int MinimumCompatibleVersion = 2;
    internal const int MaximumStateBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _statePath;
    private readonly string _safeBaselineMarkerPath;

    public WatchStateStore()
        : this(
            Path.Combine(SettingsStore.DataDirectory, "state.json"),
            SettingsStore.SafeBaselineMarkerPath)
    {
    }

    internal WatchStateStore(string statePath, string safeBaselineMarkerPath)
    {
        _statePath = Path.GetFullPath(statePath);
        _safeBaselineMarkerPath = Path.GetFullPath(safeBaselineMarkerPath);
    }

    internal string StatePath => _statePath;

    /// <summary>
    /// Strict, read-only evidence for routing activation. Unlike LoadOrInitializeAsync this probe
    /// never creates, upgrades, normalizes, or saves state: missing, malformed, redirected, or
    /// future-version state must pause cutover rather than being interpreted as empty queues.
    /// </summary>
    internal WatchStateRoutingProbe ProbeForRoutingActivation(
        CancellationToken cancellationToken = default) =>
        ProbeReadOnlyState(allowCompatibleLegacyVersion: false, cancellationToken);

    /// <summary>
    /// Strict, read-only evidence used only to decide whether a profile already belonged to a
    /// 1.x install. The exact unversioned v1.0-v1.1 schema and versions 2 and 3 were emitted by
    /// released 1.x builds and are accepted here; the legacy runtime upgrades them to version 4
    /// before Routing can commit.
    /// </summary>
    internal WatchStateRoutingProbe ProbeForLegacyMigrationAdmission(
        CancellationToken cancellationToken = default) =>
        ProbeReadOnlyState(allowCompatibleLegacyVersion: true, cancellationToken);

    private WatchStateRoutingProbe ProbeReadOnlyState(
        bool allowCompatibleLegacyVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EnsureNoReparsePointsInExistingPath(_statePath);
            if (!File.Exists(_statePath)) return Probe(WatchStateRoutingProbeStatus.Missing);
            var info = new FileInfo(_statePath);
            if (info.Length is <= 0 or > MaximumStateBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Probe(WatchStateRoutingProbeStatus.Invalid);
            }

            var bytes = File.ReadAllBytes(_statePath);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length is <= 0 or > MaximumStateBytes)
            {
                return Probe(WatchStateRoutingProbeStatus.Invalid);
            }

            using var parsed = JsonDocument.Parse(bytes);
            var state = JsonSerializer.Deserialize<WatchState>(bytes, JsonOptions);
            if (state is null) return Probe(WatchStateRoutingProbeStatus.Corrupt);
            if (allowCompatibleLegacyVersion && state.Version == 0 &&
                IsReleasedUnversionedLegacyState(parsed.RootElement, state))
            {
                return new WatchStateRoutingProbe(
                    WatchStateRoutingProbeStatus.ReleasedUnversioned,
                    state.PendingMoves.Count,
                    state.PendingLocalOnlyMoves.Count,
                    state.PendingEditedUploads.Count,
                    state.IgnoredFileKeys.Count,
                    state);
            }
            var versionSupported = allowCompatibleLegacyVersion
                ? state.Version is >= MinimumCompatibleVersion and <= CurrentVersion
                : state.Version == CurrentVersion;
            if (!versionSupported)
            {
                return Probe(WatchStateRoutingProbeStatus.UnsupportedVersion);
            }
            if (allowCompatibleLegacyVersion)
                ValidateLegacyAdmissionDocument(parsed.RootElement, state.Version);
            ValidateRoutingProbeState(state);
            if (state.Version < CurrentVersion &&
                state.CaptureSource != ClipCaptureSource.SteelSeriesGg)
            {
                return Probe(WatchStateRoutingProbeStatus.Invalid);
            }
            return new WatchStateRoutingProbe(
                WatchStateRoutingProbeStatus.Loaded,
                state.PendingMoves.Count,
                state.PendingLocalOnlyMoves.Count,
                state.PendingEditedUploads.Count,
                state.IgnoredFileKeys.Count,
                state);
        }
        catch (JsonException)
        {
            return Probe(WatchStateRoutingProbeStatus.Corrupt);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                NullReferenceException or OverflowException)
        {
            return Probe(WatchStateRoutingProbeStatus.Invalid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Probe(WatchStateRoutingProbeStatus.Unavailable);
        }
    }

    public async Task<WatchState> LoadOrInitializeAsync(
        string clipsFolder,
        Action<string> reportStatus,
        CancellationToken cancellationToken,
        ClipCaptureSource captureSource = ClipCaptureSource.SteelSeriesGg)
    {
        captureSource = AppSettings.NormalizeCaptureSource(captureSource);
        var forceSafeBaseline = File.Exists(_safeBaselineMarkerPath);
        WatchState? saved = null;
        var unsupportedFutureVersion = false;
        try
        {
            if (File.Exists(_statePath))
            {
                var serialized = File.ReadAllText(_statePath);
                using var parsed = JsonDocument.Parse(serialized);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object &&
                    parsed.RootElement.TryGetProperty(nameof(WatchState.Version), out var version) &&
                    version.ValueKind == JsonValueKind.Number &&
                    (!version.TryGetInt32(out var persistedVersion) ||
                     persistedVersion > CurrentVersion))
                {
                    unsupportedFutureVersion = true;
                }
                else
                {
                    saved = JsonSerializer.Deserialize<WatchState>(serialized, JsonOptions);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not read uploader state; creating a safe baseline.", exception);
        }

        if (unsupportedFutureVersion)
        {
            reportStatus("Watcher state is from a newer ClipCord version — processing is paused");
            throw new InvalidDataException(
                "The uploader state belongs to a newer ClipCord version and cannot be downgraded safely.");
        }

        if (!forceSafeBaseline && saved is not null &&
            saved.Version is >= MinimumCompatibleVersion and <= CurrentVersion)
        {
            var needsLocalOnlyBaseline = saved.Version < 3;
            var needsVersionUpgrade = saved.Version < CurrentVersion;
            Normalize(saved);
            var sameFolder = saved.ClipsFolder.Equals(clipsFolder, StringComparison.OrdinalIgnoreCase);
            var sameSource = saved.CaptureSource == captureSource;
            if (sameFolder && sameSource)
            {
                var repairedStaleIgnoredKeys = RemoveIgnoredKeysOutsideCurrentSource(
                    saved,
                    clipsFolder,
                    captureSource);
                if (needsLocalOnlyBaseline)
                {
                    await AddLocalOnlyBaselineAsync(saved, clipsFolder, cancellationToken);
                }
                var repairedDeliveryClassifications = ApplyUploadedClassificationPrecedence(saved);
                if (needsLocalOnlyBaseline || needsVersionUpgrade ||
                    repairedStaleIgnoredKeys || repairedDeliveryClassifications)
                {
                    Save(saved);
                }
                return saved;
            }

            // A new capture source looks somewhere the previous one never did. Baseline what
            // it finds exactly like a folder change, so switching can never bulk-upload an
            // existing capture backlog.
            reportStatus(sameFolder
                ? "Capture source changed — building a safe baseline"
                : "Clips folder changed — building a safe baseline");
            saved.ClipsFolder = clipsFolder;
            saved.CaptureSource = captureSource;
            // Exact file keys are meaningful only inside the source geometry that created
            // them. Retaining keys from an older folder/source cannot protect any candidate
            // in the new scan, and later makes a safe Routing cutover impossible to prove.
            saved.IgnoredFileKeys.Clear();
            await AddSafeBaselineAsync(saved, clipsFolder, cancellationToken, captureSource);
            ApplyUploadedClassificationPrecedence(saved);
            Save(saved);
            return saved;
        }

        reportStatus(forceSafeBaseline
            ? "Recovering migration — building a safe baseline"
            : "Building content-hash baseline");

        var state = new WatchState
        {
            Version = CurrentVersion,
            ClipsFolder = clipsFolder,
            CaptureSource = captureSource,
            PendingMoves = saved?.PendingMoves ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingLocalOnlyMoves = saved?.PendingLocalOnlyMoves ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            PendingEditedUploads = saved?.PendingEditedUploads ?? []
        };
        Normalize(state);
        await AddSafeBaselineAsync(state, clipsFolder, cancellationToken, captureSource);
        ApplyUploadedClassificationPrecedence(state);
        Save(state);
        TryDeleteSafeBaselineMarker();
        Log.Info($"Initialized content-hash state with {state.KnownContentHashes.Count} existing clip(s); they will not be uploaded.");
        return state;
    }

    public void Save(WatchState state)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOptions));
        if (payload.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidDataException("The uploader state is too large.");
        }
        var stateDirectory = Path.GetDirectoryName(_statePath)
            ?? throw new InvalidOperationException("The state directory could not be determined.");
        Directory.CreateDirectory(stateDirectory);
        var temporaryPath = _statePath + ".tmp";
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, _statePath, true);
    }

    public static string FileKey(FileInfo file) =>
        $"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    /// <summary>
    /// Folders ClipCord owns. A per-game scan must never treat one as a game folder, or the
    /// app would rediscover its own archive and upload it again.
    /// </summary>
    private static readonly string[] ManagedChildFolders = AppSettings.ManagedChildFolderNames;

    public static IEnumerable<string> EnumerateClips(
        string clipsFolder,
        ClipCaptureSource captureSource = ClipCaptureSource.SteelSeriesGg) =>
        EnumerateCandidateClips(clipsFolder, captureSource).OrderBy(File.GetLastWriteTimeUtc);

    private static IEnumerable<string> EnumerateCandidateClips(
        string clipsFolder,
        ClipCaptureSource captureSource)
    {
        if (AppSettings.NormalizeCaptureSource(captureSource) != ClipCaptureSource.Nvidia)
        {
            return Directory.EnumerateFiles(clipsFolder, "*.mp4", SearchOption.TopDirectoryOnly);
        }

        // NVIDIA organizes clips into <watched folder>\<game>\clip.mp4, so the configured
        // folder is the one holding those game folders. Scanning exactly one level keeps the
        // blast radius to that folder's own children.
        return EnumerateGameFolderClips(clipsFolder);
    }

    private static IEnumerable<string> EnumerateGameFolderClips(string captureRoot)
    {
        string[] gameFolders;
        try
        {
            gameFolders = Directory.EnumerateDirectories(captureRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not list capture game folders.", exception);
            yield break;
        }

        foreach (var gameFolder in gameFolders)
        {
            DirectoryInfo info;
            try { info = new DirectoryInfo(gameFolder); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            if (ManagedChildFolders.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string[] clips;
            try
            {
                clips = Directory.EnumerateFiles(gameFolder, "*.mp4", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not read capture game folder {info.Name}.", exception);
                continue;
            }
            foreach (var clip in clips) yield return clip;
        }
    }

    private async Task AddSafeBaselineAsync(
        WatchState state,
        string clipsFolder,
        CancellationToken cancellationToken,
        ClipCaptureSource captureSource = ClipCaptureSource.SteelSeriesGg)
    {
        var topLevelPaths = EnumerateClips(clipsFolder, captureSource).ToList();
        var uploadedFolder = UploadedFolder.GetOrCreate(clipsFolder);
        var uploadedPaths = UploadedFolder.EnumerateArchivedClips(uploadedFolder).ToList();

        foreach (var path in topLevelPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                state.IgnoredFileKeys.Add(FileKey(file));
                state.KnownContentHashes.Add(
                    await ContentIdentity.ComputeSha256Async(path, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not hash baseline clip {Path.GetFileName(path)}; it remains protected by its file key.", exception);
            }
        }

        foreach (var path in uploadedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contentHash = await ContentIdentity.ComputeSha256Async(path, cancellationToken);
                state.KnownContentHashes.Add(contentHash);
                state.UploadedContentHashes.Add(contentHash);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not hash archived baseline clip {Path.GetFileName(path)}.", exception);
            }
        }

        await AddLocalOnlyBaselineAsync(state, clipsFolder, cancellationToken);
    }

    private static async Task AddLocalOnlyBaselineAsync(
        WatchState state,
        string clipsFolder,
        CancellationToken cancellationToken)
    {
        var localOnlyFolder = UploadedFolder.FindExistingLocalOnly(clipsFolder);
        if (localOnlyFolder is null) return;

        foreach (var path in UploadedFolder.EnumerateArchivedClips(localOnlyFolder)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contentHash = await ContentIdentity.ComputeSha256Async(path, cancellationToken);
                state.KnownContentHashes.Add(contentHash);
                state.LocalOnlyContentHashes.Add(contentHash);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Error($"Could not hash local-only baseline clip {Path.GetFileName(path)}.", exception);
            }
        }
    }

    private static void Normalize(WatchState state)
    {
        state.Version = CurrentVersion;
        state.ClipsFolder ??= string.Empty;
        state.CaptureSource = AppSettings.NormalizeCaptureSource(state.CaptureSource);
        state.KnownContentHashes = new HashSet<string>(
            state.KnownContentHashes ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.IgnoredFileKeys = new HashSet<string>(
            state.IgnoredFileKeys ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.UploadedContentHashes = new HashSet<string>(
            state.UploadedContentHashes ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.LocalOnlyContentHashes = new HashSet<string>(
            state.LocalOnlyContentHashes ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.PendingMoves = new HashSet<string>(
            state.PendingMoves ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.PendingLocalOnlyMoves = new HashSet<string>(
            state.PendingLocalOnlyMoves ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.PendingEditedUploads = (state.PendingEditedUploads ?? [])
            .Where(pending => pending is not null && pending.Id != Guid.Empty)
            .GroupBy(pending => pending.Id)
            .Select(group => group.First())
            .ToList();
        state.KnownSignatures = null;
    }

    /// <summary>
    /// A content hash can legitimately have both histories in legacy builds: it may have been
    /// uploaded once and later encountered while Local-only mode was selected. Routing needs one
    /// conservative duplicate classification, so Uploaded wins. This can never cause a repost;
    /// the hash remains in KnownContentHashes and in the stronger Uploaded exclusion set.
    /// </summary>
    private static bool ApplyUploadedClassificationPrecedence(WatchState state) =>
        state.LocalOnlyContentHashes.RemoveWhere(state.UploadedContentHashes.Contains) != 0;

    /// <summary>
    /// Removes only well-formed exact file keys that provably cannot belong to the currently
    /// configured scanner. Older builds retained these after folder/source changes. Malformed or
    /// ambiguous evidence is deliberately kept so the strict Routing probe still fails closed.
    /// </summary>
    private static bool RemoveIgnoredKeysOutsideCurrentSource(
        WatchState state,
        string clipsFolder,
        ClipCaptureSource captureSource)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clipsFolder));
        return state.IgnoredFileKeys.RemoveWhere(key =>
            TryParseIgnoredFileKeyPath(key, out var path) &&
            !IsPathInCaptureSource(root, path!, captureSource)) != 0;
    }

    private static bool TryParseIgnoredFileKeyPath(string? key, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var ticksSeparator = key.LastIndexOf('|');
        var lengthSeparator = ticksSeparator <= 0
            ? -1
            : key.LastIndexOf('|', ticksSeparator - 1);
        if (lengthSeparator <= 0 || ticksSeparator <= lengthSeparator + 1 ||
            ticksSeparator == key.Length - 1 ||
            !long.TryParse(key[(lengthSeparator + 1)..ticksSeparator],
                NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            !long.TryParse(key[(ticksSeparator + 1)..],
                NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            length < 0 || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }
        var pathText = key[..lengthSeparator];
        if (!Path.IsPathFullyQualified(pathText)) return false;
        try
        {
            var canonical = Path.GetFullPath(pathText);
            if (!pathText.Equals(canonical.ToLowerInvariant(), StringComparison.Ordinal))
                return false;
            path = canonical;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsPathInCaptureSource(
        string root,
        string candidate,
        ClipCaptureSource captureSource)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or "..") ||
            components.Length == 0 ||
            !Path.GetExtension(components[^1]).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return AppSettings.NormalizeCaptureSource(captureSource) switch
        {
            ClipCaptureSource.SteelSeriesGg => components.Length == 1,
            ClipCaptureSource.Nvidia => components.Length == 2 &&
                                        !ManagedChildFolders.Contains(
                                            components[0], StringComparer.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static WatchStateRoutingProbe Probe(WatchStateRoutingProbeStatus status) =>
        new(status, 0, 0, 0, 0, null);

    private static void ValidateRoutingProbeState(WatchState state)
    {
        if (string.IsNullOrWhiteSpace(state.ClipsFolder) ||
            !Path.IsPathFullyQualified(state.ClipsFolder) ||
            !Enum.IsDefined(state.CaptureSource) ||
            state.KnownContentHashes is null || state.UploadedContentHashes is null ||
            state.LocalOnlyContentHashes is null || state.IgnoredFileKeys is null ||
            state.PendingMoves is null || state.PendingLocalOnlyMoves is null ||
            state.PendingEditedUploads is null)
        {
            throw new InvalidDataException("The legacy watcher state is incomplete.");
        }

        ValidateHashes(state.KnownContentHashes);
        ValidateHashes(state.UploadedContentHashes);
        ValidateHashes(state.LocalOnlyContentHashes);
        var known = state.KnownContentHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!state.UploadedContentHashes.All(known.Contains) ||
            !state.LocalOnlyContentHashes.All(known.Contains) ||
            state.IgnoredFileKeys.Any(string.IsNullOrWhiteSpace) ||
            state.PendingMoves.Any(path => string.IsNullOrWhiteSpace(path) ||
                                           !Path.IsPathFullyQualified(path)) ||
            state.PendingLocalOnlyMoves.Any(path => string.IsNullOrWhiteSpace(path) ||
                                                    !Path.IsPathFullyQualified(path)) ||
            state.PendingEditedUploads.Any(pending => pending is null || pending.Id == Guid.Empty))
        {
            throw new InvalidDataException("The legacy watcher state is inconsistent.");
        }
        // A hash can legitimately have both histories in legacy builds: uploaded once, then
        // encountered again while Local-only mode was selected. The quiesced Routing migration
        // canonicalizes that history with Uploaded precedence before committing its marker.
    }

    private static void ValidateLegacyAdmissionDocument(JsonElement root, int version)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The legacy watcher state is not a JSON object.");

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
                throw new InvalidDataException("The legacy watcher state contains duplicate fields.");
        }

        var required = version switch
        {
            2 => new[]
            {
                "Version", "ClipsFolder", "KnownContentHashes", "UploadedContentHashes",
                "IgnoredFileKeys", "PendingMoves"
            },
            3 => new[]
            {
                "Version", "ClipsFolder", "KnownContentHashes", "UploadedContentHashes",
                "LocalOnlyContentHashes", "IgnoredFileKeys", "PendingMoves",
                "PendingLocalOnlyMoves"
            },
            4 => new[]
            {
                "Version", "ClipsFolder", "KnownContentHashes", "UploadedContentHashes",
                "LocalOnlyContentHashes", "IgnoredFileKeys", "PendingMoves",
                "PendingLocalOnlyMoves", "PendingEditedUploads"
            },
            _ => throw new InvalidDataException(
                "The legacy watcher state version is not admission-compatible.")
        };
        if (required.Any(property => !propertyNames.Contains(property)))
            throw new InvalidDataException("The legacy watcher state is missing required fields.");
    }

    private static bool IsReleasedUnversionedLegacyState(
        JsonElement root,
        WatchState state)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
                throw new InvalidDataException(
                    "The unversioned legacy watcher state contains duplicate fields.");
        }
        if (!propertyNames.SetEquals(
                ["ClipsFolder", "KnownSignatures", "PendingMoves"]))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(state.ClipsFolder) ||
            !Path.IsPathFullyQualified(state.ClipsFolder) ||
            state.KnownSignatures is null ||
            state.KnownSignatures.Any(string.IsNullOrWhiteSpace) ||
            state.PendingMoves is null ||
            state.PendingMoves.Any(path => string.IsNullOrWhiteSpace(path) ||
                                           !Path.IsPathFullyQualified(path)))
        {
            throw new InvalidDataException(
                "The unversioned legacy watcher state is incomplete or inconsistent.");
        }
        return true;
    }

    private static void ValidateHashes(IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
        {
            if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("The legacy watcher state contains an invalid hash.");
            }
        }
    }

    private static void EnsureNoReparsePointsInExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ??
                   throw new IOException("The watcher state path has no filesystem root.");
        var current = root;
        foreach (var component in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Watcher state cannot traverse a symbolic link, mount point, or junction.");
            }
        }
    }

    private void TryDeleteSafeBaselineMarker()
    {
        try { File.Delete(_safeBaselineMarkerPath); } catch { }
    }
}

internal sealed class WatchState
{
    public int Version { get; set; }
    public string ClipsFolder { get; set; } = string.Empty;

    /// <summary>
    /// The capture source this baseline was built for. Changing it re-baselines, because a
    /// different source scans a different part of the watched folder.
    /// </summary>
    public ClipCaptureSource CaptureSource { get; set; } = ClipCaptureSource.SteelSeriesGg;
    public HashSet<string> KnownContentHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UploadedContentHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // This subset records why known content must remain local. KnownContentHashes is the
    // enforcement guard; this separate set preserves the routing history for migration,
    // recovery, diagnostics, and future activity UI without ever implying an upload.
    public HashSet<string> LocalOnlyContentHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> IgnoredFileKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PendingMoves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PendingLocalOnlyMoves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PendingEditedClipDisposition> PendingEditedUploads { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? KnownSignatures { get; set; }
}

internal sealed record PendingEditedClipDisposition
{
    public Guid Id { get; init; }
    public string ClipsFolder { get; init; } = string.Empty;
    public string EditedPath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public string OriginalLocalOnlyPath { get; init; } = string.Empty;
    public string EditedContentHash { get; init; } = string.Empty;
    public string OriginalContentHash { get; init; } = string.Empty;
    public bool KeepOriginal { get; init; }
    public long OutputBytes { get; init; }
}
