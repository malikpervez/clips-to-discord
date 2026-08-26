using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipsToDiscord;

internal enum CaptureAudioSourceKind
{
    Game,
    Microphone,
    VoiceChat
}

internal static class CaptureAudioEndpointPolicy
{
    internal static bool TryReserve(ISet<string> endpointIds, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(endpointIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        return endpointIds.Add(endpointId);
    }
}

internal sealed record CaptureAudioMuxTrack(
    string Path,
    TimeSpan FirstPacketSystemRelativeTime);

internal sealed record ManualAudioTrackPlan(
    CaptureAudioSourceKind Kind,
    string SelectedDevice,
    DataFlow Flow,
    Role DefaultRole,
    string Path);

internal static class CaptureAudioDeviceCatalog
{
    internal static IReadOnlyList<string> GetOutputDeviceNames() =>
        GetDeviceNames(DataFlow.Render);

    internal static IReadOnlyList<string> GetInputDeviceNames() =>
        GetDeviceNames(DataFlow.Capture);

    private static IReadOnlyList<string> GetDeviceNames(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var names = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) names.Add(device.FriendlyName);
            }
            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            Log.Error("ClipCord could not enumerate Windows audio devices.", exception);
            return [];
        }
    }
}

internal sealed class CaptureAudioSession : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private readonly List<CaptureAudioTrack> _tracks;
    private bool _stopRequested;
    private bool _stopCompleted;
    private bool _disposed;

    private CaptureAudioSession(List<CaptureAudioTrack> tracks)
    {
        _tracks = tracks;
    }

    internal static CaptureAudioSession Start(CaptureSettings settings, string stagingDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var tracks = new List<CaptureAudioTrack>();
        var endpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var token = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var item in CreateTrackPlan(settings, stagingDirectory, token))
            {
                AddTrack(
                    tracks,
                    endpointIds,
                    item.Kind,
                    item.SelectedDevice,
                    item.Flow,
                    item.DefaultRole,
                    item.Path);
            }

            foreach (var track in tracks)
            {
                track.Start();
            }
            return new CaptureAudioSession(tracks);
        }
        catch
        {
            foreach (var track in tracks) track.Dispose();
            throw;
        }
    }

    internal static IReadOnlyList<ManualAudioTrackPlan> CreateTrackPlan(
        CaptureSettings settings,
        string stagingDirectory,
        string token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        settings = CaptureSettings.Normalize(settings);
        var plan = new List<ManualAudioTrackPlan>(3);
        if (settings.RecordGameAudio)
        {
            plan.Add(new ManualAudioTrackPlan(
                CaptureAudioSourceKind.Game,
                settings.GameAudioDevice,
                DataFlow.Render,
                Role.Multimedia,
                Path.Combine(stagingDirectory, $"manual-capture-{token}.game.wav")));
        }
        if (settings.IncludeMicrophone)
        {
            plan.Add(new ManualAudioTrackPlan(
                CaptureAudioSourceKind.Microphone,
                settings.MicrophoneDevice,
                DataFlow.Capture,
                Role.Multimedia,
                Path.Combine(stagingDirectory, $"manual-capture-{token}.microphone.wav")));
        }
        if (settings.IncludeVoiceChat)
        {
            plan.Add(new ManualAudioTrackPlan(
                CaptureAudioSourceKind.VoiceChat,
                settings.VoiceChatDevice,
                DataFlow.Render,
                Role.Communications,
                Path.Combine(stagingDirectory, $"manual-capture-{token}.chat.wav")));
        }
        return plan;
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stopCompleted) return;
        RequestStop();
        foreach (var track in _tracks)
        {
            await track.Completion
                .WaitAsync(StopTimeout, cancellationToken)
                .ConfigureAwait(false);
            track.ThrowIfFailed();
        }
        _stopCompleted = true;
    }

    internal void RequestStop()
    {
        if (_disposed || _stopRequested) return;
        _stopRequested = true;
        foreach (var track in _tracks) track.RequestStop();
    }

    internal IReadOnlyList<CaptureAudioMuxTrack> GetMuxTracks()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _tracks
            .Where(track => track.BytesRecorded > 0 && track.FirstPacketSystemRelativeTime.HasValue)
            .Select(track => new CaptureAudioMuxTrack(
                track.Path,
                track.FirstPacketSystemRelativeTime!.Value))
            .ToArray();
    }

    private static void AddTrack(
        List<CaptureAudioTrack> tracks,
        HashSet<string> endpointIds,
        CaptureAudioSourceKind kind,
        string selectedDevice,
        DataFlow flow,
        Role defaultRole,
        string path)
    {
        var device = ResolveDevice(kind, selectedDevice, flow, defaultRole);
        if (!CaptureAudioEndpointPolicy.TryReserve(endpointIds, device.ID))
        {
            // Game and voice chat can resolve to the same Windows render endpoint. Capturing it
            // twice would double every sound, so one loopback source intentionally represents both.
            device.Dispose();
            return;
        }

        try
        {
            IWaveIn capture = flow == DataFlow.Render
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device);
            tracks.Add(new CaptureAudioTrack(kind, device, capture, path));
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static MMDevice ResolveDevice(
        CaptureAudioSourceKind kind,
        string selectedDevice,
        DataFlow flow,
        Role defaultRole)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (IsDefaultSelection(kind, selectedDevice))
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(flow, defaultRole);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"The default {Describe(kind)} audio device is unavailable.",
                    exception);
            }
        }

        MMDevice? selected = null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (selected is null &&
                device.FriendlyName.Equals(selectedDevice, StringComparison.OrdinalIgnoreCase))
            {
                selected = device;
            }
            else
            {
                device.Dispose();
            }
        }
        if (selected is not null) return selected;
        throw new InvalidOperationException(
            $"The selected {Describe(kind)} audio device is no longer available. Choose another device in Capture.");
    }

    private static bool IsDefaultSelection(CaptureAudioSourceKind kind, string value) =>
        string.IsNullOrWhiteSpace(value) || kind switch
        {
            CaptureAudioSourceKind.Game => value.Equals(
                CaptureSettings.DefaultOutputDevice,
                StringComparison.OrdinalIgnoreCase),
            CaptureAudioSourceKind.Microphone => value.Equals(
                CaptureSettings.DefaultMicrophoneDevice,
                StringComparison.OrdinalIgnoreCase),
            CaptureAudioSourceKind.VoiceChat => value.Equals(
                CaptureSettings.DefaultVoiceChatDevice,
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static string Describe(CaptureAudioSourceKind kind) => kind switch
    {
        CaptureAudioSourceKind.Game => "game",
        CaptureAudioSourceKind.Microphone => "microphone",
        CaptureAudioSourceKind.VoiceChat => "voice-chat",
        _ => "capture"
    };

    public void Dispose()
    {
        if (_disposed) return;
        if (!_stopRequested)
        {
            RequestStop();
        }
        if (!_stopCompleted)
        {
            try
            {
                Task.WhenAll(_tracks.Select(track => track.Completion))
                    .Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Disposal is also used after a failed video start. The original exception remains
                // the actionable error and every private WAV is removed below.
            }
        }
        _disposed = true;
        foreach (var track in _tracks) track.Dispose();
    }
}

internal sealed class CaptureAudioTrack : IDisposable
{
    private readonly object _gate = new();
    private readonly CaptureAudioSourceKind _kind;
    private readonly MMDevice _device;
    private readonly IWaveIn _capture;
    private readonly WaveFileWriter _writer;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;
    private long _bytesRecorded;
    private long _firstPacketTicks = -1;
    private bool _started;
    private bool _writerClosed;
    private bool _disposed;

    internal CaptureAudioTrack(
        CaptureAudioSourceKind kind,
        MMDevice device,
        IWaveIn capture,
        string path)
    {
        _kind = kind;
        _device = device;
        _capture = capture;
        Path = path;
        _writer = new WaveFileWriter(path, capture.WaveFormat);
        _capture.DataAvailable += CaptureDataAvailable;
        _capture.RecordingStopped += CaptureRecordingStopped;
    }

    internal string Path { get; }
    internal long BytesRecorded => Interlocked.Read(ref _bytesRecorded);
    internal TimeSpan? FirstPacketSystemRelativeTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _firstPacketTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }
    internal Task Completion => _completion.Task;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _capture.StartRecording();
        _started = true;
    }

    internal void RequestStop()
    {
        if (_disposed)
        {
            _completion.TrySetResult();
            return;
        }
        if (!_started)
        {
            CloseWriter();
            _completion.TrySetResult();
            return;
        }
        try
        {
            _capture.StopRecording();
        }
        catch (Exception exception)
        {
            Complete(exception);
        }
    }

    internal void ThrowIfFailed()
    {
        if (_failure is null) return;
        throw new InvalidOperationException(
            $"ClipCord could not finish the {Describe(_kind)} audio capture.",
            _failure);
    }

    private void CaptureDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded <= 0) return;
        try
        {
            var packetDuration = TimeSpan.FromSeconds(
                eventArgs.BytesRecorded / (double)Math.Max(1, _capture.WaveFormat.AverageBytesPerSecond));
            var packetStart = MonotonicClock.Now - packetDuration;
            Interlocked.CompareExchange(ref _firstPacketTicks, packetStart.Ticks, -1);
            lock (_gate)
            {
                if (_writerClosed) return;
                _writer.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            }
            Interlocked.Add(ref _bytesRecorded, eventArgs.BytesRecorded);
        }
        catch (Exception exception)
        {
            Complete(exception);
            try { _capture.StopRecording(); } catch { }
        }
    }

    private void CaptureRecordingStopped(object? sender, StoppedEventArgs eventArgs) =>
        Complete(eventArgs.Exception);

    private void Complete(Exception? exception)
    {
        lock (_gate)
        {
            _failure ??= exception;
        }
        CloseWriter();
        _completion.TrySetResult();
    }

    private void CloseWriter()
    {
        lock (_gate)
        {
            if (_writerClosed) return;
            _writerClosed = true;
            _writer.Dispose();
        }
    }

    private static string Describe(CaptureAudioSourceKind kind) => kind switch
    {
        CaptureAudioSourceKind.Game => "game",
        CaptureAudioSourceKind.Microphone => "microphone",
        CaptureAudioSourceKind.VoiceChat => "voice-chat",
        _ => "capture"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.DataAvailable -= CaptureDataAvailable;
        _capture.RecordingStopped -= CaptureRecordingStopped;
        try { _capture.Dispose(); } catch { }
        CloseWriter();
        _device.Dispose();
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch
        {
            // The WAV is private staging data and startup recovery can remove it later.
        }
    }
}

