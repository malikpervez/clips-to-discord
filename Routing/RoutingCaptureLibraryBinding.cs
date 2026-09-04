using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClipsToDiscord;

/// <summary>
/// Privacy-safe durable identity for the ClipCord Capture library. The two hashes deliberately
/// keep the configured location separate from the Windows directory object at that location so
/// both a settings redirect and a delete/recreate replacement fail closed. No path, user name,
/// volume label, or other reversible filesystem value is persisted.
/// </summary>
internal sealed record RoutingCaptureLibraryBinding(
    string CanonicalPathFingerprint,
    string NativeDirectoryIdentityFingerprint);

internal static class RoutingCaptureLibraryBindingModel
{
    private const string PathFingerprintDomain = "clipcord-capture-library-path-v1";
    private const string NativeIdentityFingerprintDomain =
        "clipcord-capture-library-native-identity-v1";

    internal static RoutingCaptureLibraryBinding Create(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var opened = RoutingWatchedFileSystem.OpenOrdinaryRoot(libraryRoot);
        using (opened.Handle)
        {
            return Create(opened);
        }
    }

    /// <summary>
    /// Derives a binding from an already-open root without taking ownership of its handle. This
    /// lets authority-sensitive workers validate and then retain the exact same directory object
    /// for their whole mutation lifetime rather than reopening a pathname after validation.
    /// </summary>
    internal static RoutingCaptureLibraryBinding Create(RoutingWatchedRootHandle opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        var currentIdentity = RoutingWatchedFileSystem.ReadIdentity(
            opened.Handle,
            requireDirectory: true);
        RoutingWatchedFileSystem.RequireExactFinalPath(
            opened.Handle,
            opened.CanonicalPath,
            "ClipCord Capture library");
        if (currentIdentity.VolumeSerialNumber != opened.Identity.VolumeSerialNumber ||
            !currentIdentity.FileIdHex.Equals(
                opened.Identity.FileIdHex,
                StringComparison.Ordinal) ||
            currentIdentity.CreationUtcTicks != opened.Identity.CreationUtcTicks)
        {
            throw new InvalidDataException(
                "The ClipCord Capture library changed while its identity was inspected.");
        }

        var binding = new RoutingCaptureLibraryBinding(
            Hash(
                PathFingerprintDomain,
                opened.CanonicalPath.Normalize(NormalizationForm.FormC)
                    .ToUpperInvariant()),
            Hash(
                NativeIdentityFingerprintDomain,
                opened.Identity.VolumeSerialNumber.ToString(
                    "x8",
                    CultureInfo.InvariantCulture),
                opened.Identity.FileIdHex,
                opened.Identity.CreationUtcTicks.ToString(
                    CultureInfo.InvariantCulture)));
        Validate(binding);
        return binding;
    }

    internal static void Validate(RoutingCaptureLibraryBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        RoutingValidation.RequireSha256(
            binding.CanonicalPathFingerprint,
            "Capture library canonical-path fingerprint");
        RoutingValidation.RequireSha256(
            binding.NativeDirectoryIdentityFingerprint,
            "Capture library native-directory fingerprint");
        RoutingValidation.Require(
            binding.CanonicalPathFingerprint ==
            binding.CanonicalPathFingerprint.ToUpperInvariant() &&
            binding.NativeDirectoryIdentityFingerprint ==
            binding.NativeDirectoryIdentityFingerprint.ToUpperInvariant(),
            "Capture library fingerprints must use canonical uppercase SHA-256 text.");
    }

    internal static void RequireExact(
        RoutingCaptureLibraryBinding expected,
        RoutingCaptureLibraryBinding current)
    {
        Validate(expected);
        Validate(current);
        if (expected != current)
        {
            throw new InvalidDataException(
                "The configured ClipCord Capture library no longer matches Routing authority.");
        }
    }

    private static string Hash(string domain, params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var value in values) Append(hash, value);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
