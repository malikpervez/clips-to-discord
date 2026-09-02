using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClipsToDiscord;

internal static class CaptureHostAbortTests
{
    internal static async Task RunAsync()
    {
        AssertAuthorityAbortSharesSynchronousCommandAdmissionBoundary();
        var executable = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("127.0.0.1");
        using var liveProcess = Process.Start(startInfo) ??
            throw new InvalidOperationException("The abort fixture child process did not start.");
        using var injectedProcess = Process.GetProcessById(liveProcess.Id);
        var mainStream = new ProcessObservedMemoryStream(
            () => !liveProcess.HasExited);
        var urgentStream = new ProcessObservedMemoryStream(
            () => !liveProcess.HasExited);
        var mainChannel = new CaptureHostCommandChannel(mainStream, liveProcess.Id);
        var urgentChannel = new CaptureHostCommandChannel(urgentStream, liveProcess.Id);
        var client = new CaptureHostClient(Environment.ProcessPath);
        var lifecycleGate = GetField<SemaphoreSlim>(client, "_gate") ??
            throw new InvalidOperationException("The CaptureHostClient lifecycle gate is unavailable.");
        var lifecycleGateHeld = false;
        try
        {
            SetField(client, "_process", injectedProcess);
            SetField(client, "_mainChannel", mainChannel);
            SetField(client, "_urgentChannel", urgentChannel);
            Assert(client.IsRunning,
                "The abort fixture must begin with a live isolated-host process and both channels.");

            await lifecycleGate.WaitAsync();
            lifecycleGateHeld = true;
            var abort = ((ICaptureLibraryAuthorityAbort)client)
                .AbortForCaptureLibraryAuthorityRevocationAsync();
            Assert(
                abort.IsCompleted && liveProcess.HasExited &&
                mainStream.WasDisposed && urgentStream.WasDisposed,
                "Authority abort must latch, terminate, join, and close channels synchronously before returning its task; deferred teardown leaves a command-admission race.");
            var repeated = ((ICaptureLibraryAuthorityAbort)client)
                .AbortForCaptureLibraryAuthorityRevocationAsync();
            Assert(ReferenceEquals(abort, repeated),
                "Concurrent authority-abort callers must join one process-termination operation.");
            await abort.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(liveProcess.WaitForExit(5_000),
                "Authority abort must terminate and join the isolated child process.");

            client.Dispose();
            lifecycleGate.Release();
            lifecycleGateHeld = false;
            var restartRejected = false;
            try { _ = await client.EnsureReadyAsync(); }
            catch (ObjectDisposedException) { restartRejected = true; }
            catch (InvalidOperationException) { restartRejected = true; }
            Assert(!client.IsRunning &&
                   GetField<Process>(client, "_process") is null &&
                   GetField<CaptureHostCommandChannel>(client, "_mainChannel") is null &&
                   GetField<CaptureHostCommandChannel>(client, "_urgentChannel") is null &&
                   mainStream.WasDisposed &&
                   urgentStream.WasDisposed &&
                   !mainStream.DisposedWhileProcessRunning &&
                   !urgentStream.DisposedWhileProcessRunning &&
                   mainStream.ToArray().Length == 0 &&
                   urgentStream.ToArray().Length == 0 &&
                   restartRejected,
                "Authority abort must terminate and join the isolated child before disposing " +
                "either control channel; it must leave no child/channel, emit no " +
                "Shutdown/Stop command, and permanently reject restart in this process.");
        }
        finally
        {
            if (lifecycleGateHeld)
            {
                try { lifecycleGate.Release(); }
                catch (ObjectDisposedException) { }
            }
            try
            {
                await ((ICaptureLibraryAuthorityAbort)client)
                    .AbortForCaptureLibraryAuthorityRevocationAsync();
            }
            catch { }
            client.Dispose();
            if (!liveProcess.HasExited)
            {
                liveProcess.Kill(entireProcessTree: true);
                liveProcess.WaitForExit(5_000);
            }
        }
    }