internal static class CaptureAudioMuxer
{
    private static readonly TimeSpan MaximumClockDifference = TimeSpan.FromSeconds(30);

    internal static async Task MuxAsync(
        string videoPath,
        IReadOnlyList<CaptureAudioMuxTrack> tracks,
        TimeSpan videoStart,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (tracks.Count == 0)
        {
            File.Move(videoPath, outputPath, overwrite: false);
            return;
        }

        var ffmpeg = FfmpegCompressor.FindExecutable() ??
            throw new InvalidOperationException(
                "ClipCord's bundled FFmpeg tool is required to combine captured audio with video.");
        try
        {
            await FfmpegCompressor.RunAsync(
                ffmpeg,
                BuildArguments(videoPath, tracks, videoStart, outputPath),
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidOperationException("FFmpeg did not create the completed audio-video recording.");
            }
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    internal static IReadOnlyList<string> BuildArguments(
        string videoPath,
        IReadOnlyList<CaptureAudioMuxTrack> tracks,
        TimeSpan videoStart,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentOutOfRangeException.ThrowIfZero(tracks.Count);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath
        };
        arguments.AddRange(tracks.SelectMany(track => new[] { "-i", track.Path }));

        var chains = new List<string>();
        var labels = new List<string>();
        for (var index = 0; index < tracks.Count; index++)
        {
            var offset = tracks[index].FirstPacketSystemRelativeTime - videoStart;
            if (offset.Duration() > MaximumClockDifference)
            {
                throw new InvalidOperationException(
                    "The Windows audio and video clocks reported an invalid alignment difference.");
            }

            var label = $"[clipcordaudio{index}]";
            var chain = $"[{index + 1}:a:0]aresample=48000:async=1:first_pts=0";
            if (offset < TimeSpan.Zero)
            {
                chain += string.Create(
                    CultureInfo.InvariantCulture,
                    $",atrim=start={(-offset.TotalSeconds):0.000000}");
            }
            else if (offset > TimeSpan.Zero)
            {
                var delayMilliseconds = Math.Max(1L, (long)Math.Round(offset.TotalMilliseconds));
                chain += string.Create(
                    CultureInfo.InvariantCulture,
                    $",adelay={delayMilliseconds}:all=1");
            }
            chains.Add(chain + $",asetpts=PTS-STARTPTS{label}");
            labels.Add(label);
        }

        var mixedInputs = string.Concat(labels);
        var mix = tracks.Count == 1
            ? $"{mixedInputs}alimiter=limit=0.95:level=disabled,apad{FfmpegCompressor.MixedAudioLabel}"
            : $"{mixedInputs}amix=inputs={tracks.Count}:duration=longest:normalize=0," +
              $"alimiter=limit=0.95:level=disabled,apad{FfmpegCompressor.MixedAudioLabel}";
        chains.Add(mix);
        arguments.AddRange([
            "-filter_complex", string.Join(';', chains),
            "-map", "0:v:0",
            "-map", FfmpegCompressor.MixedAudioLabel,
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", $"{CaptureProfileCatalog.MixedAudioBitrateKbps}k",
            "-ar", "48000",
            "-ac", "2",
            "-shortest",
            "-movflags", "+faststart",
            outputPath
        ]);
        return arguments;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup remains inside ClipCord's private staging folder.
        }
    }
}

internal static class MonotonicClock
{
    internal static TimeSpan Now => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
}
