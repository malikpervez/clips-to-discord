using System.Security.Cryptography;
using ClipsToDiscord;

internal static class RoutingWatchedSourceAdapterTests
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        AssertAdapterCatalogIsExact();
        await AssertSteelSeriesUsesOnlyRootLeavesAsync(Path.Combine(root, "steelseries"));
        await AssertNvidiaUsesExactlyGameAndLeafAsync(Path.Combine(root, "nvidia"));
        await AssertRootIdentitySeparatesSourcesAsync(Path.Combine(root, "root-identity"));
        await AssertReplacementAndMutationFailExactRevalidationAsync(
            Path.Combine(root, "replacement"));
        await AssertReparsePointsFailClosedWhenSupportedAsync(
            Path.Combine(root, "reparse"));
        AssertNativeIdentityValidationIsCanonical();
    }

    private static void AssertAdapterCatalogIsExact()
    {
        Assert(RoutingWatchedSourceAdapters.CoveredSources.Count == 2 &&
               RoutingWatchedSourceAdapters.CoveredSources.Contains(
                   ClipCaptureSource.SteelSeriesGg) &&
               RoutingWatchedSourceAdapters.CoveredSources.Contains(
                   ClipCaptureSource.Nvidia) &&
               RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg).Source ==
                   ClipCaptureSource.SteelSeriesGg &&
               RoutingWatchedSourceAdapters.Get(ClipCaptureSource.Nvidia).Source ==
                   ClipCaptureSource.Nvidia,
            "The watched-source catalog must expose exactly the two supported adapters.");
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = RoutingWatchedSourceAdapters.Get((ClipCaptureSource)999));
    }

    private static async Task AssertSteelSeriesUsesOnlyRootLeavesAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var fileName = "Battlefield 6 2026.08.29 - 12.01.02.03.DVR.mp4";
        var bytes = new byte[] { 1, 3, 5, 7, 9, 11 };
        var clip = await WriteAsync(Path.Combine(clips, fileName), bytes);
        _ = await WriteAsync(Path.Combine(clips, "ignore.txt"), [2, 4, 6]);
        _ = await WriteAsync(
            Path.Combine(clips, "Nested", "Nested Game 2026.08.29.mp4"),
            [8, 10, 12]);

        var adapter = RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg);
        var candidates = adapter.EnumerateCandidates(
            clips + Path.DirectorySeparatorChar);
        Assert(candidates.Count == 1 &&
               candidates[0].Equals(clip, StringComparison.OrdinalIgnoreCase),
            "SteelSeries discovery must include only direct MP4 leaves.");

        var result = await adapter.OpenAndFingerprintAsync(clips, clip);
        Assert(result.Source == ClipCaptureSource.SteelSeriesGg &&
               result.CanonicalRoot.Equals(
                   Path.TrimEndingDirectorySeparator(Path.GetFullPath(clips)),
                   StringComparison.OrdinalIgnoreCase) &&
               result.PortableRelativePath == fileName &&
               result.GameName == "Battlefield 6" &&
               result.DisplayFileName == fileName &&
               IsCanonicalSha256(result.RootIdentitySha256) &&
               result.NativeFileIdentity.FileIdHex.Length == 16 &&
               result.NativeFileIdentity.FileIdHex.All(IsLowerHex) &&
               result.NativeFileIdentity.ByteLength == bytes.Length &&
               result.NativeFileIdentity.CreationUtcTicks > 0 &&
               result.NativeFileIdentity.LastWriteUtcTicks > 0 &&
               result.ContentSha256 == Hash(bytes),
            "SteelSeries evidence must expose the canonical root, portable leaf, game, native identity, and content hash.");
        Assert(await adapter.RevalidateAsync(result) == result,
            "An unchanged SteelSeries occurrence must pass exact revalidation.");

        await AssertThrowsAsync<InvalidDataException>(() =>
            adapter.OpenAndFingerprintAsync(
                clips,
                Path.Combine(clips, "Nested", "Nested Game 2026.08.29.mp4")));
        var outside = await WriteAsync(Path.Combine(root, "outside.mp4"), [13, 14]);
        await AssertThrowsAsync<InvalidDataException>(() =>
            adapter.OpenAndFingerprintAsync(clips, outside));
    }

    private static async Task AssertNvidiaUsesExactlyGameAndLeafAsync(string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var valid = await WriteAsync(
            Path.Combine(clips, "Duskfade", "Highlight 2026.08.29.MP4"),
            [21, 22, 23, 24]);
        var flat = await WriteAsync(Path.Combine(clips, "flat.mp4"), [25]);
        var deep = await WriteAsync(
            Path.Combine(clips, "Duskfade", "Nested", "deep.mp4"),
            [26]);
        var managed = await WriteAsync(
            Path.Combine(clips, "uploaded", "managed.mp4"),
            [27]);
        _ = await WriteAsync(
            Path.Combine(clips, "local-only", "managed.mp4"),
            [28]);
        _ = await WriteAsync(
            Path.Combine(clips, ".clipcord-editing", "managed.mp4"),
            [29]);
        _ = await WriteAsync(
            Path.Combine(clips, "Duskfade", "ignore.webm"),
            [30]);

        var adapter = RoutingWatchedSourceAdapters.Get(ClipCaptureSource.Nvidia);
        var candidates = adapter.EnumerateCandidates(clips);
        Assert(candidates.Count == 1 &&
               candidates[0].Equals(valid, StringComparison.OrdinalIgnoreCase),
            "NVIDIA discovery must include exactly root/game/leaf MP4s and exclude ClipCord-managed directories.");

        var result = await adapter.OpenAndFingerprintAsync(clips, valid);
        Assert(result.Source == ClipCaptureSource.Nvidia &&
               result.PortableRelativePath == "Duskfade/Highlight 2026.08.29.MP4" &&
               result.GameName == "Duskfade" &&
               result.DisplayFileName == "Highlight 2026.08.29.MP4" &&
               result.ContentSha256 == Hash([21, 22, 23, 24]) &&
               await adapter.RevalidateAsync(result) == result,
            "NVIDIA evidence must preserve its exact game/leaf relative path and stable identity.");

        await AssertThrowsAsync<InvalidDataException>(() =>
            adapter.OpenAndFingerprintAsync(clips, flat));
        await AssertThrowsAsync<InvalidDataException>(() =>
            adapter.OpenAndFingerprintAsync(clips, deep));
        await AssertThrowsAsync<InvalidDataException>(() =>
            adapter.OpenAndFingerprintAsync(clips, managed));
    }

    private static async Task AssertRootIdentitySeparatesSourcesAsync(string root)
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        var name = "Game 2026.08.29 - 12.00.00.00.DVR.mp4";
        var payload = new byte[] { 31, 32, 33, 34 };
        var firstPath = await WriteAsync(Path.Combine(firstRoot, name), payload);
        var secondPath = await WriteAsync(Path.Combine(secondRoot, name), payload);
        var adapter = RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg);
        var first = await adapter.OpenAndFingerprintAsync(firstRoot, firstPath);
        var second = await adapter.OpenAndFingerprintAsync(secondRoot, secondPath);

        Assert(first.ContentSha256 == second.ContentSha256 &&
               first.RootIdentitySha256 != second.RootIdentitySha256 &&
               first.NativeFileIdentity.FileIdHex != second.NativeFileIdentity.FileIdHex,
            "Identical bytes under different watched roots must retain distinct root and native identities.");
    }

    private static async Task AssertReplacementAndMutationFailExactRevalidationAsync(
        string root)
    {
        var clips = Directory.CreateDirectory(Path.Combine(root, "clips")).FullName;
        var name = "Game 2026.08.29 - 12.00.00.00.DVR.mp4";
        var path = await WriteAsync(Path.Combine(clips, name), [41, 42, 43, 44]);
        var adapter = RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg);
        var prior = await adapter.OpenAndFingerprintAsync(clips, path);

        var held = Path.Combine(clips, "held-original.mp4");
        File.Move(path, held, overwrite: true);
        _ = await WriteAsync(path, [44, 43, 42, 41]);
        TrySetExactTimestamps(path, prior.NativeFileIdentity);
        var replacement = await adapter.OpenAndFingerprintAsync(clips, path);
        Assert(replacement.NativeFileIdentity.ByteLength ==
                   prior.NativeFileIdentity.ByteLength &&
               replacement.NativeFileIdentity.FileIdHex !=
                   prior.NativeFileIdentity.FileIdHex &&
               replacement.ContentSha256 != prior.ContentSha256,
            "Replacing a path with same-length bytes must create different native and content evidence.");
        await AssertThrowsAsync<InvalidDataException>(() =>
            adapter.RevalidateAsync(prior));

        var replacementPrior = replacement;
        await File.WriteAllBytesAsync(path, [41, 43, 42, 44]);
        await AssertThrowsAsync<InvalidDataException>(() =>
            adapter.RevalidateAsync(replacementPrior));

        var uppercaseId = replacementPrior with
        {
            NativeFileIdentity = replacementPrior.NativeFileIdentity with
            {
                FileIdHex = replacementPrior.NativeFileIdentity.FileIdHex.ToUpperInvariant()
            }
        };
        if (uppercaseId.NativeFileIdentity.FileIdHex.Any(character =>
                character is >= 'A' and <= 'F'))
        {
            await AssertThrowsAsync<InvalidDataException>(() =>
                adapter.RevalidateAsync(uppercaseId));
        }
    }

    private static async Task AssertReparsePointsFailClosedWhenSupportedAsync(string root)
    {
        var targetRoot = Directory.CreateDirectory(Path.Combine(root, "targets")).FullName;
        var targetFile = await WriteAsync(
            Path.Combine(targetRoot, "target.mp4"),
            [51, 52, 53]);

        var steelRoot = Directory.CreateDirectory(Path.Combine(root, "steel-clips")).FullName;
        var linkedFile = Path.Combine(steelRoot, "Linked Game 2026.08.29.mp4");
        if (TryCreateFileSymbolicLink(linkedFile, targetFile))
        {
            var steel = RoutingWatchedSourceAdapters.Get(ClipCaptureSource.SteelSeriesGg);
            Assert(!steel.EnumerateCandidates(steelRoot).Any(path =>
                    path.Equals(linkedFile, StringComparison.OrdinalIgnoreCase)),
                "SteelSeries discovery must skip reparse-point files.");
            await AssertThrowsAsync<IOException>(() =>
                steel.OpenAndFingerprintAsync(steelRoot, linkedFile));
        }

        var nvidiaRoot = Directory.CreateDirectory(Path.Combine(root, "nvidia-clips")).FullName;
        var targetGame = Directory.CreateDirectory(Path.Combine(targetRoot, "target-game")).FullName;
        var targetGameClip = await WriteAsync(Path.Combine(targetGame, "linked.mp4"), [54]);
        var linkedGame = Path.Combine(nvidiaRoot, "LinkedGame");
        if (TryCreateDirectorySymbolicLink(linkedGame, targetGame))
        {
            var nvidia = RoutingWatchedSourceAdapters.Get(ClipCaptureSource.Nvidia);
            Assert(!nvidia.EnumerateCandidates(nvidiaRoot).Any(),
                "NVIDIA discovery must skip reparse-point game directories.");
            await AssertThrowsAsync<IOException>(() =>
                nvidia.OpenAndFingerprintAsync(
                    nvidiaRoot,
                    Path.Combine(linkedGame, Path.GetFileName(targetGameClip))));
        }

        var linkedRoot = Path.Combine(root, "linked-root");
        if (TryCreateDirectorySymbolicLink(linkedRoot, steelRoot))
        {
            AssertThrows<IOException>(() =>
                _ = RoutingWatchedSourceAdapters
                    .Get(ClipCaptureSource.SteelSeriesGg)
                    .EnumerateCandidates(linkedRoot));
        }
    }

    private static void AssertNativeIdentityValidationIsCanonical()
    {
        RoutingWatchedNativeFileIdentityModel.Validate(new(
            0x12345678,
            "0123456789abcdef",
            1,
            1,
            1));
        AssertThrows<InvalidDataException>(() =>
            RoutingWatchedNativeFileIdentityModel.Validate(new(
                0x12345678,
                "0123456789ABCDEF",
                1,
                1,
                1)));
        AssertThrows<InvalidDataException>(() =>
            RoutingWatchedNativeFileIdentityModel.Validate(new(
                0x12345678,
                "0123456789abcde",
                1,
                1,
                1)));
        AssertThrows<InvalidDataException>(() =>
            RoutingWatchedNativeFileIdentityModel.Validate(new(
                0x12345678,
                "0123456789abcdef",
                0,
                1,
                1)));
    }

    private static async Task<string> WriteAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        return Path.GetFullPath(path);
    }

    private static void TrySetExactTimestamps(
        string path,
        RoutingWatchedNativeFileIdentity identity)
    {
        try
        {
            File.SetCreationTimeUtc(path,
                new DateTime(identity.CreationUtcTicks, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(path,
                new DateTime(identity.LastWriteUtcTicks, DateTimeKind.Utc));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentOutOfRangeException)
        {
            // The stable native file id and content hash still make replacement fail closed on
            // filesystems that do not permit callers to restore creation timestamps.
        }
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            _ = File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsCanonicalSha256(string value) =>
        value.Length == 64 && value.All(IsLowerHex);

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
