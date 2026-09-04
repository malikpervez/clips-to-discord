using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClipsToDiscord;

/// <summary>
/// Establishes the production worker's process-tree lifetime before any capture-library path is
/// opened. The handle is intentionally rooted until Windows tears the process down: closing it
/// early would activate KILL_ON_JOB_CLOSE against the worker itself, while keeping it alive makes
/// Environment.Exit also terminate any ffmpeg descendants that have not observed cancellation.
/// </summary>
internal static class SilhouetteWorkerJobLifetime
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private static readonly object Gate = new();
    private static SafeFileHandle? _rootedJob;

    internal static bool TryEstablish(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Silhouette worker containment requires Windows.";
            return false;
        }

        lock (Gate)
        {
            if (_rootedJob is { IsInvalid: false, IsClosed: false }) return true;

            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                var nativeError = Marshal.GetLastWin32Error();
                job.Dispose();
                error = new Win32Exception(
                        nativeError,
                        "Windows could not create the silhouette worker Job Object.")
                    .Message;
                return false;
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectInformationClass.ExtendedLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var nativeError = Marshal.GetLastWin32Error();
                job.Dispose();
                error = new Win32Exception(
                        nativeError,
                        "Windows could not configure silhouette worker containment.")
                    .Message;
                return false;
            }

            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                var nativeError = Marshal.GetLastWin32Error();
                job.Dispose();
                error = new Win32Exception(
                        nativeError,
                        "Windows could not contain the silhouette worker process tree.")
                    .Message;
                return false;
            }

            // Do not dispose this handle. It is the lifetime boundary for the current worker and
            // every encoder child, and Windows closes it atomically as the process exits.
            _rootedJob = job;
            return true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }
}

/// <summary>
/// One retained native parent-process handle. PID lookup happens once; every identity check and
/// wait thereafter targets this same kernel object, so PID reuse cannot transfer worker authority.
/// </summary>
internal sealed class SilhouetteWorkerParentHandle : WaitHandle
{
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;

    private SilhouetteWorkerParentHandle(
        SafeWaitHandle handle,
        ulong creationTimeFileTime)
    {
        SafeWaitHandle = handle;
        CreationTimeFileTime = creationTimeFileTime;
    }

    internal ulong CreationTimeFileTime { get; }

    internal bool IsSignaled => WaitOne(0);

    internal static SilhouetteWorkerParentHandle OpenValidated(
        int processId,
        ulong expectedCreationTimeFileTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (expectedCreationTimeFileTime == 0)
        {
            throw new InvalidDataException(
                "The silhouette parent creation identity is unavailable.");
        }

        var handle = OpenProcess(
            Synchronize | ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "The silhouette worker parent process could not be opened safely.");
        }

        try
        {
            var actualCreationTime = ReadCreationTimeFileTime(handle);
            if (actualCreationTime != expectedCreationTimeFileTime)
            {
                throw new InvalidDataException(
                    "The silhouette worker parent process identity no longer matches its launch authority.");
            }
            return new SilhouetteWorkerParentHandle(handle, actualCreationTime);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static ulong ReadCreationTimeFileTime(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        var handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "The silhouette worker parent creation identity could not be opened.");
        }
        using (handle)
        {
            return ReadCreationTimeFileTime(handle);
        }
    }

    internal async Task WatchAsync(
        Action parentExited,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentExited);
        if (cancellationToken.IsCancellationRequested) return;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registeredWait = ThreadPool.RegisterWaitForSingleObject(
            this,
            static (state, _) =>
                ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            completion,
            Timeout.Infinite,
            executeOnlyOnce: true);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false),
            completion);
        bool observedExit;
        try
        {
            observedExit = await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registeredWait.Unregister(null);
        }

        // If watcher cancellation and parent exit raced, the signaled retained handle wins.
        if (observedExit || IsSignaled) parentExited();
    }

    private static ulong ReadCreationTimeFileTime(SafeWaitHandle handle)
    {
        if (!GetProcessTimes(
                handle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The silhouette worker parent creation identity could not be read.");
        }
        return ((ulong)creationTime.HighDateTime << 32) | creationTime.LowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeWaitHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }
}

