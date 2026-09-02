using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClipsToDiscord;

internal sealed record RoutingWatchedNativeFileIdentity(
    uint VolumeSerialNumber,
    string FileIdHex,
    long ByteLength,
    long CreationUtcTicks,
    long LastWriteUtcTicks);

internal static class RoutingWatchedNativeFileIdentityModel
{
    /// <summary>
    /// Validates the persistable Windows identity captured from BY_HANDLE_FILE_INFORMATION.
    /// FileIdHex intentionally stores that API's 64-bit volume-local file index. It is not a
    /// content identity and must always be paired with the volume serial, exact metadata, and
    /// content hash held by <see cref="RoutingWatchedSourceFile"/>.
    /// </summary>
    internal static void Validate(RoutingWatchedNativeFileIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RoutingValidation.Require(
            identity.FileIdHex is { Length: 16 } &&
            identity.FileIdHex.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "The watched source native file id must be canonical lowercase hexadecimal.");
        RoutingValidation.Require(identity.ByteLength > 0,
            "The watched source native file length must be positive.");
        RoutingValidation.Require(identity.CreationUtcTicks > 0 &&
                                  identity.LastWriteUtcTicks > 0,
            "The watched source native timestamps must be positive UTC ticks.");
    }
}

/// <summary>
/// Stable, persistable evidence for one ordinary watched-source MP4. The absolute file path is
/// deliberately represented as an authorized root plus a portable relative path so a consumer
/// must re-enter through the same source adapter before it opens the file again.
/// </summary>
internal sealed record RoutingWatchedSourceFile(
    ClipCaptureSource Source,
    string CanonicalRoot,
    string PortableRelativePath,
    string GameName,
    string DisplayFileName,
    string RootIdentitySha256,
    RoutingWatchedNativeFileIdentity NativeFileIdentity,
    string ContentSha256);

/// <summary>
/// Cheap, content-free identity for one candidate. It is safe for journal lookup only when all
/// fields, including the native volume/file id and exact metadata, match validated durable data.
/// </summary>
internal sealed record RoutingWatchedSourceOccurrence(
    ClipCaptureSource Source,
    string CanonicalRoot,
    string PortableRelativePath,
    string GameName,
    string DisplayFileName,
    string RootIdentitySha256,
    RoutingWatchedNativeFileIdentity NativeFileIdentity);

internal interface IRoutingWatchedSourceAdapter
{
    ClipCaptureSource Source { get; }

    IReadOnlyList<string> EnumerateCandidates(
        string clipsRoot,
        CancellationToken cancellationToken = default);

    Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
        string clipsRoot,
        string candidatePath,
        CancellationToken cancellationToken = default);

    Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
        string clipsRoot,
        string candidatePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reopens and rehashes the exact prior occurrence. Any source-root, path-layout, native file
    /// identity, metadata, or content change fails closed instead of silently producing new facts.
    /// </summary>
    Task<RoutingWatchedSourceFile> RevalidateAsync(
        RoutingWatchedSourceFile prior,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the same path- and native-directory-bound authority used by admitted clips,
    /// without requiring the source to contain a clip yet.
    /// </summary>
    string InspectRootIdentity(string clipsRoot);
}

internal static class RoutingWatchedSourceAdapters
{
    private static readonly IRoutingWatchedSourceAdapter SteelSeries =
        new SteelSeriesRoutingWatchedSourceAdapter();
    private static readonly IRoutingWatchedSourceAdapter Nvidia =
        new NvidiaRoutingWatchedSourceAdapter();

    internal static IReadOnlySet<ClipCaptureSource> CoveredSources { get; } =
        new HashSet<ClipCaptureSource>
        {
            ClipCaptureSource.SteelSeriesGg,
            ClipCaptureSource.Nvidia
        };

