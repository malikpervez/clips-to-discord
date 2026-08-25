using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClipsToDiscord;

internal sealed record CaptureHostStatus(int ProtocolVersion, int ProcessId, string EngineState);

internal sealed record CaptureHostRecorderSnapshot(
    ManualCaptureState State,
    ManualCaptureTarget? Target,
    string? LastError,
    ManualCaptureResult? Result = null,
    ReplayCaptureStatus? ReplayStatus = null,
    ReactionCameraRuntimeStatus? ReactionCameraStatus = null);

internal sealed record CaptureHostMessage(
    int ProtocolVersion,
    string Type,
    long RequestId,
    bool Ok = true,
    int HostProcessId = 0,
    string? Error = null,
    long OwnerWindowHandle = 0,
    CaptureSettings? Settings = null,
    ManualCaptureState RecorderState = ManualCaptureState.NoTarget,
    ManualCaptureTarget? Target = null,
    ManualCaptureResult? Result = null,
    string? RecorderError = null,
    ReplayCaptureState ReplayState = ReplayCaptureState.Off,
    ManualCaptureTarget? ReplayTarget = null,
    string? ReplayError = null,
    long ReplayBufferedMilliseconds = 0,
    long ReplayResidentBytes = 0,
    bool ManualReactionCameraActive = false,
    string? ManualReactionCameraError = null,
    bool ReplayReactionCameraActive = false,
    string? ReplayReactionCameraError = null,
    long ReplayReactionCameraResidentBytes = 0,
    bool ManualReactionCameraStarting = false,
    bool ReplayReactionCameraStarting = false,
    bool ManualReactionCameraReleaseNeedsAttention = false,
    bool ReplayReactionCameraReleaseNeedsAttention = false,
    long CommandSequence = 0,
    long AppliedReactionCameraOffSequence = 0);

internal static class CaptureHostProtocol
{
    internal const int Version = 8;
    internal const int MaximumMessageCharacters = 32_768;
    internal const string Hello = "hello";
    internal const string Ping = "ping";
    internal const string Status = "status";
    internal const string SelectTarget = "selectTarget";
    internal const string DetectTarget = "detectTarget";
    internal const string StartRecording = "startRecording";
    internal const string StopRecording = "stopRecording";
    internal const string StartReplay = "startReplay";
    internal const string StopReplay = "stopReplay";
    internal const string SaveReplay = "saveReplay";
    internal const string DisableReactionCamera = "disableReactionCamera";
    internal const string Shutdown = "shutdown";
    internal const string Response = "response";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static string Serialize(CaptureHostMessage message) =>
        JsonSerializer.Serialize(message, JsonOptions);

    internal static CaptureHostMessage Deserialize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaximumMessageCharacters)
        {
            throw new InvalidDataException("The capture-host message was empty or oversized.");
        }
        return JsonSerializer.Deserialize<CaptureHostMessage>(payload, JsonOptions) ??
            throw new InvalidDataException("The capture-host message was invalid.");
    }
}

internal static class CaptureHostCommandRouting
{
    internal static bool UsesUrgentChannel(string requestType) =>
        requestType.Equals(CaptureHostProtocol.DisableReactionCamera, StringComparison.Ordinal);
}

internal enum CaptureHostUrgentChannelAction
{
    StartHost,
    Send,
    FailClosed
}

internal static class CaptureHostUrgentChannelPolicy
{
    internal static CaptureHostUrgentChannelAction GetAction(
        bool hostProcessRunning,
        bool mainChannelConnected,
        bool urgentChannelConnected)
    {
        _ = mainChannelConnected;
        if (hostProcessRunning)
        {
            return urgentChannelConnected
                ? CaptureHostUrgentChannelAction.Send
                : CaptureHostUrgentChannelAction.FailClosed;
        }
        return CaptureHostUrgentChannelAction.StartHost;
    }
}

