using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClipsToDiscord;

internal sealed class SingleFlightGate
{
    private int _active;

    internal bool TryEnter() => Interlocked.Exchange(ref _active, 1) == 0;
    internal void Exit() => Volatile.Write(ref _active, 0);
}

/// <summary>
/// Joins the one-time authority bootstrap before admitting serialized operations that can
/// change a path or archive the same files as the active processing runtime. The synchronous
/// entry point never waits for another mutation on the UI thread; it fails clearly instead.
/// </summary>
internal sealed class TrayProcessingOperationGate
{
    private readonly object _startupSync = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private Task _startup = Task.CompletedTask;
    private bool _startupRegistered;

    internal void SetStartup(Task startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        lock (_startupSync)
        {
            if (_startupRegistered)
            {
                throw new InvalidOperationException(
                    "The clip-processing startup task is already registered.");
            }
            _startup = startup;
            _startupRegistered = true;
        }
    }

    internal async ValueTask<IDisposable> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        await StartupTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_mutation);
    }

    internal IDisposable EnterSynchronously(
        CancellationToken cancellationToken = default)
    {
        StartupTask().WaitAsync(cancellationToken).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_mutation.Wait(0))
        {
            throw new InvalidOperationException(
                "ClipCord is already applying another clip-processing change.");
        }
        return new Releaser(_mutation);
    }

    private Task StartupTask()
    {
        lock (_startupSync) return _startup;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

/// <summary>
/// Runs a user-requested operation while Routing is quiesced. Cleanup/restart is convergence:
/// once quiescing succeeds it runs even after cancellation, and its failure never replaces the
/// operation's authoritative result (notably a Discord-confirmed manual upload).
/// </summary>
internal static class TrayRoutingExclusiveOperation
{
    internal static async Task<T> RunAsync<T>(
        RoutingExecutionAuthorityInspection authority,
        Func<CancellationToken, Task> quiesceRouting,
        Func<CancellationToken, Task<T>> operation,
        Func<Task> recoverAndRestartRouting,
        Action<Exception> reportRestartFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(quiesceRouting);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(recoverAndRestartRouting);
        ArgumentNullException.ThrowIfNull(reportRestartFailure);

        RequireManualUploadAllowed(authority);

        if (authority.LegacyPermitted)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        await quiesceRouting(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await recoverAndRestartRouting().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                try { reportRestartFailure(exception); }
                catch { }
            }
        }
    }

    internal static void RequireManualUploadAllowed(
        RoutingExecutionAuthorityInspection authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.Blocked) return;
        throw new InvalidDataException(
            $"Routing execution authority cannot be trusted ({authority.LoadStatus}). " +
            "Manual uploads are paused until Routes authority is repaired.");
    }
}

/// <summary>
/// Resolves the destination for the explicit Gallery "Edit &amp; upload" escape. Once Routing
/// owns delivery, the legacy webhook in AppSettings is no longer authoritative; select the first
/// ready catalog connection in its durable order instead. Local-only mode deliberately does not
/// participate here because this operation is an explicit user-requested send.
/// </summary>
internal static class TrayManualDiscordConnection
{
    internal static DiscordRoutingConnection Resolve(
        RoutingExecutionAuthorityInspection authority,
        AppSettings settings,
        DiscordConnectionCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        TrayRoutingExclusiveOperation.RequireManualUploadAllowed(authority);

        if (authority.LegacyPermitted)
        {
            if (!WebhookValidation.IsDiscordWebhook(settings.WebhookUrl))
            {
                throw new InvalidOperationException(
                    "Add a valid Discord webhook in Settings before uploading a Local-only clip.");
            }
            return new DiscordRoutingConnection(settings.WebhookUrl.Trim(), settings);
        }

        var snapshot = catalog.Inspect(cancellationToken);
        if (snapshot.IsUsable)
        {
            foreach (var summary in snapshot.Connections.Where(connection =>
                         connection.Health == DiscordConnectionHealth.Ready))
            {
                var resolved = catalog.ResolveForRouting(
                    summary.ConnectionId, settings, cancellationToken);
                if (resolved is
                    {
                        Status: DiscordRoutingConnectionResolutionStatus.Resolved,
                        Connection: not null
                    })
                {
                    SensitiveDataRedactor.RegisterSecret(resolved.Connection.WebhookUrl);
                    return resolved.Connection;
                }
            }
        }

        throw new InvalidOperationException(
            "Connect a ready Discord destination in Routes before uploading a Local-only clip.");
    }
}

/// <summary>
/// Extends the durable authority boundary across the short committed-migration recovery window.
/// The execution-authority file is still missing there, but Lifecycle already holds the Routing
/// fence, so legacy-only mutations must remain blocked until recovery commits exact authority.
/// </summary>
internal static class TrayRoutingOperationalAuthority
{
    internal static RoutingExecutionAuthorityInspection Evaluate(
        RoutingExecutionAuthorityInspection persisted,
        bool legacyOperationsPermitted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (!persisted.LegacyPermitted || legacyOperationsPermitted) return persisted;
        return new RoutingExecutionAuthorityInspection(
            RoutingExecutionAuthorityInspectionState.Blocked,
            RoutingDocumentLoadStatus.Unavailable,
            null);
    }
}

internal sealed record TrayRoutingStartupSequenceResult<T>(
    T Result,
    bool ActivationAttempted,
    Exception? RecoveryError = null);

internal static class TrayRoutingStartupSequence
{
    internal static async Task<TrayRoutingStartupSequenceResult<T>> RunAsync<T>(
        bool routingRequired,
        Func<CancellationToken, Task> recover,
        Func<CancellationToken, Task<T>> startSelected,
        Func<T, bool> legacyStarted,
        Func<CancellationToken, Task<T>> activateRouting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recover);
        ArgumentNullException.ThrowIfNull(startSelected);
        ArgumentNullException.ThrowIfNull(legacyStarted);
        ArgumentNullException.ThrowIfNull(activateRouting);