    internal static IRoutingWatchedSourceAdapter Get(ClipCaptureSource source) =>
        source switch
        {
            ClipCaptureSource.SteelSeriesGg => SteelSeries,
            ClipCaptureSource.Nvidia => Nvidia,
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
}

internal sealed class SteelSeriesRoutingWatchedSourceAdapter : RoutingWatchedSourceAdapter
{
    internal SteelSeriesRoutingWatchedSourceAdapter()
        : base(ClipCaptureSource.SteelSeriesGg)
    {
    }

    protected override IReadOnlyList<string> EnumerateCore(
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     canonicalRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                RoutingWatchedFileSystem.IsReparsePoint(path))
            {
                continue;
            }
            candidates.Add(Path.GetFullPath(path));
        }
        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        return candidates;
    }

    protected override RoutingWatchedPathFacts ValidateLayout(
        string canonicalRoot,
        string canonicalCandidate)
    {
        var components = GetRelativeComponents(canonicalRoot, canonicalCandidate);
        if (components.Length != 1)
        {
            throw new InvalidDataException(
                "A SteelSeries GG clip must be an MP4 directly inside the configured folder.");
        }
        RequireMp4Leaf(components[0]);
        return new RoutingWatchedPathFacts(
            ToPortablePath(components),
            UploadedFolder.GetGameFolderName(components[0]),
            components[0]);
    }
}

internal sealed class NvidiaRoutingWatchedSourceAdapter : RoutingWatchedSourceAdapter
{
    internal NvidiaRoutingWatchedSourceAdapter()
        : base(ClipCaptureSource.Nvidia)
    {
    }

    protected override IReadOnlyList<string> EnumerateCore(
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        foreach (var gameFolder in Directory.EnumerateDirectories(
                     canonicalRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gameDirectory = new DirectoryInfo(gameFolder);
            if (AppSettings.ManagedChildFolderNames.Contains(
                    gameDirectory.Name,
                    StringComparer.OrdinalIgnoreCase) ||
                RoutingWatchedFileSystem.IsReparsePoint(gameDirectory.FullName))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         gameDirectory.FullName,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    RoutingWatchedFileSystem.IsReparsePoint(path))
                {
                    continue;
                }
                candidates.Add(Path.GetFullPath(path));
            }
        }
        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        return candidates;
    }

    protected override RoutingWatchedPathFacts ValidateLayout(
        string canonicalRoot,
        string canonicalCandidate)
    {
        var components = GetRelativeComponents(canonicalRoot, canonicalCandidate);
        if (components.Length != 2)
        {
            throw new InvalidDataException(
                "An NVIDIA clip must be an MP4 exactly one game folder below the configured folder.");
        }
        if (AppSettings.ManagedChildFolderNames.Contains(
                components[0],
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A ClipCord-managed folder cannot be used as an NVIDIA game source.");
        }
        RequireMp4Leaf(components[1]);
        return new RoutingWatchedPathFacts(
            ToPortablePath(components),
            UploadedFolder.SanitizeGameFolderName(components[0]),
            components[1]);
    }
}

internal abstract class RoutingWatchedSourceAdapter : IRoutingWatchedSourceAdapter
{
    protected RoutingWatchedSourceAdapter(ClipCaptureSource source)
    {
        Source = source;
    }

    public ClipCaptureSource Source { get; }

    public string InspectRootIdentity(string clipsRoot)
    {
        var root = RoutingWatchedFileSystem.OpenOrdinaryRoot(clipsRoot);
        using (root.Handle)
        {
            RoutingWatchedFileSystem.RequireExactFinalPath(
                root.Handle,
                root.CanonicalPath,
                "watched source root");
            return CreateRootIdentity(Source, root.CanonicalPath, root.Identity);
        }
    }

    public IReadOnlyList<string> EnumerateCandidates(
        string clipsRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RoutingWatchedFileSystem.OpenOrdinaryRoot(clipsRoot);
        using (root.Handle)
        {
            return EnumerateCore(root.CanonicalPath, cancellationToken);
        }
    }

