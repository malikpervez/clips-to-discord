namespace ClipsToDiscord;

public static class CompressionTargetPlanner
{
    internal const int MinimumVideoKbps = 180;

    public static IReadOnlyList<int> Build(int configuredTargetMb)
    {
        var targets = new List<int>();
        var target = Math.Clamp(configuredTargetMb, 1, 100);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!targets.Contains(target)) targets.Add(target);
            if (target == 1) break;

            target = target > 9
                ? Math.Max(9, target / 2)
                : Math.Max(1, (int)Math.Floor(target * 0.67));
        }
        return targets;
    }

    internal static IReadOnlyList<int> BuildAchievable(int configuredTargetMb, TimeSpan duration) =>
        Build(configuredTargetMb)
            .Where(targetMb => TryCreateBitrates(duration, targetMb, out _))
            .ToArray();

    internal static bool TryCreateBitrates(
        TimeSpan duration,
        int targetMegabytes,
        out CompressionBitrates bitrates)
    {
        bitrates = default;
        if (duration <= TimeSpan.Zero || targetMegabytes is < 1 or > 100) return false;

        var targetBytes = (long)targetMegabytes * 1024 * 1024;
        var totalKbps = Math.Floor((targetBytes * 8d / duration.TotalSeconds / 1000d) * 0.94d);
        var audioKbps = totalKbps >= 500 ? 96 : 64;
        var availableVideoKbps = totalKbps - audioKbps;
        if (availableVideoKbps < MinimumVideoKbps) return false;

        bitrates = new CompressionBitrates(
            (int)Math.Floor(Math.Min(6000, availableVideoKbps)),
            audioKbps);
        return true;
    }

    internal readonly record struct CompressionBitrates(int VideoKbps, int AudioKbps);
}
