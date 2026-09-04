namespace ClipsToDiscord;

/// <summary>
/// One optional supervisor for all additive SteelSeries/NVIDIA connections. A bad or missing
/// recorder folder is isolated to that source; the migrated watcher, Capture, Xbox, and the shared
/// outbox keep running. Catalog changes are observed without rebuilding the global Routing graph.
/// </summary>
internal sealed class RoutingNamedWatchedFolderRuntimeHost : IAsyncDisposable, IDisposable
{
    private sealed class SourceScanState
    {
        internal FileReadinessTracker Readiness { get; } = new();
        internal string? Cursor { get; set; }
    }

    private readonly string _legacyWatchedRoot;
    private readonly string _captureLibraryRoot;
    private readonly ClipProcessingOwnershipLease _ownership;
    private readonly RoutingRuntimeGateInspection _initialPermit;
    private readonly RoutingRuntimeFeatureGate _featureGate;
    private readonly RoutingInputSourceCatalog _sources;
    private readonly RoutingNamedWatchedFolderIngress _ingress;
    private readonly RoutingOutboxExecutor _executor;
    private readonly RoutingWatchedFolderRuntimeHostOptions _options;
    private readonly Dictionary<string, SourceScanState> _states = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private Task? _loop;
    private Exception? _failure;
    private int _disposed;