    public async Task<RoutingWatchedSourceFile> OpenAndFingerprintAsync(
        string clipsRoot,
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RoutingWatchedFileSystem.OpenOrdinaryRoot(clipsRoot);
        using (root.Handle)
        {
            var canonicalCandidate = Path.GetFullPath(
                string.IsNullOrWhiteSpace(candidatePath)
                    ? throw new ArgumentException(
                        "The watched clip path is missing.",
                        nameof(candidatePath))
                    : candidatePath);
            var pathFacts = ValidateLayout(root.CanonicalPath, canonicalCandidate);
            RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
                root.CanonicalPath,
                canonicalCandidate,
                requireDirectory: false,
                "watched clip");

            var opened = RoutingWatchedFileSystem.OpenOrdinaryFile(
                canonicalCandidate,
                root.CanonicalPath);
            await using var stream = opened.Stream;
            var before = opened.Identity;
            var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            var after = RoutingWatchedFileSystem.ReadIdentity(stream.SafeFileHandle);
            if (before != after)
            {
                throw new InvalidDataException(
                    "The watched clip changed while its content identity was read.");
            }

            RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
                root.CanonicalPath,
                canonicalCandidate,
                requireDirectory: false,
                "watched clip");
            RoutingWatchedFileSystem.RequireExactFinalPath(
                stream.SafeFileHandle,
                canonicalCandidate,
                "watched clip");
            var rootAfter = RoutingWatchedFileSystem.ReadIdentity(
                root.Handle,
                requireDirectory: true);
            if (rootAfter != root.Identity)
            {
                throw new InvalidDataException(
                    "The watched source root changed while its clip was fingerprinted.");
            }
            RoutingWatchedFileSystem.RequireExactFinalPath(
                root.Handle,
                root.CanonicalPath,
                "watched source root");
            return new RoutingWatchedSourceFile(
                Source,
                root.CanonicalPath,
                pathFacts.PortableRelativePath,
                pathFacts.GameName,
                pathFacts.DisplayFileName,
                CreateRootIdentity(Source, root.CanonicalPath, root.Identity),
                before,
                hash);
        }
    }

    public Task<RoutingWatchedSourceOccurrence> InspectOccurrenceAsync(
        string clipsRoot,
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RoutingWatchedFileSystem.OpenOrdinaryRoot(clipsRoot);
        using (root.Handle)
        {
            var canonicalCandidate = Path.GetFullPath(
                string.IsNullOrWhiteSpace(candidatePath)
                    ? throw new ArgumentException(
                        "The watched clip path is missing.",
                        nameof(candidatePath))
                    : candidatePath);
            var pathFacts = ValidateLayout(root.CanonicalPath, canonicalCandidate);
            RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
                root.CanonicalPath,
                canonicalCandidate,
                requireDirectory: false,
                "watched clip");

            var opened = RoutingWatchedFileSystem.OpenOrdinaryFile(
                canonicalCandidate,
                root.CanonicalPath);
            using var stream = opened.Stream;
            var identity = opened.Identity;
            var after = RoutingWatchedFileSystem.ReadIdentity(stream.SafeFileHandle);
            if (identity != after)
            {
                throw new InvalidDataException(
                    "The watched clip changed while its native identity was inspected.");
            }
            RoutingWatchedFileSystem.RequireExactFinalPath(
                stream.SafeFileHandle,
                canonicalCandidate,
                "watched clip");
            var rootAfter = RoutingWatchedFileSystem.ReadIdentity(
                root.Handle,
                requireDirectory: true);
            if (rootAfter != root.Identity)
            {
                throw new InvalidDataException(
                    "The watched source root changed while its clip was inspected.");
            }
            RoutingWatchedFileSystem.RequireExactFinalPath(
                root.Handle,
                root.CanonicalPath,
                "watched source root");
            return Task.FromResult(new RoutingWatchedSourceOccurrence(
                Source,
                root.CanonicalPath,
                pathFacts.PortableRelativePath,
                pathFacts.GameName,
                pathFacts.DisplayFileName,
                CreateRootIdentity(Source, root.CanonicalPath, root.Identity),
                identity));
        }
    }

    public async Task<RoutingWatchedSourceFile> RevalidateAsync(
        RoutingWatchedSourceFile prior,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prior);
        if (prior.Source != Source)
        {
            throw new InvalidDataException(
                "The watched clip was passed to a different capture-source adapter.");
        }
        ValidatePersistableResult(prior);
        var candidate = Path.GetFullPath(Path.Combine(
            prior.CanonicalRoot,
            prior.PortableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var current = await OpenAndFingerprintAsync(
                prior.CanonicalRoot,
                candidate,
                cancellationToken)
            .ConfigureAwait(false);
        if (current != prior)
        {
            throw new InvalidDataException(
                "The watched clip no longer matches its durable source evidence.");
        }
        return current;
    }

    protected abstract IReadOnlyList<string> EnumerateCore(
        string canonicalRoot,
        CancellationToken cancellationToken);

    protected abstract RoutingWatchedPathFacts ValidateLayout(
        string canonicalRoot,
        string canonicalCandidate);

    protected static string[] GetRelativeComponents(
        string canonicalRoot,
        string canonicalCandidate)
    {
        var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The watched clip must stay inside its configured source folder.");
        }
        return relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    protected static void RequireMp4Leaf(string leaf)
    {
        if (!Path.GetFileName(leaf).Equals(leaf, StringComparison.Ordinal) ||
            !Path.GetExtension(leaf).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A watched clip must be an MP4 leaf file.");
        }
    }

    protected static string ToPortablePath(IReadOnlyList<string> components) =>
        string.Join('/', components);

    internal static string CreateRootIdentity(
        ClipCaptureSource source,
        string canonicalRoot,
        RoutingWatchedNativeFileIdentity identity)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"clipcord-watched-root-v1\n{source}\n" +
            $"{canonicalRoot.Normalize(NormalizationForm.FormC).ToUpperInvariant()}\n" +
            $"{identity.VolumeSerialNumber:x8}\n{identity.FileIdHex}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static void ValidatePersistableResult(RoutingWatchedSourceFile result)
    {
        RoutingValidation.Require(Enum.IsDefined(result.Source),
            "The watched source kind is unsupported.");
        RoutingValidation.RequireText(result.GameName, 1, 160,
            "watched source game name");
        RoutingValidation.RequireText(result.DisplayFileName, 1, 260,
            "watched source display file name");
        RoutingValidation.RequireSha256(result.RootIdentitySha256,
            "watched source root identity");
        RoutingValidation.RequireSha256(result.ContentSha256,
            "watched source content hash");
        RoutingWatchedNativeFileIdentityModel.Validate(result.NativeFileIdentity);
        var components = result.PortableRelativePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        RoutingValidation.Require(components.Length > 0 &&
                                  !result.PortableRelativePath.Contains('\\') &&
                                  !Path.IsPathRooted(result.PortableRelativePath) &&
                                  components.All(component => component is not "." and not ".."),
            "The watched source relative path is invalid.");
    }
}

