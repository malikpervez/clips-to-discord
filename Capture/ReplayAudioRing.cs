using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipsToDiscord;

internal sealed record ReplayAudioTrackPlan(
    CaptureAudioSourceKind Kind,
    string SelectedDevice,
    DataFlow Flow,
    Role DefaultRole);

internal sealed record PcmReplayPacket(
    ReadOnlyMemory<byte> Bytes,
    TimeSpan StartTimestamp,
    TimeSpan EndTimestamp)
{
    internal bool IsValid => Bytes.Length > 0 &&
        StartTimestamp >= TimeSpan.Zero && EndTimestamp > StartTimestamp;
}

internal sealed record PcmReplaySnapshot(
    IReadOnlyList<PcmReplayPacket> Packets,
    TimeSpan StartTimestamp,
    TimeSpan EndTimestamp,
    long TotalBytes);

internal sealed class PcmReplayRing
{
    private readonly object _gate = new();
    private readonly LinkedList<PcmReplayPacket> _packets = [];
    private readonly TimeSpan _retention;
    private readonly long _maximumBytes;
    private long _totalBytes;

    internal PcmReplayRing(TimeSpan retention, long maximumBytes)
    {
        if (retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _retention = retention;
        _maximumBytes = maximumBytes;
    }

    internal long TotalBytes { get { lock (_gate) return _totalBytes; } }

    internal void Append(PcmReplayPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!packet.IsValid) throw new ArgumentException("The replay audio packet is invalid.", nameof(packet));
        var owned = packet with { Bytes = packet.Bytes.ToArray() };
        lock (_gate)
        {
            if (_packets.Last is not null &&
                owned.StartTimestamp < _packets.Last.Value.StartTimestamp)
            {
                throw new InvalidOperationException("Replay audio timestamps must be monotonic.");
            }
            _packets.AddLast(owned);
            _totalBytes += owned.Bytes.Length;
            while (_packets.Count > 1 && _packets.First is not null && _packets.Last is not null &&
                   (_packets.Last.Value.EndTimestamp - _packets.First.Value.StartTimestamp > _retention ||
                    _totalBytes > _maximumBytes))
            {
                _totalBytes -= _packets.First.Value.Bytes.Length;
                _packets.RemoveFirst();
            }
        }
    }

    internal PcmReplaySnapshot? Snapshot(TimeSpan start, TimeSpan end)
    {
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        lock (_gate)
        {
            var packets = _packets
                .Where(packet => packet.EndTimestamp > start && packet.StartTimestamp < end)
                .ToArray();
            if (packets.Length == 0) return null;
            return new PcmReplaySnapshot(
                packets,
                packets[0].StartTimestamp,
                packets[^1].EndTimestamp,
                packets.Sum(packet => (long)packet.Bytes.Length));
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _packets.Clear();
            _totalBytes = 0;
        }
    }
}

