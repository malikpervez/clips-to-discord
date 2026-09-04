using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace ClipsToDiscord;

internal sealed record ReactionCameraDevice(string Id, string Name);

internal sealed class ReactionCameraUnavailableException(string message) :
    InvalidOperationException(message);

internal static class ReactionCameraDeviceCatalog
{
    private const string EnumerationFailure = "ClipCord could not list available cameras.";

    internal static async Task<IReadOnlyList<ReactionCameraDevice>> FindAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return devices
                .Where(device =>
                    !string.IsNullOrWhiteSpace(device.Id) &&
                    !string.IsNullOrWhiteSpace(device.Name))
                .Select(device => new ReactionCameraDevice(device.Id, device.Name.Trim()))
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.Id, StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ReactionCameraUnavailableException(EnumerationFailure);
        }
    }
}

internal interface IVideoFrameSource : IDisposable
{
    SizeInt32 SourceSize { get; }
    CapturedSurface? WaitForNewFrame();
    void Stop();
}

/// <summary>
/// Reads timestamped BGRA camera surfaces for the isolated capture host. The reader requests
/// copied output frames so retaining one latest frame cannot exhaust the camera driver's pool.
/// </summary>
internal sealed class ReactionCameraFrameSource : IVideoFrameSource
{
    private const int MaximumInvalidFrameCount = 8;
    private const string AccessDenied =
        "Camera access is turned off in Windows privacy settings.";
    private const string OpenFailed =
        "ClipCord could not open the selected reaction camera. Close other camera apps or choose another camera.";
    private const string StreamUnavailable =
        "The selected reaction camera does not provide a compatible video stream.";
    private const string StreamStopped =
        "The reaction camera stopped unexpectedly.";

    private readonly object _gate = new();
    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private readonly ManualResetEvent _frameEvent = new(false);
    private readonly ManualResetEvent _closedEvent = new(false);
    private MediaFrameReference? _latestFrame;
    private Task? _readerStopTask;
    private string? _failureMessage;
    private int _invalidFrameCount;
    private bool _stopping;
    private bool _disposed;

    private ReactionCameraFrameSource(ReactionCameraReaderResources resources)
    {
        _capture = resources.Capture;
        _reader = resources.Reader;
        SourceSize = resources.OutputSize;
        _capture.Failed += CaptureFailed;
        _reader.FrameArrived += FrameArrived;
    }

    public SizeInt32 SourceSize { get; }

    internal static async Task<ReactionCameraFrameSource> CreateAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ReactionCameraReaderResources? resources = null;
        ReactionCameraFrameSource? source = null;
        try
        {
            resources = await ReactionCameraReaderFactory.CreateAsync(
                deviceId,
                maximumWidth: 1280,
                maximumHeight: 720,
                MediaCaptureMemoryPreference.Auto,
                cancellationToken);
            source = new ReactionCameraFrameSource(resources);
            resources = null;
            var status = await source._reader.StartAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                throw new ReactionCameraUnavailableException(
                    status == MediaFrameReaderStartStatus.OutputFormatNotSupported
                        ? StreamUnavailable
                        : OpenFailed);
            }
            string? startupFailure;
            lock (source._gate) startupFailure = source._failureMessage;
            if (startupFailure is not null)
            {
                throw new ReactionCameraUnavailableException(startupFailure);
            }
            return source;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            source?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw;
        }
        catch (ReactionCameraUnavailableException)
        {
            source?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            source?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw new ReactionCameraUnavailableException(AccessDenied);
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070005))
        {
            source?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw new ReactionCameraUnavailableException(AccessDenied);
        }
        catch
        {
            source?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw new ReactionCameraUnavailableException(OpenFailed);
        }
    }

    public CapturedSurface? WaitForNewFrame()
    {
        while (true)
        {
            if (Volatile.Read(ref _disposed)) return null;
            int signaled;
            try
            {
                signaled = WaitHandle.WaitAny([_closedEvent, _frameEvent]);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            if (signaled == 0)
            {
                string? failure;
                lock (_gate) failure = _failureMessage;
                if (failure is not null)
                {
                    throw new ReactionCameraUnavailableException(failure);
                }
                return null;
            }

            MediaFrameReference? frame;
            lock (_gate)
            {
                frame = _latestFrame;
                _latestFrame = null;
                _frameEvent.Reset();
            }
            if (frame is null) continue;

            var surface = frame.VideoMediaFrame?.Direct3DSurface;
            var timestamp = frame.SystemRelativeTime;
            if (surface is not null && timestamp.HasValue)
            {
                // CapturedSurface owns both references. Holding the MediaFrameReference until the
                // sample is released is conservative across camera drivers whose surface lifetime
                // is shorter than the copied-frame contract suggests.
                Volatile.Write(ref _invalidFrameCount, 0);
                try
                {
                    return new CapturedSurface(surface, timestamp.Value, frame);
                }
                catch
                {
                    surface.Dispose();
                    frame.Dispose();
                    throw;
                }
            }

            surface?.Dispose();
            frame.Dispose();
            if (Interlocked.Increment(ref _invalidFrameCount) >= MaximumInvalidFrameCount)
            {
                Fail(StreamUnavailable);
            }
        }
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        MediaFrameReference? frame = null;
        try
        {
            frame = sender.TryAcquireLatestFrame();
            if (frame is null) return;
            lock (_gate)
            {
                if (_disposed || _stopping)
                {
                    frame.Dispose();
                    return;
                }
                _latestFrame?.Dispose();
                _latestFrame = frame;
                frame = null;
                _frameEvent.Set();
            }
        }
        catch
        {
            frame?.Dispose();
            Fail(StreamStopped);
        }
    }

    private void CaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs) =>
        Fail(StreamStopped);

    private void Fail(string publicMessage)
    {
        lock (_gate)
        {
            if (_disposed || _stopping) return;
            _failureMessage ??= publicMessage;
        }
        Stop();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _readerStopTask = Task.Run(() => ReactionCameraReaderFactory.Stop(_reader));
        }
        try { _closedEvent.Set(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _reader.FrameArrived -= FrameArrived;
        _capture.Failed -= CaptureFailed;
        WaitForReaderStop();
        lock (_gate)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
        _reader.Dispose();
        _capture.Dispose();
        _frameEvent.Dispose();
        _closedEvent.Dispose();
    }

    private void WaitForReaderStop()
    {
        Task? stopTask;
        lock (_gate) stopTask = _readerStopTask;
        if (stopTask is null) return;
        try { stopTask.Wait(TimeSpan.FromSeconds(4)); }
        catch { }
    }
}

