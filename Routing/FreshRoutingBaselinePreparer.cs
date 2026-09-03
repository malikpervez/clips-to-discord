using System.Text.Json;

namespace ClipsToDiscord;

/// <summary>
/// The strict watcher-state baseline together with the native identity of the exact source root
/// that was inspected while that baseline was built. Fresh Routing binds both facts into its
/// activation marker so replacing a directory at the same path cannot inherit authority.
/// </summary>
internal sealed record FreshRoutingBaselineResult
{
    internal FreshRoutingBaselineResult(
        WatchStateRoutingProbe probe,
        string watchedRootIdentitySha256)
    {
        Probe = probe ?? throw new ArgumentNullException(nameof(probe));
        RoutingValidation.RequireSha256(
            watchedRootIdentitySha256,
            "fresh watched-source root identity");
        WatchedRootIdentitySha256 = watchedRootIdentitySha256.ToLowerInvariant();
    }

    internal WatchStateRoutingProbe Probe { get; }
    internal string WatchedRootIdentitySha256 { get; }

    // Preserve the probe-shaped surface for callers that need to inspect the durable baseline.
    internal bool Loaded => Probe.Loaded;
    internal WatchState? State => Probe.State;
    internal WatchStateRoutingProbeStatus Status => Probe.Status;
}

/// <summary>
/// Establishes the legacy watch-state exclusion baseline immediately before a fresh-profile
/// Routing activation. The caller owns worker coordination: this type never starts a worker and
/// will operate only after it is explicitly told that the legacy worker is quiesced.
/// </summary>
internal sealed class FreshRoutingBaselinePreparer
{
    private readonly WatchStateStore _watchState;
    private readonly Func<ClipCaptureSource, IRoutingWatchedSourceAdapter> _adapterProvider;

    internal FreshRoutingBaselinePreparer(
        WatchStateStore watchState,
        Func<ClipCaptureSource, IRoutingWatchedSourceAdapter>? adapterProvider = null)
    {
        _watchState = watchState ?? throw new ArgumentNullException(nameof(watchState));
        _adapterProvider = adapterProvider ?? RoutingWatchedSourceAdapters.Get;
    }

