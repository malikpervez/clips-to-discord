using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClipsToDiscord;

internal sealed record XboxDvrWindowsRootSnapshot(
    ulong VolumeSerialNumber,
    string FileId128Hex,
    uint FileAttributes,
    uint ReparseTag);

internal sealed record XboxDvrWindowsFindEntry(
    string FileName,
    long LogicalBytes,
    long LastWriteUtcTicks,
    uint FileAttributes,
    uint ReparseTag);

internal sealed class XboxDvrMetadataCapacityExceededException : IOException
{
    internal XboxDvrMetadataCapacityExceededException(int maximumEntries)
        : base(
            $"The Xbox DVR folder contains more than the allowed {maximumEntries:N0} direct entries.")
    {
        MaximumEntries = maximumEntries;
    }

    internal int MaximumEntries { get; }
}

/// <summary>
/// The deliberately split Windows boundary. Setup/preflight can use only the three NoData
/// operations. OpenSourceRead is the explicit point at which an admitted OneDrive placeholder may
/// hydrate. Destination operations are restricted to one direct child of an ordinary ClipCord-
/// owned staging folder.
/// </summary>
internal interface IXboxDvrWindowsFileOperations
{
    XboxDvrWindowsRootSnapshot InspectRootNoData(
        string canonicalRoot,
        CancellationToken cancellationToken);

    IReadOnlyList<XboxDvrWindowsFindEntry> FindTopLevelNoData(
        string canonicalRoot,
        int maximumEntries,
        CancellationToken cancellationToken);

    XboxDvrCandidateMetadata InspectLeafNoData(
        string canonicalRoot,
        string portableLeaf,
        CancellationToken cancellationToken);

    IXboxDvrSourceReadSession OpenSourceRead(
        string canonicalRoot,
        string portableLeaf,
        CancellationToken cancellationToken);

    Stream CreateOwnedPartial(
        string canonicalStagingRoot,
        string exactPartialPath,
        CancellationToken cancellationToken);

    void PublishOwnedPartial(
        string canonicalStagingRoot,
        string exactPartialPath,
        string exactFinalPath);

    void TryDeleteOwnedPartial(
        string canonicalStagingRoot,
        string exactPartialPath);
}

internal interface IXboxDvrSourceReadSession : IAsyncDisposable
{
    Stream Content { get; }

    XboxDvrCandidateMetadata ReadCurrentMetadata(
        CancellationToken cancellationToken);
}

/// <summary>
/// Production Xbox OneDrive metadata adapter. It enumerates only the configured folder's direct
/// children and obtains native identities through zero-access handles opened on the reparse point.
/// It has no API capable of reading file content or requesting hydration.
/// </summary>
internal sealed class WindowsXboxDvrMetadataFileSystem : IXboxDvrMetadataFileSystem
{
    internal const int MaximumAllowedMetadataEntries = 10_000;
    private readonly IXboxDvrWindowsFileOperations _operations;

    internal WindowsXboxDvrMetadataFileSystem()
        : this(new XboxDvrWindowsFileOperations())
    {
    }