/// <summary>
/// Short-lived camera session for the visible consent dialog. Frames are converted on the media
/// callback thread, capped at 640x360, and at most one bitmap delivery is queued to the caller's
/// synchronization context. Ownership of each delivered bitmap transfers to the callback.
/// </summary>
internal sealed class ReactionCameraPreviewSession : IDisposable
{
    private const string AccessDenied =
        "Camera access is turned off in Windows privacy settings.";
    private const string OpenFailed =
        "ClipCord could not open the selected reaction camera. Close other camera apps or choose another camera.";
    private const string PreviewUnavailable =
        "The selected reaction camera does not provide a compatible preview.";
    private const string PreviewStopped =
        "The reaction camera preview stopped unexpectedly.";

    private readonly object _gate = new();
    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private readonly Action<Bitmap> _frameReady;
    private readonly SynchronizationContext _callbackContext;
    private int _processingFrame;
    private int _deliveryPending;
    private Task? _readerStopTask;
    private Bitmap? _pendingBitmap;
    private bool _isActive;
    private bool _disposed;
    private string? _lastError;

    private ReactionCameraPreviewSession(
        ReactionCameraReaderResources resources,
        Action<Bitmap> frameReady,
        SynchronizationContext callbackContext)
    {
        _capture = resources.Capture;
        _reader = resources.Reader;
        PreviewSize = resources.OutputSize;
        _frameReady = frameReady;
        _callbackContext = callbackContext;
        _capture.Failed += CaptureFailed;
        _reader.FrameArrived += FrameArrived;
    }

    internal event EventHandler? Failed;
    internal SizeInt32 PreviewSize { get; }
    internal bool IsActive { get { lock (_gate) return _isActive && !_disposed; } }
    internal string? LastError { get { lock (_gate) return _lastError; } }

