namespace ClipsToDiscord;

public static class CompressionTargetPlanner
{
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
}