internal sealed record RoutingWatchedPathFacts(
    string PortableRelativePath,
    string GameName,
    string DisplayFileName);

internal static class RoutingWatchedFileSystem
{
    private const uint GenericRead = 0x80000000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;

    internal static RoutingWatchedRootHandle OpenOrdinaryRoot(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Watched-source native identity requires Windows.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        EnsureNoReparsePointsFromVolumeRoot(canonical);
        var handle = OpenHandle(
            canonical,
            desiredAccess: FileListDirectory,
            FileShare.Read | FileShare.Write,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        try
        {
            var identity = ReadIdentity(handle, requireDirectory: true);
            RequireExactFinalPath(handle, canonical, "watched source root");
            EnsureNoReparsePointsFromVolumeRoot(canonical);
            return new RoutingWatchedRootHandle(canonical, identity, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static RoutingWatchedOpenedFile OpenOrdinaryFile(
        string canonicalPath,
        string canonicalRoot)
    {
        EnsureOrdinaryExistingPath(
            canonicalRoot,
            canonicalPath,
            requireDirectory: false,
            "watched clip");
        var handle = OpenHandle(
            canonicalPath,
            GenericRead,
            FileShare.Read,
            FileFlagOpenReparsePoint | FileFlagSequentialScan | FileFlagOverlapped);
        try
        {
            var identity = ReadIdentity(handle, requireDirectory: false);
            RequireExactFinalPath(handle, canonicalPath, "watched clip");
            var stream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 1024 * 1024,
                isAsync: true);
            return new RoutingWatchedOpenedFile(identity, stream);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static RoutingWatchedNativeFileIdentity ReadIdentity(
        SafeFileHandle handle,
        bool? requireDirectory = null)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            throw NativeError("The watched source identity could not be read.");
        }
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new IOException(
                "A watched source cannot be a symbolic link, mount point, or junction.");
        }
        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
        if (requireDirectory is { } expectedDirectory && isDirectory != expectedDirectory)
        {
            throw new IOException("The watched source has an unexpected filesystem type.");
        }
        var byteLength = checked(((long)information.FileSizeHigh << 32) |
                                 (long)information.FileSizeLow);
        var fileId = ((ulong)information.FileIndexHigh << 32) |
                     information.FileIndexLow;
        return new RoutingWatchedNativeFileIdentity(
            information.VolumeSerialNumber,
            fileId.ToString("x16", CultureInfo.InvariantCulture),
            byteLength,
            ToUtcTicks(information.CreationTime),
            ToUtcTicks(information.LastWriteTime));
    }

    internal static void RequireExactFinalPath(
        SafeFileHandle handle,
        string expectedPath,
        string description)
    {
        var finalPath = GetFinalPath(handle);
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
        if (!finalPath.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The {description} resolves through a redirected filesystem path.");
        }
    }

    internal static void EnsureOrdinaryExistingPath(
        string root,
        string candidate,
        bool requireDirectory,
        string description)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"The {description} escaped its configured source folder.");
        }
        EnsureNoReparsePointsFromVolumeRoot(canonicalRoot);
        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = canonicalRoot;
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            var expectedDirectory = index < components.Length - 1 || requireDirectory;
            if (expectedDirectory ? !Directory.Exists(current) : !File.Exists(current))
            {
                throw new IOException($"The {description} is unavailable.");
            }
            if (IsReparsePoint(current))
            {
                throw new IOException($"The {description} uses a redirected filesystem path.");
            }
        }
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static void EnsureNoReparsePointsFromVolumeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath) ??
                         throw new IOException(
                             "The watched source path has no filesystem root.");
        var current = volumeRoot;
        foreach (var component in fullPath[volumeRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                throw new IOException("The watched source path is unavailable.");
            }
            if (IsReparsePoint(current))
            {
                throw new IOException(
                    "A watched source cannot traverse a symbolic link, mount point, or junction.");
            }
        }
    }

    private static SafeFileHandle OpenHandle(
        string path,
        uint desiredAccess,
        FileShare shareMode,
        uint flags)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            FileMode.Open,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The watched source could not be opened safely.");
        }
        return handle;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw NativeError("The watched source final path could not be resolved.");
            }
            if (length < (uint)buffer.Capacity)
            {
                return NormalizeFinalPath(buffer.ToString());
            }
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("The watched source final path is too long.");
    }

    private static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        var normalized = path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[uncPrefix.Length..]
            : path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
                ? path[devicePrefix.Length..]
                : path;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized));
    }

    private static long ToUtcTicks(NativeFileTime value)
    {
        var fileTime = ((long)value.HighDateTime << 32) | value.LowDateTime;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).Ticks;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                "The watched source carries an invalid filesystem timestamp.",
                exception);
        }
    }

    private static Win32Exception NativeError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out NativeByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

internal sealed record RoutingWatchedRootHandle(
    string CanonicalPath,
    RoutingWatchedNativeFileIdentity Identity,
    SafeFileHandle Handle);

internal sealed record RoutingWatchedOpenedFile(
    RoutingWatchedNativeFileIdentity Identity,
    FileStream Stream);
