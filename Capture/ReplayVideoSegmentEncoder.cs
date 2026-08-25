using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace ClipsToDiscord;

internal sealed class ReplayVideoSegmentEncoder : IDisposable
{
    private const int NoSamplesProcessedHResult = unchecked((int)0xC00D4A44);
    internal static readonly TimeSpan SegmentDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private readonly GraphicsCaptureItem? _captureItem;
    private readonly Func<CancellationToken, Task<IVideoFrameSource>>? _frameSourceFactory;
    private readonly SizeInt32 _outputSize;
    private readonly uint _bitrate;
    private readonly uint _frameRate;
    private readonly bool _suppressSystemBorder;
    private readonly EncodedReplayRing _ring;
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly TaskCompletionSource _pipelineReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private IVideoFrameSource? _frameSource;
    private Task? _encodingTask;
    private Exception? _failure;
    private bool _disposed;

    internal ReplayVideoSegmentEncoder(
        GraphicsCaptureItem captureItem,
        SizeInt32 outputSize,
        uint bitrate,
        uint frameRate,
        bool suppressSystemBorder,
        EncodedReplayRing ring)
    {
        _captureItem = captureItem ?? throw new ArgumentNullException(nameof(captureItem));
        _outputSize = outputSize;
        _bitrate = bitrate;
        _frameRate = frameRate;
        _suppressSystemBorder = suppressSystemBorder;
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
    }

    internal ReplayVideoSegmentEncoder(
        Func<CancellationToken, Task<IVideoFrameSource>> frameSourceFactory,
        SizeInt32 outputSize,
        uint bitrate,
        uint frameRate,
        EncodedReplayRing ring)
    {
        _frameSourceFactory = frameSourceFactory ?? throw new ArgumentNullException(nameof(frameSourceFactory));
        _outputSize = outputSize;
        _bitrate = bitrate;
        _frameRate = frameRate;
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
    }