    private static void AssertAuthorityAbortSharesSynchronousCommandAdmissionBoundary()
    {
        const BindingFlags instance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var clientType = typeof(CaptureHostClient);
        var admissionField = clientType.GetField(
            "_authorityAdmissionSync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The CaptureHostClient authority-admission lock is unavailable.");
        var revokedField = clientType.GetField(
            "_captureLibraryAuthorityRevoked",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The CaptureHostClient authority latch is unavailable.");
        var abort = clientType.GetMethod(
            nameof(ICaptureLibraryAuthorityAbort.AbortForCaptureLibraryAuthorityRevocationAsync),
            instance) ?? throw new InvalidOperationException(
                "The CaptureHostClient authority-abort seam is unavailable.");
        var cleanup = clientType.GetMethod(
            "CleanupCore",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The CaptureHostClient cleanup seam is unavailable.");
        var sendCore = GetAsyncMoveNext(clientType, "SendCoreAsync");
        var sendUrgent = GetAsyncMoveNext(clientType, "SendUrgentRecorderCommandAsync");
        var channelSend = typeof(CaptureHostCommandChannel).GetMethod(
            nameof(CaptureHostCommandChannel.SendAsync),
            instance) ?? throw new InvalidOperationException(
                "The CaptureHostCommandChannel send seam is unavailable.");

        var abortAdmissionOffset = FirstFieldAccessOffset(abort, admissionField);
        var abortLatchOffset = FirstFieldAccessOffset(abort, revokedField);
        var abortCleanupOffset = FirstDirectCallOffset(abort, cleanup);
        var ordinaryAdmissionOffset = FirstFieldAccessOffset(sendCore, admissionField);
        var ordinarySendOffset = FirstDirectCallOffset(sendCore, channelSend);
        var urgentAdmissionOffset = FirstFieldAccessOffset(sendUrgent, admissionField);
        var urgentSendOffset = FirstDirectCallOffset(sendUrgent, channelSend);
        Assert(
            abort.GetCustomAttribute<AsyncStateMachineAttribute>() is null &&
            !CallsTaskRun(abort) &&
            abortAdmissionOffset >= 0 &&
            abortLatchOffset > abortAdmissionOffset &&
            abortCleanupOffset > abortLatchOffset &&
            ordinaryAdmissionOffset >= 0 &&
            ordinarySendOffset > ordinaryAdmissionOffset &&
            urgentAdmissionOffset >= 0 &&
            urgentSendOffset > urgentAdmissionOffset,
            "Authority abort must synchronously latch and terminate under the same admission lock that ordinary and urgent commands acquire before beginning a pipe write.");
    }

    private static MethodInfo GetAsyncMoveNext(Type type, string methodName)
    {
        var method = type.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"The CaptureHostClient method '{methodName}' is unavailable.");
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?
            .StateMachineType ?? throw new InvalidOperationException(
                $"The CaptureHostClient method '{methodName}' is not asynchronous.");
        return stateMachine.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"The CaptureHostClient method '{methodName}' has no state-machine body.");
    }

    private static bool CallsTaskRun(MethodBase caller)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x28 or 0x6F)) continue;
            var called = TryResolveMethod(caller, BitConverter.ToInt32(il, offset + 1));
            if (called?.DeclaringType == typeof(Task) &&
                called.Name.Equals(nameof(Task.Run), StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static int FirstDirectCallOffset(MethodBase caller, MethodBase callee)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x28 or 0x6F)) continue;
            var called = TryResolveMethod(caller, BitConverter.ToInt32(il, offset + 1));
            if (called is not null && called.Module == callee.Module &&
                called.MetadataToken == callee.MetadataToken)
            {
                return offset;
            }
        }
        return -1;
    }

    private static int FirstFieldAccessOffset(MethodBase caller, FieldInfo field)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (var offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] is not (0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80)) continue;
            try
            {
                var accessed = caller.Module.ResolveField(
                    BitConverter.ToInt32(il, offset + 1),
                    caller.DeclaringType?.GetGenericArguments(),
                    caller is MethodInfo method ? method.GetGenericArguments() : null);
                if (accessed is not null && accessed.Module == field.Module &&
                    accessed.MetadataToken == field.MetadataToken)
                {
                    return offset;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or BadImageFormatException)
            {
            }
        }
        return -1;
    }

    private static MethodBase? TryResolveMethod(MethodBase caller, int metadataToken)
    {
        try
        {
            return caller.Module.ResolveMethod(
                metadataToken,
                caller.DeclaringType?.GetGenericArguments(),
                caller is MethodInfo method ? method.GetGenericArguments() : null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }

    private sealed class ProcessObservedMemoryStream(Func<bool> processRunning) : MemoryStream
    {
        internal bool WasDisposed { get; private set; }
        internal bool DisposedWhileProcessRunning { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !WasDisposed)
            {
                WasDisposed = true;
                DisposedWhileProcessRunning = processRunning();
            }
            base.Dispose(disposing);
        }
    }

    private static void SetField<T>(CaptureHostClient client, string name, T value)
        where T : class
    {
        var field = typeof(CaptureHostClient).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"The CaptureHostClient field '{name}' is unavailable.");
        field.SetValue(client, value);
    }

    private static T? GetField<T>(CaptureHostClient client, string name)
        where T : class
    {
        var field = typeof(CaptureHostClient).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"The CaptureHostClient field '{name}' is unavailable.");
        return field.GetValue(client) as T;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
