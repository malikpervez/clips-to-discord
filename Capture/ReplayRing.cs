namespace ClipsToDiscord;

internal sealed record EncodedReplaySegment(
    ReadOnlyMemory<byte> Mp4Bytes,
    TimeSpan StartTimestamp,
    TimeSpan EndTimestamp)
{
    internal TimeSpan Duration => EndTimestamp - StartTimestamp;
    internal long ByteLength => Mp4Bytes.Length;
    internal bool IsValid => Mp4Bytes.Length > 0 &&
        StartTimestamp >= TimeSpan.Zero && EndTimestamp > StartTimestamp;
}

internal sealed record EncodedReplaySnapshot(
    IReadOnlyList<EncodedReplaySegment> Segments,
    TimeSpan RequestedDuration,
    TimeSpan ActualDuration,
    long TotalBytes,
    TimeSpan StartTimestamp,
    TimeSpan EndTimestamp);

internal sealed class EncodedReplayRing
{
    private readonly object _gate = new();
    private readonly LinkedList<EncodedReplaySegment> _segments = [];
    private readonly TimeSpan _retention;
    private readonly long _maximumBytes;
    private long _totalBytes;

    internal EncodedReplayRing(TimeSpan replayDuration, long maximumBytes)
    {
        if (replayDuration < TimeSpan.FromSeconds(CaptureProfile.MinimumReplaySeconds) ||
            replayDuration > TimeSpan.FromSeconds(CaptureProfile.MaximumReplaySeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(replayDuration));
        }
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ReplayDuration = replayDuration;
        // Retain one extra two-second GOP so a snapshot can always begin on a decodable boundary.
        _retention = replayDuration + TimeSpan.FromSeconds(2);
        _maximumBytes = maximumBytes;
    }

    internal TimeSpan ReplayDuration { get; }
    internal int SegmentCount { get { lock (_gate) return _segments.Count; } }
    internal long TotalBytes { get { lock (_gate) return _totalBytes; } }
    internal TimeSpan BufferedDuration
    {
        get
        {
            lock (_gate)
            {
                return _segments.First is null || _segments.Last is null
                    ? TimeSpan.Zero
                    : _segments.Last.Value.EndTimestamp - _segments.First.Value.StartTimestamp;
            }
        }
    }

    internal void Append(EncodedReplaySegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!segment.IsValid) throw new ArgumentException("The replay segment is invalid.", nameof(segment));
        // Own the encoded bytes so an encoder can immediately reuse or dispose its output stream.
        var owned = segment with { Mp4Bytes = segment.Mp4Bytes.ToArray() };
        lock (_gate)
        {
            if (_segments.Last is not null &&
                owned.StartTimestamp < _segments.Last.Value.EndTimestamp)
            {
                throw new InvalidOperationException("Replay segment timestamps must be monotonic and non-overlapping.");
            }
            _segments.AddLast(owned);
            _totalBytes += owned.ByteLength;
            EvictCore();
        }
    }

    internal EncodedReplaySnapshot? Snapshot()
    {
        lock (_gate)
        {
            if (_segments.Count == 0 || _segments.Last is null) return null;
            var end = _segments.Last.Value.EndTimestamp;
            return SnapshotCore(end - ReplayDuration, end);
        }
    }

    internal EncodedReplaySnapshot? Snapshot(TimeSpan requestedStart, TimeSpan requestedEnd)
    {
        if (requestedStart < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestedStart));
        if (requestedEnd <= requestedStart) throw new ArgumentOutOfRangeException(nameof(requestedEnd));
        lock (_gate) return SnapshotCore(requestedStart, requestedEnd);
    }

    private EncodedReplaySnapshot? SnapshotCore(TimeSpan requestedStart, TimeSpan requestedEnd)
    {
        var selected = new List<EncodedReplaySegment>();
        long bytes = 0;
        for (var node = _segments.First; node is not null; node = node.Next)
        {
            var segment = node.Value;
            if (segment.EndTimestamp <= requestedStart) continue;
            if (segment.StartTimestamp >= requestedEnd) break;
            selected.Add(segment);
            bytes += segment.ByteLength;
        }
        if (selected.Count == 0) return null;
        var actualStart = selected[0].StartTimestamp;
        var actualEnd = selected[^1].EndTimestamp;
        return new EncodedReplaySnapshot(
            selected,
            requestedEnd - requestedStart,
            actualEnd - actualStart,
            bytes,
            actualStart,
            actualEnd);
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _segments.Clear();
            _totalBytes = 0;
        }
    }

    private void EvictCore()
    {
        while (_segments.First is not null && _segments.Last is not null &&
               (_segments.Last.Value.EndTimestamp - _segments.First.Value.StartTimestamp > _retention ||
                _totalBytes > _maximumBytes))
        {
            // Always retain the newest complete segment, even if one pathological segment is above
            // the byte budget. The engine will surface that pressure rather than return no replay.
            if (_segments.Count == 1) break;
            _totalBytes -= _segments.First.Value.ByteLength;
            _segments.RemoveFirst();
        }
    }
}