    internal WindowsXboxDvrMetadataFileSystem(IXboxDvrWindowsFileOperations operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public IReadOnlyList<XboxDvrCandidateMetadata> EnumerateMetadata(
        string canonicalRoot,
        CancellationToken cancellationToken) => EnumerateMetadata(
            canonicalRoot,
            MaximumAllowedMetadataEntries,
            cancellationToken);

    internal IReadOnlyList<XboxDvrCandidateMetadata> EnumerateMetadata(
        string canonicalRoot,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireMetadataEntryLimit(maximumEntries);
        canonicalRoot = RequireCanonicalRoot(canonicalRoot);
        var rootBefore = _operations.InspectRootNoData(canonicalRoot, cancellationToken);
        ValidateRootSnapshot(rootBefore);
        var found = _operations.FindTopLevelNoData(
                        canonicalRoot,
                        maximumEntries,
                        cancellationToken) ??
                    throw new InvalidDataException("Xbox DVR enumeration returned no collection.");
        if (found.Count > maximumEntries)
        {
            throw new XboxDvrMetadataCapacityExceededException(maximumEntries);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<XboxDvrCandidateMetadata>(found.Count);
        foreach (var entry in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.FileName is "." or ".." ||
                (entry.FileAttributes & XboxDvrCloudReparsePolicy.FileAttributeDirectory) != 0 ||
                !Path.GetExtension(entry.FileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var leaf = RequirePortableLeaf(entry.FileName);
            if (!seen.Add(leaf))
            {
                throw new InvalidDataException(
                    "Xbox DVR enumeration returned a case-insensitive duplicate leaf.");
            }

            var metadata = _operations.InspectLeafNoData(
                canonicalRoot,
                leaf,
                cancellationToken);
            ValidateFindSnapshot(entry, metadata);
            candidates.Add(metadata);
        }

        var rootAfter = _operations.InspectRootNoData(canonicalRoot, cancellationToken);
        ValidateRootSnapshot(rootAfter);
        if (!SameRootAuthority(rootBefore, rootAfter))
        {
            throw new IOException(
                "The Xbox DVR source root changed while its metadata was enumerated.");
        }
        return candidates;
    }

    private static void ValidateFindSnapshot(
        XboxDvrWindowsFindEntry found,
        XboxDvrCandidateMetadata inspected)
    {
        ArgumentNullException.ThrowIfNull(inspected);
        var foundLeaf = RequirePortableLeaf(found.FileName);
        var inspectedLeaf = RequirePortableLeaf(inspected.PortableRelativePath);
        if (!foundLeaf.Equals(inspectedLeaf, StringComparison.Ordinal) ||
            found.LogicalBytes != inspected.LogicalBytes ||
            found.LastWriteUtcTicks != inspected.LastWriteUtcTicks)
        {
            throw new IOException(
                "An Xbox DVR item changed while its metadata was inspected.");
        }

        var foundClass = XboxDvrCloudReparsePolicy.Classify(
            found.FileAttributes,
            found.ReparseTag);
        var inspectedClass = XboxDvrCloudReparsePolicy.Classify(
            inspected.FileAttributes,
            inspected.ReparseTag);
        if (foundClass != inspectedClass &&
            !(foundClass == XboxDvrReparseClassification.CloudPlaceholder &&
              inspectedClass == XboxDvrReparseClassification.CloudPlaceholder))
        {
            throw new IOException(
                "An Xbox DVR item's reparse authority changed during metadata inspection.");
        }
    }

    private static void ValidateRootSnapshot(XboxDvrWindowsRootSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((snapshot.FileAttributes & XboxDvrCloudReparsePolicy.FileAttributeDirectory) == 0 ||
            !IsCanonicalHex(snapshot.FileId128Hex, 32))
        {
            throw new IOException("The Xbox DVR source root identity is invalid.");
        }

        var isReparse = (snapshot.FileAttributes &
                         XboxDvrCloudReparsePolicy.FileAttributeReparsePoint) != 0;
        if (!isReparse && snapshot.ReparseTag != 0 ||
            isReparse &&
            ((snapshot.ReparseTag & XboxDvrCloudReparsePolicy.ReparseTagNameSurrogate) != 0 ||
             (snapshot.ReparseTag & XboxDvrCloudReparsePolicy.CloudTagFamilyMask) !=
                 XboxDvrCloudReparsePolicy.CloudTagFamily))
        {
            throw new IOException(
                "The Xbox DVR source root is redirected or is not an ordinary/Cloud Files folder.");
        }
    }

    private static bool SameRootAuthority(
        XboxDvrWindowsRootSnapshot before,
        XboxDvrWindowsRootSnapshot after) =>
        before.VolumeSerialNumber == after.VolumeSerialNumber &&
        before.FileId128Hex.Equals(after.FileId128Hex, StringComparison.Ordinal) &&
        RootKind(before) == RootKind(after);

    private static XboxDvrReparseClassification RootKind(
        XboxDvrWindowsRootSnapshot snapshot) =>
        (snapshot.FileAttributes & XboxDvrCloudReparsePolicy.FileAttributeReparsePoint) == 0
            ? XboxDvrReparseClassification.Ordinary
            : XboxDvrReparseClassification.CloudPlaceholder;

    internal static string RequireCanonicalRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The Xbox DVR root must be fully qualified.", nameof(root));
        }
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!canonical.Equals(
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Xbox DVR filesystem boundary requires a canonical root.",
                nameof(root));
        }
        return canonical;
    }

