using System.Globalization;

namespace ClipsToDiscord;

/// <summary>
/// Converts the legacy watcher's path-and-metadata baseline into content-hash exclusions before
/// Routing cutover. The legacy worker must already be quiesced. Work is accumulated in a detached
/// state copy and persisted once, so cancellation or failure before Save leaves durable state
/// unchanged.
/// </summary>
internal sealed class LegacyIgnoredBaselineReconciler
{
    private readonly WatchStateStore _watchState;
    private readonly Func<ClipCaptureSource, IRoutingWatchedSourceAdapter> _adapterProvider;

    internal LegacyIgnoredBaselineReconciler(
        WatchStateStore watchState,
        Func<ClipCaptureSource, IRoutingWatchedSourceAdapter>? adapterProvider = null)
    {
        _watchState = watchState ?? throw new ArgumentNullException(nameof(watchState));
        _adapterProvider = adapterProvider ?? RoutingWatchedSourceAdapters.Get;
    }

    internal async Task<WatchStateRoutingProbe> ReconcileAsync(
        AppSettings settings,
        bool legacyWorkerQuiesced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!legacyWorkerQuiesced)
        {
            throw new InvalidOperationException(
                "The legacy watcher must be quiesced before its ignored baseline is reconciled.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var probe = _watchState.ProbeForRoutingActivation(cancellationToken);
        if (!probe.Loaded || probe.State is null)
        {
            throw new InvalidDataException(
                $"The legacy watcher baseline cannot be reconciled safely ({probe.Status}).");
        }
        var state = probe.State;
        var source = RequireExactSource(settings, state);
        if (state.IgnoredFileKeys.Count == 0) return RequireDrainedProbe(cancellationToken);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.ClipsFolder));
        var adapter = _adapterProvider(source) ??
                      throw new InvalidDataException(
                          "The legacy capture-source adapter is unavailable.");
        if (adapter.Source != source)
        {
            throw new InvalidDataException(
                "The legacy baseline was passed to the wrong capture-source adapter.");
        }

        var parsed = state.IgnoredFileKeys
            .Select(ParseFileKey)
            .OrderBy(item => item.CanonicalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parsed.GroupBy(item => item.CanonicalPath, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "The legacy ignored baseline contains ambiguous evidence for one path.");
        }

        var reconciled = Clone(state);
        var addedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireLexicalGeometry(root, item.CanonicalPath, source);
            EnsureExistingComponentsAreOrdinary(root, item.CanonicalPath);
            if (!TryReadCurrentFileFacts(
                    item.CanonicalPath,
                    out var currentLength,
                    out var currentLastWriteUtcTicks))
            {
                // A missing exact occurrence is stale protection, not evidence for whatever may
                // later appear at the same name.
                reconciled.IgnoredFileKeys.Remove(item.OriginalKey);
                continue;
            }

            if (currentLength != item.ByteLength ||
                currentLastWriteUtcTicks != item.LastWriteUtcTicks)
            {
                // A changed occurrence is intentionally not grandfathered into the content-hash
                // exclusion set. Routing may observe it later as new content.
                reconciled.IgnoredFileKeys.Remove(item.OriginalKey);
                continue;
            }

            var fingerprint = await adapter.OpenAndFingerprintAsync(
                    root,
                    item.CanonicalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fingerprint.Source != source ||
                fingerprint.NativeFileIdentity.ByteLength != item.ByteLength ||
                fingerprint.NativeFileIdentity.LastWriteUtcTicks != item.LastWriteUtcTicks)
            {
                throw new InvalidDataException(
                    "A legacy ignored file changed while its baseline was reconciled.");
            }
            reconciled.KnownContentHashes.Add(fingerprint.ContentSha256);
            addedHashes.Add(fingerprint.ContentSha256);
            reconciled.IgnoredFileKeys.Remove(item.OriginalKey);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (reconciled.IgnoredFileKeys.Count != 0)
        {
            throw new InvalidDataException(
                "The legacy ignored baseline was not reconciled completely.");
        }
        _watchState.Save(reconciled);