    internal RoutingNamedWatchedFolderRuntimeHost(
        string legacyWatchedRoot,
        string captureLibraryRoot,
        ClipProcessingOwnershipLease ownership,
        RoutingRuntimeFeatureGate featureGate,
        RoutingInputSourceCatalog sources,
        RoutingNamedWatchedFolderIngress ingress,
        RoutingOutboxExecutor executor,
        RoutingWatchedFolderRuntimeHostOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyWatchedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureLibraryRoot);
        _legacyWatchedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(legacyWatchedRoot));
        _captureLibraryRoot = CaptureJournalStore.NormalizeLibraryRoot(captureLibraryRoot);
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? RoutingWatchedFolderRuntimeHostOptions.Default;
        _options.Validate();
        _initialPermit = _featureGate.Inspect();
        RequireGlobalAuthority();
    }

    internal Exception? Failure => Volatile.Read(ref _failure);
    internal Task Completion => _completion.Task;

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Task loop;
        lock (_sync)
        {
            _loop ??= RunAsync(_shutdown.Token);
            loop = _loop;
        }
        try
        {
            await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _shutdown.Cancel();
            await loop.ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _shutdown.Cancel();
        Task? loop;
        lock (_sync) loop = _loop;
        if (loop is not null) await loop.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            RequireGlobalAuthority();
            _ = await _ingress.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
            RequireGlobalAuthority();
            var recovered = await _executor.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            if (!recovered.Enabled)
                throw new RoutingWatchedFolderRuntimeAuthorityException(
                    "Routing authority was revoked during named-source recovery.");
            _started.TrySetResult();

            while (!cancellationToken.IsCancellationRequested)
            {
                RequireGlobalAuthority();
                await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
                RequireGlobalAuthority();
                var executed = await _executor.RunOnceAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!executed.Enabled)
                    throw new RoutingWatchedFolderRuntimeAuthorityException(
                        "Routing authority was revoked before named-source work could execute.");
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _started.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            _started.TrySetException(exception);
            _completion.TrySetException(exception);
            Log.Error("Named watched-folder Routing runtime stopped fail-closed.", exception);
        }
        finally
        {
            if (!_started.Task.IsCompleted)
            {
                _started.TrySetException(new InvalidOperationException(
                    "The named watched-folder runtime stopped before startup completed."));
            }
            if (Failure is null) _completion.TrySetResult();
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = _sources.Inspect(cancellationToken);
        if (!snapshot.IsUsable)
        {
            Log.Error("Named watched sources could not be read; this pass was skipped.");
            return;
        }
        var active = snapshot.Sources
            .Where(source => !source.Retired && source.Enabled &&
                             source.Health == RoutingInputSourceHealth.Ready &&
                             source.Kind is RoutingInputSourceKind.SteelSeriesGg or
                                 RoutingInputSourceKind.Nvidia)
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .ToArray();
        var conflictedSourceIds = FindConflictedSourceIds(
            _legacyWatchedRoot,
            _captureLibraryRoot,
            active,
            snapshot.Sources);
        var activeIds = active.Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _states.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            _states.Remove(stale);

        foreach (var source in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (conflictedSourceIds.Contains(source.SourceId))
            {
                _states.Remove(source.SourceId);
                await MarkNeedsAttentionBestEffortAsync(
                        source,
                        RoutingInputSourceAttentionReason.MetadataInvalid,
                        cancellationToken)
                    .ConfigureAwait(false);
                Log.Error(
                    $"Named {DescribeKind(source.Kind)} source '{source.DisplayName}' overlaps another watched or ClipCord-owned root; this source was skipped while other sources continue.");
                continue;
            }
            try
            {
                await ScanSourceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or
                    ArgumentException or NotSupportedException or
                    System.Security.SecurityException)
            {
                Log.Error(
                    $"Named {DescribeKind(source.Kind)} source '{source.DisplayName}' needs attention; other sources continue.",
                    exception);
            }
        }
    }

    private async Task ScanSourceAsync(
        RoutingInputSourceRecord source,
        CancellationToken cancellationToken)
    {
        var adapter = RoutingWatchedSourceAdapters.Get(ToCaptureSource(source.Kind));
        string currentIdentity;
        try
        {
            currentIdentity = adapter.InspectRootIdentity(source.CanonicalRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException or
                System.Security.SecurityException)
        {
            await MarkNeedsAttentionBestEffortAsync(
                    source,
                    RoutingInputSourceAttentionReason.MetadataInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        if (!currentIdentity.Equals(source.RootIdentitySha256, StringComparison.Ordinal))
        {
            await MarkNeedsAttentionBestEffortAsync(
                    source,
                    RoutingInputSourceAttentionReason.RootAuthorityChanged,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidDataException("The named watched-source folder was replaced.");
        }
        var state = _states.TryGetValue(source.SourceId, out var existing)
            ? existing
            : _states[source.SourceId] = new SourceScanState();
        state.Readiness.RemoveMissingFiles();
        string[] candidates;
        try
        {
            candidates = adapter.EnumerateCandidates(source.CanonicalRoot, cancellationToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException or
                System.Security.SecurityException)
        {
            await MarkNeedsAttentionBestEffortAsync(
                    source,
                    RoutingInputSourceAttentionReason.MetadataInvalid,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        if (candidates.Length == 0)
        {
            state.Cursor = null;
            return;
        }
        var start = 0;
        if (state.Cursor is not null)
        {
            while (start < candidates.Length &&
                   StringComparer.OrdinalIgnoreCase.Compare(candidates[start], state.Cursor) <= 0)
                start++;
            if (start == candidates.Length) start = 0;
        }
        var budget = Math.Min(_options.MaximumCandidatesPerScan, candidates.Length);
        for (var offset = 0; offset < budget; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = candidates[(start + offset) % candidates.Length];
            state.Cursor = path;
            try
            {
                RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
                    source.CanonicalRoot, path, requireDirectory: false,
                    "named watched runtime candidate");
                if (!state.Readiness.Observe(new FileInfo(path), DateTime.UtcNow).IsReady) continue;
                RequireGlobalAuthority();
                _ = await _ingress.AdmitPathAsync(
                        source, adapter, path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException or
                    ArgumentException or NotSupportedException or
                    System.Security.SecurityException)
            {
                Log.Error(
                    $"Named watched-folder Routing skipped {Path.GetFileName(path)} for this pass.",
                    exception);
            }
        }
    }

    private async Task MarkNeedsAttentionBestEffortAsync(
        RoutingInputSourceRecord source,
        RoutingInputSourceAttentionReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _sources.SetNeedsAttentionAsync(
                    source.SourceId,
                    source.Revision,
                    reason,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                Log.Error(
                    $"Named {DescribeKind(source.Kind)} source '{source.DisplayName}' could not persist its needs-attention state ({result.Status}); this scan still remains fail-closed.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException or
                System.Security.SecurityException)
        {
            Log.Error(
                $"Named {DescribeKind(source.Kind)} source '{source.DisplayName}' could not persist its needs-attention state; this scan still remains fail-closed.",
                exception);
        }
    }

    private void RequireGlobalAuthority()
    {
        var current = _featureGate.Inspect();
        if (!_ownership.IsCurrent || _ownership.Owner != ClipProcessingRuntimeOwner.Routing ||
            !_initialPermit.SameAuthority(current) || !current.Enabled ||
            current.ExecutionAuthorityActivationId is null ||
            current.OwnershipEpoch != _ownership.Epoch ||
            current.RoutingGeneration is null or <= 0 ||
            current.MarkerPayloadFingerprint is not { Length: 64 })
        {
            throw new RoutingWatchedFolderRuntimeAuthorityException(
                "The named watched-folder runtime lost global Routing authority.");
        }
    }

    internal static IReadOnlySet<string> FindConflictedSourceIds(
        string legacyWatchedRoot,
        string captureLibraryRoot,
        IReadOnlyList<RoutingInputSourceRecord> sources,
        IReadOnlyList<RoutingInputSourceRecord> allSources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyWatchedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureLibraryRoot);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(allSources);
        var protectedRoots = new List<string>
        {
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyWatchedRoot)),
            CaptureJournalStore.NormalizeLibraryRoot(captureLibraryRoot)
        };
        protectedRoots.AddRange(allSources
            .Where(source => !source.Retired && source.Enabled &&
                             source.Kind == RoutingInputSourceKind.XboxGameDvrOneDrive)
            .Select(source => Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(source.CanonicalRoot))));

        var roots = sources.Select(source => (
                source.SourceId,
                Root: Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(source.CanonicalRoot))))
            .ToArray();
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in roots)
        {
            if (protectedRoots.Any(existing =>
                    CapturePathPolicy.PathsOverlap(existing, source.Root)))
            {
                conflicts.Add(source.SourceId);
            }
        }
        for (var left = 0; left < roots.Length; left++)
        {
            for (var right = left + 1; right < roots.Length; right++)
            {
                if (!CapturePathPolicy.PathsOverlap(roots[left].Root, roots[right].Root)) continue;
                conflicts.Add(roots[left].SourceId);
                conflicts.Add(roots[right].SourceId);
            }
        }
        return conflicts;
    }

    internal static ClipCaptureSource ToCaptureSource(RoutingInputSourceKind kind) => kind switch
    {
        RoutingInputSourceKind.SteelSeriesGg => ClipCaptureSource.SteelSeriesGg,
        RoutingInputSourceKind.Nvidia => ClipCaptureSource.Nvidia,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string DescribeKind(RoutingInputSourceKind kind) => kind switch
    {
        RoutingInputSourceKind.SteelSeriesGg => "SteelSeries GG",
        RoutingInputSourceKind.Nvidia => "NVIDIA",
        _ => "external"
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAsync().AsTask().GetAwaiter().GetResult();
        _shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
