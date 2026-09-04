using System.Diagnostics;
using System.Threading.Channels;

namespace ClipsToDiscord;

internal sealed class SilhouetteProjectSettledEventArgs : EventArgs
{
    internal SilhouetteProjectSettledEventArgs(string libraryRoot, string projectId, int exitCode)
    {
        LibraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        ProjectId = projectId;
        ExitCode = exitCode;
    }

    internal string LibraryRoot { get; }
    internal string ProjectId { get; }
    internal int ExitCode { get; }
}

/// <summary>
/// Bridges committed capture projects to the isolated silhouette worker. The committed
/// project directory is the durable queue; the in-memory channel is only a low-latency,
/// single-reader accelerator and is rebuilt by bounded reconciliation after restart.
/// </summary>
internal sealed class SilhouetteProcessingCoordinator : IDisposable
{
    private const int QueueCapacity = 64;
    private const int MaximumDirectoriesPerReconciliation = 128;
    private static readonly TimeSpan MaximumReconciliationTime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<SilhouetteProjectWorkItem> _queue;
    // Observed projects are suppressed for this process lifetime so the periodic durable-queue
    // scan does not relaunch a completed or failed worker every twenty seconds. Queued projects
    // are tracked separately: an explicit user retry may bypass observed suppression, but may
    // never race a worker that is already queued or active for the same committed project.
    private readonly HashSet<string> _observedProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedOrActiveProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueGate = new();
    private readonly object _scanGate = new();
    private readonly object _processGate = new();
    private readonly object _retryGate = new();
    private readonly HashSet<Task> _activeRetries = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _reconciliationRequested = new(0, 1);
    private readonly Task _processingLoop;
    private readonly Task _reconciliationLoop;
    private string _libraryRoot;
    private string? _enumeratedProjectsRoot;
    private IEnumerator<DirectoryInfo>? _projectEnumerator;
    private Process? _activeWorker;
    private int _disposeStarted;
    private int _cleanupStarted;

    internal event EventHandler<SilhouetteProjectSettledEventArgs>? ProjectSettled;

    internal bool IsIdle
    {
        get
        {
            if (Volatile.Read(ref _disposeStarted) != 0) return true;
            lock (_queueGate)
            {
                if (_queuedOrActiveProjects.Count != 0) return false;
            }
            lock (_retryGate) return _activeRetries.Count == 0;
        }
    }

    internal SilhouetteProcessingCoordinator(string libraryRoot)
    {
        _libraryRoot = NormalizeLibraryRoot(libraryRoot);
        _queue = Channel.CreateBounded<SilhouetteProjectWorkItem>(new BoundedChannelOptions(
            QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _processingLoop = Task.Run(ProcessQueueAsync);
        _reconciliationLoop = Task.Run(ReconciliationLoopAsync);
    }

    /// <summary>
    /// Enqueues only a fully promoted, ordinary capture project. Cryptographic source and
    /// composition validation remains the worker's responsibility immediately before use.
    /// </summary>
    internal bool TryEnqueue(string libraryRoot, string projectId)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 ||
            !SilhouetteWorkerLaunchOptions.IsProjectId(projectId))
        {
            return false;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = NormalizeLibraryRoot(libraryRoot);
            if (!IsCommittedProjectDirectory(normalizedRoot, projectId)) return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }

        return TryEnqueueValidated(
            new SilhouetteProjectWorkItem(normalizedRoot, projectId),
            bypassObservedDedupe: false);
    }

    /// <summary>
    /// Retries only durable failed work for one enabled orientation. A Ready output first has
    /// to fail fixed-path fingerprint validation; it is then invalidated and retried as two
    /// separately persisted compare-and-swap transitions. No transition is speculatively kept
    /// in memory, and a project already queued or active cannot be mutated by this path.
    /// </summary>
    internal Task<bool> RetryAsync(
        string libraryRoot,
        string projectId,
        string orientationId) =>
        RetryAsync(libraryRoot, projectId, orientationId, CancellationToken.None);