        var verified = RequireDrainedProbe(CancellationToken.None);
        if (verified.State is null ||
            !addedHashes.All(verified.State.KnownContentHashes.Contains))
        {
            throw new InvalidDataException(
                "The reconciled legacy content-hash baseline could not be verified.");
        }
        return verified;
    }

    private WatchStateRoutingProbe RequireDrainedProbe(CancellationToken cancellationToken)
    {
        var verified = _watchState.ProbeForRoutingActivation(cancellationToken);
        if (!verified.Loaded || verified.State is null || verified.IgnoredFileKeys != 0)
        {
            throw new InvalidDataException(
                $"The reconciled legacy watcher baseline cannot be trusted ({verified.Status}).");
        }
        return verified;
    }

    private static ClipCaptureSource RequireExactSource(AppSettings settings, WatchState state)
    {
        if (!settings.IsValid ||
            !Path.GetFullPath(settings.ClipsFolder).Equals(
                Path.GetFullPath(state.ClipsFolder),
                StringComparison.OrdinalIgnoreCase) ||
            settings.CaptureSource != AppSettings.NormalizeCaptureSource(settings.CaptureSource) ||
            state.CaptureSource != AppSettings.NormalizeCaptureSource(state.CaptureSource) ||
            settings.CaptureSource != state.CaptureSource)
        {
            throw new InvalidDataException(
                "The ignored baseline does not belong to the configured legacy capture source.");
        }
        return settings.CaptureSource;
    }

    private static LegacyIgnoredFileKey ParseFileKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidDataException("A legacy ignored file key is empty.");
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
            throw new InvalidDataException("A legacy ignored file key is malformed.");
        }
        var pathText = key[..lengthSeparator];
        if (!Path.IsPathFullyQualified(pathText))
            throw new InvalidDataException("A legacy ignored file key has no absolute path.");
        var canonical = Path.GetFullPath(pathText);
        if (!pathText.Equals(canonical.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("A legacy ignored file key path is not canonical.");
        }
        return new LegacyIgnoredFileKey(key, canonical, length, ticks);
    }

    private static void RequireLexicalGeometry(
        string root,
        string candidate,
        ClipCaptureSource source)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A legacy ignored file escaped its configured source folder.");
        }
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var valid = source switch
        {
            ClipCaptureSource.SteelSeriesGg => components.Length == 1,
            ClipCaptureSource.Nvidia => components.Length == 2 &&
                                        !AppSettings.ManagedChildFolderNames.Contains(
                                            components[0], StringComparer.OrdinalIgnoreCase),
            _ => false
        };
        if (!valid || components.Any(component => component is "." or "..") ||
            !Path.GetExtension(components[^1]).Equals(
                ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A legacy ignored file does not match its capture-source geometry.");
        }
    }

    private static void EnsureExistingComponentsAreOrdinary(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(current);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The legacy capture root is unavailable.", exception);
        }
        if (!rootAttributes.HasFlag(FileAttributes.Directory) ||
            rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The legacy capture root is unavailable or redirected.");
        }
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    "A legacy ignored file path is unavailable.", exception);
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "A legacy ignored file uses a redirected filesystem path.");
            }
        }
    }

    private static bool TryReadCurrentFileFacts(
        string path,
        out long byteLength,
        out long lastWriteUtcTicks)
    {
        byteLength = 0;
        lastWriteUtcTicks = 0;
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "A legacy ignored occurrence is not an ordinary file.");
            }
            var info = new FileInfo(path);
            byteLength = info.Length;
            lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "A legacy ignored occurrence is unavailable.", exception);
        }
    }

    private static WatchState Clone(WatchState state) => new()
    {
        Version = state.Version,
        ClipsFolder = state.ClipsFolder,
        CaptureSource = state.CaptureSource,
        KnownContentHashes = new HashSet<string>(
            state.KnownContentHashes, StringComparer.OrdinalIgnoreCase),
        UploadedContentHashes = new HashSet<string>(
            state.UploadedContentHashes, StringComparer.OrdinalIgnoreCase),
        LocalOnlyContentHashes = new HashSet<string>(
            state.LocalOnlyContentHashes, StringComparer.OrdinalIgnoreCase),
        IgnoredFileKeys = new HashSet<string>(
            state.IgnoredFileKeys, StringComparer.OrdinalIgnoreCase),
        PendingMoves = new HashSet<string>(
            state.PendingMoves, StringComparer.OrdinalIgnoreCase),
        PendingLocalOnlyMoves = new HashSet<string>(
            state.PendingLocalOnlyMoves, StringComparer.OrdinalIgnoreCase),
        PendingEditedUploads = state.PendingEditedUploads.ToList(),
        KnownSignatures = state.KnownSignatures is null
            ? null
            : new HashSet<string>(state.KnownSignatures, StringComparer.OrdinalIgnoreCase)
    };

    private sealed record LegacyIgnoredFileKey(
        string OriginalKey,
        string CanonicalPath,
        long ByteLength,
        long LastWriteUtcTicks);
}