    internal static string RequirePortableLeaf(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.GetFileName(value).Equals(value, StringComparison.Ordinal) ||
            value is "." or ".." || value.Contains('/') || value.Contains('\\'))
        {
            throw new InvalidDataException("An Xbox DVR item must be one exact source leaf.");
        }
        return value;
    }

    internal static int RequireMetadataEntryLimit(int maximumEntries)
    {
        if (maximumEntries is < 1 or > MaximumAllowedMetadataEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                $"Xbox DVR metadata enumeration must stay between 1 and {MaximumAllowedMetadataEntries:N0} entries.");
        }
        return maximumEntries;
    }

    private static bool IsCanonicalHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record XboxDvrContentImportRequest(
    string SourceId,
    long ExpectedSourceRevision,
    long ExpectedRoutingGeneration,
    string ExpectedRootIdentitySha256,
    string CanonicalRoot,
    XboxDvrCandidateMetadata ExpectedMetadata,
    string ExpectedOccurrenceIdentitySha256,
    string ExpectedRevisionIdentitySha256,
    string OwnedStagingRoot);

internal sealed record XboxDvrContentImportResult(
    string StagedPath,
    long CopiedBytes,
    string ContentSha256,
    string OccurrenceIdentitySha256,
    string RevisionIdentitySha256);