internal sealed class ReplayAudioSession : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private readonly List<ReplayAudioTrack> _tracks;
    private bool _stopRequested;
    private bool _disposed;

    private ReplayAudioSession(List<ReplayAudioTrack> tracks)
    {
        _tracks = tracks;
    }

    internal static ReplayAudioSession Start(CaptureSettings settings)
    {
        settings = CaptureSettings.Normalize(settings);
        var tracks = new List<ReplayAudioTrack>();
        var endpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retention = TimeSpan.FromSeconds(settings.ReplaySeconds + 3);
        try
        {
            foreach (var item in CreateTrackPlan(settings))
            {
                AddTrack(
                    tracks,
                    endpointIds,
                    item.Kind,
                    item.SelectedDevice,
                    item.Flow,
                    item.DefaultRole,
                    retention);
            }
            foreach (var track in tracks) track.Start();
            return new ReplayAudioSession(tracks);
        }
        catch
        {
            foreach (var track in tracks) track.Dispose();
            throw;
        }
    }

    internal static IReadOnlyList<ReplayAudioTrackPlan> CreateTrackPlan(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = CaptureSettings.Normalize(settings);
        var plan = new List<ReplayAudioTrackPlan>(3);
        if (settings.RecordGameAudio)
        {
            plan.Add(new ReplayAudioTrackPlan(
                CaptureAudioSourceKind.Game,
                settings.GameAudioDevice,
                DataFlow.Render,
                Role.Multimedia));
        }
        if (settings.IncludeMicrophone)
        {
            plan.Add(new ReplayAudioTrackPlan(
                CaptureAudioSourceKind.Microphone,
                settings.MicrophoneDevice,
                DataFlow.Capture,
                Role.Multimedia));
        }
        if (settings.IncludeVoiceChat)
        {
            plan.Add(new ReplayAudioTrackPlan(
                CaptureAudioSourceKind.VoiceChat,
                settings.VoiceChatDevice,
                DataFlow.Render,
                Role.Communications));
        }
        return plan;
    }

    internal Exception? Failure => _tracks.Select(track => track.Failure).FirstOrDefault(value => value is not null);
    internal long TotalBytes => _tracks.Sum(track => track.TotalBytes);

    internal async Task<IReadOnlyList<CaptureAudioMuxTrack>> WriteSnapshotAsync(
        TimeSpan start,
        TimeSpan end,
        string stagingDirectory,
        string token,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var results = new List<CaptureAudioMuxTrack>();
        foreach (var track in _tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = track.Snapshot(start, end);
            if (snapshot is null) continue;
            var path = Path.Combine(
                stagingDirectory,
                $"replay-save-{token}.{Describe(track.Kind)}.wav");
            try
            {
                await Task.Run(() => track.WriteWave(path, snapshot), cancellationToken)
                    .ConfigureAwait(false);
                results.Add(new CaptureAudioMuxTrack(path, snapshot.StartTimestamp));
            }
            catch
            {
                TryDelete(path);
                foreach (var result in results) TryDelete(result.Path);
                throw;
            }
        }
        return results;
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequestStop();
        foreach (var track in _tracks)
        {
            await track.Completion.WaitAsync(StopTimeout, cancellationToken).ConfigureAwait(false);
        }
        if (Failure is { } failure)
        {
            throw new InvalidOperationException("A replay audio input stopped unexpectedly.", failure);
        }
    }

    internal void RequestStop()
    {
        if (_stopRequested) return;
        _stopRequested = true;
        foreach (var track in _tracks) track.RequestStop();
    }

    private static void AddTrack(
        List<ReplayAudioTrack> tracks,
        HashSet<string> endpointIds,
        CaptureAudioSourceKind kind,
        string selectedDevice,
        DataFlow flow,
        Role defaultRole,
        TimeSpan retention)
    {
        var device = ResolveDevice(kind, selectedDevice, flow, defaultRole);
        if (!CaptureAudioEndpointPolicy.TryReserve(endpointIds, device.ID))
        {
            device.Dispose();
            return;
        }
        try
        {
            IWaveIn capture = flow == DataFlow.Render
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device);
            tracks.Add(new ReplayAudioTrack(kind, device, capture, retention));
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
            return enumerator.GetDefaultAudioEndpoint(flow, defaultRole);
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
            CaptureAudioSourceKind.Game => value.Equals(CaptureSettings.DefaultOutputDevice, StringComparison.OrdinalIgnoreCase),
            CaptureAudioSourceKind.Microphone => value.Equals(CaptureSettings.DefaultMicrophoneDevice, StringComparison.OrdinalIgnoreCase),
            CaptureAudioSourceKind.VoiceChat => value.Equals(CaptureSettings.DefaultVoiceChatDevice, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static string Describe(CaptureAudioSourceKind kind) => kind switch
    {
        CaptureAudioSourceKind.Game => "game",
        CaptureAudioSourceKind.Microphone => "microphone",
        CaptureAudioSourceKind.VoiceChat => "chat",
        _ => "audio"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_stopRequested)
        {
            try { StopAsync().Wait(TimeSpan.FromSeconds(3)); }
            catch { }
        }
        _disposed = true;
        foreach (var track in _tracks) track.Dispose();
    }
}

internal sealed class ReplayAudioTrack : IDisposable
{
    private readonly CaptureAudioSourceKind _kind;
    private readonly MMDevice _device;
    private readonly IWaveIn _capture;
    private readonly PcmReplayRing _ring;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;
    private bool _started;
    private bool _disposed;

    internal ReplayAudioTrack(
        CaptureAudioSourceKind kind,
        MMDevice device,
        IWaveIn capture,
        TimeSpan retention)
    {
        _kind = kind;
        _device = device;
        _capture = capture;
        var maximumBytes = Math.Max(
            1L * 1024 * 1024,
            (long)Math.Ceiling(capture.WaveFormat.AverageBytesPerSecond * retention.TotalSeconds * 1.05d));
        _ring = new PcmReplayRing(retention, maximumBytes);
        _capture.DataAvailable += CaptureDataAvailable;
        _capture.RecordingStopped += CaptureRecordingStopped;
    }

    internal CaptureAudioSourceKind Kind => _kind;
    internal WaveFormat WaveFormat => _capture.WaveFormat;
    internal Exception? Failure => _failure;
    internal long TotalBytes => _ring.TotalBytes;
    internal Task Completion => _completion.Task;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _capture.StartRecording();
        _started = true;
    }

    internal PcmReplaySnapshot? Snapshot(TimeSpan start, TimeSpan end) => _ring.Snapshot(start, end);

    internal void WriteWave(string path, PcmReplaySnapshot snapshot)
    {
        using var writer = new WaveFileWriter(path, WaveFormat);
        foreach (var packet in snapshot.Packets)
        {
            writer.Write(packet.Bytes.Span);
        }
    }

    internal void RequestStop()
    {
        if (_disposed || !_started)
        {
            _completion.TrySetResult();
            return;
        }
        try { _capture.StopRecording(); }
        catch (Exception exception)
        {
            _failure ??= exception;
            _completion.TrySetResult();
        }
    }

    private void CaptureDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded <= 0) return;
        try
        {
            var duration = TimeSpan.FromSeconds(
                eventArgs.BytesRecorded / (double)Math.Max(1, WaveFormat.AverageBytesPerSecond));
            var end = MonotonicClock.Now;
            _ring.Append(new PcmReplayPacket(
                eventArgs.Buffer.AsMemory(0, eventArgs.BytesRecorded),
                end - duration,
                end));
        }
        catch (Exception exception)
        {
            _failure ??= exception;
            try { _capture.StopRecording(); } catch { }
        }
    }

    private void CaptureRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        _failure ??= eventArgs.Exception;
        _completion.TrySetResult();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.DataAvailable -= CaptureDataAvailable;
        _capture.RecordingStopped -= CaptureRecordingStopped;
        try { _capture.Dispose(); } catch { }
        _device.Dispose();
        _ring.Clear();
    }
}