    internal async Task<FreshRoutingBaselineResult> PrepareAsync(
        AppSettings settings,
        LegacyRoutingMigrationAdmission admission,
        bool legacyWorkerQuiesced,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (admission != LegacyRoutingMigrationAdmission.FreshOrInvalidProfile)
        {
            throw new InvalidOperationException(
                "A fresh Routing baseline requires explicit fresh-profile admission.");
        }
        if (!legacyWorkerQuiesced)
        {
            throw new InvalidOperationException(
                "The legacy watcher must be quiesced before a fresh Routing baseline is prepared.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var (root, source) = RequireCanonicalSettings(settings);
        var adapter = _adapterProvider(source) ??
                      throw new InvalidDataException(
                          "The fresh capture-source adapter is unavailable.");
        if (adapter.Source != source)
        {
            throw new InvalidDataException(
                "The fresh baseline was passed to the wrong capture-source adapter.");
        }

        var original = _watchState.ProbeForRoutingActivation(cancellationToken);
        var candidate = CreateCandidateState(original, root, source);

        // Root identity and every file fact are collected before the detached state is saved.
        // Re-enumeration plus occurrence inspection closes over additions, removals, replacements,
        // and mutations during hashing. A third enumeration catches changes during inspection.
        var rootIdentity = NormalizeSha256(
            adapter.InspectRootIdentity(root),
            "fresh watched-source root identity");
        var firstPaths = CanonicalCandidates(
            adapter.EnumerateCandidates(root, cancellationToken), root);
        var fingerprints = new List<RoutingWatchedSourceFile>(firstPaths.Length);
        foreach (var path in firstPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = await adapter.OpenAndFingerprintAsync(
                    root,
                    path,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateFingerprint(fingerprint, source, root, path, rootIdentity);
            fingerprints.Add(fingerprint);
        }

        RequireSameCandidateSet(
            firstPaths,
            CanonicalCandidates(adapter.EnumerateCandidates(root, cancellationToken), root));
        for (var index = 0; index < firstPaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occurrence = await adapter.InspectOccurrenceAsync(
                    root,
                    firstPaths[index],
                    cancellationToken)
                .ConfigureAwait(false);
            RequireSameOccurrence(fingerprints[index], occurrence);
        }
        RequireSameCandidateSet(
            firstPaths,
            CanonicalCandidates(adapter.EnumerateCandidates(root, cancellationToken), root));
        var finalRootIdentity = NormalizeSha256(
            adapter.InspectRootIdentity(root),
            "fresh watched-source root identity");
        if (!finalRootIdentity.Equals(rootIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The watched source root changed while its fresh baseline was prepared.");
        }

        foreach (var fingerprint in fingerprints)
            candidate.State.KnownContentHashes.Add(fingerprint.ContentSha256);

        cancellationToken.ThrowIfCancellationRequested();
        RequirePersistableCandidate(candidate.State);
        if (candidate.RequiresReset)
        {
            // A terminal fresh-profile decision is allowed to discard an unrelated or invalid
            // legacy document, but never in place. Move the exact prior bytes out of the active
            // state path first. If the subsequent atomic save fails or the process stops, the
            // legacy queues remain non-executable in quarantine and the next attempt sees a
            // missing state that it can baseline from the watched source again.
            QuarantineResetState(root, source, cancellationToken);
        }
        _watchState.Save(candidate.State);

        // Saving is the single write-through point. Verify the exact strict, drained v4 shape
        // without allowing a late caller cancellation to turn a completed durable write into an
        // indeterminate result.
        var verified = _watchState.ProbeForRoutingActivation(CancellationToken.None);
        RequireStrictDrainedState(verified, root, source);
        if (verified.State is null ||
            !fingerprints.All(item =>
                verified.State.KnownContentHashes.Contains(item.ContentSha256)))
        {
            throw new InvalidDataException(
                "The fresh content-hash baseline could not be verified after it was saved.");
        }
        return new FreshRoutingBaselineResult(verified, rootIdentity);
    }

    private static (string Root, ClipCaptureSource Source) RequireCanonicalSettings(
        AppSettings settings)
    {
        if (!settings.IsValid ||
            settings.CaptureSource != AppSettings.NormalizeCaptureSource(settings.CaptureSource) ||
            !Enum.IsDefined(settings.CaptureSource) ||
            string.IsNullOrWhiteSpace(settings.ClipsFolder) ||
            !Path.IsPathFullyQualified(settings.ClipsFolder))
        {
            throw new InvalidDataException(
                "Fresh Routing baseline settings are invalid or non-canonical.");
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(settings.ClipsFolder));
        if (!settings.ClipsFolder.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The fresh watched-source root must be stored as a canonical absolute path.");
        }
        return (root, settings.CaptureSource);
    }

    private static FreshBaselineCandidate CreateCandidateState(
        WatchStateRoutingProbe probe,
        string root,
        ClipCaptureSource source)
    {
        if (probe.Status == WatchStateRoutingProbeStatus.Missing)
        {
            return new FreshBaselineCandidate(new WatchState
            {
                Version = WatchStateStore.CurrentVersion,
                ClipsFolder = root,
                CaptureSource = source
            }, RequiresReset: false);
        }

        if (probe.Status == WatchStateRoutingProbeStatus.Invalid)
        {
            return new FreshBaselineCandidate(new WatchState
            {
                Version = WatchStateStore.CurrentVersion,
                ClipsFolder = root,
                CaptureSource = source
            }, RequiresReset: true);
        }

        if (probe.Loaded && probe.State is not null &&
            !BelongsToConfiguredSource(probe.State, root, source))
        {
            return new FreshBaselineCandidate(new WatchState
            {
                Version = WatchStateStore.CurrentVersion,
                ClipsFolder = root,
                CaptureSource = source
            }, RequiresReset: true);
        }

        RequireStrictDrainedState(probe, root, source);
        var state = probe.State!;
        return new FreshBaselineCandidate(new WatchState
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
            PendingEditedUploads = state.PendingEditedUploads
                .Select(item => item with { })
                .ToList(),
            KnownSignatures = state.KnownSignatures is null
                ? null
                : new HashSet<string>(state.KnownSignatures, StringComparer.OrdinalIgnoreCase)
        }, RequiresReset: false);
    }

    private void QuarantineResetState(
        string root,
        ClipCaptureSource source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _watchState.ProbeForRoutingActivation(cancellationToken);
        if (current.Status == WatchStateRoutingProbeStatus.Missing) return;
        if (current.Status != WatchStateRoutingProbeStatus.Invalid &&
            !(current.Loaded && current.State is not null &&
              !BelongsToConfiguredSource(current.State, root, source)))
        {
            throw new InvalidDataException(
                $"The watcher state changed before its fresh reset could be quarantined ({current.Status}).");
        }

        var statePath = _watchState.StatePath;
        var attributes = File.GetAttributes(statePath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "A redirected watcher state cannot be quarantined for a fresh reset.");
        }

        var quarantinePath = statePath + ".fresh-rebaseline-" +
                             Guid.NewGuid().ToString("N") + ".quarantine";
        File.Move(statePath, quarantinePath, overwrite: false);
    }

    private static bool BelongsToConfiguredSource(
        WatchState state,
        string root,
        ClipCaptureSource source)
    {
        try
        {
            return state.CaptureSource == source &&
                   Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.ClipsFolder))
                       .Equals(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void RequirePersistableCandidate(WatchState state)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            state,
            new JsonSerializerOptions { WriteIndented = true });
        if (payload.Length is <= 0 or > WatchStateStore.MaximumStateBytes)
            throw new InvalidDataException("The fresh watcher baseline is too large.");
    }

    private static void RequireStrictDrainedState(
        WatchStateRoutingProbe probe,
        string root,
        ClipCaptureSource source)
    {
        if (!probe.Loaded || probe.State is null ||
            probe.State.Version != WatchStateStore.CurrentVersion ||
            probe.PendingMoves != 0 || probe.PendingLocalOnlyMoves != 0 ||
            probe.PendingEditedUploads != 0 || probe.IgnoredFileKeys != 0)
        {
            throw new InvalidDataException(
                $"The fresh watcher baseline is not a strict, drained v4 state ({probe.Status}).");
        }
        var stateRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(probe.State.ClipsFolder));
        if (!stateRoot.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            probe.State.CaptureSource != source)
        {
            throw new InvalidDataException(
                "The fresh watcher baseline belongs to another folder or capture source.");
        }
    }

