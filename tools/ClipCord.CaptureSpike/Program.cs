using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ClipCord.CaptureSpike;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!TryParseDuration(args, out var durationSeconds))
        {
            Console.Error.WriteLine("Usage: ClipCord.CaptureSpike [--seconds 5-120]");
            return 2;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) ||
            !GraphicsCaptureSession.IsSupported())
        {
            Console.Error.WriteLine("Windows Graphics Capture is unavailable on this PC.");
            return 3;
        }

        ApplicationConfiguration.Initialize();
        using var owner = CreatePickerOwner();
        CaptureSpikeReport? report = null;
        Exception? failure = null;
        var cancelled = false;
        owner.Shown += async (_, _) =>
        {
            try
            {
                var picker = new GraphicsCapturePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, owner.Handle);
                var item = await picker.PickSingleItemAsync();
                if (item is null)
                {
                    cancelled = true;
                    return;
                }

                owner.Hide();
                var hardwareEncoders = EnumerateHardwareH264Encoders();
                report = await RunFrameAcquisitionAsync(
                    item,
                    TimeSpan.FromSeconds(durationSeconds),
                    hardwareEncoders);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                owner.Close();
            }
        };
        Application.Run(owner);

        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            return 6;
        }
        if (cancelled || report is null)
        {
            Console.Error.WriteLine("Capture selection was cancelled.");
            return 4;
        }
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.FramesCaptured > 0 ? 0 : 5;
    }

    private static async Task<CaptureSpikeReport> RunFrameAcquisitionAsync(
        GraphicsCaptureItem item,
        TimeSpan duration,
        string[] hardwareEncoders)
    {
        using var d3dDevice = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        var winRtDevice = D3D11.CreateDirect3D11DeviceFromDXGIDevice<IDirect3DDevice>(dxgiDevice);
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            3,
            item.Size);
        using var session = framePool.CreateCaptureSession(item);
        using var immediateContext = d3dDevice.ImmediateContext;

        var frameTimes = new List<double>();
        ID3D11Texture2D? gpuCopyTarget = null;
        var gpuCopies = 0;
        var captureStarted = Stopwatch.GetTimestamp();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        framePool.FrameArrived += OnFrameArrived;
        session.StartCapture();

        using var timeout = new CancellationTokenSource(duration);
        using var registration = timeout.Token.Register(() => completion.TrySetResult());
        await completion.Task.ConfigureAwait(false);
        session.Dispose();

        var elapsed = Stopwatch.GetElapsedTime(captureStarted);
        gpuCopyTarget?.Dispose();
        var sortedTimes = frameTimes.Order().ToArray();
        return new CaptureSpikeReport(
            item.DisplayName,
            item.Size.Width,
            item.Size.Height,
            elapsed.TotalSeconds,
            frameTimes.Count,
            gpuCopies,
            elapsed.TotalSeconds > 0 ? frameTimes.Count / elapsed.TotalSeconds : 0,
            Percentile(sortedTimes, 0.50),
            Percentile(sortedTimes, 0.95),
            Percentile(sortedTimes, 0.99),
            hardwareEncoders);

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
        {
            var started = Stopwatch.GetTimestamp();
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            using var sourceTexture = Direct3DInterop.GetTexture(frame.Surface);
            gpuCopyTarget ??= d3dDevice.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)frame.ContentSize.Width,
                (uint)frame.ContentSize.Height,
                1,
                1,
                BindFlags.ShaderResource));
            immediateContext.CopyResource(gpuCopyTarget, sourceTexture);
            gpuCopies++;
            lock (frameTimes)
            {
                frameTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private static string[] EnumerateHardwareH264Encoders()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            var input = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.NV12
            };
            var output = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.H264
            };
            using var activations = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
                input,
                output);
            return activations
                .Select(activation => activation.FriendlyName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            MediaFactory.MFShutdown().CheckError();
        }
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private static Form CreatePickerOwner() => new()
    {
        Text = "ClipCord 2.0 capture benchmark",
        Width = 460,
        Height = 150,
        StartPosition = FormStartPosition.CenterScreen,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        MaximizeBox = false,
        MinimizeBox = false,
        Controls =
        {
            new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Select the game window or display to benchmark.\r\nNo recording is saved or uploaded."
            }
        }
    };

    private static bool TryParseDuration(string[] args, out int seconds)
    {
        seconds = 15;
        if (args.Length == 0) return true;
        return args.Length == 2 &&
               args[0].Equals("--seconds", StringComparison.Ordinal) &&
               int.TryParse(args[1], out seconds) &&
               seconds is >= 5 and <= 120;
    }

    private static class Direct3DInterop
    {
        private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        internal static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
        {
            var access = (IDirect3DDxgiInterfaceAccess)(object)surface;
            var iid = Texture2DIid;
            var pointer = access.GetInterface(ref iid);
            return new ID3D11Texture2D(pointer);
        }

        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }
    }
}

internal sealed record CaptureSpikeReport(
    string Target,
    int Width,
    int Height,
    double DurationSeconds,
    int FramesCaptured,
    int GpuCopies,
    double FramesPerSecond,
    double FrameCallbackP50Milliseconds,
    double FrameCallbackP95Milliseconds,
    double FrameCallbackP99Milliseconds,
    IReadOnlyList<string> HardwareH264Encoders);