    internal static async Task<ReactionCameraPreviewSession> StartAsync(
        string deviceId,
        Action<Bitmap> frameReady,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(frameReady);
        var callbackContext = SynchronizationContext.Current ?? new SynchronizationContext();
        ReactionCameraReaderResources? resources = null;
        ReactionCameraPreviewSession? session = null;
        try
        {
            resources = await ReactionCameraReaderFactory.CreateAsync(
                deviceId,
                maximumWidth: 640,
                maximumHeight: 360,
                MediaCaptureMemoryPreference.Cpu,
                cancellationToken);
            session = new ReactionCameraPreviewSession(resources, frameReady, callbackContext);
            resources = null;
            lock (session._gate) session._isActive = true;
            var status = await session._reader.StartAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                throw new ReactionCameraUnavailableException(
                    status == MediaFrameReaderStartStatus.OutputFormatNotSupported
                        ? PreviewUnavailable
                        : OpenFailed);
            }
            string? startupFailure;
            lock (session._gate) startupFailure = session._lastError;
            if (startupFailure is not null)
            {
                throw new ReactionCameraUnavailableException(startupFailure);
            }
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw;
        }
        catch (ReactionCameraUnavailableException)
        {
            session?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            session?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw new ReactionCameraUnavailableException(AccessDenied);
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80070005))
        {
            session?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw new ReactionCameraUnavailableException(AccessDenied);
        }
        catch
        {
            session?.Dispose();
            ReactionCameraReaderFactory.Dispose(resources);
            throw new ReactionCameraUnavailableException(OpenFailed);
        }
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.Exchange(ref _processingFrame, 1) != 0) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var softwareBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (softwareBitmap is null) return;
            using var converted = SoftwareBitmap.Convert(
                softwareBitmap,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore);
            var bitmap = CopyToDrawingBitmap(converted);
            if (Interlocked.Exchange(ref _deliveryPending, 1) != 0)
            {
                bitmap.Dispose();
                return;
            }
            lock (_gate)
            {
                if (_disposed || !_isActive)
                {
                    Volatile.Write(ref _deliveryPending, 0);
                    bitmap.Dispose();
                    return;
                }
                _pendingBitmap = bitmap;
            }
            try
            {
                _callbackContext.Post(
                    _ => DeliverPendingBitmap(),
                    null);
            }
            catch
            {
                var ownsBitmap = false;
                lock (_gate)
                {
                    if (ReferenceEquals(_pendingBitmap, bitmap))
                    {
                        _pendingBitmap = null;
                        ownsBitmap = true;
                    }
                }
                Volatile.Write(ref _deliveryPending, 0);
                if (ownsBitmap) bitmap.Dispose();
                throw;
            }
        }
        catch
        {
            Fail(PreviewStopped);
        }
        finally
        {
            Volatile.Write(ref _processingFrame, 0);
        }
    }

    private void DeliverPendingBitmap()
    {
        Bitmap? bitmap;
        lock (_gate)
        {
            bitmap = _pendingBitmap;
            _pendingBitmap = null;
        }
        try
        {
            if (bitmap is null) return;
            if (!IsActive)
            {
                bitmap.Dispose();
                return;
            }
            _frameReady(bitmap);
        }
        catch
        {
            bitmap?.Dispose();
            Fail(PreviewStopped);
        }
        finally
        {
            Volatile.Write(ref _deliveryPending, 0);
        }
    }

    private static Bitmap CopyToDrawingBitmap(SoftwareBitmap softwareBitmap)
    {
        var width = softwareBitmap.PixelWidth;
        var height = softwareBitmap.PixelHeight;
        if (width <= 0 || height <= 0 || width > 640 || height > 360)
        {
            throw new InvalidOperationException(PreviewUnavailable);
        }
        var byteCount = checked(width * height * 4);
        var bytes = new byte[byteCount];
        var buffer = new Windows.Storage.Streams.Buffer((uint)byteCount);
        softwareBitmap.CopyToBuffer(buffer);
        if (buffer.Length < byteCount)
        {
            throw new InvalidOperationException(PreviewUnavailable);
        }
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(bytes);
        }

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            var data = result.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var sourceStride = width * 4;
                if (data.Stride == sourceStride)
                {
                    Marshal.Copy(bytes, 0, data.Scan0, byteCount);
                }
                else
                {
                    for (var row = 0; row < height; row++)
                    {
                        Marshal.Copy(
                            bytes,
                            row * sourceStride,
                            IntPtr.Add(data.Scan0, row * data.Stride),
                            sourceStride);
                    }
                }
            }
            finally
            {
                result.UnlockBits(data);
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void CaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs) =>
        Fail(PreviewStopped);

    private void Fail(string publicMessage)
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed || !_isActive) return;
            _isActive = false;
            _lastError ??= publicMessage;
            changed = true;
        }
        if (changed)
        {
            ScheduleReaderStop();
            try
            {
                _callbackContext.Post(
                    _ =>
                    {
                        if (_disposed) return;
                        try { Failed?.Invoke(this, EventArgs.Empty); }
                        catch { }
                    },
                    null);
            }
            catch
            {
                // The consent dialog's synchronization context may already be shutting down.
            }
        }
    }

    public void Dispose()
    {
        Bitmap? pendingBitmap;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _isActive = false;
            pendingBitmap = _pendingBitmap;
            _pendingBitmap = null;
        }
        pendingBitmap?.Dispose();
        _reader.FrameArrived -= FrameArrived;
        _capture.Failed -= CaptureFailed;
        ScheduleReaderStop();
        WaitForReaderStop();
        _reader.Dispose();
        _capture.Dispose();
        Failed = null;
    }

    private void ScheduleReaderStop()
    {
        lock (_gate)
        {
            _readerStopTask ??= Task.Run(() => ReactionCameraReaderFactory.Stop(_reader));
        }
    }

    private void WaitForReaderStop()
    {
        Task? stopTask;
        lock (_gate) stopTask = _readerStopTask;
        if (stopTask is null) return;
        try { stopTask.Wait(TimeSpan.FromSeconds(4)); }
        catch { }
    }
}