internal static class ReplayMemoryPolicy
{
    private const long MinimumBudgetBytes = 64L * 1024 * 1024;
    private const long MaximumBudgetBytes = 3L * 1024 * 1024 * 1024;

    internal static long GetMaximumBytes(CaptureSettings settings)
    {
        settings = CaptureSettings.Normalize(settings);
        var estimate = CaptureProfileCatalog.Estimate(settings.Profile, settings.HasAudio);
        return ClampEncodedBudget(estimate.UpperBoundBytes);
    }

    internal static long GetGameplayMaximumBytes(CaptureSettings settings)
    {
        settings = CaptureSettings.Normalize(settings);
        var gameplay = settings.Profile with { IncludeReactionCamera = false };
        return ClampEncodedBudget(CaptureProfileCatalog.Estimate(gameplay, includeAudio: false).UpperBoundBytes);
    }

    internal static long GetReactionCameraMaximumBytes(CaptureSettings settings)
    {
        settings = CaptureSettings.Normalize(settings);
        if (!settings.IncludeReactionCamera) return 0;
        var seconds = settings.ReplaySeconds + ReplayVideoSegmentEncoder.SegmentDuration.TotalSeconds;
        var bytes = CaptureProfileCatalog.ReactionCameraBitrateKbps * 1000d / 8d * seconds;
        return Math.Clamp(
            bytes >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(bytes * 1.12d),
            16L * 1024 * 1024,
            512L * 1024 * 1024);
    }

    private static long ClampEncodedBudget(long upperBoundBytes)
    {
        var withGopAndContainerSlack = Math.Ceiling(upperBoundBytes * 1.12d);
        return Math.Clamp(
            withGopAndContainerSlack >= long.MaxValue
                ? long.MaxValue
                : (long)withGopAndContainerSlack,
            MinimumBudgetBytes,
            MaximumBudgetBytes);
    }

    internal static long GetEstimatedResidentBytes(CaptureSettings settings)
    {
        settings = CaptureSettings.Normalize(settings);
        var encoded = CaptureProfileCatalog.Estimate(settings.Profile, settings.HasAudio).ExpectedBytes;
        // WASAPI commonly supplies 48 kHz 32-bit float. Keep enabled sources as raw PCM so the
        // save-time mixer can align their callback-derived monotonic boundaries into one AAC mix.
        var bytesPerSecond = 0L;
        if (settings.RecordGameAudio) bytesPerSecond += 48_000L * 4 * 2;
        if (settings.IncludeVoiceChat) bytesPerSecond += 48_000L * 4 * 2;
        if (settings.IncludeMicrophone) bytesPerSecond += 48_000L * 4;
        var pcm = bytesPerSecond * (settings.ReplaySeconds + 3L);
        return encoded >= long.MaxValue - pcm ? long.MaxValue : encoded + pcm;
    }
}