    internal event EventHandler? SegmentReady;
    internal Exception? Failure { get { lock (_gate) return _failure; } }
    internal bool IsRunning => _encodingTask is { IsCompleted: false };

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_encodingTask is not null)
            {
                throw new InvalidOperationException("This replay encoder has already been started.");
            }
            _encodingTask = Task.Run(RunAsync);
        }
        try
        {
            // Readiness means the capture session and GPU resources exist. A real game may pause
            // presentation while it is launching or unfocused, so waiting for a complete GOP here
            // would incorrectly turn a temporary lack of frames into a permanent startup failure.
            await _pipelineReady.Task.WaitAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RequestStop();
            Task? encodingTask;
            lock (_gate) encodingTask = _encodingTask;
            if (encodingTask is not null)
            {
                try { await encodingTask.ConfigureAwait(false); }
                catch
                {
                    // The readiness failure or caller cancellation remains the actionable startup
                    // result. Joining here is still required so a camera-off confirmation cannot
                    // race ahead of the frame source actually releasing the webcam.
                }
            }
            throw;
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequestStop();
        Task? task;
        lock (_gate) task = _encodingTask;
        if (task is not null)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var failure = Failure;
        if (failure is not null)
        {
            throw new InvalidOperationException("ClipCord's replay video encoder stopped unexpectedly.", failure);
        }
    }

    internal void RequestStop()
    {
        if (_stopCancellation.IsCancellationRequested) return;
        _stopCancellation.Cancel();
        lock (_gate) _frameSource?.Stop();
    }

    private async Task RunAsync()
    {
        try
        {
            if (_frameSourceFactory is not null)
            {
                using var frameSource = await _frameSourceFactory(_stopCancellation.Token)
                    .ConfigureAwait(false);
                await RunFrameSourceAsync(frameSource).ConfigureAwait(false);
            }
            else
            {
                var captureItem = _captureItem ??
                    throw new InvalidOperationException("ClipCord did not receive a replay capture item.");
                using var d3dDevice = D3D11.D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
                using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
                using var winRtDevice = Direct3DInterop.CreateWinRtDevice(dxgiDevice);
                using var frameSource = new CaptureFrameSource(
                    d3dDevice,
                    winRtDevice,
                    captureItem,
                    captureItem.Size,
                    _suppressSystemBorder);
                await RunFrameSourceAsync(frameSource).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopCancellation.IsCancellationRequested) { }
        catch (COMException exception) when (
            exception.HResult == NoSamplesProcessedHResult &&
            _stopCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate) _failure ??= exception;
            _pipelineReady.TrySetException(exception);
            Log.Error("ClipCord's in-memory replay video encoder failed.", exception);
        }
        finally
        {
            lock (_gate) _frameSource = null;
            if (!_pipelineReady.Task.IsCompleted)
            {
                _pipelineReady.TrySetException(new InvalidOperationException(
                    "ClipCord could not initialize the replay capture pipeline."));
            }
        }
    }

    private async Task RunFrameSourceAsync(IVideoFrameSource frameSource)
    {
        lock (_gate) _frameSource = frameSource;
        _pipelineReady.TrySetResult();
        try
        {
            while (!_stopCancellation.IsCancellationRequested)
            {
                var segment = await EncodeNextSegmentAsync(
                    frameSource,
                    _stopCancellation.Token).ConfigureAwait(false);
                if (segment is null) break;
                _ring.Append(segment);
                SegmentReady?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_frameSource, frameSource)) _frameSource = null;
            }
        }
    }

    private async Task<EncodedReplaySegment?> EncodeNextSegmentAsync(
        IVideoFrameSource frameSource,
        CancellationToken cancellationToken)
    {
        var input = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8,
            (uint)frameSource.SourceSize.Width,
            (uint)frameSource.SourceSize.Height);
        var descriptor = new VideoStreamDescriptor(input);
        var source = new MediaStreamSource(descriptor) { BufferTime = TimeSpan.Zero };
        TimeSpan? segmentStart = null;
        TimeSpan? segmentEnd = null;
        var samples = 0;

        source.Starting += MediaSourceStarting;
        source.SampleRequested += MediaSourceSampleRequested;
        using var outputStream = new InMemoryRandomAccessStream();
        try
        {
            var profile = new MediaEncodingProfile();
            profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
            profile.Video.Subtype = MediaEncodingSubtypes.H264;
            profile.Video.Width = (uint)_outputSize.Width;
            profile.Video.Height = (uint)_outputSize.Height;
            profile.Video.Bitrate = _bitrate;
            profile.Video.FrameRate.Numerator = _frameRate;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                source,
                outputStream,
                profile);
            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException(
                    $"Windows could not prepare the replay hardware encoder ({prepared.FailureReason}).");
            }
            await prepared.TranscodeAsync();
            if (samples == 0 || segmentStart is null || segmentEnd is null ||
                segmentEnd <= segmentStart || outputStream.Size == 0)
            {
                return null;
            }
            if (outputStream.Size > int.MaxValue)
            {
                throw new InvalidOperationException("A replay segment exceeded ClipCord's in-memory safety limit.");
            }
            var bytes = new byte[(int)outputStream.Size];
            using var reader = new DataReader(outputStream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync((uint)bytes.Length);
            if (loaded != bytes.Length)
            {
                throw new EndOfStreamException("Windows returned an incomplete in-memory replay segment.");
            }
            reader.ReadBytes(bytes);
            return new EncodedReplaySegment(bytes, segmentStart.Value, segmentEnd.Value);
        }
        finally
        {
            source.Starting -= MediaSourceStarting;
            source.SampleRequested -= MediaSourceSampleRequested;
        }

        void MediaSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            try
            {
                using var frame = frameSource.WaitForNewFrame();
                args.Request.SetActualStartPosition(frame?.SystemRelativeTime ?? TimeSpan.Zero);
            }
            catch (Exception exception)
            {
                args.Request.SetActualStartPosition(TimeSpan.Zero);
                LatchFailure("ClipCord could not begin an in-memory replay segment.", exception);
                frameSource.Stop();
            }
        }

        void MediaSourceSampleRequested(
            MediaStreamSource sender,
            MediaStreamSourceSampleRequestedEventArgs args)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    args.Request.Sample = null;
                    return;
                }
                using var frame = frameSource.WaitForNewFrame();
                if (frame is null)
                {
                    args.Request.Sample = null;
                    return;
                }
                segmentStart ??= frame.SystemRelativeTime;
                if (frame.SystemRelativeTime - segmentStart.Value >= SegmentDuration)
                {
                    segmentEnd = frame.SystemRelativeTime;
                    args.Request.Sample = null;
                    return;
                }
                segmentEnd = frame.SystemRelativeTime + TimeSpan.FromSeconds(1d / _frameRate);
                args.Request.Sample = MediaStreamSample.CreateFromDirect3D11Surface(
                    frame.Surface,
                    frame.SystemRelativeTime);
                samples++;
            }
            catch (Exception exception)
            {
                LatchFailure("ClipCord could not encode an in-memory replay frame.", exception);
                args.Request.Sample = null;
                frameSource.Stop();
            }
        }
    }

    private void LatchFailure(string message, Exception exception)
    {
        var shouldLog = false;
        lock (_gate)
        {
            if (_failure is null)
            {
                _failure = new InvalidOperationException(message, exception);
                shouldLog = true;
            }
        }
        if (shouldLog) Log.Error(message, exception);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RequestStop();
        Task? encodingTask;
        lock (_gate) encodingTask = _encodingTask;
        if (encodingTask is null || encodingTask.IsCompleted)
        {
            _stopCancellation.Dispose();
        }
        else
        {
            _ = encodingTask.ContinueWith(
                _ => _stopCancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        SegmentReady = null;
    }
}