/// <summary>
/// Owns one ordered request/response stream. The capture host deliberately uses two instances:
/// ordinary capture commands may take minutes, while the privacy-critical camera-off stream must
/// remain independently writable and readable.
/// </summary>
internal sealed class CaptureHostCommandChannel : IDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _expectedHostProcessId;
    private long _requestId;
    private bool _disposed;

    internal CaptureHostCommandChannel(Stream stream, int expectedHostProcessId)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (expectedHostProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedHostProcessId));
        }
        _expectedHostProcessId = expectedHostProcessId;
        _reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    internal bool IsConnected => !_disposed &&
        (_stream is not PipeStream pipe || pipe.IsConnected);

    internal async Task<CaptureHostMessage> SendAsync(
        CaptureHostMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool honorCallerCancellationAfterAdmission = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var commandCancellation = honorCallerCancellationAfterAdmission
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellation.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(timeoutCancellation.Token);
            try
            {
                var requestId = Interlocked.Increment(ref _requestId);
                request = request with { RequestId = requestId };
                await _writer.WriteLineAsync(
                    CaptureHostProtocol.Serialize(request).AsMemory(),
                    commandCancellation.Token).ConfigureAwait(false);
                var payload = await _reader.ReadLineAsync(commandCancellation.Token)
                    .ConfigureAwait(false) ??
                    throw new IOException("The ClipCord capture host closed its control channel.");
                var response = CaptureHostProtocol.Deserialize(payload);
                if (response.ProtocolVersion != CaptureHostProtocol.Version ||
                    response.Type != CaptureHostProtocol.Response ||
                    response.RequestId != requestId ||
                    response.HostProcessId != _expectedHostProcessId)
                {
                    throw new InvalidDataException(
                        "The ClipCord capture host returned an invalid response.");
                }
                if (!response.Ok)
                {
                    throw new CaptureHostCommandException(
                        response.Error ?? "The isolated capture worker rejected the request.",
                        response);
                }
                return response;
            }
            catch (OperationCanceledException exception) when (
                !honorCallerCancellationAfterAdmission ||
                !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The ClipCord capture host did not answer in time.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
        _reader.Dispose();
        _stream.Dispose();
        // A fail-closed transport cleanup can dispose this channel while an admitted command is
        // unwinding. Keep the tiny semaphore for GC so that command's finally can still release it.
    }
}

internal static class CaptureHostMessageMapper
{
    internal static CaptureHostRecorderSnapshot ToRecorderSnapshot(CaptureHostMessage response) =>
        new(
            response.RecorderState,
            response.Target,
            response.RecorderError,
            response.Result,
            new ReplayCaptureStatus(
                response.ReplayState,
                response.ReplayTarget,
                response.ReplayError,
                TimeSpan.FromMilliseconds(Math.Max(0, response.ReplayBufferedMilliseconds)),
                Math.Max(0, response.ReplayResidentBytes),
                response.Result,
                response.ReplayReactionCameraActive,
                response.ReplayReactionCameraError,
                Math.Max(0, response.ReplayReactionCameraResidentBytes),
                response.ReplayReactionCameraStarting,
                response.ReplayReactionCameraReleaseNeedsAttention),
            new ReactionCameraRuntimeStatus(
                response.ManualReactionCameraActive,
                response.ReplayReactionCameraActive,
                response.ManualReactionCameraError ?? response.ReplayReactionCameraError,
                response.ManualReactionCameraStarting,
                response.ReplayReactionCameraStarting,
                response.ManualReactionCameraReleaseNeedsAttention ||
                    response.ReplayReactionCameraReleaseNeedsAttention));
}