    private static string[] CanonicalCandidates(
        IReadOnlyList<string> candidates,
        string root)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var canonical = new string[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (string.IsNullOrWhiteSpace(candidate))
                throw new InvalidDataException("A fresh baseline candidate path is empty.");
            canonical[index] = Path.GetFullPath(candidate);
            if (!IsInsideRoot(root, canonical[index]))
            {
                throw new InvalidDataException(
                    "A fresh baseline candidate escaped its watched-source root.");
            }
        }
        Array.Sort(canonical, StringComparer.OrdinalIgnoreCase);
        if (canonical.Distinct(StringComparer.OrdinalIgnoreCase).Count() != canonical.Length)
            throw new InvalidDataException("The fresh baseline candidate set is ambiguous.");
        return canonical;
    }

    private static bool IsInsideRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RequireSameCandidateSet(string[] expected, string[] actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The watched clip candidate set changed while its fresh baseline was prepared.");
        }
    }

    private static void ValidateFingerprint(
        RoutingWatchedSourceFile fingerprint,
        ClipCaptureSource source,
        string root,
        string candidatePath,
        string rootIdentity)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        RoutingWatchedNativeFileIdentityModel.Validate(fingerprint.NativeFileIdentity);
        RequireSha256(fingerprint.ContentSha256, "fresh watched-source content hash");
        RequireSha256(fingerprint.RootIdentitySha256, "fresh watched-source root identity");
        var portableComponents = fingerprint.PortableRelativePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (portableComponents.Length == 0 ||
            fingerprint.PortableRelativePath.Contains('\\') ||
            Path.IsPathRooted(fingerprint.PortableRelativePath) ||
            portableComponents.Any(component => component is "." or "..") ||
            !Path.GetExtension(portableComponents[^1]).Equals(
                ".mp4", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(fingerprint.GameName) ||
            string.IsNullOrWhiteSpace(fingerprint.DisplayFileName))
        {
            throw new InvalidDataException(
                "A fresh baseline fingerprint has invalid portable source facts.");
        }
        var representedPath = Path.GetFullPath(Path.Combine(
            fingerprint.CanonicalRoot,
            fingerprint.PortableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (fingerprint.Source != source ||
            !fingerprint.CanonicalRoot.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            !representedPath.Equals(candidatePath, StringComparison.OrdinalIgnoreCase) ||
            !fingerprint.RootIdentitySha256.Equals(rootIdentity, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A fresh baseline fingerprint does not belong to its configured source.");
        }
    }

    private static void RequireSameOccurrence(
        RoutingWatchedSourceFile fingerprint,
        RoutingWatchedSourceOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (occurrence.Source != fingerprint.Source ||
            !occurrence.CanonicalRoot.Equals(
                fingerprint.CanonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            !occurrence.PortableRelativePath.Equals(
                fingerprint.PortableRelativePath, StringComparison.Ordinal) ||
            !occurrence.GameName.Equals(fingerprint.GameName, StringComparison.Ordinal) ||
            !occurrence.DisplayFileName.Equals(
                fingerprint.DisplayFileName, StringComparison.Ordinal) ||
            !occurrence.RootIdentitySha256.Equals(
                fingerprint.RootIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
            occurrence.NativeFileIdentity != fingerprint.NativeFileIdentity)
        {
            throw new InvalidDataException(
                "A watched clip changed after it was fingerprinted for the fresh baseline.");
        }
    }

    private static void RequireSha256(string value, string field)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException($"The {field} is invalid.");
    }

    private static string NormalizeSha256(string value, string field)
    {
        RequireSha256(value, field);
        return value.ToLowerInvariant();
    }

    private sealed record FreshBaselineCandidate(WatchState State, bool RequiresReset);
}