        if (routingRequired)
        {
            await recover(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var routing = await startSelected(cancellationToken).ConfigureAwait(false);
            return new(routing, ActivationAttempted: false);
        }

        var legacy = await startSelected(cancellationToken).ConfigureAwait(false);
        if (!legacyStarted(legacy))
        {
            return new(legacy, ActivationAttempted: false);
        }
        try
        {
            await recover(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(legacy, ActivationAttempted: false, exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var activated = await activateRouting(cancellationToken).ConfigureAwait(false);
        return new(activated, ActivationAttempted: true);
    }
}

internal static class TrayRoutingRetrySequence
{
    internal static async Task<TrayRoutingStartupSequenceResult<T>> RunAsync<T>(
        Func<CancellationToken, Task> recover,
        Func<bool> legacyStartRequired,
        Func<CancellationToken, Task<T>> startLegacy,
        Func<T, bool> legacyStarted,
        Func<CancellationToken, Task<T>> activateRouting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recover);
        ArgumentNullException.ThrowIfNull(legacyStartRequired);
        ArgumentNullException.ThrowIfNull(startLegacy);
        ArgumentNullException.ThrowIfNull(legacyStarted);
        ArgumentNullException.ThrowIfNull(activateRouting);

        await recover(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (legacyStartRequired())
        {
            var legacy = await startLegacy(cancellationToken).ConfigureAwait(false);
            if (!legacyStarted(legacy))
            {
                return new(legacy, ActivationAttempted: false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var activated = await activateRouting(cancellationToken).ConfigureAwait(false);
        return new(activated, ActivationAttempted: true);
    }
}

internal static class TrayRoutesRuntimePresentation
{
    internal static RoutesRuntimeViewState Map(
        bool startupRunning,
        RoutingApplicationLifecycleState lifecycleState)
    {
        if (startupRunning) return RoutesRuntimeViewState.Activating;
        return lifecycleState switch
        {
            RoutingApplicationLifecycleState.RoutingRunning => RoutesRuntimeViewState.Active,
            RoutingApplicationLifecycleState.LegacyRunning => RoutesRuntimeViewState.LegacyActive,
            RoutingApplicationLifecycleState.LegacyReady =>
                RoutesRuntimeViewState.LegacySetupNeeded,
            RoutingApplicationLifecycleState.RoutingReady or
                RoutingApplicationLifecycleState.RoutingQuiesced or
                RoutingApplicationLifecycleState.RoutingRecoveryNeeded =>
                RoutesRuntimeViewState.RecoveryNeeded,
            _ => RoutesRuntimeViewState.Blocked
        };
    }

    internal static bool IsActivationStatus(string? status) =>
        status?.StartsWith("Routes activating", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Runs every shutdown fallback even when an earlier fallback or its diagnostic reporter fails.
/// Shared processing resources are disposed only after this convergence sequence returns.
/// </summary>
internal static class TrayShutdownFallbacks
{
    internal static void Run(
        Action<string, Exception> reportError,
        params (string ErrorMessage, Action Step)[] steps)
    {
        ArgumentNullException.ThrowIfNull(reportError);
        ArgumentNullException.ThrowIfNull(steps);

        foreach (var (errorMessage, step) in steps)
        {
            try
            {
                step();
            }
            catch (Exception exception)
            {
                try { reportError(errorMessage, exception); }
                catch { }
            }
        }
    }
}

/// <summary>
/// Capture LibraryRoot is part of Routing's durable source contract. Until a durable switch
/// transaction exists, only Legacy authority may change it. This guard is deliberately pure and
/// must run before hotkeys, settings, runtime state, or files are changed.
/// </summary>
internal static class TrayCaptureLibraryRootAuthorityGuard
{
    internal static bool RequireAllowed(
        string currentLibraryRoot,
        string requestedLibraryRoot,
        RoutingExecutionAuthorityInspection authority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLibraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLibraryRoot);
        ArgumentNullException.ThrowIfNull(authority);

        var changed = !SameCanonicalPath(currentLibraryRoot, requestedLibraryRoot);
        if (!changed || authority.LegacyPermitted) return changed;
        if (authority.Blocked)
        {
            throw new InvalidDataException(
                $"Routing execution authority cannot be trusted ({authority.LoadStatus}). " +
                "The Capture library was not changed.");
        }
        if (authority.RoutingRequired)
        {
            throw new InvalidOperationException(
                "Active Routes own the Capture library. Change it through a future durable " +
                "Routing source switch; the Capture library was not changed.");
        }
        throw new InvalidDataException(
            "Routing execution authority has an unsupported state. The Capture library was not changed.");
    }

    internal static bool SameCanonicalPath(string left, string right)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// A Capture library switch is a process-wide authority handoff. No pipeline may retain the old
/// root when that handoff begins, otherwise a later Stop or Save could commit across authority.
/// </summary>
internal static class TrayCaptureLibrarySwitchPolicy
{
    internal static void RequireIdle(
        ManualCaptureState manualState,
        ReplayCaptureState replayState,
        ReactionCameraRuntimeStatus cameraStatus,
        bool silhouetteProcessingIdle)
    {
        var manualIdle = manualState is ManualCaptureState.NoTarget or ManualCaptureState.Ready;
        var cameraIdle = !cameraStatus.IsActive &&
            !cameraStatus.IsStarting &&
            !cameraStatus.ReleaseNeedsAttention;
        if (manualIdle && replayState == ReplayCaptureState.Off &&
            cameraIdle && silhouetteProcessingIdle)
        {
            return;
        }
        throw new InvalidOperationException(
            "Stop recording, turn Instant Replay and Reaction Camera off, and wait for local " +
            "renditions to finish before changing the Capture library.");
    }
}

internal static class TrayCaptureLibraryEventAuthority
{
    internal static void RequireCurrentRoot(
        string eventRoot,
        RoutingCaptureLibraryPermit permit,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventRoot);
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        _ = permit.RequireCurrent(operation);
        var eventBinding = RoutingCaptureLibraryBindingModel.Create(
            CaptureJournalStore.NormalizeLibraryRoot(eventRoot));
        RoutingCaptureLibraryBindingModel.RequireExact(
            permit.ExpectedBinding,
            eventBinding);
    }
}

/// <summary>
/// Single startup boundary for every component that can read or mutate the Capture library.
/// Durable Routing evidence must validate before Tray constructs any of those components.
/// </summary>
internal static class TrayCaptureLibraryStartupGate
{
    internal static bool IsAllowed(RoutingApplicationLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return lifecycle.CaptureLibraryOperationsPermitted;
    }
}

/// <summary>
/// Completes only the durable local disposition of Discord-confirmed manual edits. It never
/// constructs an uploader or performs a provider call, and saves after each successful item so a
/// later failure cannot resurrect already-completed recovery work.
/// </summary>
internal sealed class TrayPendingEditedDispositionRecovery(
    WatchStateStore stateStore,
    EditedClipDispositionProcessor dispositionProcessor)
{
    private readonly WatchStateStore _stateStore = stateStore ??
        throw new ArgumentNullException(nameof(stateStore));
    private readonly EditedClipDispositionProcessor _dispositionProcessor =
        dispositionProcessor ?? throw new ArgumentNullException(nameof(dispositionProcessor));

    internal async Task<int> RecoverAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default,
        bool allowMissing = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var probe = _stateStore.ProbeForRoutingActivation(cancellationToken);
        if (allowMissing && probe.Status == WatchStateRoutingProbeStatus.Missing)
        {
            return 0;
        }
        var state = probe.Status == WatchStateRoutingProbeStatus.Loaded
            ? probe.State
            : null;
        if (state is null)
        {
            throw new InvalidDataException(
                $"Watcher state is not safe for Routing recovery ({probe.Status}).");
        }
        if (!SamePath(state.ClipsFolder, settings.ClipsFolder) ||
            AppSettings.NormalizeCaptureSource(state.CaptureSource) !=
            AppSettings.NormalizeCaptureSource(settings.CaptureSource))
        {
            throw new InvalidDataException(
                "Watcher state does not match the active watched source.");
        }
        var recovered = 0;
        foreach (var pending in state.PendingEditedUploads.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _dispositionProcessor.CompleteAsync(
                        pending,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "A confirmed edited clip still needs local archive recovery.",
                    exception);
            }

            state.PendingEditedUploads.RemoveAll(item => item.Id == pending.Id);
            _stateStore.Save(state);
            recovered++;
        }
        return recovered;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed class ReplayAutomaticStartCoordinator
{
    private readonly IReplayCaptureController _controller;
    private readonly Func<CaptureSettings> _settingsProvider;
    private readonly Func<bool> _hotkeyAvailableProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<Exception> _reportFailure;
    private readonly CancellationToken _lifetimeCancellation;
    private readonly SingleFlightGate _gate = new();
    private readonly object _stateSync = new();
    private TaskCompletionSource<bool>? _activeCompletion;
    private bool _paused;

    internal ReplayAutomaticStartCoordinator(
        IReplayCaptureController controller,
        Func<CaptureSettings> settingsProvider,
        Func<bool> hotkeyAvailableProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<Exception> reportFailure,
        CancellationToken lifetimeCancellation)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _hotkeyAvailableProvider = hotkeyAvailableProvider ??
            throw new ArgumentNullException(nameof(hotkeyAvailableProvider));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _lifetimeCancellation = lifetimeCancellation;
    }

    internal async Task RequestAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_stateSync)
        {
            if (_paused || !_gate.TryEnter()) return;
            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeCompletion = completion;
        }
        try
        {
            await ReplayAutomaticStartRunner.RunAsync(
                _controller,
                _settingsProvider,
                CanAttemptStart,
                _delayAsync,
                _lifetimeCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _reportFailure(exception);
        }
        finally
        {
            _gate.Exit();
            var restart = false;
            lock (_stateSync)
            {
                if (ReferenceEquals(_activeCompletion, completion))
                {
                    _activeCompletion = null;
                }
                restart = !_paused && !_lifetimeCancellation.IsCancellationRequested;
            }
            completion.TrySetResult(true);
            if (restart && ShouldAttemptStart()) _ = RequestAsync();
        }
    }

    internal Task PauseAndDrainAsync()
    {
        lock (_stateSync)
        {
            _paused = true;
            return _activeCompletion?.Task ?? Task.CompletedTask;
        }
    }

    internal void Resume()
    {
        lock (_stateSync) _paused = false;
    }

    private bool CanAttemptStart()
    {
        lock (_stateSync)
        {
            return !_paused && _hotkeyAvailableProvider();
        }
    }

    private bool ShouldAttemptStart()
    {
        var settings = CaptureSettings.Normalize(_settingsProvider());
        return ReplayCapturePolicy.ShouldAttemptAutomaticStart(
            settings.InstantReplayEnabled,
            CanAttemptStart(),
            _controller.ReplayStatus);
    }
}

internal static class CaptureHotkeyRegistration
{
    internal static bool TryApply(
        GlobalHotkeyManager manager,
        CaptureSettings settings,
        out bool hotkeyAvailable,
        out int errorCode)
    {
        ArgumentNullException.ThrowIfNull(manager);
        settings = CaptureSettings.Normalize(settings);
        GlobalHotkeyBinding? binding = null;
        if (settings.InstantReplayEnabled)
        {
            if (!GlobalHotkeyBinding.TryParse(settings.SaveHotkey, out var parsed))
            {
                errorCode = 0;
                hotkeyAvailable = manager.GetBinding(GlobalHotkeyManager.CaptureHotkeyIdentifier) is not null;
                return false;
            }
            binding = parsed;
        }

        var applied = manager.TrySetBinding(
            GlobalHotkeyManager.CaptureHotkeyIdentifier,
            binding,
            out errorCode);
        hotkeyAvailable = applied ||
            manager.GetBinding(GlobalHotkeyManager.CaptureHotkeyIdentifier) is not null;
        return applied;
    }
}

internal static class ReactionCameraTrayIconFactory
{
    private const int IconSize = 32;

    internal static ReactionCameraRuntimeState SelectState(ReactionCameraRuntimeStatus status) =>
        status.IsActive
            ? ReactionCameraRuntimeState.Active
            : status.IsStarting
                ? ReactionCameraRuntimeState.Starting
                : status.ReleaseNeedsAttention
                    ? ReactionCameraRuntimeState.ReleaseNeedsAttention
                    : ReactionCameraRuntimeState.Off;

    internal static Icon Create(ReactionCameraRuntimeState state)
    {
        if (state is not (ReactionCameraRuntimeState.Starting or
            ReactionCameraRuntimeState.Active or
            ReactionCameraRuntimeState.ReleaseNeedsAttention))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var backgroundColor = state switch
            {
                ReactionCameraRuntimeState.Starting => Color.FromArgb(244, 177, 76),
                ReactionCameraRuntimeState.ReleaseNeedsAttention => Color.FromArgb(214, 55, 78),
                _ => ClipCordTheme.Coral
            };
            using var background = new SolidBrush(backgroundColor);
            using var outline = new Pen(Color.FromArgb(235, 255, 255, 255), 1.5f);
            var badgeBounds = new RectangleF(1.5f, 1.5f, IconSize - 3f, IconSize - 3f);
            graphics.FillEllipse(background, badgeBounds);
            graphics.DrawEllipse(outline, badgeBounds);
            FigmaIconRenderer.Draw(
                graphics,
                new Rectangle(7, 7, 18, 18),
                state == ReactionCameraRuntimeState.ReleaseNeedsAttention
                    ? FigmaIconAsset.Alert
                    : FigmaIconAsset.Camera,
                state == ReactionCameraRuntimeState.Starting
                    ? Color.FromArgb(10, 18, 32)
                    : Color.White);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}

internal enum ModeHotkeyBlockReason
{
    None,
    ShuttingDown,
    DialogOpen,
    ReconfigurationInProgress
}

internal sealed class RoutingLocalOnlyModeViewSource : IRoutingLocalOnlyModeViewSource
{
    private readonly RoutingLocalOnlyOverrideState _state;
    private readonly GlobalHotkeyManager _hotkeys;
    private readonly Action<RoutingLocalOnlyOverrideInspection, bool> _stateChanged;
    private readonly SemaphoreSlim _enabledMutation = new(1, 1);
    private readonly Func<bool, CancellationToken, Task> _beforeEnabledMutation;

    internal RoutingLocalOnlyModeViewSource(
        RoutingLocalOnlyOverrideState state,
        GlobalHotkeyManager hotkeys,
        Action<RoutingLocalOnlyOverrideInspection, bool> stateChanged,
        Func<bool, CancellationToken, Task>? beforeEnabledMutation = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        _beforeEnabledMutation = beforeEnabledMutation ??
            (static (_, _) => Task.CompletedTask);
    }

    public RoutingLocalOnlyModeViewSnapshot Inspect() => ToView(_state.Inspect());

    public async Task<RoutingLocalOnlyModeViewActionResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        await MutateEnabledAsync(_ => enabled, cancellationToken).ConfigureAwait(false);

    internal async Task<RoutingLocalOnlyModeViewActionResult> ToggleEnabledAsync(
        CancellationToken cancellationToken = default) =>
        await MutateEnabledAsync(
                before => !before.EffectiveEnabled,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<RoutingLocalOnlyModeViewActionResult> MutateEnabledAsync(
        Func<RoutingLocalOnlyOverrideInspection, bool> selectEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectEnabled);
        await _enabledMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _state.Inspect(cancellationToken);
            var enabled = selectEnabled(before);
            await _beforeEnabledMutation(enabled, cancellationToken).ConfigureAwait(false);
            var updated = await _state.SetEnabledAsync(enabled, cancellationToken)
                .ConfigureAwait(false);
            _stateChanged(updated, before.EffectiveEnabled != updated.EffectiveEnabled);
            return Success(updated);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not persist the Routing Local-only override.", exception);
            return Failure(
                "ClipCord could not save Local-only mode. Existing routing is unchanged.");
        }
        finally
        {
            _enabledMutation.Release();
        }
    }

    public async Task<RoutingLocalOnlyModeViewActionResult> SetHotkeyAsync(
        string hotkeyDisplayText,
        CancellationToken cancellationToken = default)
    {
        var normalized = AppSettings.NormalizeModeToggleHotkey(hotkeyDisplayText);
        GlobalHotkeyBinding? candidate = null;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            if (!GlobalHotkeyBinding.TryParse(normalized, out var parsed))
            {
                return Failure("Use Ctrl or Alt with a letter, number, or function key.");
            }
            candidate = parsed;
        }

        var previouslyRegistered = _hotkeys.RegisteredBinding;
        if (!_hotkeys.TrySetBinding(candidate, out var errorCode))
        {
            return Failure(
                errorCode == GlobalHotkeyManager.HotkeyConflictError
                    ? $"{normalized} is already in use by another app. ClipCord kept " +
                      $"{previouslyRegistered?.DisplayText ?? "the previous shortcut"}."
                    : "Windows could not register that shortcut. ClipCord kept the previous shortcut.",
                hotkeyConflict: errorCode == GlobalHotkeyManager.HotkeyConflictError);
        }

        try
        {
            var updated = await _state.SetHotkeyAsync(normalized, cancellationToken)
                .ConfigureAwait(false);
            _stateChanged(updated, false);
            return Success(updated);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException or OperationCanceledException)
        {
            if (!_hotkeys.TrySetBinding(previouslyRegistered, out var restoreError))
            {
                Log.Error(
                    $"ClipCord could not restore the previous Local-only shortcut after a save failure. Windows error {restoreError}.");
            }
            if (exception is not OperationCanceledException)
            {
                Log.Error("ClipCord could not persist the Routing Local-only shortcut.", exception);
            }
            return Failure("ClipCord kept the previous Local-only mode shortcut.");
        }
    }

    public async Task<RoutingLocalOnlyModeViewActionResult> DismissFirstRunNoticeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await _state.DismissFirstRunNoticeAsync(cancellationToken)
                .ConfigureAwait(false);
            _stateChanged(updated, false);
            return Success(updated);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not dismiss the Local-only shortcut notice.", exception);
            return Failure("ClipCord could not dismiss this notice yet.");
        }
    }

    private RoutingLocalOnlyModeViewActionResult Success(
        RoutingLocalOnlyOverrideInspection inspection) => new(
        inspection.LoadedFromDisk,
        ToView(inspection),
        inspection.LoadedFromDisk ? null :
            "Saved Local-only mode needs attention. External delivery remains paused.");

    private RoutingLocalOnlyModeViewActionResult Failure(
        string error,
        bool hotkeyConflict = false) => new(
        false,
        Inspect(),
        error,
        hotkeyConflict);

    private static RoutingLocalOnlyModeViewSnapshot ToView(
        RoutingLocalOnlyOverrideInspection inspection) => new(
        IsAvailable: inspection.LoadedFromDisk,
        inspection.EffectiveEnabled,
        inspection.EffectiveHotkeyBinding,
        inspection.Document?.FirstRunNoticeDismissed ?? false,
        inspection.LoadedFromDisk
            ? string.Empty
            : "Saved Local-only mode needs attention. External delivery remains paused.");
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly Icon _applicationIcon;
    private readonly Icon _reactionCameraStartingIcon;
    private readonly Icon _reactionCameraActiveIcon;
    private readonly Icon _reactionCameraAttentionIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _disableReactionCameraItem;
    private readonly ToolStripMenuItem _uploadToDiscordItem;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly UpdateCoordinator _updateCoordinator;
    private readonly IUpdateDownloadService _updateDownloadService;
    private readonly ActivityHistoryStore _activityHistory;
    private readonly IFavoritesService _favorites;
    private readonly GlobalHotkeyManager _globalHotkey;
    private readonly ModeFeedbackOverlay _modeFeedbackOverlay;
    private readonly CaptureHostClient? _captureHostClient;
    private readonly IManualCaptureRecorder? _manualCaptureRecorder;
    private readonly ICaptureProjectCompletionSource? _captureProjectCompletionSource;
    private SilhouetteProcessingCoordinator? _silhouetteProcessingCoordinator;
    private readonly ClipProcessingOwnershipCoordinator _processingOwnership = new();
    private readonly DiscordConnectionCatalog _discordConnectionCatalog = new();
    private readonly RoutingExecutionAuthorityStore _routingAuthorityStore;
    private readonly LegacyRoutingMigrationMarkerStore _routingMigrationMarkers;
    private readonly RoutingApplicationLifecycle _routingLifecycle;
    private readonly RoutingLocalOnlyOverrideState _routingLocalOnlyState;
    private readonly RoutingLocalOnlyModeViewSource _routingLocalOnlyViewSource;
    private RoutingCaptureLibraryPermit? _captureLibraryPermit;
    private RoutingWatchedRootHandle? _captureLibraryRootPin;
    private System.Threading.Timer? _captureLibraryPermitMonitor;
    private CancellationTokenSource _captureLibraryAuthorityCancellation = new();
    private readonly object _captureLibraryRevocationSync = new();
    private Task _captureLibraryRevocationTask = Task.CompletedTask;
    private readonly object _legacyControllerSync = new();
    private readonly TrayProcessingOperationGate _processingOperationGate = new();
    private readonly TrayPendingEditedDispositionRecovery _pendingEditedRecovery;
    private readonly WatchStateStore _routingManualEditStateStore;
    private readonly TrayPendingEditedDispositionRecovery _routingManualEditRecovery;
    // Anything created at or after this process boundary may still be completing in the
    // isolated capture host while startup reconciliation scans the shared library.
    private readonly DateTimeOffset _captureRecoveryCutoffUtc = DateTimeOffset.UtcNow;
    private readonly object _captureRecoverySync = new();
    private Task<bool> _captureRecoveryTask;
    private Task _captureSubsystemStartupTask = Task.CompletedTask;
    private readonly object _silhouetteSettlementSync = new();
    private readonly HashSet<Task> _silhouetteSettlementTasks = [];
    private readonly object _captureLibrarySafetyPinSync = new();
    private readonly List<RoutingWatchedRootHandle> _captureLibrarySafetyPins = [];
    private int _captureLibraryWorkerStopFailed;
    private volatile CaptureSettings _captureSettings;
    private string _baseTrayStatus = "Starting…";
    private ManualCaptureState _manualCaptureState = ManualCaptureState.NoTarget;
    private ReplayCaptureState _replayCaptureState = ReplayCaptureState.Off;
    private ReactionCameraRuntimeStatus _reactionCameraStatus = new(false, false);
    private volatile bool _captureHotkeyAvailable;
    private readonly ReplayAutomaticStartCoordinator? _replayStartCoordinator;
    private int _replaySaveInProgress;
    private int _reactionCameraDisableInProgress;
    private int _captureLibraryRevocationScheduled;
    private int _captureLibrarySwitchInProgress;
    private AppSettings _settings;
    private AppSettings? _settingsBeingApplied;
    private DiscordAwareController? _controller;
    private RoutingActiveWorkSession? _routingSession;
    private Task? _routingSessionObserver;
    private Task? _processingStartupTask;
    private int _routingRetryInProgress;
    private bool _settingsOpen;
    private bool _automaticUpdateCheckScheduled;
    private bool _updateDialogOpen;
    private volatile bool _shutdownScheduled;
    private bool _reconfigurationInProgress;
    private bool _exitRequestedAfterReconfiguration;
    private CancellationTokenSource? _manualClipOperationCancellation;
    private SettingsForm? _settingsForm;

    internal UpdateLaunchRequest? PendingUpdateLaunch { get; private set; }

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _applicationIcon = LoadApplicationIcon();
        _reactionCameraStartingIcon = ReactionCameraTrayIconFactory.Create(
            ReactionCameraRuntimeState.Starting);
        _reactionCameraActiveIcon = ReactionCameraTrayIconFactory.Create(
            ReactionCameraRuntimeState.Active);
        _reactionCameraAttentionIcon = ReactionCameraTrayIconFactory.Create(
            ReactionCameraRuntimeState.ReleaseNeedsAttention);
        _settings = SettingsStore.Load();
        var captureSettingsInspection = CaptureSettingsStore.Inspect();
        _captureSettings = captureSettingsInspection.Settings ?? CaptureSettings.Default;
        if (captureSettingsInspection.Error is not null)
        {
            Log.Error(
                "Could not load strict ClipCord Capture settings evidence.",
                captureSettingsInspection.Error);
        }
        _routingAuthorityStore = new RoutingExecutionAuthorityStore();
        _routingMigrationMarkers = new LegacyRoutingMigrationMarkerStore();
        _activityHistory = new ActivityHistoryStore();
        _favorites = new FavoritesService();
        _pendingEditedRecovery = new TrayPendingEditedDispositionRecovery(
            new WatchStateStore(),
            new EditedClipDispositionProcessor(favorites: _favorites));
        _routingManualEditStateStore = new WatchStateStore(
            Path.Combine(SettingsStore.DataDirectory, "routing-manual-edits.json"),
            Path.Combine(SettingsStore.DataDirectory, "routing-manual-edits.safe-baseline"));
        _routingManualEditRecovery = new TrayPendingEditedDispositionRecovery(
            _routingManualEditStateStore,
            new EditedClipDispositionProcessor(favorites: _favorites));
        _globalHotkey = new GlobalHotkeyManager();
        _globalHotkey.Pressed += ModeToggleHotkeyPressed;
        _globalHotkey.HotkeyPressed += GlobalHotkeyPressed;
        _modeFeedbackOverlay = new ModeFeedbackOverlay();
        _routingLocalOnlyState = new RoutingLocalOnlyOverrideState();
        _routingLocalOnlyViewSource = new RoutingLocalOnlyModeViewSource(
            _routingLocalOnlyState,
            _globalHotkey,
            RoutingLocalOnlyStateChanged);
        _routingLifecycle = CreateRoutingApplicationLifecycle();

        _captureHostClient = null;
        _manualCaptureRecorder = null;
        _replayStartCoordinator = null;
        _silhouetteProcessingCoordinator = null;
        _captureRecoveryTask = Task.FromResult(false);
        if (TrayCaptureLibraryStartupGate.IsAllowed(_routingLifecycle) &&
            TryInitializeCaptureLibraryPermit(captureSettingsInspection))
        {
            var captureCapability = new WindowsCaptureCapabilityProbe().Inspect();
            _captureHostClient = captureCapability.Readiness == CaptureReadiness.Ready
                ? new CaptureHostClient()
                : null;
            _manualCaptureRecorder = captureCapability.Readiness == CaptureReadiness.Ready
                ? new CaptureHostManualRecorder(_captureHostClient!)
                : null;
            if (_manualCaptureRecorder is IReplayCaptureController automaticReplayController)
            {
                _replayStartCoordinator = new ReplayAutomaticStartCoordinator(
                    automaticReplayController,
                    () => _captureSettings,
                    () => _captureHotkeyAvailable && CaptureLibraryAccessAllowed(),
                    static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                    exception => Log.Error(
                        "ClipCord could not restore the configured Instant Replay buffer.",
                        exception),
                    _lifetimeCancellation.Token);
            }
            _captureRecoveryTask = RecoverCaptureJournalsAsync(
                _captureSettings.LibraryRoot,
                RequireCaptureLibraryPermit("starting Capture recovery"),
                Volatile.Read(ref _captureLibraryAuthorityCancellation).Token);
        }
        _captureProjectCompletionSource =
            _manualCaptureRecorder as ICaptureProjectCompletionSource;
        if (_captureProjectCompletionSource is not null)
        {
            _captureProjectCompletionSource.ProjectCommitted += CaptureProjectCommitted;
        }
        var assemblyVersion = typeof(TrayApplicationContext).Assembly.GetName().Version ?? new Version(0, 0, 0);
        _updateCoordinator = new UpdateCoordinator(
            GitHubUpdateChecker.Create(),
            new UpdatePreferencesStore(),
            StableVersion.FromAssemblyVersion(assemblyVersion));
        _updateDownloadService = UpdateDownloadService.Create();
        _updateTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds
        };
        _updateTimer.Tick += UpdateTimerTick;

        _statusItem = new ToolStripMenuItem("Starting…")
        {
            Name = "TrayStatusMenuItem",
            Enabled = true
        };
        _disableReactionCameraItem = new ToolStripMenuItem("Turn Reaction Camera off")
        {
            Name = "TurnReactionCameraOffMenuItem",
            Visible = false,
            Enabled = false
        };
        _disableReactionCameraItem.Click += (_, _) => _ = DisableReactionCameraFromTrayAsync();
        var homeItem = new ToolStripMenuItem("Open ClipCord…", null, (_, _) => ShowSettings(initialPage: SettingsPage.Home));
        var configureItem = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        var activityItem = new ToolStripMenuItem("Activity…", null, (_, _) => ShowSettings(initialPage: SettingsPage.Activity));
        var openFolderItem = new ToolStripMenuItem("Open clips folder", null, (_, _) => OpenClipsFolder());
        _uploadToDiscordItem = new ToolStripMenuItem("Upload new clips to Discord")
        {
            Name = "ModeToggleMenuItem",
            CheckOnClick = true,
            Checked = _settings.UploadToDiscord,
            Enabled = _settings.IsValid
        };
        _uploadToDiscordItem.Click += (_, _) => ToggleUploadModeFromTray();
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => RequestExit());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_disableReactionCameraItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(homeItem);
        menu.Items.Add(configureItem);
        menu.Items.Add(activityItem);
        menu.Items.Add(openFolderItem);
        menu.Items.Add(_uploadToDiscordItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "ClipCord",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings(initialPage: SettingsPage.Home);
        if (_manualCaptureRecorder is not null)
        {
            _manualCaptureState = _manualCaptureRecorder.State;
            _manualCaptureRecorder.StateChanged += ManualCaptureRecorderStateChanged;
            if (_manualCaptureRecorder is IReplayCaptureController replayController)
            {
                _replayCaptureState = replayController.ReplayStatus.State;
                replayController.ReplayStateChanged += ReplayCaptureStateChanged;
            }
            if (_manualCaptureRecorder is IReactionCameraController reactionCameraController)
            {
                _reactionCameraStatus = reactionCameraController.ReactionCameraStatus;
                reactionCameraController.ReactionCameraStateChanged += ReactionCameraStateChanged;
            }
        }

        var startupAuthority = InspectOperationalRoutingAuthority();
        if (!startupAuthority.LegacyPermitted)
        {
            try
            {
                _ = _routingLocalOnlyState.EnsureMigratedAsync(
                        _settings.UploadToDiscord,
                        _settings.ModeToggleHotkey,
                        _lifetimeCancellation.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (
                exception is InvalidDataException or InvalidOperationException or IOException or
                    UnauthorizedAccessException)
            {
                Log.Error(
                    "ClipCord could not initialize the Routing Local-only override; external delivery remains fail-safe paused.",
                    exception);
            }
        }

        if (_settings.IsValid)
        {
            if (!TryApplyModeToggleHotkey(_settings, out var hotkeyError))
            {
                Log.Error($"Could not register the global mode shortcut. Windows error {hotkeyError}.");
                _uiContext.Post(_ => ShowHotkeyNotification(
                    "Shortcut unavailable",
                    $"{GetEffectiveModeToggleHotkey(_settings)} is already in use. Choose another shortcut in Routes.",
                    ToolTipIcon.Warning), null);
            }
            UpdateLegacyModeControl();
            _processingStartupTask = StartClipProcessingAsync();
            _processingOperationGate.SetStartup(_processingStartupTask);
            _ = ApplyInitialStartupPreferenceAsync(_settings.StartWithWindows);
            StartUpdateChecks();
        }
        else
        {
            SetStatus(startupAuthority.Blocked || startupAuthority.RoutingRequired
                ? "Routes need attention — open Settings"
                : "Setup required");
            UpdateLegacyModeControl();
            if (startupAuthority.RoutingRequired)
            {
                // Sticky authority never falls back to Legacy just because the mutable settings
                // document needs attention. Attempt the cold Routing start so its exact marker
                // validation can produce the authoritative failure state.
                _processingStartupTask = StartClipProcessingAsync();
                _processingOperationGate.SetStartup(_processingStartupTask);
                StartUpdateChecks();
            }
            Application.Idle += ShowFirstRunSettings;
        }
        var initialCaptureHotkeyAvailable = false;
        var captureHotkeyError = 0;
        if (CaptureLibraryAccessAllowed())
        {
            _ = TryApplyCaptureHotkey(
                _captureSettings,
                out initialCaptureHotkeyAvailable,
                out captureHotkeyError);
        }
        _captureHotkeyAvailable = initialCaptureHotkeyAvailable;
        if (CaptureLibraryAccessAllowed() &&
            !_captureHotkeyAvailable)
        {
            Log.Error($"Could not register the Instant Replay shortcut. Windows error {captureHotkeyError}.");
            _uiContext.Post(_ => ShowHotkeyNotification(
                "Replay shortcut unavailable",
                $"{_captureSettings.SaveHotkey} is already in use. Choose another shortcut in Capture.",
                ToolTipIcon.Warning), null);
        }
        StartCaptureLibraryPermitMonitor();
        _captureSubsystemStartupTask = InitializeCaptureSubsystemsAfterRecoveryAsync();
    }

    private RoutingApplicationLifecycle CreateRoutingApplicationLifecycle()
    {
        var watchState = new WatchStateStore();
        var snapshots = new RoutingSnapshotStore();
        var markers = _routingMigrationMarkers;
        var evidence = new LegacyRoutingActivationEvidenceSource(
            watchState,
            CurrentAppSettings,
            () => _discordConnectionCatalog.GetLegacyConnectionIds(
                CurrentAppSettings()),
            RequireCurrentCaptureLibraryBinding);
        var cutover = new LegacyDiscordConnectionCutoverAdapter(
            _discordConnectionCatalog,
            new LegacyRoutingMigrationCoordinator(snapshots, markers));
        var productionRuntime = new RoutingProductionRuntime(
            new LegacyRoutingActivationPreparer(
                CurrentAppSettings,
                watchState,
                cutover,
                RequireCurrentCaptureLibraryBinding),
            markers,
            snapshots,
            evidence,
            _routingAuthorityStore,
            RoutingWatchedSourceAdapters.CoveredSources,
            StartRoutingWorkSessionAsync,
            StopRoutingWorkSessionAsync,
            PreflightRoutingWorkSessionAsync);
        return RoutingApplicationLifecycle.Create(
            _routingAuthorityStore,
            _processingOwnership,
            new TrayLegacyClipProcessingRuntime(this),
            productionRuntime,
            migrationMarkers: markers,
            currentCaptureLibraryBinding: RequireCurrentCaptureLibraryBinding);
    }

    private async Task StartClipProcessingAsync()
    {
        var cancellationToken = _lifetimeCancellation.Token;
        var lifecycle = _routingLifecycle;
        try
        {
            var sequence = await TrayRoutingStartupSequence.RunAsync(
                    lifecycle.RoutingSelectedAtBootstrap,
                    recoveryToken => RecoverRoutingPrerequisitesAsync(
                        CurrentAppSettings(),
                        lifecycle.RoutingSelectedAtBootstrap,
                        recoveryToken),
                    lifecycle.StartAsync,
                    static result => result.Status ==
                        RoutingApplicationLifecycleStatus.LegacyStarted,
                    lifecycle.ActivateRoutingAsync,
                    cancellationToken)
                .ConfigureAwait(false);
            if (sequence.RecoveryError is not null)
            {
                ReportRoutingActivationDeferred(sequence.RecoveryError);
                return;
            }
            HandleProcessingLifecycleResult(sequence.Result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not establish its clip-processing runtime.", exception);
            SetRoutingNeedsAttention(
                "Routes need attention — processing did not start",
                exception);
        }
    }

    private void HandleProcessingLifecycleResult(RoutingApplicationLifecycleResult result)
    {
        if (result.State == RoutingApplicationLifecycleState.RoutingRunning)
        {
            var localOnly = _routingLocalOnlyState.Inspect();
            SetStatus(localOnly.EffectiveEnabled
                ? "Local-only mode on · future clips stay here"
                : "Routes active — watching for new clips");
            _uiContext.Post(_ =>
            {
                if (!TryApplyModeToggleHotkey(
                        localOnly.EffectiveHotkeyBinding,
                        out var hotkeyError))
                {
                    Log.Error(
                        $"Could not register the Routing Local-only shortcut. Windows error {hotkeyError}.");
                    ShowHotkeyNotification(
                        "Shortcut unavailable",
                        $"{localOnly.EffectiveHotkeyBinding} is already in use. Choose another shortcut in Routes.",
                        ToolTipIcon.Warning);
                }
                UpdateLegacyModeControl();
            }, null);
            return;
        }
        if (result.State == RoutingApplicationLifecycleState.LegacyRunning)
        {
            // The reversible migration was not ready. The legacy controller has already been
            // restored and remains authoritative; its own status callback is more specific.
            if (result.Error is not null)
            {
                Log.Error(
                    "Routes were not ready; legacy clip processing remains active.",
                    result.Error);
            }
            return;
        }
        if (result.Status is RoutingApplicationLifecycleStatus.NeedsAttention or
            RoutingApplicationLifecycleStatus.StartFailed)
        {
            SetRoutingNeedsAttention(
                "Routes need attention — processing is paused",
                result.Error ?? result.RoutingTransition?.Error);
        }
    }

    private async ValueTask StartRoutingWorkSessionAsync(
        ClipProcessingOwnershipLease routingOwnership,
        RoutingRuntimeFeatureGate featureGate,
        CancellationToken cancellationToken)
    {
        await RequireCurrentCaptureRecoveryAsync(cancellationToken)
            .ConfigureAwait(false);
        var watchedSettings = CanonicalRoutingWatchedSettings(CurrentAppSettings());
        var captureSettings = CanonicalRoutingCaptureSettings(_captureSettings);
        var session = RoutingActiveWorkSession.CreateProduction(
            watchedSettings,
            CurrentAppSettings,
            captureSettings,
            () => _captureSettings,
            routingOwnership,
            featureGate,
            captureLibraryPermit: RequireCaptureLibraryPermit(
                "starting the Routing work session"),
            activityHistory: _activityHistory);
        if (Interlocked.CompareExchange(ref _routingSession, session, null) is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "A Routing work session is already active.");
        }
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            _routingSessionObserver = ObserveRoutingSessionAsync(session);
        }
        catch
        {
            _ = Interlocked.CompareExchange(ref _routingSession, null, session);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask PreflightRoutingWorkSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDurableCaptureSettingsForRoutingActivation();
        _ = RoutingActiveWorkSession.RequireProductionConfiguration(
            CanonicalRoutingWatchedSettings(CurrentAppSettings()),
            CanonicalRoutingCaptureSettings(_captureSettings));
        _ = RequireCurrentCaptureLibraryBinding();
        _ = RequireCaptureLibraryPermit("preflighting Routing activation");
        return ValueTask.CompletedTask;
    }

    private async ValueTask StopRoutingWorkSessionAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var session = Interlocked.Exchange(ref _routingSession, null);
        if (session is null) return;
        try
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ObserveRoutingSessionAsync(RoutingActiveWorkSession session)
    {
        try
        {
            await session.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_shutdownScheduled || !ReferenceEquals(
                    Volatile.Read(ref _routingSession), session))
            {
                return;
            }
            Log.Error("The active Routes processing session stopped unexpectedly.", exception);
            try
            {
                _ = await _routingLifecycle.StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception stopException)
            {
                Log.Error(
                    "ClipCord could not finish quiescing the failed Routes session.",
                    stopException);
            }
            SetRoutingNeedsAttention(
                "Routes need attention — processing stopped",
                session.Failure ?? exception);
        }
    }

    private void SetRoutingNeedsAttention(string status, Exception? exception)
    {
        if (exception is not null)
        {
            Log.Error(status, exception);
        }
        SetStatus(status);
        _uiContext.Post(_ =>
        {
            UpdateLegacyModeControl();
            ShowHotkeyNotification(
                "Routes need attention",
                "Clip processing is paused. Open Routes to review the current setup.",
                ToolTipIcon.Warning);
        }, null);
    }

    private void ReportRoutingActivationDeferred(
        Exception exception,
        string? statusBeforeActivation = null)
    {
        Log.Error(
            "Routes were not activated because local recovery prerequisites did not finish; legacy clip processing remains active.",
            exception);
        _uiContext.Post(_ =>
        {
            if (!string.IsNullOrWhiteSpace(statusBeforeActivation) &&
                TrayRoutesRuntimePresentation.IsActivationStatus(_baseTrayStatus))
            {
                _baseTrayStatus = statusBeforeActivation;
                _statusItem.Text = statusBeforeActivation;
                UpdateTrayCaptureIndicator();
            }
            UpdateLegacyModeControl();
            ShowHotkeyNotification(
                "Routes not activated",
                "Existing clip processing remains active. Open Routes after local recovery is resolved.",
                ToolTipIcon.Warning);
        }, null);
    }

    private RoutesRuntimeViewState GetRoutesRuntimeViewState()
    {
        var transitionRunning = _processingStartupTask is { IsCompleted: false } ||
                                Volatile.Read(ref _routingRetryInProgress) != 0;
        return TrayRoutesRuntimePresentation.Map(
            transitionRunning,
            _routingLifecycle.State);
    }

    /// <summary>
    /// Projects durable Routing state into the intentionally narrow model consumed by Home and
    /// About. No path, route name, destination, webhook, clip name, or arbitrary runtime text can
    /// cross this boundary.
    /// </summary>
    private RoutingUiPresentationSnapshot? GetRoutingUiPresentationSnapshot()
    {
        // Unknown state is fail-safe Local-only. This value is deliberately retained outside
        // the inspection try/catch so an inspection failure never calls the same failing store
        // a second time while constructing a privacy-safe fallback.
        var localOnlyEnabled = true;
        try
        {
            var authority = InspectOperationalRoutingAuthority();
            if (authority.LegacyPermitted) return null;

            var localOnly = _routingLocalOnlyState.Inspect();
            localOnlyEnabled = localOnly.EffectiveEnabled;
            var lifecycleState = _routingLifecycle.State;
            if (!localOnly.LoadedFromDisk)
            {
                return new RoutingUiPresentationSnapshot(
                    RoutingUiState.Unavailable,
                    ActiveRouteCount: 0,
                    WatchingSourceCount: 0,
                    localOnlyEnabled);
            }

            var routes = new RoutingSnapshotStore().Load();
            var inputs = new RoutingInputSourceCatalog().Inspect();
            if (routes.Status is not (RoutingDocumentLoadStatus.Missing or
                    RoutingDocumentLoadStatus.Loaded) ||
                !inputs.IsUsable)
            {
                return new RoutingUiPresentationSnapshot(
                    RoutingUiState.Unavailable,
                    ActiveRouteCount: 0,
                    WatchingSourceCount: 0,
                    localOnlyEnabled);
            }

            var state = authority.Blocked ||
                        lifecycleState is RoutingApplicationLifecycleState.NeedsAttention or
                            RoutingApplicationLifecycleState.RoutingRecoveryNeeded
                ? RoutingUiState.NeedsAttention
                : lifecycleState == RoutingApplicationLifecycleState.RoutingRunning
                    ? RoutingUiState.Active
                    : RoutingUiState.Inactive;
            var enabledRoutes = routes.Document?.Routes
                .Where(route => route.Enabled)
                .ToArray() ?? [];
            var activeRoutes = enabledRoutes.Length;
            var specificRoutes = enabledRoutes.Count(route => route.Kind == RoutingRouteKind.Specific);
            var fallbackRoutes = enabledRoutes.Count(route => route.Kind == RoutingRouteKind.Fallback);
            // Local-only is an overlay, not a disabled route. Count only routes the user has
            // explicitly paused here; held Deliver actions remain active route definitions and
            // are represented by LocalOnlyModeEnabled instead.
            var pausedRoutes = routes.Document?.Routes.Count(route => !route.Enabled) ?? 0;

            var legacySettings = CurrentAppSettings();
            var watchingSources = !string.IsNullOrWhiteSpace(legacySettings.ClipsFolder) &&
                                  Directory.Exists(legacySettings.ClipsFolder)
                ? 1
                : 0;
            if (_captureSettings.InstantReplayEnabled && CaptureLibraryAccessAllowed())
            {
                watchingSources++;
            }
            watchingSources += inputs.Sources.Count(source =>
                source.Enabled && !source.Retired &&
                source.Health == RoutingInputSourceHealth.Ready);

            var destinations = RoutingUiDestinations.Library;
            foreach (var action in enabledRoutes.SelectMany(route => route.Actions)
                         .Where(action => action.Enabled))
            {
                destinations |= action.Kind switch
                {
                    RoutingActionKind.FileIntoLibrary => RoutingUiDestinations.Library,
                    RoutingActionKind.Deliver when action.Destination == RoutingDestinationKind.Discord =>
                        RoutingUiDestinations.Discord,
                    RoutingActionKind.Deliver when action.Destination == RoutingDestinationKind.YouTube =>
                        RoutingUiDestinations.YouTube,
                    RoutingActionKind.Deliver when action.Destination == RoutingDestinationKind.TikTok =>
                        RoutingUiDestinations.TikTok,
                    _ => RoutingUiDestinations.None
                };
            }
            var destinationCount = 0;
            foreach (var destination in new[]
                     {
                         RoutingUiDestinations.Library,
                         RoutingUiDestinations.Discord,
                         RoutingUiDestinations.YouTube,
                         RoutingUiDestinations.TikTok
                     })
            {
                if (destinations.HasFlag(destination)) destinationCount++;
            }

            return new RoutingUiPresentationSnapshot(
                state,
                activeRoutes,
                watchingSources,
                localOnlyEnabled,
                specificRoutes,
                fallbackRoutes,
                pausedRoutes,
                destinationCount,
                destinations).Normalize();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or IOException or
                UnauthorizedAccessException or ArgumentException or NotSupportedException or
                PathTooLongException or System.Security.SecurityException)
        {
            Log.Error("Could not inspect the privacy-safe Routing presentation state.", exception);
            return new RoutingUiPresentationSnapshot(
                RoutingUiState.Unavailable,
                ActiveRouteCount: 0,
                WatchingSourceCount: 0,
                localOnlyEnabled);
        }
    }

    private async Task<bool> RetryRoutesRuntimeFromUiAsync()
    {
        using var processingMutation = await _processingOperationGate.EnterAsync(
            _lifetimeCancellation.Token);
        if (_shutdownScheduled) return false;
        var statusBeforeActivation = _baseTrayStatus;
        Interlocked.Exchange(ref _routingRetryInProgress, 1);
        SetStatus("Routes activating — safely preparing existing clips");
        try
        {
            var authority = InspectOperationalRoutingAuthority();
            var sequence = await TrayRoutingRetrySequence.RunAsync(
                    recoveryToken => RecoverRoutingPrerequisitesAsync(
                        CurrentAppSettings(),
                        recoverPendingEditedDispositions:
                            authority.RoutingRequired ||
                            _routingLifecycle.RoutingSelectedAtBootstrap,
                        recoveryToken),
                    () => _routingLifecycle.State ==
                        RoutingApplicationLifecycleState.LegacyReady,
                    _routingLifecycle.StartAsync,
                    static result => result.State ==
                        RoutingApplicationLifecycleState.LegacyRunning,
                    _routingLifecycle.ActivateRoutingAsync,
                    _lifetimeCancellation.Token)
                .ConfigureAwait(false);
            var result = sequence.Result;
            HandleProcessingLifecycleResult(result);
            if (result.State == RoutingApplicationLifecycleState.RoutingRunning)
            {
                _uiContext.Post(_ => ShowHotkeyNotification(
                    "Routes active",
                    "New clips now use your saved routes.",
                    ToolTipIcon.Info), null);
                return true;
            }
            if (result.State == RoutingApplicationLifecycleState.LegacyRunning)
            {
                ReportRoutingActivationDeferred(
                    result.Error ?? result.RoutingTransition?.Error ??
                    new InvalidOperationException(
                        "The safe Routing migration did not finish."),
                    statusBeforeActivation);
            }
            return false;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            if (_routingLifecycle.State == RoutingApplicationLifecycleState.LegacyRunning)
            {
                ReportRoutingActivationDeferred(exception, statusBeforeActivation);
            }
            else
            {
                SetRoutingNeedsAttention(
                    "Routes need attention — processing is paused",
                    exception);
            }
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _routingRetryInProgress, 0);
            _uiContext.Post(_ =>
            {
                UpdateLegacyModeControl();
            }, null);
        }
    }