internal interface ICaptureHostRecorderClient
{
    Task<CaptureHostRecorderSnapshot> GetRecorderStatusAsync(
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> SelectTargetAsync(
        nint ownerWindowHandle,
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> DetectTargetAsync(
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> StartRecordingAsync(
        CaptureSettings settings,
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> StopRecordingAsync(
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> StartReplayAsync(
        CaptureSettings settings,
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> StopReplayAsync(
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> SaveReplayAsync(
        CancellationToken cancellationToken = default);
    Task<CaptureHostRecorderSnapshot> DisableReactionCameraAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class CaptureHostCommandException : InvalidOperationException
{
    internal CaptureHostCommandException(string message, CaptureHostMessage response)
        : base(message)
    {
        Snapshot = CaptureHostMessageMapper.ToRecorderSnapshot(response);
    }

    internal CaptureHostRecorderSnapshot Snapshot { get; }
}

internal sealed class CaptureHostClient : ICaptureHostRecorderClient, IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);
    private readonly string _executablePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private CaptureHostCommandChannel? _mainChannel;
    private CaptureHostCommandChannel? _urgentChannel;
    private long _commandSequence;
    private bool _disposed;

    internal CaptureHostClient(string? executablePath = null)
    {
        _executablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath ??
            throw new InvalidOperationException("ClipCord could not locate its executable."));
    }

    private bool IsHostProcessRunning => !_disposed && IsProcessRunning(_process);

    private bool IsMainChannelRunning => IsHostProcessRunning &&
        _mainChannel is { IsConnected: true };

    internal bool IsRunning => IsMainChannelRunning &&
        _urgentChannel is { IsConnected: true };

    internal async Task<CaptureHostStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                try
                {
                    return ToStatus(await SendCoreAsync(
                        NewRequest(CaptureHostProtocol.Ping), cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    CleanupCore(terminateProcess: true);
                }
            }
            return await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CaptureHostRecorderSnapshot> GetRecorderStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(NewRequest(CaptureHostProtocol.Status), cancellationToken);

    public Task<CaptureHostRecorderSnapshot> SelectTargetAsync(
        nint ownerWindowHandle,
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(
            NewRequest(CaptureHostProtocol.SelectTarget) with
            {
                OwnerWindowHandle = ownerWindowHandle.ToInt64()
            },
            cancellationToken);

    public Task<CaptureHostRecorderSnapshot> DetectTargetAsync(
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(NewRequest(CaptureHostProtocol.DetectTarget), cancellationToken);

    public Task<CaptureHostRecorderSnapshot> StartRecordingAsync(
        CaptureSettings settings,
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(
            NewRequest(CaptureHostProtocol.StartRecording) with { Settings = settings },
            cancellationToken);

    public Task<CaptureHostRecorderSnapshot> StopRecordingAsync(
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(NewRequest(CaptureHostProtocol.StopRecording), cancellationToken);

    public Task<CaptureHostRecorderSnapshot> StartReplayAsync(
        CaptureSettings settings,
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(
            NewRequest(CaptureHostProtocol.StartReplay) with { Settings = settings },
            cancellationToken);

    public Task<CaptureHostRecorderSnapshot> StopReplayAsync(
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(NewRequest(CaptureHostProtocol.StopReplay), cancellationToken);

    public Task<CaptureHostRecorderSnapshot> SaveReplayAsync(
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(NewRequest(CaptureHostProtocol.SaveReplay), cancellationToken);

    public Task<CaptureHostRecorderSnapshot> DisableReactionCameraAsync(
        CancellationToken cancellationToken = default) =>
        SendRecorderCommandAsync(
            NewRequest(CaptureHostProtocol.DisableReactionCamera),
            cancellationToken);

    private async Task<CaptureHostRecorderSnapshot> SendRecorderCommandAsync(
        CaptureHostMessage request,
        CancellationToken cancellationToken)
    {
        // This causal sequence is assigned before either pipe gate. It lets the host recognize an
        // earlier-issued Start whose UI dispatch happens after a later urgent camera-off request.
        request = request with
        {
            CommandSequence = Interlocked.Increment(ref _commandSequence)
        };
        if (CaptureHostCommandRouting.UsesUrgentChannel(request.Type))
        {
            return await SendUrgentRecorderCommandAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
            return CaptureHostMessageMapper.ToRecorderSnapshot(response);
        }
        catch (Exception exception) when (
            exception is not CaptureHostCommandException &&
            exception is IOException or InvalidDataException or InvalidOperationException or
                OperationCanceledException or TimeoutException)
        {
            // A response may still arrive after a timed-out/cancelled write. Reusing that pipe
            // would misattribute the stale response to the next command. Terminating the isolated
            // host releases every isolated capture resource and prevents stale response reuse.
            CleanupCore(terminateProcess: true);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CaptureHostRecorderSnapshot> SendUrgentRecorderCommandAsync(
        CaptureHostMessage request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var action = CaptureHostUrgentChannelPolicy.GetAction(
            IsHostProcessRunning,
            _mainChannel is { IsConnected: true },
            _urgentChannel is { IsConnected: true });
        if (action == CaptureHostUrgentChannelAction.FailClosed)
        {
            // Do not enter the ordinary lifecycle gate: it can be held for minutes by SaveReplay.
            // A live host without its urgent channel can no longer honor the privacy contract.
            CleanupCore(terminateProcess: true);
            throw new InvalidOperationException(
                "ClipCord lost its urgent Reaction Camera control channel and stopped capture to release the camera.");
        }
        if (action == CaptureHostUrgentChannelAction.StartHost)
        {
            // Startup still belongs to the lifecycle gate. Once the two channels are connected,
            // camera-off never waits for an ordinary capture command or its multi-minute timeout.
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var urgentChannel = _urgentChannel ??
                throw new InvalidOperationException(
                    "The ClipCord capture host urgent control channel is not connected.");
            // Once the privacy command enters its dedicated channel, finish the bounded exchange
            // even if the caller cancels. This prevents an unread stale response from poisoning a
            // later off request and keeps the device-release result truthful.
            var response = await urgentChannel.SendAsync(
                request,
                TimeSpan.FromSeconds(15),
                cancellationToken,
                honorCallerCancellationAfterAdmission: false).ConfigureAwait(false);
            if (response.AppliedReactionCameraOffSequence < request.CommandSequence)
            {
                throw new InvalidDataException(
                    "The ClipCord capture host did not acknowledge the latest Reaction Camera off request.");
            }
            return CaptureHostMessageMapper.ToRecorderSnapshot(response);
        }
        catch (Exception exception) when (
            exception is not CaptureHostCommandException &&
            exception is IOException or InvalidDataException or InvalidOperationException or
                TimeoutException)
        {
            // The host-side off operation is bounded. A broken, malformed, or timed-out urgent
            // channel therefore means privacy signaling is irrecoverable; terminate only in that
            // exceptional case so Windows releases the camera. Ordinary long-running gameplay
            // saves never enter this path and are not interrupted.
            CleanupCore(terminateProcess: true);
            throw;
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null)
            {
                CleanupCore(terminateProcess: false);
                return;
            }
            if (IsRunning)
            {
                try
                {
                    await SendCoreAsync(NewRequest(CaptureHostProtocol.Shutdown), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRecoverable(exception)) { }
            }
            var process = _process;
            if (process is not null && IsProcessRunning(process))
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken)
                        .WaitAsync(ShutdownTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    if (IsProcessRunning(process)) process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            CleanupCore(terminateProcess: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CaptureHostStatus> StartCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("The ClipCord capture-host executable was not found.", _executablePath);
        }
        CleanupCore(terminateProcess: true);
        var pipeName = CaptureHostLaunchOptions.PipePrefix + Environment.ProcessId + "-" +
            Guid.NewGuid().ToString("N");
        var urgentPipeName = CaptureHostLaunchOptions.UrgentPipePrefix + Environment.ProcessId + "-" +
            Guid.NewGuid().ToString("N");
        NamedPipeServerStream? pipe = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        NamedPipeServerStream? urgentPipe = new(
            urgentPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var startInfo = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_executablePath) ?? AppContext.BaseDirectory
        };
        foreach (var argument in new[]
                 {
                     CaptureHostLaunchOptions.HostArgument,
                     CaptureHostLaunchOptions.PipeArgument,
                     pipeName,
                     CaptureHostLaunchOptions.UrgentPipeArgument,
                     urgentPipeName,
                     CaptureHostLaunchOptions.ParentArgument,
                     Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        CaptureHostCommandChannel? mainChannel = null;
        CaptureHostCommandChannel? urgentChannel = null;
        try
        {
            process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Windows did not start the ClipCord capture host.");
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCancellation.CancelAfter(StartupTimeout);
            await Task.WhenAll(
                    pipe.WaitForConnectionAsync(startupCancellation.Token),
                    urgentPipe.WaitForConnectionAsync(startupCancellation.Token))
                .ConfigureAwait(false);
            mainChannel = new CaptureHostCommandChannel(pipe, process.Id);
            pipe = null;
            urgentChannel = new CaptureHostCommandChannel(urgentPipe, process.Id);
            urgentPipe = null;
            _mainChannel = mainChannel;
            _urgentChannel = urgentChannel;
            _process = process;
            mainChannel = null;
            urgentChannel = null;
            process = null;
            return ToStatus(await SendCoreAsync(
                NewRequest(CaptureHostProtocol.Hello), startupCancellation.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            CleanupCore(terminateProcess: true);
            throw new TimeoutException("The ClipCord capture host did not become ready in time.", exception);
        }
        catch
        {
            CleanupCore(terminateProcess: true);
            throw;
        }
        finally
        {
            pipe?.Dispose();
            urgentPipe?.Dispose();
            mainChannel?.Dispose();
            urgentChannel?.Dispose();
            if (process is not null)
            {
                try
                {
                    if (IsProcessRunning(process)) process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
        }
    }

    private async Task<CaptureHostMessage> SendCoreAsync(
        CaptureHostMessage request,
        CancellationToken cancellationToken)
    {
        if (!IsHostProcessRunning ||
            _mainChannel is not { IsConnected: true } mainChannel)
        {
            throw new InvalidOperationException("The ClipCord capture host is not connected.");
        }

        var timeout = request.Type switch
        {
            CaptureHostProtocol.SelectTarget => TimeSpan.FromMinutes(5),
            CaptureHostProtocol.StartRecording => TimeSpan.FromMinutes(2),
            CaptureHostProtocol.StopRecording => TimeSpan.FromMinutes(2),
            CaptureHostProtocol.StartReplay => TimeSpan.FromMinutes(2),
            CaptureHostProtocol.StopReplay => TimeSpan.FromMinutes(2),
            CaptureHostProtocol.SaveReplay => TimeSpan.FromMinutes(2),
            CaptureHostProtocol.Shutdown => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(3)
        };
        return await mainChannel.SendAsync(request, timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static CaptureHostMessage NewRequest(string type) =>
        new(CaptureHostProtocol.Version, type, 0);

    private static CaptureHostStatus ToStatus(CaptureHostMessage response) =>
        new(response.ProtocolVersion, response.HostProcessId, "Ready");

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException;

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void CleanupCore(bool terminateProcess)
    {
        var urgentChannel = Interlocked.Exchange(ref _urgentChannel, null);
        var mainChannel = Interlocked.Exchange(ref _mainChannel, null);
        var process = Interlocked.Exchange(ref _process, null);
        urgentChannel?.Dispose();
        mainChannel?.Dispose();
        if (process is null) return;
        if (terminateProcess && IsProcessRunning(process))
        {
            try { process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        process.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { StopAsync().GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not stop its isolated capture worker cleanly.", exception);
        }
        finally
        {
            _disposed = true;
            CleanupCore(terminateProcess: true);
            _gate.Dispose();
        }
    }
}