internal static class SilhouetteWorkerProcess
{
    internal const int SuccessExitCode = 0;
    internal const int ProcessingFailedExitCode = 3;
    internal const int DependencyUnavailableExitCode = 4;
    internal const int LifetimeAuthorityFailedExitCode = 5;
    internal const int InvalidArgumentsExitCode = 22;

    internal static async Task<int> RunAsync(
        SilhouetteWorkerLaunchOptions options,
        CancellationToken cancellationToken = default,
        Action<int>? exitProcess = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ParentCreationTimeFileTime == 0 ||
            options.ExpectedLibraryBinding is null)
        {
            return InvalidArgumentsExitCode;
        }

        using var workerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var parentWatchCancellation = new CancellationTokenSource();
        var terminateProcess = exitProcess ?? Environment.Exit;

        try
        {
            // Open and validate the parent once. The returned HANDLE, not the PID, is retained
            // through processing and is also the exact wait target used by the watcher.
            using var parent = SilhouetteWorkerParentHandle.OpenValidated(
                options.ParentProcessId,
                options.ParentCreationTimeFileTime);

            // Open and pin one exact directory object, derive the current binding from that same
            // handle, and retain it across every read and mutation performed by the processor.
            var libraryRoot = RoutingWatchedFileSystem.OpenOrdinaryRoot(options.LibraryRoot);
            using (libraryRoot.Handle)
            {
                var currentBinding = RoutingCaptureLibraryBindingModel.Create(libraryRoot);
                RoutingCaptureLibraryBindingModel.RequireExact(
                    options.ExpectedLibraryBinding,
                    currentBinding);

                void ParentExited()
                {
                    try
                    {
                        workerCancellation.Cancel();
                    }
                    finally
                    {
                        // Production uses Environment.Exit. Its OS handle teardown closes the
                        // rooted Job Object and therefore terminates encoder descendants too.
                        terminateProcess(SuccessExitCode);
                    }
                }

                // This second check closes the validation-to-watcher window using the same
                // retained HANDLE. A signaled parent can never authorize library processing.
                if (parent.IsSignaled)
                {
                    ParentExited();
                    return ProcessingFailedExitCode;
                }

                var parentWatcher = parent.WatchAsync(
                    ParentExited,
                    parentWatchCancellation.Token);
                try
                {
                    TryLowerPriority();
                    var ffmpegPath = FfmpegCompressor.FindExecutable();
                    if (ffmpegPath is null)
                    {
                        Log.Error(
                            "ClipCord could not start silhouette processing because ffmpeg.exe is unavailable.");
                        return DependencyUnavailableExitCode;
                    }

                    var modelPath = OnnxSilhouetteFrameSegmenter.GetPackagedModelPath();
                    if (!File.Exists(modelPath))
                    {
                        Log.Error(
                            "ClipCord could not start silhouette processing because its local model is unavailable.");
                        return DependencyUnavailableExitCode;
                    }

                    var processor = new SilhouetteProjectProcessor(
                        ffmpegPath,
                        modelPath,
                        preferDirectMl: !options.ForceCpu);
                    var state = await processor.ProcessAsync(
                            libraryRoot.CanonicalPath,
                            options.ProjectId,
                            workerCancellation.Token)
                        .ConfigureAwait(false);
                    return state.AggregateState is
                        SilhouetteRenditionAggregateState.Ready or
                        SilhouetteRenditionAggregateState.Disabled
                        ? SuccessExitCode
                        : ProcessingFailedExitCode;
                }
                finally
                {
                    parentWatchCancellation.Cancel();
                    await parentWatcher.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return ProcessingFailedExitCode;
        }
        catch (Exception exception)
        {
            Log.Error(
                $"ClipCord silhouette processing failed for project {options.ProjectId}.",
                exception);
            return ProcessingFailedExitCode;
        }
    }

    private static void TryLowerPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or
                NotSupportedException)
        {
            // Best effort. The worker remains isolated from the capture process even if Windows
            // or a policy prevents adjusting its scheduling priority.
        }
    }
}