    private static AppSettings CanonicalRoutingWatchedSettings(AppSettings settings) =>
        settings with
        {
            ClipsFolder = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(settings.ClipsFolder))
        };

    private static CaptureSettings CanonicalRoutingCaptureSettings(CaptureSettings settings) =>
        settings with
        {
            LibraryRoot = CaptureJournalStore.NormalizeLibraryRoot(settings.LibraryRoot)
        };

    private void EnsureDurableCaptureSettingsForRoutingActivation()
    {
        var inspection = CaptureSettingsStore.Inspect();
        if (inspection.Status == CaptureSettingsDocumentStatus.Missing)
        {
            // This is still the reversible Legacy side of activation. Persisting the effective
            // defaults gives a later cold Routing start an unambiguous settings document to bind.
            CaptureSettingsStore.Save(_captureSettings);
            inspection = CaptureSettingsStore.Inspect();
        }
        if (!inspection.Loaded)
        {
            throw new InvalidDataException(
                $"Capture settings cannot be bound for Routing ({inspection.Status}).",
                inspection.Error);
        }
        RequireSameCaptureLibraryRoot(inspection.Settings!, _captureSettings);
    }

    private RoutingCaptureLibraryBinding RequireCurrentCaptureLibraryBinding()
    {
        var inspection = CaptureSettingsStore.Inspect();
        if (!inspection.Loaded)
        {
            throw new InvalidDataException(
                $"Strict Capture settings evidence is unavailable ({inspection.Status}).",
                inspection.Error);
        }
        RequireSameCaptureLibraryRoot(inspection.Settings!, _captureSettings);
        return RoutingCaptureLibraryBindingModel.Create(
            inspection.Settings!.LibraryRoot);
    }

    private bool TryInitializeCaptureLibraryPermit(
        CaptureSettingsDocumentInspection inspection)
    {
        try
        {
            var durableBinding = ResolveDurableCaptureLibraryBinding();
            if (inspection.Status == CaptureSettingsDocumentStatus.Missing &&
                durableBinding is null)
            {
                CaptureSettingsStore.Save(_captureSettings);
                inspection = CaptureSettingsStore.Inspect();
            }
            if (!inspection.Loaded)
            {
                throw new InvalidDataException(
                    $"Capture settings cannot authorize library access ({inspection.Status}).",
                    inspection.Error);
            }
            RequireSameCaptureLibraryRoot(inspection.Settings!, _captureSettings);
            var root = CaptureJournalStore.NormalizeLibraryRoot(
                inspection.Settings!.LibraryRoot);
            if (durableBinding is null && !Directory.Exists(root))
            {
                // Missing authority is still the reversible Legacy side. Creating the user's
                // effective default here makes the first Capture permit explicit before any
                // recovery or recorder component is constructed.
                Directory.CreateDirectory(root);
            }
            var acquired = AcquireCaptureLibraryPermit(root, durableBinding);
            _captureLibraryPermit = acquired.Permit;
            _captureLibraryRootPin = acquired.RootPin;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or PathTooLongException or
                System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            Log.Error(
                "ClipCord could not establish a live permit for the Capture library.",
                exception);
            return false;
        }
    }

    private (RoutingCaptureLibraryPermit Permit, RoutingWatchedRootHandle RootPin)
        AcquireCaptureLibraryPermit(
            string libraryRoot,
            RoutingCaptureLibraryBinding? durableBinding = null)
    {
        var pinned = AcquireCaptureLibraryRootPin(libraryRoot);
        try
        {
            var root = pinned.RootPin.CanonicalPath;
            var current = RequireCaptureLibraryBindingForRoot(root);
            var expected = durableBinding ?? current;
            RoutingCaptureLibraryBindingModel.RequireExact(pinned.Binding, current);
            RoutingCaptureLibraryBindingModel.RequireExact(expected, current);
            return (CreateCaptureLibraryPermit(root, expected), pinned.RootPin);
        }
        catch
        {
            pinned.RootPin.Handle.Dispose();
            throw;
        }
    }

    private static (RoutingCaptureLibraryBinding Binding, RoutingWatchedRootHandle RootPin)
        AcquireCaptureLibraryRootPin(string libraryRoot)
    {
        var root = CaptureJournalStore.NormalizeLibraryRoot(libraryRoot);
        var pin = RoutingWatchedFileSystem.OpenOrdinaryRoot(root);
        try
        {
            return (RoutingCaptureLibraryBindingModel.Create(root), pin);
        }
        catch
        {
            pin.Handle.Dispose();
            throw;
        }
    }

    private RoutingCaptureLibraryPermit CreateCaptureLibraryPermit(
        string libraryRoot,
        RoutingCaptureLibraryBinding expectedBinding)
    {
        var root = CaptureJournalStore.NormalizeLibraryRoot(libraryRoot);
        var current = RequireCaptureLibraryBindingForRoot(root);
        RoutingCaptureLibraryBindingModel.RequireExact(expectedBinding, current);
        var permit = new RoutingCaptureLibraryPermit(
            expectedBinding,
            () => RequireAuthorizedCaptureLibraryBinding(root, expectedBinding));
        _ = permit.RequireCurrent("Capture library permit acquisition");
        return permit;
    }

    private RoutingCaptureLibraryBinding RequireCaptureLibraryBindingForRoot(
        string expectedRoot)
    {
        var inspection = CaptureSettingsStore.Inspect();
        if (!inspection.Loaded)
        {
            throw new InvalidDataException(
                $"Strict Capture settings evidence is unavailable ({inspection.Status}).",
                inspection.Error);
        }
        var durableRoot = CaptureJournalStore.NormalizeLibraryRoot(
            inspection.Settings!.LibraryRoot);
        var canonicalExpected = CaptureJournalStore.NormalizeLibraryRoot(expectedRoot);
        if (!durableRoot.Equals(canonicalExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The durable Capture library no longer matches the live permit.");
        }
        return RoutingCaptureLibraryBindingModel.Create(durableRoot);
    }

    private RoutingCaptureLibraryBinding RequireAuthorizedCaptureLibraryBinding(
        string expectedRoot,
        RoutingCaptureLibraryBinding expectedBinding)
    {
        var current = RequireCaptureLibraryBindingForRoot(expectedRoot);
        RoutingCaptureLibraryBindingModel.RequireExact(expectedBinding, current);
        var durable = ResolveDurableCaptureLibraryBinding();
        if (durable is not null)
        {
            RoutingCaptureLibraryBindingModel.RequireExact(expectedBinding, durable);
        }
        return current;
    }

    private RoutingCaptureLibraryBinding? ResolveDurableCaptureLibraryBinding()
    {
        var authority = _routingAuthorityStore.Inspect();
        var markerLoad = _routingMigrationMarkers.Load();
        if (markerLoad.Status is not (RoutingDocumentLoadStatus.Missing or
            RoutingDocumentLoadStatus.Loaded))
        {
            throw new InvalidDataException(
                $"Routing migration evidence cannot authorize Capture ({markerLoad.Status}).");
        }
        var marker = markerLoad.Document;
        if (authority.RoutingRequired)
        {
            var document = authority.Document ?? throw new InvalidDataException(
                "Routing authority is missing its Capture-library binding.");
            if (marker is not { Phase: LegacyRoutingMigrationMarkerPhase.Committed } ||
                marker.MigrationId != document.MigrationId ||
                !marker.PayloadFingerprint.Equals(
                    document.MigrationPayloadFingerprint,
                    StringComparison.Ordinal) ||
                !marker.SourceFingerprint.Equals(
                    document.SourceFingerprint,
                    StringComparison.Ordinal) ||
                marker.CaptureLibraryBinding != document.CaptureLibraryBinding)
            {
                throw new InvalidDataException(
                    "Routing authority does not match its Capture-library migration evidence.");
            }
            return document.CaptureLibraryBinding;
        }
        if (authority.Blocked)
        {
            throw new InvalidDataException(
                $"Routing authority cannot authorize Capture ({authority.LoadStatus}).");
        }
        if (!authority.LegacyPermitted)
        {
            throw new InvalidDataException(
                "Routing authority has an unsupported Capture-library state.");
        }
        return marker?.CaptureLibraryBinding;
    }

    private bool CaptureLibraryAccessAllowed(bool allowRevocationDuringShutdown = false)
    {
        if (Volatile.Read(ref _captureLibrarySwitchInProgress) != 0) return false;
        if (Volatile.Read(ref _captureLibraryRevocationScheduled) != 0) return false;
        var permit = Volatile.Read(ref _captureLibraryPermit);
        if (permit?.Inspect().Allowed == true) return true;
        ScheduleCaptureLibraryRevocation(allowRevocationDuringShutdown);
        return false;
    }

    private void StartCaptureLibraryPermitMonitor()
    {
        if (Volatile.Read(ref _captureLibraryPermit) is null ||
            Volatile.Read(ref _captureLibraryPermitMonitor) is not null)
        {
            return;
        }
        var monitor = new System.Threading.Timer(
            static state => ((TrayApplicationContext)state!).MonitorCaptureLibraryPermit(),
            this,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        if (Interlocked.CompareExchange(
                ref _captureLibraryPermitMonitor,
                monitor,
                null) is not null)
        {
            monitor.Dispose();
        }
    }

    private void MonitorCaptureLibraryPermit()
    {
        if (Volatile.Read(ref _captureLibrarySwitchInProgress) != 0 ||
            Volatile.Read(ref _captureLibraryRevocationScheduled) != 0)
        {
            return;
        }
        _ = CaptureLibraryAccessAllowed(allowRevocationDuringShutdown: true);
    }

    private void StopCaptureLibraryPermitMonitor()
    {
        var monitor = Interlocked.Exchange(ref _captureLibraryPermitMonitor, null);
        if (monitor is null) return;
        monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private RoutingCaptureLibraryPermit RequireCaptureLibraryPermit(string operation)
    {
        var permit = Volatile.Read(ref _captureLibraryPermit) ??
            throw new InvalidDataException(
                "Capture is paused until its library matches durable Routing authority.");
        _ = permit.RequireCurrent(operation);
        return permit;
    }

    private void ScheduleCaptureLibraryRevocation(bool allowDuringShutdown = false)
    {
        lock (_captureLibraryRevocationSync)
        {
            if (_shutdownScheduled && !allowDuringShutdown) return;
            if (Interlocked.Exchange(ref _captureLibraryRevocationScheduled, 1) != 0) return;
            _captureHotkeyAvailable = false;
            try
            {
                Volatile.Read(ref _captureLibraryAuthorityCancellation).Cancel();
            }
            catch (ObjectDisposedException) { }
            _captureLibraryRevocationTask = Task.Run(RevokeCaptureLibraryAccessAsync);
        }
    }

    private async Task RevokeCaptureLibraryAccessAsync()
    {
        var abortTarget = _manualCaptureRecorder as ICaptureLibraryAuthorityAbort ??
            _captureHostClient as ICaptureLibraryAuthorityAbort;
        Task? abortTask = null;
        if (abortTarget is not null)
        {
            try
            {
                // Begin the non-finalizing child kill before any local coordinator can block
                // while winding down. We join/retry it after local mutation sources are detached.
                abortTask = abortTarget.AbortForCaptureLibraryAuthorityRevocationAsync();
            }
            catch (Exception exception)
            {
                abortTask = Task.FromException(exception);
            }
        }
        var coordinator = Interlocked.Exchange(
            ref _silhouetteProcessingCoordinator,
            null);
        if (coordinator is not null)
        {
            try
            {
                coordinator.ProjectSettled -= SilhouetteProjectSettled;
                coordinator.DisposeAndRequireStopped();
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _captureLibraryWorkerStopFailed, 1);
                Log.Error(
                    "ClipCord could not stop local rendition processing after Capture-library authority was revoked.",
                    exception);
            }
        }
        await DrainSilhouetteSettlementsAsync().ConfigureAwait(false);
        if (abortTarget is not null && abortTask is not null)
        {
            Exception? abortFailure = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await abortTask.ConfigureAwait(false);
                    abortFailure = null;
                    break;
                }
                catch (Exception exception)
                {
                    abortFailure = exception;
                    if (attempt == 3) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                    try
                    {
                        abortTask = abortTarget
                            .AbortForCaptureLibraryAuthorityRevocationAsync();
                    }
                    catch (Exception retryException)
                    {
                        abortTask = Task.FromException(retryException);
                    }
                }
            }
            if (abortFailure is not null)
            {
                Log.Error(
                    "ClipCord could not abort its isolated capture pipelines after Capture-library authority was revoked.",
                    abortFailure);
            }
        }
        try
        {
            _uiContext.Post(unused =>
            {
                _ = unused;
                if (_shutdownScheduled) return;
                _ = _globalHotkey.TrySetBinding(
                    GlobalHotkeyManager.CaptureHotkeyIdentifier,
                    binding: null,
                    out _);
                _baseTrayStatus = "Capture library needs attention — capture is paused";
                _statusItem.Text = _baseTrayStatus;
                UpdateTrayCaptureIndicator();
                ShowHotkeyNotification(
                    "Capture library needs attention",
                    "Recording and Capture-library processing are paused. Open Capture to restore the authorized folder, then restart ClipCord.",
                    ToolTipIcon.Warning);
            }, null);
        }
        catch (Exception exception)
        {
            Log.Error(
                "ClipCord could not present its Capture-library revocation status.",
                exception);
        }
    }

    private Task CurrentCaptureLibraryRevocationTask()
    {
        lock (_captureLibraryRevocationSync) return _captureLibraryRevocationTask;
    }

    private static void RequireSameCaptureLibraryRoot(
        CaptureSettings durable,
        CaptureSettings current)
    {
        var durableRoot = CaptureJournalStore.NormalizeLibraryRoot(durable.LibraryRoot);
        var currentRoot = CaptureJournalStore.NormalizeLibraryRoot(current.LibraryRoot);
        if (!durableRoot.Equals(currentRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "In-memory Capture settings do not match their durable library root.");
        }
    }

    private static async Task RequireCaptureRecoveryAsync(
        Task<bool> recoveryTask,
        CancellationToken cancellationToken)
    {
        var recovered = await recoveryTask.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!recovered)
        {
            throw new InvalidOperationException(
                "Capture journal recovery did not complete, so Routes were not started.");
        }
    }

    private Task<bool> CurrentCaptureRecoveryTask()
    {
        lock (_captureRecoverySync) return _captureRecoveryTask;
    }

    private Task<bool> BeginCaptureRecovery(
        string libraryRoot,
        RoutingCaptureLibraryPermit permit,
        CancellationToken authorityCancellation,
        Action? additionalAuthorityCheck = null)
    {
        var recovery = RecoverCaptureJournalsAsync(
            libraryRoot,
            permit,
            authorityCancellation,
            additionalAuthorityCheck);
        lock (_captureRecoverySync) _captureRecoveryTask = recovery;
        return recovery;
    }

    private async Task RequireCurrentCaptureRecoveryAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var recovery = CurrentCaptureRecoveryTask();
            await RequireCaptureRecoveryAsync(recovery, cancellationToken)
                .ConfigureAwait(false);
            lock (_captureRecoverySync)
            {
                if (ReferenceEquals(recovery, _captureRecoveryTask)) return;
            }
        }
    }

    private async Task RecoverRoutingPrerequisitesAsync(
        AppSettings settings,
        bool recoverPendingEditedDispositions,
        CancellationToken cancellationToken)
    {
        await RequireCurrentCaptureRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (recoverPendingEditedDispositions)
        {
            await _pendingEditedRecovery.RecoverAsync(settings, cancellationToken)
                .ConfigureAwait(false);
            await _routingManualEditRecovery.RecoverAsync(
                    settings,
                    cancellationToken,
                    allowMissing: false)
                .ConfigureAwait(false);
            return;
        }
        // Create the Routing-owned manual-edit journal before the first authority commit. A
        // later missing file is therefore ambiguous and must fail closed on cold Routing start.
        _ = await _routingManualEditStateStore.LoadOrInitializeAsync(
                settings.ClipsFolder,
                _ => { },
                cancellationToken,
                settings.CaptureSource)
            .ConfigureAwait(false);
    }

    private AppSettings CurrentAppSettings() =>
        Volatile.Read(ref _settingsBeingApplied) ?? Volatile.Read(ref _settings);

    private RoutingExecutionAuthorityInspection InspectOperationalRoutingAuthority() =>
        TrayRoutingOperationalAuthority.Evaluate(
            _routingAuthorityStore.Inspect(),
            _routingLifecycle.LegacyOperationsPermitted);

    private void ShowFirstRunSettings(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= ShowFirstRunSettings;
        ShowSettings(exitIfCancelled: true);
    }

    private async void ShowSettings(
        bool exitIfCancelled = false,
        SettingsPage initialPage = SettingsPage.Settings)
    {
        if (_settingsOpen)
        {
            if (_settingsForm is { IsDisposed: false, Disposing: false } existingForm)
            {
                existingForm.ShowPage(initialPage);
                existingForm.Activate();
                if (existingForm.WindowState == FormWindowState.Minimized)
                {
                    existingForm.WindowState = FormWindowState.Normal;
                }
            }
            return;
        }
        _settingsOpen = true;
        try
        {
            if (InspectOperationalRoutingAuthority().LegacyPermitted &&
                ShouldStageLegacyDiscordConnection(_settings))
            {
                var imported = await _discordConnectionCatalog.EnsureLegacyConnectionAsync(
                    _settings,
                    cancellationToken: _lifetimeCancellation.Token);
                if (!imported.Succeeded)
                {
                    Log.Error($"Could not stage the existing Discord destination for Routes: {imported.Status}.");
                }
            }
            using var form = new SettingsForm(
                _settings,
                (Icon)_applicationIcon.Clone(),
                CheckForUpdatesManuallyAsync,
                () => _statusItem.Text ?? "Starting…",
                _activityHistory,
                initialPage,
                new ManualClipEditCoordinator(
                    _settings,
                    UploadPreparedEditedClipExclusiveAsync),
                favorites: _favorites,
                captureSettings: _captureSettings,
                captureEngineAvailable: _manualCaptureRecorder is IReplayCaptureController &&
                    CaptureLibraryAccessAllowed(),
                saveCaptureSettings: SaveAndApplyCaptureSettings,
                manualCaptureRecorder: _manualCaptureRecorder,
                discordConnectionCatalog: _discordConnectionCatalog,
                captureLibraryAccessAllowed: () => CaptureLibraryAccessAllowed(),
                repairCaptureLibraryRoot: TryRepairCaptureLibraryRoot,
                routesRuntimeStateProvider: GetRoutesRuntimeViewState,
                retryRoutesRuntimeAsync: RetryRoutesRuntimeFromUiAsync,
                localOnlyMode: _routingLocalOnlyViewSource,
                routingPresentationProvider: GetRoutingUiPresentationSnapshot);
            form.GalleryRenditionRetryRequested += GalleryRenditionRetryRequested;
            _settingsForm = form;
            if (form.ShowDialog() == DialogResult.OK &&
                form.SavedSettings is not null &&
                !_shutdownScheduled)
            {
                await PersistAndApplySettingsAsync(form.SavedSettings);
                if (_shutdownScheduled) return;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "ClipCord",
                    GetSettingsSavedMessage(
                        !InspectOperationalRoutingAuthority().LegacyPermitted,
                        _routingLifecycle.State,
                        _settings.UploadToDiscord),
                    ToolTipIcon.Info);
            }
            else if (exitIfCancelled && !_settings.IsValid)
            {
                ExitThread();
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not save settings.", exception);
            MessageBox.Show(exception.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _settingsForm = null;
            _settingsOpen = false;
        }
    }

    internal static bool ShouldStageLegacyDiscordConnection(AppSettings settings) =>
        settings is { UploadToDiscord: true } &&
        WebhookValidation.IsDiscordWebhook(settings.WebhookUrl);

    private async Task PersistAndApplySettingsAsync(AppSettings updated)
    {
        using var processingMutation = await _processingOperationGate.EnterAsync(
            _lifetimeCancellation.Token);
        await PersistAndApplySettingsCoreAsync(updated);
    }

    private async Task PersistAndApplySettingsCoreAsync(AppSettings updated)
    {
        if (_reconfigurationInProgress)
        {
            throw new InvalidOperationException("ClipCord is already applying another settings change.");
        }

        var previous = _settings;
        var authorityBefore = InspectOperationalRoutingAuthority();
        var settingsGuard = RoutingSettingsAuthorityGuard.Evaluate(
            authorityBefore,
            previous,
            updated);
        if (!settingsGuard.Allowed)
        {
            throw new InvalidOperationException(
                settingsGuard.Status == RoutingSettingsGuardStatus.AuthorityNeedsAttention
                    ? "Routes authority needs attention. ClipCord did not change its watched source or legacy destination."
                    : "Active Routes own the watched folder, clip source, upload mode, and legacy Discord destination. Open Routes to change where new clips go. This settings change was not saved.");
        }
        var persisted = false;
        _reconfigurationInProgress = true;
        _uploadToDiscordItem.Enabled = false;
        Volatile.Write(ref _settingsBeingApplied, updated);
        try
        {
            SettingsStore.Save(updated);
            persisted = true;
            await ApplySettingsAsync(updated);
            Volatile.Write(ref _settings, updated);
        }
        catch (Exception exception)
        {
            if (persisted)
            {
                // A first-run save may have crossed sticky authority before a later startup
                // callback failed. Never roll its exact source settings back underneath the
                // committed marker; Routing remains fenced and the saved candidate is retained.
                if (authorityBefore.LegacyPermitted &&
                    _routingAuthorityStore.Inspect().RoutingRequired)
                {
                    Volatile.Write(ref _settings, updated);
                    SetRoutingNeedsAttention(
                        "Routes need attention — settings were saved",
                        exception);
                    return;
                }
                try
                {
                    Volatile.Write(ref _settingsBeingApplied, previous);
                    SettingsStore.Save(previous);
                    await ApplySettingsAsync(previous);
                    Volatile.Write(ref _settings, previous);
                }
                catch (Exception recoveryException)
                {
                    Log.Error("Could not restore the previous settings after a reconfiguration failure.", recoveryException);
                }
            }
            throw;
        }
        finally
        {
            Volatile.Write(ref _settingsBeingApplied, null);
            _reconfigurationInProgress = false;
            if (!_shutdownScheduled)
            {
                UpdateLegacyModeControl();
                UpdateModeToggleHotkeyDisplay();
            }
            ScheduleDeferredExitIfRequested();
        }
    }

    private async Task ApplySettingsAsync(AppSettings settings)
    {
        if (!TryApplyModeToggleHotkey(settings, out var hotkeyError))
        {
            Log.Error($"Could not register the requested global mode shortcut. Windows error {hotkeyError}.");
            throw new InvalidOperationException(
                $"Windows could not register {AppSettings.NormalizeModeToggleHotkey(settings.ModeToggleHotkey)}. " +
                "It may already be used by another application. Choose a different shortcut.");
        }

        var authority = InspectOperationalRoutingAuthority();
        if (authority.Blocked)
        {
            await StartupManager.ApplyAsync(settings.StartWithWindows);
            SetRoutingNeedsAttention(
                "Routes need attention — processing is paused",
                new InvalidDataException(
                    $"Routing execution authority cannot be trusted ({authority.LoadStatus})."));
            return;
        }
        if (authority.RoutingRequired)
        {
            // Protected source/destination fields were rejected before persistence. Safe fields
            // such as startup and shortcuts do not require a Routing restart.
            await StartupManager.ApplyAsync(settings.StartWithWindows);
            _uiContext.Post(_ => UpdateLegacyModeControl(), null);
            return;
        }

        var hadLegacyController = HasLegacyController;
        if (hadLegacyController)
        {
            SetStatus("Applying settings — stopping current watcher");
            await StopLegacyControllerAsync();
        }

        if (_shutdownScheduled) return;
        await StartupManager.ApplyAsync(settings.StartWithWindows);
        if (_routingLifecycle.State == RoutingApplicationLifecycleState.LegacyReady)
        {
            var sequence = await TrayRoutingStartupSequence.RunAsync(
                routingRequired: false,
                recoveryToken => RecoverRoutingPrerequisitesAsync(
                    CurrentAppSettings(),
                    recoverPendingEditedDispositions: false,
                    recoveryToken),
                _routingLifecycle.StartAsync,
                static result => result.Status ==
                    RoutingApplicationLifecycleStatus.LegacyStarted,
                _routingLifecycle.ActivateRoutingAsync,
                _lifetimeCancellation.Token);
            if (sequence.RecoveryError is not null)
            {
                ReportRoutingActivationDeferred(sequence.RecoveryError);
                return;
            }
            HandleProcessingLifecycleResult(sequence.Result);
            return;
        }
        if (!hadLegacyController)
        {
            throw new InvalidOperationException(
                "Legacy clip processing is not ready to adopt the updated settings.");
        }
        StartControllerWithNewLegacyLease(settings);
    }

    private static async Task ApplyInitialStartupPreferenceAsync(bool enabled)
    {
        try
        {
            await StartupManager.ApplyAsync(enabled);
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not apply the saved startup preference.", exception);
        }
    }

    private async Task<ManualClipEditResult> UploadPreparedEditedClipExclusiveAsync(
        PreparedClipEdit prepared,
        IProgress<ManualClipEditProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        using var processingMutation = await _processingOperationGate.EnterAsync(
            operationCancellation.Token);
        if (_shutdownScheduled)
        {
            throw new OperationCanceledException("ClipCord is shutting down.", cancellationToken);
        }
        if (_reconfigurationInProgress)
        {
            throw new InvalidOperationException("ClipCord is already applying another change.");
        }
        var authority = InspectOperationalRoutingAuthority();
        TrayRoutingExclusiveOperation.RequireManualUploadAllowed(authority);
        var manualSettings = CurrentAppSettings();
        var manualConnection = TrayManualDiscordConnection.Resolve(
            authority,
            manualSettings,
            _discordConnectionCatalog,
            operationCancellation.Token);
        _manualClipOperationCancellation = operationCancellation;
        _reconfigurationInProgress = true;
        _uploadToDiscordItem.Enabled = false;
        var routingRequired = authority.RoutingRequired;
        var legacyControllerWasRunning = authority.LegacyPermitted && HasLegacyController;
        try
        {
            if (legacyControllerWasRunning)
            {
                SetStatus("Preparing manual upload — pausing clip watcher");
                await StopLegacyControllerAsync();
            }
            operationCancellation.Token.ThrowIfCancellationRequested();
            SetStatus("Uploading edited Local-only clip");
            var service = new EditedClipUploadService(
                stateStore: routingRequired ? _routingManualEditStateStore : null,
                dispositionProcessor: new EditedClipDispositionProcessor(favorites: _favorites));
            return await TrayRoutingExclusiveOperation.RunAsync(
                authority,
                QuiesceRoutingAsync,
                token => service.UploadAsync(
                    manualSettings,
                    manualConnection,
                    prepared,
                    _activityHistory,
                    progress,
                    token),
                () => RecoverPendingAndRestartRoutingAsync(CurrentAppSettings()),
                exception =>
                {
                    Log.Error(
                        "The edited clip operation finished, but Routes could not resume.",
                        exception);
                    if (!_shutdownScheduled)
                    {
                        SetRoutingNeedsAttention(
                            "Routes need attention — manual upload recovery is pending",
                            exception);
                    }
                },
                operationCancellation.Token);
        }
        finally
        {
            try
            {
                if (!_shutdownScheduled && !_exitRequestedAfterReconfiguration)
                {
                    try
                    {
                        if (legacyControllerWasRunning &&
                            InspectOperationalRoutingAuthority().LegacyPermitted)
                        {
                            StartControllerWithNewLegacyLease(_settings);
                        }
                    }
                    catch (Exception exception)
                    {
                        // The manual upload result is authoritative. A watcher restart failure
                        // must not turn a confirmed Discord upload into an apparent upload error.
                        Log.Error("The edited clip operation finished, but ClipCord could not restart its watcher.", exception);
                        SetStatus("Watcher restart failed — open Settings to retry");
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(_manualClipOperationCancellation, operationCancellation))
                {
                    _manualClipOperationCancellation = null;
                }
                _reconfigurationInProgress = false;
                if (!_shutdownScheduled)
                {
                    UpdateLegacyModeControl();
                }
                ScheduleDeferredExitIfRequested();
            }
        }
    }

    private async Task QuiesceRoutingAsync(CancellationToken cancellationToken)
    {
        SetStatus("Preparing manual upload — pausing Routes");
        var stopped = await _routingLifecycle.StopAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stopped.State != RoutingApplicationLifecycleState.RoutingQuiesced ||
            stopped.Status is not (RoutingApplicationLifecycleStatus.Stopped or
                RoutingApplicationLifecycleStatus.AlreadyStopped))
        {
            throw new InvalidOperationException(
                "Routes could not be quiesced before the manual upload.",
                stopped.Error ?? stopped.RoutingTransition?.Error);
        }
    }

    private async Task RecoverPendingAndRestartRoutingAsync(AppSettings settings)
    {
        await _routingManualEditRecovery.RecoverAsync(
                settings,
                CancellationToken.None,
                allowMissing: false)
            .ConfigureAwait(false);
        if (_shutdownScheduled || _exitRequestedAfterReconfiguration) return;
        var started = await _routingLifecycle.StartAsync(CancellationToken.None)
            .ConfigureAwait(false);
        HandleProcessingLifecycleResult(started);
        if (started.State != RoutingApplicationLifecycleState.RoutingRunning)
        {
            throw new InvalidOperationException(
                "Routes could not restart after the manual upload.",
                started.Error ?? started.RoutingTransition?.Error);
        }
    }

    private void RequestExit()
    {
        if (TryDeferExitAndCancelManualOperation(
                _reconfigurationInProgress,
                ref _exitRequestedAfterReconfiguration,
                _manualClipOperationCancellation))
        {
            SetStatus("Finishing the current clip operation before exit");
            return;
        }
        _shutdownScheduled = true;
        ExitThread();
    }

    internal static bool TryDeferExitAndCancelManualOperation(
        bool reconfigurationInProgress,
        ref bool exitRequested,
        CancellationTokenSource? manualOperationCancellation)
    {
        if (!reconfigurationInProgress) return false;
        exitRequested = true;
        try { manualOperationCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    private void ScheduleDeferredExitIfRequested()
    {
        if (!_exitRequestedAfterReconfiguration || _shutdownScheduled || _reconfigurationInProgress) return;
        _exitRequestedAfterReconfiguration = false;
        _uiContext.Post(_ => RequestExit(), null);
    }

    private bool HasLegacyController
    {
        get
        {
            lock (_legacyControllerSync) return _controller is not null;
        }
    }

    private async ValueTask StopLegacyControllerAsync()
    {
        DiscordAwareController? controller;
        lock (_legacyControllerSync)
        {
            controller = _controller;
            _controller = null;
        }
        if (controller is not null)
        {
            await controller.StopAsync().ConfigureAwait(false);
        }
    }

    private void StartControllerWithNewLegacyLease(AppSettings settings)
    {
        if (!_processingOwnership.TryAcquire(
                ClipProcessingRuntimeOwner.Legacy,
                out var legacyOwnership) || legacyOwnership is null)
        {
            throw new InvalidOperationException(
                "Another ClipCord pipeline still owns clip processing.");
        }
        try
        {
            StartController(settings, legacyOwnership);
        }
        catch
        {
            legacyOwnership.Dispose();
            throw;
        }
    }

    private void StartController(
        AppSettings settings,
        ClipProcessingOwnershipLease legacyOwnership)
    {
        ArgumentNullException.ThrowIfNull(legacyOwnership);
        if (settings.UploadToDiscord)
        {
            UploadedFolder.GetOrCreate(settings.ClipsFolder);
        }
        else
        {
            UploadedFolder.GetOrCreateLocalOnly(settings.ClipsFolder);
        }
        lock (_legacyControllerSync)
        {
            if (_controller is not null)
            {
                throw new InvalidOperationException(
                    "The legacy clip watcher is already running.");
            }
            _controller = new DiscordAwareController(
                settings,
                SetStatus,
                legacyOwnership,
                _activityHistory,
                _favorites);
        }
        _uiContext.Post(_ => UpdateLegacyModeControl(), null);
    }

    private async void ToggleUploadModeFromTray()
    {
        if (!InspectOperationalRoutingAuthority().LegacyPermitted)
        {
            await ChangeRoutingLocalOnlyModeAsync(_uploadToDiscordItem.Checked);
            return;
        }
        await ChangeUploadModeAsync(_uploadToDiscordItem.Checked, invokedByHotkey: false);
    }

    private async void ModeToggleHotkeyPressed(object? sender, EventArgs eventArgs)
    {
        switch (GetModeHotkeyBlockReason(
                    _shutdownScheduled,
                    _settingsOpen,
                    _updateDialogOpen,
                    _reconfigurationInProgress))
        {
            case ModeHotkeyBlockReason.ShuttingDown:
                return;
            case ModeHotkeyBlockReason.DialogOpen:
                ShowModeFeedback(ModeFeedbackPresentation.DialogOpen);
                return;
            case ModeHotkeyBlockReason.ReconfigurationInProgress:
                ShowModeFeedback(ModeFeedbackPresentation.ReconfigurationInProgress);
                return;
        }

        if (!InspectOperationalRoutingAuthority().LegacyPermitted)
        {
            var result = await _routingLocalOnlyViewSource.ToggleEnabledAsync(
                _lifetimeCancellation.Token);
            if (!result.Succeeded)
            {
                _uploadToDiscordItem.Checked = result.Snapshot.EffectiveEnabled;
                ShowModeFeedback(new ModeFeedbackPresentation(
                    "Could not change Local-only mode",
                    result.Error ?? "ClipCord kept the previous routing mode.",
                    ModeFeedbackTone.Error));
            }
            return;
        }
        await ChangeUploadModeAsync(!_settings.UploadToDiscord, invokedByHotkey: true);
    }

    internal static ModeHotkeyBlockReason GetModeHotkeyBlockReason(
        bool shutdownScheduled,
        bool settingsOpen,
        bool updateDialogOpen,
        bool reconfigurationInProgress)
    {
        if (shutdownScheduled) return ModeHotkeyBlockReason.ShuttingDown;
        if (settingsOpen || updateDialogOpen) return ModeHotkeyBlockReason.DialogOpen;
        return reconfigurationInProgress
            ? ModeHotkeyBlockReason.ReconfigurationInProgress
            : ModeHotkeyBlockReason.None;
    }

    private async Task ChangeUploadModeAsync(bool uploadToDiscord, bool invokedByHotkey)
    {
        if (!InspectOperationalRoutingAuthority().LegacyPermitted) return;
        var previousSettings = _settings;
        var updated = previousSettings with { UploadToDiscord = uploadToDiscord };
        if (!updated.IsValid)
        {
            _uploadToDiscordItem.Checked = previousSettings.UploadToDiscord;
            if (invokedByHotkey)
            {
                ShowModeFeedback(ModeFeedbackPresentation.DiscordSetupRequired);
                return;
            }
            MessageBox.Show(
                "Open Settings and enter a valid Discord webhook before enabling uploads.",
                "Discord setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            ShowSettings();
            return;
        }

        try
        {
            await PersistAndApplySettingsAsync(updated);
            if (_shutdownScheduled) return;
            if (invokedByHotkey)
            {
                ShowModeFeedback(ModeFeedbackPresentation.ForUploadMode(updated.UploadToDiscord));
            }
            else
            {
                ShowHotkeyNotification(
                    "ClipCord",
                    updated.UploadToDiscord
                        ? "Discord uploads enabled. New clips will be sent automatically."
                        : "Local-only mode enabled. New clips will not be sent to Discord.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            _uploadToDiscordItem.Checked = _settings.UploadToDiscord;
            Log.Error("Could not change the clip upload mode.", exception);
            if (invokedByHotkey)
            {
                ShowModeFeedback(ModeFeedbackPresentation.SaveFailed);
                return;
            }
            MessageBox.Show(
                "ClipCord could not save the upload-mode setting.",
                "Could not change upload mode",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool TryApplyModeToggleHotkey(AppSettings settings, out int errorCode)
    {
        return TryApplyModeToggleHotkey(GetEffectiveModeToggleHotkey(settings), out errorCode);
    }

    private bool TryApplyModeToggleHotkey(string hotkeyDisplayText, out int errorCode)
    {
        var normalized = AppSettings.NormalizeModeToggleHotkey(hotkeyDisplayText);
        GlobalHotkeyBinding? binding = null;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            if (!GlobalHotkeyBinding.TryParse(normalized, out var parsed))
            {
                errorCode = 0;
                return false;
            }
            binding = parsed;
        }

        var applied = _globalHotkey.TrySetBinding(binding, out errorCode);
        if (applied) UpdateModeToggleHotkeyDisplay();
        return applied;
    }

    private string GetEffectiveModeToggleHotkey(AppSettings settings)
    {
        if (InspectOperationalRoutingAuthority().LegacyPermitted)
            return AppSettings.NormalizeModeToggleHotkey(settings.ModeToggleHotkey);
        try
        {
            return _routingLocalOnlyState.Inspect().EffectiveHotkeyBinding;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Error("ClipCord could not read the Routing Local-only shortcut.", exception);
            return GlobalHotkeyBinding.DefaultDisplayText;
        }
    }

    private void UpdateModeToggleHotkeyDisplay()
    {
        _uploadToDiscordItem.ShortcutKeyDisplayString =
            GetEffectiveModeToggleHotkey(_settings);
    }

    private void UpdateLegacyModeControl()
    {
        var authority = InspectOperationalRoutingAuthority();
        if (authority.LegacyPermitted)
        {
            _uploadToDiscordItem.Text = "Upload new clips to Discord";
            _uploadToDiscordItem.CheckOnClick = true;
            _uploadToDiscordItem.Checked = _settings.UploadToDiscord;
            _uploadToDiscordItem.Enabled = !_shutdownScheduled && _settings.IsValid;
        }
        else
        {
            var localOnly = _routingLocalOnlyState.Inspect();
            _uploadToDiscordItem.Text = "Local-only mode";
            _uploadToDiscordItem.CheckOnClick = true;
            _uploadToDiscordItem.Checked = localOnly.EffectiveEnabled;
            _uploadToDiscordItem.Enabled = !_shutdownScheduled &&
                _routingLifecycle.State == RoutingApplicationLifecycleState.RoutingRunning &&
                localOnly.LoadedFromDisk;
            _uploadToDiscordItem.ToolTipText = localOnly.LoadedFromDisk
                ? localOnly.EffectiveEnabled
                    ? "Future clips stay on this PC. Existing deliveries are unchanged."
                    : "Future clips follow your active Routes."
                : "Saved Local-only mode needs attention. External delivery remains paused.";
        }
        UpdateModeToggleHotkeyDisplay();
    }

    private void ShowHotkeyNotification(string title, string message, ToolTipIcon icon)
    {
        if (_shutdownScheduled || !_trayIcon.Visible) return;
        _trayIcon.ShowBalloonTip(2500, title, message, icon);
    }

    private void ShowModeFeedback(ModeFeedbackPresentation presentation)
    {
        if (_shutdownScheduled) return;
        _modeFeedbackOverlay.ShowFeedback(presentation);
    }

    internal static bool ConfigureAutomaticUpdateChecks(
        AppUpdateRoute route,
        bool alreadyScheduled,
        Action startTimer,
        Action scheduleOnIdle)
    {
        ArgumentNullException.ThrowIfNull(startTimer);
        ArgumentNullException.ThrowIfNull(scheduleOnIdle);
        if (route != AppUpdateRoute.GitHub) return false;

        startTimer();
        if (alreadyScheduled) return true;

        scheduleOnIdle();
        return true;
    }

    private void StartUpdateChecks()
    {
        _automaticUpdateCheckScheduled = ConfigureAutomaticUpdateChecks(
            AppDistribution.SelectUpdateRoute(AppDistribution.IsPackaged, manual: false),
            _automaticUpdateCheckScheduled,
            _updateTimer.Start,
            () => Application.Idle += CheckForUpdatesOnIdle);
    }

    private async void CheckForUpdatesOnIdle(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= CheckForUpdatesOnIdle;
        _automaticUpdateCheckScheduled = false;
        await CheckForUpdatesAutomaticallyAsync();
    }

    private async void UpdateTimerTick(object? sender, EventArgs eventArgs) =>
        await CheckForUpdatesAutomaticallyAsync();

    private async Task CheckForUpdatesAutomaticallyAsync()
    {
        var cancellationToken = _lifetimeCancellation.Token;
        try
        {
            var result = await _updateCoordinator.CheckAsync(
                manual: false,
                cancellationToken);
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is not null)
            {
                var owner = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Visible);
                PresentAvailableUpdate(result.Release, owner);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error("Automatic update check failed.", exception);
        }
    }

    private async Task CheckForUpdatesManuallyAsync(IWin32Window owner)
    {
        var route = AppDistribution.SelectUpdateRoute(AppDistribution.IsPackaged, manual: true);
        if (route == AppUpdateRoute.MicrosoftStore)
        {
            OpenStoreUpdates(GetUsableOwner(owner));
            return;
        }
        if (route != AppUpdateRoute.GitHub)
        {
            throw new InvalidOperationException("Manual update checks require a trusted update route.");
        }

        var result = await _updateCoordinator.CheckAsync(
            manual: true,
            _lifetimeCancellation.Token);
        var safeOwner = GetUsableOwner(owner);
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Release is not null:
                PresentAvailableUpdate(result.Release, safeOwner);
                break;
            case UpdateCheckStatus.UpToDate:
                MessageBox.Show(
                    safeOwner,
                    "You already have the latest stable release.",
                    "No update available",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            case UpdateCheckStatus.Busy:
                MessageBox.Show(
                    safeOwner,
                    "An update check is already running.",
                    "Update check in progress",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;
            case UpdateCheckStatus.InvalidRelease:
                MessageBox.Show(
                    safeOwner,
                    "The latest release could not be verified safely. No download was opened.",
                    "Release verification failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;
            case UpdateCheckStatus.Failed:
                MessageBox.Show(
                    safeOwner,
                    "GitHub could not be reached. Clip watching and uploads are unaffected.",
                    "Update check unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;
        }
    }

    private static void OpenStoreUpdates(IWin32Window? owner)
    {
        try
        {
            Process.Start(AppDistribution.CreateStoreUpdatesStartInfo());
        }
        catch (Exception exception)
        {
            Log.Error("Could not open Microsoft Store updates.", exception);
            MessageBox.Show(
                owner,
                "Open Microsoft Store, select Library, then choose Get updates.",
                "Could not open Microsoft Store",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    internal static IWin32Window? GetUsableOwner(IWin32Window requestedOwner) =>
        requestedOwner is Control
        {
            IsDisposed: false,
            Disposing: false,
            IsHandleCreated: true,
            Visible: true
        } control
            ? control
            : null;

    private void PresentAvailableUpdate(UpdateRelease release, IWin32Window? owner)
    {
        if (_updateDialogOpen) return;
        _updateDialogOpen = true;
        try
        {
            using var dialog = new UpdateAvailableDialog(release, (Icon)_applicationIcon.Clone());
            dialog.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            if (owner is null) dialog.ShowDialog();
            else dialog.ShowDialog(owner);

            switch (dialog.SelectedAction)
            {
                case UpdateDialogAction.ViewChanges:
                    OpenReleasePage(release.ReleasePageUri, owner);
                    break;
                case UpdateDialogAction.InstallUpdate:
                    DownloadAndInstallUpdate(release, owner);
                    break;
                case UpdateDialogAction.SkipVersion:
                    if (!_updateCoordinator.Skip(release)) ShowPreferenceSaveError(owner);
                    break;
                case UpdateDialogAction.RemindLater:
                    if (!_updateCoordinator.RemindLater(release)) ShowPreferenceSaveError(owner);
                    break;
            }
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    private void DownloadAndInstallUpdate(UpdateRelease release, IWin32Window? owner)
    {
        using var dialog = new UpdateDownloadDialog(
            release,
            _updateDownloadService,
            (Icon)_applicationIcon.Clone(),
            _lifetimeCancellation.Token);
        dialog.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (result != DialogResult.OK || dialog.DownloadedUpdate is null) return;

        PendingUpdateLaunch = new UpdateLaunchRequest(
            release.Version,
            dialog.DownloadedUpdate.InstallerPath,
            release.InstallerSha256);
        // Use the same deferred-exit boundary as the tray Exit command. If a manual
        // clip operation is active, cancellable FFmpeg work stops while any started
        // webhook POST reaches an authoritative result and persists it before setup runs.
        _uiContext.Post(_ => RequestExit(), null);
    }

    private static void OpenReleasePage(Uri releasePageUri, IWin32Window? owner)
    {
        try
        {
            Process.Start(new ProcessStartInfo(releasePageUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Error("Could not open the verified release page.", exception);
            MessageBox.Show(
                owner,
                "Windows could not open the official GitHub release page.",
                "Could not open update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void ShowPreferenceSaveError(IWin32Window? owner) => MessageBox.Show(
        owner,
        "The update preference could not be saved. The application will continue normally.",
        "Could not save update preference",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);

    private void SetStatus(string status)
    {
        _uiContext.Post(_ =>
        {
            _baseTrayStatus = status;
            _statusItem.Text = status;
            UpdateTrayCaptureIndicator();
        }, null);
    }

    private async Task InitializeCaptureSubsystemsAfterRecoveryAsync()
    {
        try
        {
            await RequireCurrentCaptureRecoveryAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            if (_shutdownScheduled || !CaptureLibraryAccessAllowed()) return;

            var coordinator = new SilhouetteProcessingCoordinator(
                _captureSettings.LibraryRoot);
            coordinator.ProjectSettled += SilhouetteProjectSettled;
            if (_shutdownScheduled || !CaptureLibraryAccessAllowed() ||
                Interlocked.CompareExchange(
                    ref _silhouetteProcessingCoordinator,
                    coordinator,
                    null) is not null)
            {
                coordinator.ProjectSettled -= SilhouetteProjectSettled;
                try
                {
                    coordinator.DisposeAndRequireStopped();
                }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref _captureLibraryWorkerStopFailed, 1);
                    ScheduleCaptureLibraryRevocation();
                    Log.Error(
                        "ClipCord retained its Capture-library pin because an unstarted rendition coordinator did not stop.",
                        exception);
                }
            }
            if (_manualCaptureRecorder is not null &&
                !_shutdownScheduled &&
                CaptureLibraryAccessAllowed())
            {
                await WarmCaptureHostAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error(
                "ClipCord could not initialize Capture after journal recovery.",
                exception);
        }
    }

    private async Task ChangeRoutingLocalOnlyModeAsync(bool enabled)
    {
        var result = await _routingLocalOnlyViewSource.SetEnabledAsync(
            enabled,
            _lifetimeCancellation.Token);
        if (result.Succeeded) return;
        _uploadToDiscordItem.Checked = result.Snapshot.EffectiveEnabled;
        ShowModeFeedback(new ModeFeedbackPresentation(
            "Could not change Local-only mode",
            result.Error ?? "ClipCord kept the previous routing mode.",
            ModeFeedbackTone.Error));
    }

    private void RoutingLocalOnlyStateChanged(
        RoutingLocalOnlyOverrideInspection inspection,
        bool notifyTransition)
    {
        _uiContext.Post(_ =>
        {
            if (_shutdownScheduled) return;
            UpdateLegacyModeControl();
            _settingsForm?.RefreshRoutingPresentation();
            if (!notifyTransition) return;
            SetStatus(inspection.EffectiveEnabled
                ? "Local-only mode on · future clips stay here"
                : "Routes active — watching for new clips");
            ShowModeFeedback(
                ModeFeedbackPresentation.ForRoutingLocalOnlyMode(
                    inspection.EffectiveEnabled));
        }, null);
    }

    private async Task WarmCaptureHostAsync()
    {
        if (_captureHostClient is null) return;
        try
        {
            await _captureHostClient.EnsureReadyAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            await StartConfiguredReplayWhenGameAppearsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not warm its isolated capture worker.", exception);
        }
    }

    private async Task StartConfiguredReplayWhenGameAppearsAsync()
    {
        if (_replayStartCoordinator is not null && CaptureLibraryAccessAllowed())
        {
            await _replayStartCoordinator.RequestAsync().ConfigureAwait(false);
        }
    }

    private bool TryRepairCaptureLibraryRoot(string candidateRoot)
    {
        RoutingWatchedRootHandle? candidatePin = null;
        try
        {
            using var processingMutation = _processingOperationGate.EnterSynchronously(
                _lifetimeCancellation.Token);
            var backup = CaptureSettingsStore.CreateBackup();
            var candidate = AcquireCaptureLibraryRootPin(candidateRoot);
            candidatePin = candidate.RootPin;
            var expected = ResolveDurableCaptureLibraryBinding();
            if (expected is null)
            {
                var authority = InspectOperationalRoutingAuthority();
                if (!authority.LegacyPermitted)
                {
                    throw new InvalidOperationException(
                        "Only reversible Legacy mode can select a new Capture library without an existing binding.");
                }
                expected = candidate.Binding;
            }
            else
            {
                RoutingCaptureLibraryBindingModel.RequireExact(expected, candidate.Binding);
            }
            var repaired = RoutingCaptureLibraryRepair.RestoreExactRoot(
                _captureSettings,
                candidateRoot,
                expected,
                CaptureSettingsStore.Save,
                RequireCaptureLibraryBindingForRoot,
                () => CaptureSettingsStore.RestoreBackup(backup));
            _captureSettings = repaired;
            Log.Info(
                "The Capture library setting was restored. Capture remains paused until ClipCord restarts.");
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
                InvalidOperationException or AggregateException or ArgumentException or NotSupportedException or
                PathTooLongException or System.ComponentModel.Win32Exception or
                System.Security.SecurityException)
        {
            Log.Error(
                "ClipCord could not restore the authorized Capture library.",
                exception);
            return false;
        }
        finally
        {
            candidatePin?.Handle.Dispose();
        }
    }

    private void RequireCaptureLibrarySwitchIdle()
    {
        var manualState = _manualCaptureRecorder?.State ?? _manualCaptureState;
        var replayState = _manualCaptureRecorder is IReplayCaptureController replayController
            ? replayController.ReplayStatus.State
            : _replayCaptureState;
        var cameraStatus = _manualCaptureRecorder is IReactionCameraController cameraController
            ? cameraController.ReactionCameraStatus
            : _reactionCameraStatus;
        TrayCaptureLibrarySwitchPolicy.RequireIdle(
            manualState,
            replayState,
            cameraStatus,
            _silhouetteProcessingCoordinator?.IsIdle ?? true);
    }

    private void SaveAndApplyCaptureSettings(CaptureSettings settings)
    {
        using var processingMutation = _processingOperationGate.EnterSynchronously(
            _lifetimeCancellation.Token);
        settings = CaptureSettings.Normalize(settings);
        var previous = _captureSettings;
        var previousRecovery = CurrentCaptureRecoveryTask();
        var libraryRootChanged = !TrayCaptureLibraryRootAuthorityGuard.SameCanonicalPath(
            previous.LibraryRoot,
            settings.LibraryRoot);
        var switchLatched = false;
        if (libraryRootChanged)
        {
            if (Interlocked.CompareExchange(
                    ref _captureLibrarySwitchInProgress,
                    1,
                    0) != 0)
            {
                throw new InvalidOperationException(
                    "Another Capture-library switch is already in progress.");
            }
            switchLatched = true;
        }

        RoutingCaptureLibraryPermit? replacementPermit = null;
        RoutingWatchedRootHandle? replacementPin = null;
        RoutingCaptureLibraryBinding? replacementBinding = null;
        SilhouetteProcessingCoordinator? replacementCoordinator = null;
        CancellationTokenSource? replacementAuthorityCancellation = null;
        CaptureSettingsDocumentBackup? settingsBackup = null;
        CaptureSettings? failClosedCameraSettings = null;
        var settingsPersisted = false;
        try
        {
            RoutingCaptureLibraryPermit currentPermit;
            if (libraryRootChanged)
            {
                if (Volatile.Read(ref _captureLibraryRevocationScheduled) != 0)
                {
                    throw new InvalidOperationException(
                        "Capture-library authority was revoked while the folder switch was starting.");
                }
                currentPermit = RequireCaptureLibraryPermit(
                    "starting a Capture-library switch");
                _captureSubsystemStartupTask.GetAwaiter().GetResult();
                var replayDrain = _replayStartCoordinator?.PauseAndDrainAsync() ??
                    Task.CompletedTask;
                replayDrain.GetAwaiter().GetResult();
                if (!previousRecovery.GetAwaiter().GetResult())
                {
                    throw new InvalidOperationException(
                        "The current Capture library has not recovered safely, so it cannot be switched.");
                }
                _ = currentPermit.RequireCurrent(
                    "checking Capture-library switch idleness");
                RequireCaptureLibrarySwitchIdle();
            }
            else
            {
                if (!CaptureLibraryAccessAllowed())
                {
                    throw new InvalidOperationException(
                        "Capture settings cannot be changed until the Capture library matches durable Routing authority.");
                }
                currentPermit = RequireCaptureLibraryPermit(
                    "changing Capture settings");
            }

            var authorityApprovedChange = TrayCaptureLibraryRootAuthorityGuard.RequireAllowed(
                previous.LibraryRoot,
                settings.LibraryRoot,
                InspectOperationalRoutingAuthority());
            if (authorityApprovedChange != libraryRootChanged)
            {
                throw new InvalidDataException(
                    "The Capture-library switch decision changed while settings were being validated.");
            }
            if (libraryRootChanged && ResolveDurableCaptureLibraryBinding() is not null)
            {
                throw new InvalidOperationException(
                    "The Capture library is already bound to an in-progress Routing migration and cannot be changed.");
            }

            failClosedCameraSettings = previous.IncludeReactionCamera &&
                !settings.IncludeReactionCamera
                    ? previous with { IncludeReactionCamera = false }
                    : null;
            if (failClosedCameraSettings is not null)
            {
                _captureSettings = failClosedCameraSettings;
            }
            if (!TryApplyCaptureHotkey(
                    settings,
                    out var appliedHotkeyAvailable,
                    out var errorCode))
            {
                _captureHotkeyAvailable = appliedHotkeyAvailable;
                throw new InvalidOperationException(
                    $"{settings.SaveHotkey} is already in use. Choose another Instant Replay shortcut.");
            }
            _captureHotkeyAvailable = appliedHotkeyAvailable;

            if (libraryRootChanged)
            {
                settingsBackup = CaptureSettingsStore.CreateBackup();
                var previousCoordinator = Volatile.Read(
                    ref _silhouetteProcessingCoordinator);
                if (previousCoordinator is not null)
                {
                    previousCoordinator.ProjectSettled -= SilhouetteProjectSettled;
                    try
                    {
                        previousCoordinator.DisposeAndRequireStopped();
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _captureLibraryWorkerStopFailed, 1);
                        ScheduleCaptureLibraryRevocation();
                        throw;
                    }
                    if (!ReferenceEquals(
                            Interlocked.CompareExchange(
                                ref _silhouetteProcessingCoordinator,
                                null,
                                previousCoordinator),
                            previousCoordinator))
                    {
                        ScheduleCaptureLibraryRevocation();
                        throw new InvalidOperationException(
                            "Capture rendition ownership changed during the library switch.");
                    }
                }
                DrainSilhouetteSettlementsAsync().GetAwaiter().GetResult();
                _ = currentPermit.RequireCurrent(
                    "detaching Capture-library processing");
                RequireCaptureLibrarySwitchIdle();
                var provisional = AcquireCaptureLibraryRootPin(settings.LibraryRoot);
                replacementPin = provisional.RootPin;
                replacementBinding = provisional.Binding;
                var candidateRoot = replacementPin.CanonicalPath;
                var candidatePermit = new RoutingCaptureLibraryPermit(
                    replacementBinding,
                    () => RoutingCaptureLibraryBindingModel.Create(candidateRoot));
                _ = candidatePermit.RequireCurrent(
                    "starting candidate Capture-library recovery");
                var recovery = BeginCaptureRecovery(
                    settings.LibraryRoot,
                    candidatePermit,
                    Volatile.Read(ref _captureLibraryAuthorityCancellation).Token,
                    () => currentPermit.RequireCurrent(
                        "recovering a candidate Capture library"));
                if (!recovery.GetAwaiter().GetResult())
                {
                    throw new InvalidOperationException(
                        "The new Capture library could not be recovered safely, so it was not activated.");
                }
            }
            CaptureSettingsStore.Save(settings);
            settingsPersisted = true;
            if (libraryRootChanged)
            {
                replacementPermit = CreateCaptureLibraryPermit(
                    settings.LibraryRoot,
                    replacementBinding ?? throw new InvalidOperationException(
                        "The candidate Capture library was not pinned before recovery."));
                replacementAuthorityCancellation = new CancellationTokenSource();
                replacementCoordinator = new SilhouetteProcessingCoordinator(
                    settings.LibraryRoot);
                replacementCoordinator.ProjectSettled += SilhouetteProjectSettled;
            }
            _captureSettings = settings;
            if (libraryRootChanged)
            {
                Volatile.Write(
                    ref _captureLibraryPermit,
                    replacementPermit ?? throw new InvalidOperationException(
                        "The replacement Capture library permit was not established."));
                var oldPin = Interlocked.Exchange(
                    ref _captureLibraryRootPin,
                    replacementPin);
                replacementPin = null;
                var oldAuthorityCancellation = Interlocked.Exchange(
                    ref _captureLibraryAuthorityCancellation,
                    replacementAuthorityCancellation ?? throw new InvalidOperationException(
                        "The replacement Capture-library cancellation boundary was not established."));
                replacementAuthorityCancellation = null;
                try
                {
                    oldAuthorityCancellation.Cancel();
                    oldAuthorityCancellation.Dispose();
                }
                catch (ObjectDisposedException) { }
                if (replacementCoordinator is not null)
                {
                    Volatile.Write(
                        ref _silhouetteProcessingCoordinator,
                        replacementCoordinator);
                    replacementCoordinator = null;
                }
                try { oldPin?.Handle.Dispose(); }
                catch (Exception exception)
                {
                    Log.Error(
                        "ClipCord could not release its previous Capture-library pin.",
                        exception);
                }
            }
            else
            {
                _silhouetteProcessingCoordinator?.UpdateLibraryRoot(settings.LibraryRoot);
            }
        }
        catch
        {
            if (replacementCoordinator is not null)
            {
                replacementCoordinator.ProjectSettled -= SilhouetteProjectSettled;
                try
                {
                    replacementCoordinator.DisposeAndRequireStopped();
                }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref _captureLibraryWorkerStopFailed, 1);
                    if (replacementPin is not null)
                    {
                        lock (_captureLibrarySafetyPinSync)
                        {
                            _captureLibrarySafetyPins.Add(replacementPin);
                        }
                        replacementPin = null;
                    }
                    ScheduleCaptureLibraryRevocation();
                    Log.Error(
                        "ClipCord retained a candidate Capture-library pin because rendition processing did not stop.",
                        exception);
                }
            }
            if (libraryRootChanged)
            {
                lock (_captureRecoverySync) _captureRecoveryTask = previousRecovery;
                replacementPin?.Handle.Dispose();
                if (settingsPersisted)
                {
                    try
                    {
                        if (settingsBackup is null)
                        {
                            throw new InvalidOperationException(
                                "The Capture settings backup is unavailable.");
                        }
                        CaptureSettingsStore.RestoreBackup(settingsBackup);
                    }
                    catch (Exception rollbackException)
                    {
                        Log.Error(
                            "ClipCord could not restore Capture settings after a failed library switch.",
                            rollbackException);
                        ScheduleCaptureLibraryRevocation();
                    }
                }
            }
            _ = TryApplyCaptureHotkey(previous, out var restoredHotkeyAvailable, out _);
            _captureHotkeyAvailable = restoredHotkeyAvailable;
            _captureSettings = failClosedCameraSettings ?? previous;
            if (Volatile.Read(ref _silhouetteProcessingCoordinator) is null &&
                Volatile.Read(ref _captureLibraryPermit) is not null &&
                Volatile.Read(ref _captureLibraryRevocationScheduled) == 0)
            {
                try
                {
                    var restoredCoordinator = new SilhouetteProcessingCoordinator(
                        _captureSettings.LibraryRoot);
                    restoredCoordinator.ProjectSettled += SilhouetteProjectSettled;
                    Volatile.Write(
                        ref _silhouetteProcessingCoordinator,
                        restoredCoordinator);
                }
                catch (Exception restoreException)
                {
                    Log.Error(
                        "ClipCord could not restore local rendition processing after a failed Capture-library switch.",
                        restoreException);
                    ScheduleCaptureLibraryRevocation();
                }
            }
            else
            {
                _silhouetteProcessingCoordinator?.UpdateLibraryRoot(
                    _captureSettings.LibraryRoot);
            }
            throw;
        }
        finally
        {
            replacementAuthorityCancellation?.Dispose();
            if (switchLatched)
            {
                Volatile.Write(ref _captureLibrarySwitchInProgress, 0);
                _replayStartCoordinator?.Resume();
            }
            if (_captureSettings.InstantReplayEnabled &&
                Volatile.Read(ref _captureLibraryRevocationScheduled) == 0)
            {
                _ = StartConfiguredReplayWhenGameAppearsAsync();
            }
        }
    }

    private void CaptureProjectCommitted(
        object? sender,
        CaptureProjectCommittedEventArgs eventArgs)
    {
        if (!CaptureLibraryAccessAllowed()) return;
        try
        {
            TrayCaptureLibraryEventAuthority.RequireCurrentRoot(
                eventArgs.LibraryRoot,
                RequireCaptureLibraryPermit("accepting a completed Capture project"),
                "accepting a completed Capture project");
            _ = _silhouetteProcessingCoordinator?.TryEnqueue(
                eventArgs.LibraryRoot,
                eventArgs.ProjectId);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException or
                System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            if (exception is RoutingCaptureLibraryPermitException)
            {
                ScheduleCaptureLibraryRevocation();
            }
            Log.Error(
                $"ClipCord rejected a completed project outside the authorized Capture library ({eventArgs.ProjectId}).",
                exception);
        }
    }

    private void SilhouetteProjectSettled(
        object? sender,
        SilhouetteProjectSettledEventArgs eventArgs)
    {
        _ = sender;
        TrackSilhouetteSettlement(ReconcileSilhouetteProjectSettledAsync(eventArgs));
    }

    private async Task ReconcileSilhouetteProjectSettledAsync(
        SilhouetteProjectSettledEventArgs eventArgs)
    {
        var permit = Volatile.Read(ref _captureLibraryPermit);
        var authorityCancellation = Volatile.Read(
            ref _captureLibraryAuthorityCancellation).Token;
        using var settlementCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            authorityCancellation);
        try
        {
            if (permit is null)
            {
                throw new InvalidOperationException(
                    "Capture processing is paused until the Capture library matches Routing authority.");
            }
            TrayCaptureLibraryEventAuthority.RequireCurrentRoot(
                eventArgs.LibraryRoot,
                permit,
                "reconciling silhouette outputs");
            void RequireSettlementAuthority() =>
                TrayCaptureLibraryEventAuthority.RequireCurrentRoot(
                    eventArgs.LibraryRoot,
                    permit,
                    "persisting silhouette outputs");
            _ = await CaptureJournalCaptureCommit.ReconcileRenditionsAsync(
                    eventArgs.LibraryRoot,
                    eventArgs.ProjectId,
                    settlementCancellation.Token,
                    processingSettled: true,
                    beforeMutation: RequireSettlementAuthority)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            settlementCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The silhouette rendition state remains durable and will be projected by startup
            // reconciliation. Do not turn an optional post-processing notification into a
            // capture or shutdown failure.
            Log.Error(
                $"ClipCord could not reconcile silhouette outputs for project {eventArgs.ProjectId}.",
                exception);
        }
    }

    private void TrackSilhouetteSettlement(Task settlement)
    {
        lock (_silhouetteSettlementSync) _silhouetteSettlementTasks.Add(settlement);
        _ = settlement.ContinueWith(
            completed =>
            {
                lock (_silhouetteSettlementSync)
                {
                    _silhouetteSettlementTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainSilhouetteSettlementsAsync()
    {
        while (true)
        {
            Task[] active;
            lock (_silhouetteSettlementSync)
            {
                if (_silhouetteSettlementTasks.Count == 0) return;
                active = _silhouetteSettlementTasks.ToArray();
            }
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    private async Task<bool> RecoverCaptureJournalsAsync(
        string libraryRoot,
        RoutingCaptureLibraryPermit permit,
        CancellationToken authorityCancellation,
        Action? additionalAuthorityCheck = null)
    {
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            authorityCancellation);
        var cancellationToken = recoveryCancellation.Token;
        void RequireRecoveryAuthority()
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = permit.RequireCurrent("mutating Capture recovery state");
            additionalAuthorityCheck?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {
            await CaptureJournalStartupRecovery.RecoverAsync(
                    libraryRoot,
                    _captureRecoveryCutoffUtc,
                    cancellationToken,
                    RequireRecoveryAuthority)
                .ConfigureAwait(false);
            // Promotion recovery must run first: an old staged MP4 can still be owned by a
            // durable move-before-journal intent and must not be mistaken for an orphan.
            var removed = CaptureStagingRecovery.RemoveOrphanedManualCaptures(
                libraryRoot,
                cancellationToken: cancellationToken,
                beforeDelete: RequireRecoveryAuthority);
            if (removed > 0)
            {
                Log.Info($"Removed {removed} abandoned capture staging file(s).");
            }
            var recoveredCameraProjects =
                CaptureStagingRecovery.RemoveOrphanedReactionCameraProjects(
                    libraryRoot,
                    cancellationToken: cancellationToken,
                    beforeDelete: RequireRecoveryAuthority);
            if (recoveredCameraProjects > 0)
            {
                Log.Info(
                    $"Removed {recoveredCameraProjects} abandoned temporary camera project(s).");
            }
            var removedOrphanedCameraProjects = CaptureProjectStore.RemoveOrphanedProjects(
                libraryRoot,
                cancellationToken: cancellationToken,
                beforeDelete: RequireRecoveryAuthority);
            if (removedOrphanedCameraProjects > 0)
            {
                Log.Info(
                    $"Removed {removedOrphanedCameraProjects} camera project(s) whose gameplay clip was gone.");
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not finish capture-journal startup recovery.", exception);
            return false;
        }
    }

    private async void GalleryRenditionRetryRequested(
        object? sender,
        GalleryRenditionRetryRequestedEventArgs eventArgs)
    {
        try
        {
            var coordinator = _silhouetteProcessingCoordinator;
            if (coordinator is null || !CaptureLibraryAccessAllowed())
            {
                throw new InvalidOperationException(
                    "Capture processing is paused until the Capture library matches Routing authority.");
            }
            TrayCaptureLibraryEventAuthority.RequireCurrentRoot(
                eventArgs.LibraryRoot,
                RequireCaptureLibraryPermit("retrying a silhouette rendition"),
                "retrying a silhouette rendition");
            var scheduled = await coordinator.RetryAsync(
                eventArgs.LibraryRoot,
                eventArgs.ProjectId,
                eventArgs.OrientationId,
                _lifetimeCancellation.Token);
            if (!scheduled && !_shutdownScheduled)
            {
                Log.Info(
                    $"The {GalleryCatalog.GetOrientationName(eventArgs.OrientationId)} silhouette retry " +
                    $"for project {eventArgs.ProjectId} was no longer eligible or was already active.");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error(
                $"ClipCord could not retry the {eventArgs.OrientationId} silhouette rendition.",
                exception);
            if (!_shutdownScheduled)
            {
                ShowHotkeyNotification(
                    "Rendition retry unavailable",
                    "ClipCord could not restart that local rendition. Refresh Gallery and try again.",
                    ToolTipIcon.Warning);
            }
        }
    }

    private bool TryApplyCaptureHotkey(
        CaptureSettings settings,
        out bool hotkeyAvailable,
        out int errorCode) =>
        CaptureHotkeyRegistration.TryApply(
            _globalHotkey,
            settings,
            out hotkeyAvailable,
            out errorCode);

    private void GlobalHotkeyPressed(object? sender, GlobalHotkeyPressedEventArgs eventArgs)
    {
        if (eventArgs.Identifier != GlobalHotkeyManager.CaptureHotkeyIdentifier) return;
        _ = SaveReplayFromHotkeyAsync();
    }

    private async Task SaveReplayFromHotkeyAsync()
    {
        if (_shutdownScheduled ||
            Interlocked.Exchange(ref _replaySaveInProgress, 1) != 0)
        {
            return;
        }
        try
        {
            if (!CaptureLibraryAccessAllowed())
            {
                ShowHotkeyNotification(
                    "Capture library needs attention",
                    "No clip was saved. Restore the authorized Capture folder and restart ClipCord.",
                    ToolTipIcon.Warning);
                return;
            }
            _ = RequireCaptureLibraryPermit("saving an Instant Replay clip");
            var replayController = _manualCaptureRecorder as IReplayCaptureController;
            var status = replayController?.ReplayStatus;
            var availability = status is null
                ? ReplaySaveAvailability.Off
                : ReplayCapturePolicy.ClassifySaveAvailability(
                    _captureSettings.InstantReplayEnabled,
                    status);
            if (availability != ReplaySaveAvailability.Ready)
            {
                var presentation = availability switch
                {
                    ReplaySaveAvailability.WaitingForGame => (
                        "Waiting for a game",
                        "No clip was saved. ClipCord will begin buffering automatically after it detects a supported game.",
                        ToolTipIcon.Info),
                    ReplaySaveAvailability.NeedsAttention => (
                        "Instant Replay needs attention",
                        "No clip was saved. Open Capture to review the recorder status, then restart Instant Replay.",
                        ToolTipIcon.Warning),
                    _ => (
                        "Instant Replay is off",
                        "Enable Instant Replay in Capture before using the save shortcut.",
                        ToolTipIcon.Info)
                };
                ShowHotkeyNotification(
                    presentation.Item1,
                    presentation.Item2,
                    presentation.Item3);
                return;
            }
            var result = await replayController!.SaveReplayAsync(_lifetimeCancellation.Token);
            if (result is not null)
            {
                ShowModeFeedback(ModeFeedbackPresentation.ForCapturedClip(
                    result.GameName,
                    result.Duration));
                if (!string.IsNullOrWhiteSpace(result.ReactionCameraWarning))
                {
                    ShowHotkeyNotification(
                        "Gameplay clip saved",
                        "The gameplay clip was saved, but its optional Reaction Camera layer may be missing or incomplete.",
                        ToolTipIcon.Warning);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not save the Instant Replay hotkey snapshot.", exception);
            ShowModeFeedback(ModeFeedbackPresentation.CaptureFailed);
        }
        finally
        {
            Volatile.Write(ref _replaySaveInProgress, 0);
        }
    }

    private void ReplayCaptureStateChanged(object? sender, EventArgs eventArgs)
    {
        _uiContext.Post(_ =>
        {
            if (_shutdownScheduled || sender is not IReplayCaptureController replayController) return;
            var previous = _replayCaptureState;
            var replayStatus = replayController.ReplayStatus;
            _replayCaptureState = replayStatus.State;
            UpdateTrayCaptureIndicator();
            if (_replayCaptureState == ReplayCaptureState.Buffering &&
                previous == ReplayCaptureState.Starting)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureStarted);
            }
            else if (ReplayCapturePolicy.ShouldAttemptAutomaticStart(
                         _captureSettings.InstantReplayEnabled,
                         _captureHotkeyAvailable,
                         replayStatus))
            {
                _ = StartConfiguredReplayWhenGameAppearsAsync();
            }
            else if (_replayCaptureState == ReplayCaptureState.Failed)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureFailed);
            }
        }, null);
    }

    private void ManualCaptureRecorderStateChanged(object? sender, EventArgs eventArgs)
    {
        _uiContext.Post(_ =>
        {
            if (_shutdownScheduled || _manualCaptureRecorder is null) return;
            var previous = _manualCaptureState;
            _manualCaptureState = _manualCaptureRecorder.State;
            UpdateTrayCaptureIndicator();

            if (_manualCaptureState == ManualCaptureState.Recording &&
                previous != ManualCaptureState.Recording)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureStarted);
            }
            else if (_manualCaptureState == ManualCaptureState.Ready &&
                     previous == ManualCaptureState.Finalizing)
            {
                ShowModeFeedback(ModeFeedbackPresentation.ForCapturedClip(
                    _manualCaptureRecorder.Target?.DisplayName,
                    TimeSpan.Zero));
            }
            else if (_manualCaptureState == ManualCaptureState.Failed)
            {
                ShowModeFeedback(ModeFeedbackPresentation.CaptureFailed);
            }
        }, null);
    }

    private void ReactionCameraStateChanged(object? sender, EventArgs eventArgs)
    {
        _uiContext.Post(_ =>
        {
            if (_shutdownScheduled || sender is not IReactionCameraController reactionCameraController)
            {
                return;
            }
            _reactionCameraStatus = reactionCameraController.ReactionCameraStatus;
            UpdateTrayCaptureIndicator();
        }, null);
    }

    private async Task DisableReactionCameraFromTrayAsync()
    {
        if (_shutdownScheduled ||
            _manualCaptureRecorder is not IReactionCameraController reactionCameraController)
        {
            return;
        }
        var currentCameraStatus = reactionCameraController.ReactionCameraStatus;
        if (currentCameraStatus.ReleaseNeedsAttention)
        {
            RequestExit();
            return;
        }
        if (!(currentCameraStatus.IsActive || currentCameraStatus.IsStarting) ||
            Interlocked.Exchange(ref _reactionCameraDisableInProgress, 1) != 0)
        {
            return;
        }

        Exception? preferenceFailure = null;
        Exception? runtimeFailure = null;
        try
        {
            var disabledSettings = CaptureSettings.Normalize(_captureSettings with
            {
                IncludeReactionCamera = false
            });
            // Fail closed in memory even if the settings file cannot be updated. A later
            // automatically detected game in this process must not reopen a camera the user
            // explicitly turned off.
            _captureSettings = disabledSettings;
            _settingsForm?.ApplyExternalCaptureSettings(disabledSettings);
            try
            {
                CaptureSettingsStore.Save(disabledSettings);
            }
            catch (Exception exception)
            {
                preferenceFailure = exception;
                Log.Error(
                    "ClipCord could not save the Reaction Camera off preference from the tray.",
                    exception);
            }

            try
            {
                await reactionCameraController.DisableReactionCameraAsync(
                    _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                runtimeFailure = exception;
                Log.Error("ClipCord could not turn Reaction Camera off from the tray.", exception);
            }

            if (_shutdownScheduled) return;
            _reactionCameraStatus = reactionCameraController.ReactionCameraStatus;
            UpdateTrayCaptureIndicator();
            if (preferenceFailure is not null ||
                runtimeFailure is not null ||
                _reactionCameraStatus.ReleaseNeedsAttention)
            {
                ShowHotkeyNotification(
                    "Reaction Camera needs attention",
                    _reactionCameraStatus.ReleaseNeedsAttention
                        ? "The camera stopped accepting new frames, but Windows has not confirmed release. Exit ClipCord from the tray to guarantee the device closes; gameplay capture continues."
                        : _reactionCameraStatus.IsActive
                        ? "Reaction Camera may still be active. Gameplay capture continues; open Capture to review its status."
                        : "Reaction Camera is not currently active, but ClipCord could not save every part of the off request. Review Capture before the next game.",
                    ToolTipIcon.Warning);
            }
        }
        finally
        {
            Volatile.Write(ref _reactionCameraDisableInProgress, 0);
        }
    }

    private void UpdateTrayCaptureIndicator()
    {
        var captureText = _replayCaptureState switch
        {
            ReplayCaptureState.Starting => "ClipCord — Starting Instant Replay",
            ReplayCaptureState.Buffering => "ClipCord — Instant Replay active",
            ReplayCaptureState.Saving => "ClipCord — Saving replay",
            ReplayCaptureState.Stopping => "ClipCord — Stopping replay",
            ReplayCaptureState.Off when _captureSettings.InstantReplayEnabled =>
                "ClipCord — Waiting for a game",
            ReplayCaptureState.Failed when _captureSettings.InstantReplayEnabled =>
                "ClipCord — Waiting for a game",
            _ => _manualCaptureState switch
            {
            ManualCaptureState.Starting => "ClipCord — Starting capture",
            ManualCaptureState.Recording => "ClipCord — Recording game",
            ManualCaptureState.Finalizing => "ClipCord — Saving recording",
                _ => _baseTrayStatus
            }
        };
        var cameraIndicatorState = ReactionCameraTrayIconFactory.SelectState(_reactionCameraStatus);
        var cameraActive = cameraIndicatorState == ReactionCameraRuntimeState.Active;
        var cameraStarting = cameraIndicatorState == ReactionCameraRuntimeState.Starting;
        var cameraNeedsAttention =
            cameraIndicatorState == ReactionCameraRuntimeState.ReleaseNeedsAttention;
        var cameraInUse = cameraActive || cameraStarting || cameraNeedsAttention;
        var desiredIcon = cameraActive
            ? _reactionCameraActiveIcon
            : cameraStarting
                ? _reactionCameraStartingIcon
                : cameraNeedsAttention
                    ? _reactionCameraAttentionIcon
                    : _applicationIcon;
        if (!ReferenceEquals(_trayIcon.Icon, desiredIcon)) _trayIcon.Icon = desiredIcon;
        _disableReactionCameraItem.Visible = cameraInUse;
        _disableReactionCameraItem.Enabled = cameraInUse;
        _disableReactionCameraItem.Text = cameraNeedsAttention
            ? "Exit ClipCord to release Reaction Camera"
            : "Turn Reaction Camera off";
        _statusItem.Text = cameraNeedsAttention
            ? "Reaction Camera release needs attention"
            : cameraActive
            ? $"{_baseTrayStatus} · Reaction Camera active"
            : cameraStarting
                ? $"{_baseTrayStatus} · Reaction Camera starting"
                : _baseTrayStatus;

        var text = captureText;
        if (cameraNeedsAttention)
        {
            text = "ClipCord — Camera stopping; exit to force release";
        }
        else if (cameraActive)
        {
            const string clipCordPrefix = "ClipCord — ";
            const string cameraActivePrefix = "ClipCord — Reaction Camera active";
            const string separator = " · ";
            var context = captureText.StartsWith(clipCordPrefix, StringComparison.Ordinal)
                ? captureText[clipCordPrefix.Length..]
                : captureText;
            var maximumContextLength = 63 - cameraActivePrefix.Length - separator.Length;
            if (context.Length > maximumContextLength)
            {
                context = context[..maximumContextLength];
            }
            text = $"{cameraActivePrefix}{separator}{context}";
        }
        else if (cameraStarting)
        {
            const string clipCordPrefix = "ClipCord — ";
            const string cameraStartingPrefix = "ClipCord — Reaction Camera starting";
            const string separator = " · ";
            var context = captureText.StartsWith(clipCordPrefix, StringComparison.Ordinal)
                ? captureText[clipCordPrefix.Length..]
                : captureText;
            var maximumContextLength = 63 - cameraStartingPrefix.Length - separator.Length;
            if (context.Length > maximumContextLength)
            {
                context = context[..maximumContextLength];
            }
            text = $"{cameraStartingPrefix}{separator}{context}";
        }
        _trayIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void OpenClipsFolder()
    {
        if (!_settings.IsValid) return;
        Process.Start(new ProcessStartInfo("explorer.exe", _settings.ClipsFolder) { UseShellExecute = true });
    }

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            using var extracted = Icon.ExtractAssociatedIcon(executablePath);
            if (extracted is not null) return (Icon)extracted.Clone();
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    protected override void ExitThreadCore()
    {
        var captureRootSafeToRelease = true;
        try
        {
            _ = CaptureLibraryAccessAllowed(allowRevocationDuringShutdown: true);
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not verify Capture-library authority at shutdown.", exception);
        }
        _shutdownScheduled = true;
        Application.Idle -= CheckForUpdatesOnIdle;
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _lifetimeCancellation.Cancel();
        try
        {
            var stopped = _routingLifecycle.StopAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            if (stopped.Status == RoutingApplicationLifecycleStatus.NeedsAttention)
            {
                Log.Error(
                    "Clip processing needed attention while ClipCord was shutting down.",
                    stopped.Error ?? new InvalidOperationException(
                        "The processing lifecycle did not confirm a clean stop."));
            }
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not stop its processing lifecycle cleanly.", exception);
        }
        try
        {
            _captureSubsystemStartupTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error(
                "ClipCord could not finish Capture subsystem startup during shutdown.",
                exception);
        }
        var shutdownSilhouetteCoordinator = Interlocked.Exchange(
            ref _silhouetteProcessingCoordinator,
            null);
        if (shutdownSilhouetteCoordinator is not null)
        {
            shutdownSilhouetteCoordinator.ProjectSettled -= SilhouetteProjectSettled;
            try
            {
                shutdownSilhouetteCoordinator.DisposeAndRequireStopped();
            }
            catch (Exception exception)
            {
                captureRootSafeToRelease = false;
                Log.Error(
                    "ClipCord retained its Capture-library pin because rendition processing did not stop.",
                    exception);
            }
        }
        TrayShutdownFallbacks.Run(
            static (message, exception) => Log.Error(message, exception),
            (
                "ClipCord could not finish Capture-library revocation during shutdown.",
                () => CurrentCaptureLibraryRevocationTask().GetAwaiter().GetResult()),
            (
                "ClipCord could not stop the active Routing work session cleanly.",
                () => StopRoutingWorkSessionAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult()),
            (
                "ClipCord could not stop the legacy clip watcher cleanly.",
                () => StopLegacyControllerAsync().AsTask().GetAwaiter().GetResult()),
            (
                "ClipCord could not finish capture recovery during shutdown.",
                () => CurrentCaptureRecoveryTask().GetAwaiter().GetResult()),
            (
                "ClipCord could not finish silhouette journal settlement during shutdown.",
                () => DrainSilhouetteSettlementsAsync().GetAwaiter().GetResult()),
            (
                "ClipCord could not finish observing processing startup during shutdown.",
                () => _processingStartupTask?.GetAwaiter().GetResult()),
            (
                "ClipCord could not finish observing the Routing work session during shutdown.",
                () => _routingSessionObserver?.GetAwaiter().GetResult()));
        _globalHotkey.Pressed -= ModeToggleHotkeyPressed;
        _globalHotkey.HotkeyPressed -= GlobalHotkeyPressed;
        _globalHotkey.Dispose();
        _modeFeedbackOverlay.Dispose();
        if (_captureProjectCompletionSource is not null)
        {
            _captureProjectCompletionSource.ProjectCommitted -= CaptureProjectCommitted;
        }
        if (_manualCaptureRecorder is not null)
        {
            _manualCaptureRecorder.StateChanged -= ManualCaptureRecorderStateChanged;
            if (_manualCaptureRecorder is IReplayCaptureController replayController)
            {
                replayController.ReplayStateChanged -= ReplayCaptureStateChanged;
            }
            if (_manualCaptureRecorder is IReactionCameraController reactionCameraController)
            {
                reactionCameraController.ReactionCameraStateChanged -= ReactionCameraStateChanged;
            }
        }
        try
        {
            // Drain the continuous monitor first, then establish one final serialized authority
            // decision. This prevents a queued revocation from racing ordinary host Dispose and
            // its normal Shutdown command.
            StopCaptureLibraryPermitMonitor();
            _ = CaptureLibraryAccessAllowed(allowRevocationDuringShutdown: true);
            CurrentCaptureLibraryRevocationTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error(
                "ClipCord could not enforce final Capture-library authority before recorder shutdown.",
                exception);
        }
        _manualCaptureRecorder?.Dispose();
        _captureHostClient?.Dispose();
        try
        {
            CurrentCaptureLibraryRevocationTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error(
                "ClipCord could not finish Capture-library authority monitoring at shutdown.",
                exception);
        }
        if (captureRootSafeToRelease &&
            Volatile.Read(ref _captureLibraryWorkerStopFailed) == 0)
        {
            Interlocked.Exchange(ref _captureLibraryRootPin, null)?.Handle.Dispose();
        }
        _updateCoordinator.Dispose();
        _updateDownloadService.Dispose();
        _activityHistory.Dispose();
        try
        {
            var authorityCancellation = Volatile.Read(
                ref _captureLibraryAuthorityCancellation);
            authorityCancellation.Cancel();
            authorityCancellation.Dispose();
        }
        catch (ObjectDisposedException) { }
        _lifetimeCancellation.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _reactionCameraAttentionIcon.Dispose();
        _reactionCameraActiveIcon.Dispose();
        _reactionCameraStartingIcon.Dispose();
        _applicationIcon.Dispose();
        base.ExitThreadCore();
    }

    internal static string GetSettingsSavedMessage(
        bool routingRequired,
        RoutingApplicationLifecycleState lifecycleState,
        bool uploadToDiscord)
    {
        if (routingRequired)
        {
            return lifecycleState == RoutingApplicationLifecycleState.RoutingRunning
                ? "Settings saved. Active Routes continue processing new clips."
                : "Settings saved, but Routes need attention before clip processing can continue.";
        }

        return uploadToDiscord
            ? "Settings saved. New clips will upload to Discord."
            : "Settings saved. Local-only mode will keep new clips on this PC.";
    }

    private sealed class TrayLegacyClipProcessingRuntime(TrayApplicationContext owner)
        : ILegacyClipProcessingRuntime
    {
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return owner.StopLegacyControllerAsync();
        }

        public ValueTask StartAsync(
            ClipProcessingOwnershipLease ownership,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner._shutdownScheduled)
            {
                throw new OperationCanceledException(
                    "ClipCord is shutting down.", cancellationToken);
            }
            owner.StartController(owner.CurrentAppSettings(), ownership);
            return ValueTask.CompletedTask;
        }
    }
}