internal sealed record ReactionCameraReaderResources(
    MediaCapture Capture,
    MediaFrameReader Reader,
    SizeInt32 OutputSize);

internal static class ReactionCameraReaderFactory
{
    private const string OpenFailed =
        "ClipCord could not open the selected reaction camera. Close other camera apps or choose another camera.";
    private const string StreamUnavailable =
        "The selected reaction camera does not provide a compatible video stream.";

    internal static async Task<ReactionCameraReaderResources> CreateAsync(
        string deviceId,
        int maximumWidth,
        int maximumHeight,
        MediaCaptureMemoryPreference memoryPreference,
        CancellationToken cancellationToken)
    {
        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    MemoryPreference = memoryPreference
                })
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var source = capture.FrameSources.Values
                .Where(value => value.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(value => StreamPreference(value.Info.MediaStreamType))
                .FirstOrDefault() ??
                throw new ReactionCameraUnavailableException(StreamUnavailable);
            var outputSize = SelectOutputSize(source, maximumWidth, maximumHeight);
            reader = await capture.CreateFrameReaderAsync(
                    source,
                    MediaEncodingSubtypes.Bgra8,
                    new BitmapSize
                    {
                        Width = (uint)outputSize.Width,
                        Height = (uint)outputSize.Height
                    })
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            return new ReactionCameraReaderResources(capture, reader, outputSize);
        }
        catch
        {
            reader?.Dispose();
            capture?.Dispose();
            throw;
        }
    }

    private static int StreamPreference(MediaStreamType streamType) => streamType switch
    {
        MediaStreamType.VideoPreview => 0,
        MediaStreamType.VideoRecord => 1,
        _ => 2
    };

    private static SizeInt32 SelectOutputSize(
        MediaFrameSource source,
        int maximumWidth,
        int maximumHeight)
    {
        var formats = source.SupportedFormats
            .Where(format => format.VideoFormat is { Width: > 0, Height: > 0 })
            .Select(format => new
            {
                Width = (int)format.VideoFormat.Width,
                Height = (int)format.VideoFormat.Height,
                FramesPerSecond = format.FrameRate.Denominator == 0
                    ? 0d
                    : format.FrameRate.Numerator / (double)format.FrameRate.Denominator
            })
            .ToArray();
        var withinBounds = formats
            .Where(format =>
                format.Width <= maximumWidth &&
                format.Height <= maximumHeight)
            .OrderBy(format => FormatScore(
                format.Width,
                format.Height,
                format.FramesPerSecond,
                maximumWidth,
                maximumHeight))
            .FirstOrDefault();
        if (withinBounds is not null)
        {
            return MakeEven(withinBounds.Width, withinBounds.Height);
        }

        var smallest = formats
            .OrderBy(format => (long)format.Width * format.Height)
            .ThenBy(format => Math.Abs(format.FramesPerSecond - 30d))
            .FirstOrDefault();
        if (smallest is not null)
        {
            return FitWithin(smallest.Width, smallest.Height, maximumWidth, maximumHeight);
        }

        var current = source.CurrentFormat?.VideoFormat;
        if (current is { Width: > 0, Height: > 0 })
        {
            return FitWithin(
                (int)current.Width,
                (int)current.Height,
                maximumWidth,
                maximumHeight);
        }
        throw new ReactionCameraUnavailableException(StreamUnavailable);
    }

    private static double FormatScore(
        int width,
        int height,
        double framesPerSecond,
        int targetWidth,
        int targetHeight) =>
        Math.Abs(targetWidth - width) / (double)targetWidth +
        Math.Abs(targetHeight - height) / (double)targetHeight +
        Math.Abs(30d - framesPerSecond) / 60d;

    private static SizeInt32 FitWithin(
        int width,
        int height,
        int maximumWidth,
        int maximumHeight)
    {
        var scale = Math.Min(
            1d,
            Math.Min(
                maximumWidth / (double)Math.Max(1, width),
                maximumHeight / (double)Math.Max(1, height)));
        return MakeEven(
            Math.Max(2, (int)Math.Floor(width * scale)),
            Math.Max(2, (int)Math.Floor(height * scale)));
    }

    private static SizeInt32 MakeEven(int width, int height) => new(
        Math.Max(2, width - width % 2),
        Math.Max(2, height - height % 2));

    internal static void Stop(MediaFrameReader reader)
    {
        try
        {
            reader.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Stop is best-effort during camera teardown. Reader disposal releases the device.
        }
    }

    internal static void Dispose(ReactionCameraReaderResources? resources)
    {
        if (resources is null) return;
        resources.Reader.Dispose();
        resources.Capture.Dispose();
    }
}