internal interface IXboxDvrContentImporter
{
    Task<XboxDvrContentImportResult> ImportAsync(
        XboxDvrContentImportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The sole Xbox content boundary. Calling ImportAsync is an explicit admission decision and may
/// hydrate a cloud placeholder. It reads the source once, copies to a unique ordinary partial,
/// hashes in flight, revalidates the still-open source, and atomically publishes the owned copy.
/// It never writes, moves, deletes, renames, or opens the source with write access.
/// </summary>
internal sealed class WindowsXboxDvrContentImporter : IXboxDvrContentImporter
{
    private const int CopyBufferBytes = 1024 * 1024;
    private readonly IXboxDvrWindowsFileOperations _operations;
    private readonly Func<string> _partialTokenFactory;

    internal WindowsXboxDvrContentImporter()
        : this(new XboxDvrWindowsFileOperations(), () => Guid.NewGuid().ToString("N"))
    {
    }

    internal WindowsXboxDvrContentImporter(
        IXboxDvrWindowsFileOperations operations,
        Func<string>? partialTokenFactory = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _partialTokenFactory = partialTokenFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async Task<XboxDvrContentImportResult> ImportAsync(
        XboxDvrContentImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalRoot = WindowsXboxDvrMetadataFileSystem.RequireCanonicalRoot(
            request.CanonicalRoot);
        var stagingRoot = WindowsXboxDvrMetadataFileSystem.RequireCanonicalRoot(
            request.OwnedStagingRoot);
        RequireDisjointRoots(canonicalRoot, stagingRoot);
        var expected = request.ExpectedMetadata ??
                       throw new InvalidDataException("Xbox import admission metadata is missing.");
        ValidateAdmittedSnapshot(request, expected);

        var parsed = XboxDvrFileNameParser.Parse(expected.PortableRelativePath);
        var finalLeaf = request.ExpectedOccurrenceIdentitySha256 + ".mp4";
        var token = RequireSafePartialToken(_partialTokenFactory());
        var partialLeaf = request.ExpectedOccurrenceIdentitySha256 + "." + token + ".partial";
        var finalPath = RequireDirectChild(stagingRoot, finalLeaf);
        var partialPath = RequireDirectChild(stagingRoot, partialLeaf);
        var ownsPartial = false;

        try
        {
            var before = _operations.InspectLeafNoData(
                canonicalRoot,
                expected.PortableRelativePath,
                cancellationToken);
            ValidateCurrentSnapshot(request, parsed.CapturedUtc, before);

            await using var source = _operations.OpenSourceRead(
                canonicalRoot,
                expected.PortableRelativePath,
                cancellationToken);
            ArgumentNullException.ThrowIfNull(source);
            if (source.Content is null || !source.Content.CanRead)
            {
                throw new IOException("The admitted Xbox source did not provide a readable stream.");
            }
            var opened = source.ReadCurrentMetadata(cancellationToken);
            ValidateCurrentSnapshot(request, parsed.CapturedUtc, opened);

            long copied = 0;
            string contentSha256;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                try
                {
                    await using var destination = _operations.CreateOwnedPartial(
                        stagingRoot,
                        partialPath,
                        cancellationToken);
                    if (destination is null)
                    {
                        throw new IOException(
                            "The ClipCord-owned Xbox staging file is missing.");
                    }
                    ownsPartial = true;
                    if (!destination.CanWrite)
                    {
                        throw new IOException(
                            "The ClipCord-owned Xbox staging file is not writable.");
                    }
                    while (true)
                    {
                        var read = await source.Content.ReadAsync(
                                buffer.AsMemory(0, buffer.Length),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0) break;
                        copied = checked(copied + read);
                        if (copied > expected.LogicalBytes)
                        {
                            throw new InvalidDataException(
                                "The Xbox source exceeded its admitted logical length.");
                        }
                        hash.AppendData(buffer, 0, read);
                        await destination.WriteAsync(
                                buffer.AsMemory(0, read),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    if (copied != expected.LogicalBytes)
                    {
                        throw new InvalidDataException(
                            "The Xbox source length changed while its owned copy was created.");
                    }
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
                }
                contentSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var after = source.ReadCurrentMetadata(cancellationToken);
            ValidateCurrentSnapshot(request, parsed.CapturedUtc, after);
            if (after.LogicalBytes != copied)
            {
                throw new InvalidDataException(
                    "The Xbox source metadata no longer matches the completed owned copy.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _operations.PublishOwnedPartial(stagingRoot, partialPath, finalPath);
            ownsPartial = false;
            return new XboxDvrContentImportResult(
                finalPath,
                copied,
                contentSha256,
                request.ExpectedOccurrenceIdentitySha256,
                request.ExpectedRevisionIdentitySha256);
        }
        catch
        {
            if (ownsPartial)
            {
                _operations.TryDeleteOwnedPartial(stagingRoot, partialPath);
            }
            throw;
        }
    }

    private static void ValidateAdmittedSnapshot(
        XboxDvrContentImportRequest request,
        XboxDvrCandidateMetadata expected)
    {
        if (request.ExpectedSourceRevision <= 0)
            throw new InvalidDataException("The Xbox source revision is invalid.");
        if (request.ExpectedRoutingGeneration <= 0)
            throw new InvalidDataException("The Routing snapshot generation is invalid.");
        RoutingValidation.RequireSha256(
            request.ExpectedRootIdentitySha256, "Xbox root identity");
        if (!request.ExpectedRootIdentitySha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException(
                "The Xbox root identity must use canonical lowercase SHA-256 text.");
        }
        var leaf = WindowsXboxDvrMetadataFileSystem.RequirePortableLeaf(
            expected.PortableRelativePath);
        var parsed = XboxDvrFileNameParser.Parse(leaf);
        if (!parsed.IsParsed || parsed.CapturedUtc is null || expected.LogicalBytes <= 0 ||
            XboxDvrCloudReparsePolicy.Classify(
                expected.FileAttributes,
                expected.ReparseTag) == XboxDvrReparseClassification.Rejected)
        {
            throw new InvalidDataException(
                "The Xbox content request does not contain one safely admitted modern clip.");
        }
        var occurrence = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            request.SourceId,
            leaf,
            expected);
        var revision = XboxDvrSourceAdapter.CreateRevisionIdentity(
            occurrence,
            parsed.CapturedUtc,
            expected);
        if (!occurrence.Equals(
                request.ExpectedOccurrenceIdentitySha256,
                StringComparison.Ordinal) ||
            !revision.Equals(request.ExpectedRevisionIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Xbox content request does not match its admitted occurrence and revision.");
        }
    }

    private static void ValidateCurrentSnapshot(
        XboxDvrContentImportRequest request,
        DateTimeOffset? capturedUtc,
        XboxDvrCandidateMetadata current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.PortableRelativePath.Equals(
                request.ExpectedMetadata.PortableRelativePath,
                StringComparison.Ordinal) ||
            XboxDvrCloudReparsePolicy.Classify(
                current.FileAttributes,
                current.ReparseTag) == XboxDvrReparseClassification.Rejected)
        {
            throw new InvalidDataException(
                "The Xbox source path or reparse authority changed after admission.");
        }
        var occurrence = XboxDvrSourceAdapter.CreateOccurrenceIdentity(
            request.SourceId,
            current.PortableRelativePath,
            current);
        var revision = XboxDvrSourceAdapter.CreateRevisionIdentity(
            occurrence,
            capturedUtc,
            current);
        if (!occurrence.Equals(
                request.ExpectedOccurrenceIdentitySha256,
                StringComparison.Ordinal) ||
            !revision.Equals(request.ExpectedRevisionIdentitySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Xbox source occurrence or revision changed after admission.");
        }
    }

    private static string RequireSafePartialToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException("The Xbox staging token is invalid.");
        }
        return value;
    }

    private static string RequireDirectChild(string canonicalRoot, string leaf)
    {
        WindowsXboxDvrMetadataFileSystem.RequirePortableLeaf(leaf);
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, leaf));
        if (!Path.GetDirectoryName(path)!.Equals(
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).Equals(leaf, StringComparison.Ordinal))
        {
            throw new IOException("The Xbox staging path escaped its owned folder.");
        }
        return path;
    }

    private static void RequireDisjointRoots(string sourceRoot, string stagingRoot)
    {
        static bool IsSameOrChild(string candidate, string authority)
        {
            if (candidate.Equals(authority, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = authority + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (IsSameOrChild(stagingRoot, sourceRoot) || IsSameOrChild(sourceRoot, stagingRoot))
        {
            throw new IOException(
                "The ClipCord-owned Xbox staging folder must be separate from the source tree.");
        }
    }
}

/// <summary>
/// Windows implementation. FindFirstFileExW returns placeholder metadata without opening data.
/// Metadata handles request zero access and FILE_FLAG_OPEN_REPARSE_POINT. Only OpenSourceRead uses
/// GENERIC_READ without OPEN_REPARSE_POINT, and only after an import admission has been supplied.
/// </summary>
internal sealed class XboxDvrWindowsFileOperations : IXboxDvrWindowsFileOperations
{
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorNoMoreFiles = 18;

    public XboxDvrWindowsRootSnapshot InspectRootNoData(
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        canonicalRoot = WindowsXboxDvrMetadataFileSystem.RequireCanonicalRoot(canonicalRoot);
        using var handle = OpenHandle(
            canonicalRoot,
            desiredAccess: 0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            "Xbox DVR root metadata");
        var snapshot = ReadHandleSnapshot(handle, canonicalRoot, Path.GetFileName(canonicalRoot));
        if ((snapshot.FileAttributes & XboxDvrCloudReparsePolicy.FileAttributeDirectory) == 0)
        {
            throw new IOException("The Xbox DVR source root is not a directory.");
        }
        return new XboxDvrWindowsRootSnapshot(
            snapshot.VolumeSerialNumber,
            snapshot.FileId128Hex,
            snapshot.FileAttributes,
            snapshot.ReparseTag);
    }

    public IReadOnlyList<XboxDvrWindowsFindEntry> FindTopLevelNoData(
        string canonicalRoot,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        WindowsXboxDvrMetadataFileSystem.RequireMetadataEntryLimit(maximumEntries);
        canonicalRoot = WindowsXboxDvrMetadataFileSystem.RequireCanonicalRoot(canonicalRoot);
        var search = Path.Combine(canonicalRoot, "*");
        using var find = FindFirstFileExW(
            search,
            NativeFindInfoLevel.Basic,
            out var data,
            NativeFindSearchOperation.NameMatch,
            IntPtr.Zero,
            NativeFindFlags.LargeFetch);
        if (find.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound) return [];
            throw new Win32Exception(error, "Xbox DVR metadata enumeration could not start.");
        }

        var result = new List<XboxDvrWindowsFindEntry>(maximumEntries + 1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.FileName is not "." and not "..")
            {
                result.Add(ToFindEntry(data));
                if (result.Count > maximumEntries)
                {
                    throw new XboxDvrMetadataCapacityExceededException(maximumEntries);
                }
            }
            if (FindNextFileW(find, out data)) continue;
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNoMoreFiles)
            {
                throw new Win32Exception(error, "Xbox DVR metadata enumeration failed.");
            }
            break;
        }
        return result;
    }

    public XboxDvrCandidateMetadata InspectLeafNoData(
        string canonicalRoot,
        string portableLeaf,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        canonicalRoot = WindowsXboxDvrMetadataFileSystem.RequireCanonicalRoot(canonicalRoot);
        portableLeaf = WindowsXboxDvrMetadataFileSystem.RequirePortableLeaf(portableLeaf);
        var exactPath = RequireDirectSourceChild(canonicalRoot, portableLeaf);
        using var handle = OpenHandle(
            exactPath,
            desiredAccess: 0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            FileFlagOpenReparsePoint,
            "Xbox DVR item metadata");
        return ReadHandleSnapshot(handle, exactPath, portableLeaf);
    }

    public IXboxDvrSourceReadSession OpenSourceRead(
        string canonicalRoot,
        string portableLeaf,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        canonicalRoot = WindowsXboxDvrMetadataFileSystem.RequireCanonicalRoot(canonicalRoot);
        portableLeaf = WindowsXboxDvrMetadataFileSystem.RequirePortableLeaf(portableLeaf);
        var exactPath = RequireDirectSourceChild(canonicalRoot, portableLeaf);
        var handle = OpenHandle(
            exactPath,
            GenericRead,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            FileFlagSequentialScan | FileFlagOverlapped,
            "admitted Xbox DVR content");
        try
        {
            var opened = ReadHandleSnapshot(handle, exactPath, portableLeaf);
            if (XboxDvrCloudReparsePolicy.Classify(
                    opened.FileAttributes,
                    opened.ReparseTag) == XboxDvrReparseClassification.Rejected)
            {
                throw new IOException(
                    "The admitted Xbox source resolved through an unsafe reparse point.");
            }
            var stream = new FileStream(handle, FileAccess.Read, CopyBufferSize, isAsync: true);
            return new XboxDvrWindowsSourceReadSession(
                stream,
                exactPath,
                portableLeaf,
                ReadHandleSnapshot);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public Stream CreateOwnedPartial(
        string canonicalStagingRoot,
        string exactPartialPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        canonicalStagingRoot = ValidateOrdinaryStagingRoot(canonicalStagingRoot);
        RequireExactOwnedChild(canonicalStagingRoot, exactPartialPath);
        FileStream? stream = null;
        var created = false;
        try
        {
            stream = new FileStream(
                exactPartialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
                canonicalStagingRoot,
                exactPartialPath,
                requireDirectory: false,
                "Xbox owned staging partial");
            return stream;
        }
        catch
        {
            stream?.Dispose();
            if (created)
            {
                TryDeleteExactOrdinaryFile(canonicalStagingRoot, exactPartialPath);
            }
            throw;
        }
    }

    public void PublishOwnedPartial(
        string canonicalStagingRoot,
        string exactPartialPath,
        string exactFinalPath)
    {
        canonicalStagingRoot = ValidateOrdinaryStagingRoot(canonicalStagingRoot);
        RequireExactOwnedChild(canonicalStagingRoot, exactPartialPath);
        RequireExactOwnedChild(canonicalStagingRoot, exactFinalPath);
        RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
            canonicalStagingRoot,
            exactPartialPath,
            requireDirectory: false,
            "Xbox owned staging partial");
        File.Move(exactPartialPath, exactFinalPath, overwrite: false);
    }

    public void TryDeleteOwnedPartial(
        string canonicalStagingRoot,
        string exactPartialPath)
    {
        try
        {
            canonicalStagingRoot = ValidateOrdinaryStagingRoot(canonicalStagingRoot);
            RequireExactOwnedChild(canonicalStagingRoot, exactPartialPath);
            TryDeleteExactOrdinaryFile(canonicalStagingRoot, exactPartialPath);
        }
        catch
        {
            // Cleanup is best effort and may target only the exact, app-created partial.
        }
    }

    private const int CopyBufferSize = 1024 * 1024;

    private static XboxDvrWindowsFindEntry ToFindEntry(NativeFindData data)
    {
        var length = ((ulong)data.FileSizeHigh << 32) | data.FileSizeLow;
        if (length > long.MaxValue)
        {
            throw new InvalidDataException("An Xbox DVR item reports an impossible length.");
        }
        return new XboxDvrWindowsFindEntry(
            data.FileName,
            (long)length,
            ToUtcTicks(data.LastWriteTime),
            data.FileAttributes,
            (data.FileAttributes & XboxDvrCloudReparsePolicy.FileAttributeReparsePoint) != 0
                ? data.Reserved0
                : 0);
    }

    private static XboxDvrCandidateMetadata ReadHandleSnapshot(
        SafeFileHandle handle,
        string expectedPath,
        string portableLeaf)
    {
        if (handle.IsInvalid ||
            !GetFileInformationByHandle(handle, out var basic) ||
            !GetFileInformationByHandleEx(
                handle,
                NativeFileInfoClass.FileAttributeTagInfo,
                out NativeFileAttributeTagInfo attributeTag,
                (uint)Marshal.SizeOf<NativeFileAttributeTagInfo>()) ||
            !GetFileInformationByHandleEx(
                handle,
                NativeFileInfoClass.FileIdInfo,
                out NativeFileIdInfo fileId,
                (uint)Marshal.SizeOf<NativeFileIdInfo>()))
        {
            throw NativeError("Xbox DVR native metadata could not be read.");
        }

        RequireExactFinalPath(handle, expectedPath);
        var length = ((ulong)basic.FileSizeHigh << 32) | basic.FileSizeLow;
        if (length > long.MaxValue)
        {
            throw new InvalidDataException("An Xbox DVR item reports an impossible length.");
        }
        Span<byte> idBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes[..8], fileId.FileIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes[8..], fileId.FileIdHigh);
        return new XboxDvrCandidateMetadata(
            WindowsXboxDvrMetadataFileSystem.RequirePortableLeaf(portableLeaf),
            (long)length,
            ToUtcTicks(basic.LastWriteTime),
            fileId.VolumeSerialNumber,
            Convert.ToHexString(idBytes).ToLowerInvariant(),
            ProviderIdentitySha256: null,
            attributeTag.FileAttributes,
            (attributeTag.FileAttributes &
             XboxDvrCloudReparsePolicy.FileAttributeReparsePoint) != 0
                ? attributeTag.ReparseTag
                : 0);
    }

    private static string RequireDirectSourceChild(string canonicalRoot, string leaf)
    {
        var exact = Path.GetFullPath(Path.Combine(canonicalRoot, leaf));
        if (!Path.GetDirectoryName(exact)!.Equals(
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(exact).Equals(leaf, StringComparison.Ordinal))
        {
            throw new IOException("The Xbox source leaf escaped its configured folder.");
        }
        return exact;
    }

    private static string ValidateOrdinaryStagingRoot(string root)
    {
        root = WindowsXboxDvrMetadataFileSystem.RequireCanonicalRoot(root);
        var opened = RoutingWatchedFileSystem.OpenOrdinaryRoot(root);
        using (opened.Handle)
        {
            return opened.CanonicalPath;
        }
    }

    private static void RequireExactOwnedChild(string root, string path)
    {
        var canonical = Path.GetFullPath(path);
        if (!canonical.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(canonical)!.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(canonical) is not { Length: > 0 } leaf ||
            !Path.GetFileName(path).Equals(leaf, StringComparison.Ordinal))
        {
            throw new IOException("The Xbox staging target is not one exact owned child.");
        }
    }

    private static void TryDeleteExactOrdinaryFile(string root, string path)
    {
        if (!File.Exists(path) || RoutingWatchedFileSystem.IsReparsePoint(path)) return;
        RoutingWatchedFileSystem.EnsureOrdinaryExistingPath(
            root,
            path,
            requireDirectory: false,
            "Xbox owned staging partial");
        File.Delete(path);
    }

    private static SafeFileHandle OpenHandle(
        string path,
        uint desiredAccess,
        FileShare shareMode,
        uint flags,
        string description)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            FileMode.Open,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, $"The {description} could not be opened safely.");
    }

    private static void RequireExactFinalPath(SafeFileHandle handle, string expectedPath)
    {
        var final = GetFinalPath(handle);
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
        if (!final.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("An Xbox DVR path resolved outside its configured authority.");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (length == 0) throw NativeError("An Xbox DVR final path could not be resolved.");
            if (length < buffer.Capacity) return NormalizeFinalPath(buffer.ToString());
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("An Xbox DVR final path is too long.");
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
        var fileTime = (long)(((ulong)value.HighDateTime << 32) | value.LowDateTime);
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).Ticks;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                "An Xbox DVR item has an invalid filesystem timestamp.",
                exception);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Xbox DVR OneDrive filesystem support requires Windows.");
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileExW(
        string fileName,
        NativeFindInfoLevel infoLevel,
        out NativeFindData findFileData,
        NativeFindSearchOperation searchOperation,
        IntPtr searchFilter,
        NativeFindFlags additionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(
        SafeFindHandle findFile,
        out NativeFindData findFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out NativeByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        NativeFileInfoClass fileInformationClass,
        out NativeFileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        NativeFileInfoClass fileInformationClass,
        out NativeFileIdInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    private enum NativeFindInfoLevel
    {
        Standard = 0,
        Basic = 1
    }

    private enum NativeFindSearchOperation
    {
        NameMatch = 0
    }

    [Flags]
    private enum NativeFindFlags : uint
    {
        LargeFetch = 2
    }

    private enum NativeFileInfoClass
    {
        FileAttributeTagInfo = 9,
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeFindData
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint Reserved0;
        internal uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        internal string AlternateFileName;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal ulong FileIdLow;
        internal ulong FileIdHigh;
    }

    private sealed class XboxDvrWindowsSourceReadSession(
        FileStream stream,
        string expectedPath,
        string portableLeaf,
        Func<SafeFileHandle, string, string, XboxDvrCandidateMetadata> readMetadata)
        : IXboxDvrSourceReadSession
    {
        public Stream Content => stream;

        public XboxDvrCandidateMetadata ReadCurrentMetadata(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return readMetadata(stream.SafeFileHandle, expectedPath, portableLeaf);
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