    internal Task<bool> RetryAsync(
        string libraryRoot,
        string projectId,
        string orientationId,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_retryGate)
        {
            if (Volatile.Read(ref _disposeStarted) != 0) return Task.FromResult(false);
            _activeRetries.Add(completion.Task);
        }
        return RunTrackedRetryAsync(
            libraryRoot,
            projectId,
            orientationId,
            cancellationToken,
            completion);
    }

    private async Task<bool> RunTrackedRetryAsync(
        string libraryRoot,
        string projectId,
        string orientationId,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            return await RetryCoreAsync(
                    libraryRoot,
                    projectId,
                    orientationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult(true);
            lock (_retryGate) _activeRetries.Remove(completion.Task);
        }
    }

    private async Task<bool> RetryCoreAsync(
        string libraryRoot,
        string projectId,
        string orientationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposeStarted) != 0 ||
            !SilhouetteWorkerLaunchOptions.IsProjectId(projectId) ||
            !CompositionOrientationIds.All.Contains(orientationId, StringComparer.Ordinal))
        {
            return false;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = NormalizeLibraryRoot(libraryRoot);
            if (!IsCommittedProjectDirectory(normalizedRoot, projectId)) return false;
        }
        catch (Exception exception) when (IsExpectedProjectAccessException(exception))
        {
            return false;
        }

        var workItem = new SilhouetteProjectWorkItem(normalizedRoot, projectId);
        var identity = CreateIdentity(workItem);
        if (!TryReserveRetry(identity)) return false;

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var operationToken = operationCancellation.Token;
        var transitionPersisted = false;
        var queuedSuccessfully = false;
        try
        {
            var load = SilhouetteRenditionStore.Load(
                normalizedRoot,
                projectId,
                operationToken);
            if (!load.LoadedFromDisk || load.Document is null) return false;

            var current = load.Document;
            var output = current.Outputs.Single(candidate =>
                candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
            if (output.State == SilhouetteOutputState.Disabled) return false;

            if (current.Matte.State == SilhouetteMatteState.Failed)
            {
                var retryMatte = SilhouetteRenditionModel.RetryMatte(
                    current,
                    DateTimeOffset.UtcNow);
                current = await SilhouetteRenditionStore.SaveAsync(
                        normalizedRoot,
                        projectId,
                        retryMatte,
                        current.Generation,
                        operationToken)
                    .ConfigureAwait(false);
                transitionPersisted = true;
            }

            output = current.Outputs.Single(candidate =>
                candidate.OrientationId.Equals(orientationId, StringComparison.Ordinal));
            if (output.State == SilhouetteOutputState.Failed)
            {
                var retryOutput = SilhouetteRenditionModel.RetryOutput(
                    current,
                    orientationId,
                    DateTimeOffset.UtcNow);
                current = await SilhouetteRenditionStore.SaveAsync(
                        normalizedRoot,
                        projectId,
                        retryOutput,
                        current.Generation,
                        operationToken)
                    .ConfigureAwait(false);
                transitionPersisted = true;
            }
            else if (output.State == SilhouetteOutputState.Ready &&
                     output.FinalArtifact is not null)
            {
                var validation = await SilhouetteArtifactStore.ValidateOutputAsync(
                        normalizedRoot,
                        projectId,
                        orientationId,
                        output.FinalArtifact,
                        operationToken)
                    .ConfigureAwait(false);
                if (validation.IsDefinitiveFailure)
                {
                    var invalidated = SilhouetteRenditionModel.InvalidateReadyOutput(
                        current,
                        orientationId,
                        validation.ObservedFingerprint,
                        DateTimeOffset.UtcNow);
                    current = await SilhouetteRenditionStore.SaveAsync(
                            normalizedRoot,
                            projectId,
                            invalidated,
                            current.Generation,
                            operationToken)
                        .ConfigureAwait(false);
                    transitionPersisted = true;

                    var retryOutput = SilhouetteRenditionModel.RetryOutput(
                        current,
                        orientationId,
                        DateTimeOffset.UtcNow);
                    current = await SilhouetteRenditionStore.SaveAsync(
                            normalizedRoot,
                            projectId,
                            retryOutput,
                            current.Generation,
                            operationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!transitionPersisted) return false;
            if (TryWriteReserved(workItem))
            {
                queuedSuccessfully = true;
                return true;
            }

            // Durable Pending state is still the source of truth. Make it visible to bounded
            // reconciliation rather than spinning or blocking the Gallery behind a full queue.
            ReleaseRetryReservation(identity, allowReconciliation: true);
            RequestReconciliation();
            return false;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            if (transitionPersisted)
            {
                ReleaseRetryReservation(identity, allowReconciliation: true);
                RequestReconciliation();
            }
            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
        catch (SilhouetteRenditionConcurrencyException)
        {
            // Another process advanced the CAS generation. Do not hot-loop or overwrite it.
            return false;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or PathTooLongException or
                System.Security.SecurityException)
        {
            Log.Error(
                $"ClipCord could not schedule the {orientationId} silhouette retry for project {projectId}.",
                exception);
            return false;
        }
        finally
        {
            // A successfully queued item retains this reservation until its worker exits.
            // All other paths release it; when a transition was persisted, also remove the
            // lifetime observation so reconciliation can recover the durable Pending state.
            if (!queuedSuccessfully)
            {
                ReleaseRetryReservation(identity, transitionPersisted);
            }
        }
    }

    internal void UpdateLibraryRoot(string libraryRoot)
    {
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        if (Volatile.Read(ref _disposeStarted) != 0) return;
        lock (_scanGate)
        {
            if (_libraryRoot.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return;
            ResetProjectEnumeratorCore();
            _libraryRoot = normalizedRoot;
        }
        RequestReconciliation();
    }

    private async Task ProcessQueueAsync()
    {
        var cancellationToken = _shutdown.Token;
        try
        {
            await foreach (var workItem in _queue.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                try
                {
                    await RunWorkerAsync(workItem, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Log.Error(
                        $"The isolated silhouette worker could not process project {workItem.ProjectId}.",
                        exception);
                    // A launch failure is just as terminal for this attempt as a non-zero worker
                    // exit. ProjectSettled lets the journal record a stable failure instead of
                    // leaving routing work in CameraPending forever.
                    RaiseProjectSettled(workItem, exitCode: -1);
                }
                finally
                {
                    ReleaseCompletedWorkItem(workItem);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown. The project and rendition state remain durable.
        }
    }

    private async Task RunWorkerAsync(
        SilhouetteProjectWorkItem workItem,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = SilhouetteWorkerLaunch.CreateStartInfo(
            workItem.LibraryRoot,
            workItem.ProjectId);
        var process = new Process { StartInfo = startInfo };
        try
        {
            lock (_processGate)
            {
                if (_disposeStarted != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ObjectDisposedException(nameof(SilhouetteProcessingCoordinator));
                }
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "Windows did not start the isolated silhouette worker.");
                }
                _activeWorker = process;
            }
        }
        catch
        {
            if (!HasExited(process))
            {
                // Process.Start and publication share _processGate, so this is reachable only
                // for a failed/partial launch that never became an admitted worker.
                _ = TryTerminateAndJoin(process);
            }
            if (HasExited(process)) process.Dispose();
            throw;
        }
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            if (!TryTerminateAndJoin(process))
            {
                throw new InvalidOperationException(
                    "The isolated silhouette worker did not exit during disposal.");
            }
            lock (_processGate)
            {
                if (ReferenceEquals(_activeWorker, process)) _activeWorker = null;
            }
            process.Dispose();
            throw new ObjectDisposedException(nameof(SilhouetteProcessingCoordinator));
        }
        TryApplyLowPriority(process);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                Log.Error(
                    $"The isolated silhouette worker exited with code {process.ExitCode} for project {workItem.ProjectId}.");
            }
            RaiseProjectSettled(workItem, process.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!TryTerminateAndJoin(process))
            {
                throw new InvalidOperationException(
                    "The isolated silhouette worker did not exit after cancellation.");
            }
            throw;
        }
        finally
        {
            if (HasExited(process))
            {
                lock (_processGate)
                {
                    if (ReferenceEquals(_activeWorker, process)) _activeWorker = null;
                }
                process.Dispose();
            }
        }
    }

    private async Task ReconciliationLoopAsync()
    {
        var cancellationToken = _shutdown.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReconcileNextBatch();
                await _reconciliationRequested.WaitAsync(
                    ReconciliationInterval,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private void ReconcileNextBatch()
    {
        var discovered = new List<SilhouetteProjectWorkItem>();
        lock (_scanGate)
        {
            if (_disposeStarted != 0) return;
            try
            {
                EnsureProjectEnumeratorCore();
                if (_projectEnumerator is null || _enumeratedProjectsRoot is null) return;

                var stopwatch = Stopwatch.StartNew();
                for (var inspected = 0;
                     inspected < MaximumDirectoriesPerReconciliation &&
                     stopwatch.Elapsed < MaximumReconciliationTime;
                     inspected++)
                {
                    if (!_projectEnumerator.MoveNext())
                    {
                        ResetProjectEnumeratorCore();
                        break;
                    }

                    var candidate = _projectEnumerator.Current;
                    if (IsCommittedProjectDirectory(candidate, _enumeratedProjectsRoot))
                    {
                        discovered.Add(new SilhouetteProjectWorkItem(
                            _libraryRoot,
                            candidate.Name));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or
                    NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                ResetProjectEnumeratorCore();
                Log.Error("ClipCord could not reconcile committed silhouette projects.", exception);
            }
        }

        foreach (var project in discovered)
        {
            _ = TryEnqueue(project.LibraryRoot, project.ProjectId);
        }
    }

    private void EnsureProjectEnumeratorCore()
    {
        if (_projectEnumerator is not null) return;
        var root = new DirectoryInfo(_libraryRoot);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

        var projectsRoot = new DirectoryInfo(
            Path.GetFullPath(CaptureLibraryLayout.GetProjectsDirectory(root.FullName)));
        if (!projectsRoot.Exists ||
            projectsRoot.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !IsOrdinaryDirectoryPath(root.FullName, projectsRoot.FullName))
        {
            return;
        }

        _enumeratedProjectsRoot = projectsRoot.FullName;
        _projectEnumerator = projectsRoot
            .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
            .GetEnumerator();
    }

    private static bool IsCommittedProjectDirectory(string libraryRoot, string projectId)
    {
        var root = new DirectoryInfo(libraryRoot);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var projectsRoot = new DirectoryInfo(
            Path.GetFullPath(CaptureLibraryLayout.GetProjectsDirectory(root.FullName)));
        if (!projectsRoot.Exists ||
            projectsRoot.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !IsOrdinaryDirectoryPath(root.FullName, projectsRoot.FullName))
        {
            return false;
        }

        var candidate = new DirectoryInfo(Path.Combine(projectsRoot.FullName, projectId));
        return IsCommittedProjectDirectory(candidate, projectsRoot.FullName);
    }

    private static bool IsCommittedProjectDirectory(
        DirectoryInfo candidate,
        string projectsRoot)
    {
        if (!SilhouetteWorkerLaunchOptions.IsProjectId(candidate.Name) ||
            !candidate.Exists ||
            candidate.Parent is null ||
            !candidate.Parent.FullName.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase) ||
            candidate.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        return IsOrdinaryNonEmptyFile(
                   Path.Combine(candidate.FullName, CaptureProjectStore.ManifestFileName)) &&
               IsOrdinaryNonEmptyFile(
                   Path.Combine(candidate.FullName, CaptureProjectStore.CameraLayerFileName)) &&
               IsOrdinaryNonEmptyFile(
                   Path.Combine(candidate.FullName, CaptureCompositionStore.FileName));
    }

    private static bool IsOrdinaryNonEmptyFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0 &&
               !file.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsSameOrInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsOrdinaryDirectoryPath(string root, string candidate)
    {
        if (!IsSameOrInside(root, candidate)) return false;
        var current = new DirectoryInfo(root);
        if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var relative = Path.GetRelativePath(root, candidate);
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, component));
            if (!current.Exists || current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private static string NormalizeLibraryRoot(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot.Trim()));
    }

    private static string CreateIdentity(SilhouetteProjectWorkItem workItem) =>
        workItem.LibraryRoot + '\0' + workItem.ProjectId;

    private bool TryEnqueueValidated(
        SilhouetteProjectWorkItem workItem,
        bool bypassObservedDedupe)
    {
        var identity = CreateIdentity(workItem);
        lock (_queueGate)
        {
            if (_disposeStarted != 0 ||
                _queuedOrActiveProjects.Contains(identity) ||
                (!bypassObservedDedupe && _observedProjects.Contains(identity)))
            {
                return false;
            }

            _queuedOrActiveProjects.Add(identity);
            _observedProjects.Add(identity);
            if (_queue.Writer.TryWrite(workItem)) return true;
            _queuedOrActiveProjects.Remove(identity);
            _observedProjects.Remove(identity);
            return false;
        }
    }

    private bool TryReserveRetry(string identity)
    {
        lock (_queueGate)
        {
            if (_disposeStarted != 0 || !_queuedOrActiveProjects.Add(identity)) return false;
            return true;
        }
    }

    private bool TryWriteReserved(SilhouetteProjectWorkItem workItem)
    {
        var identity = CreateIdentity(workItem);
        lock (_queueGate)
        {
            if (_disposeStarted != 0 || !_queuedOrActiveProjects.Contains(identity)) return false;
            _observedProjects.Add(identity);
            if (_queue.Writer.TryWrite(workItem)) return true;
            _observedProjects.Remove(identity);
            return false;
        }
    }

    private void ReleaseRetryReservation(string identity, bool allowReconciliation)
    {
        lock (_queueGate)
        {
            _queuedOrActiveProjects.Remove(identity);
            if (allowReconciliation) _observedProjects.Remove(identity);
        }
    }

    private void ReleaseCompletedWorkItem(SilhouetteProjectWorkItem workItem)
    {
        lock (_queueGate)
        {
            _queuedOrActiveProjects.Remove(CreateIdentity(workItem));
        }
    }

    private static bool IsExpectedProjectAccessException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException or
            System.Security.SecurityException;

    private void RequestReconciliation()
    {
        try
        {
            _reconciliationRequested.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending request already guarantees a prompt scan of the new root.
        }
        catch (ObjectDisposedException)
        {
            // Disposal won the race with a settings update.
        }
    }

    private void ResetProjectEnumeratorCore()
    {
        _projectEnumerator?.Dispose();
        _projectEnumerator = null;
        _enumeratedProjectsRoot = null;
    }

    private static void TryApplyLowPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            process.PriorityBoostEnabled = false;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
            // Priority is a performance safeguard, not a reason to discard durable work.
        }
    }

    private static bool TryTerminateAndJoin(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return process.WaitForExit((int)DisposeWaitTimeout.TotalMilliseconds) ||
                HasExited(process);
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The worker may have exited between the state check and termination request.
            return HasExited(process);
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (ObjectDisposedException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private bool TryTerminateActiveWorkerAndJoin()
    {
        Process? process;
        lock (_processGate) process = _activeWorker;
        return process is null || TryTerminateAndJoin(process);
    }

    private void RaiseProjectSettled(SilhouetteProjectWorkItem workItem, int exitCode)
    {
        var handlers = ProjectSettled;
        if (handlers is null) return;
        var eventArgs = new SilhouetteProjectSettledEventArgs(
            workItem.LibraryRoot,
            workItem.ProjectId,
            exitCode);
        foreach (EventHandler<SilhouetteProjectSettledEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                // Worker state and output fingerprints are already durable. Subscribers are
                // latency accelerators only; startup reconciliation remains authoritative.
                Log.Error("ClipCord could not notify a settled silhouette subscriber.", exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_retryGate)
        {
            lock (_processGate)
            {
                // Disposal/revocation linearizes against both retry admission and Process.Start.
                // A worker is therefore either published before this boundary or never launched.
                if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            }
        }
        ProjectSettled = null;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        RequestReconciliation();
        _ = TryTerminateActiveWorkerAndJoin();

        var completion = CreateStopCompletion();
        var completed = false;
        try
        {
            completed = completion.Wait(DisposeWaitTimeout);
        }
        catch (AggregateException)
        {
            completed = true;
        }

        if (completed)
        {
            Cleanup(completion);
            return;
        }

        _ = TryTerminateActiveWorkerAndJoin();
        Log.Error("The silhouette coordinator did not stop promptly; cleanup will continue in the background.");
        _ = completion.ContinueWith(
            Cleanup,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal void DisposeAndRequireStopped()
    {
        Dispose();
        var workerStopped = TryTerminateActiveWorkerAndJoin();
        var completion = CreateStopCompletion();
        var loopsStopped = completion.IsCompleted;
        if (!loopsStopped)
        {
            try { loopsStopped = completion.Wait(DisposeWaitTimeout); }
            catch (AggregateException) { loopsStopped = true; }
        }
        workerStopped = TryTerminateActiveWorkerAndJoin();
        if (!loopsStopped || !workerStopped)
        {
            throw new InvalidOperationException(
                "The isolated silhouette worker did not stop before Capture-library authority changed.");
        }
        Cleanup(completion);
    }

    private Task CreateStopCompletion()
    {
        Task[] retries;
        lock (_retryGate) retries = _activeRetries.ToArray();
        return Task.WhenAll(_processingLoop, _reconciliationLoop, Task.WhenAll(retries));
    }

    private void Cleanup(Task completion)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0) return;
        try
        {
            completion.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            Log.Error("The silhouette coordinator stopped with an error.", exception);
        }
        finally
        {
            lock (_scanGate) ResetProjectEnumeratorCore();
            _reconciliationRequested.Dispose();
            // RetryAsync links its caller token to this source and can still be unwinding after
            // the worker/reconciliation loops have stopped. Leave the source for GC rather than
            // disposing it underneath a concurrent token registration.
        }
    }

    private sealed record SilhouetteProjectWorkItem(
        string LibraryRoot,
        string ProjectId);
}
